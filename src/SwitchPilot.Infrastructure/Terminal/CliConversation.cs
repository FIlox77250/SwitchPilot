using System.Diagnostics;
using SwitchPilot.Core;
using System.Text.RegularExpressions;

namespace SwitchPilot.Infrastructure.Terminal;

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
    /// <summary>Startup deadline, or maximum silence while receiving a command response.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(25);
    /// <summary>When set by the calling session, logs the received chunks of that command (masked).</summary>
    internal string? CurrentCommandLogMask { get; set; }
    public bool Privileged => Prompt.EndsWith('#');
    private static readonly Regex AnyPrompt = new(@"(?:^|\n)(?<prompt>(?<host>[A-Za-z0-9_.-]+)(?:\([A-Za-z0-9_-]+\))?[>#])\s*$");
    private static readonly Regex Error = new(@"(?im)^[ \t]*(?:%(?![A-Z0-9_]+-\d-[A-Z0-9_]+:)[^\n]+|(?:Command rejected|Not allowed|Command authorization failed|Authorization failed|Access denied|Permission denied|Cannot (?:modify|create|delete)[^\n]*VLAN|(?:VTP|VLAN configuration)[^\n]*(?:client|not allowed)|(?:TDR|Cable diagnostics?)\b[^\n]*(?:not supported|not allowed))[^\n]*)$");
    private static readonly Regex PasswordPrompt = new(@"(?i)password:\s*$");
    private static readonly Regex Confirmation = new(@"(?i)(\[confirm\]|\[yes/no\]|\(y/n\)):?\s*$");
    // AlliedWare Plus consoles announce "awplus login:" or "login as:"; Cisco uses "Username:".
    private static readonly Regex LoginPrompt = new(@"(?i)(?:^|\n)\s*(?:[A-Za-z0-9_.-]+\s+)?(?:Username|User\s*Name|Login\s*Name|login(?:\s+as)?)\s*:\s*$");
    private static readonly Regex PressKey = new(@"(?i)(?:^|\n)\s*Press\s+(?:RETURN|Enter|any key|<Enter>)[^\r\n]*$");
    private static readonly Regex CiscoSyslog = new(@"(?m)^(?:\*?[A-Za-z]{3}\s+\d+[^\r\n%]*:\s*)?%[A-Z0-9_]+-\d-[A-Z0-9_]+:[^\r\n]*(?:\n|$)");
    // AlliedWare Plus logs are timestamped <time> <host> <facility>.<severity> ... with no '%'.
    private static readonly Regex AlliedSyslog = new(@"(?m)^\s*(?:[A-Z][a-z]{2}\s+\d{1,2}\s+)?\d{2}:\d{2}:\d{2}\s+(?:\S+\s+)?[a-z][a-z0-9]*\.(?:emerg|alert|crit|err|error|warning|notice|info|debug)\b[^\r\n]*(?:\n|$)");
    /// <summary>Lock the hostname after PrepareAsync, once a real command round-trip confirmed the prompt.</summary>
    private bool hostnameLocked;
    private void LockHostname() { if (!hostnameLocked) hostnameLocked = true; }
    private static void TraceSend(string text) => CliTrace.Line(">", text, mask: false);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await ReceiveAsync(false, ct, wakeSilentTerminal: true);
        await PrepareAsync(ct);
    }
    private async Task PrepareAsync(CancellationToken ct)
    {
        if (Prompt.Contains('(')) await CommandAsync("end", ct);
        try { await CommandAsync("terminal length 0", ct); } catch (CliException) { /* Pagers remain supported. */ }
        // AT-S95 / Cisco-Small-Business CLIs disable paging with `terminal datadump`; unknown
        // elsewhere and safely refused. Without it every long output deadlocks on the pager.
        try { await CommandAsync("terminal datadump", ct); } catch (CliException) { }
        try { await CommandAsync("terminal width 240", ct); } catch (CliException) { }
        LockHostname();
    }
    public async Task InitializeConsoleAsync(ConnectionProfile profile, CancellationToken ct)
    {
        profile.Validate();
        ct.ThrowIfCancellationRequested();
        // A serial console may need a carrier return to print its banner/login prompt.
        // Exactly one Enter here: a second unconditional one could submit an empty login
        // on a fast device (see the in-loop nudge in ReceiveAsync for silent devices).
        TraceSend("<Entrée>");
        channel.Send("\n");
        await ReceiveAsync(false, ct, profile);
        await PrepareAsync(ct);
        if (profile.EnablePassword.Length > 0) await EnsurePrivilegedAsync(profile.EnablePassword, ct);
    }
    public async Task EnsurePrivilegedAsync(string enablePassword, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (enablePassword.Any(char.IsControl)) throw new ArgumentException("Mot de passe enable invalide : caractère de contrôle.");
        if (Privileged) return;
        TraceSend("enable");
        channel.Send("enable\n");
        var response = await ReceiveAsync(true, ct);
        if (PasswordPrompt.IsMatch(response))
        {
            if (string.IsNullOrEmpty(enablePassword))
            {
                TraceSend("<Ctrl-C>");
                channel.Send("\x03");
                await ReceiveAsync(false, ct);
                throw new CliException("Le switch demande un mot de passe enable. Renseignez-le dans la connexion.");
            }
            ct.ThrowIfCancellationRequested();
            TraceSend("******");
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
        var response = await ReceiveAsync(false, ct, commandResponse: true);
        var error = Error.Match(response);
        if (error.Success)
        {
            var reason = error.Value;
            var failure = Regex.IsMatch(reason, "authorization|permission|access denied|not allowed", RegexOptions.IgnoreCase) ? CliFailure.Authorization :
                Regex.IsMatch(reason, "invalid input|unknown command|unrecognized command|not supported", RegexOptions.IgnoreCase) ? CliFailure.Unsupported : CliFailure.CommandRejected;
            throw new CliException(failure == CliFailure.Authorization ? "Commande refusée par les autorisations IOS." : "Commande refusée par IOS : syntaxe ou fonctionnalité non disponible.", failure);
        }
        var lines = response.TrimEnd().Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Trim() == Prompt) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && lines[0].Trim() == command) lines.RemoveAt(0);
        return string.Join('\n', lines);
    }
    private static string StripSyslog(string text) => CiscoSyslog.Replace(AlliedSyslog.Replace(text, ""), "");
    private async Task<string> ReceiveAsync(bool allowPassword, CancellationToken ct, ConnectionProfile? console = null,
        bool commandResponse = false, bool wakeSilentTerminal = false)
    {
        var timer = Stopwatch.StartNew();
        var buffer = new TerminalBuffer();
        var tail = "";
        var lastReceive = Stopwatch.StartNew();
        var loginSent = false; var passwordSent = false; var initialAnswered = false; var returnSent = false;
        var nudged = false;
        // At 9600 baud a complete switchport report can take more than 25 seconds.
        // Keep receiving while it makes progress, but retain a total cap against
        // endless output. Startup/baud probes still have their short fixed deadline.
        var totalLimit = commandResponse ? TimeSpan.FromMinutes(5) : Timeout;
        while (timer.Elapsed < totalLimit && (!commandResponse || lastReceive.Elapsed < Timeout))
        {
            ct.ThrowIfCancellationRequested();
            var chunk = channel.ReadAvailable();
            if (chunk.Length > 0)
            {
                buffer.Append(chunk, () => { ct.ThrowIfCancellationRequested(); channel.Send(" "); });
                CliTrace.Line("<", chunk, mask: CurrentCommandLogMask is not null);
                tail = buffer.Tail; lastReceive.Restart();
            }
            // Timestamped and untimestamped asynchronous IOS syslogs are not command output.
            tail = StripSyslog(tail);
            if (console is not null)
            {
                string? answer = null;
                if (PressKey.IsMatch(tail) && !returnSent)
                { answer = "\n"; returnSent = true; }
                else if (Regex.IsMatch(tail, @"(?i)(?:^|\n)\s*Would you like to enter the initial configuration dialog\? \[yes/no\]:?\s*$") && !initialAnswered)
                { answer = "no\n"; initialAnswered = true; }
                else if (LoginPrompt.IsMatch(tail))
                {
                    if (loginSent) throw new CliException("Identifiant console refusé ou authentification requise.", CliFailure.Authorization);
                    // Some consoles accept an anonymous login; send even an empty name instead
                    // of aborting before the switch had a chance to respond.
                    answer = console.Username + "\n"; loginSent = true;
                }
                else if (PasswordPrompt.IsMatch(tail))
                {
                    if (passwordSent || console.Password.Length == 0) throw new CliException("Mot de passe console requis ou authentification refusée.", CliFailure.Authorization);
                    answer = console.Password + "\n"; passwordSent = true;
                }
                if (answer is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    TraceSend(answer == console.Password + "\n" ? "******"
                        : answer == "\n" ? "<Entrée>"
                        : answer.TrimEnd('\n'));
                    channel.Send(answer);
                    buffer = new TerminalBuffer(); tail = ""; continue;
                }
            }
            if (allowPassword && PasswordPrompt.IsMatch(tail)) return buffer.ToString();
            // Wake only an initial terminal prompt. An Enter while a command is running
            // can answer an unseen question or queue another prompt and desynchronize
            // subsequent responses. Never inject it into an established dialogue.
            if ((wakeSilentTerminal || console is not null) && !nudged && tail.Length == 0 && channel.IsOpen && timer.Elapsed > TimeSpan.FromMilliseconds(1500))
            {
                ct.ThrowIfCancellationRequested();
                TraceSend("<Entrée, relance>");
                channel.Send("\n");
                nudged = true;
            }
            var match = AnyPrompt.Match(tail);
            if (match.Success && lastReceive.ElapsedMilliseconds >= 100 && (Hostname.Length == 0 || !hostnameLocked || match.Groups["host"].Value == Hostname))
            {
                Hostname = match.Groups["host"].Value; Prompt = match.Groups["prompt"].Value;
                return StripSyslog(buffer.ToString());
            }
            if (Confirmation.IsMatch(tail))
                throw new CliProtocolException("IOS demande une confirmation interactive inattendue. Session arrêtée ; vérifiez l'état du switch avant de reprendre.");
            if (!channel.IsOpen && chunk.Length == 0) throw new IOException("La session terminal a été fermée.");
            await Task.Delay(25, ct);
        }
        throw new TimeoutException("Délai de réponse IOS dépassé. Reconnectez-vous pour éviter une désynchronisation des commandes.");
    }
}
