using SwitchPilot.Core;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Infrastructure.Drivers;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Tests;

/// <summary>Driver factory, platform detection and the shared write pipeline of the CLI drivers.</summary>
public class FactoryTests
{
    // A property: the safety context is only valid for 30 s after its observation time.
    private static SafetyContext Safe => new(true, true, new HashSet<string>());

    // ---- Factory --------------------------------------------------------------------------

    [Theory]
    [InlineData(SwitchVendor.Cisco, "CiscoIosDriver")]
    [InlineData(SwitchVendor.AlliedTelesis, "AlliedTelesisDriver")]
    [InlineData(SwitchVendor.AlliedS95, "AlliedS95Driver")]
    [InlineData(SwitchVendor.CiscoNxos, "CiscoLikeDriver")]
    [InlineData(SwitchVendor.Arista, "CiscoLikeDriver")]
    [InlineData(SwitchVendor.DellOs6, "CiscoLikeDriver")]
    [InlineData(SwitchVendor.DellOs9, "CiscoLikeDriver")]
    [InlineData(SwitchVendor.DellOs10, "CiscoLikeDriver")]
    [InlineData(SwitchVendor.Huawei, "HuaweiDriver")]
    [InlineData(SwitchVendor.Juniper, "JunosDriver")]
    [InlineData(SwitchVendor.MikroTik, "RouterOsDriver")]
    [InlineData(SwitchVendor.UbiquitiEdge, "EdgeSwitchDriver")]
    [InlineData(SwitchVendor.UniFi, "UniFiSshDriver")]
    public void CreateMapsEveryPlatform(SwitchVendor vendor, string type)
    {
        var session = new FakeSession();
        var driver = SwitchDriverFactory.Create(vendor, session, new Audit(), new Backup());
        Assert.Equal(type, driver.GetType().Name);
        Assert.Equal(vendor, driver.Vendor);
        Assert.False(driver.IsDemo);
        Assert.Empty(session.Commands);
    }

    [Fact] public void CreateRejectsUnknownPlatform() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SwitchDriverFactory.Create((SwitchVendor)99, new FakeSession(), new Audit(), null));

    [Fact] public void CiscoLikeDriverRefusesOtherFamilies() =>
        Assert.Throws<ArgumentException>(() => new CiscoLikeDriver(SwitchVendor.Huawei, new FakeSession(), new Audit()));

    // ---- Detection ------------------------------------------------------------------------

    [Theory]
    [InlineData("<HUAWEI>", "display version", "Huawei Versatile Routing Platform Software", SwitchVendor.Huawei)]
    [InlineData("[admin@MikroTik] >", "/system resource print", "  platform: MikroTik\n  board-name: CRS326-24G-2S+", SwitchVendor.MikroTik)]
    [InlineData("admin@ex2300>", "show version", "Model: ex2300-24p\nJunos: 21.4R3-S5.4", SwitchVendor.Juniper)]
    [InlineData("(UBNT EdgeSwitch) >", "show version", "System Description..... EdgeSwitch 24-Port Lite, 1.9.3", SwitchVendor.UbiquitiEdge)]
    [InlineData("SW#", "show version", "Arista DCS-7050TX-64-R", SwitchVendor.Arista)]
    [InlineData("SW#", "show version", "Cisco Nexus Operating System (NX-OS) Software", SwitchVendor.CiscoNxos)]
    [InlineData("SW#", "show version", "Cisco IOS Software, C2960 Software", SwitchVendor.Cisco)]
    public async Task DetectReadsTheIdentityCommandOfThePrompt(string prompt, string command, string output, SwitchVendor expected)
    {
        var session = new FakeSession(prompt) { Responses = { [command] = output } };
        Assert.Equal(expected, await SwitchDriverFactory.DetectAsync(session));
        Assert.Equal([command], session.Commands);
    }

    [Fact]
    public async Task DetectSkipsRejectedProbes()
    {
        var session = new FakeSession("SW#") { Rejected = { "show version", "show system" }, Responses = { ["display version"] = "VRP (R) software, Version 5.170" } };
        Assert.Equal(SwitchVendor.Huawei, await SwitchDriverFactory.DetectAsync(session));
        Assert.Equal(["show version", "show system", "display version"], session.Commands);
    }

    [Fact]
    public async Task DetectReturnsNullWhenNothingMatches()
    {
        var session = new FakeSession("SW#");
        Assert.Null(await SwitchDriverFactory.DetectAsync(session));
        Assert.Equal(["show version", "show system", "display version", "info"], session.Commands);
    }

    [Fact]
    public async Task DetectFallsBackOnAConclusivePrompt()
    {
        var session = new FakeSession("admin@ex2300>") { Rejected = { "show version" } };
        Assert.Equal(SwitchVendor.Juniper, await SwitchDriverFactory.DetectAsync(session));
    }

    [Fact]
    public async Task CreateDetectedAuditsThePlatform()
    {
        var audit = new Audit();
        var driver = await SwitchDriverFactory.CreateDetectedAsync(new FakeSession("SW#") { Responses = { ["show version"] = "Dell EMC Networking OS10 Enterprise" } }, SwitchVendor.Cisco, audit, null);
        Assert.IsType<CiscoLikeDriver>(driver);
        Assert.Equal(SwitchVendor.DellOs10, driver.Vendor);
        Assert.Equal(("Détection de la plateforme", SwitchPlatforms.Get(SwitchVendor.DellOs10).DisplayName), audit.Entries.Single());
    }

    [Fact]
    public async Task CreateDetectedUsesTheFallback()
    {
        var audit = new Audit();
        var driver = await SwitchDriverFactory.CreateDetectedAsync(new FakeSession("SW#"), SwitchVendor.AlliedTelesis, audit, null);
        Assert.Equal(SwitchVendor.AlliedTelesis, driver.Vendor);
        Assert.Equal(SwitchPlatforms.Get(SwitchVendor.AlliedTelesis).DisplayName, audit.Entries.Single().Result);
    }

    // ---- Huawei VRP -----------------------------------------------------------------------

    private static FakeSession Huawei(string prompt = "<HUAWEI>") => new(prompt)
    {
        Responses = { ["display current-configuration"] = "sysname HUAWEI\n#\nreturn", ["save"] = "Info: Save the configuration successfully." },
        After = (s, command) => s.Prompt = command switch
        {
            "system-view" => "[~HUAWEI]",
            "return" => "<HUAWEI>",
            "commit" => "[~HUAWEI-GigabitEthernet0/0/1]",
            _ when command.StartsWith("interface ") => "[~HUAWEI-GigabitEthernet0/0/1]",
            _ when command.StartsWith("description ") && s.TwoStage => "[*HUAWEI-GigabitEthernet0/0/1]",
            _ => s.Prompt
        }
    };

    [Fact]
    public async Task HuaweiCommitsBeforeReturnOnTwoStageFirmware()
    {
        var session = Huawei();
        session.TwoStage = true;
        var backup = new Backup();
        var audit = new Audit();
        await new HuaweiDriver(session, audit, backup).ApplyAsync(CommandPlan.Describe("GE0/0/1", "Uplink", SwitchVendor.Huawei), false, safety: Safe);
        Assert.Equal(["display current-configuration", "system-view", "interface GE0/0/1", "description Uplink", "commit", "return"], session.Commands);
        Assert.Equal(("SW", "sysname HUAWEI\n#\nreturn"), backup.Saved.Single());
        Assert.Equal("Commandes acceptées par VRP ; relecture de l'état nécessaire.", audit.Entries[^1].Result);
    }

    [Fact]
    public async Task HuaweiDoesNotCommitOnImmediateFirmware()
    {
        var session = Huawei();
        await new HuaweiDriver(session, new Audit(), new Backup()).ApplyAsync(CommandPlan.Describe("GE0/0/1", "Uplink", SwitchVendor.Huawei), false, safety: Safe);
        Assert.DoesNotContain("commit", session.Commands);
        Assert.Equal("return", session.Commands[^1]);
    }

    [Fact]
    public async Task HuaweiSaveAnswersTheConfirmation()
    {
        var session = Huawei();
        await new HuaweiDriver(session, new Audit(), new Backup()).SaveConfigAsync();
        Assert.Equal("save ⏎ y\n", session.Commands[^1]);
    }

    [Fact]
    public async Task HuaweiSaveWithoutAcknowledgementFails()
    {
        var session = Huawei();
        session.Responses["save"] = "Warning: The current configuration will be written to the device.";
        var error = await Assert.ThrowsAsync<CliException>(() => new HuaweiDriver(session, new Audit(), new Backup()).SaveConfigAsync());
        Assert.Contains("VRP n'a pas confirmé", error.Message);
        Assert.Equal("save ⏎ y\n", session.Commands[^1]);
    }

    [Fact]
    public async Task HuaweiRecoveryDiscardsTheCandidate()
    {
        var session = Huawei();
        session.TwoStage = true;
        session.Rejected.Add("description Uplink");
        var audit = new Audit();
        await Assert.ThrowsAsync<CliException>(() => new HuaweiDriver(session, audit, new Backup()).ApplyAsync(CommandPlan.Describe("GE0/0/1", "Uplink", SwitchVendor.Huawei), false, safety: Safe));
        Assert.Equal("return ⏎ n\n", session.Commands[^1]);
        Assert.Contains(audit.Entries, e => e.Result.StartsWith("Échec après 2/4 commandes"));
        Assert.False(session.Disposed);
    }

    // ---- Juniper Junos --------------------------------------------------------------------

    private static FakeSession Junos(string commit) => new("admin@ex2300>")
    {
        Responses = { ["show configuration | display set"] = "set system host-name ex2300", ["commit and-quit"] = commit, ["rollback 0"] = "load complete" },
        After = (s, command) => s.Prompt = command switch
        {
            "configure private" => "admin@ex2300#",
            "commit and-quit" when commit.Contains("commit complete") => "admin@ex2300>",
            _ => s.Prompt
        }
    };

    [Fact]
    public async Task JunosCommitsTheCandidate()
    {
        var session = Junos("configuration check succeeds\ncommit complete\nExiting configuration mode");
        var audit = new Audit();
        await new JunosDriver(session, audit, new Backup()).ApplyAsync(CommandPlan.Describe("ge-0/0/1", "Bureau", SwitchVendor.Juniper), false, safety: Safe);
        Assert.Equal(["show configuration | display set", "configure private", "set interfaces ge-0/0/1 description \"Bureau\"", "commit and-quit"], session.Commands);
        Assert.Equal("Commit Junos accepté ; relecture de l'état nécessaire.", audit.Entries[^1].Result);
    }

    [Fact]
    public async Task JunosFailedCommitRollsBackAndLeavesConfigurationMode()
    {
        var session = Junos("error: configuration check-out failed\nerror: commit failed: (statements constraint check failed)");
        var audit = new Audit();
        var error = await Assert.ThrowsAsync<CliException>(() => new JunosDriver(session, audit, new Backup()).ApplyAsync(CommandPlan.Describe("ge-0/0/1", "Bureau", SwitchVendor.Juniper), false, safety: Safe));
        Assert.Contains("Junos n'a pas confirmé le commit", error.Message);
        Assert.Equal(["rollback 0", "exit configuration-mode ⏎ yes\n"], session.Commands[^2..]);
        Assert.Contains(audit.Entries, e => e.Result.StartsWith("Échec après 3/3 commandes"));
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task JunosFailedRollbackDisconnects()
    {
        var session = Junos("error: commit failed");
        session.Rejected.Add("rollback 0");
        await Assert.ThrowsAsync<CliException>(() => new JunosDriver(session, new Audit(), new Backup()).ApplyAsync(CommandPlan.Describe("ge-0/0/1", "Bureau", SwitchVendor.Juniper), false, safety: Safe));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task JunosSaveIsImplicit()
    {
        var session = Junos("commit complete");
        var audit = new Audit();
        await new JunosDriver(session, audit, null).SaveConfigAsync();
        Assert.Empty(session.Commands);
        Assert.StartsWith("Sauvegarde implicite", audit.Entries.Single().Result);
    }

    // ---- Shared pipeline ------------------------------------------------------------------

    [Fact]
    public async Task DryRunSendsNothing()
    {
        var session = new FakeSession("SW#");
        var audit = new Audit();
        await SwitchDriverFactory.Create(SwitchVendor.Arista, session, audit, new Backup()).ApplyAsync(CommandPlan.Describe("Et1", "Uplink", SwitchVendor.Arista), true);
        Assert.Empty(session.Commands);
        Assert.Equal("Simulation : aucune commande envoyée.", audit.Entries.Single().Result);
    }

    [Fact]
    public async Task PlanOfAnotherFamilyIsRefused()
    {
        var session = new FakeSession("SW#");
        var driver = SwitchDriverFactory.Create(SwitchVendor.Arista, session, new Audit(), new Backup());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.Describe("Eth1/1", "Uplink", SwitchVendor.CiscoNxos), false, safety: Safe));
        Assert.Contains("Actualisez la vue", error.Message);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task WriteWithoutBackupIsRefused()
    {
        var session = Huawei();
        var audit = new Audit();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HuaweiDriver(session, audit, null).ApplyAsync(CommandPlan.Describe("GE0/0/1", "Uplink", SwitchVendor.Huawei), false, safety: Safe));
        Assert.Contains("sauvegarde chiffrée indisponible", error.Message);
        Assert.Empty(session.Commands);
        Assert.StartsWith("Échec après 0/4 commandes", audit.Entries.Single().Result);
    }

    [Fact]
    public async Task UniFiSshIsReadOnly()
    {
        var session = new FakeSession("USW-Bureau#") { Responses = { ["info"] = "Model: US-24-250W\nVersion: 6.6.65\nHostname: USW-Bureau", ["swctrl port show"] = MultiVendorParserTests.UniFiPorts } };
        var driver = SwitchDriverFactory.Create(SwitchVendor.UniFi, session, new Audit(), new Backup());
        Assert.Contains("API contrôleur UniFi", (await Assert.ThrowsAsync<NotSupportedException>(() => driver.ApplyAsync(CommandPlan.Describe("Port 1", "Uplink", SwitchVendor.UniFi), false, safety: Safe))).Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SaveConfigAsync());
        Assert.Empty(session.Commands);
        Assert.Equal(["Port 1", "Port 2", "Port 3"], (await driver.GetPortsAsync()).Select(p => p.Name));
    }

    [Fact]
    public async Task RouterOsSaveIsImplicit()
    {
        var audit = new Audit();
        var session = new FakeSession("[admin@MikroTik] >");
        await SwitchDriverFactory.Create(SwitchVendor.MikroTik, session, audit, new Backup()).SaveConfigAsync();
        Assert.Empty(session.Commands);
        Assert.StartsWith("Sauvegarde implicite", audit.Entries.Single().Result);
    }

    // ---- Vendor-neutral API ---------------------------------------------------------------

    [Fact]
    public async Task DetectDeviceAndLinkStatus()
    {
        var session = new FakeSession("eos#")
        {
            Responses =
            {
                ["show version"] = "Arista DCS-7050TX-64-R\nSoftware image version: 4.28.3M",
                ["show interfaces Ethernet1"] = "Ethernet1 is up, line protocol is up (connected)\n  Full-duplex, 10Gb/s, auto negotiation: off"
            }
        };
        var driver = SwitchDriverFactory.Create(SwitchVendor.Arista, session, new Audit(), null);
        Assert.Equal(new DeviceInfo(SwitchVendor.Arista, "Arista EOS", new SwitchIdentity("SW", "DCS-7050TX-64-R", "4.28.3M")), await driver.DetectDeviceAsync());
        Assert.Equal(new LinkStatus("Et1", true, "Actif", "10000", "Full"), await driver.GetLinkStatusAsync("Et1"));
        await Assert.ThrowsAsync<ArgumentException>(() => driver.GetLinkStatusAsync("Gi0/1; reload"));
        Assert.Equal(["show version", "show interfaces Ethernet1"], session.Commands);
    }

    private static FakeSession EdgeSwitch() => new("(UBNT EdgeSwitch) #")
    {
        Responses =
        {
            ["show version"] = "Machine Model.................................. ES-24-Lite\nSoftware Version............................... 1.9.3.5089558",
            ["show port all"] = MultiVendorParserTests.EdgePortAll,
            ["show running-config"] = MultiVendorParserTests.EdgeRunning,
            ["show vlan brief"] = "VLAN ID VLAN Name                         VLAN Type\n------- --------------------------------  ---------\n1       default                           Default\n10      Bureaux                           Static\n20      voice                             Static",
            ["write memory"] = "Configuration Saved!"
        },
        Rejected = { "show vlan port all" },
        After = (s, command) => s.Prompt = command switch
        {
            "configure" => "(UBNT EdgeSwitch) (Config)#",
            _ when command.StartsWith("interface ") => "(UBNT EdgeSwitch) (Interface 0/1)#",
            "exit" => s.Prompt.Contains("Interface") ? "(UBNT EdgeSwitch) (Config)#" : "(UBNT EdgeSwitch) #",
            _ => s.Prompt
        }
    };

    [Fact]
    public async Task SetPortVlanRemovesThePreviousAccessVlan()
    {
        var session = EdgeSwitch();
        var backup = new Backup();
        var driver = SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), backup);
        await driver.SetPortVlanAsync("0/1", 20, Safe);
        var write = session.Commands.SkipWhile(c => c != "configure").ToArray();
        Assert.Equal(["configure", "interface 0/1", "vlan pvid 20", "vlan participation include 20", "no vlan tagging 20",
            "vlan participation exclude 1", "vlan participation exclude 10", "exit", "exit"], write);
        Assert.Single(backup.Saved);
        Assert.Equal("(UBNT EdgeSwitch) #", session.Prompt);
    }

    [Fact]
    public async Task SetPortVlanDryRunReadsButDoesNotWrite()
    {
        var session = EdgeSwitch();
        var driver = SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), new Backup());
        await driver.SetPortVlanAsync("0/1", 20, Safe, dryRun: true);
        Assert.DoesNotContain("configure", session.Commands);
        Assert.Contains("show port all", session.Commands);
    }

    [Fact]
    public async Task SetPortVlanRefusesTheManagementPort()
    {
        var session = EdgeSwitch();
        var driver = SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), new Backup());
        var safety = new SafetyContext(true, true, new HashSet<string> { "0/1" });
        Assert.Contains("connexion du poste", (await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetPortVlanAsync("0/1", 20, safety))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetPortVlanAsync("0/2", 20, SafetyContext.Unknown));
        Assert.DoesNotContain("configure", session.Commands);
    }

    [Fact]
    public async Task SetPortVlanRefusesAMissingVlan()
    {
        var session = EdgeSwitch();
        var driver = SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), new Backup());
        Assert.Contains("n'existe plus", (await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetPortVlanAsync("0/1", 30, Safe))).Message);
        Assert.DoesNotContain("configure", session.Commands);
    }

    [Fact]
    public async Task EdgeSwitchRecoveryWalksBackToPrivilegedMode()
    {
        var session = EdgeSwitch();
        session.Rejected.Add("vlan pvid 20");
        var audit = new Audit();
        var driver = SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, audit, new Backup());
        await Assert.ThrowsAsync<CliException>(() => driver.SetPortVlanAsync("0/1", 20, Safe));
        Assert.Equal(["configure", "interface 0/1", "vlan pvid 20", "exit", "exit"], session.Commands.SkipWhile(c => c != "configure"));
        Assert.Equal("(UBNT EdgeSwitch) #", session.Prompt);
        Assert.Contains(audit.Entries, e => e.Result.StartsWith("Échec après 2/9 commandes"));
    }

    [Fact]
    public async Task EdgeSwitchSaveAnswersWithOneKey()
    {
        var session = EdgeSwitch();
        await SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), new Backup()).SaveConfigAsync();
        Assert.Equal("write memory ⏎ y", session.Commands[^1]);
    }

    [Fact]
    public async Task EdgeSwitchSnapshotSurvivesARejectedPvidTable()
    {
        var session = EdgeSwitch();
        var ports = await SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), null).GetPortsAsync();
        Assert.Equal(("10", "access"), (ports[0].Vlan, ports[0].Mode));
        Assert.Equal(("trunk", "trunk"), (ports[1].Vlan, ports[1].Mode));
        Assert.Equal(3, (await SwitchDriverFactory.Create(SwitchVendor.UbiquitiEdge, session, new Audit(), null).GetVlansAsync()).Count);
    }

    // ---- Fakes ----------------------------------------------------------------------------

    private sealed class FakeSession(string prompt = "SW#") : ICliSession
    {
        public string Prompt { get; set; } = prompt;
        public Dictionary<string, string> Responses { get; } = [];
        public HashSet<string> Rejected { get; } = [];
        public List<string> Commands { get; } = [];
        /// <summary>Prompt change after a command, as the device would print it.</summary>
        public Action<FakeSession, string>? After { get; init; }
        public bool TwoStage { get; set; }
        public bool Disposed { get; private set; }
        public bool IsConnected => !Disposed;
        public string Hostname => "SW";

        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Reply(command);
        }

        public Task<string> ExecuteConfirmedAsync(string command, string answer, CancellationToken cancellationToken = default)
        {
            Commands.Add($"{command} ⏎ {answer}");
            return Reply(command);
        }

        private Task<string> Reply(string command)
        {
            if (Rejected.Contains(command)) throw new CliException($"% Invalid input detected: {command}");
            After?.Invoke(this, command);
            return Task.FromResult(Responses.GetValueOrDefault(command, ""));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Audit : IAuditSink
    {
        public List<(string Action, string Result)> Entries { get; } = [];
        public void Write(string action, string result) => Entries.Add((action, result));
    }

    private sealed class Backup : IConfigurationBackup
    {
        public List<(string Host, string Configuration)> Saved { get; } = [];
        public Task SaveAsync(string hostname, string configuration, CancellationToken ct)
        {
            Saved.Add((hostname, configuration));
            return Task.CompletedTask;
        }
    }
}
