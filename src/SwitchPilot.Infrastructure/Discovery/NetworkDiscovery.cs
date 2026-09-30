using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SwitchPilot.Core;

namespace SwitchPilot.Infrastructure.Discovery;

public static class NetworkDiscovery
{
    public static IReadOnlyList<LocalAdapter> Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)
        .Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
        .Where(n => !new[] { "virtual", "hyper-v", "vpn", "tap-", "vmware", "virtualbox", "wsl" }.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase)))
        .Select(n => new LocalAdapter(n.Id, n.Name, Convert.ToHexString(n.GetPhysicalAddress().GetAddressBytes()), n.OperationalStatus == OperationalStatus.Up, n.Speed,
            string.Join(", ", n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString())))).OrderByDescending(n => n.IsUp).ToArray();

    // UDP connect selects a route locally; no UDP datagram is sent.
    public static async Task<bool> RoutesViaAsync(string host, LocalAdapter adapter, CancellationToken ct)
    {
        // A routed management session can traverse an access uplink different from the
        // port learning our MAC. Only a proven direct IPv4 route authorizes destructive TDR.
        if (!OperatingSystem.IsWindows()) return false;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var local = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapter.Id);
        if (local is null || local.OperationalStatus != OperationalStatus.Up) return false;
        var ips = local.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToHashSet();
        if (addresses.Length == 0) return false;
        foreach (var address in addresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(address, 22));
                if (socket.LocalEndPoint is not IPEndPoint endpoint || !ips.Contains(endpoint.Address)) return false;
                var code = GetBestRoute(BitConverter.ToUInt32(address.GetAddressBytes()), BitConverter.ToUInt32(endpoint.Address.GetAddressBytes()), out var route);
                if (code != 0 || !IsDirectRoute(route.Type, route.InterfaceIndex, (uint)local.GetIPProperties().GetIPv4Properties().Index)) return false;
            }
            catch (SocketException) { return false; }
        }
        return true;
    }

    public static bool IsDirectRoute(uint type, uint interfaceIndex, uint expectedInterfaceIndex) => type == 3 && interfaceIndex == expectedInterfaceIndex;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute(uint destination, uint source, out IpForwardRow route);

    // MIB_IPFORWARDROW: all DWORDs, addresses stored in network byte order.
    [StructLayout(LayoutKind.Sequential)]
    private struct IpForwardRow
    {
        public uint Destination, Mask, Policy, NextHop, InterfaceIndex, Type, Protocol, Age,
            NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5;
    }
}
