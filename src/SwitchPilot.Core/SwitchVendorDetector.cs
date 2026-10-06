namespace SwitchPilot.Core;

/// <summary>
/// Detects the switch family from an identity banner (`show version`, `show system`,
/// `display version`…). The connection dialog uses this so the user does not have to know
/// the vendor: a driver of the wrong family sends the wrong read commands and fails on both
/// SSH and console. The signatures live in <see cref="Platforms.DeviceDetector"/>.
/// </summary>
public static class SwitchVendorDetector
{
    public static SwitchVendor? Detect(string? versionOutput) => Platforms.DeviceDetector.FromBanner(versionOutput);
}
