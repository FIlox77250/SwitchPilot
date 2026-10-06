using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchPilot.Core;
using SwitchPilot.Core.Parsing;
using SwitchPilot.Core.Platforms;

namespace SwitchPilot.Infrastructure.Drivers;

/// <summary>
/// UniFi switch managed through the UniFi Network controller (HTTPS API), which is the only
/// supported way to change a UniFi switch: the controller pushes the configuration and would
/// overwrite a local change. Works with a classic controller (/api/login, /api/s/{site}/…) and
/// with UniFi OS consoles (/api/auth/login, /proxy/network/api/s/{site}/…, X-CSRF-Token).
/// Port changes rewrite the device "port_overrides" array (PUT rest/device/{id}); VLANs are
/// VLAN-only networks (rest/networkconf). Every accepted request is applied by the controller,
/// so there is no save step and nothing to roll back. Self-signed controller certificates are
/// trusted on first use, like SSH host keys. Experimental: built from the API as used by the
/// community clients and recorded replies.
/// </summary>
public sealed class UniFiControllerDriver : SwitchDriver
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly HttpClient http;
    private readonly ConnectionProfile profile;
    private string prefix = "";
    private bool unifiOs;
    private string? csrf;
    private bool connected;
    private int disposed;
    private SwitchIdentity? identity;

    private UniFiControllerDriver(HttpClient http, ConnectionProfile profile, IAuditSink audit, IConfigurationBackup? backup) : base(audit, backup)
    {
        this.http = http;
        this.profile = profile;
    }

    public override SwitchVendor Vendor => SwitchVendor.UniFi;
    public override ConnectionKind Kind => ConnectionKind.Api;
    public override bool IsConnected => connected && Volatile.Read(ref disposed) == 0;
    protected override string Hostname => identity?.Name ?? profile.Host.Trim();
    protected override string AcceptedMessage => "Requête acceptée par le contrôleur UniFi ; le switch est reprovisionné, relecture de l'état nécessaire.";
    /// <summary>True on a UniFi OS console (UDM, Cloud Key Gen2+, UniFi OS Server).</summary>
    public bool IsUniFiOs => unifiOs;

    /// <summary>
    /// Opens a session on the controller. <paramref name="trustCertificate"/> receives
    /// ("https://host:port", "SHA256 AA:BB:…") for a certificate that does not chain to a trusted
    /// root, and decides whether to continue (trust on first use). <paramref name="handler"/>
    /// replaces the network stack in tests.
    /// </summary>
    public static async Task<UniFiControllerDriver> ConnectAsync(ConnectionProfile profile, Func<string, string, bool> trustCertificate,
        IAuditSink audit, IConfigurationBackup? backup, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (profile.Kind != ConnectionKind.Api) throw new ArgumentException("Profil de connexion API attendu.");
        var label = $"https://{profile.Host.Trim()}:{profile.Port}";
        handler ??= CreateHandler(label, trustCertificate);
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, profile.Host.Trim(), profile.Port, "/").Uri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        var driver = new UniFiControllerDriver(http, profile, audit, backup);
        try
        {
            await driver.LoginAsync(ct);
            // Validates the site and the switch selection now rather than at the first refresh.
            await driver.IdentityAsync(ct);
            audit.Write("Connexion UniFi", $"{(driver.unifiOs ? "Console UniFi OS" : "Contrôleur UniFi")} · site {profile.Site} · {driver.identity!.Name} ({driver.identity.Model}).");
            return driver;
        }
        catch
        {
            await driver.DisposeAsync();
            throw;
        }
    }

    private static SocketsHttpHandler CreateHandler(string label, Func<string, string, bool> trustCertificate)
    {
        string? accepted = null;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
                    var fingerprint = Fingerprint(certificate);
                    // One question per connection pool, not one per TLS connection.
                    if (fingerprint == Volatile.Read(ref accepted)) return true;
                    if (!trustCertificate(label, fingerprint)) return false;
                    Volatile.Write(ref accepted, fingerprint);
                    return true;
                }
            }
        };
    }

    /// <summary>"SHA256 AA:BB:…", the form shown by browsers' certificate viewers.</summary>
    public static string Fingerprint(X509Certificate certificate) =>
        "SHA256 " + Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b);

    // ---- HTTP ------------------------------------------------------------------------------

    private string Site(string path) => $"{prefix}api/s/{profile.Site}/{path}";

    private async Task LoginAsync(CancellationToken ct)
    {
        connected = false;
        csrf = null;
        // UniFi OS answers its own landing page (200); a classic controller redirects to /manage.
        using (var probe = await SendRawAsync(HttpMethod.Get, "", null, ct))
            unifiOs = probe.StatusCode == HttpStatusCode.OK;
        prefix = unifiOs ? "proxy/network/" : "";
        var credentials = new JsonObject { ["username"] = profile.Username, ["password"] = profile.Password, ["remember"] = false };
        using var response = await SendRawAsync(HttpMethod.Post, unifiOs ? "api/auth/login" : "api/login", credentials, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Identifiants refusés par le contrôleur UniFi. Utilisez un compte administrateur local (les comptes Ubiquiti avec double authentification ne sont pas pris en charge).");
        await EnsureSuccessAsync(response, unifiOs ? "api/auth/login" : "api/login", ct);
        if (!unifiOs) UniFiParser.Data(await response.Content.ReadAsStringAsync(ct));
        csrf = Header(response, "X-CSRF-Token") ?? Cookie(response, "csrf_token");
        connected = true;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault(v => v.Length > 0) : null;

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0].Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim() == name).Select(p => p[1].Trim()).FirstOrDefault()
            : null;

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Le contrôleur UniFi ne répond pas (délai de 30 s dépassé).", ex); }
        catch (HttpRequestException ex)
        { throw new IOException("Contrôleur UniFi inaccessible ou certificat refusé. Vérifiez l'adresse, le port HTTPS et l'empreinte du certificat.", ex); }
        if (Header(response, "X-Updated-CSRF-Token") is { } updated) csrf = updated;
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try { detail = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["meta"]?["msg"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Session refusée par le contrôleur UniFi : identifiants invalides ou session expirée.",
            HttpStatusCode.Forbidden => "Droits insuffisants sur le contrôleur UniFi : un compte administrateur (pas en lecture seule) est requis.",
            HttpStatusCode.NotFound => $"Ressource introuvable sur le contrôleur UniFi ({path}) : vérifiez le nom court du site.",
            HttpStatusCode.TooManyRequests => "Trop de tentatives auprès du contrôleur UniFi : patientez une minute avant de réessayer.",
            _ => $"Le contrôleur UniFi a répondu HTTP {(int)response.StatusCode}{(detail is null ? "" : $" ({detail})")}."
        });
    }

    /// <summary>Site request; an expired session is reopened once.</summary>
    private async Task<string> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(UniFiControllerDriver));
        var response = await SendRawAsync(method, Site(path), body, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await LoginAsync(ct);
            response = await SendRawAsync(method, Site(path), body, ct);
        }
        using (response)
        {
            await EnsureSuccessAsync(response, path, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            UniFiParser.Data(json);
            return json;
        }
    }

    // ---- Reads -----------------------------------------------------------------------------

    private async Task<(JsonObject Device, IReadOnlyList<UniFiNetwork> Networks)> ReadDeviceAsync(CancellationToken ct)
    {
        var device = UniFiParser.Switch(await SendAsync(HttpMethod.Get, "stat/device", null, ct), profile.Device);
        var networks = UniFiParser.Networks(await SendAsync(HttpMethod.Get, "rest/networkconf", null, ct));
        identity = UniFiParser.Identity(device);
        return (device, networks);
    }

    protected override async Task<SwitchIdentity> IdentityAsync(CancellationToken ct) =>
        identity ??= UniFiParser.Identity(UniFiParser.Switch(await SendAsync(HttpMethod.Get, "stat/device", null, ct), profile.Device));

    protected override async Task<SwitchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        var (device, networks) = await ReadDeviceAsync(ct);
        return new(identity!, UniFiParser.Ports(device, networks), UniFiParser.Vlans(device, networks));
    }

    protected override async Task<IReadOnlyList<MacEntry>> MacTableCoreAsync(CancellationToken ct)
    {
        var device = UniFiParser.Switch(await SendAsync(HttpMethod.Get, "stat/device", null, ct), profile.Device);
        string? clients = null;
        try { clients = await SendAsync(HttpMethod.Get, "stat/sta", null, ct); }
        catch (InvalidOperationException) { /* Learned MAC table only. */ }
        return UniFiParser.Macs(device, clients);
    }

    protected override async Task<InterfaceCounters> CountersCoreAsync(string port, CancellationToken ct) =>
        UniFiParser.Counters(UniFiParser.Switch(await SendAsync(HttpMethod.Get, "stat/device", null, ct), profile.Device), port);

    /// <summary>
    /// The switch object and the site networks as JSON. "x_*" fields (inform key, SSH host key,
    /// WAN passwords) are secrets the controller never needs back: they are not exported.
    /// </summary>
    protected override async Task<string> ExportCoreAsync(CancellationToken ct)
    {
        var device = UniFiParser.Switch(await SendAsync(HttpMethod.Get, "stat/device", null, ct), profile.Device);
        var networks = UniFiParser.Data(await SendAsync(HttpMethod.Get, "rest/networkconf", null, ct));
        var export = new JsonObject
        {
            ["exported"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["site"] = profile.Site,
            ["device"] = WithoutSecrets(device.DeepClone()),
            ["networkconf"] = WithoutSecrets(networks.DeepClone())
        };
        return export.ToJsonString(Indented);
    }

    private static JsonNode? WithoutSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).Where(k => k.StartsWith("x_", StringComparison.Ordinal)).ToArray()) obj.Remove(key);
                foreach (var (_, child) in obj) WithoutSecrets(child);
                break;
            case JsonArray array:
                foreach (var child in array) WithoutSecrets(child);
                break;
        }
        return node;
    }

    // ---- Writes ----------------------------------------------------------------------------

    /// <summary>The plan steps only describe the request; the request itself is built from fresh controller data.</summary>
    protected override async Task ExecutePlanAsync(CommandPlan plan, PlanProgress progress, CancellationToken ct)
    {
        var (device, networks) = await ReadDeviceAsync(ct);
        switch (plan.Kind)
        {
            case ChangeKind.CreateVlan:
                await SendAsync(HttpMethod.Post, "rest/networkconf", UniFiParser.NewNetwork(plan.Vlan!.Value, plan.Value!, networks), ct);
                break;
            case ChangeKind.DeleteVlan:
                var network = UniFiParser.NetworkToDelete(plan.Vlan!.Value, networks);
                await SendAsync(HttpMethod.Delete, "rest/networkconf/" + Uri.EscapeDataString(network.Id), null, ct);
                break;
            case ChangeKind.AccessVlan or ChangeKind.Trunk or ChangeKind.Description:
                var id = device["_id"]?.GetValue<string>() ?? throw new FormatException("Switch UniFi sans identifiant « _id ».");
                var body = new JsonObject { ["port_overrides"] = UniFiParser.PortOverrides(device, plan, networks) };
                await SendAsync(HttpMethod.Put, "rest/device/" + Uri.EscapeDataString(id), body, ct);
                break;
            default:
                throw new NotSupportedException("Modification non prise en charge par l'API UniFi dans Switch Pilot.");
        }
        progress.Completed = plan.Steps.Count;
    }

    protected override Task<string> RunStepAsync(PlanStep step, CancellationToken ct) =>
        throw new NotSupportedException("Les étapes UniFi décrivent une requête API ; elles ne sont jamais tapées.");

    // A request is applied as a whole by the controller: nothing is left half-configured.
    protected override Task RecoverAsync(CommandPlan plan) => Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (connected)
        {
            try
            {
                using var logout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var _ = await SendRawAsync(HttpMethod.Post, unifiOs ? "api/auth/logout" : "api/logout", new JsonObject(), logout.Token);
            }
            catch { /* Best effort: the controller expires the session anyway. */ }
        }
        connected = false;
        http.Dispose();
    }
}
