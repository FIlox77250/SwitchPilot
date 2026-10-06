using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwitchPilot.Core;
using SwitchPilot.Core.Platforms;
using SwitchPilot.Infrastructure.Serial;

namespace SwitchPilot.App.Views;
public partial class ConnectionWindow : Window
{
    private const int SshMode = 0, SerialMode = 1, ApiMode = 2;
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool scanning, closed;
    private string desiredPort = "";
    // Dropdown order: detection, then the platform catalog (Cisco, AlliedWare Plus, AT-S95 first, as before).
    private static int VendorToIndex(ConnectionProfile p)
    {
        if (p.AutoDetectVendor) return 0;
        for (var i = 0; i < SwitchPlatforms.All.Count; i++) if (SwitchPlatforms.All[i].Vendor == p.Vendor) return i + 1;
        return 0;
    }
    private (SwitchVendor SelectedVendor, bool AutoDetect) VendorFromIndex() =>
        Vendor.SelectedIndex is var index && index >= 1 && index <= SwitchPlatforms.All.Count
            ? (SwitchPlatforms.All[index - 1].Vendor, false)
            : (SwitchVendor.Cisco, true);
    public ConnectionProfile? Profile { get; private set; }
    public bool LegacyAlgorithms => Legacy.IsChecked == true;
    public ConnectionWindow(IReadOnlyList<ConnectionProfile> profiles)
    {
        InitializeComponent(); Owner = Application.Current.MainWindow;
        Services.ScreenBounds.CapDialog(this);
        foreach (var platform in SwitchPlatforms.All)
            Vendor.Items.Add(new ComboBoxItem { Content = platform.DisplayName + (platform.IsExperimental ? " · expérimental" : ""), ToolTip = platform.Notes });
        BaudRates.ItemsSource = ConnectionProfile.SerialBaudRates; BaudRates.SelectedItem = 9600;
        Profiles.ItemsSource = profiles; if (profiles.Count > 0) Profiles.SelectedIndex = 0;
        deviceTimer.Tick += async (_, _) => await RefreshPorts();
        Loaded += async (_, _) => { await RefreshPorts(); deviceTimer.Start(); };
        Closed += (_, _) => { closed = true; deviceTimer.Stop(); Password.Clear(); EnablePassword.Clear(); };
    }
    private async Task RefreshPorts()
    {
        if (scanning || closed) return; scanning = true;
        try
        {
            var devices = await Task.Run(SerialDeviceCatalog.Scan);
            if (closed) return;
            var selected = (SerialPorts.SelectedItem as SerialDevice)?.Port ?? desiredPort;
            SerialPorts.ItemsSource = devices.Where(d => d.Available).ToArray();
            SerialPorts.SelectedItem = devices.FirstOrDefault(d => d.Available && d.Port == selected) ?? devices.FirstOrDefault(d => d.Available);
            DriverGuidance.Text = string.Join("\n", devices.Where(d => !d.Available).Select(d => d.Label + " : " + d.Guidance));
        }
        catch { DriverGuidance.Text = "Énumération série indisponible. Vérifiez les périphériques Windows."; }
        finally { scanning = false; }
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SshFields is null) return;
        var serial = Mode.SelectedIndex == SerialMode; var api = Mode.SelectedIndex == ApiMode;
        SshFields.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        Legacy.Visibility = LegacyNote.Visibility = serial || api ? Visibility.Collapsed : Visibility.Visible;
        SerialFields.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        // The controller API is UniFi only: no vendor choice, no enable password, HTTPS port.
        ApiFields.Visibility = ApiNote.Visibility = api ? Visibility.Visible : Visibility.Collapsed;
        VendorFields.Visibility = EnableFields.Visibility = api ? Visibility.Collapsed : Visibility.Visible;
        HostLabel.Text = api ? "Adresse du contrôleur" : "Adresse IP ou nom DNS";
        if (api && Port.Text.Trim() == "22") Port.Text = "443";
        else if (!api && Port.Text.Trim() is "443" or "8443") Port.Text = "22";
    }
    private void VendorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlatformNote is null) return;
        var platform = Vendor.SelectedIndex >= 1 && Vendor.SelectedIndex <= SwitchPlatforms.All.Count ? SwitchPlatforms.All[Vendor.SelectedIndex - 1] : null;
        PlatformNote.Text = platform is null ? "" : platform.Notes + (platform.IsExperimental
            ? " Pilote expérimental : construit d'après la documentation du constructeur, chaque modification est présentée avant envoi."
            : "");
        if (platform?.Vendor == SwitchVendor.UniFi) PlatformNote.Text += " En SSH, le switch UniFi est en lecture seule : choisissez le mode « API contrôleur UniFi » pour modifier.";
        PlatformNote.Visibility = PlatformNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not ConnectionProfile p || Host is null) return;
        Mode.SelectedIndex = p.Kind switch { ConnectionKind.Serial => SerialMode, ConnectionKind.Api => ApiMode, _ => SshMode };
        Host.Text = p.Host; Port.Text = p.Port.ToString(); Username.Text = p.Username;
        Password.Password = p.Password; EnablePassword.Password = p.EnablePassword; Remember.IsChecked = p.Remember;
        Vendor.SelectedIndex = VendorToIndex(p); Site.Text = p.Site; Device.Text = p.Device;
        desiredPort = p.SerialPort;
        BaudRates.SelectedItem = p.BaudRate; AutoBaud.IsChecked = p.AutoBaud;
        if (SerialPorts.ItemsSource is IEnumerable<SerialDevice> devices) SerialPorts.SelectedItem = devices.FirstOrDefault(d => d.Port == desiredPort);
    }
    private void Connect(object sender, RoutedEventArgs e)
    {
        var kind = Mode.SelectedIndex switch { SerialMode => ConnectionKind.Serial, ApiMode => ConnectionKind.Api, _ => ConnectionKind.Ssh };
        var vendor = kind == ConnectionKind.Api ? (SelectedVendor: SwitchVendor.UniFi, AutoDetect: false) : VendorFromIndex();
        var profile = new ConnectionProfile(Host.Text.Trim(), int.TryParse(Port.Text, out var port) ? port : 0, Username.Text.Trim(), Remember.IsChecked == true, Password.Password,
            kind == ConnectionKind.Api ? "" : EnablePassword.Password)
        {
            Kind = kind, Vendor = vendor.SelectedVendor, AutoDetectVendor = vendor.AutoDetect,
            SerialPort = (SerialPorts.SelectedItem as SerialDevice)?.Port ?? "", BaudRate = BaudRates.SelectedItem is int baud ? baud : 9600, AutoBaud = AutoBaud.IsChecked == true,
            Site = kind == ConnectionKind.Api ? Site.Text.Trim() : "default", Device = kind == ConnectionKind.Api ? Device.Text.Trim() : ""
        };
        try { profile.Validate(); } catch (ArgumentException ex) { Error.Text = ex.Message; return; }
        Profile = profile; Password.Clear(); EnablePassword.Clear(); DialogResult = true;
    }
}
