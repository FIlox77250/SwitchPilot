using System.Text.RegularExpressions;
using SwitchPilot.Core.Cisco;

namespace SwitchPilot.Core.Allied;

/// <summary>
/// Parsers for the older Allied Telesis AT-S95 firmware (AT-8000GS and relatives). This is a
/// Cisco-Small-Business-style CLI: ports are `g1`…`g24` (stacked: `1/g1`), channels `ch1`,
/// paging is disabled with `terminal datadump`, the MAC table is `show bridge address-table`
/// and VLANs come from `show vlan` (there is no `show vlan brief`).
/// </summary>
public static class S95Parser
{
    /// <summary>"g1", "1/g1" and "ethernet 1/g1" all denote the same port; unit 1 is implicit.</summary>
    public static string NormalizePort(string token)
    {
        var m = Regex.Match(token.Trim(), @"^(?:ethernet\s+)?(?:(?<unit>\d+)/)?(?<name>g\d+|ch\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return token.Trim();
        var unit = m.Groups["unit"].Value;
        var name = m.Groups["name"].Value.ToLowerInvariant();
        // Only unit 1 is elided; other units keep their prefix so stack ports stay distinct.
        return unit.Length == 0 || unit == "1" ? name : unit + "/" + name;
    }

    public static bool IsPortToken(string token) =>
        Regex.IsMatch(token.Trim(), @"^(?:ethernet\s+)?(?:\d+/)?(?:g\d+|ch\d+)$", RegexOptions.IgnoreCase);

    /// <summary>`show version` (line form) or the stacked Unit/SW/Boot/HW table.</summary>
    public static SwitchIdentity Identity(string hostname, string versionOutput, string? systemOutput)
    {
        var clean = CiscoParser.Clean(versionOutput);
        var sw = Regex.Match(clean, @"(?im)^\s*SW version\s+(?:version\s+)?v?([0-9][0-9A-Za-z._()-]*)");
        if (!sw.Success)
            sw = Regex.Match(clean, @"(?im)^\s*\d+\s+v?([0-9]+\.[0-9][0-9.]*)\s+v?[0-9]+\.[0-9][0-9.]*\s+[0-9]");
        var model = systemOutput is null ? null : AlliedTelesisParser.Model(systemOutput);
        if (model is null && systemOutput is not null)
        {
            var row = Regex.Match(CiscoParser.Clean(systemOutput), @"(?im)^\s*\d+\s+(AT-[\w./+-]+)");
            if (row.Success) model = row.Groups[1].Value;
        }
        return new(hostname, model ?? "Allied Telesis AT-S95 (AT-8000GS)", sw.Success ? sw.Groups[1].Value : "Inconnue");
    }

    /// <summary>
    /// `show interfaces status` — columns: Port Type Duplex Speed Neg FlowCtrl LinkState BackPressure Mdix.
    /// Missing values are shown as `--`.
    /// </summary>
    public static IReadOnlyList<PortInfo> Ports(string output)
    {
        var result = new List<PortInfo>();
        foreach (var raw in CiscoParser.Clean(output).Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^(?<port>(?:\d+/)?(?:g\d+|ch\d+))\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var tokens = Regex.Split(m.Groups["rest"].Value.Trim(), @"\s+");
            if (tokens.Length < 2) continue;
            var type = tokens[0];
            string Duplex() => tokens.Length > 1 ? tokens[1] : "--";
            string Speed() => tokens.Length > 2 ? tokens[2] : "--";
            // Link State is the only Up/Down token; find it defensively rather than by index.
            var link = tokens.FirstOrDefault(t => t is "Up" or "Down", "--");
            result.Add(new(NormalizePort(m.Groups["port"].Value), "",
                link == "Up" ? "connected" : "notconnect", "",
                Duplex() is "Full" or "Half" ? Duplex().ToLowerInvariant() : "auto",
                Regex.IsMatch(Speed(), @"^\d+$") ? Speed() : "auto",
                type, "Inconnu"));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show interfaces status » (AT-S95). Format inattendu ou autorisation insuffisante.");
        return result;
    }

    /// <summary>`show interfaces description` — tolerant; unknown layouts are skipped.</summary>
    public static Dictionary<string, string> Descriptions(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in CiscoParser.Clean(output).Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^(?<port>(?:\d+/)?(?:g\d+|ch\d+))\s{2,}(?<desc>.*)$", RegexOptions.IgnoreCase);
            if (m.Success) result[NormalizePort(m.Groups["port"].Value)] = m.Groups["desc"].Value.Trim();
        }
        return result;
    }

    /// <summary>
    /// `show vlan` — columns: VLAN Name Ports Type Authorization. The name can contain spaces
    /// and the Ports column can be empty, so the row is parsed from both ends.
    /// </summary>
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var raw in CiscoParser.Clean(output).Split('\n'))
        {
            var line = raw.TrimEnd();
            var m = Regex.Match(line, @"^\s*(?<id>\d+)\s+(?<mid>.*?)\s*(?<type>other|dynamic|static|guest)\s+(?<auth>Required|Not Required|-)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups["id"].Value, out var id)) continue;
            var mid = m.Groups["mid"].Value.Trim();
            string name = mid, ports = "";
            var split = Regex.Match(mid, @"^(?<name>.*?)(?:\s{2,}(?<ports>\S.*))?$");
            if (split.Success)
            {
                name = split.Groups["name"].Value.Trim();
                ports = split.Groups["ports"].Success ? split.Groups["ports"].Value.Trim() : "";
            }
            if (name.Length == 0) name = "VLAN" + id;
            ports = Regex.Replace(ports, @"\b1/(g\d+)", "$1");
            result.Add(new(id, name, "active", ports));
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue dans « show vlan » (AT-S95).");
        return result;
    }

    /// <summary>
    /// Parses one `show interfaces switchport ethernet &lt;port&gt;` output. `General` mode only
    /// counts as access when the port is an untagged member of exactly one VLAN; anything else
    /// is treated as trunk so safety decisions never rely on a hybrid port.
    /// </summary>
    public static (string Mode, string Pvid) Switchport(string output)
    {
        var clean = CiscoParser.Clean(output);
        var mode = Regex.Match(clean, @"VLAN Membership [Mm]ode:\s*(\S+)");
        var pvid = Regex.Match(clean, @"(?im)^\s*PVID:\s*(\d+)");
        var members = new List<(int Vlan, bool Tagged)>();
        foreach (Match row in Regex.Matches(clean, @"(?im)^\s*(\d+)\s+\S(?:.*?\s)?(untagged|tagged)\s+(?:System|Dynamic|Static)\s*$"))
            members.Add((int.Parse(row.Groups[1].Value), row.Groups[2].Value.Equals("tagged", StringComparison.OrdinalIgnoreCase)));
        var raw = mode.Success ? mode.Groups[1].Value : "";
        var classified = raw.Equals("access", StringComparison.OrdinalIgnoreCase) ? "access"
            : raw.Equals("trunk", StringComparison.OrdinalIgnoreCase) ? "trunk"
            : raw.Equals("general", StringComparison.OrdinalIgnoreCase)
                ? members.Count == 1 && !members[0].Tagged ? "access" : "trunk"
            : raw.Length > 0 ? raw.ToLowerInvariant() : "Inconnu";
        return (classified, pvid.Success ? pvid.Groups[1].Value : "");
    }

    /// <summary>
    /// `show bridge address-table` — columns: vlan, mac address, Port (g16/ch5), Type. There is
    /// no per-MAC filter on this firmware, so the whole table is read.
    /// </summary>
    public static IReadOnlyList<MacEntry> Macs(string output)
    {
        var result = new List<MacEntry>();
        foreach (var raw in CiscoParser.Clean(output).Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^(?<vlan>\d+)\s+(?<mac>[0-9a-fA-F:.-]{11,17})\s+(?<port>(?:\d+/)?(?:g\d+|ch\d+))\s+(?<type>\S+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups["vlan"].Value, out var vlan)) continue;
            string mac;
            try { mac = CiscoParser.NormalizeMac(m.Groups["mac"].Value); } catch (ArgumentException) { continue; }
            result.Add(new(vlan, mac, m.Groups["type"].Value.ToUpperInvariant(), NormalizePort(m.Groups["port"].Value)));
        }
        if (result.Count == 0 && !Regex.IsMatch(output, @"address-table|mac address|Aging time|bridge", RegexOptions.IgnoreCase))
            throw new FormatException("Table MAC non reconnue dans « show bridge address-table » (AT-S95).");
        return result;
    }
}
