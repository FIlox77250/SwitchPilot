using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Infrastructure.Ssh;
using SwitchPilot.Infrastructure.Terminal;
using System.Text.RegularExpressions;

namespace SwitchPilot.Infrastructure.Cisco;

public sealed class CiscoIosDriver(ICliSession session, IAuditSink audit, TimeSpan? tdrPollInterval = null, IConfigurationBackup? backup = null) : ISwitchDriver
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SwitchIdentity? identity;
    private SwitchSnapshot? lastSnapshot;
    private bool filteredMacSupported = true;
    private string macCommand = "show mac address-table";
    public ConnectionKind Kind => session.Kind;
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
        var ports = CiscoParser.Ports(await session.ExecuteAsync("show interfaces status", ct));
        try
        {
            var descriptions = CiscoParser.Descriptions(await session.ExecuteAsync("show interfaces description", ct));
            ports = ports.Select(p => p with { Description = descriptions.GetValueOrDefault(p.Name, p.Description) }).ToArray();
        }
        catch (CliException) { audit.Write("Lecture des descriptions", "Descriptions limitées à la sortie interfaces status."); }
        try
        {
            var modes = CiscoParser.SwitchportModes(await session.ExecuteAsync("show interfaces switchport", ct));
            ports = ports.Select(p => p with { Mode = modes.GetValueOrDefault(p.Name, p.Mode) }).ToArray();
        }
        catch (CliException) { audit.Write("Lecture des modes", "Non autorisée ou non prise en charge ; modes inconnus conservés."); }
        var vlans = CiscoParser.Vlans(await session.ExecuteAsync("show vlan brief", ct));
        lastSnapshot = new(info, ports, vlans);
        return lastSnapshot;
    }
    private async Task<SwitchIdentity> ReadIdentity(CancellationToken ct) => identity ??=
        CiscoParser.Identity(session.Hostname, await session.ExecuteAsync("show version", ct));

    public async Task<DetectionObservation> ReadDetectionAsync(string mac, CancellationToken ct = default)
    {
        mac = CiscoParser.NormalizeMac(mac);
        await gate.WaitAsync(ct);
        try
        {
            var info = await ReadIdentity(ct);
            var entries = (await ReadMacs(mac, ct)).Where(e => e.Mac == mac).ToArray();
            if (entries.Length == 0) return new(new(info, [], lastSnapshot?.Vlans ?? []), entries);
            var ports = CiscoParser.Ports(await session.ExecuteAsync("show interfaces status", ct));
            var matched = entries.Select(e => e.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = new List<PortInfo>();
            foreach (var port in ports.Where(p => matched.Contains(p.Name)))
            {
                var mode = port.Mode; // Never reuse an old mode for a safety decision.
                try
                {
                    var modes = CiscoParser.SwitchportModes(await session.ExecuteAsync($"show interfaces {CommandPlan.Interface(port.Name)} switchport", ct));
                    mode = modes.GetValueOrDefault(port.Name, mode);
                }
                catch (CliException) { /* No direct-port claim when the mode is unavailable. */ }
                current.Add(port with { Mode = mode, Description = lastSnapshot?.Ports.FirstOrDefault(p => p.Name == port.Name)?.Description ?? port.Description });
            }
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
            if (suffix.Length > 0)
            {
                filteredMacSupported = false;
                return await ReadMacs(null, ct);
            }
            if (macCommand != "show mac address-table") throw;
            macCommand = "show mac-address-table";
            output = await session.ExecuteAsync(macCommand, ct);
        }
        return CiscoParser.Macs(output);
    }
    public async Task<IReadOnlyList<MacEntry>> ReadMacTableAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await ReadMacs(null, ct);
        }
        finally { gate.Release(); }
    }
    public async Task<InterfaceCounters> ReadCountersAsync(string port, CancellationToken ct = default)
    {
        port = CommandPlan.Interface(port); await gate.WaitAsync(ct);
        try { return CiscoParser.Counters(await session.ExecuteAsync($"show interfaces {port}", ct)); }
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
            // Recheck VLAN constraints immediately before writing, including stale UI selections.
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
            // Backups can take time. Revalidate evidence immediately before the first mutation.
            SafetyPolicy.RequireSafeChange(plan, safety ?? SafetyContext.Unknown, Kind);
            foreach (var command in plan.Commands)
            {
                var output = await session.ExecuteAsync(command, ct); completed++;
                if (plan.Kind == ChangeKind.Save && !output.Contains("[OK]", StringComparison.OrdinalIgnoreCase))
                    throw new CliException("IOS n'a pas confirmé la sauvegarde par [OK]. Vérifiez la startup-config.");
            }
            audit.Write(plan.Title, "Commandes acceptées par IOS ; relecture de l'état nécessaire.");
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
    public async Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default)
    {
        port = CommandPlan.Interface(port); await gate.WaitAsync(ct);
        try
        {
            var snapshot = await Snapshot(ct);
            var info = snapshot.Ports.Single(p => p.Name == port);
            SafetyPolicy.RequireSafeTdr(info, safety, Kind);
            // Read-only feature probe first. Never launch a test to discover support.
            string previous;
            try { previous = await session.ExecuteAsync($"show cable-diagnostics tdr interface {port}", ct); }
            catch (CliException e) when (e.Failure == CliFailure.Unsupported) { throw new NotSupportedException("TDR non pris en charge sur cette interface. Utilisez le contrôle passif."); }
            audit.Write($"TDR {port}", "Lancement confirmé ; interruption de lien possible.");
            try
            {
                var started = await session.ExecuteAsync($"test cable-diagnostics tdr interface {port}", ct);
                if (!started.Contains("TDR test started", StringComparison.OrdinalIgnoreCase))
                    throw new CliException("Le lancement du TDR n'a pas été confirmé.");
            }
            catch (CliException e) when (e.Failure == CliFailure.Unsupported) { throw new NotSupportedException("TDR non pris en charge sur cette interface. Le contrôle passif reste disponible."); }
            var oldStamp = Regex.Match(previous, @"TDR test last run on:\s*([^\r\n]+)").Groups[1].Value;
            var observedPending = false;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(tdrPollInterval ?? TimeSpan.FromSeconds(2), ct);
                var output = await session.ExecuteAsync($"show cable-diagnostics tdr interface {port}", ct);
                var result = CiscoParser.Tdr(port, output);
                var stamp = Regex.Match(output, @"TDR test last run on:\s*([^\r\n]+)").Groups[1].Value;
                var pending = result.Pairs.Any(p => p.Status is "Non terminé" or "En cours");
                observedPending |= pending;
                var fresh = stamp.Length > 0 && stamp != oldStamp || observedPending;
                if (fresh && result.Pairs.Count > 0 && !pending)
                { audit.Write($"TDR {port}", "Résultats reçus."); return result; }
            }
            throw new TimeoutException("TDR lancé mais résultats non disponibles après attente. Ne pas interpréter cette absence comme un câble sain.");
        }
        finally { gate.Release(); }
    }
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await session.ExecuteAsync("show running-config", ct); }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync() => session.DisposeAsync();
}
