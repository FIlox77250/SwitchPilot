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
public enum ConnectionKind { Ssh, Serial }
// The switch family drives the CLI dialect and the read-only command set. Cisco IOS and
// Allied Telesis AlliedWare Plus share enough of the switching syntax to reuse one core.
// AlliedS95 is the older AT-S95 firmware (AT-8000GS…), a Cisco-Small-Business-style CLI.
public enum SwitchVendor { Cisco, AlliedTelesis, AlliedS95 }
public record ConnectionProfile(string Host = "", int Port = 22, string Username = "", bool Remember = false, string Password = "", string EnablePassword = "")
{
    public ConnectionKind Kind { get; init; }
    public SwitchVendor Vendor { get; init; } = SwitchVendor.Cisco;
    /// <summary>When true, the switch family is detected from `show version`; Vendor is only a fallback.</summary>
    public bool AutoDetectVendor { get; init; } = true;
    public string SerialPort { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public bool AutoBaud { get; init; }
    public string Label => Kind == ConnectionKind.Serial ? $"Console {SerialPort} · {(AutoBaud ? "auto" : BaudRate.ToString())}" : $"{Host}:{Port}";
    public void Validate()
    {
        if (Username is null || Password is null || EnablePassword is null || Username.Any(char.IsControl) || Password.Any(char.IsControl) || EnablePassword.Any(char.IsControl))
            throw new ArgumentException("Identifiants invalides : caractère de contrôle.");
        if (!Enum.IsDefined(Vendor)) throw new ArgumentException("Constructeur de switch inconnu.");
        if (Kind == ConnectionKind.Ssh && (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username) || Port is < 1 or > 65535))
            throw new ArgumentException("Adresse, utilisateur et port SSH valide requis.");
        if (Kind == ConnectionKind.Serial && (!System.Text.RegularExpressions.Regex.IsMatch(SerialPort ?? "", @"^COM[1-9][0-9]{0,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || !SerialBaudRates.Contains(BaudRate)))
            throw new ArgumentException("Port COM ou vitesse non valide.");
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
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default);
}
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
