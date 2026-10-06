using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Drivers;

namespace SwitchPilot.Tests;

/// <summary>UniFi Network controller driver against a recorded-style HTTP controller.</summary>
public class UniFiControllerTests
{
    private const string Ok = """{"meta":{"rc":"ok"},"data":[]}""";
    private static SafetyContext Safe => new(true, true, new HashSet<string>());

    private static ConnectionProfile Profile(string site = "default") =>
        new("unifi.local", 8443, "admin", false, "secret") { Kind = ConnectionKind.Api, Vendor = SwitchVendor.UniFi, Site = site };

    private static Task<UniFiControllerDriver> Connect(Controller controller, IAuditSink? audit = null, IConfigurationBackup? backup = null, ConnectionProfile? profile = null) =>
        UniFiControllerDriver.ConnectAsync(profile ?? Profile(), (_, _) => false, audit ?? new Audit(), backup, CancellationToken.None, controller);

    [Fact]
    public async Task ClassicControllerLogin()
    {
        var controller = new Controller(unifiOs: false);
        var audit = new Audit();
        await using var driver = await Connect(controller, audit);
        Assert.False(driver.IsUniFiOs);
        Assert.True(driver.IsConnected);
        Assert.Equal(ConnectionKind.Api, driver.Kind);
        Assert.Equal(["GET /", "POST /api/login", "GET /api/s/default/stat/device"], controller.Requests.Select(r => $"{r.Method} {r.Path}"));
        Assert.Equal("""{"username":"admin","password":"secret","remember":false}""", controller.Requests[1].Body);
        Assert.Equal(("Connexion UniFi", "Contrôleur UniFi · site default · USW-Bureau (US24P250)."), audit.Entries.Single());
        // The CSRF token of the classic controller comes from its cookie.
        Assert.Equal("csrf-1", controller.Requests[2].Csrf);
    }

    [Fact]
    public async Task UniFiOsLoginUsesTheNetworkProxy()
    {
        var controller = new Controller(unifiOs: true);
        await using var driver = await Connect(controller);
        Assert.True(driver.IsUniFiOs);
        Assert.Equal(["GET /", "POST /api/auth/login", "GET /proxy/network/api/s/default/stat/device"], controller.Requests.Select(r => $"{r.Method} {r.Path}"));
        Assert.Null(controller.Requests[1].Csrf);
        Assert.Equal("csrf-1", controller.Requests[2].Csrf);
    }

    [Fact]
    public async Task RotatedCsrfTokenIsForwarded()
    {
        var controller = new Controller(unifiOs: true);
        await using var driver = await Connect(controller);
        controller.UpdatedCsrf = "csrf-rotated";
        Assert.Equal([1, 10, 20, 30], (await driver.GetVlansAsync()).Select(v => v.Id));
        Assert.Equal("csrf-1", controller.Requests[^2].Csrf);
        Assert.Equal("/proxy/network/api/s/default/rest/networkconf", controller.Requests[^1].Path);
        Assert.Equal("csrf-rotated", controller.Requests[^1].Csrf);
    }

    [Fact]
    public async Task ExpiredSessionLogsInAgainOnce()
    {
        var controller = new Controller(unifiOs: false);
        await using var driver = await Connect(controller);
        controller.ExpiredReplies = 1;
        Assert.Equal(4, (await driver.GetPortsAsync()).Count);
        Assert.Equal(2, controller.Logins);
        controller.ExpiredReplies = 2;
        Assert.Contains("session expirée", (await Assert.ThrowsAsync<InvalidOperationException>(() => driver.GetPortsAsync())).Message);
    }

    [Fact]
    public async Task RejectedCredentials()
    {
        var controller = new Controller(unifiOs: true) { LoginStatus = HttpStatusCode.Unauthorized };
        Assert.Contains("Identifiants refusés", (await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(controller))).Message);
        Assert.DoesNotContain(controller.Requests, r => r.Path.EndsWith("logout"));
    }

    [Fact]
    public async Task UnknownSiteIsExplainedAndTheSessionClosed()
    {
        var controller = new Controller(unifiOs: false);
        Assert.Contains("nom court du site", (await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(controller, profile: Profile("bureau")))).Message);
        Assert.Equal("POST /api/logout", $"{controller.Requests[^1].Method} {controller.Requests[^1].Path}");
    }

    [Fact]
    public async Task OnlyApiProfilesAreAccepted()
    {
        var ssh = new ConnectionProfile("unifi.local", 22, "admin", false, "secret") { Vendor = SwitchVendor.UniFi };
        await Assert.ThrowsAsync<ArgumentException>(() => Connect(new Controller(false), profile: ssh));
        await Assert.ThrowsAsync<ArgumentException>(() => Connect(new Controller(false), profile: Profile("bad site")));
    }

    [Fact]
    public async Task DescriptionRewritesPortOverrides()
    {
        var controller = new Controller(unifiOs: false)
        {
            Devices = MultiVendorParserTests.UniFiDevicesJson.Replace("\"name\":\"USW-Bureau\"", "\"name\":\"USW-Bureau\",\"x_authkey\":\"secret-key\"")
        };
        var audit = new Audit();
        var backup = new Backup();
        await using var driver = await Connect(controller, audit, backup);
        await driver.ApplyAsync(CommandPlan.Describe("Port 1", "Camera hall", SwitchVendor.UniFi), false);
        var put = controller.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/s/default/rest/device/dev1", put.Path);
        var overrides = JsonNode.Parse(put.Body!)!["port_overrides"]!.AsArray();
        Assert.Equal([2, 3, 1], overrides.Select(o => o!["port_idx"]!.GetValue<int>()));
        Assert.Equal("Camera hall", overrides[2]!["name"]!.GetValue<string>());
        // Backup before the change, without the controller secrets.
        var (host, configuration) = backup.Saved.Single();
        Assert.Equal("USW-Bureau", host);
        Assert.Contains("\"networkconf\"", configuration);
        Assert.DoesNotContain("secret-key", configuration);
        Assert.Equal("Requête acceptée par le contrôleur UniFi ; le switch est reprovisionné, relecture de l'état nécessaire.", audit.Entries[^1].Result);
    }

    [Fact]
    public async Task SetPortVlanSendsAnAccessOverride()
    {
        var controller = new Controller(unifiOs: true);
        await using var driver = await Connect(controller, backup: new Backup());
        await driver.SetPortVlanAsync("1", 20, Safe);
        var put = controller.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/proxy/network/api/s/default/rest/device/dev1", put.Path);
        Assert.Equal("csrf-1", put.Csrf);
        var target = JsonNode.Parse(put.Body!)!["port_overrides"]!.AsArray()[^1]!;
        Assert.Equal((1, "n20", "block_all"), (target["port_idx"]!.GetValue<int>(), target["native_networkconf_id"]!.GetValue<string>(), target["tagged_vlan_mgmt"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SetPortVlanIsGuardedLikeTheCli()
    {
        var controller = new Controller(unifiOs: false);
        await using var driver = await Connect(controller, backup: new Backup());
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetPortVlanAsync("Port 1", 20, SafetyContext.Unknown));
        Assert.Contains("n'existe plus", (await Assert.ThrowsAsync<InvalidOperationException>(() => driver.SetPortVlanAsync("Port 1", 40, Safe))).Message);
        await driver.SetPortVlanAsync("Port 1", 20, Safe, dryRun: true);
        Assert.DoesNotContain(controller.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task WritesNeedTheEncryptedBackup()
    {
        var controller = new Controller(unifiOs: false);
        await using var driver = await Connect(controller);
        Assert.Contains("sauvegarde chiffrée", (await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.Describe("Port 1", "x", SwitchVendor.UniFi), false))).Message);
        Assert.DoesNotContain(controller.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task CreateAndDeleteVlanUseNetworkConf()
    {
        var controller = new Controller(unifiOs: false);
        await using var driver = await Connect(controller, backup: new Backup());
        await driver.ApplyAsync(CommandPlan.CreateVlan(40, "Cameras", SwitchVendor.UniFi), false);
        var post = controller.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/s/default/rest/networkconf");
        Assert.Equal("""{"name":"Cameras","purpose":"vlan-only","vlan_enabled":true,"vlan":40,"igmp_snooping":false}""", post.Body);
        await driver.ApplyAsync(CommandPlan.DeleteVlan(20, SwitchVendor.UniFi), false);
        Assert.Equal("/api/s/default/rest/networkconf/n20", controller.Requests.Single(r => r.Method == HttpMethod.Delete).Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyAsync(CommandPlan.DeleteVlan(10, SwitchVendor.UniFi), false));
    }

    [Fact]
    public async Task ReadsAndImplicitSave()
    {
        var controller = new Controller(unifiOs: false);
        var audit = new Audit();
        var driver = await Connect(controller, audit);
        Assert.Equal(new LinkStatus("Port 1", true, "Actif", "1000", "Full"), await driver.GetLinkStatusAsync("Port 1"));
        Assert.Equal([new MacEntry(1, "001122334455", "DYNAMIC", "Port 1")], await driver.ReadMacTableAsync());
        Assert.Equal(new DeviceInfo(SwitchVendor.UniFi, SwitchPilot.Core.Platforms.SwitchPlatforms.Get(SwitchVendor.UniFi).DisplayName, new SwitchIdentity("USW-Bureau", "US24P250", "6.6.65.15435")), await driver.DetectDeviceAsync());
        var requests = controller.Requests.Count;
        await driver.SaveConfigAsync();
        Assert.Equal(requests, controller.Requests.Count);
        Assert.StartsWith("Sauvegarde implicite", audit.Entries[^1].Result);
        await driver.DisposeAsync();
        Assert.False(driver.IsConnected);
        Assert.Equal("POST /api/logout", $"{controller.Requests[^1].Method} {controller.Requests[^1].Path}");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => driver.GetPortsAsync());
    }

    [Fact]
    public void FingerprintOfASelfSignedCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=unifi.local", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = UniFiControllerDriver.Fingerprint(certificate);
        Assert.Matches("^SHA256 (?:[0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), fingerprint[7..].Replace(":", ""));
    }

    // ---- Fakes ----------------------------------------------------------------------------

    /// <summary>Classic controller (redirect on "/", /api/login, csrf cookie) or UniFi OS console (/api/auth/login, X-CSRF-Token, /proxy/network).</summary>
    private sealed class Controller(bool unifiOs) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Csrf)> Requests { get; } = [];
        public string Devices { get; init; } = MultiVendorParserTests.UniFiDevicesJson;
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;
        public int ExpiredReplies { get; set; }
        public string? UpdatedCsrf { get; set; }
        public int Logins { get; private set; }
        private string Prefix => unifiOs ? "/proxy/network/api/s/default/" : "/api/s/default/";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, body, request.Headers.TryGetValues("X-CSRF-Token", out var csrf) ? csrf.Single() : null));
            if (path == "/")
                return unifiOs ? Reply(HttpStatusCode.OK, "<html>UniFi OS</html>") : new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/manage", UriKind.Relative) } };
            if (path == (unifiOs ? "/api/auth/login" : "/api/login"))
            {
                Logins++;
                if (LoginStatus != HttpStatusCode.OK) return Reply(LoginStatus, """{"meta":{"rc":"error","msg":"api.err.Invalid"},"data":[]}""");
                var login = Reply(HttpStatusCode.OK, unifiOs ? """{"username":"admin"}""" : Ok);
                if (unifiOs) login.Headers.Add("X-CSRF-Token", $"csrf-{Logins}");
                else login.Headers.Add("Set-Cookie", [$"unifises=s{Logins}; Path=/; HttpOnly", $"csrf_token=csrf-{Logins}; Path=/"]);
                return login;
            }
            if (path == (unifiOs ? "/api/auth/logout" : "/api/logout")) return Reply(HttpStatusCode.OK, Ok);
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return Reply(HttpStatusCode.NotFound, """{"meta":{"rc":"error","msg":"api.err.NoSiteContext"},"data":[]}""");
            if (ExpiredReplies > 0)
            {
                ExpiredReplies--;
                return Reply(HttpStatusCode.Unauthorized, """{"meta":{"rc":"error","msg":"api.err.LoginRequired"},"data":[]}""");
            }
            var reply = (request.Method.Method, path[Prefix.Length..]) switch
            {
                ("GET", "stat/device") => Reply(HttpStatusCode.OK, Devices),
                ("GET", "rest/networkconf") => Reply(HttpStatusCode.OK, MultiVendorParserTests.UniFiNetworksJson),
                ("GET", "stat/sta") => Reply(HttpStatusCode.OK, Ok),
                ("PUT", var resource) when resource.StartsWith("rest/device/") => Reply(HttpStatusCode.OK, Ok),
                ("POST", "rest/networkconf") or ("DELETE", _) => Reply(HttpStatusCode.OK, Ok),
                _ => Reply(HttpStatusCode.BadRequest, """{"meta":{"rc":"error","msg":"api.err.InvalidPayload"},"data":[]}""")
            };
            if (UpdatedCsrf is { } updated)
            {
                reply.Headers.Add("X-Updated-CSRF-Token", updated);
                UpdatedCsrf = null;
            }
            return reply;
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Audit : IAuditSink
    {
        public List<(string Action, string Result)> Entries { get; } = [];
        public void Write(string action, string result) => Entries.Add((action, result));
    }

    private sealed class Backup : IConfigurationBackup
    {
        public List<(string Host, string Configuration)> Saved { get; } = [];
        public Task SaveAsync(string hostname, string configuration, CancellationToken ct)
        {
            Saved.Add((hostname, configuration));
            return Task.CompletedTask;
        }
    }
}
