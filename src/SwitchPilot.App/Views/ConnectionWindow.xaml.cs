using System.Windows;
using System.Windows.Controls;
using SwitchPilot.Core;

namespace SwitchPilot.App.Views;

public partial class ConnectionWindow : Window
{
    public ConnectionProfile? Profile { get; private set; }
    public bool LegacyAlgorithms => Legacy.IsChecked == true;
    public ConnectionWindow(IReadOnlyList<ConnectionProfile> profiles)
    {
        InitializeComponent(); Owner = Application.Current.MainWindow; Profiles.ItemsSource = profiles;
        if (profiles.Count > 0) Profiles.SelectedIndex = 0;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not ConnectionProfile p || Host is null) return;
        Host.Text = p.Host; Port.Text = p.Port.ToString(); Username.Text = p.Username;
        Password.Password = p.Password; EnablePassword.Password = p.EnablePassword; Remember.IsChecked = p.Remember;
    }
    private void Connect(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Host.Text) || string.IsNullOrWhiteSpace(Username.Text) || !int.TryParse(Port.Text, out var port) || port is < 1 or > 65535)
        { Error.Text = "Renseignez une adresse, un utilisateur et un port valide."; return; }
        Profile = new(Host.Text.Trim(), port, Username.Text.Trim(), Remember.IsChecked == true, Password.Password, EnablePassword.Password);
        Password.Clear(); EnablePassword.Clear(); DialogResult = true;
    }
}
