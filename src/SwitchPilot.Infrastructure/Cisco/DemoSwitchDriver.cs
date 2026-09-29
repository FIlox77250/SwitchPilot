using SwitchPilot.Core;
using SwitchPilot.Core.Diagnostics;

namespace SwitchPilot.Infrastructure.Cisco;

public sealed class DemoSwitchDriver(IAuditSink audit) : ISwitchDriver
{
    private bool connected = true;
    private readonly List<PortInfo> ports = Enumerable.Range(1, 24).Select(i => new PortInfo($"Fa0/{i}", i == 14 ? "Bureau 214" : i <= 8 ? $"Bureau {200 + i}" : "",
        i <= 8 || i == 14 ? "connected" : "notconnect", i == 14 ? "10" : "20", i <= 8 || i == 14 ? "a-full" : "auto", i <= 8 || i == 14 ? "a-100" : "auto", "10/100BaseTX", "access"))
        .Append(new("Gi0/1", "Uplink distribution", "connected", "trunk", "a-full", "a-1000", "10/100/1000BaseTX", "trunk"))
        .Append(new("Gi0/2", "Réserve", "notconnect", "1", "auto", "auto", "10/100/1000BaseTX", "access")).ToList();
    private readonly List<VlanInfo> vlans = [new(1, "default", "active", "Gi0/2"), new(10, "TECHNIQUE", "active", "Fa0/14"), new(20, "BUREAUX", "active", "Fa0/1–Fa0/24")];
    public bool IsConnected => connected;
    public bool IsDemo => true;
    public async Task<DetectionObservation> ReadDetectionAsync(string mac, CancellationToken ct = default) => new(await ReadSnapshotAsync(ct),
        (await ReadMacTableAsync(ct)).Where(e => e.Mac == Core.Cisco.CiscoParser.NormalizeMac(mac)).ToArray());
    public Task<SwitchSnapshot> ReadSnapshotAsync(CancellationToken ct = default) => Task.FromResult(new SwitchSnapshot(new("Switch-A", "WS-C2960+24TC-L", "15.2(4)E — démonstration"), ports.ToArray(), vlans.ToArray()));
    public Task<IReadOnlyList<MacEntry>> ReadMacTableAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MacEntry>>([new(int.TryParse(ports.Single(p => p.Name == "Fa0/14").Vlan, out var id) ? id : 10, "001122334455", "DYNAMIC", "Fa0/14")]);
    public Task<InterfaceCounters> ReadCountersAsync(string port, CancellationToken ct = default) => Task.FromResult(new InterfaceCounters(0, 0, 0, port.StartsWith("Gi") ? "1000" : "100", "Full", ports.Single(p => p.Name == port).IsUp ? "Actif" : "Inactif"));
    public Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default)
    {
        SafetyPolicy.RequireSafeTdr(ports.Single(p => p.Name == port), safety);
        audit.Write($"TDR {port}", "Démonstration : résultat fictif.");
        return Task.FromResult(new TdrResult(port, [new("A", "18 +/- 2 meters", "Pair B", "OK"), new("B", "18 +/- 2 meters", "Pair A", "OK"), new("C", "19 +/- 2 meters", "Pair D", "OK"), new("D", "19 +/- 2 meters", "Pair C", "OK")], "Données fictives du mode démonstration."));
    }
    public Task ApplyAsync(CommandPlan plan, bool dryRun, CancellationToken ct = default)
    {
        audit.Write(plan.Title, dryRun ? "Simulation : aucune modification." : "Démonstration : modification en mémoire uniquement.");
        if (dryRun) return Task.CompletedTask;
        if (plan.Kind == ChangeKind.CreateVlan)
        {
            vlans.RemoveAll(v => v.Id == plan.Vlan); vlans.Add(new(plan.Vlan!.Value, plan.Value!, "active", ""));
        }
        else if (plan.Kind == ChangeKind.DeleteVlan)
        {
            if (ports.Any(p => p.Vlan == plan.Vlan.ToString())) throw new InvalidOperationException("Des ports utilisent encore ce VLAN.");
            vlans.RemoveAll(v => v.Id == plan.Vlan);
        }
        else if (plan.Port is not null)
        {
            var index = ports.FindIndex(p => p.Name == plan.Port);
            var p = ports[index];
            ports[index] = plan.Kind switch
            {
                ChangeKind.AccessVlan => p with { Vlan = plan.Vlan.ToString()!, Mode = "access" },
                ChangeKind.Trunk => p with { Vlan = "trunk", Mode = "trunk" },
                ChangeKind.Description => p with { Description = plan.Value! },
                ChangeKind.EnablePort => p with { Status = "notconnect" },
                ChangeKind.DisablePort => p with { Status = "disabled" },
                _ => p
            };
        }
        return Task.CompletedTask;
    }
    public Task<string> ExportAsync(CancellationToken ct = default) => Task.FromResult("! Configuration fictive\nhostname Switch-A\n" + string.Join('\n', ports.Select(p => $"interface {p.Name}\n description {p.Description}\n switchport mode {p.Mode}\n!")));
    public ValueTask DisposeAsync() { connected = false; return ValueTask.CompletedTask; }
}
