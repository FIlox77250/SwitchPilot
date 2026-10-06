using System.Text.Json.Nodes;
using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Tests;

/// <summary>Read-only parsers of the multi-vendor platforms, on captured-style outputs.</summary>
public class MultiVendorParserTests
{
    // ---- Huawei VRP ----------------------------------------------------------------------

    private const string HuaweiBrief = """
        PHY: Physical
        *down: administratively down
        ^down: standby
        (l): loopback
        (s): spoofing
        InUti/OutUti: input utility/output utility
        Interface                   PHY   Protocol InUti OutUti   inErrors  outErrors
        Eth-Trunk1                  up    up       0.01%  0.01%          0          0
          GigabitEthernet0/0/23     up    up       0.01%  0.01%          0          0
        GigabitEthernet0/0/1        up    up       0.01%  0.04%          0          0
        GigabitEthernet0/0/2        down  down        0%     0%          0          0
        GigabitEthernet0/0/3        *down down        0%     0%          0          0
        GigabitEthernet0/0/23       up    up       0.01%  0.01%          0          0
        NULL0                       up    up(s)       0%     0%          0          0
        Vlanif1                     up    up          0%     0%          0          0
        """;

    [Fact]
    public void HuaweiInterfaceBriefKeepsPhysicalPortsOnce()
    {
        var ports = HuaweiParser.InterfaceBrief(HuaweiBrief);
        Assert.Equal(["Eth-Trunk1", "GigabitEthernet0/0/23", "GigabitEthernet0/0/1", "GigabitEthernet0/0/2", "GigabitEthernet0/0/3"], ports.Select(p => p.Name));
        Assert.Equal(["connected", "connected", "connected", "notconnect", "disabled"], ports.Select(p => p.Status));
    }

    [Fact] public void HuaweiInterfaceBriefRejectsUnknownOutput() => Assert.Throws<FormatException>(() => HuaweiParser.InterfaceBrief("Error: Unrecognized command found at '^' position."));

    [Fact]
    public void HuaweiDescriptionsMatchAbbreviatedNames()
    {
        var descriptions = HuaweiParser.Descriptions("""
            PHY: Physical
            *down: administratively down
            Interface                     PHY     Protocol  Description
            GE0/0/1                       up      up        Uplink core
            GE0/0/2                       down    down
            GE0/0/3                       *down   down      Imprimante RDC
            """);
        Assert.Equal("Uplink core", descriptions[PortNames.Key("GigabitEthernet0/0/1")]);
        Assert.Equal("", descriptions[PortNames.Key("GigabitEthernet0/0/2")]);
        Assert.Equal("Imprimante RDC", descriptions[PortNames.Key("GE0/0/3")]);
    }

    [Fact]
    public void HuaweiPortVlansGiveAccessOrTrunk()
    {
        var vlans = HuaweiParser.PortVlans("""
            Port                    Link Type    PVID  Trunk VLAN List
            -------------------------------------------------------------------------------
            GigabitEthernet0/0/1    access       10    -
            GigabitEthernet0/0/2    trunk        1     1-4094
            GigabitEthernet0/0/3    hybrid       1     -
            Eth-Trunk1              trunk        1     10 20
            """);
        Assert.Equal(("access", "10"), vlans[PortNames.Key("GE0/0/1")]);
        Assert.Equal(("trunk", "trunk"), vlans[PortNames.Key("GigabitEthernet0/0/2")]);
        Assert.Equal(("trunk", "trunk"), vlans[PortNames.Key("GigabitEthernet0/0/3")]);
        Assert.Equal(("trunk", "trunk"), vlans[PortNames.Key("Eth-Trunk1")]);
    }

    [Fact]
    public void HuaweiVlansJoinMembersAndProperties()
    {
        var vlans = HuaweiParser.Vlans("""
            The total number of vlans is : 3
            U: Up;         D: Down;         TG: Tagged;         UT: Untagged;
            MP: Vlan-mapping;               ST: Vlan-stacking;
            #: ProtocolTransparent-vlan;    *: Management-vlan;
            --------------------------------------------------------------------------------
            VID  Type    Ports
            --------------------------------------------------------------------------------
            1    common  UT:GE0/0/1(U)     GE0/0/2(D)     GE0/0/3(D)     GE0/0/4(D)
                            GE0/0/5(D)     GE0/0/6(D)
            10   common  UT:GE0/0/7(U)
                         TG:GE0/0/24(U)
            20   common  TG:GE0/0/24(U)
            VID  Status  Property      MAC-LRN Statistics Description
            --------------------------------------------------------------------------------
            1    enable  default       enable  disable    VLAN 0001
            10   enable  default       enable  disable    Bureaux
            20   disable default       enable  disable
            """);
        Assert.Equal(
        [
            new VlanInfo(1, "VLAN 0001", "active", "GE0/0/1, GE0/0/2, GE0/0/3, GE0/0/4, GE0/0/5, GE0/0/6"),
            new VlanInfo(10, "Bureaux", "active", "GE0/0/7, GE0/0/24(T)"),
            new VlanInfo(20, "VLAN 0020", "inactive", "GE0/0/24(T)")
        ], vlans);
    }

    [Fact]
    public void HuaweiVlansWithoutPropertyTableUseMembership()
    {
        var vlans = HuaweiParser.Vlans("""
            VID  Type    Ports
            1    common  UT:GE0/0/1(U)
            30   common  TG:GE0/0/2(U)
            """);
        Assert.Equal([new VlanInfo(1, "VLAN 0001", "active", "GE0/0/1"), new VlanInfo(30, "VLAN 0030", "active", "GE0/0/2(T)")], vlans);
    }

    [Fact] public void HuaweiVlansRejectUnknownOutput() => Assert.Throws<FormatException>(() => HuaweiParser.Vlans("Error: Unrecognized command"));

    [Fact]
    public void HuaweiIdentityIgnoresTheSoftwareBanner()
    {
        var identity = HuaweiParser.Identity("sw-rdc", """
            Huawei Versatile Routing Platform Software
            VRP (R) software, Version 5.170 (S5735 V200R019C10SPC500)
            Copyright (C) 2000-2020 HUAWEI TECH Co., Ltd.
            HUAWEI S5735-L24T4S-A1 Routing Switch uptime is 0 week, 1 day, 2 hours, 3 minutes
            """);
        Assert.Equal(new SwitchIdentity("sw-rdc", "S5735-L24T4S-A1", "V200R019C10SPC500"), identity);
    }

    [Fact]
    public void HuaweiIdentityOfCloudEngine()
    {
        var identity = HuaweiParser.Identity("ce1", """
            Huawei Versatile Routing Platform Software
            VRP (R) software, Version 8.191 (CE6857EI V200R019C10SPC800)
            HUAWEI CE6857-48S6CQ-EI uptime is 12 days, 3 hours, 4 minutes
            """);
        Assert.Equal(new SwitchIdentity("ce1", "CE6857-48S6CQ-EI", "V200R019C10SPC800"), identity);
        Assert.Equal(new SwitchIdentity("x", "Huawei VRP (modèle inconnu)", "Inconnue"), HuaweiParser.Identity("x", "garbage"));
    }

    [Fact]
    public void HuaweiCountersSeparateInputAndOutputErrors()
    {
        var counters = HuaweiParser.Counters("""
            GigabitEthernet0/0/1 current state : UP
            Line protocol current state : UP
            Description:Uplink
            Switch Port, PVID :   10, TPID : 8100(Hex), The Maximum Frame Length is 9216
            Speed : 1000,  Loopback: NONE
            Duplex: FULL,  Negotiation: ENABLE
            Mdi   : AUTO,  Flow-control: DISABLE
            Last 300 seconds input rate 1024 bits/sec, 1 packets/sec
            Last 300 seconds output rate 2048 bits/sec, 2 packets/sec
            Input:  123456 packets, 7890123 bytes
              Unicast:                    1000,  Multicast:                     200
              Broadcast:                    30,  Jumbo:                           0
              Discard:                       0,  Total Error:                     7
              CRC:                           5,  Giants:                          0
              Runts:                         0,  Symbols:                         0
            Output:  654321 packets, 9876543 bytes
              Unicast:                    3000,  Multicast:                     400
              Discard:                       0,  Total Error:                     1
              Collisions:                    3,  ExcessiveCollisions:             0
              Late Collisions:               0,  Deferreds:                       0
            """);
        Assert.Equal(new InterfaceCounters(5, 3, 7, "1000", "Full", "Actif", 1), counters);
    }

    [Fact]
    public void HuaweiCountersOfAdminDownPort()
    {
        var counters = HuaweiParser.Counters("GigabitEthernet0/0/3 current state : Administratively DOWN\nLine protocol current state : DOWN");
        Assert.Equal(new InterfaceCounters(null, null, null, "Inconnue", "Inconnu", "Inactif", null), counters);
    }

    // ---- Juniper Junos -------------------------------------------------------------------

    [Fact]
    public void JunosTerseKeepsPhysicalInterfaces()
    {
        var ports = JunosParser.Terse("""
            Interface               Admin Link Proto    Local                 Remote
            ge-0/0/0                up    up
            ge-0/0/0.0              up    up   eth-switch
            ge-0/0/1                up    down
            ge-0/0/1.0              up    down eth-switch
            ge-0/0/2                down  down
            xe-0/1/0                up    up
            ae0                     up    up
            ae0.0                   up    up   eth-switch
            me0                     up    up
            vlan                    up    up
            """);
        Assert.Equal(["ge-0/0/0", "ge-0/0/1", "ge-0/0/2", "xe-0/1/0", "ae0"], ports.Select(p => p.Name));
        Assert.Equal(["connected", "notconnect", "disabled", "connected", "connected"], ports.Select(p => p.Status));
        Assert.Throws<FormatException>(() => JunosParser.Terse("error: syntax error, expecting <command>: terse"));
    }

    [Fact]
    public void JunosDescriptionsUsePhysicalNames()
    {
        var descriptions = JunosParser.Descriptions("""
            Interface       Admin Link Description
            ge-0/0/0        up    up   Uplink core
            ge-0/0/1.0      up    down Logical unit
            xe-0/1/0        up    up   Serveur NAS
            """);
        Assert.Equal("Uplink core", descriptions["ge-0/0/0"]);
        Assert.Equal("Logical unit", descriptions["ge-0/0/1"]);
        Assert.Equal("Serveur NAS", descriptions["xe-0/1/0"]);
    }

    [Fact]
    public void JunosSwitchingInterfacesGiveModes()
    {
        var modes = JunosParser.SwitchingInterfaces("""
            Routing Instance Name : default-switch
            Logical Interface flags (DL - disable learning, AD - packet action drop,
                                     LH - MAC limit hit, DN - interface down,
                                     MMAS - Mac-move action shutdown, AS - Autostate-exclude enabled,
                                     SCTL - shutdown by Storm-control, MI - MAC+IP limit hit)

            Logical         Vlan          TAG     MAC     MAC+IP  STP         Logical           Tagging
            interface       members               limit   limit   state       interface flags
            ge-0/0/0.0                            65535   0                                     tagged
                            default       1       65535   0       Forwarding                    untagged
                            voice         20      65535   0       Forwarding                    tagged
            ge-0/0/1.0                            65535   0                                     untagged
                            bureaux       10      65535   0       Forwarding                    untagged
            ae0.0                                 65535   0                                     tagged
                            bureaux       10      65535   0       Forwarding                    tagged
                            voice         20      65535   0       Forwarding                    tagged
            xe-0/1/0.0                            65535   0                                     untagged
            """);
        Assert.Equal(("trunk", "trunk"), modes["ge-0/0/0"]);
        Assert.Equal(("access", "10"), modes["ge-0/0/1"]);
        Assert.Equal(("trunk", "trunk"), modes["ae0"]);
        Assert.Equal(("Inconnu", ""), modes["xe-0/1/0"]);
    }

    private const string JunosVlans = """
        Routing instance        VLAN name             Tag          Interfaces
        default-switch          bureaux               10
                                                                   ae0.0*
                                                                   ge-0/0/1.0*
        default-switch          default               1
                                                                   ge-0/0/0.0*
        default-switch          voice                 20
                                                                   ae0.0*, ge-0/0/0.0
        """;

    [Fact]
    public void JunosVlansCollectContinuationLines()
    {
        Assert.Equal(
        [
            new VlanInfo(10, "bureaux", "active", "ae0, ge-0/0/1"),
            new VlanInfo(1, "default", "active", "ge-0/0/0"),
            new VlanInfo(20, "voice", "active", "ae0, ge-0/0/0")
        ], JunosParser.Vlans(JunosVlans));
    }

    [Fact]
    public void JunosMacsResolveVlanNames()
    {
        var ids = JunosParser.Vlans(JunosVlans).ToDictionary(v => v.Name, v => v.Id);
        var macs = JunosParser.Macs("""
            MAC flags (S - static MAC, D - dynamic MAC, L - locally learned, P - Persistent static
                       SE - statistics enabled, NM - non configured MAC, R - remote PE MAC, O - ovsdb MAC)


            Ethernet switching table : 3 entries, 3 learned
            Routing instance : default-switch
                Vlan                MAC                 MAC         Age    Logical                NH        RTR
                name                address             flags              interface              Index     ID
                bureaux             00:11:22:33:44:55   D             -   ge-0/0/1.0             0         0
                voice               aa:bb:cc:dd:ee:ff   S             -   ae0.0                  0         0
                default             00:00:5e:00:53:01   D,SE          -   ge-0/0/0.0             0         0
            """, ids);
        Assert.Equal(
        [
            new MacEntry(10, "001122334455", "DYNAMIC", "ge-0/0/1"),
            new MacEntry(20, "AABBCCDDEEFF", "STATIC", "ae0"),
            new MacEntry(1, "00005E005301", "DYNAMIC", "ge-0/0/0")
        ], macs);
    }

    [Fact]
    public void JunosMacsEmptyOnlyWhenTheTableSaysSo()
    {
        var none = new Dictionary<string, int>();
        Assert.Empty(JunosParser.Macs("Ethernet switching table : 0 entries, 0 learned", none));
        Assert.Throws<FormatException>(() => JunosParser.Macs("error: permission denied", none));
    }

    [Fact]
    public void JunosIdentityOfElsAndOlderReleases()
    {
        Assert.Equal(new SwitchIdentity("ex2300-bureau", "ex2300-24p", "21.4R3-S5.4"), JunosParser.Identity("fallback", """
            fpc0:
            --------------------------------------------------------------------------
            Hostname: ex2300-bureau
            Model: ex2300-24p
            Junos: 21.4R3-S5.4
            JUNOS OS Kernel 64-bit  [20230713.f0c3a87_builder_stable_12_214]
            """));
        Assert.Equal(new SwitchIdentity("sw1", "ex4200-48t", "12.3R12.4"), JunosParser.Identity("fallback", "Hostname: sw1\nModel: ex4200-48t\nJUNOS Base OS boot [12.3R12.4]"));
        Assert.Equal(new SwitchIdentity("fallback", "Junos (modèle inconnu)", "Inconnue"), JunosParser.Identity("fallback", ""));
    }

    [Fact]
    public void JunosCountersFromExtensiveOutput()
    {
        var counters = JunosParser.Counters("""
            Physical interface: ge-0/0/1, Enabled, Physical link is Up
              Interface index: 650, SNMP ifIndex: 516, Generation: 141
              Link-level type: Ethernet, MTU: 1514, LAN-PHY mode, Speed: 1000mbps, BPDU Error: None,
              Link-mode: Full-duplex, Flow control: Disabled, Auto-negotiation: Enabled
              Input errors:
                Errors: 4, Drops: 0, Framing errors: 2, Runts: 0, Policed discards: 0, L3 incompletes: 0,
              Output errors:
                Carrier transitions: 3, Errors: 1, Drops: 0, Collisions: 6, Aged packets: 0, FIFO errors: 0,
              MAC statistics:                      Receive         Transmit
                CRC/Align errors                         2                0
            """);
        Assert.Equal(new InterfaceCounters(2, 6, 4, "1000", "Full", "Actif", 1), counters);
        Assert.Equal("10000", JunosParser.Counters("Physical interface: xe-0/1/0, Enabled, Physical link is Down\n  Speed: 10Gbps").Speed);
        Assert.Equal("Inactif", JunosParser.Counters("Physical interface: xe-0/1/0, Enabled, Physical link is Down").LinkState);
        Assert.Equal("Inactif", JunosParser.Counters("Physical interface: ge-0/0/2, Administratively down, Physical link is Up").LinkState);
    }

    // ---- MikroTik RouterOS ---------------------------------------------------------------

    private const string RosInterfaces = """
        Flags: R - RUNNING; X - DISABLED
         0  R  name=ether1 default-name=ether1 type=ether mtu=1500 actual-mtu=1500 mac-address=48:8F:5A:00:00:01 last-link-up-time=2026-10-01 10:00:00 link-downs=0
         1     name=ether2 default-name=ether2 type=ether mtu=1500 comment="Imprimante \"RDC\""
         2 X   name=ether3 default-name=ether3 type=ether mtu=1500
         3  R  name=bridge1 type=bridge mtu=auto
         4  R  name=bond1 type=bond mtu=1500
         5  R  name=sfp-sfpplus1 default-name=sfp-sfpplus1 type=ether
        """;

    private const string RosBridgePorts = """
         0     interface=ether1 bridge=bridge1 pvid=1 frame-types=admit-all
         1     interface=ether2 bridge=bridge1 pvid=10 frame-types=admit-only-untagged-and-priority-tagged
         2     interface=bond1 bridge=bridge1 pvid=1 frame-types=admit-only-vlan-tagged
         3 I   interface=ether3 bridge=bridge1 pvid=20 frame-types=admit-all
        """;

    private const string RosBridgeVlans = """
         0     bridge=bridge1 vlan-ids=10 tagged=bridge1,ether1 untagged=ether2 current-tagged=bridge1,ether1 current-untagged=ether2
         1     bridge=bridge1 vlan-ids=20,30 tagged=bridge1,bond1 untagged=ether3
         2 D   bridge=bridge1 vlan-ids=1 current-tagged=bridge1 current-untagged=ether1
         3 X   bridge=bridge1 vlan-ids=100-102 comment=Invites tagged=bond1
        """;

    [Fact]
    public void RouterOsTerseReadsFlagsAndQuotedValues()
    {
        var rows = RouterOsParser.Terse(RosInterfaces);
        Assert.Equal(6, rows.Count);
        Assert.True(rows[0].Has('R'));
        Assert.Equal("ether1", rows[0]["name"]);
        Assert.Equal("0", rows[0]["link-downs"]);
        Assert.Equal("2026-10-01 10:00:00", rows[0]["last-link-up-time"]);
        Assert.Equal("2026-10-02 08:00:00", RouterOsParser.Terse(" 0 name=a last-link-down-time=2026-10-02 08:00:00")[0]["last-link-down-time"]);
        Assert.Equal("Imprimante \"RDC\"", rows[1]["comment"]);
        Assert.Equal("", rows[1].Flags);
        Assert.Equal("X", rows[2].Flags);
        Assert.Equal("", rows[2]["absent"]);
    }

    [Fact]
    public void RouterOsPortsCombineBridgeSettings()
    {
        var ports = RouterOsParser.Ports(RosInterfaces, RosBridgePorts, RosBridgeVlans);
        Assert.Equal(
        [
            new PortInfo("ether1", "", "connected", "trunk", "", "", "", "trunk"),
            new PortInfo("ether2", "Imprimante \"RDC\"", "notconnect", "10", "", "", "", "access"),
            new PortInfo("ether3", "", "disabled", "20", "", "", "", "access"),
            new PortInfo("bond1", "", "connected", "trunk", "", "", "bonding", "trunk"),
            new PortInfo("sfp-sfpplus1", "", "connected", "", "", "", "", "Inconnu")
        ], ports);
        Assert.Throws<FormatException>(() => RouterOsParser.Ports(" 0  R  name=bridge1 type=bridge", "", ""));
    }

    [Fact]
    public void RouterOsVlansExpandListsAndRanges()
    {
        var vlans = RouterOsParser.Vlans(RosBridgeVlans);
        Assert.Equal([1, 10, 20, 30, 100, 101, 102], vlans.Select(v => v.Id));
        Assert.Equal(new VlanInfo(1, "default", "active", "ether1, bridge1(T)"), vlans[0]);
        Assert.Equal(new VlanInfo(10, "vlan10", "active", "ether2, bridge1(T), ether1(T)"), vlans[1]);
        Assert.Equal(new VlanInfo(30, "vlan30", "active", "ether3, bridge1(T), bond1(T)"), vlans[3]);
        Assert.Equal(new VlanInfo(101, "Invites", "inactive", "bond1(T)"), vlans[5]);
    }

    [Fact]
    public void RouterOsMacsSkipLocalEntries()
    {
        var macs = RouterOsParser.Macs("""
            Flags: X - DISABLED, I - INVALID; D - DYNAMIC; L - LOCAL
             0   DL  mac-address=48:8F:5A:00:00:01 on-interface=bridge1 bridge=bridge1
             1   D   mac-address=00:11:22:33:44:55 vid=10 on-interface=ether2 bridge=bridge1
             2       mac-address=AA:BB:CC:DD:EE:FF vid=20 on-interface=ether1 bridge=bridge1
             3   D   mac-address=00:11:22:33:44:66 on-interface=ether1 bridge=bridge1
            """);
        Assert.Equal(
        [
            new MacEntry(10, "001122334455", "DYNAMIC", "ether2"),
            new MacEntry(20, "AABBCCDDEEFF", "STATIC", "ether1"),
            new MacEntry(1, "001122334466", "DYNAMIC", "ether1")
        ], macs);
    }

    [Fact]
    public void RouterOsIdentityAndCounters()
    {
        var resource = """
                               uptime: 1w2d3h
                              version: 7.15.3 (stable)
                           build-time: 2024-07-24 10:39:00
                          free-memory: 450.2MiB
                                  cpu: ARMv7
                           board-name: CRS326-24G-2S+
                             platform: MikroTik
            """;
        Assert.Equal(new SwitchIdentity("CRS326-Bureau", "CRS326-24G-2S+", "7.15.3 (stable)"), RouterOsParser.Identity("  name: CRS326-Bureau", resource, "fallback"));
        Assert.Equal(new SwitchIdentity("fallback", "RouterOS (modèle inconnu)", "Inconnue"), RouterOsParser.Identity("", "", "fallback"));

        var counters = RouterOsParser.Counters("""
                                  name: ether1
                                status: link-ok
                      auto-negotiation: done
                                  rate: 1Gbps
                           full-duplex: yes
            """, """
                                  name: ether1
                          rx-broadcast: 1 234
                          rx-fcs-error: 3
                              rx-error: 0
                          tx-collision: 2
                              tx-error: 1
            """);
        Assert.Equal(new InterfaceCounters(3, 2, 0, "1000", "Full", "Actif", 1), counters);
        Assert.Equal(new InterfaceCounters(null, null, null, "Inconnue", "Inconnu", "Inactif", null), RouterOsParser.Counters("status: no-link", ""));
    }

    // ---- Ubiquiti EdgeSwitch (FASTPATH) --------------------------------------------------

    internal const string EdgePortAll = """
                         Admin     Physical   Physical   Link   Link    LACP   Actor
         Intf      Type  Mode      Mode       Status     Status Trap    Mode   Timeout
         --------- ------ --------- ---------- ---------- ------ ------- ------ --------
         0/1              Enable    Auto       1000 Full  Up     Enable  Enable long
         0/2              Enable    Auto                  Down   Enable  Enable long
         0/3              Disable   Auto                  Down   Enable  Enable long
         0/4              Enable    100 Half   100 Half   Up     Enable  Enable long
         3/1       LAG    Enable    Auto       10G Full   Up     Enable  N/A    N/A
        """;

    [Fact]
    public void EdgeSwitchPortAllThroughTextFsm()
    {
        Assert.Equal(
        [
            new PortInfo("0/1", "", "connected", "", "full", "1000", ""),
            new PortInfo("0/2", "", "notconnect", "", "", "auto", ""),
            new PortInfo("0/3", "", "disabled", "", "", "auto", ""),
            new PortInfo("0/4", "", "connected", "", "half", "100", ""),
            new PortInfo("3/1", "", "connected", "", "full", "10000", "LAG")
        ], EdgeSwitchParser.PortAll(EdgePortAll));
        Assert.Throws<FormatException>(() => EdgeSwitchParser.PortAll("% Invalid input detected at '^' marker."));
    }

    [Fact]
    public void EdgeSwitchVlanBrief()
    {
        var vlans = EdgeSwitchParser.Vlans("""
            VLAN ID VLAN Name                         VLAN Type
            ------- --------------------------------  -------------------
            1       default                           Default
            10      Bureaux RDC                       Static
            20      voice                             Static
            4089    Auto-Video                        Dynamic (Auto)
            """);
        Assert.Equal([1, 10, 20, 4089], vlans.Select(v => v.Id));
        Assert.Equal("Bureaux RDC", vlans[1].Name);
        Assert.Throws<FormatException>(() => EdgeSwitchParser.Vlans(""));
    }

    internal const string EdgeRunning = """
        !Current Configuration:
        !
        !System Description "EdgeSwitch 24-Port Lite, 1.9.3.5089558, Linux 3.6.5-f4a26ed5"
        !
        vlan database
        vlan 10,20
        vlan name 10 "Bureaux"
        exit

        interface 0/1
        description 'Poste accueil'
        vlan pvid 10
        vlan participation exclude 1
        vlan participation include 10
        exit

        interface 0/2
        description "Uplink"
        vlan participation include 1,10,20
        vlan tagging 10,20
        exit

        interface 0/3
        exit
        """;

    [Fact]
    public void EdgeSwitchRunningConfigPerPort()
    {
        var config = EdgeSwitchParser.RunningConfig(EdgeRunning);
        Assert.Equal(3, config.Count);
        Assert.Equal("Poste accueil", config["0/1"].Description);
        Assert.Equal(10, config["0/1"].Pvid);
        Assert.Equal([10], config["0/1"].Included.Order());
        Assert.Equal([1], config["0/1"].Excluded.Order());
        Assert.Equal("Uplink", config["0/2"].Description);
        Assert.Null(config["0/2"].Pvid);
        Assert.Equal([10, 20], config["0/2"].Tagged.Order());
        Assert.Empty(config["0/3"].Included);
    }

    [Fact]
    public void EdgeSwitchMergeAppliesPvidAndTagging()
    {
        var pvids = EdgeSwitchParser.Pvids("""
                                                             Default
            Interface  Port      Acceptable  Ingress         Priority  Admin   Admin    Protected
                       VLAN ID   Frame Types Filtering       Mode      Mode    Mode
            ---------  --------  ----------- -----------     --------  ------- -------  ---------
            0/1        10        Admit All   Disable         0         Auto    Auto     Disable
            0/2        1         Admit All   Disable         0         Auto    Auto     Disable
            0/4        30        Admit All   Disable         0         Auto    Auto     Disable
            """);
        Assert.Equal(30, pvids["0/4"]);
        var ports = EdgeSwitchParser.Merge(EdgeSwitchParser.PortAll(EdgePortAll), EdgeSwitchParser.RunningConfig(EdgeRunning), pvids);
        Assert.Equal(("Poste accueil", "10", "access"), (ports[0].Description, ports[0].Vlan, ports[0].Mode));
        Assert.Equal(("Uplink", "trunk", "trunk"), (ports[1].Description, ports[1].Vlan, ports[1].Mode));
        Assert.Equal(("", "1", "access"), (ports[2].Description, ports[2].Vlan, ports[2].Mode));
        Assert.Equal(("30", "Inconnu"), (ports[3].Vlan, ports[3].Mode));
        Assert.Equal(("", "Inconnu"), (ports[4].Vlan, ports[4].Mode));
    }

    [Fact]
    public void EdgeSwitchIdentity()
    {
        const string version = """
            Switch: 1

            System Description............................. EdgeSwitch 24-Port Lite, 1.9.3.5089558, Linux 3.6.5-f4a26ed5
            Machine Type................................... EdgeSwitch 24-Port Lite
            Machine Model.................................. ES-24-Lite
            Serial Number.................................. F09FC2000000
            Software Version............................... 1.9.3.5089558
            """;
        Assert.Equal(new SwitchIdentity("edge1", "ES-24-Lite", "1.9.3.5089558"), EdgeSwitchParser.Identity("edge1", version));
        Assert.Equal(new SwitchIdentity("edge1", "EdgeSwitch 24-Port Lite", "1.9.3.5089558"), EdgeSwitchParser.Identity("edge1", """
            System Description............................. EdgeSwitch 24-Port Lite, 1.9.3.5089558, Linux 3.6.5-f4a26ed5
            Machine Type................................... EdgeSwitch 24-Port Lite
            """));
    }

    [Fact]
    public void EdgeSwitchCounters()
    {
        var counters = EdgeSwitchParser.Counters("""
            Total Packets Received (Octets)................ 123456789
            Packets Received 64 Octets..................... 100
            Total Packets Received Without Errors.......... 5000
            Total Packets Received with MAC Errors......... 4
            Jabbers Received............................... 0
            Alignment Errors............................... 0
            FCS Errors..................................... 3
            Total Transmit Errors.......................... 1
            Single Collision Frames........................ 2
            Multiple Collision Frames...................... 1
            Excessive Collision Frames..................... 0
            """, """
                             Admin     Physical   Physical   Link   Link    LACP   Actor
             Intf      Type  Mode      Mode       Status     Status Trap    Mode   Timeout
             --------- ------ --------- ---------- ---------- ------ ------- ------ --------
             0/1              Enable    Auto       1000 Full  Up     Enable  Enable long
            """);
        Assert.Equal(new InterfaceCounters(3, 3, 4, "1000", "Full", "Actif", 1), counters);
    }

    // ---- NX-OS, Arista EOS, Dell OS6 / OS9 / OS10 ----------------------------------------

    [Theory]
    [InlineData("connected", "connected")]
    [InlineData("up", "connected")]
    [InlineData("notconnec", "notconnect")]
    [InlineData("notconnect", "notconnect")]
    [InlineData("down", "notconnect")]
    [InlineData("Admin Down", "disabled")]
    [InlineData("disabled", "disabled")]
    [InlineData("err-disabled", "err-disabled")]
    [InlineData("xcvrAbsen", "sfpAbsent")]
    [InlineData("monitoring", "monitoring")]
    public void CiscoLikeStatusVocabulary(string raw, string expected) => Assert.Equal(expected, CiscoLikeParser.Status(raw));

    [Fact]
    public void NxosStatusTable()
    {
        var ports = CiscoLikeParser.StatusTable(SwitchVendor.CiscoNxos, """
            --------------------------------------------------------------------------------
            Port          Name               Status    Vlan      Duplex  Speed   Type
            --------------------------------------------------------------------------------
            mgmt0         --                 connected routed    full    1000    --
            Eth1/1        Uplink core        connected trunk     full    10G     10Gbase-SR
            Eth1/2        --                 notconnec 10        auto    auto    10Gbase-SR
            Eth1/3        Serveur            disabled  20        auto    auto    --
            Eth1/4        --                 connected in Po10   full    10G     10Gbase-SR
            Po10          --                 connected trunk     full    10G     --
            """);
        Assert.Equal(
        [
            new PortInfo("mgmt0", "", "connected", "routed", "full", "1000", "", "routed"),
            new PortInfo("Eth1/1", "Uplink core", "connected", "trunk", "full", "10000", "10Gbase-SR", "trunk"),
            new PortInfo("Eth1/2", "", "notconnect", "10", "auto", "auto", "10Gbase-SR", "access"),
            new PortInfo("Eth1/3", "Serveur", "disabled", "20", "auto", "auto", "", "access"),
            new PortInfo("Eth1/4", "", "connected", "trunk", "full", "10000", "10Gbase-SR", "trunk"),
            new PortInfo("Po10", "", "connected", "trunk", "full", "10000", "", "trunk")
        ], ports);
        Assert.Throws<FormatException>(() => CiscoLikeParser.StatusTable(SwitchVendor.CiscoNxos, "% Permission denied"));
    }

    [Fact]
    public void AristaStatusTableDropsFlagColumns()
    {
        var ports = CiscoLikeParser.StatusTable(SwitchVendor.Arista, """
            Port       Name        Status       Vlan     Duplex Speed  Type            Flags Encapsulation
            Et1        Uplink      connected    trunk    full   10G    10GBASE-SR
            Et2                    notconnect   10       auto   auto   10GBASE-T
            Et3/1      Serveur     connected    20       full   1G     1000BASE-T      e     dot1q
            Ma1                    connected    routed   a-full a-1G   10/100/1000
            Po1                    connected    trunk    full   20G    N/A
            """);
        Assert.Equal(["Et1", "Et2", "Et3/1", "Ma1", "Po1"], ports.Select(p => p.Name));
        Assert.Equal(new PortInfo("Et3/1", "Serveur", "connected", "20", "full", "1000", "1000BASE-T", "access"), ports[2]);
        Assert.Equal(("a-full", "a-1000"), (ports[3].Duplex, ports[3].Speed));
        Assert.Equal("", ports[1].Description);
    }

    [Fact]
    public void DellOs6StatusIncludesPortChannels()
    {
        var ports = CiscoLikeParser.DellOs6Status("""
            Port       Description               Duplex Speed    Neg  Link   Flow Control
                                                                      State  Status
            ---------  ------------------------- ------ -------  ---- ------ -------
            Gi1/0/1    Poste accueil             Full   1000     Auto Up     Inactive
            Gi1/0/2                              N/A    Unknown  Auto Down   Inactive
            Te1/0/1    Uplink                    Full   10000    Off  Up     Active

            Port    Description                    Link
            Channel                                State
            ------- ------------------------------ -------
            Po1                                    Down
            Po2     Agregat serveur                Up
            """);
        Assert.Equal(
        [
            new PortInfo("Gi1/0/1", "Poste accueil", "connected", "", "full", "1000", ""),
            new PortInfo("Gi1/0/2", "", "notconnect", "", "n/a", "unknown", ""),
            new PortInfo("Te1/0/1", "Uplink", "connected", "", "full", "10000", ""),
            new PortInfo("Po1", "", "notconnect", "", "", "", ""),
            new PortInfo("Po2", "", "connected", "", "", "", "Agregat serveur")
        ], ports);
    }

    [Fact]
    public void DellOs9StatusReadsVlanColumn()
    {
        var ports = CiscoLikeParser.DellOs9Status("""
            Port                Description  Status Speed        Duplex Vlan
            Gi 1/1              Poste        Up     1000 Mbit    Full   10
            Gi 1/2                           Down   Auto         Auto   --
            Gi 1/3              Uplink       Up     1000 Mbit    Full   1,10-20
            Te 1/49                          Admin Down Auto     Auto   1
            Po 1                Lag          Up     2000 Mbit    Full   1-4094
            """);
        Assert.Equal(
        [
            new PortInfo("Gi 1/1", "Poste", "connected", "10", "full", "1000", "", "access"),
            new PortInfo("Gi 1/2", "", "notconnect", "", "auto", "auto", "", "Inconnu"),
            new PortInfo("Gi 1/3", "Uplink", "connected", "trunk", "full", "1000", "", "trunk"),
            new PortInfo("Te 1/49", "", "disabled", "1", "auto", "auto", "", "access"),
            new PortInfo("Po 1", "Lag", "connected", "trunk", "full", "2000", "", "trunk")
        ], ports);
    }

    [Fact]
    public void DellOs10StatusUsesModeColumn()
    {
        var ports = CiscoLikeParser.DellOs10Status("""
            --------------------------------------------------------------------------------------------------
            Port            Description     Status   Speed    Duplex   Mode Vlan Tagged-Vlans
            --------------------------------------------------------------------------------------------------
            Eth 1/1/1       Poste           up       1000M    full     A    10   -
            Eth 1/1/2                       down     0        auto     A    1    -
            Eth 1/1/3       Uplink          up       10G      full     T    1    10-20,30
            Eth 1/1/4                       admin-down 0      auto     -    -    -
            Eth 1/1/5:1     Breakout        up       25G      full     A    1    -
            """);
        Assert.Equal(
        [
            new PortInfo("ethernet1/1/1", "Poste", "connected", "10", "full", "1000", "", "access"),
            new PortInfo("ethernet1/1/2", "", "notconnect", "1", "auto", "0", "", "access"),
            new PortInfo("ethernet1/1/3", "Uplink", "connected", "trunk", "full", "10000", "", "trunk"),
            new PortInfo("ethernet1/1/4", "", "disabled", "", "auto", "0", "", "routed"),
            new PortInfo("ethernet1/1/5:1", "Breakout", "connected", "1", "full", "25000", "", "access")
        ], ports);
    }

    [Fact]
    public void DellOs6VlansAndMembership()
    {
        var vlans = CiscoLikeParser.DellOs6Vlans("""
            VLAN   Name                             Ports          Type
            -----  ---------------                  -------------  --------------
            1      default                          Po1-2,         Default
                                                    Gi1/0/1-4,
                                                    Te1/0/1
            10     Bureaux                          Gi1/0/5-6,     Static
                                                    Te1/0/1
            20     VoIP                                            Static
            """);
        Assert.Equal(
        [
            new VlanInfo(1, "default", "active", "Po1-2,Gi1/0/1-4,Te1/0/1"),
            new VlanInfo(10, "Bureaux", "active", "Gi1/0/5-6,Te1/0/1"),
            new VlanInfo(20, "VoIP", "active", "")
        ], vlans);
        var membership = CiscoLikeParser.MembershipVlans(SwitchVendor.DellOs6, vlans);
        Assert.Equal("1", membership["Po2"]);
        Assert.Equal("1", membership["Gi1/0/4"]);
        Assert.Equal("10", membership["Gi1/0/6"]);
        Assert.Equal("trunk", membership["Te1/0/1"]);
        Assert.Equal(9, membership.Count);
    }

    [Fact]
    public void DellOs9VlansSplitUntaggedAndTagged()
    {
        var (vlans, untagged, tagged) = CiscoLikeParser.DellVlans(SwitchVendor.DellOs9, """
            Codes: * - Default VLAN, G - GVRP VLANs, R - Remote Port Mirroring VLANs, P - Primary, C - Community, I - Isolated
                   O - Openflow, Vx - Vxlan
            Q: U - Untagged, T - Tagged
               x - Dot1x untagged, X - Dot1x tagged
               G - GVRP tagged, M - Vlan-stack

                NUM    Status    Description                     Q Ports
            *   1      Active                                    U Gi 1/3-4
                                                                 U Po1
                10     Active    Bureaux                         U Gi 1/1,1/5
                                                                 T Po1
                20     Inactive
            """);
        Assert.Equal(
        [
            new VlanInfo(1, "default", "active", "Gi 1/3, Gi 1/4, Po 1"),
            new VlanInfo(10, "Bureaux", "active", "Gi 1/1, Gi 1/5, Po 1(T)"),
            new VlanInfo(20, "VLAN0020", "inactive", "")
        ], vlans);
        Assert.Equal(10, untagged["Gi 1/5"]);
        Assert.Equal(1, untagged["Po 1"]);
        Assert.Equal(["Po 1"], tagged);
    }

    [Fact]
    public void DellOs10VlansExpandSlotRanges()
    {
        var (vlans, untagged, tagged) = CiscoLikeParser.DellVlans(SwitchVendor.DellOs10, """
            Codes: * - Default VLAN, M - Management VLAN, R - Remote Port Mirroring VLANs,
                   @ - Attached to Virtual Network, P - Primary, C - Community, I - Isolated
            Q: A - Access (Untagged), T - Tagged
                NUM    Status    Description                     Q Ports
            *   1      Active                                    A Eth1/1/1-1/1/3
                                                                 A Eth1/1/5
                10     Active    Bureaux                         T Eth1/1/4
                                                                 A Eth1/1/6
            """);
        Assert.Equal("ethernet1/1/1, ethernet1/1/2, ethernet1/1/3, ethernet1/1/5", vlans[0].Ports);
        Assert.Equal("ethernet1/1/4(T), ethernet1/1/6", vlans[1].Ports);
        Assert.Equal(1, untagged["ethernet1/1/2"]);
        Assert.Equal(10, untagged["ethernet1/1/6"]);
        Assert.Equal(["ethernet1/1/4"], tagged);
        Assert.Throws<FormatException>(() => CiscoLikeParser.DellVlans(SwitchVendor.DellOs10, "% Error: Invalid input"));
    }

    [Fact]
    public void CiscoLikeIdentities()
    {
        Assert.Equal(new SwitchIdentity("n9k", "Nexus9000 C93180YC-EX", "9.3(10)"), CiscoLikeParser.Identity(SwitchVendor.CiscoNxos, "n9k", """
            Cisco Nexus Operating System (NX-OS) Software
            TAC support: http://www.cisco.com/tac
            Software
              BIOS: version 07.69
              NXOS: version 9.3(10)
            Hardware
              cisco Nexus9000 C93180YC-EX chassis
            """));
        Assert.Equal(new SwitchIdentity("eos", "DCS-7050TX-64-R", "4.28.3M"), CiscoLikeParser.Identity(SwitchVendor.Arista, "eos", """
            Arista DCS-7050TX-64-R
            Hardware version:    01.11
            Serial number:       JPE00000000
            System MAC address:  001c.7300.0000

            Software image version: 4.28.3M
            Architecture:           i686
            """));
        Assert.Equal(new SwitchIdentity("n1548", "N1548P", "6.6.3.10"), CiscoLikeParser.Identity(SwitchVendor.DellOs6, "n1548", """
            Machine Description............... Dell Networking Switch
            System Model ID................... N1548P
            Machine Type...................... Dell Networking N1548P

            unit active      backup      current-active next-active
            ---- ----------- ----------- -------------- --------------
            1    6.6.3.10    6.6.3.4     6.6.3.10       6.6.3.10
            """));
        Assert.Equal(new SwitchIdentity("s4048", "S4048-ON", "9.14(2.4)"), CiscoLikeParser.Identity(SwitchVendor.DellOs9, "s4048", """
            Dell Real Time Operating System Software
            Dell Operating System Version:  2.0
            Dell Application Software Version:  9.14(2.4)
            Copyright (c) 1999-2019 by Dell Inc. All Rights Reserved.
            Dell Networking OS uptime is 1 day(s), 2 hour(s), 3 minute(s)

            System Type: S4048-ON
            """));
        Assert.Equal(new SwitchIdentity("os10", "S4148F-ON", "10.5.2.6"), CiscoLikeParser.Identity(SwitchVendor.DellOs10, "os10", """
            Dell EMC Networking OS10 Enterprise
            Copyright (c) 1999-2021 by Dell Inc. All Rights Reserved.
            OS Version: 10.5.2.6
            Build Version: 10.5.2.6.258
            System Type: S4148F-ON
            Architecture: x86_64
            """));
        Assert.Equal(new SwitchIdentity("x", "EOS (modèle inconnu)", "Inconnue"), CiscoLikeParser.Identity(SwitchVendor.Arista, "x", ""));
    }

    // ---- UniFi local shell ---------------------------------------------------------------

    internal const string UniFiPorts = """
        Port  Link  Enable  Speed  Duplex  PVID  Name
        ----  ----  ------  -----  ------  ----  ----------------
        1     Up    Yes     1000   Full    1     Uplink
        2     Down  Yes     0      -       10    Imprimante
        3     Down  No      0      -       1     Port 3
        """;

    [Fact]
    public void UniFiShellIdentity()
    {
        Assert.Equal(new SwitchIdentity("USW-Bureau", "US-24-250W", "6.6.65.15435"), UniFiShellParser.Identity("""
            Model:       US-24-250W
            Version:     6.6.65.15435
            MAC Address: 74:83:c2:00:00:01
            IP Address:  192.168.1.20
            Hostname:    USW-Bureau
            Uptime:      12345 seconds

            Status:      Connected (http://192.168.1.2:8080/inform)
            """, "fallback"));
        Assert.Equal(new SwitchIdentity("fallback", "UniFi (modèle inconnu)", "Inconnue"), UniFiShellParser.Identity("", "fallback"));
    }

    [Fact]
    public void UniFiShellPortsByColumnName()
    {
        var ports = UniFiShellParser.Ports(UniFiPorts);
        Assert.Equal(
        [
            new PortInfo("Port 1", "Uplink", "connected", "1", "full", "1000", ""),
            new PortInfo("Port 2", "Imprimante", "notconnect", "10", "-", "0", ""),
            new PortInfo("Port 3", "Port 3", "disabled", "1", "-", "0", "")
        ], ports);
        Assert.Equal([new VlanInfo(1, "", "active", "Port 1, Port 3"), new VlanInfo(10, "", "active", "Port 2")], UniFiShellParser.Vlans(ports));
        Assert.Throws<FormatException>(() => UniFiShellParser.Ports("sh: swctrl: not found"));
    }

    [Fact]
    public void UniFiShellMacsAndCounters()
    {
        var macs = UniFiShellParser.Macs("""
            VLAN  MAC Address        Port  Type
            ----  -----------------  ----  -------
            1     00:11:22:33:44:55  1     Dynamic
            10    aa:bb:cc:dd:ee:ff  2     Static
            """);
        Assert.Equal([new MacEntry(1, "001122334455", "DYNAMIC", "Port 1"), new MacEntry(10, "AABBCCDDEEFF", "STATIC", "Port 2")], macs);
        Assert.Empty(UniFiShellParser.Macs("Total MAC entries: 0"));
        Assert.Throws<FormatException>(() => UniFiShellParser.Macs("sh: swctrl: not found"));

        Assert.Equal(new InterfaceCounters(null, null, null, "1000", "Full", "Actif", null), UniFiShellParser.Counters(UniFiPorts, "Port 1"));
        Assert.Equal(new InterfaceCounters(null, null, null, "Inconnue", "Inconnu", "Inactif", null), UniFiShellParser.Counters(UniFiPorts, "Port 3"));
        Assert.Throws<ArgumentException>(() => UniFiShellParser.Counters(UniFiPorts, "Port 9"));
    }

    // ---- UniFi controller JSON -----------------------------------------------------------

    internal const string UniFiNetworksJson = """
        {"meta":{"rc":"ok"},"data":[
         {"_id":"n1","name":"Default","purpose":"corporate","vlan_enabled":false},
         {"_id":"n10","name":"Bureaux","purpose":"corporate","vlan_enabled":true,"vlan":10},
         {"_id":"n20","name":"VoIP","purpose":"vlan-only","vlan":20},
         {"_id":"n30","name":"Invites","purpose":"guest","vlan_enabled":true,"vlan":"30"},
         {"_id":"w1","name":"WAN","purpose":"wan"},
         {"_id":"vpn","name":"VPN","purpose":"remote-user-vpn"}
        ]}
        """;

    internal const string UniFiDevicesJson = """
        {"meta":{"rc":"ok"},"data":[
         {"_id":"ap1","type":"uap","mac":"74:83:c2:00:00:99","name":"AP"},
         {"_id":"dev1","type":"usw","mac":"74:83:c2:00:00:01","name":"USW-Bureau","model":"US24P250","version":"6.6.65.15435",
          "port_table":[
            {"port_idx":1,"name":"Port 1","up":true,"speed":1000,"full_duplex":true,"media":"GE","enable":true,"forward":"native","native_networkconf_id":"n1","tagged_vlan_mgmt":"auto","rx_errors":2,"tx_errors":1,
              "mac_table":[{"mac":"00:11:22:33:44:55","vlan":1,"static":false}]},
            {"port_idx":2,"name":"Imprimante","up":false,"speed":0,"media":"GE","enable":true,"native_networkconf_id":"n1","tagged_vlan_mgmt":"auto"},
            {"port_idx":3,"name":"Port 3","up":true,"speed":100,"full_duplex":false,"media":"GE","native_networkconf_id":"n1","tagged_vlan_mgmt":"auto"},
            {"port_idx":4,"name":"Port 4","up":false,"enable":false,"native_networkconf_id":"n1","tagged_vlan_mgmt":"block_all"}
          ],
          "port_overrides":[
            {"port_idx":2,"name":"Imprimante","native_networkconf_id":"n10","tagged_vlan_mgmt":"block_all","poe_mode":"off"},
            {"port_idx":3,"native_networkconf_id":"n10","tagged_vlan_mgmt":"custom","excluded_networkconf_ids":["n1","n30"]}
          ]}
        ]}
        """;

    private static IReadOnlyList<UniFiNetwork> Networks => UniFiParser.Networks(UniFiNetworksJson);
    private static JsonObject Device => UniFiParser.Switch(UniFiDevicesJson, null);

    [Fact]
    public void UniFiDataUnwrapsOrExplains()
    {
        Assert.Equal(2, UniFiParser.Data("""{"meta":{"rc":"ok"},"data":[{},{}]}""").Count);
        Assert.Contains("api.err.LoginRequired", Assert.Throws<InvalidOperationException>(() => UniFiParser.Data("""{"meta":{"rc":"error","msg":"api.err.LoginRequired"},"data":[]}""")).Message);
        Assert.Throws<FormatException>(() => UniFiParser.Data("<html>"));
        Assert.Throws<FormatException>(() => UniFiParser.Data("""{"meta":{"rc":"ok"}}"""));
    }

    [Fact]
    public void UniFiNetworksKeepSwitchingPurposes()
    {
        Assert.Equal(
        [
            new UniFiNetwork("n1", "Default", 1, "corporate"),
            new UniFiNetwork("n10", "Bureaux", 10, "corporate"),
            new UniFiNetwork("n20", "VoIP", 20, "vlan-only"),
            new UniFiNetwork("n30", "Invites", 30, "guest")
        ], Networks);
        Assert.True(Networks[2].IsVlanOnly);
        Assert.False(Networks[1].IsVlanOnly);
    }

    [Fact]
    public void UniFiSwitchSelection()
    {
        Assert.Equal("dev1", Device["_id"]!.GetValue<string>());
        Assert.Equal("dev1", UniFiParser.Switch(UniFiDevicesJson, "74-83-C2-00-00-01")["_id"]!.GetValue<string>());
        Assert.Equal("dev1", UniFiParser.Switch(UniFiDevicesJson, "usw-bureau")["_id"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => UniFiParser.Switch(UniFiDevicesJson, "inconnu"));
        Assert.Throws<InvalidOperationException>(() => UniFiParser.Switch("""{"meta":{"rc":"ok"},"data":[{"type":"uap"}]}""", null));
        var two = """{"meta":{"rc":"ok"},"data":[{"type":"usw","mac":"74:83:c2:00:00:01","name":"A"},{"type":"usw","mac":"74:83:c2:00:00:02","name":"B"}]}""";
        Assert.Contains("Plusieurs switchs", Assert.Throws<InvalidOperationException>(() => UniFiParser.Switch(two, null)).Message);
        Assert.Equal(new SwitchIdentity("USW-Bureau", "US24P250", "6.6.65.15435"), UniFiParser.Identity(Device));
    }

    [Fact]
    public void UniFiPortsApplyOverrides()
    {
        Assert.Equal(
        [
            new PortInfo("Port 1", "", "connected", "trunk", "full", "1000", "GE", "trunk"),
            new PortInfo("Port 2", "Imprimante", "notconnect", "10", "auto", "auto", "GE", "access"),
            new PortInfo("Port 3", "", "connected", "trunk", "half", "100", "GE", "trunk"),
            new PortInfo("Port 4", "", "disabled", "1", "auto", "auto", "", "access")
        ], UniFiParser.Ports(Device, Networks));
        Assert.Equal(
        [
            new VlanInfo(1, "Default", "active", "Port 4"),
            new VlanInfo(10, "Bureaux", "active", "Port 2"),
            new VlanInfo(20, "VoIP", "active", ""),
            new VlanInfo(30, "Invites", "active", "")
        ], UniFiParser.Vlans(Device, Networks));
    }

    [Fact]
    public void UniFiMacsFromPortTableOrClients()
    {
        Assert.Equal([new MacEntry(1, "001122334455", "DYNAMIC", "Port 1")], UniFiParser.Macs(Device, null));
        var bare = JsonNode.Parse("""{"type":"usw","mac":"74:83:c2:00:00:01","port_table":[{"port_idx":2},{"port_idx":3}]}""")!.AsObject();
        var clients = """
            {"meta":{"rc":"ok"},"data":[
             {"mac":"aa:bb:cc:dd:ee:ff","sw_mac":"74:83:c2:00:00:01","sw_port":2,"vlan":10},
             {"mac":"aa:bb:cc:dd:ee:00","sw_mac":"74:83:c2:00:00:77","sw_port":5},
             {"mac":"aa:bb:cc:dd:ee:11","sw_mac":"74:83:c2:00:00:01","sw_port":3}
            ]}
            """;
        Assert.Equal([new MacEntry(10, "AABBCCDDEEFF", "DYNAMIC", "Port 2"), new MacEntry(1, "AABBCCDDEE11", "DYNAMIC", "Port 3")], UniFiParser.Macs(bare, clients));
    }

    [Fact]
    public void UniFiCounters()
    {
        Assert.Equal(new InterfaceCounters(null, null, 2, "1000", "Full", "Actif", 1), UniFiParser.Counters(Device, "Port 1"));
        Assert.Equal(new InterfaceCounters(null, null, null, "Inconnue", "Inconnu", "Inactif", null), UniFiParser.Counters(Device, "Port 2"));
        Assert.Throws<ArgumentException>(() => UniFiParser.Counters(Device, "Port 9"));
    }

    [Fact]
    public void UniFiAccessOverrideKeepsOtherPorts()
    {
        Assert.True(UniFiParser.HasTaggedVlanManagement(Device));
        var overrides = UniFiParser.PortOverrides(Device, CommandPlan.Access("Port 1", 20, SwitchVendor.UniFi), Networks);
        Assert.Equal([2, 3, 1], overrides.Select(o => o!["port_idx"]!.GetValue<int>()));
        var target = overrides[2]!.AsObject();
        Assert.Equal("n20", target["native_networkconf_id"]!.GetValue<string>());
        Assert.Equal("block_all", target["tagged_vlan_mgmt"]!.GetValue<string>());
        Assert.Empty(target["excluded_networkconf_ids"]!.AsArray());
        Assert.Equal("off", overrides[0]!["poe_mode"]!.GetValue<string>());
    }

    [Fact]
    public void UniFiOverrideKeepsUnrelatedFieldsOfTheTarget()
    {
        var overrides = UniFiParser.PortOverrides(Device, CommandPlan.Access("Port 2", 30, SwitchVendor.UniFi), Networks);
        Assert.Equal([3, 2], overrides.Select(o => o!["port_idx"]!.GetValue<int>()));
        Assert.Equal("off", overrides[1]!["poe_mode"]!.GetValue<string>());
        Assert.Equal("Imprimante", overrides[1]!["name"]!.GetValue<string>());
        Assert.Equal("n30", overrides[1]!["native_networkconf_id"]!.GetValue<string>());
    }

    [Fact]
    public void UniFiTrunkOverrideExcludesOtherNetworks()
    {
        var overrides = UniFiParser.PortOverrides(Device, CommandPlan.Trunk("Port 1", 1, "1,10,20", SwitchVendor.UniFi), Networks);
        var target = overrides[^1]!.AsObject();
        Assert.Equal("n1", target["native_networkconf_id"]!.GetValue<string>());
        Assert.Equal("custom", target["tagged_vlan_mgmt"]!.GetValue<string>());
        Assert.Equal(["n30"], target["excluded_networkconf_ids"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void UniFiDescriptionOverride()
    {
        Assert.Equal("Camera hall", UniFiParser.PortOverrides(Device, CommandPlan.Describe("Port 1", "Camera hall", SwitchVendor.UniFi), Networks)[^1]!["name"]!.GetValue<string>());
        Assert.False(UniFiParser.PortOverrides(Device, CommandPlan.Describe("Port 2", "", SwitchVendor.UniFi), Networks)[^1]!.AsObject().ContainsKey("name"));
    }

    [Fact]
    public void UniFiOverrideRefusals()
    {
        Assert.Contains("VLAN 40", Assert.Throws<InvalidOperationException>(() => UniFiParser.PortOverrides(Device, CommandPlan.Access("Port 1", 40, SwitchVendor.UniFi), Networks)).Message);
        Assert.Throws<ArgumentException>(() => UniFiParser.PortOverrides(Device, CommandPlan.Access("Port 9", 10, SwitchVendor.UniFi), Networks));
        Assert.Throws<NotSupportedException>(() => UniFiParser.PortOverrides(Device, CommandPlan.Enabled("Port 1", false, SwitchVendor.UniFi), Networks));
        var old = JsonNode.Parse("""{"type":"usw","port_table":[{"port_idx":1,"forward":"all"}]}""")!.AsObject();
        Assert.False(UniFiParser.HasTaggedVlanManagement(old));
        Assert.Throws<NotSupportedException>(() => UniFiParser.PortOverrides(old, CommandPlan.Access("Port 1", 10, SwitchVendor.UniFi), Networks));
        Assert.Equal(new PortInfo("Port 1", "", "notconnect", "trunk", "auto", "auto", "", "trunk"), UniFiParser.Ports(old, Networks)[0]);
    }

    [Fact]
    public void UniFiNetworkCreationAndDeletion()
    {
        var body = UniFiParser.NewNetwork(40, "Cameras", Networks);
        Assert.Equal("""{"name":"Cameras","purpose":"vlan-only","vlan_enabled":true,"vlan":40,"igmp_snooping":false}""", body.ToJsonString());
        Assert.Contains("Bureaux", Assert.Throws<InvalidOperationException>(() => UniFiParser.NewNetwork(10, "Doublon", Networks)).Message);
        Assert.Throws<ArgumentException>(() => UniFiParser.NewNetwork(1003, "Reserve", Networks));
        Assert.Equal("n20", UniFiParser.NetworkToDelete(20, Networks).Id);
        Assert.Throws<InvalidOperationException>(() => UniFiParser.NetworkToDelete(10, Networks));
        Assert.Throws<InvalidOperationException>(() => UniFiParser.NetworkToDelete(99, Networks));
    }

    // ---- TextFSM engine ------------------------------------------------------------------

    [Fact]
    public void TextFsmFilldownAndRequired()
    {
        var fsm = new TextFsm("""
            Value Filldown VLAN (\d+)
            Value Required PORT (\S+)

            Start
              ^VLAN ${VLAN}
              ^\s+${PORT} -> Record
            """);
        var records = fsm.Parse("VLAN 10\n  ge1\n  ge2\nVLAN 20\n  ge3\nVLAN 30");
        Assert.Equal(["VLAN", "PORT"], fsm.Header);
        Assert.Equal([("10", "ge1"), ("10", "ge2"), ("20", "ge3")], records.Select(r => (r["VLAN"], r["PORT"])));
    }

    [Fact]
    public void TextFsmListsStatesAndBareTransitions()
    {
        var fsm = new TextFsm("""
            # Groups and their members.
            Value NAME (\S+)
            Value List MEMBERS (\S+)

            Start
              ^Group ${NAME} -> Members

            Members
              ^\s+member ${MEMBERS}
              ^end -> Record Start
            """);
        var records = fsm.Parse("Group admins\n  member alice\n  member bob\nend\nGroup guests\nend\n");
        Assert.Equal(2, records.Count);
        Assert.Equal(["alice", "bob"], records[0].List("MEMBERS"));
        Assert.Equal("alice bob", records[0]["MEMBERS"]);
        Assert.Equal("guests", records[1]["NAME"]);
        Assert.Empty(records[1].List("MEMBERS"));
        Assert.Equal("", records[1]["ABSENT"]);
    }

    [Fact]
    public void TextFsmFillupAndContinue()
    {
        var fsm = new TextFsm("""
            Value Fillup HOST (\S+)
            Value PORT (\d+)
            Value SPEED (\d+)

            Start
              ^port ${PORT} -> Continue
              ^port \d+ speed ${SPEED} -> Record
              ^host ${HOST}
            """);
        var records = fsm.Parse("port 1 speed 100\nport 2 speed 1000\nhost sw1");
        Assert.Equal([("sw1", "1", "100"), ("sw1", "2", "1000")], records.Take(2).Select(r => (r["HOST"], r["PORT"], r["SPEED"])));
    }

    [Fact]
    public void TextFsmEofAndEndStates()
    {
        const string values = "Value X (\\d+)\n\nStart\n  ^x=${X}\n";
        Assert.Single(new TextFsm(values).Parse("x=5"));
        Assert.Empty(new TextFsm(values + "\nEOF\n").Parse("x=5"));
        Assert.Single(new TextFsm("Value X (\\d+)\n\nStart\n  ^x=${X} -> Record\n  ^stop -> End\n").Parse("x=1\nstop\nx=2"));
        Assert.Equal("1", new TextFsm("Value X (\\d+)\n\nStart\n  ^x=${X}$$ -> Record\n").Parse("x=1\nx=2z")[0]["X"]);
    }

    [Fact]
    public void TextFsmErrorAction()
    {
        var fsm = new TextFsm("Value X (\\d+)\n\nStart\n  ^${X} -> Record\n  ^% Invalid -> Error \"commande refusée\"\n");
        Assert.Equal("commande refusée", Assert.Throws<FormatException>(() => fsm.Parse("1\n% Invalid input")).Message);
    }

    [Theory]
    [InlineData("", "aucune Value")]
    [InlineData("Value X \\d+\n\nStart\n  ^x", "ligne Value invalide")]
    [InlineData("Value Bogus X (\\d+)\n\nStart\n  ^x", "option « Bogus » inconnue")]
    [InlineData("Value X (\\d+)\nValue X (\\d+)\n\nStart\n  ^x", "dupliquée")]
    [InlineData("Value X (\\d+)\n\nState1\n  ^x", "état Start manquant")]
    [InlineData("Value X (\\d+)\n\nStart\n  ^${Y}", "non déclarée")]
    [InlineData("Value X (\\d+)\n\nStart\n  x", "commence par « ^ »")]
    [InlineData("Value X (\\d+)\n\nStart\n  ^x -> Goto", "état cible « Goto » inconnu")]
    [InlineData("Value X (\\d+)\n\nStart\n  ^x -> Continue Start", "Continue")]
    [InlineData("Value X (\\d+)\n\nStart\n  ^x -> Record.Next", "action")]
    [InlineData("Value X (\\d+)\n\nStart\n  ^x\nStart\n  ^y", "dupliqué")]
    public void TextFsmRejectsInvalidTemplates(string template, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => new TextFsm(template)).Message);

    [Fact] public void EdgeSwitchTemplateHeader() =>
        Assert.Equal(["PORT", "TYPE", "ADMIN", "PHYSICAL_MODE", "SPEED", "DUPLEX", "LINK"], new TextFsm(EdgeSwitchParser.ShowPortAllTemplate).Header);

    // ---- ColumnTable ---------------------------------------------------------------------

    [Fact]
    public void ColumnTableUsesDashedUnderline()
    {
        var table = ColumnTable.Parse("""
            Port      Name               Status
            --------  -----------------  ----------
            Gi1/0/1   Bureau 12          connected
            Gi1/0/2                      notconnect
            """, "Port")!;
        Assert.Equal(["Port", "Name", "Status"], table.Columns);
        Assert.Equal("Bureau 12", table.Rows[0]["Name"]);
        Assert.Equal("", table.Rows[1]["Name"]);
        Assert.Equal("notconnect", table.Rows[1]["status"]);
    }

    [Fact]
    public void ColumnTableFallsBackToHeaderWords()
    {
        var table = ColumnTable.Parse("Port Status Vlan\n1    up     10\n2    down\n", "Port")!;
        Assert.Equal(["Port", "Status", "Vlan"], table.Columns);
        Assert.Equal(("1", "up", "10"), (table.Rows[0]["Port"], table.Rows[0]["Status"], table.Rows[0]["Vlan"]));
        Assert.Equal("", table.Rows[1]["Vlan"]);
        Assert.Null(ColumnTable.Parse("no table here", "Port"));
    }

    // ---- ParseKit ------------------------------------------------------------------------

    [Theory]
    [InlineData("10G", "10000")]
    [InlineData("a-1G", "a-1000")]
    [InlineData("1000M", "1000")]
    [InlineData("1000 Mbit", "1000")]
    [InlineData("1Gbps", "1000")]
    [InlineData("2.5G", "2500")]
    [InlineData("100", "100")]
    [InlineData("auto", "auto")]
    [InlineData("Unknown", "unknown")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("10/100/1000", "10/100/1000")]
    public void ParseKitSpeed(string? raw, string expected) => Assert.Equal(expected, ParseKit.Speed(raw));

    [Theory]
    [InlineData("Full", "full")]
    [InlineData("full-duplex", "full")]
    [InlineData("yes", "full")]
    [InlineData("Half", "half")]
    [InlineData("no", "half")]
    [InlineData("a-full", "a-full")]
    [InlineData("Auto", "auto")]
    [InlineData("", "")]
    [InlineData("N/A", "n/a")]
    public void ParseKitDuplex(string raw, string expected) => Assert.Equal(expected, ParseKit.Duplex(raw));

    [Theory]
    [InlineData("Gi1/0/1-3", "Gi1/0/1|Gi1/0/2|Gi1/0/3")]
    [InlineData("Gi 1/3-4", "Gi 1/3|Gi 1/4")]
    [InlineData("Eth1/1/2-1/1/4", "Eth1/1/2|Eth1/1/3|Eth1/1/4")]
    [InlineData("Po1-3,", "Po1|Po2|Po3")]
    [InlineData("Gi1/0/5", "Gi1/0/5")]
    [InlineData("Eth1/1/2-1/2/4", "Eth1/1/2-1/2/4")]
    [InlineData("Gi1/0/9-2", "Gi1/0/9-2")]
    [InlineData("Po1-600", "Po1-600")]
    [InlineData("", "")]
    public void ParseKitExpandRange(string token, string expected) => Assert.Equal(expected, string.Join('|', ParseKit.ExpandRange(token)));

    [Fact]
    public void ParseKitMacsHuaweiLayout()
    {
        var macs = ParseKit.Macs(SwitchVendor.Huawei, """
            MAC address table of slot 0:
            -------------------------------------------------------------------------------
            MAC Address    VLAN/       PEVLAN CEVLAN Port            Type      LSP/LSR-ID
                           VSI/SI                                              MAC-Tunnel
            -------------------------------------------------------------------------------
            0011-2233-4455 10          -      -      GE0/0/1         dynamic   0/-
            aabb-ccdd-eeff 20          -      -      Eth-Trunk1      static    -
            -------------------------------------------------------------------------------
            Total matching items on slot 0 displayed = 2
            """);
        Assert.Equal([new MacEntry(10, "001122334455", "DYNAMIC", "GE0/0/1"), new MacEntry(20, "AABBCCDDEEFF", "STATIC", "Eth-Trunk1")], macs);
    }

    [Fact]
    public void ParseKitMacsSplitPortNames()
    {
        var macs = ParseKit.Macs(SwitchVendor.DellOs9, """
            VlanId     Mac Address           Type          Interface        State
             10        00:11:22:33:44:55     Dynamic       Gi 1/1           Active
            """);
        Assert.Equal([new MacEntry(10, "001122334455", "DYNAMIC", "Gi 1/1")], macs);
        Assert.Empty(ParseKit.Macs(SwitchVendor.Huawei, "Info: No MAC address entries"));
        Assert.Throws<FormatException>(() => ParseKit.Macs(SwitchVendor.Huawei, "Error: Wrong parameter found at '^' position."));
    }

    [Fact]
    public void ParseKitGenericCounters()
    {
        var counters = ParseKit.Counters("""
            Ethernet1/1 is up
            admin state is up, Dedicated Interface
              Hardware: 1000/10000 Ethernet, address: 0000.0000.0001 (bia 0000.0000.0001)
              MTU 1500 bytes, BW 10000000 Kbit, DLY 10 usec
              full-duplex, 10 Gb/s, media type is 10G
              RX
                5 input error  0 short frame  0 overrun   0 underrun  0 ignored
                2 CRC
              TX
                1 output error  3 collision  0 deferred  0 late collision
            """);
        Assert.Equal(new InterfaceCounters(2, 3, 5, "10000", "Full", "Actif", 1), counters);
        Assert.Equal("Inactif", ParseKit.Counters("Ethernet1/2 is down (Link not connected)").LinkState);
        Assert.Equal("Inactif", ParseKit.Counters("Vlan10 is up, line protocol is down").LinkState);
        Assert.Equal(new InterfaceCounters(null, null, null, "Inconnue", "Inconnu", "Inconnu", null), ParseKit.Counters(""));
    }

    [Fact]
    public void ParseKitVlanBrief()
    {
        var vlans = ParseKit.VlanBrief("""
            VLAN Name                             Status    Ports
            ---- -------------------------------- --------- -------------------------------
            1    default                          active    Eth1/3, Eth1/4, Eth1/5,
                                                            Eth1/6
            10   Bureaux                          active    Eth1/1
            20   VLAN0020                         suspended
            """);
        Assert.Equal(
        [
            new VlanInfo(1, "default", "active", "Eth1/3, Eth1/4, Eth1/5, Eth1/6"),
            new VlanInfo(10, "Bureaux", "active", "Eth1/1"),
            new VlanInfo(20, "VLAN0020", "suspended", "")
        ], vlans);
        Assert.Throws<FormatException>(() => ParseKit.VlanBrief(""));
    }

    [Fact]
    public void ParseKitFieldListings()
    {
        Assert.Equal("ES-24-Lite", ParseKit.DottedFields("Machine Model.......... ES-24-Lite  ")["machine model"]);
        var colon = ParseKit.ColonFields("  board-name: CRS326\nStatus: Connected (http://10.0.0.1:8080/inform)");
        Assert.Equal("CRS326", colon["board-name"]);
        Assert.Equal("Connected (http://10.0.0.1:8080/inform)", colon["status"]);
        Assert.Equal("Gi1/0/1", ParseKit.Port(SwitchVendor.DellOs6, "gi1/0/1"));
        Assert.Null(ParseKit.Port(SwitchVendor.Juniper, "me0"));
    }
}
