using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Infrastructure.Drivers;
using SwitchPilot.Infrastructure.Terminal;
using System.Text.RegularExpressions;

namespace SwitchPilot.Infrastructure.Cisco;

/// <summary>
/// Cisco IOS / IOS-XE driver. The write pipeline (backup, safety, recovery with "end") is the
/// common one; this class keeps the IOS reads, the MAC-table command fallbacks and the TDR.
/// </summary>
public sealed class CiscoIosDriver(ICliSession session, IAuditSink audit, TimeSpan? tdrPollInterval = null, IConfigurationBackup? backup = null)
    : CliSwitchDriver(session, audit, backup)
{
    private SwitchIdentity? identity;
    private bool filteredMacSupported = true;
    private string macCommand = "show mac address-table";
    public override SwitchVendor Vendor => SwitchVendor.Cisco;
    protected override string AcceptedMessage => "Commandes acceptées par IOS ; relecture de l'état nécessaire.";
    // IOS keeps accepting the historical abbreviations (Fa0/1, Gi1/0/1, Po1…).
    protected override string CommandPort(string port) => CommandPlan.Interface(port);

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        CiscoParser.Identity(Session.Hostname, await Run("show version", ct));

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = CiscoParser.Ports(await Run("show interfaces status", ct));
        try
        {
            var descriptions = CiscoParser.Descriptions(await Run("show interfaces description", ct));
            ports = ports.Select(p => p with { Description = descriptions.GetValueOrDefault(p.Name, p.Description) }).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des descriptions", "Descriptions limitées à la sortie interfaces status."); }
        try
        {
            var modes = CiscoParser.SwitchportModes(await Run("show interfaces switchport", ct));
            ports = ports.Select(p => p with { Mode = modes.GetValueOrDefault(p.Name, p.Mode) }).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des modes", "Non autorisée ou non prise en charge ; modes inconnus conservés."); }
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = CiscoParser.Vlans(await Run("show vlan brief", ct)); }
        catch (FormatException) { vlans = []; Audit.Write("Lecture des VLAN", "Format non reconnu ; liste vide."); }
        return new(info, ports, vlans);
    }

    // Lighter than a full snapshot: the MAC lookup is filtered and only matched ports are re-read.
    protected override async Task<DetectionObservation> DetectionCoreAsync(string mac, CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var entries = (await ReadMacs(mac, ct)).Where(e => e.Mac == mac).ToArray();
        if (entries.Length == 0) return NoMatch(info, entries);
        var ports = CiscoParser.Ports(await Run("show interfaces status", ct));
        var matched = entries.Select(e => e.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = new List<PortInfo>();
        foreach (var port in ports.Where(p => matched.Contains(p.Name)))
        {
            var mode = port.Mode; // Never reuse an old mode for a safety decision.
            try
            {
                var modes = CiscoParser.SwitchportModes(await Run($"show interfaces {CommandPlan.Interface(port.Name)} switchport", ct));
                mode = modes.GetValueOrDefault(port.Name, mode);
            }
            catch (CliException) { /* No direct-port claim when the mode is unavailable. */ }
            current.Add(port with { Mode = mode, Description = DescriptionOf(port) });
        }
        return new(new(info, current, LastSnapshot?.Vlans ?? []), entries);
    }

    private async Task<IReadOnlyList<MacEntry>> ReadMacs(string? mac, CancellationToken ct)
    {
        var suffix = mac is not null && filteredMacSupported ? $" address {mac[..4]}.{mac[4..8]}.{mac[8..]}" : "";
        string output;
        try { output = await Run(macCommand + suffix, ct); }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported)
        {
            if (suffix.Length > 0)
            {
                filteredMacSupported = false;
                return await ReadMacs(null, ct);
            }
            if (macCommand != "show mac address-table") throw;
            macCommand = "show mac-address-table";
            output = await Run(macCommand, ct);
        }
        return CiscoParser.Macs(output);
    }

    protected override Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) => ReadMacs(null, ct);

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        CiscoParser.Counters(await Run($"show interfaces {port}", ct));

    public override async Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default)
    {
        port = CommandPlan.Interface(port);
        return await Locked(() => Tdr(port, safety, ct), ct);
    }

    private async Task<TdrResult> Tdr(string port, SafetyContext safety, CancellationToken ct)
    {
        var snapshot = await RefreshSnapshotAsync(ct);
        var info = snapshot.Ports.Single(p => p.Name == port);
        SafetyPolicy.RequireSafeTdr(info, safety, Kind);
        // Read-only feature probe first. Never launch a test to discover support.
        string previous;
        try { previous = await Run($"show cable-diagnostics tdr interface {port}", ct); }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported) { throw new NotSupportedException("TDR non pris en charge sur cette interface. Utilisez le contrôle passif."); }
        Audit.Write($"TDR {port}", "Lancement confirmé ; interruption de lien possible.");
        try
        {
            var started = await Run($"test cable-diagnostics tdr interface {port}", ct);
            if (!started.Contains("TDR test started", StringComparison.OrdinalIgnoreCase))
                throw new CliException("Le lancement du TDR n'a pas été confirmé.");
        }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported) { throw new NotSupportedException("TDR non pris en charge sur cette interface. Le contrôle passif reste disponible."); }
        var oldStamp = Regex.Match(previous, @"TDR test last run on:\s*([^\r\n]+)").Groups[1].Value;
        var observedPending = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(tdrPollInterval ?? TimeSpan.FromSeconds(2), ct);
            var output = await Run($"show cable-diagnostics tdr interface {port}", ct);
            var result = CiscoParser.Tdr(port, output);
            var stamp = Regex.Match(output, @"TDR test last run on:\s*([^\r\n]+)").Groups[1].Value;
            var pending = result.Pairs.Any(p => p.Status is "Non terminé" or "En cours");
            observedPending |= pending;
            var fresh = stamp.Length > 0 ? stamp != oldStamp : observedPending;
            if (fresh && result.Pairs.Count > 0 && !pending)
            { Audit.Write($"TDR {port}", "Résultats reçus."); return result; }
        }
        throw new TimeoutException("TDR lancé mais résultats non disponibles après attente. Ne pas interpréter cette absence comme un câble sain.");
    }
}
