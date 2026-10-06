using System.Text.RegularExpressions;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Parsing;

/// <summary>Huawei VRP read-only outputs (S-series and CloudEngine).</summary>
public static class HuaweiParser
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const SwitchVendor Vendor = SwitchVendor.Huawei;

    /// <summary>"display interface brief": Interface, PHY, Protocol, InUti, OutUti, inErrors, outErrors.</summary>
    public static IReadOnlyList<PortInfo> InterfaceBrief(string output)
    {
        var result = new List<PortInfo>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<port>\S+)\s+(?<phy>\*down|\^down|up|down)(?:\([a-z]\))?\s+(?<proto>\S+)", Ci);
            if (!m.Success || ParseKit.Port(Vendor, m.Groups["port"].Value) is not { } port) continue;
            // Eth-Trunk members are listed indented under their trunk: keep the first occurrence only.
            if (result.Any(p => PortNames.Same(p.Name, port))) continue;
            var status = m.Groups["phy"].Value.ToLowerInvariant() switch { "up" => "connected", "*down" => "disabled", _ => "notconnect" };
            result.Add(new(port, "", status, "", "", "", ""));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « display interface brief ».");
        return result;
    }

    /// <summary>"display interface description": Interface, PHY, Protocol, Description (names may be abbreviated: GE0/0/1).</summary>
    public static Dictionary<string, string> Descriptions(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<port>\S+)\s+(?:\*down|\^down|up|down)(?:\([a-z]\))?\s+(?:\S+)\s?(?<desc>.*)$", Ci);
            if (m.Success && ParseKit.Port(Vendor, m.Groups["port"].Value) is { } port) result[PortNames.Key(port)] = m.Groups["desc"].Value.Trim();
        }
        return result;
    }

    /// <summary>"display port vlan": Port, Link Type, PVID, Trunk VLAN List → (mode, vlan) by port key.</summary>
    public static Dictionary<string, (string Mode, string Vlan)> PortVlans(string output)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<port>\S+)\s+(?<type>access|trunk|hybrid|dot1q-tunnel|qinq|negotiation-\S+|desirable|auto)\s+(?<pvid>\d+|-)", Ci);
            if (!m.Success || ParseKit.Port(Vendor, m.Groups["port"].Value) is not { } port) continue;
            var type = m.Groups["type"].Value.ToLowerInvariant();
            var mode = type == "access" ? "access" : type is "trunk" or "hybrid" ? "trunk" : type;
            result[PortNames.Key(port)] = (mode, mode == "access" ? m.Groups["pvid"].Value : "trunk");
        }
        return result;
    }

    /// <summary>"display vlan": the membership table (VID Type Ports) and the property table (VID Status … Description).</summary>
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var ports = new Dictionary<int, List<string>>();
        var vlans = new List<VlanInfo>();
        int? current = null;
        var inProperties = false;
        foreach (var line in ParseKit.Lines(output))
        {
            if (Regex.IsMatch(line, @"^\s*VID\s+Status\b", Ci)) { inProperties = true; current = null; continue; }
            if (Regex.IsMatch(line, @"^\s*VID\s+Type\b", Ci)) { inProperties = false; continue; }
            if (inProperties)
            {
                var p = Regex.Match(line, @"^\s*(?<id>\d{1,4})\s+(?<status>enable|disable)\s+\S+\s+\S+\s+\S+\s*(?<desc>.*)$", Ci);
                if (p.Success && int.TryParse(p.Groups["id"].Value, out var vid))
                {
                    var desc = p.Groups["desc"].Value.Trim();
                    vlans.Add(new(vid, desc.Length == 0 ? $"VLAN {vid:0000}" : desc, p.Groups["status"].Value.Equals("enable", StringComparison.OrdinalIgnoreCase) ? "active" : "inactive",
                        string.Join(", ", ports.GetValueOrDefault(vid) ?? [])));
                }
                continue;
            }
            var row = Regex.Match(line, @"^\s*(?<id>\d{1,4})\s+(?<type>common|super|sub|mux|smart|\S+)\s+(?<members>.*)$", Ci);
            if (row.Success && int.TryParse(row.Groups["id"].Value, out var id)) { current = id; Members(id, row.Groups["members"].Value); continue; }
            if (current is { } open && Regex.IsMatch(line, @"^\s{8,}\S")) Members(open, line);
        }
        void Members(int id, string text)
        {
            var list = ports.TryGetValue(id, out var l) ? l : ports[id] = [];
            var tagged = false;
            foreach (Match token in Regex.Matches(text, @"(?:(?<q>UT|TG):)?(?<port>[A-Za-z][\w-]*\d[\d/]*)(?:\([A-Z]\))?", Ci))
            {
                if (token.Groups["q"].Success) tagged = token.Groups["q"].Value.Equals("TG", StringComparison.OrdinalIgnoreCase);
                if (ParseKit.Port(Vendor, token.Groups["port"].Value) is { } port) list.Add(tagged ? port + "(T)" : port);
            }
        }
        // Firmware without the property table: names unknown, membership only.
        if (vlans.Count == 0) vlans.AddRange(ports.Select(p => new VlanInfo(p.Key, $"VLAN {p.Key:0000}", "active", string.Join(", ", p.Value))));
        if (vlans.Count == 0) throw new FormatException("Liste des VLAN non reconnue (« display vlan »).");
        return vlans;
    }

    public static SwitchIdentity Identity(string hostname, string version)
    {
        // Same line only: the banner starts with "Huawei Versatile Routing Platform Software".
        var model = Regex.Match(version, @"^[ \t]*(?:HUAWEI|Quidway)[ \t]+(\S+)(?:[ \t]+\S+)*?[ \t]+uptime is", Ci | RegexOptions.Multiline);
        var vrp = Regex.Match(version, @"\b(V\d{3}R\d{3}\w*)", Ci);
        var number = Regex.Match(version, @"\bVersion\s+(\d+\.\d+)", Ci);
        return new(hostname, model.Success ? model.Groups[1].Value : "Huawei VRP (modèle inconnu)",
            vrp.Success ? vrp.Groups[1].Value : number.Success ? number.Groups[1].Value : "Inconnue");
    }

    /// <summary>"display interface X": current state, Speed, Duplex, CRC, Collisions, Total Error (input / output).</summary>
    public static InterfaceCounters Counters(string output)
    {
        long? Field(string text, string key) => Regex.Match(text, Regex.Escape(key) + @"\s*:\s*(\d+)", Ci) is { Success: true } m ? ParseKit.Long(m.Groups[1].Value) : null;
        var inputIndex = Regex.Match(output, @"^\s*Input\s*:", Ci | RegexOptions.Multiline);
        var outputIndex = Regex.Match(output, @"^\s*Output\s*:", Ci | RegexOptions.Multiline);
        var input = inputIndex.Success ? output[inputIndex.Index..(outputIndex.Success && outputIndex.Index > inputIndex.Index ? outputIndex.Index : output.Length)] : "";
        var outputPart = outputIndex.Success ? output[outputIndex.Index..] : "";
        var state = Regex.Match(output, @"current state\s*:\s*(Administratively DOWN|UP|DOWN)", Ci);
        var speed = Regex.Match(output, @"\bSpeed\s*:\s*(\d+|AUTO)", Ci);
        var duplex = Regex.Match(output, @"\bDuplex\s*:\s*(FULL|HALF|AUTO)", Ci);
        return new(Field(output, "CRC"), Field(output, "Collisions"), input.Length > 0 ? Field(input, "Total Error") : null,
            speed.Success ? ParseKit.Speed(speed.Groups[1].Value) : "Inconnue",
            duplex.Success ? char.ToUpperInvariant(duplex.Groups[1].Value[0]) + duplex.Groups[1].Value[1..].ToLowerInvariant() : "Inconnu",
            !state.Success ? "Inconnu" : state.Groups[1].Value.Equals("UP", StringComparison.OrdinalIgnoreCase) ? "Actif" : "Inactif",
            outputPart.Length > 0 ? Field(outputPart, "Total Error") : null);
    }
}
