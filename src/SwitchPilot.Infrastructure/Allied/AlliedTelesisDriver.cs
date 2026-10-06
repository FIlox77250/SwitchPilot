using SwitchPilot.Core;
using SwitchPilot.Core.Allied;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Infrastructure.Drivers;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Allied;

/// <summary>
/// Reference driver: Allied Telesis switches running AlliedWare Plus. The read-only command
/// vocabulary and the access/trunk syntax are close enough to Cisco IOS to share the
/// command plans (same "ios" dialect family); the identity, VLAN and counter layouts are
/// parsed by <see cref="AlliedTelesisParser"/>. Cable TDR is not exposed, so only the
/// passive control remains available.
/// </summary>
public sealed class AlliedTelesisDriver(ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null)
    : CliSwitchDriver(session, audit, backup)
{
    private SwitchIdentity? identity;
    private bool filteredMacSupported = true;
    private string macCommand = "show mac address-table";
    public override SwitchVendor Vendor => SwitchVendor.AlliedTelesis;
    protected override string CommandPort(string port) => CommandPlan.Interface(port);

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var ports = CiscoParser.Ports(await Run("show interface status", ct));
        var vlanOutput = await Run("show vlan brief", ct);
        IReadOnlyList<VlanInfo> vlans;
        try { vlans = AlliedTelesisParser.Vlans(vlanOutput); }
        catch (FormatException) { vlans = []; Audit.Write("Lecture des VLAN", "Format « show vlan brief » AlliedWare Plus non reconnu ; liste vide."); }
        var modes = AlliedTelesisParser.PortModes(vlanOutput);
        if (modes.Count > 0) ports = ports.Select(p => p with { Mode = modes.GetValueOrDefault(p.Name, p.Mode) }).ToArray();
        return new(info, ports, vlans);
    }

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct)
    {
        if (identity is not null) return identity;
        identity = AlliedTelesisParser.Identity(Session.Hostname, await Run("show version", ct));
        if (identity.Model.Contains("inconnu", StringComparison.OrdinalIgnoreCase))
        {
            // The board/model line lives in `show system`, not `show version`, on AW+.
            try
            {
                var system = await Run("show system", ct);
                if (AlliedTelesisParser.Model(system) is { } model) identity = identity with { Model = model };
            }
            catch (CliException) { /* Keep the generic model name. */ }
        }
        return identity;
    }

    protected override async Task<DetectionObservation> DetectionCoreAsync(string mac, CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        var entries = (await ReadMacs(mac, ct)).Where(e => e.Mac == mac).ToArray();
        if (entries.Length == 0) return NoMatch(info, entries);
        var ports = CiscoParser.Ports(await Run("show interface status", ct));
        // Re-read the VLAN membership now: never reuse an old access/trunk claim for a
        // safety-relevant direct-candidate decision.
        var modes = AlliedTelesisParser.PortModes(await Run("show vlan brief", ct));
        var matched = entries.Select(e => e.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = ports.Where(p => matched.Contains(p.Name))
            .Select(p => p with { Mode = modes.GetValueOrDefault(p.Name, p.Mode), Description = DescriptionOf(p) })
            .ToList();
        return new(new(info, current, LastSnapshot?.Vlans ?? []), entries);
    }

    private async Task<IReadOnlyList<MacEntry>> ReadMacs(string? mac, CancellationToken ct)
    {
        var suffix = mac is not null && filteredMacSupported ? $" address {mac[..4]}.{mac[4..8]}.{mac[8..]}" : "";
        string output;
        try { output = await Run(macCommand + suffix, ct); }
        catch (CliException e) when (e.Failure == CliFailure.Unsupported)
        {
            if (suffix.Length > 0) { filteredMacSupported = false; return await ReadMacs(null, ct); }
            if (macCommand != "show mac address-table") throw;
            macCommand = "show mac-address-table";
            output = await Run(macCommand, ct);
        }
        return AlliedTelesisParser.Macs(output);
    }

    protected override Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) => ReadMacs(null, ct);

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        AlliedTelesisParser.Counters(await Run($"show interface {port}", ct));

    public override Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default) =>
        throw new NotSupportedException("Le test de câble TDR n'est pas exposé par l'interface AlliedWare Plus prise en charge. Utilisez le contrôle passif.");
}
