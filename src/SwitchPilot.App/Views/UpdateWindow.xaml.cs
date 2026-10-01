using System.Windows;
using SwitchPilot.App.ViewModels;

namespace SwitchPilot.App.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateViewModel model;
    private bool restarting;
    public UpdateWindow(UpdateViewModel model, bool refreshOnLoad = true)
    {
        InitializeComponent();
        this.model = model;
        DataContext = model;
        Owner = Application.Current.MainWindow;
        if (SystemParameters.WorkArea.Height > 0) MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        model.RestartRequested += () => restarting = true;
        Closing += (_, e) => { if (model.Busy && !restarting) e.Cancel = true; };
        Loaded += async (_, _) => { if (refreshOnLoad) await model.CheckAsync(manual: true); };
    }
    private void LaterClick(object sender, RoutedEventArgs e) => Close();
    private void SkipClick(object sender, RoutedEventArgs e) => Close();
}
