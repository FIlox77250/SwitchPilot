using SwitchPilot.Core;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Storage;
namespace SwitchPilot.Tests;
public class ExtrasTests
{
    [Fact] public void CsvRoundtripPreservesAccentsQuotesAndCommas()
    {
        OutletEntry[] entries = [new("SW-A", "Fa0/14", "Salle 104, prise \"P12\""), new("SW-B", "Gi0/1", "Réserve")];
        Assert.Equal(entries, OutletCsv.Import(OutletCsv.Export(entries)));
    }
    [Theory][InlineData("Switch,Port,Prise\nSW,Fa0/1,=cmd\n")][InlineData("Switch,Port,Prise\nSW,Fa0/1,P1\nSW,FastEthernet0/1,P2")][InlineData("Switch,Port,Prise\nSW,Fa0/1,\"broken")]
    public void InvalidImportIsRejected(string csv) => Assert.ThrowsAny<Exception>(() => OutletCsv.Import(csv));
    [Fact] public async Task BackupFailurePreventsEveryMutation()
    {
        var session = new SafetyTests.FakeSession(); var backup = new Backup { Fail = true };
        await using var driver = new CiscoIosDriver(session, new SafetyTests.TestAudit(), backup: backup);
        await Assert.ThrowsAsync<IOException>(() => driver.ApplyAsync(CommandPlan.Describe("Fa0/1", "Test"), false));
        Assert.Equal(new[] { "show running-config" }, session.Commands);
    }
    [Fact] public async Task BackupPrecedesMutationAndSimulationDoesNotBackup()
    {
        var session = new SafetyTests.FakeSession(); var backup = new Backup();
        await using var driver = new CiscoIosDriver(session, new SafetyTests.TestAudit(), backup: backup);
        await driver.ApplyAsync(CommandPlan.Describe("Fa0/1", "Test"), true); Assert.False(backup.Saved); Assert.Empty(session.Commands);
        await driver.ApplyAsync(CommandPlan.Describe("Fa0/1", "Test"), false); Assert.True(backup.Saved); Assert.Equal("show running-config", session.Commands[0]);
    }
    [Fact] public async Task MissingBackupServiceBlocksRealWrites()
    {
        var session = new SafetyTests.FakeSession(); await using var driver = new CiscoIosDriver(session, new SafetyTests.TestAudit());
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.Describe("Fa0/1", "Test"), false)); Assert.Empty(session.Commands);
    }
    private sealed class Backup : IConfigurationBackup
    {
        public bool Fail, Saved;
        public Task SaveAsync(string hostname, string configuration, CancellationToken ct) { if (Fail) throw new IOException(); Saved = true; return Task.CompletedTask; }
    }
}
