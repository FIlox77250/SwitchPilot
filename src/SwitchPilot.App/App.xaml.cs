using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwitchPilot.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // CI/Wine may not provide Segoe UI. This override is limited to the explicit smoke test.
        if (e.Args.Contains("--smoke-test") && Environment.GetEnvironmentVariable("SWITCHPILOT_TEST_FONT") is { Length: > 0 } font)
        {
            var style = new Style(typeof(Window), (Style)Resources[typeof(Window)]);
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily(font)));
            Resources[typeof(Window)] = style;
            Resources["CodeFont"] = new FontFamily(font);
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Une erreur inattendue est survenue. L'opération peut être partielle : reconnectez-vous et vérifiez l'état du switch.\n\n" + args.Exception.GetType().Name, "Switch Pilot", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
