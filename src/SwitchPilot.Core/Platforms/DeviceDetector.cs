using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Platforms;

/// <summary>
/// Identifies the platform from what the device prints by itself (the prompt) and from the
/// read-only identity commands (show version / show system / display version / /system
/// resource print / info). Signatures are ordered from the most specific to the most generic:
/// NX-OS mentions "Cisco Systems", OS6 shares FASTPATH wording with EdgeSwitch, and so on.
/// </summary>
public static class DeviceDetector
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant;

    private static readonly (SwitchVendor Vendor, Regex Signature)[] Banners =
    [
        (SwitchVendor.Juniper, new(@"\bJUNOS\b|^Junos:|Juniper Networks", Options)),
        (SwitchVendor.Huawei, new(@"Huawei Versatile Routing Platform|VRP \(R\) software|HUAWEI TECH", Options)),
        (SwitchVendor.MikroTik, new(@"\bRouterOS\b|^\s*platform:\s*MikroTik|^\s*board-name:\s*(CRS|CSS|RB)", Options)),
        (SwitchVendor.UniFi, new(@"^\s*Model:\s*(?:USW|USL|USM|US)[-\w]*\s*$|Welcome to UniFi|\bUniFi\b", Options)),
        (SwitchVendor.UbiquitiEdge, new(@"EdgeSwitch|\bUBNT\b|Ubiquiti", Options)),
        (SwitchVendor.Arista, new(@"\bArista\b|\bvEOS\b|\bcEOS\b", Options)),
        (SwitchVendor.DellOs10, new(@"SmartFabric OS10|OS10 Enterprise|Networking OS10|^\s*OS Version:\s*10\.", Options)),
        (SwitchVendor.DellOs9, new(@"Dell (?:Real Time )?Operating System|Dell Application Software|Force10|\bFTOS\b", Options)),
        (SwitchVendor.DellOs6, new(@"Dell (?:EMC )?Networking N\d|PowerConnect|^\s*(?:Machine|System) Description[ .:]*Dell", Options)),
        (SwitchVendor.AlliedS95, new(@"^\s*(?:Unit\s+)?SW version\b|AT-S95|AT-8000GS", Options)),
        (SwitchVendor.AlliedTelesis, new(@"AlliedWare|Allied\s+Telesis|\bAW\+|\bawplus\b", Options)),
        (SwitchVendor.CiscoNxos, new(@"\bNX-OS\b|Cisco Nexus|Nexus Operating System", Options)),
        (SwitchVendor.Cisco, new(@"Cisco IOS|IOS Software|IOS-XE Software|Cisco Systems|Bootstrap Software", Options))
    ];

    private static readonly Regex HuaweiPrompt = new(@"^(?:<[^<>\s]{1,64}>|\[[~*]?[^\[\]\s@]{1,64}\])$");
    private static readonly Regex JunosPrompt = new(@"^[A-Za-z0-9_.-]{1,32}@[A-Za-z0-9_.-]{1,64}[>#%]$");
    private static readonly Regex RouterOsPrompt = new(@"^\[[^\[\]@\s]{1,32}@[^\[\]]{1,64}\]\s?(?:/\S*)?\s?>$");
    private static readonly Regex FastpathPrompt = new(@"^\([^()]{1,64}\)\s?(?:\([^()]{1,64}\))?\s?[>#]$");

    /// <summary>Platform implied by the prompt alone, or null when it is IOS-shaped (ambiguous).</summary>
    public static SwitchVendor? FromPrompt(string? prompt)
    {
        var text = LastLine(prompt);
        if (text.Length == 0) return null;
        if (RouterOsPrompt.IsMatch(text)) return SwitchVendor.MikroTik;
        if (JunosPrompt.IsMatch(text)) return SwitchVendor.Juniper;
        if (HuaweiPrompt.IsMatch(text)) return SwitchVendor.Huawei;
        if (FastpathPrompt.IsMatch(text)) return SwitchVendor.UbiquitiEdge;
        return null;
    }

    /// <summary>Platform named by an identity command output, or null.</summary>
    public static SwitchVendor? FromBanner(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var (vendor, signature) in Banners)
            if (signature.IsMatch(output)) return vendor;
        return null;
    }

    /// <summary>Banner first (explicit), then the prompt shape.</summary>
    public static SwitchVendor? Detect(string? prompt, string? banner) => FromBanner(banner) ?? FromPrompt(prompt);

    /// <summary>Read-only identity commands to try, in order, when the prompt is ambiguous.</summary>
    public static IReadOnlyList<string> Probes(SwitchVendor? promptHint) => promptHint switch
    {
        SwitchVendor.Huawei => ["display version"],
        SwitchVendor.MikroTik => ["/system resource print"],
        SwitchVendor.Juniper => ["show version"],
        SwitchVendor.UbiquitiEdge => ["show version"],
        _ => ["show version", "show system", "display version", "info"]
    };

    private static string LastLine(string? text) =>
        (text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
}
