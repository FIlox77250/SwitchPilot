using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SwitchPilot.Infrastructure.Updates;

/// <summary>A newer application release published on GitHub.</summary>
public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    Uri AssetUri,
    long AssetSize,
    string? Sha256,
    Uri HtmlUri);

/// <summary>Outcome of a single update probe. Never throws for the expected network cases.</summary>
public sealed record UpdateCheckResult(bool Checked, UpdateRelease? Release, string? Error)
{
    public static UpdateCheckResult UpToDate { get; } = new(true, null, null);
    public static UpdateCheckResult Available(UpdateRelease release) => new(true, release, null);
    public static UpdateCheckResult Failed(string error) => new(false, null, error);
}

public sealed record UpdateProgress(string Message, double? Percent = null);

/// <summary>Source of update metadata and binary. Replaced in tests.</summary>
public interface IUpdateSource
{
    Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct);
    Task DownloadAsync(UpdateRelease release, string path, IProgress<UpdateProgress> progress, CancellationToken ct);
}

/// <summary>
/// Reads the latest public release from the GitHub Releases API. Only HTTPS URLs on an
/// explicit host allow-list are accepted, so a compromised response cannot redirect the
/// download elsewhere. Never downloads anything during a check.
/// </summary>
public sealed class GitHubUpdateSource(string owner, string repository, string assetName = "SwitchPilot.exe", HttpMessageHandler? handler = null) : IUpdateSource
{
    public const string DefaultOwner = "FIlox77250";
    public const string DefaultRepository = "SwitchPilot";
    public const string DefaultAssetName = "SwitchPilot.exe";

    private static readonly string[] DownloadHosts = ["github.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com", "release-assets.githubusercontent.com"];
    private const long MaxReleaseJson = 4 * 1024 * 1024;
    private const long MaxAssetSize = 256L * 1024 * 1024;

    private HttpClient CreateClient()
    {
        // AllowAutoRedirect=false so every hop of the release download is re-validated against
        // the host allow-list below; otherwise the manual redirect loop is never exercised.
        var client = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SwitchPilot-Updater/1.0 (+https://github.com/" + owner + "/" + repository + ")");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync($"https://api.github.com/repos/{owner}/{repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return UpdateCheckResult.Failed("Aucune version publiée n'est encore disponible sur GitHub.");
            if (response.StatusCode == (HttpStatusCode)403 && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
                return UpdateCheckResult.Failed("Limite de requêtes GitHub atteinte. Réessayez plus tard.");
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"Vérification impossible (réponse HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is > MaxReleaseJson)
                return UpdateCheckResult.Failed("Réponse GitHub anormalement volumineuse.");
            var json = await response.Content.ReadAsStringAsync(ct);
            if (json.Length > MaxReleaseJson) return UpdateCheckResult.Failed("Réponse GitHub anormalement volumineuse.");
            var release = ParseLatest(json, current, assetName);
            return release is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available(release);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return UpdateCheckResult.Failed("Vérification expirée. Vérifiez votre connexion Internet."); }
        catch (HttpRequestException) { return UpdateCheckResult.Failed("Vérification impossible. Vérifiez votre connexion Internet ou le proxy."); }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return UpdateCheckResult.Failed("Réponse GitHub illisible ; vérification ignorée."); }
    }

    /// <summary>Pure parser kept separate so the version and asset rules can be unit tested without network access.</summary>
    public static UpdateRelease? ParseLatest(string json, Version current, string assetName = DefaultAssetName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) return null;
        var tag = tagElement.GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= Normalize(current)) return null;
        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() ?? tag : tag;
        var notes = root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String ? bodyElement.GetString() ?? "" : "";
        var html = root.TryGetProperty("html_url", out var htmlElement) && htmlElement.ValueKind == JsonValueKind.String ? htmlElement.GetString() : null;
        if (html is null || !Uri.TryCreate(html, UriKind.Absolute, out var htmlUri) || htmlUri.Scheme != Uri.UriSchemeHttps) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            if (!asset.TryGetProperty("name", out var assetNameElement) || assetNameElement.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(assetNameElement.GetString(), assetName, StringComparison.Ordinal)) continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String) return null;
            if (!Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;
            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String)
            {
                var digest = digestElement.GetString() ?? "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71 && digest[7..].All(char.IsAsciiHexDigit))
                    sha256 = digest[7..].ToLowerInvariant();
            }
            return new UpdateRelease(version, tag, name, notes, uri, size, sha256, htmlUri);
        }
        return null;
    }

    /// <summary>"v1.2.3", "1.2.3" and "1.2" are accepted; missing components compare as zero.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));

    public async Task DownloadAsync(UpdateRelease release, string path, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        using var client = CreateClient();
        var uri = release.AssetUri;
        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (uri.Scheme != Uri.UriSchemeHttps || !DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || uri.UserInfo.Length != 0)
                    throw new InvalidOperationException("Redirection de téléchargement non autorisée.");
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    uri = new Uri(uri, next); response.Dispose(); response = null; continue;
                }
                break;
            }
            if (response is null) throw new HttpRequestException("Trop de redirections.");
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaxAssetSize || release.AssetSize > MaxAssetSize) throw new InvalidDataException("Mise à jour trop volumineuse.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
            var buffer = new byte[131072]; long count = 0;
            // ResponseHeadersRead means HttpClient.Timeout only covers the headers, so guard the
            // body with an inactivity timeout; abort a stalled transfer instead of hanging.
            using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
            inactivity.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                int read;
                try { read = await input.ReadAsync(buffer, inactivity.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Téléchargement interrompu : aucune donnée reçue pendant 60 secondes."); }
                if (read == 0) break;
                inactivity.CancelAfter(TimeSpan.FromSeconds(60));
                count += read;
                if (count > MaxAssetSize) throw new InvalidDataException("Mise à jour trop volumineuse.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                progress.Report(new("Téléchargement de la mise à jour…", total is > 0 ? count * 100d / total : null));
            }
            if (count == 0 || total is not null && count != total) throw new InvalidDataException("Téléchargement incomplet.");
        }
        finally { response?.Dispose(); }
    }
}
