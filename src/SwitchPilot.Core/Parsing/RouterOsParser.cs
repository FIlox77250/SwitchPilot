using System.Text;
using System.Text.RegularExpressions;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Parsing;

/// <summary>One row of a RouterOS "print terse" listing: index, flags and key=value properties.</summary>
public sealed record RouterOsRow(int Index, string Flags, IReadOnlyDictionary<string, string> Values)
{
    public string this[string key] => Values.TryGetValue(key, out var v) ? v : "";
    public bool Has(char flag) => Flags.Contains(flag);
}

/// <summary>MikroTik RouterOS 7 read-only outputs ("print terse without-paging", "monitor … once").</summary>
public static class RouterOsParser
{
    private const SwitchVendor Vendor = SwitchVendor.MikroTik;

    /// <summary>Parses a "print terse" listing; quoted values may contain spaces and escaped quotes.</summary>
    public static IReadOnlyList<RouterOsRow> Terse(string output)
    {
        var rows = new List<RouterOsRow>();
        foreach (var line in ParseKit.Lines(output))
        {
            var head = Regex.Match(line, @"^\s*(?<idx>\d+)\s+(?<flags>(?:[A-Za-z*+;]\s*)*?)(?=[a-z][\w-]*=)");
            if (!head.Success) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = line[(head.Index + head.Length)..];
            var i = 0;
            string? previous = null;
            while (i < text.Length)
            {
                while (i < text.Length && text[i] == ' ') i++;
                var eq = text.IndexOf('=', i);
                // Read-only values are printed unquoted even with a space ("last-link-up-time=2026-10-01 10:00:00"):
                // words before the next "key=" still belong to the previous value.
                var key = eq < 0 ? text[i..] : text[i..eq];
                var space = eq < 0 ? key.Length : key.LastIndexOf(' ');
                if (space > 0 && previous is not null) values[previous] += " " + key[..space].TrimEnd();
                if (eq < 0) break;
                if (space > 0) key = key[(space + 1)..];
                i = eq + 1;
                var value = new StringBuilder();
                if (i < text.Length && text[i] == '"')
                {
                    for (i++; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) i++;
                        value.Append(text[i]);
                    }
                    i++;
                }
                else while (i < text.Length && text[i] != ' ') value.Append(text[i++]);
                if (Regex.IsMatch(key, @"^[a-z][\w-]*$")) values[previous = key] = value.ToString();
                else previous = null;
            }
            rows.Add(new(int.Parse(head.Groups["idx"].Value), Regex.Replace(head.Groups["flags"].Value, @"\s", ""), values));
        }
        return rows;
    }

    /// <summary>
    /// Ports from "/interface print terse" (ethernet and bonding), with PVID / frame types from
    /// "/interface bridge port print terse" and tagged membership from "/interface bridge vlan print terse".
    /// </summary>
    public static IReadOnlyList<PortInfo> Ports(string interfaces, string bridgePorts, string bridgeVlans)
    {
        var bridge = Terse(bridgePorts).Where(r => r["interface"].Length > 0).GroupBy(r => r["interface"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vlan in Terse(bridgeVlans))
            foreach (var name in (vlan["tagged"] + "," + vlan["current-tagged"]).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tagged.Add(name);
        var result = new List<PortInfo>();
        foreach (var row in Terse(interfaces))
        {
            if (row["type"] is not ("ether" or "bond") || ParseKit.Port(Vendor, row["name"]) is not { } port) continue;
            var status = row.Has('X') ? "disabled" : row.Has('R') ? "connected" : "notconnect";
            string vlan = "", mode = "Inconnu";
            if (bridge.TryGetValue(port, out var member))
            {
                var frames = member["frame-types"];
                mode = frames == "admit-only-vlan-tagged" || frames == "admit-all" && tagged.Contains(port) ? "trunk" : "access";
                vlan = mode == "trunk" ? "trunk" : member["pvid"].Length > 0 ? member["pvid"] : "1";
            }
            result.Add(new(port, row["comment"], status, vlan, "", "", row["type"] == "bond" ? "bonding" : "", mode));
        }
        if (result.Count == 0) throw new FormatException("Aucun port Ethernet reconnu dans « /interface print terse ».");
        return result;
    }

    /// <summary>"/interface bridge vlan print terse": one VLAN per id ("vlan-ids=20,30" or "100-110" expanded, 512 at most).</summary>
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var result = new Dictionary<int, VlanInfo>();
        foreach (var row in Terse(output))
        {
            var ports = string.Join(", ", (row["current-untagged"].Length > 0 ? row["current-untagged"] : row["untagged"]).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Concat((row["current-tagged"].Length > 0 ? row["current-tagged"] : row["tagged"]).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p + "(T)")));
            foreach (var part in row["vlan-ids"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var range = part.Split('-');
                if (!int.TryParse(range[0], out var first) || !int.TryParse(range[^1], out var last) || last < first || last - first > 511) continue;
                for (var id = first; id <= last; id++)
                    if (id is >= 1 and <= 4094 && !result.ContainsKey(id))
                        result[id] = new(id, row["comment"].Length > 0 ? row["comment"] : id == 1 ? "default" : $"vlan{id}", row.Has('X') ? "inactive" : "active", ports);
            }
        }
        return result.Values.OrderBy(v => v.Id).ToArray();
    }

    /// <summary>"/interface bridge host print terse": learned hosts (local entries of the switch itself are skipped).</summary>
    public static IReadOnlyList<MacEntry> Macs(string output)
    {
        var result = new List<MacEntry>();
        foreach (var row in Terse(output))
        {
            if (row.Has('L') || ParseKit.Port(Vendor, row["on-interface"]) is not { } port) continue;
            string mac;
            try { mac = CiscoParser.NormalizeMac(row["mac-address"]); } catch (ArgumentException) { continue; }
            var vlan = int.TryParse(row["vid"], out var vid) && vid is >= 1 and <= 4094 ? vid : 1;
            result.Add(new(vlan, mac, row.Has('D') ? "DYNAMIC" : "STATIC", port));
        }
        return result;
    }

    public static SwitchIdentity Identity(string identity, string resource, string fallbackName)
    {
        var name = ParseKit.ColonFields(identity).GetValueOrDefault("name");
        var fields = ParseKit.ColonFields(resource);
        return new(string.IsNullOrWhiteSpace(name) ? fallbackName : name, fields.GetValueOrDefault("board-name") ?? "RouterOS (modèle inconnu)",
            fields.GetValueOrDefault("version") ?? "Inconnue");
    }

    /// <summary>"/interface ethernet monitor X once" plus "/interface ethernet print stats where name=X".</summary>
    public static InterfaceCounters Counters(string monitor, string stats)
    {
        var m = ParseKit.ColonFields(monitor);
        long? Stat(string key) => Regex.Match(stats, @"(?:^|\s)" + Regex.Escape(key) + @"\s*[:=]\s*""?([\d ]+)", RegexOptions.Multiline) is { Success: true } s
            ? ParseKit.Long(s.Groups[1].Value.Replace(" ", "")) : null;
        var status = m.GetValueOrDefault("status") ?? "";
        var rate = m.GetValueOrDefault("rate");
        var full = m.GetValueOrDefault("full-duplex");
        return new(Stat("rx-fcs-error"), Stat("tx-collision"), Stat("rx-error") ?? Stat("rx-fcs-error"),
            rate is null ? "Inconnue" : ParseKit.Speed(rate), full is null ? "Inconnu" : full == "yes" ? "Full" : "Half",
            status.Length == 0 ? "Inconnu" : status == "link-ok" ? "Actif" : "Inactif", Stat("tx-error"));
    }
}
