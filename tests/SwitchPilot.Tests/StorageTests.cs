using System.Runtime.Versioning;
using SwitchPilot.Infrastructure.Storage;

namespace SwitchPilot.Tests;

public class StorageTests
{
    [WindowsFact] public void ForgettingCredentialsRemovesThemFromPersistedProfiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "switchpilot-store-" + Guid.NewGuid());
        try
        {
            var store = new UserStore(dir);
            var profile = new SwitchPilot.Core.ConnectionProfile("test-host", 22, "user", true, "login-secret", "enable-secret");
            store.SaveProfile(profile); store.SaveProfile(profile with { Remember = false });
            var loaded = new UserStore(dir); loaded.Load();
            var restored = Assert.Single(loaded.Settings.Profiles);
            Assert.Empty(restored.Password); Assert.Empty(restored.EnablePassword);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [WindowsFact, SupportedOSPlatform("windows")] public void CorruptedSettingsArePreservedForRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "switchpilot-store-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.dpapi"); File.WriteAllBytes(file, [1, 2, 3, 4]);
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new UserStore(dir).Load());
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new UserStore(dir).Load());
            Assert.Single(Directory.GetFiles(dir, "*.unreadable")); Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(dir, true); }
    }
    [WindowsFact] public void WindowsBackupIsEncryptedAndReadableByCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        VerifyDpapi();
    }
    [SupportedOSPlatform("windows")]
    private static void VerifyDpapi()
    {
        var file = Path.GetTempFileName();
        try
        {
            const string config = "hostname test\nusername admin secret should-never-be-cleartext";
            UserStore.ExportEncrypted(file, config);
            Assert.DoesNotContain("should-never-be-cleartext", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            Assert.Equal(config, UserStore.ReadEncryptedExport(file));
        }
        finally { File.Delete(file); }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "DPAPI doit être validé sur Windows natif."; }
}
