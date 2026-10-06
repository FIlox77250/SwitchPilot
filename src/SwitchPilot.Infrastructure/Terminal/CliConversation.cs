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

/// <summary>Prompt family recognised on the shell; it selects the error patterns and the preparation commands.</summary>
public enum PromptKind
{
    /// <summary>"host&gt;", "host#", "host(config-if)#": IOS, AlliedWare Plus, NX-OS, EOS, Dell OS6/9/10, UniFi shell.</summary>
    Cisco,
    /// <summary>"&lt;host&gt;" user view, "[host]" / "[~host-GE1/0/1]" system and interface views.</summary>
    Huawei,
    /// <summary>"user@host&gt;" operational mode, "user@host#" configuration mode, "user@host%" shell.</summary>
    Junos,
    /// <summary>"[user@host] &gt;" or "[user@host] /interface/bridge&gt;".</summary>
    RouterOs,
    /// <summary>"(host) &gt;", "(host) #", "(host) (Interface 0/1)#".</summary>
    EdgeSwitch
}

// One conversation per shell. The caller serializes whole transactions, not only writes.
public sealed class CliConversation(ITerminalChannel channel)
{
    public string Hostname { get; private set; } = "";
    public string Prompt { get; private set; } = "";
    /// <summary>Prompt family, fixed with the hostname once the session is prepared.</summary>
    public PromptKind Kind { get; private set; } = PromptKind.Cisco;
    /// <summary>Startup deadline, or maximum silence while receiving a command response.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(25);
    /// <summary>When set by the calling session, logs the received chunks of that command (masked).</summary>
    internal string? CurrentCommandLogMask { get; set; }
    // Huawei, Junos and RouterOS have no separate "enable" level: the login defines the rights.
    public bool Privileged => Kind is PromptKind.Huawei or PromptKind.Junos or PromptKind.RouterOs || Prompt.EndsWith('#');
    // One alternative per prompt family; the named group that matched gives the family. Huawei
    // "[edit]" is excluded: Junos prints it on its own line above the configuration prompt.
    private static readonly Regex AnyPrompt = new(@"(?:^|\n)(?<prompt>" +
        @"(?<cisco>(?<host>[A-Za-z0-9_.-]+)(?:\([A-Za-z0-9_./:-]+\))?[>#])" +
        @"|(?<huawei><(?<host>[^<>\s]{1,64})>|\[[~*]?(?!edit\])(?<host>[^\[\]\s@]{1,64})\])" +
        @"|(?<junos>[A-Za-z0-9_.-]{1,32}@(?<host>[A-Za-z0-9_.-]{1,64})(?::[A-Za-z0-9_:-]+)?[>#%])" +
        @"|(?<routeros>\[[^\[\]@\s]{1,32}@(?<host>[^\[\]\n]{1,64})\][^>\n]{0,64}>)" +
        @"|(?<edge>\((?<host>[^()\n]{1,64})\)\s?(?<mode>\([^()\n]{1,64}\))?\s?[>#])" +
        @")\s*$");
    private static readonly Regex Error = new(@"(?im)^[ \t]*(?:%(?![A-Z0-9_]+-\d-[A-Z0-9_]+:)[^\n]+|(?:Command rejected|Not allowed|Command authorization failed|Authorization failed|Access denied|Permission denied|Cannot (?:modify|create|delete)[^\n]*VLAN|(?:VTP|VLAN configuration)[^\n]*(?:client|not allowed)|(?:TDR|Cable diagnostics?)\b[^\n]*(?:not supported|not allowed))[^\n]*)$");
    private static readonly Regex HuaweiError = new(@"(?im)^[ \t]*Error\s*:[^\n]*$");
    private static readonly Regex JunosError = new(@"(?im)^[ \t]*(?:syntax error|unknown command|error:|missing argument|invalid (?:value|interface))[^\n]*$");
    private static readonly Regex RouterOsError = new(@"(?im)^[ \t]*(?:bad command name|syntax error|expected [^\n]*\(line \d+|failure:|no such item|input does not match|invalid value|not enough permissions|ambiguous value)[^\n]*$");
    private static readonly Regex EdgeError = new(@"(?im)^[ \t]*Error[!:][^\n]*$");
    // BusyBox shells (UniFi switches over SSH): an unknown applet is an unsupported command.
    private static readonly Regex ShellError = new(@"(?im)^[ \t]*-?(?:sh|ash|bash): [^\n]*not found[^\n]*$");
    private static readonly Regex PasswordPrompt = new(@"(?i)password:\s*$");
    // Yes/no questions of the supported families: IOS "[confirm]", Huawei "[Y/N]" and "(y/n)[n]",
    // FASTPATH "(y/n)", Junos "[yes,no] (yes)", Dell OS9 "[confirm yes/no]", NX-OS / EOS "[y/n]".
    private static readonly Regex Confirmation = new(@"(?i)(\[confirm\]|\[confirm yes/no\]|\[yes/no\]|\[yes,no\](?:\s*\(\w+\))?|\(y/n\)(?:\[[yn]\])?|\[y/n\]|\[y\(yes\)/n\(no\)(?:/c\(cancel\))?\]):?\s*$");
    private static readonly Regex ConfirmAnswer = new(@"^[A-Za-z]{1,3}\n?$");
    // AlliedWare Plus consoles announce "awplus login:" or "login as:"; Cisco uses "Username:", EdgeSwitch "User:".
    private static readonly Regex LoginPrompt = new(@"(?i)(?:^|\n)\s*(?:[A-Za-z0-9_.-]+\s+)?(?:Username|User\s*Name|User|Login\s*Name|login(?:\s+as)?)\s*:\s*$");
    private static readonly Regex PressKey = new(@"(?i)(?:^|\n)\s*Press\s+(?:RETURN|Enter|any key|<Enter>)[^\r\n]*$");
    private static readonly Regex CiscoSyslog = new(@"(?m)^(?:\*?[A-Za-z]{3}\s+\d+[^\r\n%]*:\s*)?%[A-Z0-9_]+-\d-[A-Z0-9_]+:[^\r\n]*(?:\n|$)");
    // AlliedWare Plus logs are timestamped <time> <host> <facility>.<severity> ... with no '%'.
    private static readonly Regex AlliedSyslog = new(@"(?m)^\s*(?:[A-Z][a-z]{2}\s+\d{1,2}\s+)?\d{2}:\d{2}:\d{2}\s+(?:\S+\s+)?[a-z][a-z0-9]*\.(?:emerg|alert|crit|err|error|warning|notice|info|debug)\b[^\r\n]*(?:\n|$)");
    // EdgeSwitch configuration sub-mode: "(host) (Config)#", "(host) (Interface 0/1)#".
    private static readonly Regex EdgeMode = new(@"\)\s?\([^()]+\)\s?#$");
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
        switch (Kind)
        {
            case PromptKind.Huawei:
                if (Prompt.StartsWith('[')) await CommandAsync("return", ct);
                try { await CommandAsync("screen-length 0 temporary-display", ct); } catch (CliException) { /* Pagers remain supported. */ }
                break;
            case PromptKind.Junos:
                // A root login lands in the FreeBSD shell ("%"); the CLI is one command away.
                if (Prompt.EndsWith('%')) await CommandAsync("cli", ct);
                foreach (var command in new[] { "set cli screen-length 0", "set cli screen-width 0", "set cli complete-on-space off" })
                    try { await CommandAsync(command, ct); } catch (CliException) { }
                break;
            case PromptKind.RouterOs:
                // Paging and colours are disabled by the "+ct240w" login suffix and "without-paging".
                break;
            case PromptKind.EdgeSwitch:
                for (var depth = 0; depth < 4 && EdgeMode.IsMatch(Prompt); depth++) await CommandAsync("exit", ct);
                try { await CommandAsync("terminal length 0", ct); } catch (CliException) { }
                break;
            default:
                if (Prompt.Contains('(')) await CommandAsync("end", ct);
                try { await CommandAsync("terminal length 0", ct); } catch (CliException) { /* Pagers remain supported. */ }
                // AT-S95 / Cisco-Small-Business CLIs disable paging with `terminal datadump`; unknown
                // elsewhere and safely refused. Without it every long output deadlocks on the pager.
                try { await CommandAsync("terminal datadump", ct); } catch (CliException) { }
                try { await CommandAsync("terminal width 240", ct); } catch (CliException) { }
                break;
        }
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
            if (string.IsNullOrEmpty(enablePassword) && Kind == PromptKind.EdgeSwitch)
            {
                // EdgeSwitch asks "Password:" even when no enable password is configured.
                TraceSend("<Entrée>");
                channel.Send("\n");
                response = await ReceiveAsync(false, ct);
            }
            else if (string.IsNullOrEmpty(enablePassword))
            {
                TraceSend("<Ctrl-C>");
                channel.Send("\x03");
                await ReceiveAsync(false, ct);
                throw new CliException("Le switch demande un mot de passe enable. Renseignez-le dans la connexion.");
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                TraceSend("******");
                channel.Send(enablePassword + "\n");
                response = await ReceiveAsync(false, ct);
            }
        }
        if (!Privileged) throw new CliException(Kind == PromptKind.Cisco
            ? "Passage en mode privilégié refusé. Vérifiez le mot de passe enable et les droits IOS."
            : "Passage en mode privilégié refusé. Vérifiez le mot de passe enable et les droits du compte.");
    }
    /// <summary>
    /// Sends one command. <paramref name="confirmAnswer"/> holds the exact keystrokes ("y" for a
    /// single-key FASTPATH question, "y\n" for a line-based one) answering the one confirmation the
    /// command is documented to ask; any other or further question stops the session.
    /// </summary>
    public async Task<string> CommandAsync(string command, CancellationToken ct, string? confirmAnswer = null)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Any(char.IsControl)) throw new ArgumentException("Une seule commande CLI sans caractère de contrôle est autorisée.");
        if (confirmAnswer is not null && !ConfirmAnswer.IsMatch(confirmAnswer)) throw new ArgumentException("Réponse de confirmation invalide.");
        channel.Send(command + "\n");
        var response = await ReceiveAsync(false, ct, commandResponse: true, confirmAnswer: confirmAnswer);
        var shell = ShellError.Match(response);
        var error = shell.Success ? shell : Kind switch
        {
            PromptKind.Huawei => HuaweiError.Match(response),
            PromptKind.Junos => JunosError.Match(response),
            PromptKind.RouterOs => RouterOsError.Match(response),
            PromptKind.EdgeSwitch => Error.Match(response) is { Success: true } e ? e : EdgeError.Match(response),
            _ => Error.Match(response)
        };
        if (error.Success)
        {
            var reason = error.Value;
            if (Kind == PromptKind.Cisco && !shell.Success)
            {
                var failure = Regex.IsMatch(reason, "authorization|permission|access denied|not allowed", RegexOptions.IgnoreCase) ? CliFailure.Authorization :
                    Regex.IsMatch(reason, "invalid input|unknown command|unrecognized command|not supported", RegexOptions.IgnoreCase) ? CliFailure.Unsupported : CliFailure.CommandRejected;
                throw new CliException(failure == CliFailure.Authorization ? "Commande refusée par les autorisations IOS." : "Commande refusée par IOS : syntaxe ou fonctionnalité non disponible.", failure);
            }
            var kind = Regex.IsMatch(reason, "authori[sz]|permission|access denied|not allowed|not enough permissions|insufficient", RegexOptions.IgnoreCase) ? CliFailure.Authorization :
                Regex.IsMatch(reason, "invalid input|invalid command|unknown command|unrecogni[sz]ed command|not supported|bad command name|syntax error|not found|incomplete command|wrong parameter|too many parameters|expected ", RegexOptions.IgnoreCase)
                    ? CliFailure.Unsupported : CliFailure.CommandRejected;
            var detail = reason.Trim();
            if (detail.Length > 160) detail = detail[..160] + "…";
            throw new CliException((kind == CliFailure.Authorization ? "Commande refusée par les autorisations du switch" : "Commande refusée par le switch") + $" : « {detail} ».", kind);
        }
        var lines = response.TrimEnd().Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Trim() == Prompt) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && lines[0].Trim() == command) lines.RemoveAt(0);
        return string.Join('\n', lines);
    }
    private static string StripSyslog(string text) => CiscoSyslog.Replace(AlliedSyslog.Replace(text, ""), "");
    private static PromptKind KindOf(Match match) =>
        match.Groups["huawei"].Success ? PromptKind.Huawei : match.Groups["junos"].Success ? PromptKind.Junos
        : match.Groups["routeros"].Success ? PromptKind.RouterOs : match.Groups["edge"].Success ? PromptKind.EdgeSwitch : PromptKind.Cisco;
    /// <summary>
    /// Before the lock any prompt is accepted; afterwards only the same family and hostname. A Huawei
    /// view appends a suffix ("[SW-GigabitEthernet0/0/1]"), accepted as a sub-view of the locked host.
    /// </summary>
    private bool Accept(PromptKind kind, string host) =>
        Hostname.Length == 0 || !hostnameLocked
        || kind == Kind && (host == Hostname || kind == PromptKind.Huawei && host.StartsWith(Hostname + "-", StringComparison.Ordinal));
    private async Task<string> ReceiveAsync(bool allowPassword, CancellationToken ct, ConnectionProfile? console = null,
        bool commandResponse = false, bool wakeSilentTerminal = false, string? confirmAnswer = null)
    {
        var timer = Stopwatch.StartNew();
        var buffer = new TerminalBuffer();
        // Output received before an answered confirmation: the buffer restarts after the answer.
        var answered = "";
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
            // A question is checked before the prompt: "[Y/N]" or "[confirm]" alone on a line would
            // otherwise look like a Huawei system-view prompt.
            if (Confirmation.IsMatch(tail))
            {
                if (confirmAnswer is null)
                    throw new CliProtocolException(Kind == PromptKind.Cisco
                        ? "IOS demande une confirmation interactive inattendue. Session arrêtée ; vérifiez l'état du switch avant de reprendre."
                        : "Le switch demande une confirmation interactive inattendue. Session arrêtée ; vérifiez l'état du switch avant de reprendre.");
                if (lastReceive.ElapsedMilliseconds >= 100)
                {
                    // The documented question is answered once; a further one is unexpected.
                    ct.ThrowIfCancellationRequested();
                    TraceSend(confirmAnswer.TrimEnd('\n'));
                    channel.Send(confirmAnswer);
                    confirmAnswer = null;
                    answered += buffer.ToString();
                    buffer = new TerminalBuffer(); tail = "";
                    continue;
                }
            }
            else if (AnyPrompt.Match(tail) is { Success: true } match && lastReceive.ElapsedMilliseconds >= 100 && Accept(KindOf(match), match.Groups["host"].Value))
            {
                if (!hostnameLocked || Hostname.Length == 0) { Hostname = match.Groups["host"].Value; Kind = KindOf(match); }
                Prompt = match.Groups["prompt"].Value;
                return StripSyslog(answered + buffer.ToString());
            }
            if (!channel.IsOpen && chunk.Length == 0) throw new IOException("La session terminal a été fermée.");
            await Task.Delay(25, ct);
        }
        throw new TimeoutException(Kind == PromptKind.Cisco
            ? "Délai de réponse IOS dépassé. Reconnectez-vous pour éviter une désynchronisation des commandes."
            : "Délai de réponse du switch dépassé. Reconnectez-vous pour éviter une désynchronisation des commandes.");
    }
}
