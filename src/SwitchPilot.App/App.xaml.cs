using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwitchPilot.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Lancez Switch Pilot avec un compte standard, sans « Exécuter en tant qu’administrateur ». Seul l’installateur Npcap peut demander l’élévation.", "Switch Pilot");
            Shutdown(1); return;
        }
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
