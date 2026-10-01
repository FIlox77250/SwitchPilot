using System.Text.RegularExpressions;

namespace SwitchPilot.Core;

public enum ChangeKind { AccessVlan, Trunk, EnablePort, DisablePort, Description, CreateVlan, DeleteVlan, Save }

// Plans are immutable and constructed only from validated values, never arbitrary CLI text.
public sealed class CommandPlan
{
    public ChangeKind Kind { get; }
    public string Title { get; }
    public string? Port { get; }
    public int? Vlan { get; }
    public string? Value { get; }
    public IReadOnlyList<string> Commands { get; }
    public string Preview => string.Join(Environment.NewLine, Commands);
    private CommandPlan(ChangeKind kind, string title, string? port, int? vlan, string? value, params string[] commands)
        => (Kind, Title, Port, Vlan, Value, Commands) = (kind, title, port, vlan, value, Array.AsReadOnly(commands));

    public static string Interface(string value)
    {
        if (!Regex.IsMatch(value, @"^(?:(?:Fa(?:stEthernet)?|Gi(?:gabitEthernet)?|Te(?:nGigabitEthernet)?|Po(?:rt-channel)?)\d+(?:/\d+){0,2}|(?:port)?\d+(?:\.\d+){1,3}|(?:ethernet\s+)?(?:\d+/)?(?:g\d+|ch\d+))$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Interface invalide.");
        return Cisco.CiscoParser.NormalizeInterface(value);
    }
    public static void ValidateVlan(int id)
    {
        if (id is < 1 or > 4094 || id is >= 1002 and <= 1005)
            throw new ArgumentException("VLAN attendu : 1–4094, hors VLAN réservés 1002–1005.");
    }
    private static string Text(string value, int max, bool spaces)
    {
        if (value.Length > max || value.Any(c => c < 32 || c > 126 || c == '?' || (!spaces && char.IsWhiteSpace(c))))
            throw new ArgumentException($"Texte ASCII imprimable attendu, {max} caractères maximum{(spaces ? "" : ", sans espace")}.");
        return value.Trim();
    }
    private static CommandPlan PortPlan(ChangeKind kind, string title, string port, int? vlan, string? value, params string[] commands)
    {
        port = Interface(port);
        return new(kind, title, port, vlan, value, ["configure terminal", $"interface {port}", .. commands, "end"]);
    }
    public static CommandPlan Access(string port, int vlan)
    {
        ValidateVlan(vlan);
        return PortPlan(ChangeKind.AccessVlan, $"Affecter {port} au VLAN {vlan}", port, vlan, null, "switchport mode access", $"switchport access vlan {vlan}");
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
        // AlliedWare Plus has no plain allowed-vlan list form: reset then add.
        return vendor == SwitchVendor.AlliedTelesis
            ? PortPlan(ChangeKind.Trunk, $"Configurer {port} en trunk", port, native, allowed,
                "switchport mode trunk", $"switchport trunk native vlan {native}", "switchport trunk allowed vlan none", $"switchport trunk allowed vlan add {allowed}")
            : PortPlan(ChangeKind.Trunk, $"Configurer {port} en trunk", port, native, allowed, $"switchport trunk native vlan {native}", $"switchport trunk allowed vlan {allowed}", "switchport mode trunk");
    }
    public static CommandPlan Enabled(string port, bool enabled) => PortPlan(enabled ? ChangeKind.EnablePort : ChangeKind.DisablePort,
        $"{(enabled ? "Activer" : "Désactiver")} {port}", port, null, null, enabled ? "no shutdown" : "shutdown");
    public static CommandPlan Describe(string port, string description, SwitchVendor vendor = SwitchVendor.Cisco)
    {
        // AlliedWare Plus limits a port description to 80 characters.
        var text = Text(description, vendor == SwitchVendor.AlliedTelesis ? 80 : 200, true);
        return PortPlan(ChangeKind.Description, $"Description de {port}", port, null, text, text.Length == 0 ? "no description" : $"description {text}");
    }
    public static CommandPlan CreateVlan(int id, string name)
    {
        ValidateVlan(id); var text = Text(name, 32, false);
        if (text.Length == 0) throw new ArgumentException("Le nom du VLAN est requis.");
        return new(ChangeKind.CreateVlan, $"Créer le VLAN {id}", null, id, text, "configure terminal", $"vlan {id}", $"name {text}", "end");
    }
    public static CommandPlan DeleteVlan(int id)
    {
        ValidateVlan(id);
        if (id == 1) throw new ArgumentException("Le VLAN 1 ne peut pas être supprimé.");
        return new(ChangeKind.DeleteVlan, $"Supprimer le VLAN {id}", null, id, null, "configure terminal", $"no vlan {id}", "end");
    }
    public static CommandPlan Save() => new(ChangeKind.Save, "Sauvegarder en mémoire permanente", null, null, null, "write memory");
}
