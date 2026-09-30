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
    public bool IsConnected
    {
        get { try { return Volatile.Read(ref disposed) == 0 && channel.IsOpen; } catch (ObjectDisposedException) { return false; } }
    }
    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected) throw new IOException("Session terminal déconnectée.");
            if (RequiresPrivilege(command))
            {
                await Conversation.EnsurePrivilegedAsync(secret, cancellationToken);
                return await Conversation.CommandAsync(command, cancellationToken);
            }
            try { return await Conversation.CommandAsync(command, cancellationToken); }
            catch (CliException) when (!Conversation.Privileged)
            {
                // Some platforms (AlliedWare Plus in particular) only expose certain read
                // commands to privileged users. Elevate and retry once instead of forcing
                // "enable" upfront, which fails when no enable password was provided.
                await Conversation.EnsurePrivilegedAsync(secret, cancellationToken);
                return await Conversation.CommandAsync(command, cancellationToken);
            }
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        { await DisposeAsync(); throw; }
        finally { gate.Release(); }
    }
    private static bool RequiresPrivilege(string command) =>
        command is "configure terminal" or "write memory" or "show running-config" or "copy running-config startup-config"
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
