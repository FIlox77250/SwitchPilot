using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Serial;

namespace SwitchPilot.App.Views;
public partial class ConnectionWindow : Window
{
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool scanning, closed;
    private string desiredPort = "";
    public ConnectionProfile? Profile { get; private set; }
    public bool LegacyAlgorithms => Legacy.IsChecked == true;
    public ConnectionWindow(IReadOnlyList<ConnectionProfile> profiles)
    {
        InitializeComponent(); Owner = Application.Current.MainWindow;
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
        var serial = Mode.SelectedIndex == 1;
        SshFields.Visibility = Legacy.Visibility = LegacyNote.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        SerialFields.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not ConnectionProfile p || Host is null) return;
        Host.Text = p.Host; Port.Text = p.Port.ToString(); Username.Text = p.Username;
        Password.Password = p.Password; EnablePassword.Password = p.EnablePassword; Remember.IsChecked = p.Remember;
        Mode.SelectedIndex = p.Kind == ConnectionKind.Serial ? 1 : 0; desiredPort = p.SerialPort;
        BaudRates.SelectedItem = p.BaudRate; AutoBaud.IsChecked = p.AutoBaud;
        if (SerialPorts.ItemsSource is IEnumerable<SerialDevice> devices) SerialPorts.SelectedItem = devices.FirstOrDefault(d => d.Port == desiredPort);
    }
    private void Connect(object sender, RoutedEventArgs e)
    {
        var serial = Mode.SelectedIndex == 1;
        var profile = new ConnectionProfile(Host.Text.Trim(), int.TryParse(Port.Text, out var port) ? port : 0, Username.Text.Trim(), Remember.IsChecked == true, Password.Password, EnablePassword.Password)
        { Kind = serial ? ConnectionKind.Serial : ConnectionKind.Ssh, SerialPort = (SerialPorts.SelectedItem as SerialDevice)?.Port ?? "", BaudRate = BaudRates.SelectedItem is int baud ? baud : 9600, AutoBaud = AutoBaud.IsChecked == true };
        try { profile.Validate(); } catch (ArgumentException ex) { Error.Text = ex.Message; return; }
        Profile = profile; Password.Clear(); EnablePassword.Clear(); DialogResult = true;
    }
}
