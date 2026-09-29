using System.Windows;
using SwitchPilot.App.ViewModels;

namespace SwitchPilot.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    public MainWindow()
    {
        InitializeComponent(); model = new(); DataContext = model;
        Closed += async (_, _) => await model.DisposeAsync();
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--smoke-test")) await Services.SmokeTest.RunAsync(this, model);
            else if (Environment.GetCommandLineArgs().Contains("--demo")) model.DemoCommand.Execute(null);
        };
    }
}
