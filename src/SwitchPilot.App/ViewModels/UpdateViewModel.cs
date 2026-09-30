using System.IO;
using System.Net.Http;
using System.Windows.Input;
using SwitchPilot.Infrastructure.Updates;

namespace SwitchPilot.App.ViewModels;

/// <summary>
/// Checks GitHub Releases for a newer SwitchPilot and, when authorised, downloads and
/// stages it. The actual swap runs in a detached script after this process exits.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly IUpdateSource source;
    private readonly UpdateInstaller installer = new();
    private readonly Version currentVersion;
    private CancellationTokenSource? cancellation;
    private UpdateRelease? release;
    private bool busy, available, indeterminate;
    private double progress;
    private string message = "Recherche d'une nouvelle version…";

    /// <summary>Raised when the user asks not to be offered this specific version again.</summary>
    public event Action<string>? SkipRequested;
    /// <summary>Raised once the update is staged and the application must exit so the script can swap the file.</summary>
    public event Action? RestartRequested;

    public UpdateRelease? Release { get => release; private set { Set(ref release, value); Raise(nameof(Notes)); Raise(nameof(VersionLabel)); Raise(nameof(HasNotes)); Raise(nameof(CanInstall)); } }
    public bool Available { get => available; private set { Set(ref available, value); Raise(nameof(CanInstall)); CommandManager.InvalidateRequerySuggested(); } }
    public bool Busy { get => busy; private set { Set(ref busy, value); Raise(nameof(CanInstall)); CommandManager.InvalidateRequerySuggested(); } }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool Indeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public string Notes => Release?.Notes?.Trim() is { Length: > 0 } notes ? notes : "(Aucune note de version publiée.)";
    public bool HasNotes => Release?.Notes?.Trim() is { Length: > 0 };
    public string VersionLabel => Release is null ? "" : $"Version {Release.Version} publiée le GitHub — {Release.Name}";
    public bool CanInstall => Available && !Busy && Release is not null && UpdateInstaller.CanSelfUpdate();

    public ICommand InstallCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand OpenPageCommand { get; }

    public UpdateViewModel(IUpdateSource? source = null)
    {
        this.source = source ?? new GitHubUpdateSource(GitHubUpdateSource.DefaultOwner, GitHubUpdateSource.DefaultRepository);
        currentVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => CanInstall);
        LaterCommand = new RelayCommand(() => { }, () => !Busy);
        SkipCommand = new RelayCommand(() => { if (Release is not null) SkipRequested?.Invoke(Release.Tag); }, () => !Busy && Release is not null);
        OpenPageCommand = new RelayCommand(() =>
        {
            var page = Release?.HtmlUri ?? new Uri("https://github.com/" + GitHubUpdateSource.DefaultOwner + "/" + GitHubUpdateSource.DefaultRepository + "/releases");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.ToString()) { UseShellExecute = true }); } catch { /* The link is informational. */ }
        });
    }

    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        Message = "Recherche d'une nouvelle version…";
        if (manual) { Available = false; Release = null; }
        try
        {
            var result = await source.CheckAsync(currentVersion, ct);
            if (!result.Checked)
            {
                Message = result.Error ?? "Vérification impossible.";
                if (manual) Available = false;
                return;
            }
            if (result.Release is null)
            {
                Message = $"Switch Pilot {currentVersion.ToString(3)} est à jour.";
                Available = false;
                return;
            }
            Release = result.Release;
            Available = true;
            Message = $"Une nouvelle version est disponible : {result.Release.Version}.";
        }
        catch (OperationCanceledException) { Message = "Vérification annulée."; }
        finally { Busy = false; Indeterminate = false; }
    }

    public async Task InstallAsync()
    {
        if (Busy || Release is null) return;
        var target = UpdateInstaller.CurrentExecutable;
        if (target is null || !UpdateInstaller.CanSelfUpdate(target))
        {
            Message = "Mise à jour automatique impossible depuis cet emplacement. Téléchargez la nouvelle version depuis la page GitHub.";
            return;
        }
        if (Release.Sha256 is null)
        {
            Message = "Cette version ne fournit pas d'empreinte SHA-256 vérifiable. Mise à jour automatique refusée ; utilisez la page GitHub.";
            return;
        }
        Busy = true; Progress = 0; Indeterminate = false;
        cancellation = new CancellationTokenSource();
        var staged = Path.Combine(UpdateInstaller.StagingDirectory, $"SwitchPilot-{Release.Version}.exe");
        try
        {
            if (File.Exists(staged)) File.Delete(staged);
            Message = "Téléchargement de la mise à jour…";
            await source.DownloadAsync(Release, staged, new Progress<UpdateProgress>(p => { Message = p.Message; Progress = p.Percent ?? 0; Indeterminate = p.Percent is null; }), cancellation.Token);
            Indeterminate = true;
            Message = "Vérification de l'empreinte SHA-256…";
            var actual = await Task.Run(() => UpdateInstaller.HashFile(staged), cancellation.Token);
            if (!string.Equals(actual, Release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(staged);
                Message = "Empreinte SHA-256 différente : mise à jour refusée par sécurité.";
                return;
            }
            Message = "Préparation du redémarrage…";
            var script = installer.CreateApplyScript(target, staged);
            installer.Launch(script);
            Message = "Mise à jour installée. Switch Pilot va redémarrer.";
            RestartRequested?.Invoke();
        }
        catch (OperationCanceledException) { TryDelete(staged); Message = "Téléchargement de la mise à jour annulé."; }
        catch (HttpRequestException) { TryDelete(staged); Message = "Téléchargement impossible. Vérifiez Internet ou le proxy, puis réessayez."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            TryDelete(staged);
            Message = e is InvalidOperationException ? e.Message : "Mise à jour impossible. Téléchargez la nouvelle version depuis la page GitHub.";
        }
        finally { cancellation?.Dispose(); cancellation = null; Busy = false; Indeterminate = false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
