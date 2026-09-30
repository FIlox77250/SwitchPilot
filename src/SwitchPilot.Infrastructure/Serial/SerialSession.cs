using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Terminal;
namespace SwitchPilot.Infrastructure.Serial;
public sealed class SerialSession : TerminalSession
{
    public override ConnectionKind Kind => ConnectionKind.Serial;
    public int BaudRate { get; }
    private SerialSession(ITerminalChannel channel, ConnectionProfile profile, int baud) : base(channel, profile.EnablePassword) => BaudRate = baud;
    public static async Task<SerialSession> ConnectAsync(ConnectionProfile profile, CancellationToken ct,
        Func<string, int, ITerminalChannel>? channelFactory = null, TimeSpan? probeTimeout = null)
    {
        profile.Validate();
        channelFactory ??= (name, baud) => new SerialTerminalChannel(name, baud);
        foreach (var baud in profile.AutoBaud ? ConnectionProfile.SerialBaudRates : [profile.BaudRate])
        {
            ct.ThrowIfCancellationRequested();
            var session = new SerialSession(channelFactory(profile.SerialPort, baud), profile, baud);
            try
            {
                session.Conversation.Timeout = profile.AutoBaud ? probeTimeout ?? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(25);
                await session.Conversation.InitializeConsoleAsync(profile, ct);
                session.Conversation.Timeout = TimeSpan.FromSeconds(25);
                return session;
            }
            catch (TimeoutException) when (profile.AutoBaud) { await session.DisposeAsync(); }
            catch { await session.DisposeAsync(); throw; }
        }
        throw new TimeoutException("Aucun prompt IOS lisible aux vitesses proposées. Vérifiez le câble console et les paramètres du switch.");
    }
}
