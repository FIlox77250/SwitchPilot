using System.Windows;

namespace SwitchPilot.App.Services;

/// <summary>
/// Screen-aware sizing. The application targets PerMonitorV2 DPI awareness, so the
/// values returned by <see cref="SystemParameters.WorkArea"/> are already device-independent
/// pixels for the primary display, matching the units used by <see cref="Window"/>.
/// </summary>
internal static class ScreenBounds
{
    /// <summary>Primary display working area in DIPs (excludes the taskbar).</summary>
    public static Rect WorkArea => SystemParameters.WorkArea;

    /// <summary>
    /// Sizes a window to a comfortable fraction of the screen, clamped so it always fits.
    /// Large displays get more usable space; small ones still show the full window.
    /// </summary>
    public static void Fit(Window window, double fallbackWidth, double fallbackHeight)
    {
        var area = WorkArea;
        if (area.Width < 100 || area.Height < 100)
        {
            window.Width = fallbackWidth; window.Height = fallbackHeight; return;
        }
        var width = Math.Clamp(area.Width * 0.82, Math.Min(1000, area.Width), Math.Min(1680, area.Width));
        var height = Math.Clamp(area.Height * 0.88, Math.Min(660, area.Height), Math.Min(1020, area.Height));
        window.Width = width; window.Height = height;
    }

    /// <summary>Bounds a dialog so it never exceeds the usable screen height.</summary>
    public static double MaxDialogHeight => Math.Max(280, WorkArea.Height - 60);

    /// <summary>Bounds a dialog width so it stays on screen.</summary>
    public static double MaxDialogWidth => Math.Max(320, WorkArea.Width - 40);

    /// <summary>Applies the screen height/width caps to an already sized dialog.</summary>
    public static void CapDialog(Window window)
    {
        if (window.MaxHeight <= 0 || window.MaxHeight > MaxDialogHeight) window.MaxHeight = MaxDialogHeight;
        if (window.MaxWidth <= 0 || window.MaxWidth > MaxDialogWidth) window.MaxWidth = MaxDialogWidth;
    }
}
