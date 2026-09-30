using SwitchPilot.Infrastructure.Dependencies;
namespace SwitchPilot.Tests;
public class DependencyTests
{
    [Theory][InlineData(true)][InlineData(false)] public async Task StartupInspectsAndLaterDoesNotPersistRefusal(bool present)
    {
        var platform = new Platform { Present = present }; var first = new DependencyInstaller(platform);
        Assert.Equal(!present, (await first.CheckAsync()).OfferInstall);
        Assert.Equal(!present, (await new DependencyInstaller(platform).CheckAsync()).OfferInstall);
        Assert.Equal(0, platform.Launched);
    }
    [Fact] public async Task InstallationRechecksAndRemovesDownload()
    {
        var p = new Platform(); Assert.True((await new DependencyInstaller(p).InstallAsync(new Progress<InstallProgress>(), default)).Available);
        Assert.False(File.Exists(p.Path)); Assert.Equal(1, p.Launched);
    }
    [Theory][InlineData("signature")][InlineData("network")][InlineData("cancel")]
    public async Task FailureNeverExecutesAndAlwaysCleansUp(string failure)
    {
        var p = new Platform { Failure = failure };
        await Assert.ThrowsAnyAsync<Exception>(() => new DependencyInstaller(p).InstallAsync(new Progress<InstallProgress>(), default));
        Assert.Equal(0, p.Launched); Assert.False(File.Exists(p.Path));
    }
    [Fact] public async Task InstallerCancelledDoesNotClaimInstalled()
    {
        var p = new Platform { Exit = 1 }; var status = await new DependencyInstaller(p).InstallAsync(new Progress<InstallProgress>(), default);
        Assert.False(status.Available); Assert.Contains("annulée", status.Detail); Assert.False(File.Exists(p.Path));
    }
    private sealed class Platform : IDependencyPlatform
    {
        public bool Present; public string Failure = "", Path = ""; public int Launched, Exit;
        public Task<DependencyStatus> InspectAsync(CancellationToken ct) => Task.FromResult(new DependencyStatus(Present ? DependencyState.Ready : DependencyState.Missing, "1.89", "Test"));
        public Task DownloadAsync(string path, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            Path = path; File.WriteAllText(path, "test");
            if (Failure == "network") throw new System.Net.Http.HttpRequestException();
            if (Failure == "cancel") throw new OperationCanceledException();
            return Task.CompletedTask;
        }
        public bool VerifyInstaller(string path) => Failure != "signature";
        public Task<int> LaunchInstallerAsync(string path, CancellationToken ct) { Launched++; Present = Exit == 0; return Task.FromResult(Exit); }
    }
}
