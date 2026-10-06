using System.Text.RegularExpressions;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core;

public enum ChangeKind { AccessVlan, Trunk, EnablePort, DisablePort, Description, CreateVlan, DeleteVlan, Save }

// Plans are immutable and constructed only from validated values, never arbitrary CLI text.
// The values are validated here; the platform dialect (Platforms.ConfigDialect) only decides
// the command sequence, so every vendor gets the same input checks.
public sealed class CommandPlan
{
    public ChangeKind Kind { get; }
    public string Title { get; }
    public string? Port { get; }
    public int? Vlan { get; }
    public string? Value { get; }
    /// <summary>Platform the plan was written for; the driver refuses a plan of another family.</summary>
    public SwitchVendor Vendor { get; }
    /// <summary>Access VLAN the port had when the plan was built (OS9 / EdgeSwitch remove it explicitly).</summary>
    public int? CurrentVlan { get; init; }
    public IReadOnlyList<PlanStep> Steps { get; }
    public IReadOnlyList<string> Commands { get; }
    /// <summary>Commands that leave configuration mode (or discard a candidate) after a partial failure.</summary>
    public IReadOnlyList<string> Recovery { get; }
    public string Family => ConfigDialects.For(Vendor).Family;
    public string Preview => Steps.Count == 0
        ? "Aucune commande : cette plateforme enregistre chaque modification acceptée (commit ou enregistrement immédiat)."
        : string.Join(Environment.NewLine, Steps);

    private CommandPlan(ChangeKind kind, string title, string? port, int? vlan, string? value, SwitchVendor vendor, IReadOnlyList<PlanStep> steps)
    {
        (Kind, Title, Port, Vlan, Value, Vendor) = (kind, title, port, vlan, value, vendor);
        Steps = Array.AsReadOnly(steps.ToArray());
        Commands = Array.AsReadOnly(steps.Select(s => s.Command).ToArray());
        Recovery = ConfigDialects.For(vendor).Recovery;
        if (Commands.Any(c => c.Any(char.IsControl))) throw new ArgumentException("Commande invalide.");
    }

    internal static string LegacyInterface(string value)
    {
        if (!Regex.IsMatch(value, @"^(?:(?:Fa(?:stEthernet)?|Gi(?:gabitEthernet)?|Te(?:nGigabitEthernet)?|Po(?:rt-channel)?)\d+(?:/\d+){0,2}|(?:port)?\d+(?:\.\d+){1,3}|(?:ethernet\s+)?(?:\d+/)?(?:g\d+|ch\d+))$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Interface invalide.");
        return Cisco.CiscoParser.NormalizeInterface(value);
    }

    /// <summary>
    /// Platform-agnostic port check: Cisco / AlliedWare Plus / AT-S95 names first (unchanged
    /// canonical form), then the fixed grammars of the other platforms. Free-form names
    /// (RouterOS) need the platform: use <see cref="PortNames.Validate"/>.
    /// </summary>
    public static string Interface(string value)
    {
        try { return LegacyInterface(value); }
        catch (ArgumentException) when (!value.Any(char.IsControl) && PortNames.Any(value, allowFreeForm: false) is { } other) { return other; }
    }
    public static void ValidateVlan(int id)
    {
        if (id is < 1 or > 4094 || id is >= 1002 and <= 1005)
            throw new ArgumentException("VLAN attendu : 1–4094, hors VLAN réservés 1002–1005.");
    }
    private static string Text(string value, int max, bool spaces, string forbidden = "")
    {
        if (value.Length > max || value.Any(c => c < 32 || c > 126 || c == '?' || (!spaces && char.IsWhiteSpace(c)) || forbidden.Contains(c)))
            throw new ArgumentException($"Texte ASCII imprimable attendu, {max} caractères maximum{(spaces ? "" : ", sans espace")}{(forbidden.Length == 0 ? "" : $", sans {string.Join(' ', forbidden.ToCharArray())}")}.");
        return value.Trim();
    }
    private static (ConfigDialect Dialect, string Port) Prepare(string port, SwitchVendor vendor) =>
        (ConfigDialects.For(vendor), PortNames.Validate(vendor, port));

    public static CommandPlan Access(string port, int vlan, SwitchVendor vendor = SwitchVendor.Cisco, int? currentVlan = null)
    {
        ValidateVlan(vlan);
        if (currentVlan is { } current) ValidateVlan(current);
        var (dialect, name) = Prepare(port, vendor);
        return new(ChangeKind.AccessVlan, $"Affecter {port} au VLAN {vlan}", name, vlan, null, vendor, dialect.Access(name, vlan, currentVlan)) { CurrentVlan = currentVlan };
    }
    public static CommandPlan Trunk(string port, int native, string allowed, SwitchVendor vendor = SwitchVendor.Cisco)
    {
        ValidateVlan(native);
        var ids = new SortedSet<int>();
        var canonical = new List<string>();
        foreach (var part in allowed.Split(',', StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var first) || !int.TryParse(range[^1], out var last) || first > last)
                throw new ArgumentException("Liste VLAN attendue : 10,20,30-40.");
            ValidateVlan(first); ValidateVlan(last);
            for (var id = first; id <= last; id++) { ValidateVlan(id); ids.Add(id); }
            canonical.Add(first == last ? first.ToString() : $"{first}-{last}");
        }
        if (!ids.Contains(native)) throw new ArgumentException("Le VLAN natif doit figurer dans les VLAN autorisés.");
        allowed = string.Join(',', canonical);
        // Keep IOS command lines bounded; caller can use compact ranges.
        if (allowed.Length > 180) throw new ArgumentException("Liste de VLAN trop longue (180 caractères maximum).");
        var (dialect, name) = Prepare(port, vendor);
        return new(ChangeKind.Trunk, $"Configurer {port} en trunk{dialect.TrunkCaveat}", name, native, allowed, vendor, dialect.Trunk(name, native, ids, allowed));
    }
    public static CommandPlan Enabled(string port, bool enabled, SwitchVendor vendor = SwitchVendor.Cisco)
    {
        var (dialect, name) = Prepare(port, vendor);
        return new(enabled ? ChangeKind.EnablePort : ChangeKind.DisablePort, $"{(enabled ? "Activer" : "Désactiver")} {port}", name, null, null, vendor, dialect.Enabled(name, enabled));
    }
    public static CommandPlan Describe(string port, string description, SwitchVendor vendor = SwitchVendor.Cisco)
    {
        // AlliedWare Plus limits a port description to 80 characters; each dialect sets its own limit.
        var dialect = ConfigDialects.For(vendor);
        var text = Text(description, dialect.DescriptionMax, true, dialect.ForbiddenText);
        var name = PortNames.Validate(vendor, port);
        return new(ChangeKind.Description, $"Description de {port}", name, null, text, vendor, dialect.Describe(name, text));
    }
    public static CommandPlan CreateVlan(int id, string name, SwitchVendor vendor = SwitchVendor.Cisco)
    {
        var dialect = ConfigDialects.For(vendor);
        ValidateVlan(id); var text = Text(name, dialect.VlanNameMax, false, dialect.ForbiddenText);
        if (text.Length == 0) throw new ArgumentException("Le nom du VLAN est requis.");
        dialect.ValidateVlanName(text);
        return new(ChangeKind.CreateVlan, $"Créer le VLAN {id}", null, id, text, vendor, dialect.CreateVlan(id, text));
    }
    public static CommandPlan DeleteVlan(int id, SwitchVendor vendor = SwitchVendor.Cisco, string? name = null)
    {
        ValidateVlan(id);
        if (id == 1) throw new ArgumentException("Le VLAN 1 ne peut pas être supprimé.");
        var dialect = ConfigDialects.For(vendor);
        // Only Junos needs the name (VLANs are keyed by name there); an unusable name is dropped.
        string? text = null;
        if (!string.IsNullOrWhiteSpace(name)) try { text = Text(name, 64, false, dialect.ForbiddenText); } catch (ArgumentException) { }
        return new(ChangeKind.DeleteVlan, $"Supprimer le VLAN {id}", null, id, text, vendor, dialect.DeleteVlan(id, text));
    }
    public static CommandPlan Save() => Save(SwitchVendor.Cisco);
    public static CommandPlan Save(SwitchVendor vendor) =>
        new(ChangeKind.Save, "Sauvegarder en mémoire permanente", null, null, null, vendor, ConfigDialects.For(vendor).Save());
}
