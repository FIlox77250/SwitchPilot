using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwitchPilot.Core;

namespace SwitchPilot.Infrastructure.Storage;

public sealed record UserSettings(List<ConnectionProfile> Profiles, Dictionary<string, string> HostKeys);

[SupportedOSPlatform("windows")]
public sealed class UserStore(string? directory = null)
{
    public string DirectoryPath { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPilot");
    private readonly object gate = new();
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("SwitchPilot.Settings.v1");
    public UserSettings Settings { get; private set; } = new([], new(StringComparer.OrdinalIgnoreCase));
    public void Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, "settings.dpapi");
        if (!File.Exists(file)) return;
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(AtomicFile.ReadBounded(file, 2 * 1024 * 1024), Purpose, DataProtectionScope.CurrentUser);
            var value = JsonSerializer.Deserialize<UserSettings>(clear) ?? throw new InvalidDataException("Paramètres illisibles.");
            if (value.Profiles is null || value.HostKeys is null || value.Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Host) || p.Port is < 1 or > 65535 || p.Username is null || p.Password is null || p.EnablePassword is null))
                throw new InvalidDataException("Structure des paramètres invalide.");
            Settings = new(value.Profiles.Select(p => p.Remember ? p : p with { Password = "", EnablePassword = "" }).ToList(), new(value.HostKeys, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        {
            File.Copy(file, file + $".{Guid.NewGuid():N}.unreadable", false);
            throw;
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }
    public void SaveProfile(ConnectionProfile profile)
    {
        lock (gate)
        {
            Settings.Profiles.RemoveAll(p => p.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) && p.Port == profile.Port);
            Settings.Profiles.Insert(0, profile.Remember ? profile : profile with { Password = "", EnablePassword = "" });
            Save();
        }
    }
    public void Trust(string host, string fingerprint)
    {
        lock (gate) { Settings.HostKeys[host] = fingerprint; Save(); }
    }
    public string? KnownFingerprint(string host) { lock (gate) return Settings.HostKeys.GetValueOrDefault(host); }
    private void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var clear = JsonSerializer.SerializeToUtf8Bytes(Settings);
        try
        {
            var encrypted = ProtectedData.Protect(clear, Purpose, DataProtectionScope.CurrentUser);
            var path = Path.Combine(DirectoryPath, "settings.dpapi");
            AtomicFile.Write(path, encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public static void ExportEncrypted(string path, string config)
    {
        var clear = Encoding.UTF8.GetBytes(config);
        try { AtomicFile.Write(path, ProtectedData.Protect(clear, Encoding.UTF8.GetBytes("SwitchPilot.Backup.v1"), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public static string ReadEncryptedExport(string path)
    {
        var clear = ProtectedData.Unprotect(AtomicFile.ReadBounded(path, 16 * 1024 * 1024), Encoding.UTF8.GetBytes("SwitchPilot.Backup.v1"), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
