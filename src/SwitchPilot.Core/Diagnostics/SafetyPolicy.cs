using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Core.Diagnostics;

public static class SafetyPolicy
{
    public static void RequireSafeTdr(PortInfo port, SafetyContext context, ConnectionKind kind = ConnectionKind.Ssh)
    {
        var age = DateTimeOffset.UtcNow - context.ObservedAt;
        if (kind != ConnectionKind.Serial && (!context.IsFresh || context.StillCurrent?.Invoke() == false || !context.ManagementPathKnown || age > TimeSpan.FromSeconds(30) || age < TimeSpan.FromSeconds(-5)))
            throw new InvalidOperationException("TDR bloqué : le chemin de connexion du poste n'est pas identifié avec certitude. Relancez la détection sur une connexion Ethernet directe.");
        if (kind != ConnectionKind.Serial && context.ProtectedPorts.Any(p => PortNames.Same(p, port.Name)))
            throw new InvalidOperationException("TDR bloqué : ce port transporte la connexion du poste.");
        if (port.IsTrunk || port.Mode != "access" || PortNames.IsAggregate(port.Name))
            throw new InvalidOperationException("TDR bloqué sur un trunk, un agrégat ou un port de mode inconnu.");
        if (port.Media.Contains("SFP", StringComparison.OrdinalIgnoreCase) || !port.Media.Contains("BaseTX", StringComparison.OrdinalIgnoreCase) && !port.Media.Contains("BaseT", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("TDR indisponible : interface cuivre compatible non identifiée. Le contrôle passif reste disponible.");
    }
    public static void RequireSafeChange(CommandPlan plan, SafetyContext context, ConnectionKind kind)
    {
        if (kind == ConnectionKind.Serial || plan.Kind is not (ChangeKind.DisablePort or ChangeKind.AccessVlan or ChangeKind.Trunk)) return;
        var age = DateTimeOffset.UtcNow - context.ObservedAt;
        if (!context.IsFresh || context.StillCurrent?.Invoke() == false || !context.ManagementPathKnown || age > TimeSpan.FromSeconds(30) || age < TimeSpan.FromSeconds(-5))
            throw new InvalidOperationException("Modification bloquée : chemin de gestion SSH incertain. Utilisez une console locale.");
        if (context.ProtectedPorts.Any(p => PortNames.Same(p, plan.Port)))
            throw new InvalidOperationException("Modification bloquée : ce port transporte la connexion du poste ou le chemin SSH. Utilisez une console locale.");
    }
    public static string SpeedAssessment(PortInfo port)
    {
        if (!port.IsUp) return "Lien inactif : aucune vitesse négociée.";
        if (port.Name.StartsWith("Fa")) return "Port Fast Ethernet : 100 Mb/s est une vitesse normale.";
        if (port.Name.StartsWith("Gi") && port.Speed.Replace("a-", "") == "100")
            return "Port Gigabit négocié à 100 Mb/s : vérifier aussi la carte distante et la vitesse forcée, avant d'incriminer le câble.";
        return "Vitesse à comparer aux capacités et à la configuration des deux extrémités.";
    }
}
