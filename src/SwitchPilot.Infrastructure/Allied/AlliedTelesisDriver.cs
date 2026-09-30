using SwitchPilot.Core;
using SwitchPilot.Core.Allied;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Allied;

/// <summary>
/// Driver for Allied Telesis switches running AlliedWare Plus. The read-only command
/// vocabulary and the access/trunk syntax are close enough to Cisco IOS to share the
/// command plans; the identity, VLAN and counter layouts are parsed by
/// <see cref="AlliedTelesisParser"/>. Cable TDR is not exposed, so only the passive
/// control remains available.
/// </summary>
public sealed class AlliedTelesisDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null) : ISwitchDriver
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SwitchIdentity? identity;
    private SwitchSnapshot? lastSnapshot;
    private bool filteredMacSupported = true;
    private string macCommand = "show mac address-table";
    public ConnectionKind Kind => session.Kind;
    public SwitchVendor Vendor => SwitchVendor.AlliedTelesis;
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
        var ports = CiscoParser.Ports(await session.ExecuteAsync("show interface status", ct));
        try
        {
            var descriptions = CiscoParser.Descriptions(await session.ExecuteAsync("show interface description", ct));
            ports = ports.Select(p => p with { Description = descriptions.GetValueOrDefault(p.Name, p.Description) }).ToArray();
        }
        catch (CliException) { audit.Write("Lecture des descriptions", "Descriptions limitées à la sortie interface status."); }
        var vlanOutput = await session.ExecuteAsync("show vlan brief", ct);
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = AlliedTelesisParser.Vlans(vlanOutput); }
        catch (FormatException) { vlans = []; audit.Write("Lecture des VLAN", "Format « show vlan brief » AlliedWare Plus non reconnu ; liste vide."); }
        var modes = AlliedTelesisParser.PortModes(vlanOutput);
        if (modes.Count > 0) ports = ports.Select(p => p with { Mode = modes.GetValueOrDefault(p.Name, p.Mode) }).ToArray();
        lastSnapshot = new(info, ports, vlans);
        return lastSnapshot;
    }

    private async Task<SwitchIdentity> ReadIdentity(CancellationToken ct) => identity ??=
        AlliedTelesisParser.Identity(session.Hostname, await session.ExecuteAsync("show version", ct));

    public async Task<DetectionObservation> ReadDetectionAsync(string mac, CancellationToken ct = default)
    {
        mac = CiscoParser.NormalizeMac(mac);
        await gate.WaitAsync(ct);
        try
        {
            var info = await ReadIdentity(ct);
            var entries = (await ReadMacs(mac, ct)).Where(e => e.Mac == mac).ToArray();
            if (entries.Length == 0) return new(new(info, [], lastSnapshot?.Vlans ?? []), entries);
            var ports = CiscoParser.Ports(await session.ExecuteAsync("show interface status", ct));
            // Re-read the VLAN membership now: never reuse an old access/trunk claim for a
            // safety-relevant direct-candidate decision.
            var modes = AlliedTelesisParser.PortModes(await session.ExecuteAsync("show vlan brief", ct));
            var matched = entries.Select(e => e.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = ports.Where(p => matched.Contains(p.Name))
                .Select(p => p with { Mode = modes.GetValueOrDefault(p.Name, p.Mode),
                    Description = lastSnapshot?.Ports.FirstOrDefault(s => s.Name == p.Name)?.Description ?? p.Description })
                .ToList();
            return new(new(info, current, lastSnapshot?.Vlans ?? []), entries);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<MacEntry>> ReadMacs(string? mac, CancellationToken ct)
    {
        var suffix = mac is not null && filteredMacSupported ? $" address {mac[..4]}.{mac[4..8]}.{mac[8..]}" : "";
        string output;
        try { output = await session.ExecuteAsync(macCommand + suffix, ct); }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported)
        {
            if (suffix.Length > 0) { filteredMacSupported = false; return await ReadMacs(null, ct); }
            if (macCommand != "show mac address-table") throw;
            macCommand = "show mac-address-table";
            output = await session.ExecuteAsync(macCommand, ct);
        }
        return CiscoParser.Macs(output);
    }

    public async Task<IReadOnlyList<MacEntry>> ReadMacTableAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await ReadMacs(null, ct); }
        finally { gate.Release(); }
    }

    public async Task<InterfaceCounters> ReadCountersAsync(string port, CancellationToken ct = default)
    {
        port = CommandPlan.Interface(port); await gate.WaitAsync(ct);
        try { return AlliedTelesisParser.Counters(await session.ExecuteAsync($"show interface {port}", ct)); }
        finally { gate.Release(); }
    }

    public async Task ApplyAsync(CommandPlan plan, bool dryRun, CancellationToken ct = default, SafetyContext? safety = null)
    {
        if (dryRun) { audit.Write(plan.Title, "Simulation : aucune commande envoyée."); return; }
        await gate.WaitAsync(ct);
        var completed = 0;
        try
        {
            SafetyPolicy.RequireSafeChange(plan, safety ?? SafetyContext.Unknown, Kind);
            if (plan.Kind is ChangeKind.AccessVlan or ChangeKind.DeleteVlan)
            {
                var snapshot = await Snapshot(ct);
                if (!snapshot.Vlans.Any(v => v.Id == plan.Vlan)) throw new InvalidOperationException("Ce VLAN n'existe plus. Actualisez la vue.");
                if (plan.Kind == ChangeKind.DeleteVlan && snapshot.Ports.Any(p => p.Vlan == plan.Vlan.ToString()))
                    throw new InvalidOperationException("Suppression bloquée : des ports sont encore affectés à ce VLAN.");
            }
            if (backup is null) throw new InvalidOperationException("Modification bloquée : service de sauvegarde chiffrée indisponible.");
            var configuration = await session.ExecuteAsync("show running-config", ct);
            await backup.SaveAsync(session.Hostname, configuration, ct);
            audit.Write("Sauvegarde avant modification", "Configuration conservée chiffrée avant toute écriture.");
            SafetyPolicy.RequireSafeChange(plan, safety ?? SafetyContext.Unknown, Kind);
            foreach (var command in plan.Commands)
            {
                await session.ExecuteAsync(command, ct); completed++;
            }
            audit.Write(plan.Title, "Commandes acceptées par le switch ; relecture de l'état nécessaire.");
        }
        catch
        {
            audit.Write(plan.Title, $"Échec après {completed}/{plan.Commands.Count} commandes ; configuration potentiellement partielle.");
            if (session.IsConnected && completed > 0 && plan.Kind != ChangeKind.Save)
            {
                try { using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await session.ExecuteAsync("end", recovery.Token); }
                catch { await session.DisposeAsync(); }
            }
            throw;
        }
        finally { gate.Release(); }
    }

    public Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default) =>
        throw new NotSupportedException("Le test de câble TDR n'est pas exposé par l'interface AlliedWare Plus prise en charge. Utilisez le contrôle passif.");

    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await session.ExecuteAsync("show running-config", ct); }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();
}
