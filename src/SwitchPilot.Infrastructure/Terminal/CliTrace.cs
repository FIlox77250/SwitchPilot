using System.Text;

namespace SwitchPilot.Infrastructure.Terminal;

/// <summary>
/// Opt-in diagnostic transcript of the CLI dialogue, kept on the local machine only in
/// %APPDATA%\SwitchPilot\Logs. <see cref="Configure"/> is called once at startup; each session
/// then logs its initialization and the first commands. Outputs that can hold secrets
/// (running-config, write memory, enable flow) are never persisted. The file is capped.
/// </summary>
public static class CliTrace
{
    private const long MaxBytes = 200 * 1024;
    private static readonly object Gate = new();
    private static StreamWriter? file;
    /// <summary>Absolute path of the active transcript, or empty when disabled/unavailable.</summary>
    public static string CurrentFile { get; private set; } = "";

    private static readonly string[] SensitiveMarkers = ["enable\n", "copy ", "write ", "running-config", "startup-config"];

    public static void Configure(string logsDirectory)
    {
        lock (Gate)
        {
            try
            {
                Close();
                if (string.IsNullOrEmpty(logsDirectory) || !Directory.Exists(logsDirectory)) return;
                var path = Path.Combine(logsDirectory, $"console-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                file = new StreamWriter(File.Create(path), Encoding.UTF8);
                file.WriteLine($"# Transcript CLI de diagnostic · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · les réponses sensibles sont masquées");
                file.Flush();
                CurrentFile = path;
            }
            catch { file = null; CurrentFile = ""; }
        }
    }

    /// <summary>Is a command's response safe to persist? Sensitive outputs are masked.</summary>
    public static bool IsSensitive(string command)
    {
        var c = command.Trim();
        foreach (var marker in SensitiveMarkers)
            if (c.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith("enable", StringComparison.OrdinalIgnoreCase) || c.StartsWith("do show run", StringComparison.OrdinalIgnoreCase);
    }

    public static void Line(string direction, string text, bool mask)
    {
        if (text.Length == 0) return;
        lock (Gate)
        {
            if (file is null || file.BaseStream.Position > MaxBytes) return;
            try
            {
                file.WriteLine(direction + " " + (mask ? "[masqué — contenu éventuellement sensible]" : text.TrimEnd()));
                file.Flush();
            }
            catch { /* Diagnostics must never break the session. */ }
        }
    }

    public static void Close()
    {
        if (file is null) return;
        try { file.Close(); } catch { }
        file = null;
        CurrentFile = "";
    }
}
