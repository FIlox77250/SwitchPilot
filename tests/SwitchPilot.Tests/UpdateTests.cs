using SwitchPilot.Infrastructure.Updates;

namespace SwitchPilot.Tests;

public class UpdateTests
{
    private const string Digest = "sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(string tag, string version = "v1.0.3", string assetName = "SwitchPilot.exe", string url = "https://github.com/o/r/releases/download/v1.0.3/SwitchPilot.exe", string digest = Digest, bool draft = false, bool prerelease = false) =>
        $$"""
        {"tag_name":"{{tag}}","name":"Switch Pilot {{tag}}","body":"Corrections et nouveautés","html_url":"https://github.com/o/r/releases/tag/{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":[{"name":"{{assetName}}","browser_download_url":"{{url}}","size":123456,"digest":"{{digest}}"}]}
        """;

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("0.9.9.9", 0, 9, 9)]
    public void VersionTagsAreParsedAndNormalized(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateSource.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build, tag.Count(c => c == '.') >= 3 ? 9 : 0), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("nightly")]
    [InlineData("v")]
    public void InvalidTagsAreRejected(string tag) => Assert.False(GitHubUpdateSource.TryParseVersion(tag, out _));

    [Fact]
    public void NewerReleaseWithPortableAssetIsSelected()
    {
        var release = GitHubUpdateSource.ParseLatest(Release("v1.0.3"), new Version(1, 0, 2, 0));
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 0, 3, 0), release!.Version);
        Assert.Equal("v1.0.3", release.Tag);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", release.Sha256);
    }

    [Fact]
    public void SameOrOlderReleaseIsNotAnUpdate()
    {
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.2"), new Version(1, 0, 2, 0)));
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.1"), new Version(1, 0, 2, 0)));
    }

    [Fact]
    public void ThreeComponentTagEqualsFourComponentAssemblyVersion()
    {
        // 1.0.2 must not look older than 1.0.2.0, which would re-offer the installed version.
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.2"), new Version(1, 0, 2, 0)));
    }

    [Fact]
    public void DraftsAndPrereleasesAreIgnored()
    {
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.3", draft: true), new Version(1, 0, 2, 0)));
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.3", prerelease: true), new Version(1, 0, 2, 0)));
    }

    [Fact]
    public void ForeignDownloadHostIsRejected()
    {
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.3", url: "https://evil.example.com/SwitchPilot.exe"), new Version(1, 0, 2, 0)));
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.3", url: "http://github.com/SwitchPilot.exe"), new Version(1, 0, 2, 0)));
    }

    [Fact]
    public void MissingPortableAssetIsIgnored()
    {
        Assert.Null(GitHubUpdateSource.ParseLatest(Release("v1.0.3", assetName: "SwitchPilot-1.0.3-win-x64.zip"), new Version(1, 0, 2, 0)));
    }

    [Fact]
    public void AssetDigestWithoutSha256PrefixIsTreatedAsUnavailable()
    {
        var release = GitHubUpdated(Release("v1.0.3", digest: "md5:whatever"));
        Assert.Null(release.Sha256);
    }

    private static UpdateRelease GitHubUpdated(string json) => GitHubUpdateSource.ParseLatest(json, new Version(1, 0, 2, 0))!;

    [Fact]
    public void SelfUpdateOnlyAcceptsThePortableExecutableName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "switchpilot-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var exe = Path.Combine(directory, "SwitchPilot.exe");
            File.WriteAllText(exe, "stub");
            Assert.True(UpdateInstaller.CanSelfUpdate(exe));
            var other = Path.Combine(directory, "other.exe");
            File.WriteAllText(other, "stub");
            Assert.False(UpdateInstaller.CanSelfUpdate(other));
            Assert.False(UpdateInstaller.CanSelfUpdate(Path.Combine(directory, "missing.exe")));
            Assert.Equal(UpdateInstaller.HashFile(exe), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("stub"u8.ToArray())).ToLowerInvariant());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ApplyScriptIsWrittenAndUnsafePathsAreRefused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "switchpilot-script-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(Path.GetTempPath(), "switchpilot-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "SwitchPilot.exe");
            var source = Path.Combine(directory, "SwitchPilot-1.0.6.exe");
            File.WriteAllText(target, "old");
            File.WriteAllText(source, "new");
            // Use a dedicated staging directory so the test never deletes a real staged update.
            var installer = new UpdateInstaller(staging);
            var script = installer.CreateApplyScript(target, source);
            Assert.True(File.Exists(script));
            var content = File.ReadAllText(script);
            Assert.Contains(target, content);
            Assert.Contains(source, content);
            Assert.Throws<InvalidOperationException>(() => installer.CreateApplyScript(Path.Combine(directory, "%TEMP%\\SwitchPilot.exe"), source));
        }
        finally { Directory.Delete(directory, true); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
