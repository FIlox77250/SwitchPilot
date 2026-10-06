using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// Huawei VRP (S-series, CloudEngine). Reads with "display …"; writes in "system-view" ended
/// by "return". On two-stage-commit firmware (CloudEngine, prompt "[*HUAWEI]" when changes are
/// pending) the candidate is committed before "return" and discarded on recovery.
/// Experimental: built from vendor documentation and recorded outputs.
/// </summary>
public sealed class HuaweiDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null)
    : CliSwitchDriver(session, audit, backup)
{
    private SwitchIdentity? identity;
    public override SwitchVendor Vendor => SwitchVendor.Huawei;
    protected override string AcceptedMessage => "Commandes acceptées par VRP ; relecture de l'état nécessaire.";

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        HuaweiParser.Identity(Session.Hostname, await Run("display version", ct));

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = HuaweiParser.InterfaceBrief(await Run("display interface brief", ct));
        try
        {
            var descriptions = HuaweiParser.Descriptions(await Run("display interface description", ct));
            ports = ports.Select(p => descriptions.TryGetValue(PortNames.Key(p.Name), out var d) ? p with { Description = d } : p).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des descriptions", "Non autorisée ou non prise en charge ; descriptions ignorées."); }
        try
        {
            var vlansByPort = HuaweiParser.PortVlans(await Run("display port vlan", ct));
            ports = ports.Select(p => vlansByPort.TryGetValue(PortNames.Key(p.Name), out var v) ? p with { Mode = v.Mode, Vlan = v.Vlan } : p).ToArray();
        }
        catch (CliException) { Audit.Write("Lecture des modes", "Non autorisée ou non prise en charge ; modes inconnus conservés."); }
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = HuaweiParser.Vlans(await Run("display vlan", ct)); }
        catch (FormatException) { vlans = []; Audit.Write("Lecture des VLAN", "Format « display vlan » VRP non reconnu ; liste vide."); }
        return new(info, ports, vlans);
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) =>
        ParseKit.Macs(Vendor, await Run("display mac-address", ct));

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        HuaweiParser.Counters(await Run($"display interface {PortNames.CommandForm(Vendor, port)}", ct));

    private bool PendingCommit => Session.Prompt.StartsWith("[*", StringComparison.Ordinal);

    protected override async Task<string> RunStepAsync(PlanStep step, CancellationToken ct)
    {
        // Two-stage commit: changes are only active once committed; "return" would otherwise ask.
        if (step.Command == "return" && PendingCommit) await Run("commit", ct);
        return await base.RunStepAsync(step, ct);
    }

    protected override async Task RecoverAsync(CommandPlan plan)
    {
        if (!Session.Prompt.StartsWith('[')) return;
        try
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // CloudEngine asks whether to commit pending changes: "n" discards them.
            await Session.ExecuteConfirmedAsync("return", "n\n", recovery.Token);
        }
        catch { await Session.DisposeAsync(); }
    }
}
