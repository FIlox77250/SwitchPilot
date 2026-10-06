using SwitchPilot.Core;
namespace SwitchPilot.Infrastructure.Terminal;

public abstract class TerminalSession(ITerminalChannel channel, string enablePassword) : ICliSession
{
    protected readonly CliConversation Conversation = new(channel);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string secret = enablePassword;
    private int disposed;
    public abstract ConnectionKind Kind { get; }
    public string Hostname => Conversation.Hostname;
    public string Prompt => Conversation.Prompt;
    public PromptKind PromptKind => Conversation.Kind;
    public bool IsConnected
    {
        get { try { return Volatile.Read(ref disposed) == 0 && channel.IsOpen; } catch (ObjectDisposedException) { return false; } }
    }
    public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default) => RunAsync(command, null, cancellationToken);
    public Task<string> ExecuteConfirmedAsync(string command, string answer, CancellationToken cancellationToken = default) =>
        RunAsync(command, answer ?? throw new ArgumentNullException(nameof(answer)), cancellationToken);
    private async Task<string> RunAsync(string command, string? confirmAnswer, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected) throw new IOException("Session terminal déconnectée.");
            if (RequiresPrivilege(command))
            {
                await Conversation.EnsurePrivilegedAsync(secret, cancellationToken);
                CliTrace.Line(">", command, mask: false);
                Conversation.CurrentCommandLogMask = CliTrace.IsSensitive(command) ? command : null;
                try
                {
                    var result = await Conversation.CommandAsync(command, cancellationToken, confirmAnswer);
                    CliTrace.Line("<=", CliTrace.IsSensitive(command) ? "" : result, mask: CliTrace.IsSensitive(command));
                    return result;
                }
                finally { Conversation.CurrentCommandLogMask = null; }
            }
            try
            {
                CliTrace.Line(">", command, mask: false);
                Conversation.CurrentCommandLogMask = CliTrace.IsSensitive(command) ? command : null;
                try
                {
                    var result = await Conversation.CommandAsync(command, cancellationToken, confirmAnswer);
                    CliTrace.Line("<=", CliTrace.IsSensitive(command) ? "" : result, mask: CliTrace.IsSensitive(command));
                    return result;
                }
                finally { Conversation.CurrentCommandLogMask = null; }
            } catch (CliException e) when (!Conversation.Privileged && e.Failure == CliFailure.Authorization)
            {
                // Some platforms only expose certain read commands to privileged users. Elevate
                // and retry once; only an authorization refusal justifies elevation, and an
                // impossible elevation keeps the original error instead of masking it.
                CliTrace.Line("! ", command + " → élévation privilegee", mask: true);
                try { await Conversation.EnsurePrivilegedAsync(secret, cancellationToken); }
                catch (CliException) { throw e; }
                return await Conversation.CommandAsync(command, cancellationToken, confirmAnswer);
            }
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        { await DisposeAsync(); throw; }
        finally { gate.Release(); }
    }
    private static bool RequiresPrivilege(string command) =>
        command is "configure terminal" or "configure" or "vlan database" or "write memory" or "show running-config" or "copy running-config startup-config"
            or "show running-configuration" or "copy running-configuration startup-configuration"
        || command.StartsWith("test cable-diagnostics") || command.StartsWith("show cable-diagnostics");
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        secret = "";
        try { channel.Dispose(); } finally { DisposeTransport(); }
        return ValueTask.CompletedTask;
    }
    protected virtual void DisposeTransport() { }
}
