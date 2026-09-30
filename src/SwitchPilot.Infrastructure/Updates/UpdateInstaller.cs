using System.Diagnostics;
using System.Security.Cryptography;

namespace SwitchPilot.Infrastructure.Updates;

/// <summary>
/// Stages a downloaded update and, once the running process has exited, swaps the
/// portable executable and restarts it. Windows locks the image of a running EXE,
/// so the swap is delegated to a short batch script that waits for our PID.
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>Directory used for the downloaded binary and the apply script.</summary>
    public static string StagingDirectory => Path.Combine(Path.GetTempPath(), "SwitchPilot-Update");

    /// <summary>Path of the running portable executable, or null when hosted outside the app (e.g. `dotnet run`).</summary>
    public static string? CurrentExecutable => Environment.ProcessPath;

    public static bool CanSelfUpdate(string? target = null)
    {
        target ??= CurrentExecutable;
        if (string.IsNullOrEmpty(target) || !File.Exists(target)) return false;
        if (!string.Equals(Path.GetFileName(target), "SwitchPilot.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) return false;
        try
        {
            var probe = Path.Combine(directory, ".switchpilot-write-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Returns the SHA-256 of a file as a lowercase hex string.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Writes the apply script, returning its path. The caller launches it then shuts the app down.</summary>
    public string CreateApplyScript(string target, string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Le fichier de mise à jour est absent.", source);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException("Dossier de l'application introuvable.");
        var image = Path.GetFileName(target);
        foreach (var value in new[] { target, source, image })
            if (value.Any(c => c is '"' or '\r' or '\n' or '%' or '&' or '|' or '<' or '>' or '^' || c < ' '))
                throw new InvalidOperationException("Chemin de mise à jour non pris en charge.");
        Directory.CreateDirectory(StagingDirectory);
        var script = Path.Combine(StagingDirectory, "apply-update.cmd");
        var content =
            "@echo off\r\n" +
            "setlocal enableextensions\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {Environment.ProcessId}\" /NH 2>nul | findstr /I /B /C:\"{image}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  >nul ping -n 2 127.0.0.1\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            "set /a tries=0\r\n" +
            ":retry\r\n" +
            $"copy /Y \"{source}\" \"{target}\" >nul 2>&1\r\n" +
            "if not errorlevel 1 goto launch\r\n" +
            "set /a tries+=1\r\n" +
            "if %tries% GEQ 30 goto launch\r\n" +
            ">nul ping -n 1 127.0.0.1\r\n" +
            "goto retry\r\n" +
            ":launch\r\n" +
            $"if exist \"{target}\" start \"\" \"{target}\"\r\n" +
            $"del /F /Q \"{source}\" >nul 2>&1\r\n" +
            "del /F /Q \"%~f0\" >nul 2>&1\r\n";
        File.WriteAllText(script, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return script;
    }

    /// <summary>Starts the detached apply script. It survives this process.</summary>
    public void Launch(string script)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(script);
        if (Process.Start(start) is null) throw new InvalidOperationException("Impossible de lancer l'installation de la mise à jour.");
    }
}
