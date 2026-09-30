namespace SwitchPilot.Core.Discovery;

// Per-link observations. A generation changes whenever a cable is removed/replaced.
public sealed class NeighborTracker
{
    private readonly Dictionary<string, NeighborAnnouncement> neighbors = new(StringComparer.OrdinalIgnoreCase);
    public long Generation { get; private set; }
    public void Reset() { neighbors.Clear(); Generation++; }
    private static string Key(NeighborAnnouncement item) => item.Protocol + "/" + item.SwitchName + "/" + item.Port;
    public bool Accept(NeighborAnnouncement item, long generation)
    {
        if (generation != Generation) return false;
        if (item.TtlSeconds < 0) return false;
        if (neighbors.Count >= 64 && !neighbors.ContainsKey(Key(item))) return false;
        if (item.TtlSeconds == 0) neighbors.Remove(Key(item)); else neighbors[Key(item)] = item;
        return true;
    }
    public IReadOnlyList<NeighborAnnouncement> Current(DateTimeOffset now)
    {
        foreach (var key in neighbors.Where(p => now - p.Value.ReceivedAt >= TimeSpan.FromSeconds(p.Value.TtlSeconds)).Select(p => p.Key).ToArray()) neighbors.Remove(key);
        return neighbors.Values.OrderBy(n => n.Protocol).ToArray();
    }
}
public static class NeighborCorrelation
{
    public static bool SameSwitch(string announced, string connected) => announced.Equals(connected, StringComparison.OrdinalIgnoreCase) ||
        (announced.Contains('.') && !connected.Contains('.') && announced.Split('.')[0].Equals(connected, StringComparison.OrdinalIgnoreCase));
    public static string Compare(NeighborAnnouncement announcement, PortDetection detection)
    {
        if (!SameSwitch(announcement.SwitchName, detection.SwitchName)) return "Switch connecté différent du voisin annoncé : recoupement impossible.";
        if (!Cisco.CiscoParser.NormalizeInterface(announcement.Port).Equals(detection.Port.Name, StringComparison.OrdinalIgnoreCase))
            return "Incohérence : le port LLDP/CDP diffère de celui de la table MAC.";
        return announcement.Vlan is { } vlan && vlan != detection.Entry.Vlan
            ? "Port concordant ; VLAN annoncé différent du VLAN MAC (il peut s’agir du VLAN natif)."
            : "Port concordant entre l’annonce locale et la table MAC.";
    }
}
