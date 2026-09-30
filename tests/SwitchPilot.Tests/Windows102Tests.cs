using System.Runtime.Versioning;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Dependencies;
using SwitchPilot.Infrastructure.Storage;
namespace SwitchPilot.Tests;
[SupportedOSPlatform("windows")]
public class Windows102Tests
{
    [WindowsFact] public void UnsignedExecutableIsRejectedByAuthenticode()
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, "not a signed executable"); Assert.False(new NpcapPlatform().VerifyInstaller(path)); }
        finally { File.Delete(path); }
    }
    [WindowsFact] public void SerialProfilesAndOptionsSurviveDpapiRoundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "switchpilot-102-" + Guid.NewGuid());
        try
        {
            var store = new UserStore(directory);
            store.SaveProfile(new("switch", 22, "user", false));
            store.SaveProfile(new("", 22, "console", true, "fake-console-password") { Kind = ConnectionKind.Serial, SerialPort = "COM3", AutoBaud = true });
            store.SaveOptions(90, true);
            var restored = new UserStore(directory); restored.Load();
            Assert.Equal(2, restored.Settings.Profiles.Count); Assert.Equal("fake-console-password", restored.Settings.Profiles[0].Password);
            Assert.Equal(ConnectionKind.Serial, restored.Settings.Profiles[0].Kind); Assert.True(restored.Settings.AutoTdrConsole); Assert.Equal(90, restored.Settings.DetectionIntervalSeconds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [WindowsFact] public async Task AutomaticBackupIsEncryptedAndRecoverable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "switchpilot-102-" + Guid.NewGuid());
        try
        {
            await new ConfigurationBackup(directory).SaveAsync("SW", "hostname SW\nusername admin secret fake-private-secret\nend", default);
            var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "Backups")));
            Assert.DoesNotContain("fake-private-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            Assert.Contains("hostname SW", UserStore.ReadEncryptedExport(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
