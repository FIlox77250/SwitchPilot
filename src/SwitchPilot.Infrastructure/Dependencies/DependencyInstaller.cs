namespace SwitchPilot.Infrastructure.Dependencies;

public enum DependencyState { Missing, Ready, Incompatible, Incomplete, Stopped, Restricted, Unavailable }
public record DependencyStatus(DependencyState State, string Version, string Detail)
{
    public bool Available => State == DependencyState.Ready;
    // Any state other than Ready can be improved by re-running the official installer,
    // including a restricted/stopped/incomplete driver. A refusal is never persisted.
    public bool OfferInstall => State != DependencyState.Ready;
    public string Label => $"Npcap · {Version} · {Detail}";
}
public record InstallProgress(string Message, double? Percent = null);
public interface IDependencyPlatform
{
    Task<DependencyStatus> InspectAsync(CancellationToken ct);
    Task DownloadAsync(string path, IProgress<InstallProgress> progress, CancellationToken ct);
    bool VerifyInstaller(string path);
    Task<int> LaunchInstallerAsync(string path, CancellationToken ct);
}

// No persisted refusal: a new startup always inspects and offers the missing dependency.
public sealed class DependencyInstaller(IDependencyPlatform platform)
{
    public Task<DependencyStatus> CheckAsync(CancellationToken ct = default) => platform.InspectAsync(ct);
    public async Task<DependencyStatus> InstallAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SwitchPilot-Npcap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "npcap-installer.exe");
        try
        {
            await platform.DownloadAsync(path, progress, ct);
            ct.ThrowIfCancellationRequested();
            progress.Report(new("Vérification de la signature de l’éditeur…"));
            // Hold the file against modification from signature verification until exit.
            // FILE_SHARE_DELETE keeps the file launchable by the elevated installer while
            // still denying any write, so the verified bytes cannot change under our feet.
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!platform.VerifyInstaller(path)) throw new InvalidOperationException("Signature Npcap absente, invalide, non vérifiable ou éditeur inattendu. Installation refusée.");
            ct.ThrowIfCancellationRequested();
            progress.Report(new("Terminez l’installateur Npcap. Laissez la compatibilité WinPcap cochée et l’accès réservé aux administrateurs décoché."));
            // Once elevated installation starts, it owns cancellation. Never kill a driver installer.
            var exit = await platform.LaunchInstallerAsync(path, ct);
            var status = await platform.InspectAsync(CancellationToken.None);
            if (status.Available) return status;
            return status with { Detail = exit switch
            {
                1 or 2 => "Installation annulée. SSH et console restent disponibles.",
                3010 => "Installation terminée ; Windows demande un redémarrage avant utilisation.",
                350 => "Installation impossible ; redémarrez Windows puis réessayez.",
                1618 => "Un autre installateur est déjà en cours.",
                1633 => "Cette version de Windows n’est pas prise en charge par Npcap.",
                _ => status.Detail
            }};
        }
        finally
        {
            // Cleanup must never mask the install result: a locked file or a lingering
            // helper process must not turn a successful install into an exception.
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            try { Directory.Delete(directory, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }
}
