using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// Juniper Junos ELS (EX / QFX). Every change is made in a private candidate configuration and
/// activated atomically by "commit and-quit" (expected answer "commit complete"); a commit is
/// persistent, so "save" is a no-op. After a failure the candidate is discarded by "rollback 0".
/// Experimental: built from vendor documentation and recorded outputs.
/// </summary>
public sealed class JunosDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null)
    : CliSwitchDriver(session, audit, backup)
{
    private SwitchIdentity? identity;
    public override SwitchVendor Vendor => SwitchVendor.Juniper;
    protected override string AcceptedMessage => "Commit Junos accepté ; relecture de l'état nécessaire.";

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        JunosParser.Identity(Session.Hostname, await Run("show version", ct));

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = JunosParser.Terse(await Run("show interfaces terse", ct));
        try
        {
            var descriptions = JunosParser.Descriptions(await Run("show interfaces descriptions", ct));
            ports = ports.Select(p => p with { Description = descriptions.GetValueOrDefault(p.Name, p.Description) }).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des descriptions", "Non autorisée ou non prise en charge ; descriptions ignorées."); }
        try
        {
            var switching = JunosParser.SwitchingInterfaces(await Run("show ethernet-switching interface", ct));
            ports = ports.Select(p => switching.TryGetValue(p.Name, out var s) ? p with { Mode = s.Mode, Vlan = s.Vlan } : p).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des modes", "Non autorisée ou non prise en charge ; modes inconnus conservés."); }
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = JunosParser.Vlans(await Run("show vlans", ct)); }
        catch (FormatException) { vlans = []; Audit.Write("Lecture des VLAN", "Format « show vlans » Junos non reconnu ; liste vide."); }
        return new(info, ports, vlans);
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct)
    {
        // The table names VLANs; tags come from the VLAN list.
        var vlans = LastSnapshot?.Vlans is { Count: > 0 } known ? known : JunosParser.Vlans(await Run("show vlans", ct));
        var ids = vlans.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        return JunosParser.Macs(await Run("show ethernet-switching table", ct), ids);
    }

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        JunosParser.Counters(await Run($"show interfaces {port} extensive", ct));

    protected override async Task RecoverAsync(CommandPlan plan)
    {
        // Only a configuration-mode prompt ("user@host#") has a candidate to discard.
        if (!Session.Prompt.EndsWith('#')) return;
        try
        {
            using (var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                await Session.ExecuteAsync("rollback 0", rollback.Token);
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Session.ExecuteConfirmedAsync("exit configuration-mode", "yes\n", exit.Token);
        }
        catch { await Session.DisposeAsync(); }
    }
}
