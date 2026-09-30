namespace SwitchPilot.Core;
public interface IConfigurationBackup
{
    Task SaveAsync(string hostname, string configuration, CancellationToken ct);
}
