using SwitchPilot.Core;
using SwitchPilot.Core.Allied;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Allied;

namespace SwitchPilot.Tests;

public class AlliedTests
{
    private const string StatusOutput = """
        Port       Name         Status      Vlan  Duplex  Speed   Type
        port1.0.1  Access_Net1  connected   10    a-full  a-1000  1000BaseTX
        port1.0.2  Trunk_Net    connected   trunk a-full  a-1000  1000BaseTX
        port1.0.3               notconnect  1     auto    auto    1000BaseTX
        """;

    private const string VlanOutput = """
        VLAN ID  Name        Type    State   Member ports
                 (u)-Untagged, (t)-Tagged
        1        default     STATIC  ACTIVE  port1.0.3(u)
        10       vlan10      STATIC  ACTIVE  port1.0.1(u)
        20       vlan20      STATIC  ACTIVE  port1.0.2(t) port1.0.3(u)
        """;

    private const string MacOutput = """
        VLAN port      mac                fwd
        1    port1.0.1 0011.2233.4455     forward dynamic
        1    CPU       eccd.6d20.c0a3     forward static
        """;

    [Fact]
    public void AlliedPortNamesAreCanonical()
    {
        Assert.Equal("port1.0.1", CiscoParser.NormalizeInterface("1.0.1"));
        Assert.Equal("port1.0.1", CiscoParser.NormalizeInterface("port1.0.1"));
        Assert.Equal("port1.0.1", CommandPlan.Interface("port1.0.1"));
        Assert.Equal("port1.0.1", CommandPlan.Interface("1.0.1"));
    }

    [Fact]
    public void AlliedMacTableUsesPortBeforeMac()
    {
        var entries = AlliedTelesisParser.Macs(MacOutput);
        Assert.Equal(new MacEntry(1, "001122334455", "DYNAMIC", "port1.0.1"), Assert.Single(entries));
        Assert.Throws<FormatException>(() => AlliedTelesisParser.Macs("unexpected output"));
    }

    [Fact]
    public void AlliedVlanContinuationLinesKeepMemberPorts()
    {
        const string output = "VLAN ID  Name     Type    State   Member ports\n1        default  STATIC  ACTIVE  port1.0.1(u)\n                 port1.0.2(u) port1.0.3(t)\n";
        var vlan = Assert.Single(AlliedTelesisParser.Vlans(output));
        Assert.Equal("port1.0.1, port1.0.2, port1.0.3", vlan.Ports);
        var modes = AlliedTelesisParser.PortModes(output);
        Assert.Equal("access", modes["port1.0.2"]);
        Assert.Equal("trunk", modes["port1.0.3"]);
    }

    [Fact]
    public void AlliedTrunkPlanResetsThenAdds()
    {
        var plan = CommandPlan.Trunk("port1.0.1", 1, "1,10", SwitchVendor.AlliedTelesis);
        Assert.Contains("switchport trunk allowed vlan none", plan.Commands);
        Assert.Contains("switchport trunk allowed vlan add 1,10", plan.Commands);
        Assert.DoesNotContain("switchport trunk allowed vlan 1,10", plan.Commands);
        Assert.Contains("switchport trunk allowed vlan 1,10", CommandPlan.Trunk("Gi0/1", 1, "1,10").Commands);
    }

    [Fact]
    public void AlliedDescriptionLimitIsEighty()
    {
        Assert.Throws<ArgumentException>(() => CommandPlan.Describe("port1.0.1", new string('a', 81), SwitchVendor.AlliedTelesis));
        Assert.NotNull(CommandPlan.Describe("port1.0.1", new string('a', 80), SwitchVendor.AlliedTelesis));
        Assert.NotNull(CommandPlan.Describe("Fa0/1", new string('a', 81), SwitchVendor.Cisco));
    }

    [Theory]
    [InlineData("AlliedWare Plus (AW+) version 5.4.7-1.1\nModel: x230-28GP", SwitchVendor.AlliedTelesis)]
    [InlineData("Cisco IOS Software, C2960 Software (C2960-LANBASEK9-M), Version 15.2(4)E10", SwitchVendor.Cisco)]
    [InlineData("Allied Telesis Inc.", SwitchVendor.AlliedTelesis)]
    [InlineData("something else entirely", null)]
    public void VendorIsDetectedFromTheBanner(string banner, SwitchVendor? expected) =>
        Assert.Equal(expected, SwitchVendorDetector.Detect(banner));

    [Fact]
    public void AlliedStatusColumnsAreParsed()
    {
        var ports = CiscoParser.Ports(StatusOutput);
        Assert.Equal(3, ports.Count);
        Assert.Equal("port1.0.1", ports[0].Name);
        Assert.Equal("Access_Net1", ports[0].Description);
        Assert.Equal("10", ports[0].Vlan);
        Assert.Equal("trunk", ports[1].Mode);
        Assert.Equal("notconnect", ports[2].Status);
    }

    [Fact]
    public void AlliedVlanMembershipDrivesModes()
    {
        var modes = AlliedTelesisParser.PortModes(VlanOutput);
        Assert.Equal("access", modes["port1.0.1"]);
        Assert.Equal("trunk", modes["port1.0.2"]);
        Assert.Equal("access", modes["port1.0.3"]);
    }

    [Fact]
    public void AlliedVlanBriefKeepsMemberPorts()
    {
        var vlans = AlliedTelesisParser.Vlans(VlanOutput);
        Assert.Equal(3, vlans.Count);
        Assert.Equal("default", vlans[0].Name);
        Assert.Equal("port1.0.3", vlans[0].Ports);
        Assert.Equal("port1.0.2, port1.0.3", vlans[2].Ports);
        Assert.Throws<FormatException>(() => AlliedTelesisParser.Vlans("unexpected output"));
    }

    [Fact]
    public void AlliedCountersUseTheInlineLayout()
    {
        var counters = AlliedTelesisParser.Counters("port1.0.1\n  Link is UP, administrative state is UP\n  input errors 0, length 0, overrun 0, CRC 5, frame 0\n  Full duplex, 1000 Mbps");
        Assert.Equal(5, counters.Crc);
        Assert.Equal(0, counters.InputErrors);
        Assert.Equal("Actif", counters.LinkState);
        Assert.Equal("Full", counters.Duplex);
        Assert.Equal("1000", counters.Speed);
    }

    [Fact]
    public void AlliedIdentityReportsBoardAndVersion()
    {
        var info = AlliedTelesisParser.Identity("awplus", "AlliedWare Plus (AW+) version 5.4.7-1.1\nModel: x230-28GP");
        Assert.Equal("x230-28GP", info.Model);
        Assert.Equal("5.4.7-1.1", info.IosVersion);
        var named = AlliedTelesisParser.Identity("awplus", "AlliedWare Plus (AW+) version 5.4.7-1.1\nModel name : AT-x930-28GTX");
        Assert.Equal("AT-x930-28GTX", named.Model);
        var banner = AlliedTelesisParser.Identity("awplus", "AlliedWare Plus (TM) 5.4.9-0.1");
        Assert.Equal("5.4.9-0.1", banner.IosVersion);
    }

    [Fact]
    public async Task DriverReadsAlliedSnapshot()
    {
        var session = new Session(); await using var driver = new AlliedTelesisDriver(session, new SafetyTests.TestAudit(), backup: new SafetyTests.TestBackup());
        Assert.Equal(SwitchVendor.AlliedTelesis, driver.Vendor);
        var snapshot = await driver.ReadSnapshotAsync();
        Assert.Equal("port1.0.1", snapshot.Ports[0].Name);
        Assert.Equal("access", snapshot.Ports[0].Mode);
        Assert.Equal("trunk", snapshot.Ports[1].Mode);
        Assert.Equal(3, snapshot.Vlans.Count);
        Assert.Equal("x230-28GP", snapshot.Identity.Model);
        Assert.Equal("5.4.7-1.1", snapshot.Identity.IosVersion);
    }

    [Fact]
    public async Task AlliedAccessPortIsADirectCandidate()
    {
        var session = new Session(); await using var driver = new AlliedTelesisDriver(session, new SafetyTests.TestAudit(), backup: new SafetyTests.TestBackup());
        await driver.ReadSnapshotAsync();
        var observation = await driver.ReadDetectionAsync("00:11:22:33:44:55");
        var match = Assert.Single(PortLocator.Find("001122334455", observation.Snapshot, observation.Entries));
        Assert.True(match.DirectCandidate);
        Assert.Equal("port1.0.1", match.Port.Name);
    }

    [Fact]
    public async Task AlliedTdrIsNotSupported()
    {
        var session = new Session(); await using var driver = new AlliedTelesisDriver(session, new SafetyTests.TestAudit(), backup: new SafetyTests.TestBackup());
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.RunTdrAsync("port1.0.1", new(true, true, new HashSet<string>())));
    }

    [Fact]
    public async Task AlliedWriteUsesPlannedCommands()
    {
        var session = new Session(); await using var driver = new AlliedTelesisDriver(session, new SafetyTests.TestAudit(), backup: new SafetyTests.TestBackup());
        await driver.ReadSnapshotAsync(); session.Commands.Clear();
        await driver.ApplyAsync(CommandPlan.Access("port1.0.1", 10), false, safety: new(true, true, new HashSet<string>()));
        Assert.Contains("show running-config", session.Commands);
        Assert.Contains("interface port1.0.1", session.Commands);
        Assert.Contains("switchport access vlan 10", session.Commands);
    }

    private sealed class Session : ICliSession
    {
        public List<string> Commands { get; } = [];
        public bool IsConnected => true;
        public string Hostname => "awplus";
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(command switch
            {
                "show version" => "AlliedWare Plus (AW+) version 5.4.7-1.1\nModel: x230-28GP",
                "show interface status" => StatusOutput,
                "show vlan brief" => VlanOutput,
                _ when command.StartsWith("show mac") => MacOutput,
                _ => ""
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
