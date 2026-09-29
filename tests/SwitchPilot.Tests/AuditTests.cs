using System.Text.Json;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Storage;

namespace SwitchPilot.Tests;

public class AuditTests
{
    [Fact] public void RetentionOnlyDeletesExpiredApplicationLogs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "switchpilot-log-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var old = Path.Combine(dir, "actions-2000-01-01.jsonl"); File.WriteAllText(old, "test"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));
            var unrelated = Path.Combine(dir, "keep.txt"); File.WriteAllText(unrelated, "keep"); File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-60));
            var log = new AuditLog(dir); log.Write("Simulation", "Aucune modification.");
            Assert.False(File.Exists(old)); Assert.True(File.Exists(unrelated));
            var path = Assert.Single(Directory.GetFiles(dir, "actions-*.jsonl"));
            Assert.Equal("Simulation", JsonSerializer.Deserialize<AuditEntry>(File.ReadAllText(path))?.Action);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact] public void DiskFailureIsReportedAndDoesNotLoseInMemoryEvent()
    {
        var path = Path.GetTempFileName();
        try
        {
            var log = new AuditLog(path); AuditEntry? observed = null; log.Added += entry => observed = entry;
            log.Write("Test", "Résultat");
            Assert.NotNull(log.PersistenceError); Assert.NotNull(observed);
        }
        finally { File.Delete(path); }
    }
}
