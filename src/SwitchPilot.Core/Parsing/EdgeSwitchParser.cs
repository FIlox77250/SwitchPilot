using System.Text.RegularExpressions;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Parsing;

/// <summary>
/// Ubiquiti EdgeSwitch (Broadcom FASTPATH CLI) read-only outputs. "show port all" is described by
/// a TextFSM template, the format used by the ntc-templates community library.
/// </summary>
public static class EdgeSwitchParser
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const SwitchVendor Vendor = SwitchVendor.UbiquitiEdge;

    //                  Admin     Physical   Physical   Link   Link    LACP   Actor
    //  Intf      Type  Mode      Mode       Status     Status Trap    Mode   Timeout
    // --------- ------ --------- ---------- ---------- ------ ------- ------ --------
    // 0/1              Enable    Auto       1000 Full  Up     Enable  Enable long
    // 0/2              Enable    Auto                  Down   Enable  Enable long
    // 3/1       LAG    Enable    Auto       10G Full   Up     Enable  N/A    N/A
    public const string ShowPortAllTemplate = """
        Value Required PORT (\d{1,2}/\d{1,3})
        Value TYPE (PC Mbr|LAG|Mirror|Probe|\S+)
        Value ADMIN (Enable|Disable|D-Disable)
        Value PHYSICAL_MODE (Auto|\d+G?\s(?:Full|Half))
        Value SPEED (\d+G?)
        Value DUPLEX (Full|Half)
        Value LINK (Up|Down|Detach)

        Start
          ^\s*${PORT}\s+(?:${TYPE}\s+)?${ADMIN}\s+${PHYSICAL_MODE}\s+(?:${SPEED}\s${DUPLEX}\s+)?${LINK}(?:\s|$$) -> Record
        """;

    private static readonly TextFsm PortAllFsm = new(ShowPortAllTemplate);

    /// <summary>"show port all" → ports; descriptions, PVID and mode come from the running configuration when readable.</summary>
    public static IReadOnlyList<PortInfo> PortAll(string output)
    {
        var result = new List<PortInfo>();
        foreach (var record in PortAllFsm.Parse(output))
        {
            if (ParseKit.Port(Vendor, record["PORT"]) is not { } port) continue;
            var status = record["ADMIN"] != "Enable" ? "disabled" : record["LINK"] == "Up" ? "connected" : "notconnect";
            result.Add(new(port, "", status, "", ParseKit.Duplex(record["DUPLEX"]), record["SPEED"].Length > 0 ? ParseKit.Speed(record["SPEED"]) : "auto",
                record["TYPE"] == "LAG" ? "LAG" : ""));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show port all ».");
        return result;
    }

    /// <summary>"show vlan brief": VLAN ID, VLAN Name, VLAN Type (ports are not listed by this command).</summary>
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<id>\d{1,4})\s+(?<name>.*?)\s+(?<type>Default|Static|Dynamic(?:\s*\(\w+\))?)\s*$", Ci);
            if (m.Success) result.Add(new(int.Parse(m.Groups["id"].Value), m.Groups["name"].Value.Trim(), "active", ""));
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue (« show vlan brief »).");
        return result;
    }

    /// <summary>Per-port settings of "show running-config": description, PVID, included and tagged VLANs.</summary>
    public sealed record PortConfig(string Description, int? Pvid, IReadOnlySet<int> Included, IReadOnlySet<int> Tagged, IReadOnlySet<int> Excluded);

    public static IReadOnlyDictionary<string, PortConfig> RunningConfig(string output)
    {
        var result = new Dictionary<string, PortConfig>(StringComparer.OrdinalIgnoreCase);
        string? port = null;
        string description = ""; int? pvid = null;
        HashSet<int> included = [], tagged = [], excluded = [];
        void Flush()
        {
            if (port is not null) result[port] = new(description, pvid, included, tagged, excluded);
            port = null; description = ""; pvid = null; included = []; tagged = []; excluded = [];
        }
        foreach (var raw in ParseKit.Lines(output))
        {
            var line = raw.Trim();
            var head = Regex.Match(line, @"^interface\s+(\d{1,2}/\d{1,3})$", Ci);
            if (head.Success) { Flush(); port = head.Groups[1].Value; continue; }
            if (port is null) continue;
            if (line == "exit") { Flush(); continue; }
            if (Regex.Match(line, @"^description\s+'(.*)'$|^description\s+""(.*)""$|^description\s+(\S.*)$") is { Success: true } d)
                description = d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Success ? d.Groups[2].Value : d.Groups[3].Value;
            else if (Regex.Match(line, @"^vlan pvid (\d+)$") is { Success: true } p) pvid = int.Parse(p.Groups[1].Value);
            else if (Regex.Match(line, @"^vlan participation include ([\d,-]+)$") is { Success: true } inc) included.UnionWith(Ids(inc.Groups[1].Value));
            else if (Regex.Match(line, @"^vlan participation exclude ([\d,-]+)$") is { Success: true } exc) excluded.UnionWith(Ids(exc.Groups[1].Value));
            else if (Regex.Match(line, @"^vlan tagging ([\d,-]+)$") is { Success: true } tag) tagged.UnionWith(Ids(tag.Groups[1].Value));
        }
        Flush();
        return result;
    }

    private static IEnumerable<int> Ids(string list)
    {
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (!int.TryParse(range[0], out var first) || !int.TryParse(range[^1], out var last) || last < first || last - first > 4094) continue;
            for (var id = first; id <= last; id++) yield return id;
        }
    }

    /// <summary>"show vlan port all": Interface, Port VLAN ID (PVID), Acceptable Frame Types…</summary>
    public static Dictionary<string, int> Pvids(string output)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseKit.Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<port>\d{1,2}/\d{1,3})\s+(?<pvid>\d{1,4})\s", Ci);
            if (m.Success) result[m.Groups["port"].Value] = int.Parse(m.Groups["pvid"].Value);
        }
        return result;
    }

    /// <summary>Merges the configuration into the status table: description, access VLAN or trunk.</summary>
    public static IReadOnlyList<PortInfo> Merge(IReadOnlyList<PortInfo> ports, IReadOnlyDictionary<string, PortConfig> config, IReadOnlyDictionary<string, int> pvids)
    {
        return ports.Select(port =>
        {
            config.TryGetValue(port.Name, out var c);
            var pvid = c?.Pvid ?? (pvids.TryGetValue(port.Name, out var p) ? p : (int?)null);
            if (c is null) return pvid is null ? port : port with { Vlan = pvid.Value.ToString(), Mode = "Inconnu" };
            var trunk = c.Tagged.Count > 0;
            // FASTPATH omits "vlan pvid 1": the default PVID is 1.
            return port with { Description = c.Description, Vlan = trunk ? "trunk" : (pvid ?? 1).ToString(), Mode = trunk ? "trunk" : "access" };
        }).ToArray();
    }

    public static SwitchIdentity Identity(string hostname, string version)
    {
        var fields = ParseKit.DottedFields(version);
        var model = fields.GetValueOrDefault("Machine Model") ?? fields.GetValueOrDefault("Machine Type") ?? "EdgeSwitch (modèle inconnu)";
        var software = fields.GetValueOrDefault("Software Version")
            ?? (fields.GetValueOrDefault("System Description") is { } d && Regex.Match(d, @",\s*(\d+\.\d+\.\d+\.\d+)") is { Success: true } m ? m.Groups[1].Value : "Inconnue");
        return new(hostname, model, software);
    }

    /// <summary>"show interface ethernet 0/1" (receive FCS errors, MAC errors, transmit errors, collisions) and the "show port 0/1" row.</summary>
    public static InterfaceCounters Counters(string statistics, string portRow)
    {
        var fields = new List<(string Key, string Value)>();
        foreach (var line in ParseKit.Lines(statistics))
            if (Regex.Match(line, @"^\s*(?<k>[^.\n]*?[^.\s])\s*\.{2,}\s*(?<v>\S+)") is { Success: true } m) fields.Add((m.Groups["k"].Value, m.Groups["v"].Value));
        long? First(string key) => fields.Where(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(f => ParseKit.Long(f.Value)).FirstOrDefault(v => v is not null);
        long? Sum(params string[] keys)
        {
            var values = keys.Select(First).Where(v => v is not null).ToArray();
            return values.Length == 0 ? null : values.Sum();
        }
        var row = PortAll(portRow).FirstOrDefault();
        return new(First("FCS Errors"), Sum("Single Collision Frames", "Multiple Collision Frames", "Excessive Collision Frames"),
            First("Total Packets Received with MAC Errors") ?? First("Total Received Packets Not Forwarded"),
            row is null || row.Speed is "" or "auto" ? "Inconnue" : row.Speed, row is null || row.Duplex.Length == 0 ? "Inconnu" : char.ToUpperInvariant(row.Duplex[0]) + row.Duplex[1..],
            row is null ? "Inconnu" : row.IsUp ? "Actif" : "Inactif", First("Total Transmit Errors"));
    }
}
