using SwitchPilot.Core;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Ssh;

namespace SwitchPilot.Tests;

public sealed class SshIntegrationFactAttribute : FactAttribute
{
    public SshIntegrationFactAttribute() { if (Environment.GetEnvironmentVariable("SWITCHPILOT_SSH_TEST_PORT") is null) Skip = "Démarrer via tests/ssh_emulator.py pour le test SSH de bout en bout."; }
}

public class SshIntegrationTests
{
    private static ConnectionProfile Profile => new("127.0.0.1", int.Parse(Environment.GetEnvironmentVariable("SWITCHPILOT_SSH_TEST_PORT")!), "test-technician", false, "test-only-password", "test-enable");
    [SshIntegrationFact] public async Task RealSshTransportReadsAndConfiguresEmulatedIos()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sawFingerprint = false;
        await using var session = await SshSession.ConnectAsync(Profile, (_, fingerprint) => { sawFingerprint = fingerprint.Length > 20; return true; }, false, timeout.Token);
        Assert.True(sawFingerprint);
        await using var driver = new CiscoIosDriver(session, new SafetyTests.TestAudit());
        var snapshot = await driver.ReadSnapshotAsync(timeout.Token);
        Assert.Equal("LAB-SW", snapshot.Identity.Name); Assert.Equal("WS-C2960+24TC-L", snapshot.Identity.Model);
        var macs = await driver.ReadMacTableAsync(timeout.Token);
        var result = Assert.Single(PortLocator.Find("00:11:22:33:44:55", snapshot, macs));
        Assert.Equal("Fa0/14", result.Port.Name); Assert.True(result.DirectCandidate);
        Assert.Equal("Bureau 214 - prise murale A-014", result.Port.Description);
        var counters = await driver.ReadCountersAsync("Fa0/14", timeout.Token);
        Assert.Equal(3, counters.Crc); Assert.Equal("Actif", counters.LinkState);
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var observation = await driver.ReadDetectionAsync("001122334455", timeout.Token);
            var detected = Assert.Single(PortLocator.Find("001122334455", observation.Snapshot, observation.Entries));
            Assert.True(detected.DirectCandidate); Assert.Equal("Fa0/14", detected.Port.Name);
        }
        await driver.ApplyAsync(CommandPlan.Describe("Fa0/14", "Test lab"), false, timeout.Token);
        await driver.ApplyAsync(CommandPlan.Save(), false, timeout.Token);
        Assert.Contains("hostname LAB-SW", await driver.ExportAsync(timeout.Token));
    }
    [SshIntegrationFact] public async Task RejectingHostKeyPreventsConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => SshSession.ConnectAsync(Profile, (_, _) => false, false, timeout.Token));
    }
    [SshIntegrationFact] public async Task WrongPasswordIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<Renci.SshNet.Common.SshAuthenticationException>(() => SshSession.ConnectAsync(Profile with { Password = "wrong-test-password" }, (_, _) => true, false, timeout.Token));
    }
    [SshIntegrationFact] public async Task WrongEnablePasswordIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<CliException>(() => SshSession.ConnectAsync(Profile with { EnablePassword = "wrong-enable" }, (_, _) => true, false, timeout.Token));
    }
    [SshIntegrationFact] public async Task CancellationClosesSessionAndDisposeIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await SshSession.ConnectAsync(Profile, (_, _) => true, false, timeout.Token);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync("show hang", cancel.Token));
        Assert.False(session.IsConnected);
        await session.DisposeAsync(); Assert.False(session.IsConnected);
    }
    [SshIntegrationFact] public async Task UnexpectedConfirmationClosesSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await SshSession.ConnectAsync(Profile, (_, _) => true, false, timeout.Token);
        await Assert.ThrowsAsync<CliProtocolException>(() => session.ExecuteAsync("show confirm", timeout.Token));
        Assert.False(session.IsConnected);
    }
    [SshIntegrationFact] public async Task ConcurrentCommandsDoNotMixResponses()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await SshSession.ConnectAsync(Profile, (_, _) => true, false, timeout.Token);
        var version = session.ExecuteAsync("show version", timeout.Token);
        var vlan = session.ExecuteAsync("show vlan brief", timeout.Token);
        await Task.WhenAll(version, vlan);
        Assert.Contains("Version 15.2", version.Result); Assert.Contains("TECHNIQUE", vlan.Result);
    }
}
