using System.Globalization;
using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Parsing;

/// <summary>
/// Local shell of a UniFi switch (SSH with the device credentials of the site): "info" for the
/// identity and "swctrl port show" / "swctrl mac show" tables. The column layout differs
/// between switch generations, so columns are found by name, never by position. Read-only:
/// the controller overwrites any local change at the next provisioning.
/// </summary>
public static class UniFiShellParser
{
    private static string Cell(IReadOnlyDictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetValue(name, out var value) && value.Length > 0) return value;
        return "";
    }

    private static int? Index(string value) =>
        Regex.Match(value, @"^(?:port\s*)?(\d{1,3})$", RegexOptions.IgnoreCase) is { Success: true } m
        && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is >= 1 and <= 512 and var n ? n : null;

    /// <summary>"info": Model, Version, Hostname (the "Status" line tells whether the controller is reachable).</summary>
    public static SwitchIdentity Identity(string info, string fallbackName)
    {
        var fields = ParseKit.ColonFields(info);
        var name = fields.GetValueOrDefault("Hostname") is { Length: > 0 } host ? host : fallbackName;
        var model = fields.GetValueOrDefault("Model") is { Length: > 0 } m ? m : "UniFi (modèle inconnu)";
        var version = fields.GetValueOrDefault("Version") is { Length: > 0 } v ? v : "Inconnue";
        return new(name, model, version);
    }

    /// <summary>
    /// "swctrl port show": one row per port. Link and admin state, speed, duplex and PVID are
    /// read when their column exists; VLAN membership beyond the PVID is not exposed locally,
    /// so the mode stays unknown.
    /// </summary>
    public static IReadOnlyList<PortInfo> Ports(string output)
    {
        var table = ColumnTable.Parse(output, "Port") ?? throw new FormatException("Tableau « swctrl port show » non reconnu.");
        var result = new List<PortInfo>();
        foreach (var row in table.Rows)
        {
            if (Index(Cell(row, "Port", "Port#", "Port ID")) is not { } index) continue;
            var link = Cell(row, "Link", "Link State", "Status", "State").ToLowerInvariant();
            var admin = Cell(row, "Enable", "Enabled", "Admin", "Admin State").ToLowerInvariant();
            var disabled = admin is "no" or "disable" or "disabled" or "down" || link.Contains("disable") || Regex.IsMatch(link, @"admin[\s-]*down");
            var status = disabled ? "disabled" : link.StartsWith("up", StringComparison.Ordinal) || link.Contains("link up") ? "connected" : "notconnect";
            var pvid = Cell(row, "PVID", "Native VLAN", "VLAN", "Native");
            var vlan = int.TryParse(pvid, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is >= 1 and <= 4094 ? id.ToString(CultureInfo.InvariantCulture) : "";
            var speed = ParseKit.Speed(Cell(row, "Speed", "Link Speed"));
            result.Add(new("Port " + index, Cell(row, "Name", "Description", "Alias"), status, vlan,
                ParseKit.Duplex(Cell(row, "Duplex")), speed.Length == 0 ? "auto" : speed, Cell(row, "Media", "Type")));
        }
        if (result.Count == 0) throw new FormatException("« swctrl port show » ne liste aucun port.");
        return result;
    }

    /// <summary>VLANs seen as a PVID on at least one port (the local shell has no VLAN list).</summary>
    public static IReadOnlyList<VlanInfo> Vlans(IReadOnlyList<PortInfo> ports) =>
        ports.Where(p => p.Vlan.Length > 0).GroupBy(p => int.Parse(p.Vlan, CultureInfo.InvariantCulture)).OrderBy(g => g.Key)
            .Select(g => new VlanInfo(g.Key, "", "active", string.Join(", ", g.Select(p => p.Name)))).ToArray();

    /// <summary>"swctrl mac show": columns VLAN / MAC / Port, whatever their order.</summary>
    public static IReadOnlyList<MacEntry> Macs(string output)
    {
        ColumnTable? table = null;
        foreach (var header in new[] { "VLAN", "VID", "MAC", "Port", "Idx", "Index" })
            if ((table = ColumnTable.Parse(output, header)) is not null) break;
        var result = new List<MacEntry>();
        foreach (var row in table?.Rows ?? [])
        {
            var mac = ParseKit.MacToken.Match(Cell(row, "MAC", "MAC Address", "Mac Addr", "Address"));
            if (!mac.Success || Index(Cell(row, "Port", "Port#", "Interface")) is not { } index) continue;
            var vlan = int.TryParse(Cell(row, "VLAN", "VID", "VLAN ID"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 4094 ? v : 1;
            var type = Cell(row, "Type", "Status").ToLowerInvariant() is "static" or "permanent" or "config" ? "STATIC" : "DYNAMIC";
            result.Add(new(vlan, Cisco.CiscoParser.NormalizeMac(mac.Value), type, "Port " + index));
        }
        if (result.Count == 0 && !Regex.IsMatch(output, @"\bmac\b", RegexOptions.IgnoreCase))
            throw new FormatException("Table MAC UniFi non reconnue ; résultat non considéré comme vide.");
        return result;
    }

    /// <summary>Link state of one port from the "swctrl port show" table; error counters when the firmware prints them.</summary>
    public static InterfaceCounters Counters(string output, string port)
    {
        var table = ColumnTable.Parse(output, "Port") ?? throw new FormatException("Tableau « swctrl port show » non reconnu.");
        var row = table.Rows.FirstOrDefault(r => Index(Cell(r, "Port", "Port#", "Port ID")) is { } i && "Port " + i == port)
            ?? throw new ArgumentException($"{port} absent du switch UniFi.");
        var info = Ports(output).First(p => p.Name == port);
        return new(ParseKit.Long(Cell(row, "CRC", "FCS", "Rx CRC")), null, ParseKit.Long(Cell(row, "Rx Errors", "RxErr", "Rx Err", "In Errors")),
            info.IsUp ? info.Speed : "Inconnue", info.IsUp ? info.Duplex switch { "full" or "a-full" => "Full", "half" or "a-half" => "Half", _ => "Inconnu" } : "Inconnu",
            info.IsUp ? "Actif" : "Inactif", ParseKit.Long(Cell(row, "Tx Errors", "TxErr", "Tx Err", "Out Errors")));
    }
}
