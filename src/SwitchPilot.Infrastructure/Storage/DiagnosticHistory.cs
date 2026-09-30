using System.Text.Json;
using SwitchPilot.Core.Diagnostics;
namespace SwitchPilot.Infrastructure.Storage;
public sealed class DiagnosticHistory(string directory)
{
    private string Pathname => Path.Combine(directory, "diagnostics.json");
    public List<DiagnosticRecord> Records { get; } = [];
    public void Load()
    {
        if (!File.Exists(Pathname)) return;
        var records = JsonSerializer.Deserialize<List<DiagnosticRecord>>(AtomicFile.ReadBounded(Pathname, 16 * 1024 * 1024)) ?? [];
        Records.Clear(); Records.AddRange(records.TakeLast(1000));
    }
    public void Add(DiagnosticRecord record)
    {
        Records.Add(record); if (Records.Count > 1000) Records.RemoveRange(0, Records.Count - 1000);
        Directory.CreateDirectory(directory); AtomicFile.Write(Pathname, JsonSerializer.SerializeToUtf8Bytes(Records));
    }
}
