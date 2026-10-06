using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// UniFi switch reached over its local SSH shell. Read-only on purpose: the controller is the
/// source of truth and overwrites any local change at the next provisioning, so writes go
/// through <see cref="UniFiControllerDriver"/>. Experimental: built from recorded outputs.
/// </summary>
public sealed class UniFiSshDriver(ICliSession session, IAuditSink audit) : CliSwitchDriver(session, audit, null)
{
    private SwitchIdentity? identity;
    public override SwitchVendor Vendor => SwitchVendor.UniFi;
    protected override string? WriteRefusal =>
        "UniFi en SSH local : lecture seule. Le contrôleur réécrit la configuration du switch à chaque provisionnement ; connectez-vous en mode « API contrôleur UniFi » pour modifier.";

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        UniFiShellParser.Identity(await Run("info", ct), Session.Hostname);

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = UniFiShellParser.Ports(await Run("swctrl port show", ct));
        return new(info, ports, UniFiShellParser.Vlans(ports));
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) =>
        UniFiShellParser.Macs(await Run("swctrl mac show", ct));

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        UniFiShellParser.Counters(await Run("swctrl port show", ct), port);

    // The provisioned configuration file is the local equivalent of a running configuration.
    protected override Task<string> ExportCoreAsync(CancellationToken ct) => Run("cat /tmp/system.cfg", ct);

    protected override Task RecoverAsync(CommandPlan plan) => Task.CompletedTask;
}
