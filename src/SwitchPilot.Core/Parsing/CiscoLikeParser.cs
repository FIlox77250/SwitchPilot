using System.Text.RegularExpressions;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Parsing;

/// <summary>
/// Read-only outputs of the IOS-like platforms that are not IOS: Cisco NX-OS, Arista EOS and
/// Dell OS6 / OS9 / OS10. Port names are validated against the platform grammar
/// (<see cref="PortNames"/>); rows that are not physical or aggregated ports are skipped.
/// </summary>
public static class CiscoLikeParser
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Normalizes the status words of the status tables to the historical IOS vocabulary.</summary>
    public static string Status(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "connected" or "up" => "connected",
        var s when s.StartsWith("notconn") || s == "down" => "notconnect",
        "disabled" or "admin down" or "admindown" or "admin" or "*down" => "disabled",
        "err-disabled" or "errdisabled" or "err-disable" => "err-disabled",
        var s when s.StartsWith("xcvrabsen") || s.StartsWith("sfpabsent") => "sfpAbsent",
        var s => s
    };

    private static string Mode(string vlan) => vlan.ToLowerInvariant() switch
    {
        "trunk" => "trunk",
        "routed" => "routed",
        var v when int.TryParse(v, out _) => "access",
        _ => "Inconnu"
    };

    private static readonly Regex StatusRow = new(
        @"^(?<port>\S+)\s+(?<name>.*?)\s*\b(?<state>connected|notconnec\w*|disabled|err-?disabled|sfpAbsent|xcvrAbsen\w*|noOperMem\w*|linkFlapE\w*|inactive|suspended?|monitoring|errDisabl\w*)\s+(?<vlan>in\s+Po\d+|\S+)\s+(?<duplex>\S+)\s+(?<speed>\S+)\s*(?<type>.*)$", Ci);

    /// <summary>"show interface status" (NX-OS) / "show interfaces status" (EOS).</summary>
    public static IReadOnlyList<PortInfo> StatusTable(SwitchVendor vendor, string output)
    {
        var result = new List<PortInfo>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = StatusRow.Match(line.TrimEnd());
            if (!m.Success || ParseKit.Port(vendor, m.Groups["port"].Value) is not { } port) continue;
            var name = m.Groups["name"].Value.Trim();
            if (name == "--") name = "";
            var vlan = m.Groups["vlan"].Value;
            if (vlan.StartsWith("in ", StringComparison.OrdinalIgnoreCase)) vlan = "trunk";
            var type = m.Groups["type"].Value.Trim();
            if (vendor == SwitchVendor.Arista) type = Regex.Replace(type, @"\s{2,}.*$", ""); // Flags / Encapsulation columns.
            result.Add(new(port, name, Status(m.Groups["state"].Value), vlan, ParseKit.Duplex(m.Groups["duplex"].Value), ParseKit.Speed(m.Groups["speed"].Value),
                type == "--" ? "" : type, Mode(vlan)));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans la table d'état des interfaces. Format inattendu ou autorisation insuffisante.");
        return result;
    }

    /// <summary>Dell OS6 "show interfaces status": Port, Description, Duplex, Speed, Neg, Link State, Flow Control.</summary>
    public static IReadOnlyList<PortInfo> DellOs6Status(string output)
    {
        var result = new List<PortInfo>();
        var row = new Regex(@"^(?<port>(?:Gi|Te|Fo|Tw|Hu|Po)\d+(?:/\d+){0,2})\s+(?<desc>.*?)\s*(?<duplex>Full|Half|N/A|Unknown|Auto)\s+(?<speed>\d+|Unknown|Auto|N/A)\s+(?<neg>Auto|Off|On|N/A)\s+(?<link>Up|Down|Detach|Err-?\w*)\b", Ci);
        var lag = new Regex(@"^(?<port>Po\d+)\s+(?<type>.*?)\s+(?<link>Up|Down)\s*$", Ci);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = row.Match(line.Trim());
            if (m.Success && ParseKit.Port(SwitchVendor.DellOs6, m.Groups["port"].Value) is { } port)
            {
                result.Add(new(port, m.Groups["desc"].Value.Trim(), Status(m.Groups["link"].Value), "", ParseKit.Duplex(m.Groups["duplex"].Value),
                    ParseKit.Speed(m.Groups["speed"].Value), ""));
                continue;
            }
            var l = lag.Match(line.Trim());
            if (l.Success && ParseKit.Port(SwitchVendor.DellOs6, l.Groups["port"].Value) is { } channel)
                result.Add(new(channel, "", Status(l.Groups["link"].Value), "", "", "", l.Groups["type"].Value.Trim()));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show interfaces status » (Dell OS6).");
        return result;
    }

    /// <summary>Dell OS9 "show interfaces status": Port, Description, Status, Speed, Duplex, Vlan.</summary>
    public static IReadOnlyList<PortInfo> DellOs9Status(string output)
    {
        var result = new List<PortInfo>();
        var row = new Regex(@"^(?<port>(?:Gi|Te|Fo|Hu|Po)\s+\d+(?:/\d+){0,2})\s+(?<desc>.*?)\s*\b(?<status>Up|Down|Admin\s?Down)\s+(?<speed>Auto|\d+\s*Mbit|\d+G?)\s+(?<duplex>Full|Half|Auto|N/A)\s*(?<vlan>\S*)\s*$", Ci);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = row.Match(line.Trim());
            if (!m.Success || ParseKit.Port(SwitchVendor.DellOs9, m.Groups["port"].Value) is not { } port) continue;
            var vlan = m.Groups["vlan"].Value;
            if (vlan == "--") vlan = ""; // No VLAN: must not be read as a range.
            var mode = vlan.Contains('-') || vlan.Contains(',') ? "trunk" : int.TryParse(vlan, out _) ? "access" : "Inconnu";
            if (mode == "trunk") vlan = "trunk";
            result.Add(new(port, m.Groups["desc"].Value.Trim(), Status(Regex.Replace(m.Groups["status"].Value, @"\s", " ")), vlan,
                ParseKit.Duplex(m.Groups["duplex"].Value), ParseKit.Speed(m.Groups["speed"].Value), "", mode));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show interfaces status » (Dell OS9).");
        return result;
    }

    /// <summary>Dell OS10 "show interface status": Port, Description, Status, Speed, Duplex, Mode (A/T), Vlan, Tagged-Vlans.</summary>
    public static IReadOnlyList<PortInfo> DellOs10Status(string output)
    {
        var result = new List<PortInfo>();
        var row = new Regex(@"^(?<port>(?:Eth|Po|port-channel)\s*\d+(?:/\d+){0,2}(?::\d+)?)\s+(?<desc>.*?)\s*\b(?<status>up|down|admin-down)\s+(?<speed>\S+)\s+(?<duplex>full|half|auto|-)\s+(?<mode>A|T|-)\s+(?<vlan>\S+)\s*(?<tagged>\S*)\s*$", Ci);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = row.Match(line.Trim());
            if (!m.Success || ParseKit.Port(SwitchVendor.DellOs10, m.Groups["port"].Value) is not { } port) continue;
            var mode = m.Groups["mode"].Value switch { "A" => "access", "T" => "trunk", _ => "routed" };
            var vlan = mode == "trunk" ? "trunk" : m.Groups["vlan"].Value == "-" ? "" : m.Groups["vlan"].Value;
            var status = m.Groups["status"].Value.ToLowerInvariant() switch { "up" => "connected", "admin-down" => "disabled", _ => "notconnect" };
            result.Add(new(port, m.Groups["desc"].Value.Trim(), status, vlan, ParseKit.Duplex(m.Groups["duplex"].Value), ParseKit.Speed(m.Groups["speed"].Value), "", mode));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show interface status » (Dell OS10).");
        return result;
    }

    /// <summary>Dell OS6 "show vlan": VLAN, Name, Ports (ranges, continued on the next lines), Type.</summary>
    public static IReadOnlyList<VlanInfo> DellOs6Vlans(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^(?<id>\d{1,4})\s+(?<name>\S+)\s+(?<ports>\S*?,?)\s*(?<type>Default|Static|Dynamic|GVRP|Private)?\s*$", Ci);
            if (m.Success && int.TryParse(m.Groups["id"].Value, out var id))
            {
                result.Add(new(id, m.Groups["name"].Value, "active", m.Groups["ports"].Value.TrimEnd(',')));
                continue;
            }
            var more = Regex.Match(line, @"^\s{10,}(?<ports>(?:Gi|Te|Fo|Tw|Hu|Po)\S*)", Ci);
            if (more.Success && result.Count > 0)
                result[^1] = result[^1] with { Ports = string.Join(',', new[] { result[^1].Ports, more.Groups["ports"].Value.TrimEnd(',') }.Where(p => p.Length > 0)) };
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue (Dell OS6).");
        return result;
    }

    /// <summary>
    /// Dell OS9 / OS10 "show vlan": "[*] NUM Status Description Q Ports" with one "Q Ports" pair
    /// per line (U/A = untagged / access, T = tagged). Also returns the untagged VLAN of each port.
    /// </summary>
    public static (IReadOnlyList<VlanInfo> Vlans, IReadOnlyDictionary<string, int> Untagged, IReadOnlySet<string> Tagged) DellVlans(SwitchVendor vendor, string output)
    {
        var vlans = new List<VlanInfo>();
        var untagged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var header = new Regex(@"^\s*\*?\s*(?<id>\d{1,4})\s+(?<status>Active|Inactive|Up|Down)\b\s*(?<rest>.*)$", Ci);
        var member = new Regex(@"(?:^|\s)(?<q>[UATxXGMHiIvV])\s+(?<ports>(?:Gi|Te|Fo|Hu|Po|Eth|ethernet|port-channel)\s?\S+(?:\s*,\s*\S+)*)\s*$", Ci);
        foreach (var raw in ParseKit.Lines(output))
        {
            var line = raw.TrimEnd();
            var h = header.Match(line);
            if (h.Success && int.TryParse(h.Groups["id"].Value, out var id))
            {
                var rest = h.Groups["rest"].Value;
                var mm = member.Match(rest);
                var description = (mm.Success ? rest[..mm.Index] : rest).Trim();
                vlans.Add(new(id, description.Length == 0 ? (id == 1 ? "default" : $"VLAN{id:0000}") : description,
                    h.Groups["status"].Value.ToLowerInvariant() is "active" or "up" ? "active" : "inactive", ""));
                if (mm.Success) Member(mm);
                continue;
            }
            if (vlans.Count > 0 && Regex.IsMatch(line, @"^\s{8,}\S") && member.Match(line) is { Success: true } next) Member(next);
        }
        void Member(Match m)
        {
            var q = m.Groups["q"].Value.ToUpperInvariant();
            var names = new List<string>();
            foreach (var part in m.Groups["ports"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // "Gi 1/1-3" or "Eth1/1/2-1/1/4": the first part carries the type for the next numbers ("Gi 1/1,1/5").
                var text = Regex.IsMatch(part, @"^\d") && names.Count > 0 ? Regex.Match(names[^1], @"^\D+\s?").Value + part : part;
                foreach (var expanded in ParseKit.ExpandRange(text))
                    if (ParseKit.Port(vendor, expanded) is { } port) names.Add(port);
            }
            var current = vlans[^1];
            vlans[^1] = current with { Ports = string.Join(", ", new[] { current.Ports }.Concat(names.Select(n => q == "T" ? n + "(T)" : n)).Where(p => p.Length > 0)) };
            foreach (var name in names)
                if (q == "T") tagged.Add(name);
                else if (q is "U" or "A") untagged[name] = current.Id;
        }
        if (vlans.Count == 0) throw new FormatException("Liste des VLAN non reconnue (Dell).");
        return (vlans, untagged, tagged);
    }

    /// <summary>Membership of each port in the Dell OS6 VLAN list: one VLAN = access, several = trunk/general.</summary>
    public static IReadOnlyDictionary<string, string> MembershipVlans(SwitchVendor vendor, IEnumerable<VlanInfo> vlans)
    {
        var members = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var vlan in vlans)
            foreach (var part in vlan.Ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                foreach (var name in ParseKit.ExpandRange(part))
                    if (ParseKit.Port(vendor, name) is { } port)
                        (members.TryGetValue(port, out var list) ? list : members[port] = []).Add(vlan.Id);
        return members.ToDictionary(p => p.Key, p => p.Value.Count == 1 ? p.Value[0].ToString() : "trunk", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"show version" of NX-OS, EOS and Dell OS6/9/10.</summary>
    public static SwitchIdentity Identity(SwitchVendor vendor, string hostname, string version)
    {
        string Find(params string[] patterns)
        {
            foreach (var pattern in patterns)
                if (Regex.Match(version, pattern, Ci | RegexOptions.Multiline) is { Success: true } m) return m.Groups[1].Value.Trim();
            return "";
        }
        var dotted = ParseKit.DottedFields(version);
        var model = vendor switch
        {
            SwitchVendor.CiscoNxos => Find(@"^\s*cisco\s+(Nexus\s*\S+(?:\s+\S+)?)\s+[Cc]hassis", @"^\s*cisco\s+(\S+)\s+[Cc]hassis", @"Hardware\s*\n\s*cisco\s+(.+?)\s+\("),
            SwitchVendor.Arista => Find(@"^\s*Arista\s+(\S+)", @"^\s*Hardware version:\s*(\S+)"),
            SwitchVendor.DellOs6 => dotted.GetValueOrDefault("System Model ID") ?? dotted.GetValueOrDefault("Machine Type") ?? dotted.GetValueOrDefault("Machine Description") ?? "",
            SwitchVendor.DellOs9 => Find(@"^\s*System Type:\s*(\S+)", @"^Dell\s+(?:Networking\s+)?(\S+)\s"),
            SwitchVendor.DellOs10 => Find(@"^\s*System Type:\s*(\S+)", @"^\s*Product\s*:\s*(.+)$"),
            _ => ""
        };
        var os = vendor switch
        {
            SwitchVendor.CiscoNxos => Find(@"^\s*NXOS:\s*version\s+(\S+)", @"^\s*system:\s*version\s+(\S+)", @"\bversion\s+(\d\S*)"),
            SwitchVendor.Arista => Find(@"^\s*Software image version:\s*(\S+)"),
            SwitchVendor.DellOs6 => dotted.GetValueOrDefault("Software Version") ?? Find(@"^\s*\d+\s+(\d+\.\d+\.\d+\.\d+)\s"),
            SwitchVendor.DellOs9 => Find(@"Software Version:\s*(\S+)", @"Dell Application Software Version:\s*(\S+)"),
            SwitchVendor.DellOs10 => Find(@"^\s*OS Version:\s*(\S+)"),
            _ => ""
        };
        var platform = SwitchPlatforms.Get(vendor).ShortName;
        return new(hostname, model.Length == 0 ? $"{platform} (modèle inconnu)" : model, os.Length == 0 ? "Inconnue" : os);
    }
}
