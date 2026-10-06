using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// IOS-like platforms other than IOS itself: Cisco NX-OS, Arista EOS and Dell OS6 / OS9 / OS10.
/// They share the "configure … end" model of the common pipeline; only the read commands and
/// table layouts differ. Experimental: built from vendor documentation and recorded outputs.
/// </summary>
public sealed class CiscoLikeDriver : CliSwitchDriver
{
    private SwitchIdentity? identity;

    public CiscoLikeDriver(SwitchVendor vendor, ICliSession session, IAuditSink audit, IConfigurationBackup? backup = null) : base(session, audit, backup)
    {
        if (vendor is not (SwitchVendor.CiscoNxos or SwitchVendor.Arista or SwitchVendor.DellOs6 or SwitchVendor.DellOs9 or SwitchVendor.DellOs10))
            throw new ArgumentException("Plateforme non gérée par le driver Cisco-like.", nameof(vendor));
        Vendor = vendor;
    }

    public override SwitchVendor Vendor { get; }

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) => identity ??=
        CiscoLikeParser.Identity(Vendor, Session.Hostname, await Run("show version", ct));

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var info = await IdentityAsync(ct);
        IReadOnlyList<PortInfo> ports;
        IReadOnlyList<VlanInfo> vlans = [];
        switch (Vendor)
        {
            case SwitchVendor.DellOs6:
                ports = CiscoLikeParser.DellOs6Status(await Run("show interfaces status", ct));
                try
                {
                    vlans = CiscoLikeParser.DellOs6Vlans(await Run("show vlan", ct));
                    var membership = CiscoLikeParser.MembershipVlans(Vendor, vlans);
                    ports = ports.Select(p => membership.TryGetValue(p.Name, out var v)
                        ? p with { Vlan = v, Mode = v == "trunk" ? "trunk" : "access" } : p).ToArray();
                }
                catch (FormatException) { VlanUnreadable(); }
                break;
            case SwitchVendor.DellOs9 or SwitchVendor.DellOs10:
                ports = Vendor == SwitchVendor.DellOs9
                    ? CiscoLikeParser.DellOs9Status(await Run("show interfaces status", ct))
                    : CiscoLikeParser.DellOs10Status(await Run("show interface status", ct));
                try
                {
                    var (list, untagged, tagged) = CiscoLikeParser.DellVlans(Vendor, await Run("show vlan", ct));
                    vlans = list;
                    ports = ports.Select(p => tagged.Contains(p.Name) ? p with { Vlan = "trunk", Mode = "trunk" }
                        : untagged.TryGetValue(p.Name, out var id) ? p with { Vlan = id.ToString(), Mode = "access" } : p).ToArray();
                }
                catch (FormatException) { VlanUnreadable(); }
                break;
            default:
                ports = CiscoLikeParser.StatusTable(Vendor, await Run(Vendor == SwitchVendor.CiscoNxos ? "show interface status" : "show interfaces status", ct));
                try { vlans = ParseKit.VlanBrief(await Run("show vlan brief", ct)); }
                catch (FormatException) { VlanUnreadable(); }
                break;
        }
        return new(info, ports, vlans);
    }

    private void VlanUnreadable() => Audit.Write("Lecture des VLAN", $"Format {Platform.ShortName} non reconnu ; liste vide.");

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct) =>
        ParseKit.Macs(Vendor, await Run(Vendor == SwitchVendor.DellOs9 ? "show mac-address-table" : "show mac address-table", ct));

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct)
    {
        var command = PortNames.CommandForm(Vendor, port);
        if (Vendor != SwitchVendor.DellOs6)
            return ParseKit.Counters(await Run($"{(Vendor is SwitchVendor.CiscoNxos or SwitchVendor.DellOs10 ? "show interface" : "show interfaces")} {command}", ct));
        // OS6 (FASTPATH heritage): link from the status row, errors from the dotted statistics when readable.
        var row = CiscoLikeParser.DellOs6Status(await Run($"show interfaces status {command}", ct)).FirstOrDefault();
        long? crc = null, input = null, output = null;
        try
        {
            var fields = ParseKit.DottedFields(await Run($"show statistics {command}", ct));
            crc = ParseKit.Long(fields.GetValueOrDefault("FCS Errors"));
            input = ParseKit.Long(fields.GetValueOrDefault("Total Packets Received with MAC Errors"));
            output = ParseKit.Long(fields.GetValueOrDefault("Total Transmit Errors"));
        }
        catch (CliException) { /* Error counters unknown; link state still reported. */ }
        return row is null ? new(crc, null, input, "Inconnue", "Inconnu", "Inconnu", output)
            : new(crc, null, input, row.Speed, row.Duplex, row.IsUp ? "Actif" : "Inactif", output);
    }
}
