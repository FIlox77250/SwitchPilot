using System.Windows;
using SwitchPilot.App.ViewModels;
namespace SwitchPilot.App.Views;
public partial class DependenciesWindow : Window
{
    public DependenciesWindow(DependenciesViewModel model)
    {
        InitializeComponent(); DataContext = model; Owner = Application.Current.MainWindow;
        Services.ScreenBounds.CapDialog(this);
        Closing += (_, e) => { if (model.Busy) { e.Cancel = true; model.CancelCommand.Execute(null); } };
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
