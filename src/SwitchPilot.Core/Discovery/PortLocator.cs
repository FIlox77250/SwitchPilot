using SwitchPilot.Core.Cisco;

namespace SwitchPilot.Core.Discovery;

public static class PortLocator
{
    public static IReadOnlyList<PortDetection> Find(string mac, SwitchSnapshot snapshot, IReadOnlyList<MacEntry> entries)
    {
        mac = CiscoParser.NormalizeMac(mac);
        return entries.Where(e => e.Mac == mac).Select(e =>
        {
            var port = snapshot.Ports.FirstOrDefault(p => p.Name == e.Port);
            if (port is null) return null;
            // An access port is only a candidate: a phone or unmanaged switch can sit downstream.
            var direct = port.Mode == "access" && port.IsUp && !port.Name.StartsWith("Po") && e.Type == "DYNAMIC";
            return new PortDetection(snapshot.Identity.Name, e, port, direct, direct
                ? "MAC apprise sur un port access. Un équipement intermédiaire reste possible."
                : "MAC vue sur ce port ; uplink, agrégat ou état indéterminé. Branchement direct non confirmé.");
        }).OfType<PortDetection>().ToArray();
    }
}
