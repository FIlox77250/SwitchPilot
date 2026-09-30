using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SwitchPilot.App.Services;
using SwitchPilot.App.Views;
using SwitchPilot.Core;
using SwitchPilot.Core.Cisco;
using SwitchPilot.Core.Diagnostics;
using SwitchPilot.Core.Discovery;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Discovery;
using SwitchPilot.Infrastructure.Serial;
using SwitchPilot.Infrastructure.Ssh;
using SwitchPilot.Infrastructure.Terminal;
using SwitchPilot.Infrastructure.Storage;

namespace SwitchPilot.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly UserStore store = new();
    private readonly AuditLog audit;
    private readonly Dispatcher dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? operation;
    private ISwitchDriver? driver;
    private ConnectionProfile? profile;
    private SwitchSnapshot? snapshot;
    private bool disposed;
    private DateTimeOffset lastDetection;
    private readonly DetectionSchedule detectionSchedule = new();
    private bool busy, dryRun = true;
    private string status = "Prêt. Connectez un switch ou explorez la démonstration.", connection = "Aucun switch connecté", search = "";
    private string resultSwitch = "Votre point de connexion", resultPort = "—", resultVlan = "—", resultSpeed = "—", resultDuplex = "—", detectionNote = "Connectez un switch pour retrouver votre carte Ethernet dans sa table MAC.", source = "Aucune détection", model = "SSH · Cisco IOS ou Allied Telesis", passiveNote = "Sélectionnez un port, puis lancez le contrôle passif.", captureNote = "Capture optionnelle : nécessite Npcap et des annonces LLDP/CDP.", counters = "CRC —   ·   Collisions —   ·   Erreurs entrantes —", tdrNote = "Aucun test lancé.", detectionSummary = "";
    private LocalAdapter? adapter;
    private PortInfo? selectedPort;
    private VlanInfo? selectedVlan;
    private string vlanId = "10", vlanName = "", description = "", nativeVlan = "1", allowedVlans = "1,10,20";
    private int tab;
    public ObservableCollection<LocalAdapter> Adapters { get; } = [];
    public ObservableCollection<PortInfo> Ports { get; } = [];
    public ObservableCollection<VlanInfo> Vlans { get; } = [];
    public ObservableCollection<TdrPair> TdrPairs { get; } = [];
    public ObservableCollection<AuditEntry> Journal { get; } = [];
    public ICollectionView PortsView { get; }
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(IsIdle)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsIdle => !IsBusy;
    public bool DryRun { get => dryRun; set { Set(ref dryRun, value); Raise(nameof(ModeLabel)); } }
    public string ModeLabel => DryRun ? "Simulation activée" : "Modifications réelles activées";
    public bool Connected => driver?.IsConnected == true;
    public bool IsDemo => driver?.IsDemo == true;
    private SwitchVendor ActiveVendor => driver?.Vendor ?? SwitchVendor.Cisco;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public string Model { get => model; private set => Set(ref model, value); }
    public string ResultSwitch { get => resultSwitch; private set => Set(ref resultSwitch, value); }
    public string ResultPort { get => resultPort; private set => Set(ref resultPort, value); }
    public string ResultVlan { get => resultVlan; private set => Set(ref resultVlan, value); }
    public string ResultSpeed { get => resultSpeed; private set => Set(ref resultSpeed, value); }
    public string ResultDuplex { get => resultDuplex; private set => Set(ref resultDuplex, value); }
    public string DetectionNote { get => detectionNote; private set => Set(ref detectionNote, value); }
    public string Source { get => source; private set => Set(ref source, value); }
    public string PassiveNote { get => passiveNote; private set => Set(ref passiveNote, value); }
    public string Counters { get => counters; private set => Set(ref counters, value); }
    public string TdrNote { get => tdrNote; private set => Set(ref tdrNote, value); }
    public string CaptureNote { get => captureNote; private set => Set(ref captureNote, value); }
    public string Search { get => search; set { Set(ref search, value); PortsView.Refresh(); } }
    public int Tab { get => tab; set => Set(ref tab, value); }
    public string VlanId { get => vlanId; set => Set(ref vlanId, value); }
    public string VlanName { get => vlanName; set => Set(ref vlanName, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string NativeVlan { get => nativeVlan; set => Set(ref nativeVlan, value); }
    public string AllowedVlans { get => allowedVlans; set => Set(ref allowedVlans, value); }
    public LocalAdapter? Adapter { get => adapter; set => SelectAdapter(value); }
    public PortInfo? SelectedPort
    {
        get => selectedPort;
        set { if (Set(ref selectedPort, value)) { Description = value?.Description ?? ""; if (int.TryParse(value?.Vlan, out _)) VlanId = value!.Vlan; TdrPairs.Clear(); TdrNote = "Aucun test pour ce port."; Counters = "CRC —   ·   Collisions —   ·   Erreurs entrantes —"; PassiveNote = value is null ? "Sélectionnez un port." : SafetyPolicy.SpeedAssessment(value); Raise(nameof(SelectedPortLabel)); } }
    }
    public string SelectedPortLabel => SelectedPort is null ? "Sélectionnez un port" : $"{SelectedPort.Name} · {SelectedPort.StateLabel}";
    public VlanInfo? SelectedVlan { get => selectedVlan; set { if (Set(ref selectedVlan, value) && value != null) { VlanId = value.Id.ToString(); VlanName = value.Name; } } }
    public string PortCount => $"{Ports.Count(p => p.IsUp)} actifs / {Ports.Count} ports";
    public DependenciesViewModel Dependencies { get; } = new();
    public UpdateViewModel Updates { get; } = new();
    public ICommand DependenciesCommand { get; }
    public ICommand UpdatesCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DemoCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DetectCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand RefreshAdaptersCommand { get; }
    public ICommand ViewPortCommand { get; }
    public ICommand AccessCommand { get; }
    public ICommand TrunkCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand DescriptionCommand { get; }
    public ICommand CreateVlanCommand { get; }
    public ICommand DeleteVlanCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenBackupCommand { get; }
    public ICommand PassiveCommand { get; }
    public ICommand TdrCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AboutCommand { get; }

    public MainViewModel()
    {
        audit = new(Path.Combine(store.DirectoryPath, "Logs"));
        audit.Added += item => dispatcher.Invoke(() => { Journal.Insert(0, item); if (Journal.Count > 500) Journal.RemoveAt(500); });
        PortsView = CollectionViewSource.GetDefaultView(Ports);
        PortsView.Filter = item => item is PortInfo p && (p.Name + " " + p.Description + " " + p.Vlan + " " + p.StateLabel).Contains(Search, StringComparison.OrdinalIgnoreCase);
        DependenciesCommand = new RelayCommand(() =>
        {
            var existing = Application.Current.Windows.OfType<DependenciesWindow>().FirstOrDefault();
            if (existing != null) existing.Activate(); else new DependenciesWindow(Dependencies).Show();
        });
        UpdatesCommand = new RelayCommand(() =>
        {
            var existing = Application.Current.Windows.OfType<UpdateWindow>().FirstOrDefault();
            if (existing != null) existing.Activate(); else new UpdateWindow(Updates).Show();
        });
        Updates.SkipRequested += tag => { try { store.SaveSkippedUpdate(tag); } catch { /* A refusal that cannot be saved is not fatal. */ } };
        Updates.RestartRequested += () => Application.Current.Shutdown(0);
        ConnectCommand = new RelayCommand(Connect, () => !IsBusy);
        DemoCommand = new RelayCommand(() => Run("Démonstration", StartDemo), () => !IsBusy);
        DisconnectCommand = new RelayCommand(() => Run("Déconnexion", async _ => await Disconnect()), () => !IsBusy && driver != null);
        RefreshCommand = new RelayCommand(() => Run("Actualisation", Refresh), CanRead);
        DetectCommand = new RelayCommand(() => Run("Détection du port", Detect), () => CanRead() && Adapter != null);
        CaptureCommand = new RelayCommand(() => Run("Écoute LLDP/CDP", Capture), () => !IsBusy && Adapter != null && !IsDemo);
        RefreshAdaptersCommand = new RelayCommand(RefreshAdapters, () => !IsBusy && !IsDemo);
        ViewPortCommand = new RelayCommand(() => { SelectedPort = Ports.FirstOrDefault(p => p.Name == ResultPort); Tab = 1; }, () => Ports.Any(p => p.Name == ResultPort));
        AccessCommand = Change(() => CommandPlan.Access(SelectedPort!.Name, ParseVlan()), CanEditPort);
        TrunkCommand = Change(() => CommandPlan.Trunk(SelectedPort!.Name, int.Parse(NativeVlan), AllowedVlans, ActiveVendor), CanEditPort);
        EnableCommand = Change(() => CommandPlan.Enabled(SelectedPort!.Name, true), CanEditPort);
        DisableCommand = Change(() => CommandPlan.Enabled(SelectedPort!.Name, false), CanEditPort);
        DescriptionCommand = Change(() => CommandPlan.Describe(SelectedPort!.Name, Description, ActiveVendor), CanEditPort);
        CreateVlanCommand = Change(() => CommandPlan.CreateVlan(ParseVlan(), VlanName), CanRead);
        DeleteVlanCommand = Change(() => CommandPlan.DeleteVlan(SelectedVlan!.Id), () => CanRead() && SelectedVlan != null);
        SaveCommand = Change(CommandPlan.Save, CanRead);
        ExportCommand = new RelayCommand(() => Run("Export chiffré", Export), CanRead);
        OpenBackupCommand = new RelayCommand(OpenBackup, () => !IsBusy);
        PassiveCommand = new RelayCommand(() => Run("Contrôle passif", Passive), CanEditPort);
        TdrCommand = new RelayCommand(() => Run("Test de câble TDR", Tdr), CanEditPort);
        CancelCommand = new RelayCommand(() => operation?.Cancel(), () => IsBusy);
        AboutCommand = new RelayCommand(() =>
        {
            var assembly = typeof(MainViewModel).Assembly;
            var text = new System.Text.StringBuilder($"Switch Pilot · {assembly.GetName().Version?.ToString(3)}\nGestion de switchs Cisco IOS\n\n");
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("SwitchPilot.Notices") || n.StartsWith("SwitchPilot.Licenses")))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                text.AppendLine(resource).AppendLine(reader.ReadToEnd()).AppendLine();
            }
            Dialogs.ShowText("À propos · Licences", text.ToString());
        }, () => !IsBusy);
        try { store.Load(); } catch { Status = "Paramètres chiffrés illisibles pour ce compte Windows. Le fichier est conservé ; connectez-vous manuellement."; }
        detectionSchedule.Interval = TimeSpan.FromSeconds(store.Settings.DetectionIntervalSeconds);
        InitializeHistory(); InitializeExtras();
        RefreshAdapters();
        monitor.Changed += ObservationsChanged;
        Dependencies.Changed += DependencyChanged;
        timer.Tick += Tick; timer.Start();
    }
    public async Task InitializeAsync()
    {
        monitor.Start();
        await Dependencies.CheckAsync();
        if (Dependencies.Status.OfferInstall) DependenciesCommand.Execute(null);
        await Updates.CheckAsync(manual: false);
        if (Updates.Available && Updates.Release is { } candidate && !string.Equals(candidate.Tag, store.Settings.SkippedUpdateTag, StringComparison.OrdinalIgnoreCase))
            UpdatesCommand.Execute(null);
    }
    private bool CanRead() => !IsBusy && Connected;
    private bool CanEditPort() => CanRead() && SelectedPort != null;
    private int ParseVlan() => int.TryParse(VlanId, out var value) ? value : throw new ArgumentException("Numéro de VLAN invalide.");
    private ICommand Change(Func<CommandPlan> build, Func<bool> can) => new RelayCommand(() => Run("Configuration", async ct =>
    {
        var plan = build();
        if (!Dialogs.Preview(plan, DryRun)) return;
        var safety = DryRun || plan.Kind is not (ChangeKind.DisablePort or ChangeKind.AccessVlan or ChangeKind.Trunk) ? SafetyContext.Unknown : await Safety(ct);
        await driver!.ApplyAsync(plan, DryRun, ct, safety);
        if (!DryRun) { await Refresh(ct); ClearDetection(); }
        Status = DryRun ? "Simulation terminée : aucune modification envoyée." : "Commandes appliquées ; état relu. Consultez les ports et VLAN pour vérifier le résultat.";
    }), can);
    private async void Run(string action, Func<CancellationToken, Task> task)
    {
        if (IsBusy || disposed) return;
        IsBusy = true; Status = action + "…";
        operation = new CancellationTokenSource();
        try { await task(operation.Token); }
        catch (OperationCanceledException) { Status = "Opération annulée. Si des commandes avaient été envoyées, vérifiez l'état du switch."; audit.Write(action, "Annulée ; état à vérifier."); }
        catch (Exception e)
        {
            Status = FriendlyError(e); audit.Write(action, "Échec : " + e.GetType().Name);
            if (action.Contains("TDR")) TdrNote = Status;
            if (action.Contains("TDR") && e is NotSupportedException && Connected && SelectedPort != null)
            {
                try { await Passive(operation.Token); Status = TdrNote + " Contrôle passif effectué."; }
                catch { Status = TdrNote + " Le contrôle passif n'a pas pu aboutir."; }
            }
            if (action.Contains("Détection")) ClearDetection();
        }
        finally
        {
            operation.Dispose(); operation = null; IsBusy = false;
            if (driver != null && !Connected) { Connection = "Session déconnectée"; ClearDetection(); }
            Raise(nameof(Connected));
            if (audit.PersistenceError != null) Status += " " + audit.PersistenceError;
        }
    }
    private static string FriendlyError(Exception e) => e switch
    {
        Renci.SshNet.Common.SshAuthenticationException => "Authentification SSH refusée. Vérifiez les identifiants et les droits IOS." + Detail(e),
        Renci.SshNet.Common.SshConnectionException => "Connexion SSH refusée ou négociation incompatible. Vérifiez la clé du switch ; pour un ancien IOS, acceptez la proposition de compatibilité." + Detail(e),
        Renci.SshNet.Common.SshException => "Connexion SSH impossible." + Detail(e),
        System.Net.Sockets.SocketException => "Switch inaccessible. Vérifiez l'adresse, le routage et le port SSH.",
        _ when e is ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException or FormatException or CliException or IOException => e.Message,
        _ => $"L'opération a échoué ({e.GetType().Name}). Vérifiez la connexion et le journal."
    };
    private static string Detail(Exception e) => string.IsNullOrWhiteSpace(e.Message) ? "" : " Détail : " + e.Message.Trim();
    private void Connect()
    {
        var dialog = new ConnectionWindow(store.Settings.Profiles);
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        var selected = dialog.Profile; var legacy = dialog.LegacyAlgorithms;
        Run(selected.Kind == ConnectionKind.Serial ? "Connexion console" : "Connexion SSH", async ct =>
        {
            await Disconnect(); monitor.EnableCapture(Dependencies.Available);
            ICliSession session;
            try { session = selected.Kind == ConnectionKind.Serial
                ? await Task.Run(() => SerialSession.ConnectAsync(selected, ct), ct)
                : await Task.Run(() => SshSession.ConnectAsync(selected, Trust, legacy, ct), ct); }
            catch (Exception e) when (!legacy && SshSession.IsNegotiationFailure(e))
            {
                if (!Dialogs.Confirm("La négociation SSH moderne a échoué. Réessayer avec les algorithmes hérités pour un ancien IOS ? La clé du switch restera vérifiée.", "Compatibilité ancien IOS")) return;
                legacy = true;
                session = await Task.Run(() => SshSession.ConnectAsync(selected, Trust, true, ct), ct);
            }
            var detected = (SwitchVendor?)null;
            if (selected.AutoDetectVendor)
            {
                try { detected = SwitchVendorDetector.Detect(await session.ExecuteAsync("show version", ct)); }
                catch (CliException) { /* Inconclusive: try both families below. */ }
            }
            // Auto-detect: trust a conclusive banner, otherwise probe both command sets in turn
            // rather than defaulting to Cisco, so a wrong family cannot fail on both transports.
            var order = !selected.AutoDetectVendor ? new[] { selected.Vendor }
                : detected is { } only ? new[] { only }
                : new[] { SwitchVendor.Cisco, SwitchVendor.AlliedTelesis };
            profile = selected with { Password = "", EnablePassword = "" };
            store.SaveProfile(selected);
            selected = selected with { Password = "", EnablePassword = "" };
            var vendor = order[0];
            Exception? readError = null;
            foreach (var candidate in order)
            {
                vendor = candidate;
                driver = CreateDriver(session, candidate);
                try { await Refresh(ct); await Detect(ct); readError = null; break; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    readError = e;
                    if (!session.IsConnected || order.Length == 1) break;
                    audit.Write("Détection du constructeur", $"Lecture impossible avec {(candidate == SwitchVendor.AlliedTelesis ? "Allied Telesis" : "Cisco IOS")} : {e.GetType().Name}.");
                }
            }
            audit.Write(selected.Kind == ConnectionKind.Serial ? "Connexion console locale" : "Connexion SSH", (legacy ? "Connecté avec compatibilité ancien IOS" : "Connecté") + $" · {(vendor == SwitchVendor.AlliedTelesis ? "Allied Telesis" : "Cisco IOS")}.");
            Raise(nameof(IsDemo)); Raise(nameof(Connected));
            if (readError is not null)
            {
                // A read failure must not look like a login failure: keep the session open and
                // state exactly what could not be read so the user can retry or adjust.
                Model = vendor == SwitchVendor.AlliedTelesis ? "Allied Telesis · AlliedWare Plus" : "Cisco IOS";
                Connection = session.Hostname + " · connecté";
                Status = "Connecté, mais la lecture de l'état a échoué : " + FriendlyError(readError);
            }
        });
    }
    private ISwitchDriver CreateDriver(ICliSession session, SwitchVendor vendor) => vendor == SwitchVendor.AlliedTelesis
        ? new SwitchPilot.Infrastructure.Allied.AlliedTelesisDriver(session, audit, backup: new ConfigurationBackup(store.DirectoryPath))
        : new CiscoIosDriver(session, audit, backup: new ConfigurationBackup(store.DirectoryPath));
    private bool Trust(string host, string fingerprint) => dispatcher.Invoke(() =>
    {
        var known = store.KnownFingerprint(host);
        if (known == fingerprint) return true;
        var text = known == null ? $"Première connexion à {host}.\n\nEmpreinte SHA-256 :\n{fingerprint}\n\nVérifiez cette empreinte auprès de l'administrateur avant de l'accepter." :
            $"La clé SSH de {host} a changé.\n\nAncienne : {known}\nNouvelle : {fingerprint}\n\nN'acceptez que si ce changement a été vérifié.";
        if (!Dialogs.Confirm(text, "Vérification de la clé SSH")) return false;
        store.Trust(host, fingerprint); return true;
    });
    private async Task StartDemo(CancellationToken ct)
    {
        await Disconnect(); monitor.EnableCapture(false); driver = new DemoSwitchDriver(audit);
        Adapters.Clear(); Adapters.Add(new("demo", "Ethernet de démonstration", "001122334455", true, 100000000, "192.168.10.42")); Adapter = Adapters[0];
        Raise(nameof(IsDemo)); Raise(nameof(Connected)); await Refresh(ct); await Detect(ct);
        Status = "Démonstration active : données fictives, aucune connexion réseau.";
    }
    private async Task Disconnect()
    {
        if (driver != null) await driver.DisposeAsync();
        switchMeasurement = previousSwitchMeasurement = null; measuredSwitch = ""; measuredAdapter = ""; measuredGeneration = -1;
        driver = null; profile = null; snapshot = null; Ports.Clear(); Vlans.Clear(); TdrPairs.Clear(); SelectedPort = null; SelectedVlan = null;
        monitor.EnableCapture(Dependencies.Available);
        Connection = "Aucun switch connecté"; Model = "SSH · Cisco IOS ou Allied Telesis"; ClearDetection(); RefreshAdapters();
        Raise(nameof(IsDemo)); Raise(nameof(Connected)); Raise(nameof(PortCount)); Status = "Déconnecté.";
    }
    private async Task Refresh(CancellationToken ct)
    {
        var selected = SelectedPort?.Name;
        snapshot = await driver!.ReadSnapshotAsync(ct);
        Ports.Clear(); foreach (var p in snapshot.Ports) Ports.Add(p);
        Vlans.Clear(); foreach (var v in snapshot.Vlans) Vlans.Add(v);
        SelectedPort = Ports.FirstOrDefault(p => p.Name == selected);
        Connection = snapshot.Identity.Name + (IsDemo ? " · Démonstration" : driver.Kind == ConnectionKind.Serial ? " · Connexion console locale" : " · Connecté en SSH");
        Model = snapshot.Identity.Model + " · IOS " + snapshot.Identity.IosVersion;
        Raise(nameof(PortCount)); Status = "État des ports et VLAN actualisé.";
    }
    private async Task Passive(CancellationToken ct)
    {
        var port = SelectedPort!; var reading = await driver!.ReadCountersAsync(port.Name, ct);
        Counters = $"Lien {reading.LinkState} · {reading.Speed} Mb/s / {reading.Duplex}\nCRC {reading.Crc?.ToString() ?? "—"}   ·   Collisions {reading.Collisions?.ToString() ?? "—"}   ·   Erreurs entrantes {reading.InputErrors?.ToString() ?? "—"}";
        var measured = port with { Speed = reading.Speed, Duplex = reading.Duplex, Status = reading.LinkState == "Actif" ? "connected" : "notconnect" };
        PassiveNote = (reading.LinkState == "Inconnu" ? "État du lien non reconnu." : SafetyPolicy.SpeedAssessment(measured)) + " Les compteurs sont cumulatifs depuis leur dernière remise à zéro.";
        if (reading.Crc > 0 || reading.Collisions > 0) PassiveNote += " Des erreurs sont présentes : contrôlez leur progression et les deux extrémités.";
        Status = $"Contrôle passif de {port.Name} terminé."; audit.Write("Contrôle passif " + port.Name, "Compteurs lus.");
        if (!IsDemo) SaveDiagnostic(new(DateTimeOffset.UtcNow, snapshot!.Identity.Name, port.Name, "À vérifier", Counters + "\n" + PassiveNote + "\nRelevé unique : progression des erreurs non mesurée.", Remote: new(port.Name, DateTimeOffset.UtcNow, reading)));
    }
    private async Task<SafetyContext> Safety(CancellationToken ct)
    {
        if (driver?.Kind == ConnectionKind.Serial) return new(true, true, new HashSet<string>());
        if (IsDemo) return new(true, true, new HashSet<string> { "Fa0/14", "Gi0/1" });
        var selected = Adapter; var generation = CurrentGeneration; var observedAt = DateTimeOffset.UtcNow;
        if (selected == null || profile == null || !await NetworkDiscovery.RoutesViaAsync(profile.Host, selected, ct)) return SafetyContext.Unknown;
        var fresh = await driver!.ReadSnapshotAsync(ct); var entries = await driver.ReadMacTableAsync(ct);
        bool StillCurrent() => !disposed && Adapter?.Id == selected.Id && Adapter.IsUp && CurrentGeneration == generation;
        if (!StillCurrent()) return SafetyContext.Unknown;
        var candidates = PortLocator.Find(selected.Mac, fresh, entries);
        var localMacs = NetworkDiscovery.Adapters().Select(a => a.Mac).ToHashSet();
        var protectedPorts = entries.Where(e => localMacs.Contains(e.Mac)).Select(e => e.Port).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var port in fresh.Ports.Where(p => p.IsTrunk || p.Name.StartsWith("Po"))) protectedPorts.Add(port.Name);
        return new(true, candidates.Count == 1 && candidates[0].DirectCandidate, protectedPorts) { ObservedAt = observedAt, StillCurrent = StillCurrent };
    }
    private async Task Tdr(CancellationToken ct)
    {
        var port = SelectedPort!;
        var safety = await Safety(ct); SafetyPolicy.RequireSafeTdr(port, safety, driver!.Kind);
        var commands = $"test cable-diagnostics tdr interface {port.Name}\nshow cable-diagnostics tdr interface {port.Name}";
        var warning = DryRun ? "Simulation : les commandes seront affichées et aucune ne sera envoyée." :
            driver!.Kind == ConnectionKind.Serial ? "Ce test coupera le lien Ethernet du poste quelques secondes si vous testez son port. La console locale restera disponible." : "Ce test peut couper brièvement le lien du port. Le port du poste est protégé en SSH. Confirmez l’interruption du port sélectionné.";
        if (!Dialogs.PreviewText($"Test TDR · {port.Name}", commands, warning, DryRun ? "Simuler le test" : "Lancer le test")) return;
        TdrPairs.Clear();
        if (DryRun) { TdrNote = "Simulation : aucun test lancé."; Status = TdrNote; audit.Write("TDR " + port.Name, TdrNote); return; }
        // Revalidate after the confirmation: the dialog may have remained open for a long time.
        safety = await Safety(ct);
        expectedInterruptionUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        TdrResult result;
        try { result = await driver!.RunTdrAsync(port.Name, safety, ct); }
        finally { expectedInterruptionUntil = DateTimeOffset.UtcNow.AddSeconds(15); if (!IsDemo) { monitor.Redetect(); ClearDetection(); detectionSchedule.Request(); } }
        foreach (var pair in result.Pairs) TdrPairs.Add(pair);
        TdrNote = result.Note; Status = "Résultats TDR reçus."; RecordTdr(snapshot!.Identity.Name, result);
    }
    private async Task Export(CancellationToken ct)
    {
        var dialog = new SaveFileDialog { Filter = "Sauvegarde chiffrée Switch Pilot|*.spbackup", FileName = $"SwitchPilot-{DateTime.Now:yyyyMMdd-HHmm}.spbackup" };
        if (dialog.ShowDialog() != true) return;
        var config = await driver!.ExportAsync(ct);
        UserStore.ExportEncrypted(dialog.FileName, config);
        audit.Write("Export de configuration", "Sauvegarde complète chiffrée avec DPAPI.");
        Status = "Sauvegarde chiffrée créée. Lecture avec ce compte Windows via « Lire une sauvegarde ».";
    }
    private void OpenBackup()
    {
        var dialog = new OpenFileDialog { Filter = "Sauvegarde chiffrée Switch Pilot|*.spbackup" };
        if (dialog.ShowDialog() != true) return;
        try { Dialogs.ShowText("Configuration sauvegardée · lecture en mémoire", UserStore.ReadEncryptedExport(dialog.FileName)); }
        catch { Status = "Impossible de déchiffrer ce fichier avec ce compte Windows, ou fichier corrompu."; }
    }
    private void RefreshAdapters()
    {
        var selected = Adapter?.Id;
        Adapters.Clear(); foreach (var a in NetworkDiscovery.Adapters()) Adapters.Add(a);
        Adapter = Adapters.FirstOrDefault(a => a.Id == selected) ?? Adapters.FirstOrDefault();
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true; timer.Stop(); operation?.Cancel(); lifetime.Cancel();
        Dependencies.Changed -= DependencyChanged; monitor.Changed -= ObservationsChanged;
        await monitor.DisposeAsync(); notifications.Dispose();
        if (driver != null) await driver.DisposeAsync();
    }
}
