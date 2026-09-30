using System.Runtime.Versioning;
using SwitchPilot.Core;
namespace SwitchPilot.Infrastructure.Storage;
[SupportedOSPlatform("windows")]
public sealed class ConfigurationBackup(string directory) : IConfigurationBackup
{
    public Task SaveAsync(string hostname, string configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuration)) throw new InvalidDataException("Configuration vide : modification bloquée, sauvegarde impossible.");
        var destination = Path.Combine(directory, "Backups"); Directory.CreateDirectory(destination);
        // Device names never become filesystem paths. No raw config or credentials in metadata/logs.
        var path = Path.Combine(destination, $"before-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.spbackup");
        UserStore.ExportEncrypted(path, configuration);
        return Task.CompletedTask;
    }
}
