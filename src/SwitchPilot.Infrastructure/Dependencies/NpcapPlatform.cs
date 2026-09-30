using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace SwitchPilot.Infrastructure.Dependencies;

[SupportedOSPlatform("windows")]
public sealed class NpcapPlatform : IDependencyPlatform
{
    // Reviewed release, never obtain an executable URL from untrusted HTML or user input.
    public const string DownloadUrl = "https://npcap.com/dist/npcap-1.89.exe";
    public static readonly Version MinimumVersion = new(1, 79);
    public Task<DependencyStatus> InspectAsync(CancellationToken ct) => Task.Run(() => Inspect(), ct);
    private static DependencyStatus Inspect()
    {
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap");
            if (service is null) return new(DependencyState.Missing, "—", "Non installé");
            var directory = Path.Combine(system, "Npcap");
            var library = Path.Combine(directory, "wpcap.dll");
            if (!File.Exists(Path.Combine(system, "drivers", "npcap.sys")) || !File.Exists(library) || !File.Exists(Path.Combine(directory, "Packet.dll")))
                return new(DependencyState.Incomplete, "—", "Pilote ou bibliothèques manquants");
            using var parameters = service.OpenSubKey("Parameters");
            var adminOnly = parameters?.GetValue("AdminOnly");
            if (adminOnly is not null && int.TryParse(Convert.ToString(adminOnly, System.Globalization.CultureInfo.InvariantCulture), out var adminFlag) && adminFlag != 0)
                return new(DependencyState.Restricted, "—", "Accès réservé aux administrateurs. Réinstaller Npcap sans cette option ; l’application ne demandera pas d’élévation pour capturer.");
            if (!ServiceRunning()) return new(DependencyState.Stopped, "—", "Service npcap arrêté. Réparez l’installation depuis npcap.com.");
            var handle = NativeLibrary.Load(library);
            try
            {
                var getVersion = Marshal.GetDelegateForFunctionPointer<PcapVersion>(NativeLibrary.GetExport(handle, "pcap_lib_version"));
                var description = Marshal.PtrToStringAnsi(getVersion()) ?? "";
                var match = Regex.Match(description, @"Npcap version (\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version) || version < MinimumVersion || version.Major != 1)
                    return new(DependencyState.Incompatible, match.Success ? match.Groups[1].Value : "inconnue", "Version compatible attendue : Npcap 1.79 à 1.x");
                return new(DependencyState.Ready, version.ToString(), "Installé · service démarré");
            }
            finally { NativeLibrary.Free(handle); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or Win32Exception or FormatException or OverflowException or InvalidCastException)
        { return new(DependencyState.Unavailable, "—", "État ou API Npcap inaccessible. Vérifiez l’installation et les droits du compte."); }
    }
    public async Task DownloadAsync(string path, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var uri = new Uri(DownloadUrl);
        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (uri.Scheme != "https" || uri.Host != "npcap.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                    throw new InvalidOperationException("Redirection de téléchargement non autorisée.");
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                { uri = new Uri(uri, next); response.Dispose(); response = null; continue; }
                break;
            }
            if (response is null) throw new HttpRequestException("Trop de redirections.");
            response.EnsureSuccessStatusCode();
            const long limit = 64 * 1024 * 1024;
            var total = response.Content.Headers.ContentLength;
            if (total > limit) throw new InvalidDataException("Installateur trop volumineux.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long count = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                count += read;
                if (count > limit) throw new InvalidDataException("Installateur trop volumineux.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                progress.Report(new("Téléchargement depuis npcap.com…", total > 0 ? count * 100d / total : null));
            }
            if (count == 0 || total is not null && count != total) throw new InvalidDataException("Téléchargement incomplet.");
        }
        finally { response?.Dispose(); }
    }
    public bool VerifyInstaller(string path) => Authenticode.VerifyNpcap(path);
    public async Task<int> LaunchInstallerAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "/winpcap_mode=yes /admin_only=no /no_kill=yes") { UseShellExecute = true, Verb = "runas" })
                ?? throw new IOException("L’installateur n’a pas démarré.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Élévation UAC refusée. SSH et console restent disponibles."); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PcapVersion();
    private static bool ServiceRunning()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var service = OpenService(manager, "npcap", 4);
            if (service == IntPtr.Zero) throw new Win32Exception();
            try { return QueryServiceStatus(service, out var status) && status.CurrentState == 4; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, CurrentState, Controls, Win32Exit, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
