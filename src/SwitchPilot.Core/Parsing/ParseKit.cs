using System.Globalization;
using System.Text.RegularExpressions;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Parsing;

/// <summary>Small helpers shared by the vendor parsers.</summary>
public static class ParseKit
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    public static readonly Regex MacToken = new(@"(?<![0-9A-Fa-f:.-])(?:[0-9A-Fa-f]{4}[.-][0-9A-Fa-f]{4}[.-][0-9A-Fa-f]{4}|(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})(?![0-9A-Fa-f:.-])", RegexOptions.CultureInvariant);

    public static string[] Lines(string output) => CiscoParser.Clean(output).Split('\n');

    public static long? Long(string? value) =>
        long.TryParse((value ?? "").Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"10G" → "10000", "a-1G" → "a-1000", "1000M" / "1000 Mbit" / "1Gbps" → "1000"; anything else unchanged.</summary>
    public static string Speed(string? raw)
    {
        var text = (raw ?? "").Trim();
        var m = Regex.Match(text, @"^(?<a>a-)?(?<n>\d+(?:\.\d+)?)\s*(?<u>[MGT])?(?:b/s|bps|bit|bit/s)?$", Ci);
        if (!m.Success) return text.Length == 0 ? "" : text.ToLowerInvariant() is "auto" or "unknown" or "n/a" ? text.ToLowerInvariant() : text;
        var value = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) * (m.Groups["u"].Value.ToUpperInvariant() switch { "G" => 1000, "T" => 1_000_000, _ => 1 });
        return m.Groups["a"].Value + ((long)value).ToString(CultureInfo.InvariantCulture);
    }

    public static string Duplex(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "full" or "full-duplex" or "yes" or "fdx" => "full",
        "half" or "half-duplex" or "no" or "hdx" => "half",
        "a-full" => "a-full",
        "a-half" => "a-half",
        "auto" => "auto",
        "" => "",
        var other => other
    };

    /// <summary>Canonical port name for <paramref name="vendor"/>, or null when the token is not a port of that platform.</summary>
    public static string? Port(SwitchVendor vendor, string? token) =>
        PortNames.TryValidate(vendor, token, out var port) ? port : null;

    /// <summary>
    /// Expands "Gi1/0/1-24", "Gi 1/3-4", "Eth1/1/2-1/1/4", "Po1-128" into port names; a plain
    /// name is returned alone. Never more than <paramref name="limit"/> names per range.
    /// </summary>
    public static IEnumerable<string> ExpandRange(string token, int limit = 512)
    {
        token = token.Trim().TrimEnd(',');
        var m = Regex.Match(token, @"^(?<head>.*?)(?<first>\d+)-(?<tail>(?:\d+/)*)(?<last>\d+)$");
        if (!m.Success) { if (token.Length > 0) yield return token; yield break; }
        var head = m.Groups["head"].Value;
        var tail = m.Groups["tail"].Value;
        // "Eth1/1/2-1/1/4": the right side repeats the slot prefix of the left side.
        if (tail.Length > 0 && !Regex.Replace(head, @"^\D+\s?", "").Equals(tail, StringComparison.Ordinal)) { yield return token; yield break; }
        if (!int.TryParse(m.Groups["first"].Value, out var first) || !int.TryParse(m.Groups["last"].Value, out var last) || last < first || last - first >= limit)
        { yield return token; yield break; }
        for (var i = first; i <= last; i++) yield return head + i;
    }

    /// <summary>Fields of FASTPATH-style "Key........ value" listings (EdgeSwitch, Dell OS6).</summary>
    public static Dictionary<string, string> DottedFields(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<k>[^.\n]*?[^.\s])\s*\.{2,}\s*(?<v>.*?)\s*$");
            if (m.Success) result.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value);
        }
        return result;
    }

    /// <summary>Fields of "key: value" listings (RouterOS print, Junos show version, UniFi info).</summary>
    public static Dictionary<string, string> ColonFields(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(?<k>[A-Za-z][\w .()/-]*?)\s*:\s*(?<v>.*?)\s*$");
            if (m.Success) result.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value);
        }
        return result;
    }

    /// <summary>
    /// Generic MAC table: one entry per line holding a MAC address, a VLAN number before it
    /// (or right after it, "10/-/-" on Huawei) and a port of <paramref name="vendor"/> after it.
    /// Ports may be split in two tokens ("Gi 1/1") and carry a logical unit ("ge-0/0/1.0").
    /// </summary>
    public static IReadOnlyList<MacEntry> Macs(SwitchVendor vendor, string output, string emptyMarker = @"\bmac\b")
    {
        var result = new List<MacEntry>();
        foreach (var line in Lines(output))
        {
            var mac = MacToken.Match(line);
            if (!mac.Success) continue;
            var before = Regex.Matches(line[..mac.Index], @"\S+").Select(t => t.Value).ToArray();
            var after = Regex.Matches(line[(mac.Index + mac.Length)..], @"\S+").Select(t => t.Value).ToArray();
            int? vlan = null;
            for (var i = before.Length - 1; i >= 0 && vlan is null; i--)
                if (int.TryParse(before[i], out var v) && v is >= 1 and <= 4094) vlan = v;
            var used = -1;
            if (vlan is null && after.Length > 0 && Regex.Match(after[0], @"^(\d{1,4})(?:/|$)") is { Success: true } first && int.Parse(first.Groups[1].Value) is >= 1 and <= 4094)
            { vlan = int.Parse(first.Groups[1].Value); used = 0; }
            string? port = null;
            for (var i = used + 1; i < after.Length && port is null; i++)
            {
                var token = Regex.Replace(after[i], @"\.\d+$|\([A-Za-z]\)$", "");
                if (i + 1 < after.Length && Regex.IsMatch(after[i], "^[A-Za-z][A-Za-z-]*$")) port = Port(vendor, after[i] + " " + after[i + 1]);
                port ??= Regex.IsMatch(token, @"^\d+$") ? null : Port(vendor, token);
            }
            if (vlan is null || port is null) continue;
            var type = after.FirstOrDefault(t => Regex.IsMatch(t, "^(?:dynamic|static|learned|sticky|secure|security|self|management|config|permanent|D|S)$", Ci)) ?? "DYNAMIC";
            type = type.ToUpperInvariant() switch { "D" or "LEARNED" => "DYNAMIC", "S" or "PERMANENT" or "CONFIG" => "STATIC", var t => t };
            result.Add(new(vlan.Value, CiscoParser.NormalizeMac(mac.Value), type, port));
        }
        if (result.Count == 0 && !Regex.IsMatch(output, emptyMarker, Ci))
            throw new FormatException("Table MAC non reconnue ; résultat non considéré comme vide.");
        return result;
    }

    /// <summary>
    /// Shared tolerant interface counters: "N input errors", "N CRC", "N collisions",
    /// "N output errors", "1 Gb/s" / "1000 Mbit" / "LineSpeed 10G", "full-duplex" / "Mode full duplex",
    /// "X is up[, line protocol is up]".
    /// </summary>
    public static InterfaceCounters Counters(string output)
    {
        long? Read(string key) { var m = Regex.Match(output, @"(\d+)\s+" + key + @"\b", Ci); return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null; }
        var speed = Regex.Match(output, @"\b(\d+(?:\.\d+)?)\s*([MG])(?:b/s|bit|bps)\b", Ci);
        if (!speed.Success) speed = Regex.Match(output, @"LineSpeed\s+(\d+)\s*([MG])?", Ci);
        var speedText = !speed.Success ? "Inconnue" : Speed(speed.Groups[1].Value + speed.Groups[2].Value);
        if (speedText == "0") speedText = "Inconnue";
        var duplex = Regex.Match(output, @"\b(full|half)[ -]duplex\b", Ci);
        var link = Regex.Match(output, @"(?im)^\S+(?:\s\d\S*)? is (up|down|administratively down|admin down|disabled)(?:,\s*line protocol is (up|down))?");
        var state = !link.Success ? "Inconnu"
            : link.Groups[1].Value.Equals("up", StringComparison.OrdinalIgnoreCase) && (!link.Groups[2].Success || link.Groups[2].Value.Equals("up", StringComparison.OrdinalIgnoreCase)) ? "Actif" : "Inactif";
        return new(Read("CRC"), Read("collisions?"), Read("input errors?"),
            speedText, duplex.Success ? char.ToUpperInvariant(duplex.Groups[1].Value[0]) + duplex.Groups[1].Value[1..].ToLowerInvariant() : "Inconnu",
            state, Read("output errors?"));
    }

    /// <summary>Generic "VLAN Name Status Ports" listing (show vlan brief on NX-OS, EOS…).</summary>
    public static IReadOnlyList<VlanInfo> VlanBrief(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var line in Lines(output))
        {
            var m = Regex.Match(line, @"^\s*(\d{1,4})\s+(\S+)\s+(active|act/\S+|suspend\S*|sus/\S+)\s*(.*)$", Ci);
            if (m.Success) result.Add(new(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value.Trim()));
            else if (result.Count > 0 && Regex.IsMatch(line, @"^\s{8,}[A-Za-z]+[\d/ ]")) result[^1] = result[^1] with { Ports = (result[^1].Ports + " " + line.Trim()).Trim() };
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue.");
        return result;
    }
}
