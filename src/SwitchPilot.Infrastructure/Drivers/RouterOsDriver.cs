using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// MikroTik RouterOS 7 with bridge VLAN filtering. Reads use "print terse without-paging";
/// every accepted command is stored immediately, so there is no save and no mode to leave.
/// Experimental: built from vendor documentation and recorded outputs.
/// </summary>
public sealed class RouterOsDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null)
    : CliSwitchDriver(session, audit, backup)
{
    private SwitchIdentity? identity;
    public override SwitchVendor Vendor => SwitchVendor.MikroTik;
    protected override string AcceptedMessage => "Commandes acceptées par RouterOS (enregistrement immédiat) ; relecture de l'état nécessaire.";

    private Task<string> Print(string path, CancellationToken ct) => Run($"{path} print terse without-paging", ct);

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        RouterOsParser.Identity(await Run("/system identity print", ct), await Run("/system resource print", ct), Session.Hostname);

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var interfaces = await Print("/interface", ct);
        var bridgePorts = await Print("/interface bridge port", ct);
        var bridgeVlans = await Print("/interface bridge vlan", ct);
        return new(info, RouterOsParser.Ports(interfaces, bridgePorts, bridgeVlans), RouterOsParser.Vlans(bridgeVlans));
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) =>
        RouterOsParser.Macs(await Print("/interface bridge host", ct));

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct)
    {
        // Bonding interfaces have no ethernet monitor: link and errors stay unknown.
        string monitor = "", stats = "";
        try { monitor = await Run($"/interface ethernet monitor {port} once", ct); }
        catch (CliException) { /* Not an ethernet interface. */ }
        try { stats = await Run($"/interface ethernet print stats without-paging where name=\"{port}\"", ct); }
        catch (CliException) { /* Counters unknown. */ }
        return RouterOsParser.Counters(monitor, stats);
    }
}
