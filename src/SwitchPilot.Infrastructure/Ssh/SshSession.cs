using Renci.SshNet;
using SwitchPilot.Core;

namespace SwitchPilot.Infrastructure.Ssh;

public sealed class SshSession : ICliSession
{
    private readonly SshClient client;
    private readonly ITerminalChannel channel;
    private readonly CliConversation conversation;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string enablePassword;
    private int disposed;
    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0) return false;
            try { return client.IsConnected && channel.IsOpen; }
            catch (ObjectDisposedException) { return false; }
        }
    }
    public string Hostname => conversation.Hostname;
    private SshSession(SshClient client, ITerminalChannel channel, string enablePassword)
    {
        this.client = client; this.channel = channel; this.enablePassword = enablePassword;
        conversation = new(channel);
    }
    public static async Task<SshSession> ConnectAsync(ConnectionProfile profile, Func<string, string, bool> trustHost, bool legacyAlgorithms, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username) || profile.Port is < 1 or > 65535)
            throw new ArgumentException("Adresse du switch, utilisateur et port SSH valide requis.");
        if (profile.EnablePassword.Any(char.IsControl)) throw new ArgumentException("Le mot de passe enable ne doit pas contenir de caractères de contrôle.");
        var info = new PasswordConnectionInfo(profile.Host.Trim(), profile.Port, profile.Username, profile.Password) { Timeout = TimeSpan.FromSeconds(15) };
        if (!legacyAlgorithms)
        {
            foreach (var key in info.KeyExchangeAlgorithms.Keys.Where(k => k.Contains("sha1")).ToArray()) info.KeyExchangeAlgorithms.Remove(key);
            foreach (var key in info.HostKeyAlgorithms.Keys.Where(k => k is "ssh-rsa" or "ssh-dss").ToArray()) info.HostKeyAlgorithms.Remove(key);
            foreach (var key in info.Encryptions.Keys.Where(k => k.Contains("cbc") || k.Contains("3des")).ToArray()) info.Encryptions.Remove(key);
            foreach (var key in info.HmacAlgorithms.Keys.Where(k => k.Contains("md5") || k.Contains("sha1")).ToArray()) info.HmacAlgorithms.Remove(key);
        }
        var ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(20) };
        ssh.HostKeyReceived += (_, e) => e.CanTrust = trustHost(profile.Host.Trim() + ":" + profile.Port, e.FingerPrintSHA256);
        SshSession? session = null;
        try
        {
            await ssh.ConnectAsync(ct);
            var stream = ssh.CreateShellStream("vt100", 240, 80, 0, 0, 65536);
            session = new(ssh, new ShellChannel(stream, ssh), profile.EnablePassword);
            await session.conversation.InitializeAsync(ct);
            if (profile.EnablePassword.Length > 0) await session.conversation.EnsurePrivilegedAsync(profile.EnablePassword, ct);
            return session;
        }
        catch { if (session != null) await session.DisposeAsync(); else ssh.Dispose(); throw; }
    }
    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected) throw new IOException("Session SSH déconnectée.");
            if (command is "configure terminal" or "write memory" or "show running-config" or "show vlan brief" || command.StartsWith("test cable-diagnostics") || command.StartsWith("show cable-diagnostics") || command.StartsWith("show mac"))
                await conversation.EnsurePrivilegedAsync(enablePassword, cancellationToken);
            return await conversation.CommandAsync(command, cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        { await DisposeAsync(); throw; }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        enablePassword = "";
        channel.Dispose(); client.Dispose();
        return ValueTask.CompletedTask;
    }
    private sealed class ShellChannel(ShellStream stream, SshClient ssh) : ITerminalChannel
    {
        private bool disposed;
        public bool IsOpen => !disposed && ssh.IsConnected && stream.CanWrite;
        public string ReadAvailable() => !disposed && stream.DataAvailable ? stream.Read() : "";
        public void Send(string text) { stream.Write(text); stream.Flush(); }
        public void Dispose() { disposed = true; stream.Dispose(); }
    }
}
