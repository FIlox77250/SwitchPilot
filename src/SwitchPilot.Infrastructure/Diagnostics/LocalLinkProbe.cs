using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using SwitchPilot.Core;
using SwitchPilot.Core.Diagnostics;
namespace SwitchPilot.Infrastructure.Diagnostics;

[SupportedOSPlatform("windows")]
public static class LocalLinkProbe
{
    public static LocalLinkMeasurement Read(LocalAdapter adapter, int transitions)
    {
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapter.Id);
        long? receiveErrors = null, transmitErrors = null, received = null, sent = null, max = null;
        string? duplex = null;
        if (network is not null)
        {
            try
            {
                var counters = network.GetIPv4Statistics();
                receiveErrors = counters.IncomingPacketsWithErrors; transmitErrors = counters.OutgoingPacketsWithErrors;
                received = counters.BytesReceived; sent = counters.BytesSent;
            }
            catch (NetworkInformationException) { }
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT InterfaceGuid, MediaDuplexState, MaxSpeed FROM MSFT_NetAdapter");
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        if (!Guid.TryParse(row["InterfaceGuid"] as string, out var actual) || !Guid.TryParse(adapter.Id, out var expected) || actual != expected) continue;
                        duplex = Convert.ToUInt32(row["MediaDuplexState"] ?? 0u) switch { 1 => "Half", 2 => "Full", _ => null };
                        if (row["MaxSpeed"] is ulong value && value > 0 && value <= long.MaxValue) max = (long)value;
                    }
                }
            }
            catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        }
        return new(adapter.Id, DateTimeOffset.UtcNow, network?.OperationalStatus == OperationalStatus.Up,
            network?.OperationalStatus == OperationalStatus.Up && network.Speed > 0 ? network.Speed : null,
            max, duplex, receiveErrors, transmitErrors, received, sent, transitions);
    }
}
