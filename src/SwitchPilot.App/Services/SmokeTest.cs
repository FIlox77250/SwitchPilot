using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchPilot.App.ViewModels;
using SwitchPilot.App.Views;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Storage;

namespace SwitchPilot.App.Services;

// Invoked only with --smoke-test. Runs without a switch or credentials and exits.
internal static class SmokeTest
{
    public static async Task RunAsync(Window window, MainViewModel model)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPilot", "SmokeTest");
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "result.txt"));
        File.WriteAllText(Path.Combine(directory, "progress.txt"), "Starting smoke test\n");
        try
        {
            var store = new UserStore(Path.Combine(directory, "StoreTest"));
            store.Load();
            store.SaveProfile(new ConnectionProfile("test-switch", 22, "test-user", true, "test-secret-never-clear", "test-enable"));
            var loaded = new UserStore(store.DirectoryPath); loaded.Load();
            if (loaded.Settings.Profiles[0].Password != "test-secret-never-clear" ||
                System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "settings.dpapi"))).Contains("test-secret-never-clear"))
                throw new InvalidOperationException("Encrypted profile roundtrip failed.");
            var backup = Path.Combine(directory, "test.spbackup");
            UserStore.ExportEncrypted(backup, "hostname TEST-SMOKE");
            if (UserStore.ReadEncryptedExport(backup) != "hostname TEST-SMOKE") throw new InvalidOperationException("Encrypted backup roundtrip failed.");
            store.SaveProfile(store.Settings.Profiles[0] with { Remember = false });
            loaded = new UserStore(store.DirectoryPath); loaded.Load();
            if (loaded.Settings.Profiles[0].Password.Length != 0 || loaded.Settings.Profiles[0].EnablePassword.Length != 0)
                throw new InvalidOperationException("Forgetting saved credentials failed.");
            if (Directory.EnumerateFiles(store.DirectoryPath, "*.tmp").Any()) throw new InvalidOperationException("Temporary encrypted file not cleaned.");
            _ = System.IO.Ports.SerialPort.GetPortNames(); // Exercise the published runtime-specific assembly.
            var serialProfile = new ConnectionProfile { Kind = ConnectionKind.Serial, SerialPort = "COM123", BaudRate = 115200, AutoBaud = true };
            store.SaveProfile(serialProfile);
            loaded = new UserStore(store.DirectoryPath); loaded.Load();
            if (loaded.Settings.Profiles[0].Kind != ConnectionKind.Serial || loaded.Settings.Profiles[0].SerialPort != "COM123") throw new InvalidOperationException("Serial profile migration failed.");
            var settingsWindow = new SettingsWindow(store, model); settingsWindow.Show(); settingsWindow.UpdateLayout(); settingsWindow.Close();
            var dependenciesWindow = new DependenciesWindow(model.Dependencies); dependenciesWindow.Show(); dependenciesWindow.UpdateLayout(); dependenciesWindow.Close();
            var inventoryWindow = new InventoryWindow(model); inventoryWindow.Show(); inventoryWindow.UpdateLayout(); inventoryWindow.Close();
            var connection = new ConnectionWindow([new("smoke-switch", 2222, "smoke-user", true, "fake-secret", "fake-enable")]);
            connection.Show(); connection.UpdateLayout();
            if (((TextBox)connection.FindName("Host")).Text != "smoke-switch" || ((PasswordBox)connection.FindName("Password")).Password != "fake-secret")
                throw new InvalidOperationException("Connection profile fields failed to load.");
            connection.Close();
            model.DemoCommand.Execute(null);
            await Wait(model);
            if (!model.IsDemo || model.ResultPort != "Fa0/14" || model.Ports.Count != 26 || model.ResultVlan != "10")
                throw new InvalidOperationException("Demo port detection did not produce the expected result.");
            for (var tab = 0; tab < 5; tab++)
            {
                model.Tab = tab;
                if (tab == 1) model.SelectedPort = model.Ports.First(p => p.Name == "Fa0/14");
                if (tab == 3) { model.PassiveCommand.Execute(null); await Wait(model); }
                await Task.Delay(250); window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"screen-{tab}.png")); encoder.Save(file);
            }
            model.SelectedPort = model.Ports.First(p => p.Name == "Fa0/14");
            model.TdrCommand.Execute(null); await Wait(model);
            if (!model.Status.Contains("bloqué", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Own-port TDR was not blocked.");
            model.Description = "Smoke description";
            await Confirm(model.DescriptionCommand, model, "description Smoke description");
            if (model.Ports.Single(p => p.Name == "Fa0/14").Description != "Bureau 214") throw new InvalidOperationException("Dry run modified demo configuration.");
            model.DryRun = false;
            await Confirm(model.DescriptionCommand, model, "description Smoke description");
            if (model.Ports.Single(p => p.Name == "Fa0/14").Description != "Smoke description") throw new InvalidOperationException("Apply did not refresh the description.");
            model.VlanId = "99"; model.VlanName = "SMOKE";
            await Confirm(model.CreateVlanCommand, model, "vlan 99");
            model.SelectedVlan = model.Vlans.Single(v => v.Id == 99);
            await Confirm(model.DeleteVlanCommand, model, "no vlan 99");
            if (model.Vlans.Any(v => v.Id == 99)) throw new InvalidOperationException("VLAN delete did not refresh.");
            model.SelectedPort = model.Ports.First(p => p.Name == "Gi0/2");
            await Confirm(model.TdrCommand, model, "test cable-diagnostics tdr interface Gi0/2");
            if (model.TdrPairs.Count != 4) throw new InvalidOperationException("TDR result table did not refresh.");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: WPF startup; demo detection; five tabs; connection form; serial assembly enumeration; serial profile DPAPI; settings, dependency and inventory views; passive counters; own-port TDR guard; encrypted profile and backup; forget credentials; dry-run; command previews; description apply; VLAN create/delete; four-pair TDR table.\n");
            Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + e);
            Application.Current.Shutdown(1);
        }
    }
    private static async Task Wait(MainViewModel model)
    {
        for (var i = 0; model.IsBusy && i < 200; i++) await Task.Delay(25);
        if (model.IsBusy) throw new TimeoutException("Demo operation timed out.");
    }
    private static async Task Confirm(ICommand command, MainViewModel model, string expectedCommand)
    {
        void Trace(string text) => File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPilot", "SmokeTest", "progress.txt"), text + "\n");
        Trace("Preview: " + expectedCommand);
        var confirmation = new TaskCompletionSource();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != Application.Current.MainWindow && w.IsVisible && w.Owner == Application.Current.MainWindow);
            if (dialog is null && DateTime.UtcNow < deadline) return;
            timer.Stop();
            try
            {
                Trace("Dialog: " + (dialog?.Title ?? "missing"));
                if (dialog is null) throw new InvalidOperationException("Command preview was not shown.");
                var text = Descendants(dialog).OfType<TextBox>().Select(t => t.Text);
                if (!text.Any(t => t.Contains(expectedCommand))) throw new InvalidOperationException("Command preview content did not match.");
                dialog.DialogResult = true; confirmation.SetResult(); Trace("Confirmed");
            }
            catch (Exception e) { dialog?.Close(); confirmation.SetException(e); }
        };
        timer.Start();
        try
        {
            command.Execute(null);
            await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(6)); await Wait(model);
            // Let native modal-window teardown complete before opening the next dialog.
            await Task.Delay(150);
            Trace("Completed: " + model.Status);
        }
        finally { timer.Stop(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
}
