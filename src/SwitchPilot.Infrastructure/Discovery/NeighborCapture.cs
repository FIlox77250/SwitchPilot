using SharpPcap;
using SharpPcap.LibPcap;
using SwitchPilot.Core;
using SwitchPilot.Core.Discovery;

namespace SwitchPilot.Infrastructure.Discovery;

public static class NeighborCapture
{
    public static async Task WatchAsync(LocalAdapter adapter, Action<NeighborAnnouncement> received, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("La capture locale nécessite Windows et Npcap.");
        await Task.Run(async () =>
        {
            var id = adapter.Id.Trim('{', '}');
            using var device = LibPcapLiveDeviceList.New().FirstOrDefault(d => d.Name.Contains(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Carte introuvable dans Npcap.");
            device.OnPacketArrival += (_, e) =>
            {
                if (ct.IsCancellationRequested) return;
                var packet = e.GetPacket();
                if (packet.LinkLayerType == PacketDotNet.LinkLayers.Ethernet && NeighborParser.Parse(packet.Data) is { } neighbor) received(neighbor);
            };
            device.Open(DeviceModes.None, 250);
            // Destination filtering also covers VLAN-tagged announcements.
            device.Filter = "ether dst 01:80:c2:00:00:0e or ether dst 01:00:0c:cc:cc:cc";
            device.StartCapture();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { device.StopCapture(); device.Close(); }
        }, ct);
    }
    public static async Task<NeighborAnnouncement> ListenAsync(LocalAdapter adapter, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(adapter.Id)) throw new ArgumentException("Carte de capture non sélectionnée.");
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("La capture locale nécessite Windows et Npcap.");
        try
        {
            return await Task.Run(async () =>
            {
                var id = adapter.Id.Trim('{', '}');
                // Fresh enumeration supports adapters plugged in after an earlier capture and
                // avoids retaining event handlers in SharpPcap's process-wide singleton.
                var device = LibPcapLiveDeviceList.New().FirstOrDefault(d => d.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
                if (device == null) throw new InvalidOperationException("Carte introuvable dans Npcap. Vérifiez son installation et les droits de capture.");
                using (device)
                {
                    var result = new TaskCompletionSource<NeighborAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
                    device.OnPacketArrival += (_, e) =>
                    {
                        var packet = e.GetPacket();
                        if (packet.LinkLayerType != PacketDotNet.LinkLayers.Ethernet) return;
                        var neighbor = NeighborParser.Parse(packet.Data);
                        if (neighbor is not null) result.TrySetResult(neighbor);
                    };
                    device.Open(DeviceModes.Promiscuous, 250);
                    device.Filter = "ether proto 0x88cc or ether dst 01:00:0c:cc:cc:cc or (vlan and ether proto 0x88cc)";
                    device.StartCapture();
                    try { return await result.Task.WaitAsync(timeout, ct); }
                    finally { device.StopCapture(); device.Close(); }
                }
            }, ct);
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or PcapException)
        { throw new InvalidOperationException("Npcap absent ou capture inaccessible. La détection par SSH reste disponible.", e); }
        catch (TimeoutException)
        { throw new TimeoutException("Aucune annonce LLDP/CDP reçue. Le protocole peut être désactivé sur le switch ; utilisez la détection SSH."); }
    }
}
