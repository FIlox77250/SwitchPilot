using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Ssh;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Tests;

public class DriverTests
{
    private static SafetyContext Safe => new(true, true, new HashSet<string> { "Fa0/14" });
    private const string ValidTdr = "TDR test last run on: September 30 10:01:00\nGi0/2 auto Pair A 18 +/- 2 meters Pair B Normal\nPair B 18 +/- 2 meters Pair A Normal\nPair C 18 +/- 2 meters Pair D Normal\nPair D 18 +/- 2 meters Pair C Normal";
    private const string OldTdr = "TDR test last run on: September 30 10:00:00\nGi0/2 auto Pair A 18 +/- 2 meters Pair B Normal";

    [Fact] public async Task PollingUsesThreeCommandsAndFreshPortMode()
    {
        var s = new Session(); await using var driver = Driver(s);
        await driver.ReadSnapshotAsync(); s.Commands.Clear();
        var result = await driver.ReadDetectionAsync("00:11:22:33:44:55");
        Assert.Equal(3, s.Commands.Count);
        Assert.DoesNotContain("show version", s.Commands); Assert.DoesNotContain("show vlan brief", s.Commands);
        Assert.Contains("show mac address-table address 0011.2233.4455", s.Commands);
        Assert.Equal("access", Assert.Single(result.Snapshot.Ports).Mode);
    }
    [Fact] public async Task MacAbsentSkipsInterfaceInventory()
    {
        var s = new Session { MacOutput = "Mac Address Table\nTotal Mac Addresses: 0" }; await using var driver = Driver(s);
        await driver.ReadSnapshotAsync(); s.Commands.Clear();
        var result = await driver.ReadDetectionAsync("001122334455");
        Assert.Empty(result.Entries); Assert.Single(s.Commands);
    }
    [Fact] public async Task UnsupportedFilteredQueryIsRemembered()
    {
        var s = new Session { RejectFiltered = true }; await using var driver = Driver(s);
        await driver.ReadDetectionAsync("001122334455"); s.Commands.Clear();
        await driver.ReadDetectionAsync("001122334455");
        Assert.DoesNotContain(s.Commands, c => c.Contains(" address 0011"));
    }
    [Fact] public async Task AuthorizationErrorDoesNotRetryAlternativeSyntax()
    {
        var s = new Session { RejectAuthorization = true }; await using var driver = Driver(s);
        await Assert.ThrowsAsync<CliException>(() => driver.ReadMacTableAsync());
        Assert.Single(s.Commands);
    }
    [Fact] public async Task RejectedModeReadCannotReuseCachedAccessClaim()
    {
        var s = new Session(); await using var driver = Driver(s);
        await driver.ReadSnapshotAsync(); s.RejectPortMode = true;
        var result = await driver.ReadDetectionAsync("001122334455");
        Assert.Equal("Inconnu", Assert.Single(result.Snapshot.Ports).Mode);
    }
    [Fact] public async Task OwnPortNeverReceivesTdrLaunch()
    {
        var s = new Session(); await using var driver = Driver(s);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunTdrAsync("Fa0/14", Safe));
        Assert.DoesNotContain(s.Commands, c => c.StartsWith("test "));
    }
    [Fact] public async Task UnsupportedProbeNeverLaunchesTdr()
    {
        var s = new Session { RejectProbe = true }; await using var driver = Driver(s);
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.RunTdrAsync("Gi0/2", Safe));
        Assert.DoesNotContain(s.Commands, c => c.StartsWith("test "));
    }
    [Fact] public async Task AuthorizationRefusalIsNotUnsupported()
    {
        var s = new Session { RejectProbeAuthorization = true }; await using var driver = Driver(s);
        var error = await Assert.ThrowsAsync<CliException>(() => driver.RunTdrAsync("Gi0/2", Safe));
        Assert.Equal(CliFailure.Authorization, error.Failure); Assert.DoesNotContain(s.Commands, c => c.StartsWith("test "));
    }
    [Fact] public async Task OldTdrResultIsNeverAccepted()
    {
        var s = new Session { TdrResponses = new([OldTdr]) }; await using var driver = Driver(s);
        await Assert.ThrowsAsync<TimeoutException>(() => driver.RunTdrAsync("Gi0/2", Safe));
    }
    [Fact] public async Task NewTimestampAndCompletedPairsAreAccepted()
    {
        var s = new Session { TdrResponses = new([OldTdr, ValidTdr]) }; await using var driver = Driver(s);
        var result = await driver.RunTdrAsync("Gi0/2", Safe);
        Assert.Equal(4, result.Pairs.Count); Assert.All(result.Pairs, p => Assert.Equal("OK", p.Status));
    }
    [Fact] public async Task InProgressWithNewTimestampMustFinish()
    {
        var pending = ValidTdr.Replace("Normal", "inprogress");
        var s = new Session { TdrResponses = new([OldTdr, pending, ValidTdr]) }; await using var driver = Driver(s);
        var result = await driver.RunTdrAsync("Gi0/2", Safe);
        Assert.All(result.Pairs, p => Assert.Equal("OK", p.Status));
        Assert.Equal(3, s.Commands.Count(c => c.StartsWith("show cable")));
    }
    [Fact] public async Task UnacknowledgedTdrLaunchCannotReadOldSuccess()
    {
        var s = new Session { StartAcknowledgement = "No test performed" }; await using var driver = Driver(s);
        await Assert.ThrowsAsync<CliException>(() => driver.RunTdrAsync("Gi0/2", Safe));
        Assert.Single(s.Commands, c => c.StartsWith("show cable"));
    }
    [Fact] public async Task ActiveVlanCannotBeDeleted()
    {
        var s = new Session(); await using var driver = Driver(s);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.DeleteVlan(10), false));
        Assert.DoesNotContain("configure terminal", s.Commands);
    }
    [Fact] public async Task MissingVlanCannotBeAssigned()
    {
        var s = new Session(); await using var driver = Driver(s);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.Access("Fa0/1", 999), false));
        Assert.DoesNotContain("configure terminal", s.Commands);
    }
    private static CiscoIosDriver Driver(Session s) => new(s, new SafetyTests.TestAudit(), TimeSpan.Zero, new SafetyTests.TestBackup());
    private sealed class Session : ICliSession
    {
        public List<string> Commands { get; } = [];
        public bool RejectFiltered, RejectAuthorization, RejectPortMode, RejectProbe, RejectProbeAuthorization;
        public string MacOutput = ParserTests.Fixture("mac-table.txt");
        public string StartAcknowledgement = "TDR test started on interface Gi0/2";
        public Queue<string> TdrResponses = new([OldTdr]);
        public bool IsConnected => true;
        public string Hostname => "SW";
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.StartsWith("show mac"))
            {
                if (RejectAuthorization) throw new CliException("Denied", CliFailure.Authorization);
                if (RejectFiltered && command.Contains(" address 0011")) throw new CliException("Invalid", CliFailure.Unsupported);
                return Task.FromResult(MacOutput);
            }
            if (command.StartsWith("show cable"))
            {
                if (RejectProbeAuthorization) throw new CliException("Denied", CliFailure.Authorization);
                if (RejectProbe) throw new CliException("Invalid", CliFailure.Unsupported);
                return Task.FromResult(TdrResponses.Count > 1 ? TdrResponses.Dequeue() : TdrResponses.Peek());
            }
            if (command == "show interfaces Fa0/14 switchport" && RejectPortMode) throw new CliException("Denied", CliFailure.Authorization);
            return Task.FromResult(command switch
            {
                "show version" => "Cisco IOS Version 15.2(4)E\nModel number : WS-C2960+24TC-L",
                "show interfaces status" => ParserTests.Fixture("interfaces-status.txt"),
                "show vlan brief" => ParserTests.Fixture("vlans.txt"),
                _ when command.EndsWith("switchport") => "Name: Fa0/14\nOperational Mode: static access\nName: Gi0/2\nOperational Mode: static access",
                _ when command.StartsWith("test cable") => StartAcknowledgement,
                _ => ""
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
