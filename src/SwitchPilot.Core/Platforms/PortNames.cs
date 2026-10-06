using System.Text.RegularExpressions;
using SwitchPilot.Core.Cisco;

namespace SwitchPilot.Core.Platforms;

/// <summary>
/// Per-platform interface grammar. Every port name that reaches a command line is validated
/// against the grammar of the connected platform and rewritten into its canonical display form
/// (what the GUI shows) and command form (what the CLI receives). <see cref="Key"/> gives a
/// vendor-neutral comparison key so safety checks match "Gi0/1" with "GigabitEthernet0/1",
/// "Eth 1/1/1" with "ethernet1/1/1" or "GE0/0/1" with "GigabitEthernet0/0/1".
/// </summary>
public static class PortNames
{
    private sealed record Grammar(Regex Pattern, Func<Match, string> Display, Func<string, string> Command);

    private static Regex R(string pattern) => new("^(?:" + pattern + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Same(string port) => port;

    private static readonly Dictionary<string, string> HuaweiTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ge"] = "GE", ["gigabitethernet"] = "GigabitEthernet", ["xge"] = "XGE", ["xgigabitethernet"] = "XGigabitEthernet",
        ["eth"] = "Eth", ["ethernet"] = "Ethernet", ["10ge"] = "10GE", ["25ge"] = "25GE", ["40ge"] = "40GE", ["100ge"] = "100GE",
        ["multige"] = "MultiGE", ["eth-trunk"] = "Eth-Trunk", ["meth"] = "MEth"
    };

    private static readonly Dictionary<SwitchVendor, Grammar> Grammars = new()
    {
        // NX-OS: status tables print "Eth1/1"; the configuration accepts the full keyword.
        [SwitchVendor.CiscoNxos] = new(R(@"(?<t>e|eth|ethernet)\s*(?<n>\d+/\d+(?:/\d+)?)|(?<t>po|port-channel)\s*(?<n>\d+)|(?<t>mgmt)\s*(?<n>\d+)"),
            m => Type(m) switch { "po" or "port-channel" => "Po", "mgmt" => "mgmt", _ => "Eth" } + m.Groups["n"].Value,
            p => p.StartsWith("Eth") ? "Ethernet" + p[3..] : p.StartsWith("Po") ? "port-channel" + p[2..] : p),
        [SwitchVendor.Arista] = new(R(@"(?<t>et|ethernet)\s*(?<n>\d+(?:/\d+){0,2})|(?<t>po|port-channel)\s*(?<n>\d+)|(?<t>ma|management)\s*(?<n>\d+(?:/\d+)?)"),
            m => Type(m) switch { "po" or "port-channel" => "Po", "ma" or "management" => "Ma", _ => "Et" } + m.Groups["n"].Value,
            p => p.StartsWith("Et") ? "Ethernet" + p[2..] : p.StartsWith("Po") ? "Port-Channel" + p[2..] : "Management" + p[2..]),
        // Dell OS6 (N-Series) accepts the short "gi1/0/1" form in configuration mode.
        [SwitchVendor.DellOs6] = new(R(@"(?<t>gi|gigabitethernet|te|tengigabitethernet|fo|fortygigabitethernet|tw|twentyfivegigabitethernet|hu|hundredgigabitethernet)\s*(?<n>\d+/\d+/\d+)|(?<t>po|port-channel)\s*(?<n>\d+)"),
            m => Type(m) switch
            {
                "gi" or "gigabitethernet" => "Gi", "te" or "tengigabitethernet" => "Te", "fo" or "fortygigabitethernet" => "Fo",
                "tw" or "twentyfivegigabitethernet" => "Tw", "hu" or "hundredgigabitethernet" => "Hu", _ => "Po"
            } + m.Groups["n"].Value,
            Same),
        // Dell OS9 (FTOS) separates the type and the number with a space.
        [SwitchVendor.DellOs9] = new(R(@"(?<t>gi|gigabitethernet|te|tengigabitethernet|fo|fortygige|hu|hundredgige|po|port-channel)\s*(?<n>\d+(?:/\d+){0,2})"),
            m => Type(m) switch
            {
                "gi" or "gigabitethernet" => "Gi", "te" or "tengigabitethernet" => "Te", "fo" or "fortygige" => "Fo",
                "hu" or "hundredgige" => "Hu", _ => "Po"
            } + " " + m.Groups["n"].Value,
            p => (p[..2] switch { "Gi" => "GigabitEthernet", "Te" => "TenGigabitEthernet", "Fo" => "FortyGigE", "Hu" => "hundredGigE", _ => "Port-channel" }) + p[2..]),
        [SwitchVendor.DellOs10] = new(R(@"(?<t>e|eth|ethernet)\s*(?<n>\d+/\d+/\d+(?::\d+)?)|(?<t>po|port-channel)\s*(?<n>\d+)|(?<t>mgmt)\s*(?<n>\d+/\d+/\d+)"),
            m => Type(m) switch { "po" or "port-channel" => "port-channel", "mgmt" => "mgmt", _ => "ethernet" } + m.Groups["n"].Value,
            Same),
        // Huawei keeps the spelling the device printed: S-series tables say "GigabitEthernet0/0/1",
        // CloudEngine says "GE1/0/1"; Key() makes both comparable.
        [SwitchVendor.Huawei] = new(R(@"(?<t>eth-trunk|xgigabitethernet|gigabitethernet|ethernet|multige|meth|100ge|40ge|25ge|10ge|xge|ge|eth)\s*(?<n>\d+(?:/\d+){0,3})"),
            m => HuaweiTypes[Type(m)] + m.Groups["n"].Value, Same),
        // Junos ELS physical interfaces and aggregated Ethernet bundles (no logical unit).
        [SwitchVendor.Juniper] = new(R(@"(?<t>ge|xe|et|mge)-(?<n>\d+/\d+/\d+)|(?<t>ae)(?<n>\d+)"),
            m => Type(m) + (Type(m) == "ae" ? "" : "-") + m.Groups["n"].Value, Same),
        // RouterOS interface names are user-defined; refuse anything a script could interpret.
        [SwitchVendor.MikroTik] = new(R(@"(?<n>[A-Za-z][A-Za-z0-9_.-]{0,31})"), m => m.Groups["n"].Value, Same),
        // EdgeSwitch (FASTPATH): slot/port, "0/1"…, LAGs "3/1".
        [SwitchVendor.UbiquitiEdge] = new(R(@"(?<n>\d{1,2}/\d{1,3})"), m => m.Groups["n"].Value, Same),
        // UniFi: port_idx exposed as "Port 7".
        [SwitchVendor.UniFi] = new(R(@"(?:port\s*)?(?<n>\d{1,3})"), m => "Port " + int.Parse(m.Groups["n"].Value), p => p[5..])
    };

    private static string Type(Match m) => m.Groups["t"].Value.ToLowerInvariant();

    private static bool Legacy(SwitchVendor vendor) => vendor is SwitchVendor.Cisco or SwitchVendor.AlliedTelesis or SwitchVendor.AlliedS95;

    /// <summary>Validates a port name for <paramref name="vendor"/> and returns its canonical display form.</summary>
    public static string Validate(SwitchVendor vendor, string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length is 0 or > 48 || value.Any(char.IsControl)) throw new ArgumentException("Interface invalide.");
        if (Legacy(vendor)) return CommandPlan.LegacyInterface(value);
        if (!Grammars.TryGetValue(vendor, out var grammar)) throw new ArgumentException("Interface invalide.");
        var m = grammar.Pattern.Match(value);
        if (!m.Success) throw new ArgumentException($"Interface invalide pour {SwitchPlatforms.Get(vendor).DisplayName}.");
        return grammar.Display(m);
    }

    public static bool TryValidate(SwitchVendor vendor, string? value, out string port)
    {
        try { port = Validate(vendor, value); return true; }
        catch (ArgumentException) { port = ""; return false; }
    }

    /// <summary>Form sent in "interface …" (or the platform equivalent) for an already validated port.</summary>
    public static string CommandForm(SwitchVendor vendor, string port)
    {
        port = Validate(vendor, port);
        return Legacy(vendor) ? port : Grammars[vendor].Command(port);
    }

    /// <summary>
    /// Vendor-agnostic grammar used where the platform is unknown (outlet inventory, legacy
    /// callers). Free-form RouterOS names are excluded unless <paramref name="allowFreeForm"/>.
    /// </summary>
    internal static string? Any(string value, bool allowFreeForm)
    {
        foreach (var (vendor, grammar) in Grammars)
        {
            if (vendor == SwitchVendor.MikroTik && !allowFreeForm) continue;
            var m = grammar.Pattern.Match(value.Trim());
            if (m.Success) return grammar.Display(m);
        }
        return null;
    }

    /// <summary>Normalizes a port typed in an inventory, whatever the platform.</summary>
    public static string Inventory(string value)
    {
        try { return CommandPlan.Interface(value); }
        catch (ArgumentException) { return Any(value, allowFreeForm: true) ?? throw new ArgumentException("Interface invalide."); }
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["fastethernet"] = "fa", ["gigabitethernet"] = "gi", ["ge"] = "gi", ["tengigabitethernet"] = "te",
        ["xgigabitethernet"] = "xge", ["fortygigabitethernet"] = "fo", ["fortygige"] = "fo", ["hundredgigabitethernet"] = "hu",
        ["hundredgige"] = "hu", ["port-channel"] = "po", ["ethernet"] = "eth", ["et"] = "eth", ["e"] = "eth",
        ["management"] = "ma", ["twentyfivegigabitethernet"] = "tw"
    };

    /// <summary>Comparison key: same physical port ⇔ same key, across short/long/spaced spellings.</summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var text = Regex.Replace(CiscoParser.NormalizeInterface(name), @"\s+", "").ToLowerInvariant();
        var m = Regex.Match(text, @"^(?<t>[a-z]+(?:-[a-z]+)*)(?<rest>[-\d].*)?$");
        if (!m.Success) return text;
        var type = m.Groups["t"].Value;
        return (Aliases.TryGetValue(type, out var alias) ? alias : type) + m.Groups["rest"].Value;
    }

    /// <summary>Link aggregation (Port-channel, ae, Eth-Trunk, LAG): never a target for TDR. "Po1" is one, the UniFi "Port 7" is not.</summary>
    public static bool IsAggregate(string? name) => Regex.IsMatch(Key(name), @"^(?:po|ae|eth-trunk|bond|lag)\d", RegexOptions.CultureInvariant);

    public static bool Same(string? a, string? b)
    {
        var key = Key(a);
        return key.Length > 0 && key == Key(b);
    }
}
