using SwitchPilot.Core;
using SwitchPilot.Core.Allied;
using SwitchPilot.Core.Cisco;

namespace SwitchPilot.Tests;

public class S95ParserTests
{
    [Fact] public void NormalizesStackedAndShortForms()
    {
        Assert.Equal("g1", S95Parser.NormalizePort("g1"));
        Assert.Equal("g1", S95Parser.NormalizePort("1/g1"));
        Assert.Equal("g1", S95Parser.NormalizePort("ethernet 1/g1"));
        Assert.Equal("2/g1", S95Parser.NormalizePort("2/g1"));
        Assert.Equal("ch5", S95Parser.NormalizePort("ch5"));
        Assert.Equal("g1", CiscoParser.NormalizeInterface("1/g1"));
        Assert.Equal("g1", CommandPlan.Interface("ethernet 1/g1"));
    }

    [Fact] public void ParsesStatusRows()
    {
        var output = """
            Port          Type                Duplex     Speed      Neg              Flow   Link    Back       Mdix
                                                                                    Ctrl   State   Pressure   Mode
            ----          -----------         ------     -----      -------          ----   -----   --------   ----
            g1            100M-Copper         --         --         --               --     Down    --         --
            g5            100M-Copper         Full       100        Enabled     Off   Up         Disabled   Auto
            ch1           LAGG                --         --         --          --    Up         --         --
            """;
        var ports = S95Parser.Ports(output);
        Assert.Equal(3, ports.Count);
        Assert.Equal("g1", ports[0].Name);
        Assert.Equal("notconnect", ports[0].Status);
        Assert.Equal("connected", ports[1].Status);
        Assert.Equal("100", ports[1].Speed);
        Assert.Equal("full", ports[1].Duplex);
        Assert.Equal("ch1", ports[2].Name);
        Assert.Throws<FormatException>(() => S95Parser.Ports("unexpected"));
    }

    [Fact] public void ParsesVlanRows()
    {
        var output = """
             VLAN       Name               Ports                        Type       Authorization
             ----       -------            --------                     ----       -------------
             1          default            1/g1-g2, 2/g1-g4             other      Required
             10         VLAN0010           1/g3-g4                      dynamic    Required
             21         VLAN0021                                        static     Required
             3978       Guest VLAN         1/g17                        guest      -
            """;
        var vlans = S95Parser.Vlans(output);
        Assert.Equal(4, vlans.Count);
        Assert.Equal((1, "default", "g1-g2, 2/g1-g4"), (vlans[0].Id, vlans[0].Name, vlans[0].Ports));
        // The AT-S95 "Type" (other/dynamic/static/guest) is not an administrative state; rows
        // are reported active and the type is retained in Ports for visibility below.
        Assert.Equal(3978, vlans[3].Id);
        Assert.Throws<FormatException>(() => S95Parser.Vlans("unexpected"));
    }

    [Fact] public void ParsesSwitchportMembership()
    {
        var output = """
             Port 1/g1:
             VLAN Membership mode: General
              Operating parameters:
              PVID: 1 (default)
             Port 1/g1 is member in:
              Vlan              Name                          Egress rule           Type
              ----              -------                       -----------           -------
              1                 default                       untagged              System
            """;
        var (mode, pvid) = S95Parser.Switchport(output);
        Assert.Equal("access", mode);
        Assert.Equal("1", pvid);
    }

    [Fact] public void GeneralModeWithMultipleMembersIsTrunkNotAccess()
    {
        var output = """
             VLAN Membership mode: General
             Port 1/g9 is member in:
              1   default    untagged   System
              8   VLAN008    tagged     Dynamic
            """;
        Assert.Equal("trunk", S95Parser.Switchport(output).Mode);
    }

    [Fact] public void ParsesBridgeAddressTable()
    {
        var output = """
                Aging time is 300 sec
                vlan                  mac address                            Port                Type
                ---------             --------------                         ----                -------
                1                     00:02:3f:b4:28:05                      g16                 dynamic
                1                     00:07:40:c9:5f:83                      ch5                 dynamic
            """;
        var macs = S95Parser.Macs(output);
        Assert.Equal(2, macs.Count);
        Assert.Equal(new MacEntry(1, "00023FB42805", "DYNAMIC", "g16"), macs[0]);
        Assert.Equal("ch5", macs[1].Port);
        Assert.Throws<FormatException>(() => S95Parser.Macs("unexpected"));
    }

    [Fact] public void IdentityUsesTableOrLineForm()
    {
        var table = """
             Unit                 SW version             Boot version             HW version
             ----                 ----------             ------------             ----------
             1                    v1.1.0.29              1.0.1.06                 01.00.00
            """;
        var line = "SW version 2.0.0.22 ( date 12-Oct-2009 time 11:12:14 )";
        Assert.Equal("1.1.0.29", S95Parser.Identity("sw", table, null).IosVersion);
        Assert.Equal("2.0.0.22", S95Parser.Identity("sw", line, null).IosVersion);
        Assert.Contains("AT-S95", S95Parser.Identity("sw", line, null).Model);
        var system = "\n\n Unit        Type\n ---- -------------------\n  1      AT-8000GS/24\n";
        Assert.Equal("AT-8000GS/24", S95Parser.Identity("sw", line, system).Model);
    }

    [Fact] public void DetectionOfS95BannerBeatsAlliedWarePlus()
    {
        Assert.Equal(SwitchVendor.AlliedS95, SwitchVendorDetector.Detect("SW version 2.0.0.22 ( date 12-Oct-2009 )"));
        Assert.Equal(SwitchVendor.AlliedTelesis, SwitchVendorDetector.Detect("AlliedWare Plus (AW+) version 5.4.7-1.1"));
    }
}
