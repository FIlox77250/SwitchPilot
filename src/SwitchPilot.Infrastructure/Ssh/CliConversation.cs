using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SwitchPilot.Infrastructure.Ssh;

public interface ITerminalChannel : IDisposable
{
    bool IsOpen { get; }
    string ReadAvailable();
    void Send(string text);
}

public enum CliFailure { CommandRejected, Unsupported, Authorization }
public sealed class CliException(string message, CliFailure failure = CliFailure.CommandRejected) : Exception(message)
{
    public CliFailure Failure { get; } = failure;
}
public sealed class CliProtocolException(string message) : IOException(message);

// One conversation per shell. The caller serializes whole transactions, not only writes.
public sealed class CliConversation(ITerminalChannel channel)
{
    public string Hostname { get; private set; } = "";
    public string Prompt { get; private set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(25);
    public bool Privileged => Prompt.EndsWith('#');
    private static readonly Regex AnyPrompt = new(@"(?:^|\n)(?<prompt>(?<host>[A-Za-z0-9_.-]+)(?:\([A-Za-z0-9_-]+\))?[>#])\s*$");
    private static readonly Regex Error = new(@"(?im)^\s*(?:%(?![A-Z0-9_]+-\d-[A-Z0-9_]+:)[^\n]+|(?:TDR|Cable diagnostics?)\b[^\n]*(?:not supported|not allowed)[^\n]*)$");
    private static readonly Regex PasswordPrompt = new(@"(?i)password:\s*$");
    private static readonly Regex Confirmation = new(@"(?i)(\[confirm\]|\[yes/no\]|\(y/n\))\s*$");

    public async Task InitializeAsync(CancellationToken ct)
    {
        await ReceiveAsync(false, ct);
        try { await CommandAsync("terminal length 0", ct); } catch (CliException) { /* --More-- remains supported. */ }
        try { await CommandAsync("terminal width 240", ct); } catch (CliException) { }
    }
    public async Task EnsurePrivilegedAsync(string enablePassword, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (enablePassword.Any(char.IsControl)) throw new ArgumentException("Mot de passe enable invalide : caractère de contrôle.");
        if (Privileged) return;
        channel.Send("enable\n");
        var response = await ReceiveAsync(true, ct);
        if (PasswordPrompt.IsMatch(response))
        {
            if (string.IsNullOrEmpty(enablePassword))
            {
                channel.Send("\x03");
                await ReceiveAsync(false, ct);
                throw new CliException("Le switch demande un mot de passe enable. Renseignez-le dans la connexion.");
            }
            ct.ThrowIfCancellationRequested();
            channel.Send(enablePassword + "\n");
            response = await ReceiveAsync(false, ct);
        }
        if (!Privileged) throw new CliException("Passage en mode privilégié refusé. Vérifiez le mot de passe enable et les droits IOS.");
    }
    public async Task<string> CommandAsync(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Any(char.IsControl)) throw new ArgumentException("Une seule commande CLI sans caractère de contrôle est autorisée.");
        channel.Send(command + "\n");
        var response = await ReceiveAsync(false, ct);
        var error = Error.Match(response);
        if (error.Success)
        {
            var reason = error.Value;
            var failure = Regex.IsMatch(reason, "authorization|permission|access denied", RegexOptions.IgnoreCase) ? CliFailure.Authorization :
                Regex.IsMatch(reason, "invalid input|unknown command|unrecognized command|not supported", RegexOptions.IgnoreCase) ? CliFailure.Unsupported : CliFailure.CommandRejected;
            throw new CliException(failure == CliFailure.Authorization ? "Commande refusée par les autorisations IOS." : "Commande refusée par IOS : syntaxe ou fonctionnalité non disponible.", failure);
        }
        var lines = response.TrimEnd().Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Trim() == Prompt) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && lines[0].Trim() == command) lines.RemoveAt(0);
        return string.Join('\n', lines);
    }
    private async Task<string> ReceiveAsync(bool allowPassword, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var buffer = new TerminalBuffer();
        var tail = "";
        var lastReceive = Stopwatch.StartNew();
        while (timer.Elapsed < Timeout)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = channel.ReadAvailable();
            if (chunk.Length > 0)
            {
                buffer.Append(chunk, () => { ct.ThrowIfCancellationRequested(); channel.Send(" "); });
                tail = buffer.Tail; lastReceive.Restart();
            }
            if (allowPassword && PasswordPrompt.IsMatch(tail)) return buffer.ToString();
            var match = AnyPrompt.Match(tail);
            if (match.Success && lastReceive.ElapsedMilliseconds >= 100 && (Hostname.Length == 0 || match.Groups["host"].Value == Hostname))
            {
                Hostname = match.Groups["host"].Value; Prompt = match.Groups["prompt"].Value;
                return buffer.ToString();
            }
            if (Confirmation.IsMatch(tail))
                throw new CliProtocolException("IOS demande une confirmation interactive inattendue. Session arrêtée ; vérifiez l'état du switch avant de reprendre.");
            if (!channel.IsOpen && chunk.Length == 0) throw new IOException("La session SSH a été fermée par le switch.");
            await Task.Delay(25, ct);
        }
        throw new TimeoutException("Délai de réponse IOS dépassé. Reconnectez-vous pour éviter une désynchronisation des commandes.");
    }
}
