using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Diagnostics;
namespace SwitchPilot.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, LinkStability> stability = new();
    private readonly Dictionary<string, (LocalLinkMeasurement Current, LocalLinkMeasurement? Previous, long Generation)> measurements = new();
    private readonly HashSet<string> autoTdrCompleted = new();
    private bool measuringLocal;
    private DateTimeOffset nextLocalMeasurement, expectedInterruptionUntil;
    private SwitchMeasurement? switchMeasurement, previousSwitchMeasurement;
    private string measuredSwitch = "", measuredAdapter = "";
    private long measuredGeneration = -1;
    private string cableVerdict = "À vérifier", cableDetails = "Aucune mesure disponible.", linkLabel = "Lien non mesuré";
    public string CableVerdict { get => cableVerdict; private set => Set(ref cableVerdict, value); }
    public string CableDetails { get => cableDetails; private set => Set(ref cableDetails, value); }
    public string LinkLabel { get => linkLabel; private set => Set(ref linkLabel, value); }
    private void ObserveLinks()
    {
        foreach (var observation in observations)
        {
            if (!stability.TryGetValue(observation.Adapter.Id, out var tracker)) stability[observation.Adapter.Id] = tracker = new();
            tracker.Observe(observation.Adapter.IsUp, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow < expectedInterruptionUntil);
            if (!observation.Adapter.IsUp && DateTimeOffset.UtcNow >= expectedInterruptionUntil) autoTdrCompleted.Remove(observation.Adapter.Id);
        }
        MeasureLocal(); RefreshCable();
    }
    private async void MeasureLocal()
    {
        if (measuringLocal || IsDemo || disposed || DateTimeOffset.UtcNow < nextLocalMeasurement) return;
        measuringLocal = true; nextLocalMeasurement = DateTimeOffset.UtcNow.AddSeconds(3);
        var current = observations.Select(o => (Observation: o, Transitions: stability[o.Adapter.Id].Observe(o.Adapter.IsUp, DateTimeOffset.UtcNow))).ToArray();
        try
        {
            var results = await Task.Run(() => current.Select(item => (item.Observation, Measurement: LocalLinkProbe.Read(item.Observation.Adapter, item.Transitions))).ToArray(), lifetime.Token);
            if (disposed || IsDemo) return;
            foreach (var item in results)
            {
                var latest = observations.FirstOrDefault(o => o.Adapter.Id == item.Observation.Adapter.Id);
                if (latest is null || latest.Generation != item.Observation.Generation || latest.Adapter.IsUp != item.Measurement.IsUp) continue;
                var old = measurements.GetValueOrDefault(item.Measurement.AdapterId);
                measurements[item.Measurement.AdapterId] = (item.Measurement, old.Generation == latest.Generation ? old.Current : null, latest.Generation);
            }
            RefreshCable();
        }
        catch (OperationCanceledException) { }
        catch { if (!disposed) { CableVerdict = "À vérifier"; CableDetails = "Mesures Windows indisponibles ; aucune conclusion sur le câble."; } }
        finally { measuringLocal = false; }
    }
    private bool CanCompareSwitch => macDetection?.DirectCandidate == true && Adapter?.Id == macAdapter && CurrentGeneration == macGeneration &&
        Neighbors.All(n => NeighborCorrelation.SameSwitch(n.SwitchName, macDetection.SwitchName) && PortNames.Same(n.Port, macDetection.Port.Name));
    private async Task AutomaticPassive(CancellationToken ct)
    {
        if (!CanCompareSwitch || driver is null || Adapter is null) return;
        var mac = macDetection!; var adapterId = Adapter.Id; var generation = CurrentGeneration;
        var counters = await driver.ReadCountersAsync(mac.Port.Name, ct);
        if (Adapter?.Id != adapterId || CurrentGeneration != generation || !CanCompareSwitch) return;
        previousSwitchMeasurement = measuredSwitch == mac.SwitchName && measuredAdapter == adapterId && measuredGeneration == generation ? switchMeasurement : null;
        switchMeasurement = new(mac.Port.Name, DateTimeOffset.UtcNow, counters);
        measuredSwitch = mac.SwitchName; measuredAdapter = adapterId; measuredGeneration = generation;
        RefreshCable();
        if (!IsDemo && driver.Kind == ConnectionKind.Serial && store.Settings.AutoTdrConsole && !DryRun && autoTdrCompleted.Add(adapterId))
        {
            expectedInterruptionUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            TdrNote = "TDR automatique console activé : interruption temporaire du lien Ethernet.";
            try
            {
                var result = await driver.RunTdrAsync(mac.Port.Name, new(true, true, new HashSet<string>()), ct);
                TdrPairs.Clear(); foreach (var pair in result.Pairs) TdrPairs.Add(pair); TdrNote = result.Note;
                RecordTdr(mac.SwitchName, result);
            }
            finally { expectedInterruptionUntil = DateTimeOffset.UtcNow.AddSeconds(15); monitor.Redetect(); ClearDetection(); detectionSchedule.Request(); }
        }
    }
    private void RefreshCable()
    {
        if (IsDemo) { CableVerdict = "Démonstration"; CableDetails = "Mesures fictives du mode démonstration."; LinkLabel = "Lien fictif actif"; return; }
        var sample = Adapter is null ? default : measurements.GetValueOrDefault(Adapter.Id);
        var current = sample.Generation == CurrentGeneration ? sample.Current : null;
        if (Adapter?.IsUp == false) current = new(Adapter.Id, DateTimeOffset.UtcNow, false, null, null, null, null, null, null, null,
            stability.TryGetValue(Adapter.Id, out var link) ? link.Observe(false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow < expectedInterruptionUntil) : 0);
        var remote = CanCompareSwitch && measuredAdapter == Adapter?.Id && measuredGeneration == CurrentGeneration && measuredSwitch == macDetection?.SwitchName ? switchMeasurement : null;
        var report = CableAssessment.Evaluate(current, sample.Previous, remote, previousSwitchMeasurement);
        CableVerdict = report.Verdict; CableDetails = report.Details;
        LinkLabel = current is null ? "Lien non mesuré" : $"Lien {(current.IsUp ? "actif" : "inactif")} · {(current.Speed > 0 ? current.Speed / 1_000_000 + " Mb/s" : "vitesse inconnue")} · {current.Duplex ?? "duplex inconnu"}";
        RecordCable(report, current, remote); NotifyCable();
    }
}
