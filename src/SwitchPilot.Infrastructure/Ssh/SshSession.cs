using SwitchPilot.Infrastructure.Terminal;
using Renci.SshNet;
using SwitchPilot.Core;

namespace SwitchPilot.Infrastructure.Ssh;

public sealed class SshSession : TerminalSession
{
    private readonly SshClient client;
    public override ConnectionKind Kind => ConnectionKind.Ssh;
    private SshSession(SshClient client, ITerminalChannel channel, string enablePassword) : base(channel, enablePassword) => this.client = client;
    public static bool IsNegotiationFailure(Exception error) => error is Renci.SshNet.Common.SshConnectionException e &&
        e.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.KeyExchangeFailed;

    public static async Task<SshSession> ConnectAsync(ConnectionProfile profile, Func<string, string, bool> trustHost, bool legacyAlgorithms, CancellationToken ct)
    {
        profile.Validate();
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
            await session.Conversation.InitializeAsync(ct);
            if (profile.EnablePassword.Length > 0) await session.Conversation.EnsurePrivilegedAsync(profile.EnablePassword, ct);
            return session;
        }
        catch { if (session != null) await session.DisposeAsync(); else ssh.Dispose(); throw; }
    }
    protected override void DisposeTransport() => client.Dispose();
    private sealed class ShellChannel(ShellStream stream, SshClient ssh) : ITerminalChannel
    {
        private bool disposed;
        public bool IsOpen => !disposed && ssh.IsConnected && stream.CanWrite;
        public string ReadAvailable() => !disposed && stream.DataAvailable ? stream.Read() : "";
        public void Send(string text) { stream.Write(text); stream.Flush(); }
        public void Dispose() { disposed = true; stream.Dispose(); }
    }
}
