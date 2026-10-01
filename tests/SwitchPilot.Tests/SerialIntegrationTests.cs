using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Serial;
using SwitchPilot.Infrastructure.Terminal;
namespace SwitchPilot.Tests;
public sealed class SerialIntegrationFactAttribute : FactAttribute
{
    public SerialIntegrationFactAttribute() { if (Environment.GetEnvironmentVariable("SWITCHPILOT_SERIAL_TEST_PORT") is null) Skip = "Nécessite le pseudo-terminal isolé de tests/serial_emulator.py."; }
}
public class SerialIntegrationTests
{
    private static string Device => Environment.GetEnvironmentVariable("SWITCHPILOT_SERIAL_TEST_PORT")!;
    [SerialIntegrationFact] public async Task RealSerialChannelReadsSlowSwitchportDumpWithoutLosingSynchronization()
    {
        using var channel = new SerialTerminalChannel(Device, 9600);
        var cli = new CliConversation(channel);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await cli.InitializeConsoleAsync(new() { Kind = ConnectionKind.Serial, SerialPort = "COM1" }, deadline.Token);
        var modes = Core.Cisco.CiscoParser.SwitchportModes(await cli.CommandAsync("show interfaces switchport", deadline.Token));
        Assert.Equal(26, modes.Count);
        Assert.Equal("access", modes["Fa0/26"]);
        var counters = Core.Cisco.CiscoParser.Counters(await cli.CommandAsync("show interfaces Fa0/1", deadline.Token));
        Assert.Equal("100", counters.Speed);
        Assert.Equal("SERIAL-SW", cli.Hostname);
    }
    [SerialIntegrationFact] public async Task RealSerialChannelHandlesFragmentedOutputAndSyslog()
    {
        using var channel = new SerialTerminalChannel(Device, 9600);
        var cli = new CliConversation(channel);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await cli.InitializeConsoleAsync(new() { Kind = ConnectionKind.Serial, SerialPort = "COM1" }, deadline.Token);
        var counters = Core.Cisco.CiscoParser.Counters(await cli.CommandAsync("show interfaces Fa0/1", deadline.Token));
        Assert.Equal("100", counters.Speed); Assert.Equal("Full", counters.Duplex); Assert.Equal(0, counters.Crc);
        Assert.Equal("SERIAL-SW", cli.Hostname);
    }
    [SerialIntegrationFact] public async Task RealSerialChannelDoesNotAcceptIosRefusal()
    {
        using var channel = new SerialTerminalChannel(Device, 9600);
        var cli = new CliConversation(channel);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await cli.InitializeConsoleAsync(new() { Kind = ConnectionKind.Serial, SerialPort = "COM1" }, deadline.Token);
        await Assert.ThrowsAsync<CliException>(() => cli.CommandAsync("show denied", deadline.Token));
    }
}
