using System.Text.RegularExpressions;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Parsing;

/// <summary>Juniper Junos ELS read-only outputs (EX / QFX).</summary>
public static class JunosParser
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const SwitchVendor Vendor = SwitchVendor.Juniper;

    private static string Physical(string name) => Regex.Replace(name.TrimEnd('*'), @"\.\d+$", "");

    /// <summary>"show interfaces terse": physical rows only (no ".0" logical units).</summary>
    public static IReadOnlyList<PortInfo> Terse(string output)
    {
        var result = new List<PortInfo>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^(?<port>\S+)\s+(?<admin>up|down)\s+(?<link>up|down)\b", Ci);
            if (!m.Success || m.Groups["port"].Value.Contains('.') || ParseKit.Port(Vendor, m.Groups["port"].Value) is not { } port) continue;
            var status = m.Groups["admin"].Value.Equals("down", StringComparison.OrdinalIgnoreCase) ? "disabled"
                : m.Groups["link"].Value.Equals("up", StringComparison.OrdinalIgnoreCase) ? "connected" : "notconnect";
            result.Add(new(port, "", status, "", "", "", ""));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show interfaces terse ».");
        return result;
    }

    /// <summary>"show interfaces descriptions": Interface, Admin, Link, Description.</summary>
    public static Dictionary<string, string> Descriptions(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^(?<port>\S+)\s+(?:up|down)\s+(?:up|down)\s+(?<desc>.*)$", Ci);
            if (m.Success && ParseKit.Port(Vendor, Physical(m.Groups["port"].Value)) is { } port) result[port] = m.Groups["desc"].Value.Trim();
        }
        return result;
    }

    /// <summary>
    /// "show ethernet-switching interface": a logical interface line followed by one line per
    /// VLAN member ("name tag … tagged|untagged"). Returns (mode, access VLAN or "trunk") by port.
    /// </summary>
    public static Dictionary<string, (string Mode, string Vlan)> SwitchingInterfaces(string output)
    {
        var members = new Dictionary<string, List<(int Id, bool Tagged)>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in ParseKit.Lines(output))
        {
            var head = Regex.Match(line, @"^(?<if>[a-z]{2,3}-\d+/\d+/\d+\.\d+|ae\d+\.\d+)\b", Ci);
            if (head.Success)
            {
                current = ParseKit.Port(Vendor, Physical(head.Groups["if"].Value));
                if (current is not null && !members.ContainsKey(current)) members[current] = [];
                continue;
            }
            var member = Regex.Match(line, @"^\s+(?<name>\S+)\s+(?<tag>\d{1,4})\s+.*?\b(?<tagging>tagged|untagged)\s*$", Ci);
            if (current is not null && member.Success)
                members[current].Add((int.Parse(member.Groups["tag"].Value), member.Groups["tagging"].Value.Equals("tagged", StringComparison.OrdinalIgnoreCase)));
        }
        return members.ToDictionary(p => p.Key, p => p.Value.Count == 1 && !p.Value[0].Tagged ? ("access", p.Value[0].Id.ToString())
            : p.Value.Count == 0 ? ("Inconnu", "") : ("trunk", "trunk"), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"show vlans": Routing instance, VLAN name, Tag, Interfaces (continued on the next lines).</summary>
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^(?<ri>\S+)\s+(?<name>\S+)\s+(?<tag>\d{1,4})\b\s*(?<ifs>.*)$", Ci);
            if (m.Success && !m.Groups["ri"].Value.Equals("Routing", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(int.Parse(m.Groups["tag"].Value), m.Groups["name"].Value, "active", Interfaces(m.Groups["ifs"].Value)));
                continue;
            }
            if (result.Count > 0 && Regex.IsMatch(line, @"^\s{8,}\S"))
                result[^1] = result[^1] with { Ports = string.Join(", ", new[] { result[^1].Ports, Interfaces(line) }.Where(p => p.Length > 0)) };
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue (« show vlans »).");
        return result;
    }

    private static string Interfaces(string text) => string.Join(", ", text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(Physical).Where(p => PortNames.TryValidate(Vendor, p, out _)));

    /// <summary>"show ethernet-switching table": VLAN names are resolved to tags with <paramref name="vlanIds"/>.</summary>
    public static IReadOnlyList<MacEntry> Macs(string output, IReadOnlyDictionary<string, int> vlanIds)
    {
        var result = new List<MacEntry>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<vlan>\S+)\s+(?<mac>(?:[0-9a-f]{2}:){5}[0-9a-f]{2})\s+(?<flags>\S+)\s+(?:\S+\s+)?(?<if>[a-z]{2,3}-\d+/\d+/\d+(?:\.\d+)?|ae\d+(?:\.\d+)?)\b", Ci);
            if (!m.Success || ParseKit.Port(Vendor, Physical(m.Groups["if"].Value)) is not { } port) continue;
            var vlanText = m.Groups["vlan"].Value;
            if (!vlanIds.TryGetValue(vlanText, out var vlan) && !int.TryParse(vlanText, out vlan)) continue;
            var flags = m.Groups["flags"].Value.ToUpperInvariant();
            result.Add(new(vlan, CiscoParser.NormalizeMac(m.Groups["mac"].Value), flags.Contains('S') && !flags.Contains("SE") ? "STATIC" : "DYNAMIC", port));
        }
        if (result.Count == 0 && !Regex.IsMatch(output, @"ethernet switching table|MAC\s+address|0 entries", Ci))
            throw new FormatException("Table MAC non reconnue ; résultat non considéré comme vide.");
        return result;
    }

    public static SwitchIdentity Identity(string hostname, string version)
    {
        var fields = ParseKit.ColonFields(version);
        var junos = fields.GetValueOrDefault("Junos") ?? (Regex.Match(version, @"JUNOS [^\[]*\[([^\]]+)\]", Ci) is { Success: true } m ? m.Groups[1].Value : null);
        var name = fields.GetValueOrDefault("Hostname");
        return new(string.IsNullOrWhiteSpace(name) ? hostname : name, fields.GetValueOrDefault("Model") ?? "Junos (modèle inconnu)", junos ?? "Inconnue");
    }

    /// <summary>"show interfaces X extensive": link, speed, duplex, input/output errors, collisions, CRC/Align.</summary>
    public static InterfaceCounters Counters(string output)
    {
        long? Match(string pattern) => Regex.Match(output, pattern, Ci | RegexOptions.Singleline) is { Success: true } m ? ParseKit.Long(m.Groups[1].Value) : null;
        var link = Regex.Match(output, @"Physical interface:\s*\S+,\s*(Enabled|Administratively down|Disabled),\s*Physical link is (Up|Down)", Ci);
        var speed = Regex.Match(output, @"Speed:\s*(\d+)\s*([mg])bps", Ci);
        var duplex = Regex.Match(output, @"Link-mode:\s*(Full|Half)-duplex", Ci);
        return new(Match(@"CRC/Align errors\s+(\d+)"), Match(@"Output errors:.*?Collisions:\s*(\d+)"), Match(@"Input errors:\s*Errors:\s*(\d+)"),
            speed.Success ? ParseKit.Speed(speed.Groups[1].Value + speed.Groups[2].Value.ToUpperInvariant()) : "Inconnue",
            duplex.Success ? duplex.Groups[1].Value : "Inconnu",
            !link.Success ? "Inconnu" : link.Groups[1].Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && link.Groups[2].Value.Equals("Up", StringComparison.OrdinalIgnoreCase) ? "Actif" : "Inactif",
            Match(@"Output errors:\s*(?:Carrier transitions:\s*\d+,\s*)?Errors:\s*(\d+)"));
    }
}
