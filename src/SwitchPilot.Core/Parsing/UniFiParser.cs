using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchPilot.Core.Cisco;

namespace SwitchPilot.Core.Parsing;

/// <summary>A UniFi network ("networkconf") with the VLAN it carries; the Default LAN carries VLAN 1.</summary>
public sealed record UniFiNetwork(string Id, string Name, int Vlan, string Purpose)
{
    /// <summary>Only VLAN-only networks hold no gateway / DHCP settings and can be deleted safely.</summary>
    public bool IsVlanOnly => Purpose == "vlan-only";
}

/// <summary>
/// UniFi Network controller JSON (classic controller and UniFi OS): device list, networks,
/// port table and port_overrides. Every reply is wrapped in {"meta":{"rc":"ok"},"data":[…]}.
/// </summary>
public static class UniFiParser
{
    private static readonly string[] SwitchingPurposes = ["corporate", "vlan-only", "guest"];

    /// <summary>Returns the "data" array, or throws with the controller message when meta.rc is not "ok".</summary>
    public static JsonArray Data(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Réponse du contrôleur UniFi illisible (JSON invalide).", ex); }
        var rc = root?["meta"]?["rc"]?.GetValue<string>();
        if (rc is not null && rc != "ok")
            throw new InvalidOperationException($"Le contrôleur UniFi a refusé la requête : {root?["meta"]?["msg"]?.GetValue<string>() ?? rc}.");
        return root?["data"] as JsonArray ?? throw new FormatException("Réponse du contrôleur UniFi sans champ « data ».");
    }

    private static string Text(JsonNode? node) => node is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.Number => v.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => ""
    } : "";

    private static long? Number(JsonNode? node) => long.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static bool Flag(JsonNode? node) => Text(node) == "true";

    public static IReadOnlyList<UniFiNetwork> Networks(string json)
    {
        var result = new List<UniFiNetwork>();
        foreach (var node in Data(json).OfType<JsonObject>())
        {
            var purpose = Text(node["purpose"]);
            if (!SwitchingPurposes.Contains(purpose)) continue;
            var vlan = Flag(node["vlan_enabled"]) || purpose == "vlan-only" ? (int)(Number(node["vlan"]) ?? 0) : 1;
            if (vlan is < 1 or > 4094) continue;
            result.Add(new(Text(node["_id"]), Text(node["name"]), vlan, purpose));
        }
        return result;
    }

    /// <summary>
    /// The switch to manage: matched by MAC address or name when <paramref name="selector"/> is
    /// set, otherwise the only switch of the site.
    /// </summary>
    public static JsonObject Switch(string json, string? selector)
    {
        var switches = Data(json).OfType<JsonObject>().Where(d => Text(d["type"]) == "usw").ToArray();
        if (switches.Length == 0) throw new InvalidOperationException("Aucun switch UniFi adopté sur ce site.");
        if (string.IsNullOrWhiteSpace(selector))
        {
            if (switches.Length == 1) return switches[0];
            throw new InvalidOperationException("Plusieurs switchs sur ce site : indiquez l'adresse MAC ou le nom du switch (" +
                string.Join(", ", switches.Select(Label)) + ").");
        }
        string? mac = null;
        try { mac = CiscoParser.NormalizeMac(selector); } catch (ArgumentException) { }
        var match = switches.Where(d => mac is not null ? Normalized(Text(d["mac"])) == mac : Text(d["name"]).Equals(selector.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return match.Length == 1 ? match[0] : throw new InvalidOperationException(match.Length == 0
            ? $"Switch « {selector} » introuvable sur ce site ({string.Join(", ", switches.Select(Label))})."
            : $"Plusieurs switchs portent le nom « {selector} » : indiquez l'adresse MAC.");
    }

    private static string Label(JsonObject device) => Text(device["name"]) is { Length: > 0 } name ? $"{name} [{Text(device["mac"])}]" : Text(device["mac"]);

    private static string? Normalized(string mac)
    {
        try { return CiscoParser.NormalizeMac(mac); } catch (ArgumentException) { return null; }
    }

    public static SwitchIdentity Identity(JsonObject device)
    {
        var name = Text(device["name"]);
        var model = Text(device["model"]);
        var version = Text(device["version"]);
        return new(name.Length > 0 ? name : Text(device["mac"]), model.Length > 0 ? model : "UniFi (modèle inconnu)", version.Length > 0 ? version : "Inconnue");
    }

    private static IEnumerable<JsonObject> PortTable(JsonObject device) =>
        (device["port_table"] as JsonArray)?.OfType<JsonObject>().Where(p => Number(p["port_idx"]) is >= 1 and <= 512) ?? [];

    private static JsonObject? Override(JsonObject device, long index) =>
        (device["port_overrides"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => Number(o["port_idx"]) == index);

    /// <summary>VLAN settings of a port: the override when present, else the port table (effective values).</summary>
    private static (string? Native, string Tagging, IReadOnlyList<string> Excluded, string Forward) VlanSettings(JsonObject port, JsonObject? over)
    {
        string Pick(string key) => over?[key] is { } v ? Text(v) : Text(port[key]);
        var excludedNode = over?["excluded_networkconf_ids"] ?? port["excluded_networkconf_ids"];
        var excluded = (excludedNode as JsonArray)?.Select(Text).Where(t => t.Length > 0).ToArray() ?? [];
        var native = Pick("native_networkconf_id");
        return (native.Length > 0 ? native : null, Pick("tagged_vlan_mgmt"), excluded, Pick("forward"));
    }

    public static IReadOnlyList<PortInfo> Ports(JsonObject device, IReadOnlyList<UniFiNetwork> networks)
    {
        var result = new List<PortInfo>();
        foreach (var port in PortTable(device).OrderBy(p => Number(p["port_idx"])))
        {
            var index = Number(port["port_idx"])!.Value;
            var name = "Port " + index;
            var over = Override(device, index);
            var label = Text(over?["name"] ?? port["name"]);
            var (native, tagging, excluded, forward) = VlanSettings(port, over);
            var nativeVlan = networks.FirstOrDefault(n => n.Id == native)?.Vlan;
            var disabled = forward == "disabled" || port["enable"] is { } e && !Flag(e);
            var trunk = tagging switch
            {
                "block_all" => false,
                "auto" => true,
                "custom" => networks.Any(n => n.Id != native && !excluded.Contains(n.Id)),
                // Controllers older than Network 7.4: "all" / "native" / "customize".
                _ => forward is "all" or "customize"
            };
            var status = disabled ? "disabled" : Flag(port["up"]) ? "connected" : "notconnect";
            var speed = Number(port["speed"]) is { } s && s > 0 ? s.ToString(CultureInfo.InvariantCulture) : "auto";
            var duplex = !Flag(port["up"]) ? "auto" : Flag(port["full_duplex"]) ? "full" : "half";
            result.Add(new(name, label == name ? "" : label, status, trunk ? "trunk" : nativeVlan?.ToString(CultureInfo.InvariantCulture) ?? "",
                duplex, speed, Text(port["media"]), trunk ? "trunk" : nativeVlan is null ? "Inconnu" : "access"));
        }
        if (result.Count == 0) throw new FormatException("Le contrôleur UniFi ne renvoie aucun port pour ce switch.");
        return result;
    }

    public static IReadOnlyList<VlanInfo> Vlans(JsonObject device, IReadOnlyList<UniFiNetwork> networks)
    {
        var ports = Ports(device, networks);
        return networks.GroupBy(n => n.Vlan).OrderBy(g => g.Key).Select(g => new VlanInfo(g.Key, string.Join(" / ", g.Select(n => n.Name)), "active",
            string.Join(", ", ports.Where(p => p.Vlan == g.Key.ToString(CultureInfo.InvariantCulture)).Select(p => p.Name)))).ToArray();
    }

    /// <summary>MAC addresses learned per port (port_table[].mac_table), else the wired clients seen on this switch.</summary>
    public static IReadOnlyList<MacEntry> Macs(JsonObject device, string? clientsJson)
    {
        var result = new List<MacEntry>();
        foreach (var port in PortTable(device))
            foreach (var entry in (port["mac_table"] as JsonArray)?.OfType<JsonObject>() ?? [])
                if (Normalized(Text(entry["mac"])) is { } mac)
                    result.Add(new((int)(Number(entry["vlan"]) is { } v and >= 1 and <= 4094 ? v : 1), mac, Flag(entry["static"]) ? "STATIC" : "DYNAMIC", "Port " + Number(port["port_idx"])));
        if (result.Count > 0 || clientsJson is null) return result;
        var self = Normalized(Text(device["mac"]));
        foreach (var client in Data(clientsJson).OfType<JsonObject>())
        {
            if (Normalized(Text(client["sw_mac"])) != self || self is null || Number(client["sw_port"]) is not { } index || Normalized(Text(client["mac"])) is not { } mac) continue;
            result.Add(new((int)(Number(client["vlan"]) is { } v and >= 1 and <= 4094 ? v : 1), mac, "DYNAMIC", "Port " + index));
        }
        return result;
    }

    public static InterfaceCounters Counters(JsonObject device, string port)
    {
        var index = int.Parse(port[5..], CultureInfo.InvariantCulture);
        var row = PortTable(device).FirstOrDefault(p => Number(p["port_idx"]) == index) ?? throw new ArgumentException($"{port} absent du switch UniFi.");
        var up = Flag(row["up"]);
        return new(null, null, Number(row["rx_errors"]), up && Number(row["speed"]) is { } s ? s.ToString(CultureInfo.InvariantCulture) : "Inconnue",
            up ? Flag(row["full_duplex"]) ? "Full" : "Half" : "Inconnu", up ? "Actif" : "Inactif", Number(row["tx_errors"]));
    }

    /// <summary>True when the controller exposes "tagged_vlan_mgmt" (UniFi Network 7.4 and later).</summary>
    public static bool HasTaggedVlanManagement(JsonObject device) =>
        PortTable(device).Any(p => p.ContainsKey("tagged_vlan_mgmt")) || (device["port_overrides"] as JsonArray)?.OfType<JsonObject>().Any(o => o.ContainsKey("tagged_vlan_mgmt")) == true;

    /// <summary>
    /// Builds the complete port_overrides array for <paramref name="plan"/>: the overrides of the
    /// other ports are copied unchanged, the entry of the target port keeps its unrelated fields
    /// (PoE, storm control…) and only the fields of the change are replaced.
    /// </summary>
    public static JsonArray PortOverrides(JsonObject device, CommandPlan plan, IReadOnlyList<UniFiNetwork> networks)
    {
        if (plan.Port is not { } port || !port.StartsWith("Port ", StringComparison.Ordinal)) throw new ArgumentException("Plan UniFi sans port.");
        var index = int.Parse(port[5..], CultureInfo.InvariantCulture);
        if (!PortTable(device).Any(p => Number(p["port_idx"]) == index)) throw new ArgumentException($"{port} absent du switch UniFi.");
        var existing = (device["port_overrides"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var target = existing.FirstOrDefault(o => Number(o["port_idx"]) == index)?.DeepClone().AsObject() ?? new JsonObject { ["port_idx"] = index };

        UniFiNetwork Network(int vlan) => networks.FirstOrDefault(n => n.Vlan == vlan)
            ?? throw new InvalidOperationException($"Aucun réseau UniFi ne porte le VLAN {vlan} : créez-le d'abord.");
        switch (plan.Kind)
        {
            case ChangeKind.AccessVlan:
                RequireTagging(device);
                target["native_networkconf_id"] = Network(plan.Vlan!.Value).Id;
                target["tagged_vlan_mgmt"] = "block_all";
                target["excluded_networkconf_ids"] = new JsonArray();
                break;
            case ChangeKind.Trunk:
                RequireTagging(device);
                var allowed = new HashSet<int>();
                foreach (var part in (plan.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var range = part.Split('-');
                    for (var id = int.Parse(range[0], CultureInfo.InvariantCulture); id <= int.Parse(range[^1], CultureInfo.InvariantCulture); id++) allowed.Add(id);
                }
                foreach (var id in allowed) Network(id);
                var native = Network(plan.Vlan!.Value);
                target["native_networkconf_id"] = native.Id;
                target["tagged_vlan_mgmt"] = "custom";
                target["excluded_networkconf_ids"] = new JsonArray(networks.Where(n => n.Id != native.Id && !allowed.Contains(n.Vlan))
                    .Select(n => (JsonNode?)JsonValue.Create(n.Id)).ToArray());
                break;
            case ChangeKind.Description:
                if (string.IsNullOrEmpty(plan.Value)) target.Remove("name");
                else target["name"] = plan.Value;
                break;
            default:
                throw new NotSupportedException("Modification non prise en charge par l'API UniFi dans Switch Pilot.");
        }
        var result = new JsonArray();
        foreach (var entry in existing) result.Add(Number(entry["port_idx"]) == index ? null : entry.DeepClone());
        for (var i = result.Count - 1; i >= 0; i--) if (result[i] is null) result.RemoveAt(i);
        result.Add(target);
        return result;
    }

    private static void RequireTagging(JsonObject device)
    {
        if (!HasTaggedVlanManagement(device))
            throw new NotSupportedException("Contrôleur UniFi trop ancien : la gestion des VLAN par port (tagged_vlan_mgmt) demande UniFi Network 7.4 ou plus récent.");
    }

    /// <summary>Body of POST rest/networkconf for a VLAN-only network.</summary>
    public static JsonObject NewNetwork(int vlan, string name, IReadOnlyList<UniFiNetwork> networks)
    {
        CommandPlan.ValidateVlan(vlan);
        if (networks.FirstOrDefault(n => n.Vlan == vlan) is { } existing) throw new InvalidOperationException($"Le VLAN {vlan} existe déjà sur le contrôleur (réseau « {existing.Name} »).");
        return new JsonObject { ["name"] = name, ["purpose"] = "vlan-only", ["vlan_enabled"] = true, ["vlan"] = vlan, ["igmp_snooping"] = false };
    }

    /// <summary>The VLAN-only network to delete; networks with a gateway or DHCP are refused.</summary>
    public static UniFiNetwork NetworkToDelete(int vlan, IReadOnlyList<UniFiNetwork> networks)
    {
        var network = networks.FirstOrDefault(n => n.Vlan == vlan) ?? throw new InvalidOperationException($"Aucun réseau UniFi ne porte le VLAN {vlan}.");
        return network.IsVlanOnly ? network
            : throw new InvalidOperationException($"Le réseau « {network.Name} » (VLAN {vlan}) a une passerelle ou un DHCP : supprimez-le depuis l'application UniFi.");
    }
}
