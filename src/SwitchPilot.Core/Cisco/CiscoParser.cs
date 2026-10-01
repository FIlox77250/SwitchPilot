using System.Text;
using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Cisco;

public static class CiscoParser
{
    private static readonly Regex PortPattern = new(@"^(?<port>(?:Fa|Gi|Te|Po|FastEthernet|GigabitEthernet|TenGigabitEthernet|Port-channel)\d+(?:/\d+){0,2}|(?:port)?\d+\.\d+(?:\.\d+){0,2})\s+(?<name>.*?)\s*(?<state>connected|notconnect|disabled|err-disabled|inactive|monitoring|sfpAbsent|suspended)\s+(?<vlan>\d+|trunk|routed|unassigned)\s+(?<duplex>a-full|a-half|full|half|auto|unknown)\s+(?<speed>a-\d+|\d+|auto|unknown)\s*(?<type>.*)$", RegexOptions.IgnoreCase);
    public static string Clean(string input)
    {
        var text = Regex.Replace(input, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var b = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '\b') { if (b.Length > 0 && b[^1] != '\n') b.Length--; }
            else if (c != '\r' && (c >= ' ' || c is '\n' or '\t')) b.Append(c);
        }
        return b.ToString().Replace("--More--", "");
    }
    public static string NormalizeInterface(string name)
    {
        var allied = Regex.Match(name.Trim(), @"^(?:port)?(\d+(?:\.\d+){1,3})$", RegexOptions.IgnoreCase);
        if (allied.Success) return "port" + allied.Groups[1].Value;
        var s95 = Regex.Match(name.Trim(), @"^(?:ethernet\s+)?(?:(\d+)/)?(g\d+|ch\d+)$", RegexOptions.IgnoreCase);
        if (s95.Success)
        {
            var unit = s95.Groups[1].Value;
            var port = s95.Groups[2].Value.ToLowerInvariant();
            return unit.Length == 0 || unit == "1" ? port : unit + "/" + port;
        }
        var match = Regex.Match(name.Trim(), @"^(FastEthernet|GigabitEthernet|TenGigabitEthernet|Port-channel|Fa|Gi|Te|Po)(\d+(?:/\d+){0,2})$", RegexOptions.IgnoreCase);
        if (!match.Success) return name.Trim();
        var prefix = match.Groups[1].Value.ToLowerInvariant();
        return (prefix.StartsWith("fa") ? "Fa" : prefix.StartsWith("gi") ? "Gi" : prefix.StartsWith("te") ? "Te" : "Po") + match.Groups[2].Value;
    }
    public static string NormalizeMac(string mac)
    {
        var value = Regex.Replace(mac, "[.:-]", "").ToUpperInvariant();
        if (!Regex.IsMatch(value, "^[0-9A-F]{12}$")) throw new ArgumentException("Adresse MAC invalide.");
        return value;
    }
    public static IReadOnlyList<PortInfo> Ports(string output)
    {
        var result = new List<PortInfo>();
        foreach (var line in Clean(output).Split('\n'))
        {
            var m = PortPattern.Match(line.Trim());
            if (!m.Success) continue;
            var vlan = m.Groups["vlan"].Value;
            result.Add(new(NormalizeInterface(m.Groups["port"].Value), m.Groups["name"].Value.Trim(), m.Groups["state"].Value.ToLowerInvariant(), vlan,
                m.Groups["duplex"].Value, m.Groups["speed"].Value, m.Groups["type"].Value.Trim(), vlan.Equals("trunk", StringComparison.OrdinalIgnoreCase) ? "trunk" : "Inconnu"));
        }
        if (result.Count == 0) throw new FormatException("Aucun port reconnu dans « show interfaces status ». Format IOS inattendu ou autorisation insuffisante.");
        return result;
    }
    public static Dictionary<string, string> SwitchportModes(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? port = null;
        foreach (var line in Clean(output).Split('\n'))
        {
            if (line.StartsWith("Name: ")) port = NormalizeInterface(line[6..].Trim());
            if (port is null) continue;
            if (line.StartsWith("Operational Mode: "))
            {
                var mode = line[18..].Trim();
                if (mode.Contains("trunk", StringComparison.OrdinalIgnoreCase)) result[port] = "trunk";
                else if (mode.Contains("access", StringComparison.OrdinalIgnoreCase)) result[port] = "access";
            }
            if (line.StartsWith("Administrative Mode: "))
            {
                var mode = line[21..].Trim();
                result[port] = mode.Contains("trunk", StringComparison.OrdinalIgnoreCase) ? "trunk" : mode.Contains("access", StringComparison.OrdinalIgnoreCase) ? "access" : mode;
            }
        }
        return result;
    }
    public static Dictionary<string, string> Descriptions(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Clean(output).Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*((?:Fa|Gi|Te|Po)\S+)\s+(?:admin down|up|down|deleted|reset)\s+(?:up|down)\s*(.*)$", RegexOptions.IgnoreCase);
            if (m.Success) result[NormalizeInterface(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
        }
        return result;
    }
    public static IReadOnlyList<MacEntry> Macs(string output)
    {
        var result = new List<MacEntry>();
        foreach (var line in Clean(output).Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*\*?\s*(\d+)\s+([0-9a-f.:-]+)\s+(\S+)\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var vlan)) continue;
            string mac;
            try { mac = NormalizeMac(m.Groups[2].Value); } catch (ArgumentException) { continue; }
            foreach (var port in m.Groups[4].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                if (Regex.IsMatch(port, @"^(?:(?:Fa|Gi|Te|Po|FastEthernet|GigabitEthernet|TenGigabitEthernet|Port-channel)\S*\d|(?:port)?\d+\.\d+)", RegexOptions.IgnoreCase))
                    result.Add(new(vlan, mac, m.Groups[3].Value.ToUpperInvariant(), NormalizeInterface(port)));
        }
        if (result.Count == 0 && !Regex.IsMatch(output, @"Mac Address Table|Mac Address-Table|Total Mac Addresses|No entries|Vlan\s+Mac Address", RegexOptions.IgnoreCase))
            throw new FormatException("Table MAC non reconnue ; résultat non considéré comme vide.");
        return result;
    }
    public static IReadOnlyList<VlanInfo> Vlans(string output)
    {
        var result = new List<VlanInfo>();
        foreach (var line in Clean(output).Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(\d+)\s+(\S+)\s+(active|suspend|act/unsup|sus/unsup)\s*(.*)$");
            if (m.Success) result.Add(new(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value.Trim()));
            else if (result.Count > 0 && Regex.IsMatch(line, @"^\s+(?:Fa|Gi|Te)\d")) result[^1] = result[^1] with { Ports = result[^1].Ports + " " + line.Trim() };
        }
        if (result.Count == 0) throw new FormatException("Liste des VLAN non reconnue.");
        return result;
    }
    public static SwitchIdentity Identity(string hostname, string version)
    {
        var model = Regex.Match(version, @"Model [Nn]umber\s*:\s*(\S+)", RegexOptions.IgnoreCase);
        if (!model.Success) model = Regex.Match(version, @"\bcisco\s+((?:WS-C|C|IE|N|ISR|ASR|ME|CBS)[\w.+-]+)", RegexOptions.IgnoreCase);
        var ios = Regex.Match(version, @"\bVersion\s+([^,\s]+)");
        return new(hostname, model.Success ? model.Groups[1].Value : "Cisco IOS (modèle inconnu)", ios.Success ? ios.Groups[1].Value : "Inconnue");
    }
    public static InterfaceCounters Counters(string output)
    {
        long? Read(string key) { var m = Regex.Match(output, @"(\d+)\s+" + key, RegexOptions.IgnoreCase); return m.Success ? long.Parse(m.Groups[1].Value) : null; }
        var speed = Regex.Match(output, @"(\d+)\s*Mb/s", RegexOptions.IgnoreCase);
        var duplex = Regex.Match(output, @"(Full|Half)[ -]duplex", RegexOptions.IgnoreCase);
        var link = Regex.Match(output, @"(?im)^\S+ is (up|down|administratively down), line protocol is (up|down)");
        var state = !link.Success ? "Inconnu" : link.Groups[1].Value == "up" && link.Groups[2].Value == "up" ? "Actif" : "Inactif";
        return new(Read("CRC"), Read("collisions"), Read("input errors"), speed.Success ? speed.Groups[1].Value : "Inconnue", duplex.Success ? duplex.Groups[1].Value : "Inconnu", state, Read("output errors"));
    }
    public static TdrResult Tdr(string port, string output)
    {
        var pairs = new Dictionary<string, TdrPair>();
        foreach (var line in Clean(output).Split('\n'))
        {
            var id = Regex.Match(line, @"\bPair\s+([A-D])\b", RegexOptions.IgnoreCase);
            if (!id.Success) continue;
            var pair = id.Groups[1].Value.ToUpperInvariant();
            if (pairs.ContainsKey(pair)) throw new FormatException("Résultat TDR ambigu : paire dupliquée.");
            var m = Regex.Match(line, @"Pair\s+[A-D]\s+(.+?)\s+(Pair\s+[A-D]|N/A)\s+(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) { pairs[pair] = new(pair, "Non disponible", "N/A", "Inconnu"); continue; }
            var status = m.Groups[3].Value.Trim();
            var translated = status.ToLowerInvariant() switch
            {
                "normal" => "OK", "open" => "Ouvert", "short" => "Court-circuit",
                "not completed" => "Non terminé", "inprogress" or "in progress" => "En cours",
                "not supported" => "Non pris en charge", "not available" => "Non disponible",
                "impedance mismatch" => "Impédance incorrecte",
                _ when status.StartsWith("Short", StringComparison.OrdinalIgnoreCase) => "Court-circuit — " + status,
                _ => "Inconnu — " + status
            };
            pairs[pair] = new(pair, m.Groups[1].Value.Trim(), m.Groups[2].Value, translated);
        }
        if (pairs.Count == 0) return new(port, [], "Aucun résultat par paire disponible.");
        foreach (var pair in new[] { "A", "B", "C", "D" })
            pairs.TryAdd(pair, new(pair, "Non disponible", "N/A", "Inconnu"));
        return new(port, pairs.Values.OrderBy(p => p.Pair).ToArray(),
            pairs.Values.Any(p => p.Status.StartsWith("Inconnu")) ? "Résultats partiels : certaines paires ne peuvent pas être interprétées." :
            "Longueurs estimées par le switch, avec la tolérance affichée.");
    }
}
