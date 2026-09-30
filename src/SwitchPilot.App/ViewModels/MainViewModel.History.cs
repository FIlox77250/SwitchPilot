using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SwitchPilot.Core;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Infrastructure.Storage;
namespace SwitchPilot.App.ViewModels;
public sealed partial class MainViewModel
{
    private DiagnosticHistory? history;
    private string historyKey = "";
    private DateTimeOffset historyAt;
    private string historyFilter = "";
    public string HistoryFilter { get => historyFilter; set { Set(ref historyFilter, value); DiagnosticView.Refresh(); } }
    public ICollectionView DiagnosticView { get; private set; } = null!;
    public ObservableCollection<DiagnosticRecord> DiagnosticRecords { get; } = [];
    private DiagnosticRecord? selectedDiagnostic;
    public DiagnosticRecord? SelectedDiagnostic { get => selectedDiagnostic; set => Set(ref selectedDiagnostic, value); }
    private void InitializeHistory()
    {
        DiagnosticView = CollectionViewSource.GetDefaultView(DiagnosticRecords);
        DiagnosticView.Filter = item => item is DiagnosticRecord r && (r.Switch + " " + r.Port).Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase);
        history = new(store.DirectoryPath);
        try { history.Load(); foreach (var record in history.Records.AsEnumerable().Reverse()) DiagnosticRecords.Add(record); }
        catch { Status = "Historique de diagnostics illisible ; les mesures actuelles restent disponibles."; }
    }
    private void RecordCable(CableAssessment report, LocalLinkMeasurement? local, SwitchMeasurement? remote)
    {
        if (local is null || ResultPort == "—" || history is null || IsDemo) return;
        var key = $"{ResultSwitch}/{ResultPort}/{Adapter?.Id}/{CurrentGeneration}/{report.Verdict}";
        if (key == historyKey && DateTimeOffset.UtcNow - historyAt < TimeSpan.FromMinutes(1)) return;
        historyKey = key; historyAt = DateTimeOffset.UtcNow;
        SaveDiagnostic(new(historyAt, ResultSwitch, ResultPort, report.Verdict, report.Details, local, remote));
    }
    private void RecordTdr(string hostname, TdrResult result)
    {
        if (IsDemo) return;
        var verdict = result.Pairs.Any(p => p.Status.StartsWith("Court-circuit") || p.Status is "Ouvert" or "Impédance incorrecte") ? "Défaut probable" :
            result.Pairs.Count == 4 && result.Pairs.All(p => p.Status == "OK") ? "Câble OK" : "À vérifier";
        SaveDiagnostic(new(DateTimeOffset.UtcNow, hostname, result.Port, verdict, result.Note + "\n" + string.Join("\n", result.Pairs.Select(p => $"Paire {p.Pair} : {p.Status} · {p.Length}")), Tdr: result));
    }
    private void SaveDiagnostic(DiagnosticRecord record)
    {
        try { history?.Add(record); }
        catch { Status = "Le diagnostic est disponible, mais l’historique ne peut pas être enregistré."; }
        DiagnosticRecords.Insert(0, record); if (DiagnosticRecords.Count > 1000) DiagnosticRecords.RemoveAt(DiagnosticRecords.Count - 1);
    }
}
