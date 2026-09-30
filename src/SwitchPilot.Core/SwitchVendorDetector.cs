using System.Text.RegularExpressions;

namespace SwitchPilot.Core;

/// <summary>
/// Detects the switch family from the `show version` banner. The connection dialog uses
/// this so the user does not have to know the vendor: a Cisco driver on an Allied switch
/// (or the reverse) sends the wrong read commands and fails on both SSH and console.
/// </summary>
public static class SwitchVendorDetector
{
    public static SwitchVendor? Detect(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;
        if (Regex.IsMatch(versionOutput, @"AlliedWare|Allied\s+Telesis|\bAW\+|\bawplus\b", RegexOptions.IgnoreCase))
            return SwitchVendor.AlliedTelesis;
        if (Regex.IsMatch(versionOutput, @"Cisco IOS|Cisco Nexus|IOS Software|Cisco Systems|Bootstrap Software", RegexOptions.IgnoreCase))
            return SwitchVendor.Cisco;
        return null;
    }
}
