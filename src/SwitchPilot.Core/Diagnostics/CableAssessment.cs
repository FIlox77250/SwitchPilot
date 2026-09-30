namespace SwitchPilot.Core.Diagnostics;

public record LocalLinkMeasurement(string AdapterId, DateTimeOffset At, bool IsUp, long? Speed, long? MaxSpeed, string? Duplex,
    long? ReceiveErrors, long? TransmitErrors, long? BytesReceived, long? BytesSent, int LinkTransitions);
public record SwitchMeasurement(string Port, DateTimeOffset At, InterfaceCounters Counters);
public record CableAssessment(string Verdict, IReadOnlyList<string> Evidence)
{
    public string Details => string.Join(Environment.NewLine, Evidence);
    public static CableAssessment Evaluate(LocalLinkMeasurement? local, LocalLinkMeasurement? previous = null,
        SwitchMeasurement? remote = null, SwitchMeasurement? previousRemote = null)
    {
        var evidence = new List<string>(); var suspect = false; var incomplete = false;
        if (local is null) return new("À vérifier", ["Aucune mesure locale disponible."]);
        evidence.Add($"Poste · {local.At.ToLocalTime():HH:mm:ss} · lien {(local.IsUp ? "actif" : "inactif")} · {Rate(local.Speed)} · duplex {local.Duplex ?? "inconnu"}");
        evidence.Add($"Erreurs cumulées poste : réception {local.ReceiveErrors?.ToString() ?? "—"}, émission {local.TransmitErrors?.ToString() ?? "—"}.");
        if (!local.IsUp) { evidence.Add("Lien inactif : câble débranché, équipement éteint ou défaut possible."); incomplete = true; }
        if (local.LinkTransitions >= 4) { evidence.Add($"Lien instable : {local.LinkTransitions} transitions en 60 secondes."); suspect = true; }
        if (local.MaxSpeed >= 1_000_000_000 && local.Speed == 100_000_000)
        { evidence.Add("Carte Gigabit négociée à 100 Mb/s : vérifier le port distant, une vitesse forcée et les paires du câble."); incomplete = true; }
        if (local.Speed is null or <= 0 || local.Duplex is null) incomplete = true;
        if (local.Duplex == "Half") { evidence.Add("Half-duplex observé : vérifier la configuration des deux extrémités."); incomplete = true; }
        var samePeriod = previous is not null && previous.AdapterId == local.AdapterId && previous.IsUp && local.IsUp &&
            local.At - previous.At >= TimeSpan.FromSeconds(2) && local.At - previous.At <= TimeSpan.FromMinutes(2);
        var rx = samePeriod ? Delta(local.ReceiveErrors, previous!.ReceiveErrors) : null;
        var tx = samePeriod ? Delta(local.TransmitErrors, previous!.TransmitErrors) : null;
        if (rx > 0 || tx > 0) { suspect = true; evidence.Add($"Erreurs en progression : réception +{rx?.ToString() ?? "—"}, émission +{tx?.ToString() ?? "—"}."); }
        if (rx is null || tx is null) { incomplete = true; evidence.Add("Progression des erreurs non mesurée : attendre deux relevés comparables."); }
        var received = samePeriod ? Delta(local.BytesReceived, previous!.BytesReceived) : null;
        var sent = samePeriod ? Delta(local.BytesSent, previous!.BytesSent) : null;
        if (!(received > 0 || sent > 0)) { incomplete = true; evidence.Add("Pas de trafic observé entre deux relevés ; aucun trafic de test n’est généré."); }
        else evidence.Add($"Trafic observé sur {(local.At - previous!.At).TotalSeconds:F1} s : réception +{received?.ToString() ?? "—"} octets, émission +{sent?.ToString() ?? "—"} octets.");
        if (remote is not null)
        {
            var counters = remote.Counters;
            evidence.Add($"Switch {remote.Port} · {remote.At.ToLocalTime():HH:mm:ss} · {counters.Speed} Mb/s · {counters.Duplex} · CRC {counters.Crc?.ToString() ?? "—"}, collisions {counters.Collisions?.ToString() ?? "—"}, erreurs RX {counters.InputErrors?.ToString() ?? "—"}, TX {counters.OutputErrors?.ToString() ?? "—"}.");
            if (Math.Abs((local.At - remote.At).TotalSeconds) > 65) { incomplete = true; evidence.Add("Mesure switch trop ancienne pour comparer les extrémités."); }
            else
            {
                var knownRemoteSpeed = long.TryParse(counters.Speed, out var remoteMbps) && remoteMbps > 0;
                if (!knownRemoteSpeed || counters.Duplex is not ("Full" or "Half"))
                { incomplete = true; evidence.Add("Vitesse ou duplex du switch inconnu : comparaison incomplète."); }
                if (knownRemoteSpeed && local.Speed > 0 && remoteMbps != local.Speed / 1_000_000)
                { incomplete = true; evidence.Add("Vitesses différentes aux deux extrémités : mesures ou chemin à vérifier."); }
                if (local.Duplex is not null && counters.Duplex is "Full" or "Half" && !local.Duplex.Equals(counters.Duplex, StringComparison.OrdinalIgnoreCase))
                { incomplete = true; evidence.Add("Duplex mismatch : modes duplex différents aux deux extrémités."); }
                if (counters.LinkState != "Actif" || counters.Crc is null || counters.InputErrors is null || counters.Collisions is null) incomplete = true;
                var comparable = previousRemote?.Port == remote.Port && remote.At > previousRemote.At && remote.At - previousRemote.At <= TimeSpan.FromMinutes(2);
                if (comparable && (Delta(counters.Crc, previousRemote!.Counters.Crc) > 0 || Delta(counters.Collisions, previousRemote.Counters.Collisions) > 0 || Delta(counters.InputErrors, previousRemote.Counters.InputErrors) > 0 || Delta(counters.OutputErrors, previousRemote.Counters.OutputErrors) > 0))
                { suspect = true; evidence.Add("Les compteurs d’erreurs du switch progressent."); }
                else if (counters.Crc > 0 || counters.Collisions > 0 || counters.InputErrors > 0 || counters.OutputErrors > 0)
                { incomplete = true; evidence.Add("Erreurs cumulées côté switch : leur progression doit être vérifiée."); }
            }
        }
        if (!suspect && !incomplete) evidence.Add("Aucune anomalie mesurée pendant cette observation ; ce contrôle passif ne certifie pas le câblage.");
        return new(suspect ? "Défaut probable" : incomplete ? "À vérifier" : "Câble OK", evidence);
    }
    public static long? Delta(long? current, long? previous) => current is not null && previous is not null && current >= previous ? current - previous : null;
    private static string Rate(long? speed) => speed > 0 ? $"{speed / 1_000_000} Mb/s" : "vitesse inconnue";
}
public sealed class LinkStability
{
    private readonly Queue<DateTimeOffset> transitions = new();
    private bool? last;
    public int Observe(bool up, DateTimeOffset now, bool expectedInterruption = false)
    {
        if (last is not null && last != up && !expectedInterruption) transitions.Enqueue(now);
        last = up;
        while (transitions.TryPeek(out var old) && now - old >= TimeSpan.FromSeconds(60)) transitions.Dequeue();
        return transitions.Count;
    }
}
