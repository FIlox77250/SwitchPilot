using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwitchPilot.Core;

namespace SwitchPilot.App.Services;

public static class Dialogs
{
    private static Window Shell(string title, int width = 560) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 850,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow,
        ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
    };
    public static bool Confirm(string text, string title) => MessageBox.Show(Application.Current.MainWindow, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static bool Preview(CommandPlan plan, bool dryRun)
    {
        return PreviewText(plan.Title, plan.Preview, dryRun ? "Simulation : aucune commande de modification ne sera envoyée au switch." :
            plan.Kind == ChangeKind.Save ? "La configuration active sera écrite dans la mémoire permanente du switch." :
            "Ces commandes modifient immédiatement le switch et peuvent interrompre des connexions. En cas d'échec, les modifications peuvent être partielles. La sauvegarde permanente reste une action distincte.",
            dryRun ? "Simuler uniquement" : "Appliquer au switch");
    }
    public static bool PreviewText(string title, string commands, string note, string action)
    {
        var window = Shell(title, 660); var panel = new StackPanel { Margin = new Thickness(28) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = note, Margin = new(0, 0, 0, 20) });
        panel.Children.Add(new TextBox { Text = commands, IsReadOnly = true, FontFamily = (FontFamily)Application.Current.FindResource("CodeFont"), MinHeight = 130, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true }; var apply = new Button { Content = action, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        apply.Click += (_, _) => window.DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        return window.ShowDialog() == true;
    }
    public static void CompareConfigurations(string before, string current)
    {
        var window = Shell("Comparer · sauvegarde / configuration actuelle", 1100);
        window.Height = 700; window.SizeToContent = SizeToContent.Manual; window.ResizeMode = ResizeMode.CanResize;
        var grid = new Grid { Margin = new Thickness(24) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        foreach (var (text, column) in new[] { ("SAUVEGARDE\n\n" + before, 0), ("CONFIGURATION ACTUELLE\n\n" + current, 1) })
        {
            var box = new TextBox { Text = text, IsReadOnly = true, FontFamily = (FontFamily)Application.Current.FindResource("CodeFont"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4) };
            Grid.SetColumn(box, column); grid.Children.Add(box);
        }
        window.Content = grid; window.ShowDialog();
    }
    public static void ShowText(string title, string text)
    {
        var window = Shell(title, 900); window.Height = 650; window.SizeToContent = SizeToContent.Manual; window.ResizeMode = ResizeMode.CanResize;
        window.Content = new TextBox { Text = text, IsReadOnly = true, FontFamily = (FontFamily)Application.Current.FindResource("CodeFont"), Margin = new(24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
    }
}
