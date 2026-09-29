using SwitchPilot.Core;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Infrastructure.Cisco;

namespace SwitchPilot.Tests;

public class SafetyTests
{
    private readonly PortInfo port = new("Fa0/14", "", "connected", "10", "full", "100", "10/100BaseTX", "access");
    [Fact] public void TdrBlocksOurPort() => Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port, new(true, true, new HashSet<string> { "Fa0/14" })));
    [Fact] public void TdrBlocksUnknownPath() => Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port, SafetyContext.Unknown));
    [Fact] public void TdrBlocksStaleEvidence() => Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port, new(false, true, new HashSet<string>())));
    [Fact] public void ExpiredEvidenceIsRejectedEvenWhenFlaggedFresh() => Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port, new(true, true, new HashSet<string>()) { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
    [Fact] public void LongInterfaceNameCannotBypassOwnPortGuard() => Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port, new(true, true, new HashSet<string> { "fastethernet0/14" })));
    [Fact] public void TdrBlocksTrunk() => Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port with { Mode = "trunk" }, new(true, true, new HashSet<string>())));
    [Fact] public void TdrBlocksFiber() => Assert.Throws<NotSupportedException>(() => SafetyPolicy.RequireSafeTdr(port with { Media = "1000BaseSX SFP" }, new(true, true, new HashSet<string>())));
    [Theory][InlineData("Fa0/1\nreload")][InlineData("Fa0/1;reload")][InlineData("Fa0/1\rshutdown")]
    public void RejectInterfaceInjection(string text) => Assert.Throws<ArgumentException>(() => CommandPlan.Enabled(text, false));
    [Theory][InlineData("line\nshutdown")][InlineData("line\rshutdown")][InlineData("line\u001b")][InlineData("help?")]
    public void RejectDescriptionControlCharacters(string text) => Assert.Throws<ArgumentException>(() => CommandPlan.Describe("Fa0/1", text));
    [Theory][InlineData(0)][InlineData(4095)][InlineData(1002)][InlineData(1005)]
    public void RejectReservedVlans(int id) => Assert.Throws<ArgumentException>(() => CommandPlan.Access("Fa0/1", id));
    [Fact] public void DefaultVlanCannotBeDeleted() => Assert.Throws<ArgumentException>(() => CommandPlan.DeleteVlan(1));
    [Fact] public void ConnectionProfileStringNeverContainsSecrets()
    {
        var profile = new ConnectionProfile("switch", 22, "admin", true, "secret-login", "secret-enable");
        Assert.Equal("switch:22", profile.ToString());
    }
    [Fact] public void TrunkNativeMustBeAllowed() => Assert.Throws<ArgumentException>(() => CommandPlan.Trunk("Gi0/1", 10, "20,30"));
    [Fact] public void TrunkInputCannotInjectLines()
    {
        var plan = CommandPlan.Trunk("Gi0/1", 10, "10,\n20");
        Assert.All(plan.Commands, c => Assert.DoesNotContain('\n', c));
        Assert.Contains("switchport trunk allowed vlan 10,20", plan.Commands);
    }
    [Fact] public async Task DryRunNeverContactsSwitch()
    {
        var session = new FakeSession(); var driver = new CiscoIosDriver(session, new TestAudit());
        await driver.ApplyAsync(CommandPlan.Enabled("Fa0/1", false), true);
        await driver.ApplyAsync(CommandPlan.Save(), true);
        Assert.Empty(session.Commands);
    }
    [Fact] public async Task PartialFailureStopsAndLeavesConfigMode()
    {
        var session = new FakeSession { FailOn = "shutdown" }; var driver = new CiscoIosDriver(session, new TestAudit());
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.Enabled("Fa0/1", false), false));
        Assert.Equal(new[] { "configure terminal", "interface Fa0/1", "shutdown", "end" }, session.Commands);
        Assert.DoesNotContain("write memory", session.Commands);
    }
    [Fact] public async Task SaveRequiresAnIosAcknowledgment()
    {
        var session = new FakeSession(); var driver = new CiscoIosDriver(session, new TestAudit());
        await Assert.ThrowsAsync<SwitchPilot.Infrastructure.Ssh.CliException>(() => driver.ApplyAsync(CommandPlan.Save(), false));
    }
    internal sealed class FakeSession : ICliSession
    {
        public List<string> Commands { get; } = [];
        public string? FailOn { get; init; }
        public bool IsConnected => true;
        public string Hostname => "SW";
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        { Commands.Add(command); if (command == FailOn) throw new InvalidOperationException("Rejected"); return Task.FromResult(""); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class TestAudit : IAuditSink { public void Write(string action, string result) { } }
}
