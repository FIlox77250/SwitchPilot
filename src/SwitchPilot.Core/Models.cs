namespace SwitchPilot.Core;

public record PortInfo(string Name, string Description, string Status, string Vlan, string Duplex,
    string Speed, string Media, string Mode = "Inconnu")
{
    public bool IsUp => Status.Equals("connected", StringComparison.OrdinalIgnoreCase);
    public string StateLabel => Status switch { "connected" => "Actif", "notconnect" => "Débranché", "disabled" => "Désactivé", "err-disabled" => "Erreur", _ => Status };
    public string SpeedLabel => Speed.Replace("a-", "") is var s && int.TryParse(s, out _) ? s + " Mb/s" : Speed;
    public string DuplexLabel => Duplex.Replace("a-", "");
    public bool IsTrunk => Mode.Equals("trunk", StringComparison.OrdinalIgnoreCase) || Vlan.Equals("trunk", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;
}
public record VlanInfo(int Id, string Name, string Status, string Ports);
public record MacEntry(int Vlan, string Mac, string Type, string Port);
public record SwitchIdentity(string Name, string Model, string IosVersion);
public record InterfaceCounters(long? Crc, long? Collisions, long? InputErrors, string Speed, string Duplex, string LinkState = "Inconnu", long? OutputErrors = null);
public record TdrPair(string Pair, string Length, string RemotePair, string Status);
public record TdrResult(string Port, IReadOnlyList<TdrPair> Pairs, string Note);
public record NeighborAnnouncement(string Protocol, string SwitchName, string Port, int? Vlan, int TtlSeconds, DateTimeOffset ReceivedAt);
public record LocalAdapter(string Id, string Name, string Mac, bool IsUp, long Speed, string Addresses)
{
    public string Label => $"{Name} · {(IsUp ? "connecté" : "débranché")} · {Mac}";
    public override string ToString() => Label;
}
public record PortDetection(string SwitchName, MacEntry Entry, PortInfo Port, bool DirectCandidate, string Note);
public record SwitchSnapshot(SwitchIdentity Identity, IReadOnlyList<PortInfo> Ports, IReadOnlyList<VlanInfo> Vlans);
public record DetectionObservation(SwitchSnapshot Snapshot, IReadOnlyList<MacEntry> Entries);
// Api = HTTPS controller (UniFi Network) instead of a CLI shell. Values are persisted as
// integers in the user settings: append new members, never reorder.
public enum ConnectionKind { Ssh, Serial, Api }
// The switch family drives the CLI dialect and the read-only command set. Cisco IOS and
// Allied Telesis AlliedWare Plus share enough of the switching syntax to reuse one core.
// AlliedS95 is the older AT-S95 firmware (AT-8000GS…), a Cisco-Small-Business-style CLI.
// Platform metadata (display name, dialect, maturity) lives in Platforms.SwitchPlatforms.
// Persisted as integers: append new members, never reorder.
public enum SwitchVendor
{
    Cisco, AlliedTelesis, AlliedS95,
    CiscoNxos, Arista, DellOs6, DellOs9, DellOs10, Huawei, Juniper, MikroTik, UbiquitiEdge, UniFi
}
public record ConnectionProfile(string Host = "", int Port = 22, string Username = "", bool Remember = false, string Password = "", string EnablePassword = "")
{
    public ConnectionKind Kind { get; init; }
    public SwitchVendor Vendor { get; init; } = SwitchVendor.Cisco;
    /// <summary>When true, the switch family is detected from `show version`; Vendor is only a fallback.</summary>
    public bool AutoDetectVendor { get; init; } = true;
    public string SerialPort { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public bool AutoBaud { get; init; }
    /// <summary>UniFi controller site name (API mode only).</summary>
    public string Site { get; init; } = "default";
    /// <summary>UniFi switch MAC address or name (API mode only); empty when the site has a single switch.</summary>
    public string Device { get; init; } = "";
    public string Label => Kind switch
    {
        ConnectionKind.Serial => $"Console {SerialPort} · {(AutoBaud ? "auto" : BaudRate.ToString())}",
        ConnectionKind.Api => $"https://{Host}:{Port} · {Site}{(string.IsNullOrWhiteSpace(Device) ? "" : " · " + Device)}",
        _ => $"{Host}:{Port}"
    };
    public void Validate()
    {
        if (Username is null || Password is null || EnablePassword is null || Username.Any(char.IsControl) || Password.Any(char.IsControl) || EnablePassword.Any(char.IsControl))
            throw new ArgumentException("Identifiants invalides : caractère de contrôle.");
        if (!Enum.IsDefined(Vendor)) throw new ArgumentException("Constructeur de switch inconnu.");
        if (Kind == ConnectionKind.Ssh && (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username) || Port is < 1 or > 65535))
            throw new ArgumentException("Adresse, utilisateur et port SSH valide requis.");
        if (Kind == ConnectionKind.Serial && (!System.Text.RegularExpressions.Regex.IsMatch(SerialPort ?? "", @"^COM[1-9][0-9]{0,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || !SerialBaudRates.Contains(BaudRate)))
            throw new ArgumentException("Port COM ou vitesse non valide.");
        if (Kind == ConnectionKind.Api)
        {
            if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username) || Port is < 1 or > 65535 || Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '@'))
                throw new ArgumentException("Adresse du contrôleur, utilisateur et port HTTPS valide requis.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(Site ?? "", "^[A-Za-z0-9_-]{1,64}$"))
                throw new ArgumentException("Site UniFi invalide (lettres, chiffres, « - » et « _ »).");
            if ((Device ?? "").Length > 64 || (Device ?? "").Any(char.IsControl))
                throw new ArgumentException("Switch UniFi invalide : adresse MAC ou nom de 64 caractères maximum.");
            if (Vendor != SwitchVendor.UniFi) throw new ArgumentException("Le mode API n'est disponible que pour un contrôleur UniFi.");
        }
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Mode de connexion inconnu.");
    }
    public static IReadOnlyList<int> SerialBaudRates { get; } = Array.AsReadOnly(new[] { 9600, 19200, 38400, 57600, 115200 });
    // Never expose record-generated credential dumps in UI fallbacks or diagnostics.
    public override string ToString() => Label;
}

public interface ICliSession : IAsyncDisposable
{
    ConnectionKind Kind => ConnectionKind.Ssh;
    bool IsConnected { get; }
    string Hostname { get; }
    /// <summary>Last prompt seen (e.g. "SW#", "&lt;HUAWEI&gt;", "admin@ex2300&gt;"); used for platform detection.</summary>
    string Prompt => "";
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default);
    /// <summary>
    /// Runs a command that is EXPECTED to ask one confirmation ("(y/n)", "[Y/N]") and answers it
    /// exactly once. Any other or repeated question still aborts the session.
    /// </summary>
    Task<string> ExecuteConfirmedAsync(string command, string answer, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Cette session ne sait pas répondre à une confirmation attendue.");
}

/// <summary>What the driver found on the other end: platform family and identity.</summary>
public record DeviceInfo(SwitchVendor Vendor, string Platform, SwitchIdentity Identity);
/// <summary>Operational state of one port: link, speed and duplex as reported by the switch.</summary>
public record LinkStatus(string Port, bool IsUp, string State, string Speed, string Duplex);

/// <summary>
/// Common switch driver contract. The first block is the historical plan-based API used by the
/// GUI; the second block is the vendor-neutral semantic API (detect, ports, VLANs, set VLAN,
/// link, save). Default implementations express the semantic API with the primitives, so the
/// demo driver gets it for free; real drivers derive from Infrastructure.Drivers.SwitchDriver.
/// </summary>
public interface ISwitchDriver : IAsyncDisposable
{
    ConnectionKind Kind => ConnectionKind.Ssh;
    SwitchVendor Vendor => SwitchVendor.Cisco;
    bool IsConnected { get; }
    bool IsDemo { get; }
    Task<SwitchSnapshot> ReadSnapshotAsync(CancellationToken ct = default);
    Task<DetectionObservation> ReadDetectionAsync(string mac, CancellationToken ct = default);
    Task<IReadOnlyList<MacEntry>> ReadMacTableAsync(CancellationToken ct = default);
    Task<InterfaceCounters> ReadCountersAsync(string port, CancellationToken ct = default);
    Task<TdrResult> RunTdrAsync(string port, SafetyContext safety, CancellationToken ct = default);
    Task ApplyAsync(CommandPlan plan, bool dryRun, CancellationToken ct = default, SafetyContext? safety = null);
    Task<string> ExportAsync(CancellationToken ct = default);

    async Task<DeviceInfo> DetectDeviceAsync(CancellationToken ct = default) =>
        new(Vendor, Platforms.SwitchPlatforms.Get(Vendor).DisplayName, (await ReadSnapshotAsync(ct)).Identity);
    async Task<IReadOnlyList<PortInfo>> GetPortsAsync(CancellationToken ct = default) => (await ReadSnapshotAsync(ct)).Ports;
    async Task<IReadOnlyList<VlanInfo>> GetVlansAsync(CancellationToken ct = default) => (await ReadSnapshotAsync(ct)).Vlans;
    /// <summary>Puts a port in access mode on <paramref name="vlan"/> through the same safety pipeline as the GUI.</summary>
    Task SetPortVlanAsync(string port, int vlan, SafetyContext? safety = null, bool dryRun = false, CancellationToken ct = default) =>
        ApplyAsync(CommandPlan.Access(port, vlan, Vendor), dryRun, ct, safety);
    async Task<LinkStatus> GetLinkStatusAsync(string port, CancellationToken ct = default)
    {
        var counters = await ReadCountersAsync(port, ct);
        return new(port, counters.LinkState == "Actif", counters.LinkState, counters.Speed, counters.Duplex);
    }
    /// <summary>Persists the running configuration (write memory, save, implicit commit…) as the platform requires.</summary>
    Task SaveConfigAsync(bool dryRun = false, CancellationToken ct = default) => ApplyAsync(CommandPlan.Save(Vendor), dryRun, ct);
}
public record SafetyContext(bool IsFresh, bool ManagementPathKnown, IReadOnlySet<string> ProtectedPorts)
{
    public Func<bool>? StillCurrent { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public static SafetyContext Unknown => new(false, false, new HashSet<string>());
}
public record AuditEntry(DateTimeOffset Time, string Action, string Result)
{
    public string TimeLabel => Time.ToLocalTime().ToString("HH:mm:ss");
}
public interface IAuditSink { void Write(string action, string result); }
