using System.Text.RegularExpressions;

namespace SwitchPilot.Core.Platforms;

/// <summary>
/// Huawei VRP (S-series V200, CloudEngine V200/V300): "system-view" … "return", port link-type,
/// "save" with a [Y/N] confirmation. On two-stage-commit firmware (prompt "[~HUAWEI]") the
/// driver inserts "commit" before "return".
/// </summary>
public sealed class HuaweiDialect() : ConfigDialect(SwitchVendor.Huawei)
{
    public override string Family => "vrp";
    public override string ExportCommand => "display current-configuration";
    public override int DescriptionMax => 80;
    public override IReadOnlyList<string> Recovery => ["return"];
    private IReadOnlyList<PlanStep> PortSteps(string port, params string[] lines) => Steps(["system-view", $"interface {If(port)}", .. lines, "return"]);

    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) =>
        PortSteps(port, "port link-type access", $"port default vlan {vlan}");
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical)
    {
        // "allow-pass vlan" takes at most 10 items ("10", "30 to 40") per command line.
        var lines = new List<string> { "port link-type trunk", "undo port trunk allow-pass vlan all" };
        foreach (var chunk in Ranges(allowed).Chunk(10))
            lines.Add("port trunk allow-pass vlan " + string.Join(' ', chunk.Select(r => r.First == r.Last ? r.First.ToString() : $"{r.First} to {r.Last}")));
        lines.Add($"port trunk pvid vlan {native}");
        return PortSteps(port, [.. lines]);
    }
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) => PortSteps(port, enabled ? "undo shutdown" : "shutdown");
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => PortSteps(port, text.Length == 0 ? "undo description" : $"description {text}");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Steps("system-view", $"vlan {id}", $"description {name}", "return");
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => Steps("system-view", $"undo vlan {id}", "return");
    public override IReadOnlyList<PlanStep> Save() =>
        [new("save", Confirm: "y\n", Expect: "(?i)successfully", ExpectFailure: "VRP n'a pas confirmé la sauvegarde (« Save the configuration successfully »).")];
}

/// <summary>
/// Juniper Junos ELS (EX2300/3400/4300, QFX): hierarchical "set"/"delete" in a private candidate
/// configuration, activated atomically by "commit and-quit". A commit is persistent, so there
/// is no separate save; after a failure the candidate is discarded by "rollback 0".
/// </summary>
public sealed class JunosDialect() : ConfigDialect(SwitchVendor.Juniper)
{
    private static readonly Regex VlanName = new("^[A-Za-z][A-Za-z0-9_.-]{0,62}$");
    public override string Family => "junos";
    public override string ExportCommand => "show configuration | display set";
    public override string ForbiddenText => "\"\\;";
    public override int DescriptionMax => 200;
    public override int VlanNameMax => 63;
    public override IReadOnlyList<string> Recovery => ["rollback 0", "exit configuration-mode"];

    private static IReadOnlyList<PlanStep> Commit(params string[] lines) =>
        [new("configure private"), .. lines.Select(l => new PlanStep(l)),
         new("commit and-quit", Expect: "commit complete", ExpectFailure: "Junos n'a pas confirmé le commit (« commit complete »). La configuration candidate a été abandonnée.")];
    private string Unit(string port) => $"interfaces {If(port)} unit 0 family ethernet-switching";

    public override void ValidateVlanName(string name)
    {
        if (!VlanName.IsMatch(name)) throw new ArgumentException("Nom de VLAN Junos : lettre initiale, puis lettres, chiffres, « _ », « - » ou « . ».");
    }
    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) => Commit(
        $"delete {Unit(port)} vlan members",
        $"delete interfaces {If(port)} native-vlan-id",
        $"set {Unit(port)} interface-mode access",
        $"set {Unit(port)} vlan members {vlan}");
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) => Commit(
        $"delete {Unit(port)} vlan members",
        $"set {Unit(port)} interface-mode trunk",
        $"set {Unit(port)} vlan members [ {RangeList(allowed, " ")} ]",
        $"set interfaces {If(port)} native-vlan-id {native}");
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) =>
        Commit(enabled ? $"delete interfaces {If(port)} disable" : $"set interfaces {If(port)} disable");
    public override IReadOnlyList<PlanStep> Describe(string port, string text) =>
        Commit(text.Length == 0 ? $"delete interfaces {If(port)} description" : $"set interfaces {If(port)} description \"{text}\"");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Commit($"set vlans {name} vlan-id {id}");
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Junos identifie un VLAN par son nom : actualisez la liste des VLAN.");
        ValidateVlanName(name);
        return Commit($"delete vlans {name}");
    }
    public override IReadOnlyList<PlanStep> Save() => [];
}

/// <summary>
/// MikroTik RouterOS 7 with bridge VLAN filtering: a port's access VLAN is its bridge-port PVID,
/// VLANs are /interface bridge vlan entries. Every accepted command is stored immediately.
/// </summary>
public sealed class RouterOsDialect() : ConfigDialect(SwitchVendor.MikroTik)
{
    public override string Family => "routeros";
    public override string ExportCommand => "/export terse";
    // Characters with a meaning in the RouterOS scripting language.
    public override string ForbiddenText => "$[]{}\"\\;";
    public override int DescriptionMax => 64;
    public override bool SupportsTrunk => false;
    public override IReadOnlyList<string> Recovery => [];

    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) =>
        Steps($"/interface bridge port set [find interface={If(port)}] pvid={vlan} frame-types=admit-only-untagged-and-priority-tagged");
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        throw new NotSupportedException("Trunk non pris en charge sur RouterOS dans cette version : déclarez les VLAN étiquetés dans /interface bridge vlan.");
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) => Steps($"/interface {(enabled ? "enable" : "disable")} [find name={If(port)}]");
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => Steps($"/interface set [find name={If(port)}] comment=\"{text}\"");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) =>
        Steps($"/interface bridge vlan add bridge=[/interface bridge find] vlan-ids={id} comment=\"{name}\"");
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => Steps($"/interface bridge vlan remove [find vlan-ids={id}]");
    public override IReadOnlyList<PlanStep> Save() => [];
}

/// <summary>
/// Ubiquiti EdgeSwitch (EdgeOS / Broadcom FASTPATH CLI): "configure" … "exit", VLAN membership
/// by "vlan participation include/exclude", tagging per VLAN, VLANs in "vlan database",
/// "write memory" asks "(y/n)" and is answered with a single key.
/// </summary>
public sealed class EdgeSwitchDialect() : ConfigDialect(SwitchVendor.UbiquitiEdge)
{
    public override string Family => "edgeswitch";
    public override int DescriptionMax => 64;
    public override string ForbiddenText => "\"";
    // Recovery depends on the current mode depth; the driver walks back with "exit".
    public override IReadOnlyList<string> Recovery => [];
    public override string TrunkCaveat => " (les autres VLAN déjà inclus restent membres)";
    private IReadOnlyList<PlanStep> PortSteps(string port, params string[] lines) => Steps(["configure", $"interface {If(port)}", .. lines, "exit", "exit"]);

    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan)
    {
        var lines = new List<string> { $"vlan pvid {vlan}", $"vlan participation include {vlan}", $"no vlan tagging {vlan}" };
        if (vlan != 1) lines.Add("vlan participation exclude 1");
        if (currentVlan is { } current && current != vlan && current != 1) lines.Add($"vlan participation exclude {current}");
        return PortSteps(port, [.. lines]);
    }
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical)
    {
        var lines = new List<string> { $"vlan pvid {native}", $"vlan participation include {canonical}", $"no vlan tagging {native}" };
        var tagged = allowed.Where(id => id != native).ToArray();
        if (tagged.Length > 0) lines.Add($"vlan tagging {RangeList(tagged)}");
        if (!allowed.Contains(1)) lines.Add("vlan participation exclude 1");
        return PortSteps(port, [.. lines]);
    }
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) => PortSteps(port, enabled ? "no shutdown" : "shutdown");
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => PortSteps(port, text.Length == 0 ? "no description" : $"description \"{text}\"");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Steps("vlan database", $"vlan {id}", $"vlan name {id} {name}", "exit");
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => Steps("vlan database", $"no vlan {id}", "exit");
    public override IReadOnlyList<PlanStep> Save() =>
        [new("write memory", Confirm: "y", Expect: "(?i)saved|success", ExpectFailure: "EdgeSwitch n'a pas confirmé la sauvegarde (« Configuration Saved! »).")];
}

/// <summary>
/// UniFi switches are configured by their controller (port_overrides on the device object);
/// a local CLI change is overwritten at the next provisioning. The steps below are a readable
/// description of the API request shown in the confirmation dialog; they are never typed.
/// </summary>
public sealed class UniFiDialect() : ConfigDialect(SwitchVendor.UniFi)
{
    public override string Family => "unifi";
    public override string ExportCommand => "GET stat/device";
    public override int DescriptionMax => 64;
    public override string ForbiddenText => "\"\\";
    public override IReadOnlyList<string> Recovery => [];
    private static PlanStep[] Api(string port, string change) => [new($"API contrôleur · port_overrides[{port}] : {change}")];

    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) =>
        Api(port, $"réseau natif = VLAN {vlan}, VLAN étiquetés = aucun (block_all)");
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        Api(port, $"réseau natif = VLAN {native}, VLAN étiquetés = {RangeList(allowed.Where(id => id != native))} (custom)");
    // The controller schema for disabling a port changed across Network releases: refused
    // rather than guessed. Use the UniFi application for this change.
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) =>
        throw new NotSupportedException("UniFi : l'activation / désactivation d'un port se fait depuis l'application UniFi (non prise en charge par Switch Pilot).");
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => Api(port, text.Length == 0 ? "nom effacé" : $"nom = « {text} »");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => [new($"API contrôleur · POST rest/networkconf : réseau « {name} », VLAN {id} (vlan-only)")];
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => [new($"API contrôleur · DELETE rest/networkconf : réseau VLAN {id} (vlan-only uniquement)")];
    public override IReadOnlyList<PlanStep> Save() => [];
}
