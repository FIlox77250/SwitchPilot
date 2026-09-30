using System.Net.Http;
using System.Windows.Input;
using SwitchPilot.Infrastructure.Dependencies;

namespace SwitchPilot.App.ViewModels;

public sealed class DependenciesViewModel : ObservableObject
{
    private readonly DependencyInstaller installer = new(new NpcapPlatform());
    private CancellationTokenSource? cancellation;
    private DependencyStatus status = new(DependencyState.Missing, "—", "Vérification…");
    private bool busy;
    private double progress;
    private bool indeterminate;
    private string message = "";
    public DependencyStatus Status { get => status; private set { Set(ref status, value); Raise(nameof(Available)); Raise(nameof(Banner)); Changed?.Invoke(); } }
    public bool Available => Status.Available;
    public string Banner => Available ? Status.Label : "Détection automatique indisponible · " + Status.Detail;
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool Indeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public string SerialDrivers { get; private set; } = "";
    public event Action? Changed;
    public ICommand InstallCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ManualCommand { get; } = new RelayCommand(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/") { UseShellExecute = true }));
    public DependenciesViewModel()
    {
        InstallCommand = new RelayCommand(() => Run(true), () => !Busy);
        CheckCommand = new RelayCommand(() => Run(false), () => !Busy);
        CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => Busy);
    }
    public async Task CheckAsync(CancellationToken ct = default)
    {
        Status = await installer.CheckAsync(ct);
        try { SerialDrivers = string.Join("\n", (await Task.Run(SwitchPilot.Infrastructure.Serial.SerialDeviceCatalog.Scan)).Select(d => d.Label + " : " + d.Guidance)); }
        catch { SerialDrivers = "Inventaire série indisponible. Consultez le Gestionnaire de périphériques."; }
        if (SerialDrivers.Length == 0) SerialDrivers = "Aucun adaptateur console détecté.";
        Raise(nameof(SerialDrivers));
    }
    private async void Run(bool install)
    {
        if (Busy) return;
        Busy = true; cancellation = new(); Message = "Vérification…"; Progress = 0;
        try
        {
            if (install) Status = await installer.InstallAsync(new Progress<InstallProgress>(p => { Message = p.Message; Progress = p.Percent ?? 0; Indeterminate = p.Percent is null; }), cancellation.Token);
            else await CheckAsync(cancellation.Token);
            Message = Status.Detail;
        }
        catch (OperationCanceledException) { Message = "Installation ou téléchargement annulé. Vous pouvez continuer en SSH ou console."; }
        catch (HttpRequestException) { Message = "Téléchargement impossible. Vérifiez Internet et le proxy, ou utilisez l’installation manuelle sur npcap.com."; }
        catch (Exception e) when (e is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Message = e is InvalidOperationException ? e.Message : "Installation impossible. Utilisez le lien npcap.com pour une installation manuelle."; }
        finally { cancellation.Dispose(); cancellation = null; Busy = false; Indeterminate = false; }
    }
}
