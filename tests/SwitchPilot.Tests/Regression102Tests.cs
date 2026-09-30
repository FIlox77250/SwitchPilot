using SwitchPilot.Core;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Discovery;
using SwitchPilot.Infrastructure.Ssh;
using SwitchPilot.Infrastructure.Terminal;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace SwitchPilot.Tests;

public class Regression102Tests
{
    [Fact] public async Task PlainIosRefusalsAreNotSuccess()
    {
        foreach (var error in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures/rejections/ios.txt")))
        {
            var terminal = new Terminal(); var cli = new CliConversation(terminal);
            await cli.InitializeAsync(default); terminal.Response = error + "\nSW#";
            await Assert.ThrowsAsync<CliException>(() => cli.CommandAsync("vlan 20", default));
        }
    }
    [Fact] public async Task ConfigurationTextIsNotARefusal()
    {
        var terminal = new Terminal(); var cli = new CliConversation(terminal); await cli.InitializeAsync(default);
        terminal.Response = "interface Gi0/1\n description Not allowed\n!\nSW#";
        Assert.Contains("description", await cli.CommandAsync("show running-config", default));
    }
    [Theory][InlineData(3u, 4u, 4u, true)][InlineData(4u, 4u, 4u, false)][InlineData(3u, 4u, 5u, false)]
    public void RouteTypeAndInterfaceAreRequired(uint type, uint actual, uint expected, bool direct) =>
        Assert.Equal(direct, NetworkDiscovery.IsDirectRoute(type, actual, expected));
    [Fact] public void NetworkEventsAreCoalescedAndRateLimited()
    {
        var schedule = new DetectionSchedule(); var now = DateTimeOffset.UtcNow;
        Assert.True(schedule.TryStart(now));
        for (var i = 1; i < 60; i++) { schedule.Request(); Assert.False(schedule.TryStart(now.AddSeconds(i))); }
        Assert.True(schedule.TryStart(now.AddSeconds(60))); Assert.False(schedule.Pending);
    }
    [Fact] public void LegacyRetryIsOnlyForNegotiation()
    {
        Assert.True(SshSession.IsNegotiationFailure(new SshConnectionException("", DisconnectReason.KeyExchangeFailed)));
        Assert.False(SshSession.IsNegotiationFailure(new SshAuthenticationException("")));
        Assert.False(SshSession.IsNegotiationFailure(new SshConnectionException("", DisconnectReason.HostKeyNotVerifiable)));
        Assert.True(SshSession.IsNegotiationFailure(new SshConnectionException("no matching key exchange method found", DisconnectReason.None)));
        Assert.True(SshSession.IsNegotiationFailure(new SshConnectionException("could not agree on a cipher", DisconnectReason.None)));
        Assert.False(SshSession.IsNegotiationFailure(new SshConnectionException("Connection reset by peer", DisconnectReason.None)));
    }
    [Fact] public async Task DriverBlocksProtectedAndUncertainWritesBeforeSendingAnything()
    {
        var session = new SafetyTests.FakeSession(); await using var driver = new CiscoIosDriver(session, new SafetyTests.TestAudit());
        foreach (var plan in new[] { CommandPlan.Enabled("Fa0/14", false), CommandPlan.Access("Fa0/14", 10), CommandPlan.Trunk("Fa0/14", 10, "10") })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(plan, false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(plan, false, safety: new(true, true, new HashSet<string> { "FastEthernet0/14" })));
        }
        Assert.Empty(session.Commands);
    }
    [Fact] public void ConsoleDoesNotUseNetworkPathGuard() => SafetyPolicy.RequireSafeChange(CommandPlan.Enabled("Fa0/14", false), SafetyContext.Unknown, ConnectionKind.Serial);
    [Fact] public void CableChangeInvalidatesOtherwiseFreshSafetyContext()
    {
        var changed = false;
        var context = new SafetyContext(true, true, new HashSet<string>()) { StillCurrent = () => !changed };
        SafetyPolicy.RequireSafeChange(CommandPlan.Enabled("Fa0/1", false), context, ConnectionKind.Ssh);
        changed = true;
        Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeChange(CommandPlan.Enabled("Fa0/1", false), context, ConnectionKind.Ssh));
    }
    private sealed class Terminal : ITerminalChannel
    {
        private string ready = "SW#";
        public string Response = "SW#";
        public bool IsOpen => true;
        public string ReadAvailable() { var result = ready; ready = ""; return result; }
        public void Send(string text) => ready = Response;
        public void Dispose() { }
    }
}
