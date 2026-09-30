using System.Text.RegularExpressions;
using SwitchPilot.Core.Cisco;

namespace SwitchPilot.Core.Allied;

/// <summary>
/// Parsers for Allied Telesis AlliedWare Plus output. The port inventory and MAC table
/// share the Cisco layout, so those reuse <see cref="CiscoParser"/>; the identity, VLAN
/// and counter layouts differ and are handled here.
/// </summary>
public static class AlliedTelesisParser
{
    /// <summary>`show version` on AlliedWare Plus.</summary>
    public static SwitchIdentity Identity(string hostname, string version)
    {
        var clean = CiscoParser.Clean(version);
        var software = Regex.Match(clean, @"version\s+([0-9][^\s,]*)", RegexOptions.IgnoreCase);
        if (!software.Success)
            software = Regex.Match(clean, @"(?:AlliedWare Plus|AW\+)[^\r\n]*?([0-9]+\.[0-9][0-9A-Za-z._()-]*)", RegexOptions.IgnoreCase);
        return new(hostname, Model(clean) ?? "Allied Telesis (modèle inconnu)", software.Success ? software.Groups[1].Value : "Inconnue");
    }

    /// <summary>Board/model line as shown by `show system` or, on some versions, `show version`.</summary>
    public static string? Model(string output) =>
        Regex.Match(CiscoParser.Clean(output), @"(?im)^\s*(?:model|board|hardware|device)(?:\s+(?:name|number|type))?\s*[:\s]\s*(\S+)").Groups[1].Value.TrimEnd(':') is { Length: > 0 } model ? model : null;

    /// <summary>
    /// `show mac address-table` on AlliedWare Plus: `VLAN port mac fwd` (port before the MAC,
    /// an extra forwarding column, type last). This is the opposite of Cisco, so the Cisco
    /// parser must not be reused here.
    /// </summary>
    public static IReadOnlyList<MacEntry> Macs(string output)
    {
        var result = new List<MacEntry>();
        foreach (var raw in CiscoParser.Clean(output).Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^(?<vlan>\d+)\s+(?<port>\S+)\s+(?<mac>[0-9a-fA-F]{4}[.:-][0-9a-fA-F]{4}[.:-][0-9a-fA-F]{4})\s+(?<rest>.+)$");
            if (!m.Success) continue;
            var portToken = m.Groups["port"].Value;
            // CPU / router MAC entries are not learned on a physical port.
            if (portToken.Equals("CPU", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(portToken, @"^(?:port)?\d+(?:\.\d+)+$", RegexOptions.IgnoreCase)) continue;
            string mac;
            try { mac = CiscoParser.NormalizeMac(m.Groups["mac"].Value); } catch (ArgumentException) { continue; }
            var rest = m.Groups["rest"].Value;
            var type = Regex.IsMatch(rest, "dynamic", RegexOptions.IgnoreCase) ? "DYNAMIC"
                : Regex.IsMatch(rest, "static", RegexOptions.IgnoreCase) ? "STATIC" : rest.Trim().ToUpperInvariant();
            result.Add(new(int.Parse(m.Groups["vlan"].Value), mac, type, CiscoParser.NormalizeInterface(portToken)));
        }
        if (result.Count == 0 && !Regex.IsMatch(output, @"mac address-table|mac-address|Mac Address|No entries|Total Mac", RegexOptions.IgnoreCase))
            throw new FormatException("Table MAC AlliedWare Plus non reconnue ; résultat non considéré comme vide.");
        return result;
    }

    /// <summary>`show vlan brief` on AlliedWare Plus: VLAN ID, Name, Type, State, Member ports.</summary>
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var raw in CiscoParser.Clean(output).Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^(?<id>\d+)\s+(?<name>\S+)\s+(?<rest>.*)$");
            if (m.Success)
            {
                var rest = m.Groups["rest"].Value.Trim();
                var state = Regex.Match(rest, @"\b(ACTIVE|INACTIVE|SUSPEND(?:ED)?|ACT/UNSUP|SUS/UNSUP)\b", RegexOptions.IgnoreCase);
                var status = state.Success ? state.Value.ToUpperInvariant() : "ACTIVE";
                // Always scan the whole tail: non-port tokens (type, state) are filtered out.
                result.Add(new(int.Parse(m.Groups["id"].Value), m.Groups["name"].Value, status, NormalizePorts(rest)));
                continue;
            }
            // Long member-port lists wrap onto indented continuation lines; keep their ports.
            if (result.Count > 0 && Regex.IsMatch(raw, @"^\s+\S"))
            {
                var extra = NormalizePorts(raw);
                if (extra.Length > 0) result[^1] = result[^1] with { Ports = result[^1].Ports.Length == 0 ? extra : result[^1].Ports + ", " + extra };
            }
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue dans « show vlan brief » (AlliedWare Plus).");
        return result;
    }

    private static string NormalizePorts(string value)
    {
        var ports = new List<string>();
        foreach (var token in value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var port = Regex.Replace(token, @"\((?:u|t)\)$", "", RegexOptions.IgnoreCase).Trim();
            if (port.Length == 0) continue;
            if (!Regex.IsMatch(port, @"^(?:port)?\d+(?:\.\d+)+$", RegexOptions.IgnoreCase)) continue;
            ports.Add(CiscoParser.NormalizeInterface(port));
        }
        return string.Join(", ", ports);
    }

    /// <summary>
    /// Derives access/trunk per port from the (u)/(t) membership annotations of
    /// `show vlan brief`, which AlliedWare Plus emits instead of a switchport mode column.
    /// </summary>
    public static Dictionary<string, string> PortModes(string vlanOutput)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Scan the whole output, not only VLAN-id lines: AW+ wraps long member lists onto
        // indented continuation lines whose (u)/(t) annotation must still be honoured.
        foreach (Match token in Regex.Matches(CiscoParser.Clean(vlanOutput), @"(?<port>(?:port)?\d+(?:\.\d+)+)\((?<tag>[ut])\)", RegexOptions.IgnoreCase))
        {
            var port = CiscoParser.NormalizeInterface(token.Groups["port"].Value);
            if (token.Groups["tag"].Value.Equals("t", StringComparison.OrdinalIgnoreCase)) result[port] = "trunk";
            else if (!result.TryGetValue(port, out var existing) || existing != "trunk") result[port] = "access";
        }
        return result;
    }

    /// <summary>`show interface &lt;port&gt;` on AlliedWare Plus. Returns null counters when a field is absent.</summary>
    public static InterfaceCounters Counters(string output)
    {
        var clean = CiscoParser.Clean(output);
        long? Read(string pattern)
        {
            var m = Regex.Match(clean, pattern, RegexOptions.IgnoreCase);
            return m.Success && long.TryParse(m.Groups[1].Value, out var value) ? value : null;
        }
        var speed = Regex.Match(clean, @"(\d+)\s*(?:Mbps|Mb/s)", RegexOptions.IgnoreCase);
        if (!speed.Success) speed = Regex.Match(clean, @"[Ss]peed[^0-9\r\n]{0,16}(\d+)");
        var duplex = Regex.Match(clean, @"([Ff]ull|[Hh]alf)[ -]?[Dd]uplex");
        if (!duplex.Success) duplex = Regex.Match(clean, @"[Dd]uplex[^A-Za-z\r\n]{0,16}([Ff]ull|[Hh]alf)");
        var link = Regex.Match(clean, @"(?im)^\s*Link is\s+(UP|DOWN)");
        var ciscoLink = Regex.Match(clean, @"(?im)^\S+ is (up|down|administratively down), line protocol is (up|down)");
        var state = link.Success ? link.Groups[1].Value.Equals("UP", StringComparison.OrdinalIgnoreCase) ? "Actif" : "Inactif"
            : ciscoLink.Success ? ciscoLink.Groups[1].Value == "up" && ciscoLink.Groups[2].Value == "up" ? "Actif" : "Inactif"
            : "Inconnu";
        return new(Read(@"CRC\s*[:=]?\s*(\d+)"), Read(@"collisions?\s*[:=]?\s*(\d+)"),
            Read(@"input errors\s*[:=]?\s*(\d+)"), speed.Success ? speed.Groups[1].Value : "Inconnue",
            duplex.Success ? duplex.Groups[1].Value : "Inconnu", state, Read(@"output errors\s*[:=]?\s*(\d+)"));
    }
}
