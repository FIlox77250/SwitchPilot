using System.Globalization;
using System.Text.RegularExpressions;
using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>Number of plan steps the device accepted; read by the failure path.</summary>
public sealed class PlanProgress
{
    public int Completed { get; set; }
}

/// <summary>
/// Abstract switch driver (Adapter pattern). A concrete driver only says HOW to read its
/// platform (snapshot, MAC table, counters, export) and HOW to send one step; this class owns
/// everything that must stay identical on every platform:
/// <list type="bullet">
/// <item>one operation at a time on the session (a CLI cannot interleave two conversations);</item>
/// <item>the write pipeline: read-only platform refusal → plan family check → dry run →
/// safety policy → live VLAN recheck → encrypted backup → safety policy again → steps with
/// their expected acknowledgement → audit;</item>
/// <item>the failure path: audit of the partial count, then the platform recovery (leave
/// configuration mode, discard a Junos candidate…) and disconnection when even that fails.</item>
/// </list>
/// The vendor-neutral API (<see cref="DetectDeviceAsync"/>, <see cref="GetPortsAsync"/>,
/// <see cref="GetVlansAsync"/>, <see cref="SetPortVlanAsync"/>, <see cref="GetLinkStatusAsync"/>,
/// <see cref="SaveConfigAsync"/>) is built on the same primitives, so it inherits these guarantees.
/// </summary>
public abstract class SwitchDriver(IAuditSink audit, IConfigurationBackup? backup) : ISwitchDriver
{
    private readonly SemaphoreSlim gate = new(1, 1);
    protected IAuditSink Audit { get; } = audit;
    protected IConfigurationBackup? Backup { get; } = backup;
    /// <summary>Last complete snapshot; used for descriptions and VLAN names, never for safety decisions.</summary>
    protected SwitchSnapshot? LastSnapshot { get; private set; }

    public abstract SwitchVendor Vendor { get; }
    public abstract ConnectionKind Kind { get; }
    public abstract bool IsConnected { get; }
    public bool IsDemo => false;
    /// <summary>Name under which the configuration backup is stored.</summary>
    protected abstract string Hostname { get; }
    public SwitchPlatform Platform => SwitchPlatforms.Get(Vendor);
    protected ConfigDialect Dialect => ConfigDialects.For(Vendor);

    // ---- Platform primitives -------------------------------------------------------------

    protected abstract Task<SwitchIdentity> IdentityAsync(CancellationToken ct);
    protected abstract Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct);
    protected abstract Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct);
    /// <param name="port">Already validated by <see cref="CommandPort"/>.</param>
    protected abstract Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct);
    /// <summary>Configuration text stored (encrypted) before any write; also the "Exporter" action.</summary>
    protected abstract Task<string> ExportCoreAsync(CancellationToken ct);
    /// <summary>Sends one plan step and returns the device output.</summary>
    protected abstract Task<string> RunStepAsync(PlanStep step, CancellationToken ct);
    /// <summary>Leaves the device in a clean state after a partially applied plan (short timeouts).</summary>
    protected abstract Task RecoverAsync(CommandPlan plan);
    public abstract ValueTask DisposeAsync();

    /// <summary>Validates a port typed by the user before it reaches a read command.</summary>
    protected virtual string CommandPort(string port) => PortNames.Validate(Vendor, port);
    /// <summary>Audit text after a fully accepted plan.</summary>
    protected virtual string AcceptedMessage => "Commandes acceptées par le switch ; relecture de l'état nécessaire.";
    /// <summary>Non-null when the platform is read-only through this driver; the text explains why.</summary>
    protected virtual string? WriteRefusal => Dialect is ReadOnlyDialect readOnly ? readOnly.Reason : null;

    // ---- Locking helpers ------------------------------------------------------------------

    protected async Task<T> Locked<T>(Func<Task<T>> body, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await body(); }
        finally { gate.Release(); }
    }

    /// <summary>Reads a full snapshot and keeps it as <see cref="LastSnapshot"/>. Caller holds the gate.</summary>
    protected async Task<SwitchSnapshot> RefreshSnapshotAsync(CancellationToken ct) => LastSnapshot = await SnapshotCoreAsync(ct);

    /// <summary>Observation without any port: the MAC address is not (or no longer) learned.</summary>
    protected DetectionObservation NoMatch(SwitchIdentity identity, IReadOnlyList<MacEntry> entries) =>
        new(new(identity, [], LastSnapshot?.Vlans ?? []), entries);

    /// <summary>Description from the last full snapshot (status tables often truncate it).</summary>
    protected string DescriptionOf(PortInfo port) =>
        LastSnapshot?.Ports.FirstOrDefault(p => p.Name == port.Name)?.Description ?? port.Description;

    // ---- Historical plan-based API --------------------------------------------------------

    public Task<SwitchSnapshot> ReadSnapshotAsync(CancellationToken ct = default) => Locked(() => RefreshSnapshotAsync(ct), ct);

    public async Task<DetectionObservation> ReadDetectionAsync(string mac, CancellationToken ct = default)
    {
        mac = CiscoParser.NormalizeMac(mac);
        return await Locked(() => DetectionCoreAsync(mac, ct), ct);
    }

    /// <summary>
    /// Default detection: MAC table filtered on <paramref name="mac"/>, then a fresh full snapshot
    /// so the access/trunk mode used for the direct-candidate decision is never an old one.
    /// </summary>
    protected virtual async Task<DetectionObservation> DetectionCoreAsync(string mac, CancellationToken ct)
    {
        var identity = await IdentityAsync(ct);
        var entries = (await MacTableCoreAsync(ct)).Where(e => e.Mac == mac).ToArray();
        if (entries.Length == 0) return NoMatch(identity, entries);
        var previous = LastSnapshot;
        var snapshot = await SnapshotCoreAsync(ct);
        LastSnapshot = previous ?? snapshot;
        var ports = snapshot.Ports.Where(p => entries.Any(e => PortNames.Same(e.Port, p.Name)))
            .Select(p => p with { Description = DescriptionOf(p) }).ToArray();
        return new(new(identity, ports, snapshot.Vlans), entries);
    }

    public Task<IReadOnlyList<MacEntry>> ReadMacTableAsync(CancellationToken ct = default) => Locked(() => MacTableCoreAsync(ct), ct);

    public async Task<InterfaceCounters> ReadCountersAsync(string port, CancellationToken ct = default)
    {
        port = CommandPort(port);
        return await Locked(() => CountersCoreAsync(port, ct), ct);
    }

    public virtual Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default) =>
        throw new NotSupportedException($"Le test de câble TDR n'est pas pris en charge sur {Platform.DisplayName}. Utilisez le contrôle passif.");

    public Task<string> ExportAsync(CancellationToken ct = default) => Locked(() => ExportCoreAsync(ct), ct);

    public async Task ApplyAsync(CommandPlan plan, bool dryRun, CancellationToken ct = default, SafetyContext? safety = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (WriteRefusal is { } refusal) throw new NotSupportedException(refusal);
        if (plan.Family != Dialect.Family)
            throw new InvalidOperationException($"Ce plan a été préparé pour {SwitchPlatforms.Get(plan.Vendor).DisplayName} et ne peut pas être appliqué à {Platform.DisplayName}. Actualisez la vue.");
        if (dryRun) { Audit.Write(plan.Title, "Simulation : aucune commande envoyée."); return; }
        if (plan.Steps.Count == 0)
        {
            Audit.Write(plan.Title, "Sauvegarde implicite : chaque modification acceptée par la plateforme est déjà persistante (commit ou enregistrement immédiat).");
            return;
        }
        await gate.WaitAsync(ct);
        var progress = new PlanProgress();
        try
        {
            SafetyPolicy.RequireSafeChange(plan, safety ?? SafetyContext.Unknown, Kind);
            // Recheck VLAN constraints immediately before writing, including stale UI selections.
            await VerifyPlanAsync(plan, ct);
            if (Backup is null) throw new InvalidOperationException("Modification bloquée : service de sauvegarde chiffrée indisponible.");
            var configuration = await ExportCoreAsync(ct);
            await Backup.SaveAsync(Hostname, configuration, ct);
            Audit.Write("Sauvegarde avant modification", "Configuration conservée chiffrée avant toute écriture.");
            // Backups can take time. Revalidate evidence immediately before the first mutation.
            SafetyPolicy.RequireSafeChange(plan, safety ?? SafetyContext.Unknown, Kind);
            await ExecutePlanAsync(plan, progress, ct);
            Audit.Write(plan.Title, AcceptedMessage);
        }
        catch
        {
            Audit.Write(plan.Title, $"Échec après {progress.Completed}/{plan.Commands.Count} commandes ; configuration potentiellement partielle.");
            if (IsConnected && progress.Completed > 0 && plan.Kind != ChangeKind.Save) await RecoverAsync(plan);
            throw;
        }
        finally { gate.Release(); }
    }

    /// <summary>Live checks a plan cannot carry: the VLAN still exists, is unused before deletion, the port did not move.</summary>
    protected virtual async Task VerifyPlanAsync(CommandPlan plan, CancellationToken ct)
    {
        if (plan.Kind is not (ChangeKind.AccessVlan or ChangeKind.DeleteVlan)) return;
        var snapshot = await RefreshSnapshotAsync(ct);
        if (!snapshot.Vlans.Any(v => v.Id == plan.Vlan)) throw new InvalidOperationException("Ce VLAN n'existe plus. Actualisez la vue.");
        if (plan.Kind == ChangeKind.DeleteVlan && snapshot.Ports.Any(p => p.Vlan == plan.Vlan.ToString()))
            throw new InvalidOperationException("Suppression bloquée : des ports sont encore affectés à ce VLAN.");
        // OS9 / EdgeSwitch plans remove the previous access VLAN explicitly: it must still be the current one.
        if (plan.Kind == ChangeKind.AccessVlan && plan.CurrentVlan is { } expected
            && snapshot.Ports.FirstOrDefault(p => PortNames.Same(p.Name, plan.Port)) is { } port
            && port.Vlan != expected.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Le VLAN du port a changé depuis la préparation de la modification. Actualisez la vue.");
    }

    /// <summary>Sends the steps in order; a step whose output misses its expected acknowledgement fails the plan.</summary>
    protected virtual async Task ExecutePlanAsync(CommandPlan plan, PlanProgress progress, CancellationToken ct)
    {
        foreach (var step in plan.Steps)
        {
            var output = await RunStepAsync(step, ct);
            progress.Completed++;
            if (step.Expect is { } expect && !Regex.IsMatch(output ?? "", expect, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new CliException(step.ExpectFailure ?? $"Le switch n'a pas confirmé « {step.Command} ».");
        }
    }

    // ---- Vendor-neutral API ---------------------------------------------------------------

    /// <summary>Platform family and identity of the connected device.</summary>
    public Task<DeviceInfo> DetectDeviceAsync(CancellationToken ct = default) =>
        Locked(async () => new DeviceInfo(Vendor, Platform.DisplayName, await IdentityAsync(ct)), ct);

    public async Task<IReadOnlyList<PortInfo>> GetPortsAsync(CancellationToken ct = default) => (await ReadSnapshotAsync(ct)).Ports;

    public async Task<IReadOnlyList<VlanInfo>> GetVlansAsync(CancellationToken ct = default) => (await ReadSnapshotAsync(ct)).Vlans;

    /// <summary>
    /// Puts <paramref name="port"/> in access mode on <paramref name="vlan"/> through the same
    /// safety pipeline as the GUI. The current access VLAN is read first: platforms that manage
    /// membership per VLAN (Dell OS9, EdgeSwitch) remove the port from it explicitly.
    /// </summary>
    public async Task SetPortVlanAsync(string port, int vlan, SafetyContext? safety = null, bool dryRun = false, CancellationToken ct = default)
    {
        var name = PortNames.Validate(Vendor, port);
        var snapshot = LastSnapshot ?? await ReadSnapshotAsync(ct);
        var current = snapshot.Ports.FirstOrDefault(p => PortNames.Same(p.Name, name));
        int? currentVlan = current is not null && int.TryParse(current.Vlan, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id is >= 1 and <= 4094 and not (>= 1002 and <= 1005) ? id : null;
        await ApplyAsync(CommandPlan.Access(name, vlan, Vendor, currentVlan), dryRun, ct, safety);
    }

    public async Task<LinkStatus> GetLinkStatusAsync(string port, CancellationToken ct = default)
    {
        var counters = await ReadCountersAsync(port, ct);
        return new(port, counters.LinkState == "Actif", counters.LinkState, counters.Speed, counters.Duplex);
    }

    /// <summary>write memory / save / copy … startup-config, or nothing on commit and implicit-save platforms.</summary>
    public Task SaveConfigAsync(bool dryRun = false, CancellationToken ct = default) => ApplyAsync(CommandPlan.Save(Vendor), dryRun, ct);
}

/// <summary>
/// Driver over an interactive CLI (SSH or serial console): the dialect gives the export
/// command and the recovery commands, steps with a documented confirmation are answered once.
/// </summary>
public abstract class CliSwitchDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup) : SwitchDriver(audit, backup)
{
    protected ICliSession Session { get; } = session;
    public override ConnectionKind Kind => Session.Kind;
    public override bool IsConnected => Session.IsConnected;
    protected override string Hostname => Session.Hostname;

    protected Task<string> Run(string command, CancellationToken ct) => Session.ExecuteAsync(command, ct);

    protected override Task<string> ExportCoreAsync(CancellationToken ct) => Session.ExecuteAsync(Dialect.ExportCommand, ct);

    protected override Task<string> RunStepAsync(PlanStep step, CancellationToken ct) => step.Confirm is { } answer
        ? Session.ExecuteConfirmedAsync(step.Command, answer, ct)
        : Session.ExecuteAsync(step.Command, ct);

    protected override async Task RecoverAsync(CommandPlan plan)
    {
        try
        {
            foreach (var command in Dialect.Recovery)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Session.ExecuteAsync(command, recovery.Token);
            }
        }
        catch { await Session.DisposeAsync(); }
    }

    public override ValueTask DisposeAsync() => Session.DisposeAsync();
}
