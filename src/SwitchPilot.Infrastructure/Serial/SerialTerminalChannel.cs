using System.IO.Ports;
using SwitchPilot.Infrastructure.Terminal;
namespace SwitchPilot.Infrastructure.Serial;

public sealed class SerialTerminalChannel : ITerminalChannel
{
    private readonly SerialPort port;
    public SerialTerminalChannel(string name, int baud)
    {
        // PuTTY asserts DTR by default; deasserting it prevents some USB-to-serial adapters and
        // switch UARTs from driving TX, producing a silent timeout. RTS stays at the adapter
        // default: several switches use RTS as a power/mute gate and a bare RTS-high can mute RX.
        port = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        { Handshake = Handshake.None, DtrEnable = true, RtsEnable = false, ReadTimeout = 250, WriteTimeout = 2000 };
        try
        {
            port.Open();
            // Drop bytes left by a previous session / baud probe so the first prompt is clean.
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch { port.Dispose(); throw; }
    }
    public bool IsOpen => port.IsOpen;
    public string ReadAvailable() => port.IsOpen && port.BytesToRead > 0 ? port.ReadExisting() : "";
    public void Send(string text) => port.Write(text.Replace("\n", "\r"));
    public void Dispose() => port.Dispose();
}
