using SwitchPilot.Core;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Tests;

/// <summary>Detection, interface grammars, command dialects and the platform catalog.</summary>
public class PlatformTests
{
    // ---- DeviceDetector ------------------------------------------------------------------

    [Theory]
    [InlineData("[admin@MikroTik] >", SwitchVendor.MikroTik)]
    [InlineData("[admin@CRS326] /interface>", SwitchVendor.MikroTik)]
    [InlineData("admin@ex2300>", SwitchVendor.Juniper)]
    [InlineData("root@qfx5100#", SwitchVendor.Juniper)]
    [InlineData("<HUAWEI>", SwitchVendor.Huawei)]
    [InlineData("[~HUAWEI]", SwitchVendor.Huawei)]
    [InlineData("[*CE6800]", SwitchVendor.Huawei)]
    [InlineData("(UBNT EdgeSwitch) >", SwitchVendor.UbiquitiEdge)]
    [InlineData("(EdgeSwitch) (Config)#", SwitchVendor.UbiquitiEdge)]
    [InlineData("Last login: today\r\n<HUAWEI>", SwitchVendor.Huawei)]
    public void PromptShapeNamesThePlatform(string prompt, SwitchVendor expected) => Assert.Equal(expected, DeviceDetector.FromPrompt(prompt));

    [Theory][InlineData("Switch#")][InlineData("awplus>")][InlineData("")][InlineData(null)][InlineData("console>")]
    public void IosShapedPromptIsAmbiguous(string? prompt) => Assert.Null(DeviceDetector.FromPrompt(prompt));

    [Theory]
    [InlineData("Hostname: ex2300\nModel: ex2300-24t\nJunos: 20.4R3-S2.6", SwitchVendor.Juniper)]
    [InlineData("Huawei Versatile Routing Platform Software\nVRP (R) software, Version 5.170", SwitchVendor.Huawei)]
    [InlineData("platform: MikroTik\nboard-name: CRS326-24G-2S+", SwitchVendor.MikroTik)]
    [InlineData("Model:       USW-24-POE\nVersion:     6.6.55.15189", SwitchVendor.UniFi)]
    [InlineData("Machine Description....... EdgeSwitch 24-Port Lite", SwitchVendor.UbiquitiEdge)]
    [InlineData("Arista DCS-7050TX-64-R\nSoftware image version: 4.28.3M", SwitchVendor.Arista)]
    [InlineData("Dell SmartFabric OS10 Enterprise\nOS Version: 10.5.2.0", SwitchVendor.DellOs10)]
    [InlineData("Dell Real Time Operating System Software\nDell Application Software Version:  9.14(2.0)", SwitchVendor.DellOs9)]
    [InlineData("Machine Description....... Dell EMC Networking N1548P", SwitchVendor.DellOs6)]
    [InlineData("Unit  SW version    Boot version\n----\n 1    3.0.0.44      1.0.1.07", SwitchVendor.AlliedS95)]
    [InlineData("AlliedWare Plus (TM) 5.4.9 06/14/19", SwitchVendor.AlliedTelesis)]
    [InlineData("Cisco Nexus Operating System (NX-OS) Software", SwitchVendor.CiscoNxos)]
    [InlineData("Cisco IOS Software, C2960 Software (C2960-LANBASEK9-M), Version 15.0(2)SE11", SwitchVendor.Cisco)]
    public void BannerNamesThePlatform(string banner, SwitchVendor expected) => Assert.Equal(expected, DeviceDetector.FromBanner(banner));

    [Fact] public void UnknownBannerIsNull()
    {
        Assert.Null(DeviceDetector.FromBanner("% Invalid input detected at '^' marker."));
        Assert.Null(DeviceDetector.FromBanner(""));
    }

    [Fact] public void BannerWinsOverPrompt()
    {
        Assert.Equal(SwitchVendor.CiscoNxos, DeviceDetector.Detect("Switch#", "Cisco Nexus Operating System (NX-OS) Software"));
        Assert.Equal(SwitchVendor.Huawei, DeviceDetector.Detect("<HUAWEI>", "unrecognised"));
        Assert.Null(DeviceDetector.Detect("Switch#", "unrecognised"));
    }

    [Fact] public void ProbesFollowThePromptHint()
    {
        Assert.Equal(["display version"], DeviceDetector.Probes(SwitchVendor.Huawei));
        Assert.Equal(["/system resource print"], DeviceDetector.Probes(SwitchVendor.MikroTik));
        Assert.Equal(["show version"], DeviceDetector.Probes(SwitchVendor.Juniper));
        Assert.Equal(["show version"], DeviceDetector.Probes(SwitchVendor.UbiquitiEdge));
        Assert.Equal(["show version", "show system", "display version", "info"], DeviceDetector.Probes(null));
    }

    // ---- PortNames -----------------------------------------------------------------------

    [Theory]
    [InlineData(SwitchVendor.CiscoNxos, "ethernet1/1", "Eth1/1", "Ethernet1/1")]
    [InlineData(SwitchVendor.CiscoNxos, "Po10", "Po10", "port-channel10")]
    [InlineData(SwitchVendor.CiscoNxos, "mgmt0", "mgmt0", "mgmt0")]
    [InlineData(SwitchVendor.Arista, "Et1", "Et1", "Ethernet1")]
    [InlineData(SwitchVendor.Arista, "Ethernet49/1", "Et49/1", "Ethernet49/1")]
    [InlineData(SwitchVendor.Arista, "Port-Channel5", "Po5", "Port-Channel5")]
    [InlineData(SwitchVendor.Arista, "Ma1", "Ma1", "Management1")]
    [InlineData(SwitchVendor.DellOs6, "gigabitethernet1/0/1", "Gi1/0/1", "Gi1/0/1")]
    [InlineData(SwitchVendor.DellOs6, "Te1/0/2", "Te1/0/2", "Te1/0/2")]
    [InlineData(SwitchVendor.DellOs9, "Gi 1/1", "Gi 1/1", "GigabitEthernet 1/1")]
    [InlineData(SwitchVendor.DellOs9, "tengigabitethernet 1/49", "Te 1/49", "TenGigabitEthernet 1/49")]
    [InlineData(SwitchVendor.DellOs9, "Po 2", "Po 2", "Port-channel 2")]
    [InlineData(SwitchVendor.DellOs10, "Eth 1/1/1", "ethernet1/1/1", "ethernet1/1/1")]
    [InlineData(SwitchVendor.DellOs10, "ethernet1/1/49:1", "ethernet1/1/49:1", "ethernet1/1/49:1")]
    [InlineData(SwitchVendor.DellOs10, "po3", "port-channel3", "port-channel3")]
    [InlineData(SwitchVendor.Huawei, "ge0/0/1", "GE0/0/1", "GE0/0/1")]
    [InlineData(SwitchVendor.Huawei, "GigabitEthernet0/0/24", "GigabitEthernet0/0/24", "GigabitEthernet0/0/24")]
    [InlineData(SwitchVendor.Huawei, "Eth-Trunk1", "Eth-Trunk1", "Eth-Trunk1")]
    [InlineData(SwitchVendor.Huawei, "XGigabitEthernet0/0/1", "XGigabitEthernet0/0/1", "XGigabitEthernet0/0/1")]
    [InlineData(SwitchVendor.Juniper, "ge-0/0/1", "ge-0/0/1", "ge-0/0/1")]
    [InlineData(SwitchVendor.Juniper, "XE-0/1/0", "xe-0/1/0", "xe-0/1/0")]
    [InlineData(SwitchVendor.Juniper, "ae0", "ae0", "ae0")]
    [InlineData(SwitchVendor.MikroTik, "ether1", "ether1", "ether1")]
    [InlineData(SwitchVendor.MikroTik, "sfp-sfpplus1", "sfp-sfpplus1", "sfp-sfpplus1")]
    [InlineData(SwitchVendor.UbiquitiEdge, "0/1", "0/1", "0/1")]
    [InlineData(SwitchVendor.UbiquitiEdge, "3/1", "3/1", "3/1")]
    [InlineData(SwitchVendor.UniFi, "Port 7", "Port 7", "7")]
    [InlineData(SwitchVendor.UniFi, "port07", "Port 7", "7")]
    [InlineData(SwitchVendor.UniFi, "12", "Port 12", "12")]
    public void PortGrammarGivesDisplayAndCommandForms(SwitchVendor vendor, string typed, string display, string command)
    {
        Assert.Equal(display, PortNames.Validate(vendor, typed));
        Assert.Equal(command, PortNames.CommandForm(vendor, typed));
        Assert.True(PortNames.TryValidate(vendor, typed, out var port));
        Assert.Equal(display, port);
    }

    [Theory]
    [InlineData(SwitchVendor.Cisco, "Gi0/1")]
    [InlineData(SwitchVendor.AlliedTelesis, "port1.0.1")]
    public void LegacyVendorsKeepTheHistoricalGrammar(SwitchVendor vendor, string port) => Assert.Equal(port, PortNames.Validate(vendor, port));

    [Theory]
    [InlineData(SwitchVendor.Juniper, "ge-0/0/1.0")]
    [InlineData(SwitchVendor.Juniper, "Gi0/1")]
    [InlineData(SwitchVendor.MikroTik, "ether1;/system reset")]
    [InlineData(SwitchVendor.MikroTik, "1ether")]
    [InlineData(SwitchVendor.UbiquitiEdge, "Gi0/1")]
    [InlineData(SwitchVendor.UniFi, "Port 1;rm")]
    [InlineData(SwitchVendor.Huawei, "GE0/0/1 shutdown")]
    [InlineData(SwitchVendor.CiscoNxos, "Eth1/1\nreload")]
    public void PortOutsideTheGrammarIsRefused(SwitchVendor vendor, string port)
    {
        Assert.Throws<ArgumentException>(() => PortNames.Validate(vendor, port));
        Assert.False(PortNames.TryValidate(vendor, port, out var empty));
        Assert.Equal("", empty);
    }

    [Fact] public void GrammarErrorNamesThePlatform()
    {
        Assert.Equal("Interface invalide pour Juniper Junos (EX / QFX, ELS).", Assert.Throws<ArgumentException>(() => PortNames.Validate(SwitchVendor.Juniper, "Gi0/1")).Message);
        Assert.Equal("Interface invalide.", Assert.Throws<ArgumentException>(() => PortNames.Validate(SwitchVendor.Juniper, "")).Message);
        Assert.Equal("Interface invalide.", Assert.Throws<ArgumentException>(() => PortNames.Validate(SwitchVendor.MikroTik, new string('a', 49))).Message);
        Assert.Equal("Interface invalide.", Assert.Throws<ArgumentException>(() => PortNames.Validate(SwitchVendor.MikroTik, "ether\u00071")).Message);
    }

    [Theory]
    [InlineData("Gi0/1", "GigabitEthernet0/1")]
    [InlineData("GE0/0/1", "GigabitEthernet0/0/1")]
    [InlineData("Eth 1/1/1", "ethernet1/1/1")]
    [InlineData("Et1", "Ethernet1")]
    [InlineData("Po1", "port-channel1")]
    [InlineData("Te 1/49", "TenGigabitEthernet 1/49")]
    public void SpellingsOfOnePortAreTheSame(string a, string b) => Assert.True(PortNames.Same(a, b));

    [Fact] public void DifferentPortsAreNotTheSame()
    {
        Assert.False(PortNames.Same("Gi0/1", "Gi0/10"));
        Assert.False(PortNames.Same("ge-0/0/1", "xe-0/0/1"));
        Assert.False(PortNames.Same("", ""));
        Assert.False(PortNames.Same(null, "Gi0/1"));
    }

    [Theory][InlineData("ae0")][InlineData("Eth-Trunk1")][InlineData("Po1")][InlineData("port-channel10")][InlineData("Port-Channel5")][InlineData("bond1")]
    public void AggregatesAreRecognised(string port) => Assert.True(PortNames.IsAggregate(port));

    // Regression: "Port 7" (UniFi) used to match the "Po" prefix and was refused as an aggregate.
    [Theory][InlineData("Port 7")][InlineData("ge-0/0/1")][InlineData("Gi1/0/1")][InlineData("ether1")][InlineData("0/1")][InlineData("")]
    public void PhysicalPortsAreNotAggregates(string port) => Assert.False(PortNames.IsAggregate(port));

    [Fact] public void InventoryAcceptsEveryPlatformGrammar()
    {
        Assert.Equal("Gi1/0/1", PortNames.Inventory("Gi1/0/1"));
        Assert.Equal("ge-0/0/1", PortNames.Inventory("ge-0/0/1"));
        Assert.Equal("ether1", PortNames.Inventory("ether1"));
        Assert.Throws<ArgumentException>(() => PortNames.Inventory("not a port!"));
    }

    // ---- Dialects: exact command sequences ----------------------------------------------

    private static string[] Commands(CommandPlan plan) => plan.Commands.ToArray();

    [Fact] public void CiscoIosSequencesAreUnchanged()
    {
        Assert.Equal(["configure terminal", "interface Gi0/1", "switchport mode access", "switchport access vlan 10", "end"], Commands(CommandPlan.Access("Gi0/1", 10)));
        Assert.Equal(["configure terminal", "interface Gi0/1", "switchport trunk native vlan 10", "switchport trunk allowed vlan 10,20-30", "switchport mode trunk", "end"],
            Commands(CommandPlan.Trunk("Gi0/1", 10, "10, 20-30")));
        Assert.Equal(["configure terminal", "vlan 20", "name USERS", "end"], Commands(CommandPlan.CreateVlan(20, "USERS")));
        Assert.Equal(["configure terminal", "no vlan 20", "end"], Commands(CommandPlan.DeleteVlan(20)));
        var save = CommandPlan.Save().Steps.Single();
        Assert.Equal(("write memory", @"\[OK\]"), (save.Command, save.Expect));
        Assert.Equal(["end"], CommandPlan.Save().Recovery);
    }

    [Fact] public void AlliedWarePlusTrunkResetsTheAllowedList()
    {
        Assert.Equal(["configure terminal", "interface port1.0.1", "switchport mode trunk", "switchport trunk native vlan 10", "switchport trunk allowed vlan none", "switchport trunk allowed vlan add 10,20", "end"],
            Commands(CommandPlan.Trunk("port1.0.1", 10, "10,20", SwitchVendor.AlliedTelesis)));
        var save = CommandPlan.Save(SwitchVendor.AlliedTelesis).Steps.Single();
        Assert.Equal("write memory", save.Command);
        Assert.Null(save.Expect);
        Assert.Throws<ArgumentException>(() => CommandPlan.Describe("port1.0.1", new string('x', 81), SwitchVendor.AlliedTelesis));
    }

    [Fact] public void NxosNeedsExplicitSwitchportAndCopiesTheConfiguration()
    {
        Assert.Equal(["configure terminal", "interface Ethernet1/1", "switchport", "switchport mode access", "switchport access vlan 10", "end"],
            Commands(CommandPlan.Access("Eth1/1", 10, SwitchVendor.CiscoNxos)));
        Assert.Equal(["configure terminal", "interface Ethernet1/2", "switchport", "switchport mode trunk", "switchport trunk native vlan 1", "switchport trunk allowed vlan 1,10-12", "end"],
            Commands(CommandPlan.Trunk("Eth1/2", 1, "1,10-12", SwitchVendor.CiscoNxos)));
        var save = CommandPlan.Save(SwitchVendor.CiscoNxos).Steps.Single();
        Assert.Equal(("copy running-config startup-config", "Copy complete"), (save.Command, save.Expect));
    }

    [Fact] public void AristaUsesEthernetNamesAndConfirmsTheSave()
    {
        Assert.Equal(["configure terminal", "interface Ethernet1", "switchport mode access", "switchport access vlan 10", "end"],
            Commands(CommandPlan.Access("Et1", 10, SwitchVendor.Arista)));
        Assert.Equal(["configure terminal", "interface Ethernet1", "switchport mode trunk", "switchport trunk native vlan 10", "switchport trunk allowed vlan 10,20", "end"],
            Commands(CommandPlan.Trunk("Et1", 10, "10,20", SwitchVendor.Arista)));
        Assert.Equal("completed successfully", CommandPlan.Save(SwitchVendor.Arista).Steps.Single().Expect);
    }

    [Fact] public void DellOs6QuotesTextsAndAnswersTheSaveQuestion()
    {
        Assert.Equal(["configure", "interface Gi1/0/1", "description \"Bureau 12\"", "end"], Commands(CommandPlan.Describe("Gi1/0/1", "Bureau 12", SwitchVendor.DellOs6)));
        Assert.Equal(["configure", "interface Gi1/0/1", "description Imprimante", "end"], Commands(CommandPlan.Describe("Gi1/0/1", "Imprimante", SwitchVendor.DellOs6)));
        Assert.Equal(["configure", "vlan 30", "name VOIP", "end"], Commands(CommandPlan.CreateVlan(30, "VOIP", SwitchVendor.DellOs6)));
        var save = CommandPlan.Save(SwitchVendor.DellOs6).Steps.Single();
        Assert.Equal(("write memory", "y", "Saved"), (save.Command, save.Confirm, save.Expect));
        Assert.Equal("write memory   ← réponse « y »", save.ToString());
        Assert.Throws<ArgumentException>(() => CommandPlan.Describe("Gi1/0/1", "a\"b", SwitchVendor.DellOs6));
        Assert.Throws<ArgumentException>(() => CommandPlan.Describe("Gi1/0/1", new string('x', 65), SwitchVendor.DellOs6));
    }

    [Fact] public void DellOs9MovesTheUntaggedMembershipBetweenVlans()
    {
        Assert.Equal(["configure", "interface GigabitEthernet 1/1", "switchport", "exit", "interface vlan 20", "no untagged GigabitEthernet 1/1", "exit", "interface vlan 10", "untagged GigabitEthernet 1/1", "end"],
            Commands(CommandPlan.Access("Gi 1/1", 10, SwitchVendor.DellOs9, currentVlan: 20)));
        // Default VLAN 1 and an unchanged VLAN have nothing to leave.
        Assert.Equal(["configure", "interface GigabitEthernet 1/1", "switchport", "exit", "interface vlan 10", "untagged GigabitEthernet 1/1", "end"],
            Commands(CommandPlan.Access("Gi 1/1", 10, SwitchVendor.DellOs9, currentVlan: 1)));
        Assert.Equal(["configure", "interface vlan 10", "name SERVERS", "end"], Commands(CommandPlan.CreateVlan(10, "SERVERS", SwitchVendor.DellOs9)));
        Assert.Equal(["configure", "no interface vlan 10", "end"], Commands(CommandPlan.DeleteVlan(10, SwitchVendor.DellOs9)));
        Assert.StartsWith("Trunk non pris en charge sur Dell OS9", Assert.Throws<NotSupportedException>(() => CommandPlan.Trunk("Gi 1/1", 10, "10,20", SwitchVendor.DellOs9)).Message);
        Assert.Equal(20, CommandPlan.Access("Gi 1/1", 10, SwitchVendor.DellOs9, currentVlan: 20).CurrentVlan);
    }

    [Fact] public void DellOs10TrunkKeepsNativeAsAccessVlan()
    {
        var plan = CommandPlan.Trunk("ethernet1/1/1", 10, "10,20-22", SwitchVendor.DellOs10);
        Assert.Equal(["configure terminal", "interface ethernet1/1/1", "switchport mode trunk", "switchport access vlan 10", "switchport trunk allowed vlan 20-22", "end"], Commands(plan));
        Assert.Equal("Configurer ethernet1/1/1 en trunk (VLAN étiquetés ajoutés ; les autres VLAN déjà autorisés sont conservés)", plan.Title);
        Assert.Equal(["configure terminal", "interface ethernet1/1/1", "switchport mode trunk", "switchport access vlan 10", "end"],
            Commands(CommandPlan.Trunk("ethernet1/1/1", 10, "10", SwitchVendor.DellOs10)));
        Assert.Equal(["configure terminal", "interface vlan 30", "description VOIP", "end"], Commands(CommandPlan.CreateVlan(30, "VOIP", SwitchVendor.DellOs10)));
        Assert.Equal(["configure terminal", "no interface vlan 30", "end"], Commands(CommandPlan.DeleteVlan(30, SwitchVendor.DellOs10)));
        Assert.Equal("show running-configuration", ConfigDialects.For(SwitchVendor.DellOs10).ExportCommand);
    }

    [Fact] public void HuaweiUsesSystemViewAndPortLinkType()
    {
        Assert.Equal(["system-view", "interface GE0/0/1", "port link-type access", "port default vlan 10", "return"],
            Commands(CommandPlan.Access("GE0/0/1", 10, SwitchVendor.Huawei)));
        Assert.Equal(["system-view", "interface GE0/0/1", "port link-type trunk", "undo port trunk allow-pass vlan all", "port trunk allow-pass vlan 10 20 to 30", "port trunk pvid vlan 10", "return"],
            Commands(CommandPlan.Trunk("GE0/0/1", 10, "10,20-30", SwitchVendor.Huawei)));
        Assert.Equal(["system-view", "interface GE0/0/1", "undo shutdown", "return"], Commands(CommandPlan.Enabled("GE0/0/1", true, SwitchVendor.Huawei)));
        Assert.Equal(["system-view", "interface GE0/0/1", "undo description", "return"], Commands(CommandPlan.Describe("GE0/0/1", "", SwitchVendor.Huawei)));
        Assert.Equal(["system-view", "vlan 10", "description SERVERS", "return"], Commands(CommandPlan.CreateVlan(10, "SERVERS", SwitchVendor.Huawei)));
        Assert.Equal(["system-view", "undo vlan 10", "return"], Commands(CommandPlan.DeleteVlan(10, SwitchVendor.Huawei)));
        var save = CommandPlan.Save(SwitchVendor.Huawei).Steps.Single();
        Assert.Equal(("save", "y\n", "(?i)successfully"), (save.Command, save.Confirm, save.Expect));
        Assert.Equal("save   ← réponse « y »", save.ToString());
        Assert.Equal(["return"], CommandPlan.Save(SwitchVendor.Huawei).Recovery);
    }

    [Fact] public void HuaweiSplitsLongAllowPassLists()
    {
        var plan = CommandPlan.Trunk("GE0/0/1", 2, "2,4,6,8,10,12,14,16,18,20,22,24", SwitchVendor.Huawei);
        Assert.Contains("port trunk allow-pass vlan 2 4 6 8 10 12 14 16 18 20", plan.Commands);
        Assert.Contains("port trunk allow-pass vlan 22 24", plan.Commands);
    }

    [Fact] public void JunosCommitsAPrivateCandidate()
    {
        var access = CommandPlan.Access("ge-0/0/1", 10, SwitchVendor.Juniper);
        Assert.Equal([
            "configure private",
            "delete interfaces ge-0/0/1 unit 0 family ethernet-switching vlan members",
            "delete interfaces ge-0/0/1 native-vlan-id",
            "set interfaces ge-0/0/1 unit 0 family ethernet-switching interface-mode access",
            "set interfaces ge-0/0/1 unit 0 family ethernet-switching vlan members 10",
            "commit and-quit"], Commands(access));
        Assert.Equal("commit complete", access.Steps[^1].Expect);
        Assert.Equal(["rollback 0", "exit configuration-mode"], access.Recovery);
        Assert.Equal([
            "configure private",
            "delete interfaces ge-0/0/2 unit 0 family ethernet-switching vlan members",
            "set interfaces ge-0/0/2 unit 0 family ethernet-switching interface-mode trunk",
            "set interfaces ge-0/0/2 unit 0 family ethernet-switching vlan members [ 10 20-30 ]",
            "set interfaces ge-0/0/2 native-vlan-id 10",
            "commit and-quit"], Commands(CommandPlan.Trunk("ge-0/0/2", 10, "10,20-30", SwitchVendor.Juniper)));
        Assert.Contains("set interfaces ge-0/0/1 disable", CommandPlan.Enabled("ge-0/0/1", false, SwitchVendor.Juniper).Commands);
        Assert.Contains("delete interfaces ge-0/0/1 disable", CommandPlan.Enabled("ge-0/0/1", true, SwitchVendor.Juniper).Commands);
        Assert.Contains("set interfaces ge-0/0/1 description \"Bureau 12\"", CommandPlan.Describe("ge-0/0/1", "Bureau 12", SwitchVendor.Juniper).Commands);
        Assert.Contains("set vlans SERVERS vlan-id 10", CommandPlan.CreateVlan(10, "SERVERS", SwitchVendor.Juniper).Commands);
        Assert.Contains("delete vlans SERVERS", CommandPlan.DeleteVlan(10, SwitchVendor.Juniper, "SERVERS").Commands);
        Assert.Empty(CommandPlan.Save(SwitchVendor.Juniper).Steps);
    }

    [Fact] public void JunosRulesOnVlanNamesAndTexts()
    {
        Assert.StartsWith("Junos identifie un VLAN par son nom", Assert.Throws<ArgumentException>(() => CommandPlan.DeleteVlan(10, SwitchVendor.Juniper)).Message);
        Assert.StartsWith("Nom de VLAN Junos", Assert.Throws<ArgumentException>(() => CommandPlan.CreateVlan(10, "10-servers", SwitchVendor.Juniper)).Message);
        Assert.Throws<ArgumentException>(() => CommandPlan.Describe("ge-0/0/1", "a;b", SwitchVendor.Juniper));
        Assert.Throws<ArgumentException>(() => CommandPlan.Describe("ge-0/0/1", "a\\b", SwitchVendor.Juniper));
        Assert.Equal("show configuration | display set", ConfigDialects.For(SwitchVendor.Juniper).ExportCommand);
    }

    [Fact] public void RouterOsWritesBridgeSettings()
    {
        Assert.Equal(["/interface bridge port set [find interface=ether1] pvid=10 frame-types=admit-only-untagged-and-priority-tagged"],
            Commands(CommandPlan.Access("ether1", 10, SwitchVendor.MikroTik)));
        Assert.Equal(["/interface disable [find name=ether1]"], Commands(CommandPlan.Enabled("ether1", false, SwitchVendor.MikroTik)));
        Assert.Equal(["/interface set [find name=ether1] comment=\"Bureau 12\""], Commands(CommandPlan.Describe("ether1", "Bureau 12", SwitchVendor.MikroTik)));
        Assert.Equal(["/interface bridge vlan add bridge=[/interface bridge find] vlan-ids=10 comment=\"SERVERS\""], Commands(CommandPlan.CreateVlan(10, "SERVERS", SwitchVendor.MikroTik)));
        Assert.Equal(["/interface bridge vlan remove [find vlan-ids=10]"], Commands(CommandPlan.DeleteVlan(10, SwitchVendor.MikroTik)));
        Assert.Empty(CommandPlan.Save(SwitchVendor.MikroTik).Steps);
        Assert.Throws<NotSupportedException>(() => CommandPlan.Trunk("ether1", 10, "10,20", SwitchVendor.MikroTik));
        foreach (var text in new[] { "a$b", "a[b", "a]b", "a{b", "a}b", "a\"b", "a\\b", "a;b" })
            Assert.Throws<ArgumentException>(() => CommandPlan.Describe("ether1", text, SwitchVendor.MikroTik));
    }

    [Fact] public void EdgeSwitchManagesParticipationAndTagging()
    {
        Assert.Equal(["configure", "interface 0/1", "vlan pvid 10", "vlan participation include 10", "no vlan tagging 10", "vlan participation exclude 1", "vlan participation exclude 20", "exit", "exit"],
            Commands(CommandPlan.Access("0/1", 10, SwitchVendor.UbiquitiEdge, currentVlan: 20)));
        Assert.Equal(["configure", "interface 0/1", "vlan pvid 1", "vlan participation include 1", "no vlan tagging 1", "exit", "exit"],
            Commands(CommandPlan.Access("0/1", 1, SwitchVendor.UbiquitiEdge, currentVlan: 1)));
        var trunk = CommandPlan.Trunk("0/2", 10, "10,20-22", SwitchVendor.UbiquitiEdge);
        Assert.Equal(["configure", "interface 0/2", "vlan pvid 10", "vlan participation include 10,20-22", "no vlan tagging 10", "vlan tagging 20-22", "vlan participation exclude 1", "exit", "exit"], Commands(trunk));
        Assert.Equal("Configurer 0/2 en trunk (les autres VLAN déjà inclus restent membres)", trunk.Title);
        Assert.Equal(["vlan database", "vlan 30", "vlan name 30 VOIP", "exit"], Commands(CommandPlan.CreateVlan(30, "VOIP", SwitchVendor.UbiquitiEdge)));
        Assert.Equal(["vlan database", "no vlan 30", "exit"], Commands(CommandPlan.DeleteVlan(30, SwitchVendor.UbiquitiEdge)));
        Assert.Equal(["configure", "interface 0/1", "description \"Bureau 12\"", "exit", "exit"], Commands(CommandPlan.Describe("0/1", "Bureau 12", SwitchVendor.UbiquitiEdge)));
        var save = CommandPlan.Save(SwitchVendor.UbiquitiEdge).Steps.Single();
        Assert.Equal(("write memory", "y", "(?i)saved|success"), (save.Command, save.Confirm, save.Expect));
    }

    [Fact] public void UniFiStepsDescribeTheControllerRequest()
    {
        Assert.Equal(["API contrôleur · port_overrides[Port 7] : réseau natif = VLAN 10, VLAN étiquetés = aucun (block_all)"], Commands(CommandPlan.Access("Port 7", 10, SwitchVendor.UniFi)));
        Assert.Equal(["API contrôleur · port_overrides[Port 7] : réseau natif = VLAN 10, VLAN étiquetés = 20-22 (custom)"], Commands(CommandPlan.Trunk("7", 10, "10,20-22", SwitchVendor.UniFi)));
        Assert.Equal(["API contrôleur · port_overrides[Port 7] : nom = « Bureau »"], Commands(CommandPlan.Describe("Port 7", "Bureau", SwitchVendor.UniFi)));
        Assert.Equal(["API contrôleur · port_overrides[Port 7] : nom effacé"], Commands(CommandPlan.Describe("Port 7", "", SwitchVendor.UniFi)));
        Assert.Equal(["API contrôleur · POST rest/networkconf : réseau « VOIP », VLAN 30 (vlan-only)"], Commands(CommandPlan.CreateVlan(30, "VOIP", SwitchVendor.UniFi)));
        Assert.StartsWith("UniFi : l'activation", Assert.Throws<NotSupportedException>(() => CommandPlan.Enabled("Port 7", false, SwitchVendor.UniFi)).Message);
        var save = CommandPlan.Save(SwitchVendor.UniFi);
        Assert.Empty(save.Steps);
        Assert.StartsWith("Aucune commande", save.Preview);
    }

    [Fact] public void AtS95IsReadOnly()
    {
        var reason = Assert.Throws<NotSupportedException>(() => CommandPlan.Access("1/g1", 10, SwitchVendor.AlliedS95)).Message;
        Assert.StartsWith("Les modifications ne sont pas encore prises en charge sur la famille AT-S95", reason);
        Assert.Throws<NotSupportedException>(() => CommandPlan.Save(SwitchVendor.AlliedS95));
        Assert.Throws<NotSupportedException>(() => CommandPlan.CreateVlan(10, "X", SwitchVendor.AlliedS95));
    }

    [Theory]
    [InlineData(SwitchVendor.Cisco, "ios")][InlineData(SwitchVendor.AlliedTelesis, "ios")][InlineData(SwitchVendor.AlliedS95, "s95")]
    [InlineData(SwitchVendor.CiscoNxos, "nxos")][InlineData(SwitchVendor.Arista, "eos")][InlineData(SwitchVendor.DellOs6, "dell-os6")]
    [InlineData(SwitchVendor.DellOs9, "dell-os9")][InlineData(SwitchVendor.DellOs10, "dell-os10")][InlineData(SwitchVendor.Huawei, "vrp")]
    [InlineData(SwitchVendor.Juniper, "junos")][InlineData(SwitchVendor.MikroTik, "routeros")][InlineData(SwitchVendor.UbiquitiEdge, "edgeswitch")]
    [InlineData(SwitchVendor.UniFi, "unifi")]
    public void EveryPlatformHasAFamily(SwitchVendor vendor, string family) => Assert.Equal(family, ConfigDialects.For(vendor).Family);

    [Fact] public void PlanValidationIsSharedByEveryDialect()
    {
        foreach (var vendor in new[] { SwitchVendor.Huawei, SwitchVendor.Juniper, SwitchVendor.UbiquitiEdge })
        {
            Assert.Equal("VLAN attendu : 1–4094, hors VLAN réservés 1002–1005.", Assert.Throws<ArgumentException>(() => CommandPlan.CreateVlan(4095, "X", vendor)).Message);
            Assert.Equal("Le VLAN 1 ne peut pas être supprimé.", Assert.Throws<ArgumentException>(() => CommandPlan.DeleteVlan(1, vendor, "default")).Message);
            Assert.Throws<ArgumentException>(() => CommandPlan.CreateVlan(10, "two words", vendor));
            Assert.Throws<ArgumentException>(() => CommandPlan.CreateVlan(10, "", vendor));
        }
        Assert.Equal("Le VLAN natif doit figurer dans les VLAN autorisés.", Assert.Throws<ArgumentException>(() => CommandPlan.Trunk("GE0/0/1", 10, "20,30", SwitchVendor.Huawei)).Message);
        Assert.Equal("Liste VLAN attendue : 10,20,30-40.", Assert.Throws<ArgumentException>(() => CommandPlan.Trunk("0/1", 10, "10,x", SwitchVendor.UbiquitiEdge)).Message);
    }

    [Fact] public void RangeHelpersCompressVlanLists()
    {
        Assert.Equal([(1, 3), (7, 7), (9, 10)], ConfigDialect.Ranges([3, 1, 2, 7, 10, 9, 9]));
        Assert.Equal("1-3,7,9-10", ConfigDialect.RangeList([1, 2, 3, 7, 9, 10]));
        Assert.Equal("1 to 3 7", ConfigDialect.RangeList([1, 2, 3, 7], " ", " to "));
    }

    [Fact] public void UnknownVendorIsRefused()
    {
        Assert.Equal("Constructeur de switch inconnu.", Assert.Throws<ArgumentException>(() => ConfigDialects.For((SwitchVendor)99)).Message);
        Assert.Equal("Constructeur de switch inconnu.", Assert.Throws<ArgumentException>(() => SwitchPlatforms.Get((SwitchVendor)99)).Message);
    }

    // ---- Catalog -------------------------------------------------------------------------

    [Fact] public void CatalogListsEveryVendorInEnumOrder()
    {
        Assert.Equal(Enum.GetValues<SwitchVendor>(), SwitchPlatforms.All.Select(p => p.Vendor));
        Assert.Equal(["IOS", "AW+", "AT-S95", "NX-OS", "EOS", "OS6", "OS9", "OS10", "VRP", "Junos", "RouterOS", "EdgeSwitch", "UniFi"], SwitchPlatforms.All.Select(p => p.ShortName));
        Assert.All(SwitchPlatforms.All, p => Assert.Same(ConfigDialects.For(p.Vendor), p.Dialect));
    }

    [Fact] public void CatalogDescribesMaturityAndPersistence()
    {
        Assert.Equal([SwitchVendor.Cisco, SwitchVendor.AlliedTelesis, SwitchVendor.AlliedS95], SwitchPlatforms.All.Where(p => !p.IsExperimental).Select(p => p.Vendor));
        Assert.Equal([SwitchVendor.Cisco], SwitchPlatforms.All.Where(p => p.Tdr).Select(p => p.Vendor));
        Assert.Equal([SwitchVendor.AlliedS95], SwitchPlatforms.All.Where(p => !p.CanWrite).Select(p => p.Vendor));
        Assert.Equal(SaveModel.Commit, SwitchPlatforms.Get(SwitchVendor.Juniper).Save);
        Assert.Equal(SaveModel.Implicit, SwitchPlatforms.Get(SwitchVendor.MikroTik).Save);
        Assert.Equal(SaveModel.Implicit, SwitchPlatforms.Get(SwitchVendor.UniFi).Save);
        Assert.Equal(SaveModel.Explicit, SwitchPlatforms.Get(SwitchVendor.Huawei).Save);
        Assert.Equal("Ubiquiti UniFi (contrôleur ou SSH)", SwitchPlatforms.Get(SwitchVendor.UniFi).ToString());
    }

    [Fact] public void EnumValuesArePersistedAsIntegers()
    {
        // Saved profiles store the integer: new platforms are appended, never inserted.
        Assert.Equal(0, (int)SwitchVendor.Cisco);
        Assert.Equal(1, (int)SwitchVendor.AlliedTelesis);
        Assert.Equal(2, (int)SwitchVendor.AlliedS95);
        Assert.Equal(12, (int)SwitchVendor.UniFi);
        Assert.Equal(2, (int)ConnectionKind.Api);
    }
}
