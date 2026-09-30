using System.Text.Json;
using SwitchPilot.Core.Discovery;
namespace SwitchPilot.Infrastructure.Storage;
public sealed class OutletStore(string directory)
{
    private string Pathname => Path.Combine(directory, "outlets.json");
    public List<OutletEntry> Entries { get; } = [];
    public void Load()
    {
        if (!File.Exists(Pathname)) return;
        var entries = JsonSerializer.Deserialize<List<OutletEntry>>(AtomicFile.ReadBounded(Pathname, 4 * 1024 * 1024)) ?? [];
        Entries.Clear(); Entries.AddRange(entries.Select(OutletCsv.Validate));
    }
    public string Lookup(string hostname, string port) => Entries.FirstOrDefault(e => e.Switch.Equals(hostname, StringComparison.OrdinalIgnoreCase) && e.Port.Equals(Core.Cisco.CiscoParser.NormalizeInterface(port), StringComparison.OrdinalIgnoreCase))?.Outlet ?? "";
    public void Merge(IEnumerable<OutletEntry> entries)
    {
        var next = Entries.ToList();
        foreach (var raw in entries)
        {
            var entry = OutletCsv.Validate(raw);
            next.RemoveAll(e => e.Switch.Equals(entry.Switch, StringComparison.OrdinalIgnoreCase) && e.Port == entry.Port); next.Add(entry);
        }
        if (next.Count > 10000) throw new InvalidDataException("Maximum : 10 000 prises.");
        Directory.CreateDirectory(directory); AtomicFile.Write(Pathname, JsonSerializer.SerializeToUtf8Bytes(next));
        Entries.Clear(); Entries.AddRange(next);
    }
}
