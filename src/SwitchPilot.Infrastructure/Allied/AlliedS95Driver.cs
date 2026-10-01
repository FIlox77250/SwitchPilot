using SwitchPilot.Core;
using SwitchPilot.Core.Allied;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Allied;

/// <summary>
/// Driver for the older Allied Telesis AT-S95 firmware (AT-8000GS and relatives). The CLI is
/// Cisco-Small-Business style: `show interfaces status`, `show vlan`, `show bridge
/// address-table`, ports `g1`/`1/g1`, `terminal datadump` to disable paging. Reading, port
/// detection and configuration export are supported; writes are refused with a clear message
/// because this family uses a different configuration dialect (`configure`, `interface
/// ethernet`, `copy running-config startup-config`) that has not been validated on hardware.
/// </summary>
public sealed class AlliedS95Driver(ICliSession session, IAuditSink audit) : ISwitchDriver
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SwitchIdentity? identity;
    private SwitchSnapshot? lastSnapshot;
    public ConnectionKind Kind => session.Kind;
    public SwitchVendor Vendor => SwitchVendor.AlliedS95;
    public bool IsConnected => session.IsConnected;
    public bool IsDemo => false;

    public async Task<SwitchSnapshot> ReadSnapshotAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await Snapshot(ct); } finally { gate.Release(); }
    }

    private async Task<SwitchSnapshot> Snapshot(CancellationToken ct)
    {
        var info = await ReadIdentity(ct);
        var ports = S95Parser.Ports(await session.ExecuteAsync("show interfaces status", ct));
        try
        {
            var descriptions = S95Parser.Descriptions(await session.ExecuteAsync("show interfaces description", ct));
            if (descriptions.Count > 0)
                ports = ports.Select(p => p with { Description = descriptions.GetValueOrDefault(p.Name, p.Description) }).ToArray();
        }
        catch (CliException) { audit.Write("Lecture des descriptions", "Non autorisée ou non prise en charge ; descriptions ignorées."); }
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
        try { vlans = S95Parser.Vlans(await session.ExecuteAsync("show vlan", ct)); }
        catch (FormatException) { vlans = []; audit.Write("Lecture des VLAN", "Format « show vlan » AT-S95 non reconnu ; liste vide."); }
        lastSnapshot = new(info, enriched, vlans);
        return lastSnapshot;
    }

    /// <summary>Stacked units spell ports `1/g1`; standalone units accept the short `g1` form.</summary>
    private async Task<string> Switchport(string port, CancellationToken ct)
    {
        try { return await session.ExecuteAsync($"show interfaces switchport ethernet {port}", ct); }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported && !port.Contains('/'))
        { return await session.ExecuteAsync($"show interfaces switchport ethernet 1/{port}", ct); }
    }

    private async Task<SwitchIdentity> ReadIdentity(CancellationToken ct)
    {
        if (identity is not null) return identity;
        var version = await session.ExecuteAsync("show version", ct);
        string? system = null;
        try { system = await session.ExecuteAsync("show system", ct); }
        catch (CliException) { /* Model falls back to the generic AT-S95 name. */ }
        identity = S95Parser.Identity(session.Hostname, version, system);
        return identity;
    }

    public async Task<DetectionObservation> ReadDetectionAsync(string mac, CancellationToken ct = default)
    {
        mac = CiscoParser.NormalizeMac(mac);
        await gate.WaitAsync(ct);
        try
        {
            var info = await ReadIdentity(ct);
            var entries = (await Macs(ct)).Where(e => e.Mac == mac).ToArray();
            if (entries.Length == 0) return new(new(info, [], lastSnapshot?.Vlans ?? []), entries);
            var ports = S95Parser.Ports(await session.ExecuteAsync("show interfaces status", ct));
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
                current.Add(port with { Mode = mode,
                    Description = lastSnapshot?.Ports.FirstOrDefault(s => s.Name == port.Name)?.Description ?? port.Description });
            }
            return new(new(info, current, lastSnapshot?.Vlans ?? []), entries);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<MacEntry>> Macs(CancellationToken ct) =>
        S95Parser.Macs(await session.ExecuteAsync("show bridge address-table", ct));

    public async Task<IReadOnlyList<MacEntry>> ReadMacTableAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await Macs(ct); }
        finally { gate.Release(); }
    }

    public async Task<InterfaceCounters> ReadCountersAsync(string port, CancellationToken ct = default)
    {
        port = CommandPlan.Interface(port); await gate.WaitAsync(ct);
        try
        {
            // This firmware exposes octet/packet counters but no CRC/error counters; report
            // link, speed and duplex from the live status row and leave the counters unknown.
            var ports = S95Parser.Ports(await session.ExecuteAsync($"show interfaces status ethernet {port}", ct));
            var row = ports.FirstOrDefault(p => p.Name.Equals(S95Parser.NormalizePort(port), StringComparison.OrdinalIgnoreCase)) ?? ports.FirstOrDefault();
            if (row is null) return new(null, null, null, "Inconnue", "Inconnu");
            return new(null, null, null, row.Speed, row.Duplex, row.IsUp ? "Actif" : "Inactif");
        }
        finally { gate.Release(); }
    }

    public Task ApplyAsync(CommandPlan plan, bool dryRun, CancellationToken ct = default, SafetyContext? safety = null) =>
        throw new NotSupportedException("Les modifications ne sont pas encore prises en charge sur la famille AT-S95 / AT-8000GS (dialecte de configuration différent, non validé sur matériel). Lecture, détection du port et export de la configuration restent disponibles.");

    public Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default) =>
        throw new NotSupportedException("Le test de câble TDR n'existe pas sur la famille AT-S95 / AT-8000GS. Utilisez le contrôle passif.");

    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await session.ExecuteAsync("show running-config", ct); }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();
}
