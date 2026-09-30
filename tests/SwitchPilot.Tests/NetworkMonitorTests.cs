using System.Collections.Concurrent;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Discovery;
namespace SwitchPilot.Tests;
public class NetworkMonitorTests
{
    [Fact] public async Task StartsWithoutNpcapThenActivatesAndCancelsOnUnplug()
    {
        var current = new LocalAdapter("NIC", "Ethernet", "001122334455", true, 1000000000, "");
        var observations = new ConcurrentQueue<IReadOnlyList<AdapterObservation>>();
        var starts = 0; var cancelled = 0;
        await using var monitor = new NetworkMonitor(() => [Volatile.Read(ref current)], async (adapter, received, ct) =>
        {
            Interlocked.Increment(ref starts); received(new("LLDP", "SW", "Gi0/1", 10, 120, DateTimeOffset.UtcNow));
            try { await Task.Delay(Timeout.Infinite, ct); } finally { Interlocked.Increment(ref cancelled); }
        }, TimeSpan.FromMilliseconds(20));
        monitor.Changed += observations.Enqueue; monitor.Start();
        await Until(() => observations.Count > 0); Assert.Equal(0, starts);
        monitor.EnableCapture(true); await Until(() => observations.Any(s => s.Any(o => o.Neighbors.Count == 1)));
        Assert.Equal(1, starts);
        Volatile.Write(ref current, current with { IsUp = false }); monitor.Refresh();
        await Until(() => cancelled == 1 && observations.Any(s => s.Any(o => !o.Adapter.IsUp && o.Neighbors.Count == 0)));
        Volatile.Write(ref current, current with { IsUp = true }); monitor.Refresh();
        await Until(() => starts == 2);
    }
    [Fact] public async Task SeparateAdaptersDoNotShareAnnouncements()
    {
        var states = new ConcurrentQueue<IReadOnlyList<AdapterObservation>>();
        await using var monitor = new NetworkMonitor(() => [new("A", "A", "001122334455", true, 100, ""), new("B", "B", "001122334456", true, 100, "")], async (adapter, receive, ct) =>
        {
            receive(new("LLDP", "SW-" + adapter.Id, "Fa0/1", 10, 120, DateTimeOffset.UtcNow)); await Task.Delay(Timeout.Infinite, ct);
        }, TimeSpan.FromMilliseconds(20));
        monitor.Changed += states.Enqueue; monitor.EnableCapture(true); monitor.Start();
        await Until(() => states.Any(s => s.Count == 2 && s.All(o => o.Neighbors.Count == 1)));
        var latest = states.Last(s => s.All(o => o.Neighbors.Count == 1));
        Assert.All(latest, o => Assert.Equal("SW-" + o.Adapter.Id, o.Neighbors[0].SwitchName));
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
