using System.Windows;
using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Discovery;
namespace SwitchPilot.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly NetworkMonitor monitor = new();
    private IReadOnlyList<AdapterObservation> observations = [];
    private PortDetection? macDetection;
    private string macAdapter = "";
    private long macGeneration = -1;
    private long CurrentGeneration => observations.FirstOrDefault(o => o.Adapter.Id == Adapter?.Id)?.Generation ?? 0;
    private IReadOnlyList<NeighborAnnouncement> Neighbors => observations.FirstOrDefault(o => o.Adapter.Id == Adapter?.Id)?.Neighbors ?? [];
    private void DependencyChanged() { monitor.EnableCapture(Dependencies.Available); RenderDetection(); }
    private void SelectAdapter(LocalAdapter? value)
    {
        var previous = adapter;
        if (!Set(ref adapter, value, nameof(Adapter))) return;
        if (previous?.Id != value?.Id || previous?.Mac != value?.Mac || value?.IsUp != true) ClearDetection();
        detectionSchedule.Request(); RenderDetection();
    }
    private void ObservationsChanged(IReadOnlyList<AdapterObservation> state) => dispatcher.BeginInvoke(() =>
    {
        if (disposed || IsDemo) return;
        var previous = observations; observations = state;
        var selected = Adapter?.Id;
        var ordered = state.OrderByDescending(o => o.Adapter.IsUp).ThenBy(o => o.Adapter.Id).Select(o => o.Adapter).ToArray();
        var changed = !Adapters.SequenceEqual(ordered);
        if (changed)
        {
            Adapters.Clear(); foreach (var item in ordered) Adapters.Add(item);
            Adapter = Adapters.FirstOrDefault(a => a.Id == selected && a.IsUp) ?? Adapters.FirstOrDefault(a => a.IsUp) ?? Adapters.FirstOrDefault();
        }
        if (macDetection is not null && (Adapter?.Id != macAdapter || CurrentGeneration != macGeneration || Adapter?.IsUp != true)) ClearDetection();
        if (changed || !previous.Select(o => (o.Adapter.Id, o.Generation)).SequenceEqual(state.Select(o => (o.Adapter.Id, o.Generation)))) detectionSchedule.Request();
        RenderDetection(); ObserveLinks();
    });
    private async Task Detect(CancellationToken ct)
    {
        detectionSchedule.MarkStarted(DateTimeOffset.UtcNow);
        var selected = Adapter; var generation = CurrentGeneration;
        if (selected is null || !selected.IsUp) { ClearDetection(); return; }
        var activeDriver = driver;
        if (activeDriver is null) return;
        var observation = await activeDriver.ReadDetectionAsync(selected.Mac, ct);
        if (driver != activeDriver || Adapter?.Id != selected.Id || CurrentGeneration != generation ||
            !IsDemo && !NetworkDiscovery.Adapters().Any(a => a.Id == selected.Id && a.Mac == selected.Mac && a.IsUp)) return;
        var matches = PortLocator.Find(selected.Mac, observation.Snapshot, observation.Entries);
        macDetection = matches.Count == 1 ? matches[0] : null;
        macAdapter = selected.Id; macGeneration = generation;
        lastDetection = DateTimeOffset.UtcNow;
        if (matches.Count != 1)
        {
            RenderDetection();
            DetectionNote += matches.Count == 0 ? " MAC absente du switch connecté." : " Plusieurs correspondances MAC : port non confirmé.";
            return;
        }
        RenderDetection();
        var summary = $"{ResultSwitch}, {ResultPort}, VLAN {ResultVlan}, {ResultSpeed} {ResultDuplex}";
        if (summary != detectionSummary) { audit.Write("Détection du port", summary); detectionSummary = summary; }
        Status = summary;
        try { await AutomaticPassive(ct); }
        catch (SwitchPilot.Infrastructure.Terminal.CliException e) { PassiveNote = e.Message; }
        catch (NotSupportedException e) { TdrNote = e.Message; }
    }
    private Task Capture(CancellationToken ct)
    {
        if (!Dependencies.Available) { DependenciesCommand.Execute(null); return Task.CompletedTask; }
        monitor.Redetect(); CaptureNote = "Écoute LLDP/CDP en cours…";
        return Task.CompletedTask;
    }
    private void RenderDetection()
    {
        var announcements = Neighbors;
        var distinct = announcements.Select(n => (n.SwitchName.ToLowerInvariant(), CiscoParser.NormalizeInterface(n.Port))).Distinct().ToArray();
        var neighbor = distinct.Length == 1 ? announcements.LastOrDefault() : null;
        if (macDetection is { } mac && Adapter?.Id == macAdapter && CurrentGeneration == macGeneration)
        {
            ResultSwitch = mac.SwitchName; ResultPort = mac.Port.Name; ResultVlan = mac.Entry.Vlan.ToString();
            ResultSpeed = mac.Port.SpeedLabel; ResultDuplex = mac.Port.DuplexLabel;
            Source = $"{(IsDemo ? "Démonstration" : driver?.Kind == ConnectionKind.Serial ? "Table MAC console" : "Table MAC SSH")} · {lastDetection.ToLocalTime():HH:mm:ss}";
            DetectionNote = mac.Note;
            if (neighbor is not null) DetectionNote += " " + NeighborCorrelation.Compare(neighbor, mac);
            if (distinct.Length > 1) DetectionNote += " Annonces LLDP/CDP contradictoires : branchement non confirmé.";
        }
        else if (neighbor is not null && Adapter?.IsUp == true)
        {
            ResultSwitch = neighbor.SwitchName; ResultPort = neighbor.Port; ResultVlan = neighbor.Vlan?.ToString() ?? "Non annoncé";
            ResultSpeed = Adapter.Speed > 0 ? $"{Adapter.Speed / 1_000_000} Mb/s (poste)" : "Inconnue"; ResultDuplex = "Inconnu";
            Source = string.Join(" / ", announcements.Select(n => n.Protocol).Distinct()) + $" · {neighbor.ReceivedAt.ToLocalTime():HH:mm:ss}";
            DetectionNote = "Annonce locale non authentifiée ; le VLAN annoncé peut être natif. Les mesures du poste ne sont pas celles du switch.";
        }
        else
        {
            ResultSwitch = "Votre point de connexion"; ResultPort = "—"; ResultVlan = "—"; ResultSpeed = "—"; ResultDuplex = "—";
            Source = "Aucune détection actuelle";
            DetectionNote = distinct.Length > 1 ? "Annonces LLDP/CDP contradictoires : aucun port unique confirmé." :
                !Dependencies.Available && !Connected ? "Sans Npcap ni switch connecté, le port ne peut pas être connu." : "En attente d’une annonce ou d’une correspondance MAC actuelle.";
        }
        UpdateOutletAndNotifications();
        if (Adapter is not null && measurements.TryGetValue(Adapter.Id, out var measured) && measured.Generation == CurrentGeneration && macDetection is null && neighbor is not null)
        { ResultDuplex = measured.Current.Duplex ?? "Inconnu"; }
        var error = observations.FirstOrDefault(o => o.Adapter.Id == Adapter?.Id)?.CaptureError;
        CaptureNote = !Dependencies.Available ? Dependencies.Banner : !string.IsNullOrEmpty(error) ? error :
            announcements.Count > 0 ? string.Join(" · ", announcements.Select(n => $"{n.Protocol} {n.SwitchName}/{n.Port}, VLAN {n.Vlan?.ToString() ?? "non annoncé"}, {n.ReceivedAt.ToLocalTime():HH:mm:ss}")) :
            "Écoute continue. Si aucune annonce n’arrive, vérifiez « lldp run » ou « cdp run » sur le switch. Aucune activation automatique.";
    }
    private void ClearDetection()
    {
        macDetection = null; macAdapter = ""; macGeneration = -1; lastDetection = default;
        RenderDetection(); RefreshCable();
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (disposed || IsBusy || IsDemo) return;
        if (driver is not null && !Connected) { ClearDetection(); Connection = "Session déconnectée"; Raise(nameof(Connected)); }
        if (Connected && Adapter?.IsUp == true && detectionSchedule.TryStart(DateTimeOffset.UtcNow)) Run("Détection automatique", Detect);
    }
}
