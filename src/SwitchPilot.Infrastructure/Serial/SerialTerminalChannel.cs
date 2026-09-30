using System.IO.Ports;
using SwitchPilot.Infrastructure.Terminal;
namespace SwitchPilot.Infrastructure.Serial;

public sealed class SerialTerminalChannel : ITerminalChannel
{
    private readonly SerialPort port;
    public SerialTerminalChannel(string name, int baud)
    {
        port = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        { Handshake = Handshake.None, DtrEnable = false, RtsEnable = false, ReadTimeout = 250, WriteTimeout = 2000 };
        try { port.Open(); } catch { port.Dispose(); throw; }
    }
    public bool IsOpen => port.IsOpen;
    public string ReadAvailable() => port.IsOpen && port.BytesToRead > 0 ? port.ReadExisting() : "";
    public void Send(string text) => port.Write(text.Replace("\n", "\r"));
    public void Dispose() => port.Dispose();
}
