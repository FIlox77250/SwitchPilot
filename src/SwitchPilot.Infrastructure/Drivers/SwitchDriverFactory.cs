using SwitchPilot.Core;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Infrastructure.Allied;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// Chooses and builds the driver of a CLI session (Factory pattern). The platform is either
/// selected by the user or detected: first from the prompt shape (Huawei &lt;…&gt;, Junos
/// user@host&gt;, RouterOS [user@host] &gt;, FASTPATH (name) #), then from the read-only identity
/// commands. The UniFi controller (HTTPS) is opened by <see cref="UniFiControllerDriver.ConnectAsync"/>.
/// </summary>
public static class SwitchDriverFactory
{
    /// <summary>Driver of <paramref name="vendor"/> over <paramref name="session"/>.</summary>
    public static ISwitchDriver Create(SwitchVendor vendor, ICliSession session, IAuditSink audit, IConfigurationBackup? backup, TimeSpan? tdrPollInterval = null) => vendor switch
    {
        SwitchVendor.Cisco => new CiscoIosDriver(session, audit, tdrPollInterval, backup),
        SwitchVendor.AlliedTelesis => new AlliedTelesisDriver(session, audit, backup),
        SwitchVendor.AlliedS95 => new AlliedS95Driver(session, audit),
        SwitchVendor.CiscoNxos or SwitchVendor.Arista or SwitchVendor.DellOs6 or SwitchVendor.DellOs9 or SwitchVendor.DellOs10 =>
            new CiscoLikeDriver(vendor, session, audit, backup),
        SwitchVendor.Huawei => new HuaweiDriver(session, audit, backup),
        SwitchVendor.Juniper => new JunosDriver(session, audit, backup),
        SwitchVendor.MikroTik => new RouterOsDriver(session, audit, backup),
        SwitchVendor.UbiquitiEdge => new EdgeSwitchDriver(session, audit, backup),
        SwitchVendor.UniFi => new UniFiSshDriver(session, audit),
        _ => throw new ArgumentOutOfRangeException(nameof(vendor), "Plateforme inconnue.")
    };

    /// <summary>
    /// Detects the platform of an open session. Only read-only identity commands are sent; a
    /// command the device rejects is skipped. Returns null when nothing matched, so the caller
    /// can fall back to the user's selection.
    /// </summary>
    public static async Task<SwitchVendor?> DetectAsync(ICliSession session, CancellationToken ct = default)
    {
        var hint = DeviceDetector.FromPrompt(session.Prompt);
        foreach (var probe in DeviceDetector.Probes(hint))
        {
            string output;
            try { output = await session.ExecuteAsync(probe, ct); }
            catch (CliException) { continue; }
            if (DeviceDetector.FromBanner(output) is { } vendor) return vendor;
        }
        // The prompt alone is conclusive for these shapes even when the banner is unusual.
        return hint;
    }

    /// <summary>Detected platform, else <paramref name="fallback"/>.</summary>
    public static async Task<ISwitchDriver> CreateDetectedAsync(ICliSession session, SwitchVendor fallback, IAuditSink audit, IConfigurationBackup? backup, CancellationToken ct = default)
    {
        var vendor = await DetectAsync(session, ct) ?? fallback;
        audit.Write("Détection de la plateforme", SwitchPlatforms.Get(vendor).DisplayName);
        return Create(vendor, session, audit, backup);
    }
}
