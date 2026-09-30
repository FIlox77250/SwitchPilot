using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SwitchPilot.App.Services;
using SwitchPilot.App.Views;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Storage;
namespace SwitchPilot.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly WindowsNotifications notifications = new();
    private OutletStore? outletStore;
    private string outletName = "", resultOutlet = "", inventoryStatus = "", notificationDetection = "", notifiedVerdict = "";
    public string OutletName { get => outletName; set => Set(ref outletName, value); }
    public string ResultOutlet { get => resultOutlet; private set => Set(ref resultOutlet, value); }
    public string InventoryStatus { get => inventoryStatus; private set => Set(ref inventoryStatus, value); }
    public ObservableCollection<OutletEntry> Outlets { get; } = [];
    public ICommand SettingsCommand { get; private set; } = null!;
    public ICommand InventoryCommand { get; private set; } = null!;
    public ICommand SaveOutletCommand { get; private set; } = null!;
    public ICommand ImportOutletsCommand { get; private set; } = null!;
    public ICommand ExportOutletsCommand { get; private set; } = null!;
    public ICommand CopyResultCommand { get; private set; } = null!;
    public ICommand CompareBackupCommand { get; private set; } = null!;
    private void InitializeExtras()
    {
        outletStore = new(store.DirectoryPath);
        try { outletStore.Load(); ReloadOutlets(); } catch { InventoryStatus = "Inventaire illisible ; fichier conservé."; }
        SettingsCommand = new RelayCommand(() =>
        {
            new SettingsWindow(store, this).ShowDialog();
            detectionSchedule.Interval = TimeSpan.FromSeconds(store.Settings.DetectionIntervalSeconds);
        }, () => !IsBusy);
        InventoryCommand = new RelayCommand(() => { OutletName = ResultOutlet; new InventoryWindow(this).ShowDialog(); });
        SaveOutletCommand = new RelayCommand(() => InventoryAction(() =>
        {
            outletStore.Merge([new(ResultSwitch, ResultPort, OutletName)]); ReloadOutlets(); UpdateOutletAndNotifications(); InventoryStatus = "Prise enregistrée localement.";
        }), () => ResultPort != "—" && !IsDemo);
        ImportOutletsCommand = new RelayCommand(() => InventoryAction(() =>
        {
            var dialog = new OpenFileDialog { Filter = "Inventaire CSV|*.csv" };
            if (dialog.ShowDialog() != true) return;
            if (new FileInfo(dialog.FileName).Length > 4 * 1024 * 1024) throw new InvalidDataException("CSV trop volumineux.");
            var entries = OutletCsv.Import(File.ReadAllText(dialog.FileName));
            var replacements = entries.Count(e => outletStore.Lookup(e.Switch, e.Port).Length > 0);
            if (replacements > 0 && !Dialogs.Confirm($"L’import contient {replacements} ports déjà présents. Remplacer leurs noms de prise ?", "Importer l’inventaire")) return;
            outletStore.Merge(entries); ReloadOutlets(); UpdateOutletAndNotifications(); InventoryStatus = $"{entries.Count} prises importées.";
        }));
        ExportOutletsCommand = new RelayCommand(() => InventoryAction(() =>
        {
            var dialog = new SaveFileDialog { Filter = "Inventaire CSV|*.csv", FileName = "SwitchPilot-prises.csv" };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, OutletCsv.Export(outletStore.Entries), new System.Text.UTF8Encoding(true)); InventoryStatus = "Inventaire CSV exporté.";
        }));
        CopyResultCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText($"{ResultSwitch}, {ResultPort}, VLAN {ResultVlan}, {ResultSpeed} {ResultDuplex}, {CableVerdict}" + (ResultOutlet.Length > 0 ? $", {ResultOutlet}" : "")); Status = "Résultat copié pour le ticket."; }
            catch { Status = "Presse-papiers indisponible ; réessayez."; }
        }, () => ResultPort != "—");
        CompareBackupCommand = new RelayCommand(() => Run("Comparer la configuration", async ct =>
        {
            var dialog = new OpenFileDialog { Filter = "Sauvegarde chiffrée|*.spbackup", InitialDirectory = Path.Combine(store.DirectoryPath, "Backups") };
            if (dialog.ShowDialog() != true) return;
            var before = UserStore.ReadEncryptedExport(dialog.FileName);
            var current = await driver!.ExportAsync(ct);
            Dialogs.CompareConfigurations(before, current);
        }), CanRead);
    }
    private void InventoryAction(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidDataException) { InventoryStatus = e.Message; }
        catch { InventoryStatus = "Impossible de lire ou d’enregistrer l’inventaire."; }
    }
    private void ReloadOutlets() { Outlets.Clear(); foreach (var entry in outletStore!.Entries.OrderBy(e => e.Switch).ThenBy(e => e.Port)) Outlets.Add(entry); }
    private void UpdateOutletAndNotifications()
    {
        ResultOutlet = ResultPort == "—" ? "" : outletStore?.Lookup(ResultSwitch, ResultPort) ?? "";
        if (IsDemo || disposed) return;
        var key = $"{Adapter?.Id}/{CurrentGeneration}/{ResultSwitch}/{ResultPort}";
        if (ResultPort != "—" && notificationDetection != key)
        { notifications.Show("Port détecté", $"{ResultSwitch}, {ResultPort}, VLAN {ResultVlan}" + (ResultOutlet.Length > 0 ? " · " + ResultOutlet : "")); notificationDetection = key; }
    }
    private void NotifyCable()
    {
        var key = $"{Adapter?.Id}/{CurrentGeneration}/{CableVerdict}";
        if (!IsDemo && CableVerdict == "Défaut probable" && key != notifiedVerdict)
        { notifications.Show("Câble à contrôler", $"{ResultSwitch} · {ResultPort} · {CableVerdict}. Consultez les mesures dans Diagnostics."); notifiedVerdict = key; }
    }
}
