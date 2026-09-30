using System.Windows;
using SwitchPilot.App.ViewModels;
namespace SwitchPilot.App.Views;
public partial class InventoryWindow : Window
{
    public InventoryWindow(MainViewModel model) { InitializeComponent(); DataContext = model; Owner = Application.Current.MainWindow; }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
