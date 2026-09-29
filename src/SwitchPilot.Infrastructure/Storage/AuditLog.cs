using System.Text.Json;
using SwitchPilot.Core;

namespace SwitchPilot.Infrastructure.Storage;

// Only curated action/result messages enter this sink. No raw CLI, packets or exceptions.
public sealed class AuditLog(string directory) : IAuditSink
{
    private readonly object gate = new();
    private DateTime nextCleanup;
    public event Action<AuditEntry>? Added;
    public string? PersistenceError { get; private set; }
    public void Write(string action, string result)
    {
        var entry = new AuditEntry(DateTimeOffset.Now, action, result);
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, $"actions-{DateTime.Now:yyyy-MM-dd}.jsonl");
                File.AppendAllText(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
                if (DateTime.UtcNow >= nextCleanup)
                {
                    foreach (var old in Directory.EnumerateFiles(directory, "actions-*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30))) File.Delete(old);
                    nextCleanup = DateTime.UtcNow.Date.AddDays(1);
                }
                PersistenceError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { PersistenceError = "Le journal ne peut pas être écrit dans le dossier utilisateur."; }
        }
        Added?.Invoke(entry);
    }
}
