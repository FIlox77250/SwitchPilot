using SwitchPilot.Infrastructure.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using SwitchPilot.Core;
using System.Text.RegularExpressions;

namespace SwitchPilot.Infrastructure.Ssh;

public sealed class SshSession : TerminalSession
{
    private readonly SshClient client;
    public override ConnectionKind Kind => ConnectionKind.Ssh;
    private SshSession(SshClient client, ITerminalChannel channel, string enablePassword) : base(channel, enablePassword) => this.client = client;

    // A negotiation failure is a KEX/cipher/host-key mismatch, not a bad password or a
    // changed host key. Only those failures justify offering the legacy algorithm retry.
    public static bool IsNegotiationFailure(Exception error)
    {
        if (error is SshConnectionException { DisconnectReason: DisconnectReason.KeyExchangeFailed }) return true;
        return error is SshException && MentionsNegotiation(error.Message);
    }

    private static bool MentionsNegotiation(string? message) => !string.IsNullOrEmpty(message) &&
        Regex.IsMatch(message, "algorithm|cipher|key exchange|no matching|kex", RegexOptions.IgnoreCase);

    public static async Task<SshSession> ConnectAsync(ConnectionProfile profile, Func<string, string, bool> trustHost, bool legacyAlgorithms, CancellationToken ct)
    {
        profile.Validate();
        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username) || profile.Port is < 1 or > 65535)
            throw new ArgumentException("Adresse du switch, utilisateur et port SSH valide requis.");
        if (profile.EnablePassword.Any(char.IsControl)) throw new ArgumentException("Le mot de passe enable ne doit pas contenir de caractères de contrôle.");
        // PuTTY and OpenSSH accept both "password" and "keyboard-interactive"; many switches
        // (including Allied Telesis) only advertise keyboard-interactive. Offer both so the
        // same credentials work everywhere instead of failing on the authentication method.
        var password = new PasswordAuthenticationMethod(profile.Username, profile.Password);
        var keyboard = new KeyboardInteractiveAuthenticationMethod(profile.Username);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                var request = prompt.Request ?? "";
                // "Password for user:" contains "user": test the password keywords first, and
                // default to the password when the prompt asks for neither.
                prompt.Response = Regex.IsMatch(request, "password|passcode|secret", RegexOptions.IgnoreCase) || !Regex.IsMatch(request, "user|login|name", RegexOptions.IgnoreCase)
                    ? profile.Password : profile.Username;
            }
        };
        var info = new ConnectionInfo(profile.Host.Trim(), profile.Port, profile.Username, password, keyboard) { Timeout = TimeSpan.FromSeconds(15) };
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
