namespace SwitchPilot.Core.Platforms;

/// <summary>
/// One command step of a plan. <see cref="Confirm"/> is the answer to the single confirmation
/// the command is documented to ask; <see cref="Expect"/> is a regular expression the output must
/// match for the step to count as successful (for example "[OK]" after "write memory").
/// </summary>
public sealed record PlanStep(string Command, string? Confirm = null, string? Expect = null, string? ExpectFailure = null)
{
    public override string ToString() => Confirm is null ? Command : $"{Command}   ← réponse « {Confirm.TrimEnd('\n')} »";
}

/// <summary>
/// Configuration dialect of a platform: turns validated values into the exact command
/// sequence, including how to enter and leave configuration mode, how to persist and how to
/// back out after a partial failure. Dialects never receive free text: ports, VLAN ids and
/// names are validated by <see cref="CommandPlan"/> before reaching them.
/// </summary>
public abstract class ConfigDialect(SwitchVendor vendor)
{
    public SwitchVendor Vendor { get; } = vendor;
    /// <summary>Plans are interchangeable between vendors of the same family only.</summary>
    public abstract string Family { get; }
    /// <summary>Read-only command whose output is stored (encrypted) before any write.</summary>
    public virtual string ExportCommand => "show running-config";
    public virtual int DescriptionMax => 200;
    public virtual int VlanNameMax => 32;
    /// <summary>Characters refused in descriptions and VLAN names on top of the common rules.</summary>
    public virtual string ForbiddenText => "";
    public virtual bool SupportsTrunk => true;
    /// <summary>Commands sent, each with a short timeout, after a partially applied plan.</summary>
    public virtual IReadOnlyList<string> Recovery => ["end"];
    /// <summary>Optional caveat appended to a trunk plan title.</summary>
    public virtual string TrunkCaveat => "";

    public abstract IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan);
    public abstract IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical);
    public abstract IReadOnlyList<PlanStep> Enabled(string port, bool enabled);
    public abstract IReadOnlyList<PlanStep> Describe(string port, string text);
    public abstract IReadOnlyList<PlanStep> CreateVlan(int id, string name);
    public abstract IReadOnlyList<PlanStep> DeleteVlan(int id, string? name);
    public abstract IReadOnlyList<PlanStep> Save();

    /// <summary>Extra platform rule on VLAN names (throws <see cref="ArgumentException"/>).</summary>
    public virtual void ValidateVlanName(string name) { }

    protected string If(string port) => PortNames.CommandForm(Vendor, port);
    protected static PlanStep[] Steps(params string[] commands) => commands.Select(c => new PlanStep(c)).ToArray();
    protected static string Quoted(string text) => text.Contains(' ') ? $"\"{text}\"" : text;

    /// <summary>Compresses VLAN ids into (first, last) ranges: 1,2,3,7 → (1,3),(7,7).</summary>
    public static IReadOnlyList<(int First, int Last)> Ranges(IEnumerable<int> ids)
    {
        var result = new List<(int, int)>();
        foreach (var id in ids.Distinct().Order())
        {
            if (result.Count > 0 && result[^1].Item2 == id - 1) result[^1] = (result[^1].Item1, id);
            else result.Add((id, id));
        }
        return result;
    }
    public static string RangeList(IEnumerable<int> ids, string separator = ",", string to = "-") =>
        string.Join(separator, Ranges(ids).Select(r => r.First == r.Last ? r.First.ToString() : $"{r.First}{to}{r.Last}"));
}

public static class ConfigDialects
{
    private static readonly Dictionary<SwitchVendor, ConfigDialect> Map = new()
    {
        [SwitchVendor.Cisco] = new CiscoIosDialect(),
        [SwitchVendor.AlliedTelesis] = new AlliedWarePlusDialect(),
        [SwitchVendor.AlliedS95] = new ReadOnlyDialect(SwitchVendor.AlliedS95, "s95",
            "Les modifications ne sont pas encore prises en charge sur la famille AT-S95 / AT-8000GS (dialecte de configuration différent, non validé sur matériel). Lecture, détection du port et export de la configuration restent disponibles."),
        [SwitchVendor.CiscoNxos] = new NxosDialect(),
        [SwitchVendor.Arista] = new AristaDialect(),
        [SwitchVendor.DellOs6] = new DellOs6Dialect(),
        [SwitchVendor.DellOs9] = new DellOs9Dialect(),
        [SwitchVendor.DellOs10] = new DellOs10Dialect(),
        [SwitchVendor.Huawei] = new HuaweiDialect(),
        [SwitchVendor.Juniper] = new JunosDialect(),
        [SwitchVendor.MikroTik] = new RouterOsDialect(),
        [SwitchVendor.UbiquitiEdge] = new EdgeSwitchDialect(),
        [SwitchVendor.UniFi] = new UniFiDialect()
    };

    public static ConfigDialect For(SwitchVendor vendor) =>
        Map.TryGetValue(vendor, out var dialect) ? dialect : throw new ArgumentException("Constructeur de switch inconnu.");
}

/// <summary>Cisco IOS / IOS-XE: the historical command sequences, unchanged byte for byte.</summary>
public class CiscoIosDialect(SwitchVendor vendor = SwitchVendor.Cisco) : ConfigDialect(vendor)
{
    public override string Family => "ios";
    protected virtual string Enter => "configure terminal";
    protected virtual string Leave => "end";
    protected IReadOnlyList<PlanStep> PortSteps(string port, params string[] lines) => Steps([Enter, $"interface {If(port)}", .. lines, Leave]);

    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) =>
        PortSteps(port, "switchport mode access", $"switchport access vlan {vlan}");
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        PortSteps(port, $"switchport trunk native vlan {native}", $"switchport trunk allowed vlan {canonical}", "switchport mode trunk");
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) => PortSteps(port, enabled ? "no shutdown" : "shutdown");
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => PortSteps(port, text.Length == 0 ? "no description" : $"description {text}");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Steps(Enter, $"vlan {id}", $"name {name}", Leave);
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => Steps(Enter, $"no vlan {id}", Leave);
    public override IReadOnlyList<PlanStep> Save() =>
        [new("write memory", Expect: @"\[OK\]", ExpectFailure: "IOS n'a pas confirmé la sauvegarde par [OK]. Vérifiez la startup-config.")];
}

/// <summary>AlliedWare Plus: IOS-like, but no plain allowed-vlan list and 80-character descriptions.</summary>
public sealed class AlliedWarePlusDialect() : CiscoIosDialect(SwitchVendor.AlliedTelesis)
{
    public override int DescriptionMax => 80;
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        PortSteps(port, "switchport mode trunk", $"switchport trunk native vlan {native}", "switchport trunk allowed vlan none", $"switchport trunk allowed vlan add {canonical}");
    // AW+ prints "Building configuration... [OK]" but the historical driver never required it.
    public override IReadOnlyList<PlanStep> Save() => Steps("write memory");
}

/// <summary>Cisco NX-OS: Layer-2 ports need an explicit "switchport"; saving is a copy.</summary>
public sealed class NxosDialect() : CiscoIosDialect(SwitchVendor.CiscoNxos)
{
    public override string Family => "nxos";
    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) =>
        PortSteps(port, "switchport", "switchport mode access", $"switchport access vlan {vlan}");
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        PortSteps(port, "switchport", "switchport mode trunk", $"switchport trunk native vlan {native}", $"switchport trunk allowed vlan {canonical}");
    public override IReadOnlyList<PlanStep> Save() =>
        [new("copy running-config startup-config", Expect: "Copy complete", ExpectFailure: "NX-OS n'a pas confirmé la copie (« Copy complete »). Vérifiez la startup-config.")];
}

/// <summary>Arista EOS: IOS syntax, "Ethernet1" ports, explicit save confirmation.</summary>
public sealed class AristaDialect() : CiscoIosDialect(SwitchVendor.Arista)
{
    public override string Family => "eos";
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        PortSteps(port, "switchport mode trunk", $"switchport trunk native vlan {native}", $"switchport trunk allowed vlan {canonical}");
    public override IReadOnlyList<PlanStep> Save() =>
        [new("write memory", Expect: "completed successfully", ExpectFailure: "EOS n'a pas confirmé la sauvegarde (« Copy completed successfully »).")];
}

/// <summary>Dell OS6 (N-Series, FASTPATH lineage): "configure", quoted texts, "(y/n)" on save.</summary>
public sealed class DellOs6Dialect() : CiscoIosDialect(SwitchVendor.DellOs6)
{
    public override string Family => "dell-os6";
    protected override string Enter => "configure";
    public override int DescriptionMax => 64;
    public override string ForbiddenText => "\"";
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => PortSteps(port, text.Length == 0 ? "no description" : $"description {Quoted(text)}");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Steps(Enter, $"vlan {id}", $"name {Quoted(name)}", Leave);
    public override IReadOnlyList<PlanStep> Save() =>
        [new("write memory", Confirm: "y", Expect: "Saved", ExpectFailure: "OS6 n'a pas confirmé la sauvegarde (« Configuration Saved »).")];
}

/// <summary>
/// Dell OS9 (FTOS): VLAN membership is configured from the VLAN, not from the port
/// ("interface vlan 10" / "untagged GigabitEthernet 1/1"). A port untagged in another
/// non-default VLAN must leave it first, hence the current VLAN read by the driver.
/// </summary>
public sealed class DellOs9Dialect() : CiscoIosDialect(SwitchVendor.DellOs9)
{
    public override string Family => "dell-os9";
    protected override string Enter => "configure";
    public override bool SupportsTrunk => false;
    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan)
    {
        var commands = new List<string> { Enter, $"interface {If(port)}", "switchport", "exit" };
        if (currentVlan is { } current && current != 1 && current != vlan)
            commands.AddRange([$"interface vlan {current}", $"no untagged {If(port)}", "exit"]);
        commands.AddRange([$"interface vlan {vlan}", $"untagged {If(port)}", Leave]);
        return Steps([.. commands]);
    }
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) =>
        throw new NotSupportedException("Trunk non pris en charge sur Dell OS9 dans cette version (appartenance « tagged » à configurer VLAN par VLAN).");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Steps(Enter, $"interface vlan {id}", $"name {name}", Leave);
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => Steps(Enter, $"no interface vlan {id}", Leave);
    public override IReadOnlyList<PlanStep> Save() => Steps("write memory");
}

/// <summary>Dell SmartFabric OS10: "interface vlan" objects, native VLAN = access VLAN of a trunk.</summary>
public sealed class DellOs10Dialect() : CiscoIosDialect(SwitchVendor.DellOs10)
{
    public override string Family => "dell-os10";
    public override string ExportCommand => "show running-configuration";
    public override string ForbiddenText => "\"";
    public override string TrunkCaveat => " (VLAN étiquetés ajoutés ; les autres VLAN déjà autorisés sont conservés)";
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical)
    {
        var tagged = allowed.Where(id => id != native).ToArray();
        return tagged.Length == 0
            ? PortSteps(port, "switchport mode trunk", $"switchport access vlan {native}")
            : PortSteps(port, "switchport mode trunk", $"switchport access vlan {native}", $"switchport trunk allowed vlan {RangeList(tagged)}");
    }
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => PortSteps(port, text.Length == 0 ? "no description" : $"description {Quoted(text)}");
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => Steps(Enter, $"interface vlan {id}", $"description {Quoted(name)}", Leave);
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => Steps(Enter, $"no interface vlan {id}", Leave);
    public override IReadOnlyList<PlanStep> Save() => Steps("write memory");
}

/// <summary>Platforms that the application reads but never configures.</summary>
public sealed class ReadOnlyDialect(SwitchVendor vendor, string family, string reason) : ConfigDialect(vendor)
{
    public string Reason { get; } = reason;
    public override string Family => family;
    public override IReadOnlyList<PlanStep> Access(string port, int vlan, int? currentVlan) => throw new NotSupportedException(Reason);
    public override IReadOnlyList<PlanStep> Trunk(string port, int native, IReadOnlyCollection<int> allowed, string canonical) => throw new NotSupportedException(Reason);
    public override IReadOnlyList<PlanStep> Enabled(string port, bool enabled) => throw new NotSupportedException(Reason);
    public override IReadOnlyList<PlanStep> Describe(string port, string text) => throw new NotSupportedException(Reason);
    public override IReadOnlyList<PlanStep> CreateVlan(int id, string name) => throw new NotSupportedException(Reason);
    public override IReadOnlyList<PlanStep> DeleteVlan(int id, string? name) => throw new NotSupportedException(Reason);
    public override IReadOnlyList<PlanStep> Save() => throw new NotSupportedException(Reason);
}
