using System.Windows;
using SwitchPilot.App.Services;
using SwitchPilot.App.ViewModels;
using SwitchPilot.Infrastructure.Storage;
namespace SwitchPilot.App.Views;
public partial class SettingsWindow : Window
{
    private readonly UserStore store;
    public SettingsWindow(UserStore store, MainViewModel model)
    {
        InitializeComponent(); this.store = store; DataContext = model; Owner = Application.Current.MainWindow;
        Interval.Text = store.Settings.DetectionIntervalSeconds.ToString(); AutoTdr.IsChecked = store.Settings.AutoTdrConsole;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Interval.Text, out var interval) || interval is < 10 or > 3600) { Error.Text = "Intervalle attendu : 10 à 3600 secondes."; return; }
        if (AutoTdr.IsChecked == true && !store.Settings.AutoTdrConsole && !Dialogs.Confirm("Autoriser les interruptions de lien dues au TDR automatique en console ? La simulation continuera à empêcher tout TDR réel tant qu’elle reste activée.", "TDR automatique")) return;
        try { store.SaveOptions(interval, AutoTdr.IsChecked == true); DialogResult = true; }
        catch { Error.Text = "Impossible d’enregistrer les paramètres chiffrés."; }
    }
}
