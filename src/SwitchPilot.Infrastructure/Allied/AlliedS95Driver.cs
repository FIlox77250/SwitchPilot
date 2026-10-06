using SwitchPilot.Core;
using SwitchPilot.Core.Allied;
using SwitchPilot.Infrastructure.Drivers;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Allied;

/// <summary>
/// Driver for the older Allied Telesis AT-S95 firmware (AT-8000GS and relatives). The CLI is
/// Cisco-Small-Business style: `show interfaces status`, `show vlan`, `show bridge
/// address-table`, ports `g1`/`1/g1`, `terminal datadump` to disable paging. Reading, port
/// detection and configuration export are supported; writes are refused by the read-only
/// dialect because this family uses a different configuration dialect (`configure`, `interface
/// ethernet`, `copy running-config startup-config`) that has not been validated on hardware.
/// </summary>
public sealed class AlliedS95Driver(ICliSession session, IAuditSink audit) : CliSwitchDriver(session, audit, null)
{
    private SwitchIdentity? identity;
    public override SwitchVendor Vendor => SwitchVendor.AlliedS95;
    protected override string CommandPort(string port) => CommandPlan.Interface(port);

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = S95Parser.Ports(await Run("show interfaces status", ct));
        try
        {
            var descriptions = S95Parser.Descriptions(await Run("show interfaces description", ct));
            if (descriptions.Count > 0)
                ports = ports.Select(p => p with { Description = descriptions.GetValueOrDefault(p.Name, p.Description) }).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des descriptions", "Non autorisée ou non prise en charge ; descriptions ignorées."); }
        // One switchport read per physical port: it carries the membership mode and the PVID,
        // which the status table does not expose on this firmware.
        var enriched = new List<PortInfo>(ports.Count);
        foreach (var port in ports)
        {
            if (port.Name.StartsWith("ch", StringComparison.OrdinalIgnoreCase)) { enriched.Add(port); continue; }
            try
            {
                var (mode, pvid) = S95Parser.Switchport(await Switchport(port.Name, ct));
                enriched.Add(port with { Mode = mode, Vlan = pvid.Length > 0 ? pvid : port.Vlan });
            }
            catch (CliException) { enriched.Add(port); }
        }
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = S95Parser.Vlans(await Run("show vlan", ct)); }
        catch (FormatException) { vlans = []; Audit.Write("Lecture des VLAN", "Format « show vlan » AT-S95 non reconnu ; liste vide."); }
        return new(info, enriched, vlans);
    }

    /// <summary>Stacked units spell ports `1/g1`; standalone units accept the short `g1` form.</summary>
    private async Task<string> Switchport(string port, CancellationToken ct)
    {
        try { return await Run($"show interfaces switchport ethernet {port}", ct); }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported && !port.Contains('/'))
        { return await Run($"show interfaces switchport ethernet 1/{port}", ct); }
    }

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct)
    {
        if (identity is not null) return identity;
        var version = await Run("show version", ct);
        string? system = null;
        try { system = await Run("show system", ct); }
        catch (CliException) { /* Model falls back to the generic AT-S95 name. */ }
        identity = S95Parser.Identity(Session.Hostname, version, system);
        return identity;
    }

    protected override async Task<DetectionObservation> DetectionCoreAsync(string mac, CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var entries = (await MacTableCoreAsync(ct)).Where(e => e.Mac == mac).ToArray();
        if (entries.Length == 0) return NoMatch(info, entries);
        var ports = S95Parser.Ports(await Run("show interfaces status", ct));
        var matched = entries.Select(e => e.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = new List<PortInfo>();
        foreach (var port in ports.Where(p => matched.Contains(p.Name)))
        {
            // Fresh membership read: never reuse an old mode for a direct-candidate decision.
            var mode = port.Mode;
            if (!port.Name.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
            {
                try { mode = S95Parser.Switchport(await Switchport(port.Name, ct)).Mode; }
                catch (CliException) { mode = "Inconnu"; }
            }
            current.Add(port with { Mode = mode, Description = DescriptionOf(port) });
        }
        return new(new(info, current, LastSnapshot?.Vlans ?? []), entries);
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) =>
        S95Parser.Macs(await Run("show bridge address-table", ct));

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct)
    {
        // This firmware exposes octet/packet counters but no CRC/error counters; report
        // link, speed and duplex from the live status row and leave the counters unknown.
        var ports = S95Parser.Ports(await Run($"show interfaces status ethernet {port}", ct));
        var row = ports.FirstOrDefault(p => p.Name.Equals(S95Parser.NormalizePort(port), StringComparison.OrdinalIgnoreCase)) ?? ports.FirstOrDefault();
        if (row is null) return new(null, null, null, "Inconnue", "Inconnu");
        return new(null, null, null, row.Speed, row.Duplex, row.IsUp ? "Actif" : "Inactif");
    }

    public override Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default) =>
        throw new NotSupportedException("Le test de câble TDR n'existe pas sur la famille AT-S95 / AT-8000GS. Utilisez le contrôle passif.");
}
