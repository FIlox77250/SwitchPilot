using System.Text.RegularExpressions;
using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// Ubiquiti EdgeSwitch (Broadcom FASTPATH CLI). Ports come from "show port all" (TextFSM
/// template), access/trunk membership from the running configuration. Writes go through
/// "vlan database" / "configure" and "write memory" answered with a single-key "y".
/// Experimental: built from vendor documentation and recorded outputs.
/// </summary>
public sealed class EdgeSwitchDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null)
    : CliSwitchDriver(session, audit, backup)
{
    private static readonly Regex ConfigMode = new(@"\)\s?\([^()]+\)\s?#$");
    private SwitchIdentity? identity;
    public override SwitchVendor Vendor => SwitchVendor.UbiquitiEdge;

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        EdgeSwitchParser.Identity(Session.Hostname, await Run("show version", ct));

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = EdgeSwitchParser.PortAll(await Run("show port all", ct));
        IReadOnlyDictionary<string, EdgeSwitchParser.PortConfig> config = new Dictionary<string, EdgeSwitchParser.PortConfig>();
        try { config = EdgeSwitchParser.RunningConfig(await Run("show running-config", ct)); }
        catch (CliException) { Audit.Write("Lecture des modes", "Configuration non lisible ; descriptions et modes inconnus."); }
        IReadOnlyDictionary<string, int> pvids = new Dictionary<string, int>();
        try { pvids = EdgeSwitchParser.Pvids(await Run("show vlan port all", ct)); }
        catch (CliException) { /* PVID from the configuration only. */ }
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = EdgeSwitchParser.Vlans(await Run("show vlan brief", ct)); }
        catch (FormatException) { vlans = []; Audit.Write("Lecture des VLAN", "Format « show vlan brief » EdgeSwitch non reconnu ; liste vide."); }
        return new(info, EdgeSwitchParser.Merge(ports, config, pvids), vlans);
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) =>
        ParseKit.Macs(Vendor, await Run("show mac-addr-table", ct));

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        EdgeSwitchParser.Counters(await Run($"show interface ethernet {port}", ct), await Run($"show port {port}", ct));

    // The configuration depth varies (Config, Interface, Vlan): walk back with "exit".
    protected override async Task RecoverAsync(CommandPlan plan)
    {
        try
        {
            for (var depth = 0; depth < 4 && ConfigMode.IsMatch(Session.Prompt); depth++)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Session.ExecuteAsync("exit", recovery.Token);
            }
        }
        catch { await Session.DisposeAsync(); }
    }
}
