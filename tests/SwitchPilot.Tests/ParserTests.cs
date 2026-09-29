using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Core.Discovery;

namespace SwitchPilot.Tests;

public class ParserTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    [Fact] public void PortColumnsSurviveBlankAndSpacedDescriptions()
    {
        var ports = CiscoParser.Ports(Fixture("interfaces-status.txt"));
        Assert.Equal(6, ports.Count); Assert.Equal("", ports[1].Description);
        Assert.Equal("Bureau 214", ports[2].Description); Assert.Equal("a-100", ports[2].Speed);
        Assert.Equal("trunk", ports[4].Mode); Assert.Equal("err-disabled", ports[3].Status);
    }
    [Fact] public void UnknownPortOutputIsNotAnEmptySuccess() => Assert.Throws<FormatException>(() => CiscoParser.Ports("Access denied"));
    [Theory]
    [InlineData("FastEthernet0/14", "Fa0/14")]
    [InlineData("GigabitEthernet1/0/1", "Gi1/0/1")]
    [InlineData("Port-channel12", "Po12")]
    public void InterfacesAreCanonical(string text, string expected) => Assert.Equal(expected, CiscoParser.NormalizeInterface(text));
    [Theory]
    [InlineData("0011.2233.4455")][InlineData("00:11:22:33:44:55")][InlineData("00-11-22-33-44-55")]
    public void MacFormatsAreCanonical(string text) => Assert.Equal("001122334455", CiscoParser.NormalizeMac(text));
    [Fact] public void MacTableIgnoresCpuAndPreservesVlan()
    {
        var entries = CiscoParser.Macs(Fixture("mac-table.txt"));
        Assert.Equal(2, entries.Count); Assert.Equal(new MacEntry(10, "001122334455", "DYNAMIC", "Fa0/14"), entries[0]);
    }
    [Fact] public void UnknownMacOutputCannotClaimNoMatches() => Assert.Throws<FormatException>(() => CiscoParser.Macs("unexpected response"));
    [Fact] public void VlanWrappedPortsAreKept()
    {
        var vlans = CiscoParser.Vlans(Fixture("vlans.txt"));
        Assert.Equal(4, vlans.Count); Assert.Contains("Fa0/4", vlans[2].Ports); Assert.Equal("act/unsup", vlans[3].Status);
    }
    [Fact] public void TdrPreservesUnknownAndTolerance()
    {
        var result = CiscoParser.Tdr("Gi0/2", Fixture("tdr.txt"));
        Assert.Equal(4, result.Pairs.Count); Assert.Equal("OK", result.Pairs[0].Status);
        Assert.Contains("+/- 2", result.Pairs[0].Length); Assert.Equal("Non terminé", result.Pairs[3].Status);
    }
    [Fact] public void CountersAreNullableInsteadOfFabricatedZero()
    {
        var result = CiscoParser.Counters("Full-duplex, 100Mb/s\n 9 input errors, 7 CRC, 0 frame\n 2 collisions");
        Assert.Equal(7, result.Crc); Assert.Equal(2, result.Collisions); Assert.Equal("Full", result.Duplex);
        Assert.Null(CiscoParser.Counters("unknown").Crc);
    }
    [Fact] public void IdentityUsesModelAndVersion()
    {
        var info = CiscoParser.Identity("SW-A", "Cisco IOS Software, Version 15.2(4)E10, RELEASE\nModel number : WS-C2960+24TC-L");
        Assert.Equal("15.2(4)E10", info.IosVersion);
        Assert.Equal("WS-C2960+24TC-L", info.Model);
    }
    [Fact] public void NoGigabitAlertOnFastEthernet()
    {
        var p = CiscoParser.Ports(Fixture("interfaces-status.txt"))[0];
        Assert.Contains("normale", SafetyPolicy.SpeedAssessment(p));
    }
    [Fact] public void UplinkIsNotClassifiedAsDirect()
    {
        var ports = CiscoParser.Ports(Fixture("interfaces-status.txt"));
        var snapshot = new SwitchSnapshot(new("SW", "2960", "15.2"), ports, []);
        var result = PortLocator.Find("AA:BB:CC:DD:EE:FF", snapshot, CiscoParser.Macs(Fixture("mac-table.txt")));
        Assert.False(Assert.Single(result).DirectCandidate);
    }
    [Fact] public void SwitchportModesFollowOperationalState()
    {
        var modes = CiscoParser.SwitchportModes("Name: Fa0/14\nAdministrative Mode: dynamic auto\nOperational Mode: static access\nName: Gi0/1\nAdministrative Mode: trunk\nOperational Mode: down");
        Assert.Equal("access", modes["Fa0/14"]); Assert.Equal("trunk", modes["Gi0/1"]);
    }
    [Fact] public void FullDescriptionsAreNotTruncated()
    {
        var descriptions = CiscoParser.Descriptions("Interface Status Protocol Description\nFa0/14 up up Prise murale A-014 - bureau 214 - telephone\nFa0/2 admin down down Reserve salle reunion");
        Assert.Equal("Prise murale A-014 - bureau 214 - telephone", descriptions["Fa0/14"]);
        Assert.Equal("Reserve salle reunion", descriptions["Fa0/2"]);
    }
    [Fact] public void StatusWordsInsideDescriptionDoNotShiftColumns()
    {
        var port = Assert.Single(CiscoParser.Ports("Fa0/1 connected device A connected 10 a-full a-100 10/100BaseTX"));
        Assert.Equal("connected device A", port.Description); Assert.Equal("10", port.Vlan);
    }
    [Fact] public void MissingAndUnknownTdrPairsRemainExplicitlyUnknown()
    {
        var result = CiscoParser.Tdr("Gi0/2", "Gi0/2 auto Pair A 12 +/- 2 meters Pair B Normal\nPair B N/A N/A UnexpectedState");
        Assert.Equal(4, result.Pairs.Count);
        Assert.StartsWith("Inconnu", result.Pairs[1].Status); Assert.Equal("Inconnu", result.Pairs[2].Status);
        Assert.Contains("partiels", result.Note);
    }
    [Fact] public void DuplicatedTdrPairCannotClaimSuccess() => Assert.Throws<FormatException>(() => CiscoParser.Tdr("Gi0/2", "Pair A N/A N/A Normal\nPair A N/A N/A Open"));
}
