using System.Net.NetworkInformation;
using SwitchPilot.Core;
using SwitchPilot.Core.Discovery;
namespace SwitchPilot.Infrastructure.Discovery;

public record AdapterObservation(LocalAdapter Adapter, long Generation, IReadOnlyList<NeighborAnnouncement> Neighbors, string CaptureError);
public sealed class NetworkMonitor(
    Func<IReadOnlyList<LocalAdapter>>? enumerate = null,
    Func<LocalAdapter, Action<NeighborAnnouncement>, CancellationToken, Task>? captureFrames = null,
    TimeSpan? interval = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Link> links = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Task? loop;
    private bool capture;
    public event Action<IReadOnlyList<AdapterObservation>>? Changed;
    public void Start()
    {
        if (loop is not null) return;
        NetworkChange.NetworkAddressChanged += NetworkChanged; NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        loop = Task.Run(RunAsync);
    }
    public void Refresh() => Wake();
    public void EnableCapture(bool enabled) { lock (gate) { capture = enabled; if (enabled) foreach (var link in links.Values) link.RetryAfter = default; } Wake(); }
    public void Redetect() { lock (gate) { foreach (var link in links.Values) { link.Tracker.Reset(); link.Cancellation?.Cancel(); } } Wake(); }
    private void NetworkChanged(object? sender, EventArgs args) => Wake();
    private void Wake() { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } }
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IReadOnlyList<LocalAdapter> adapters;
                try { adapters = (enumerate ?? NetworkDiscovery.Adapters)(); }
                catch (NetworkInformationException) { adapters = []; }
                lock (gate)
                {
                    foreach (var missing in links.Keys.Except(adapters.Select(a => a.Id)).ToArray())
                    { links[missing].Cancellation?.Cancel(); links[missing].Tracker.Reset(); links[missing].Removed = true; }
                    foreach (var adapter in adapters)
                    {
                        if (!links.TryGetValue(adapter.Id, out var link)) links[adapter.Id] = link = new(adapter);
                        if (link.Removed || adapter.IsUp != link.Adapter.IsUp || adapter.Mac != link.Adapter.Mac)
                        { link.Tracker.Reset(); link.Cancellation?.Cancel(); link.RetryAfter = default; }
                        link.Removed = false; link.Adapter = adapter;
                        if (!adapter.IsUp || !capture) link.Cancellation?.Cancel();
                        if (adapter.IsUp && capture && (link.Work is null || link.Work.IsCompleted) && DateTimeOffset.UtcNow >= link.RetryAfter)
                        {
                            link.Cancellation?.Dispose(); link.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                            var generation = link.Tracker.Generation;
                            link.Error = ""; link.Work = CaptureAsync(link, generation, link.Cancellation.Token);
                        }
                    }
                }
                Publish();
                await wake.WaitAsync(interval ?? TimeSpan.FromSeconds(1), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task CaptureAsync(Link link, long generation, CancellationToken ct)
    {
        // Defer device work so no native call executes while the monitor lock is held.
        await Task.Yield();
        try
        {
            await (captureFrames ?? NeighborCapture.WatchAsync)(link.Adapter, item =>
            {
                lock (gate) { if (ct.IsCancellationRequested || !link.Tracker.Accept(item, generation)) return; }
                Publish();
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate) { link.Error = "Capture inaccessible. Vérifiez Npcap et les droits de capture ; SSH et console restent disponibles."; link.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(60); }
        }
    }
    private void Publish()
    {
        AdapterObservation[] state;
        lock (gate) state = links.Values.Where(l => !l.Removed).Select(l => new AdapterObservation(l.Adapter, l.Tracker.Generation, l.Tracker.Current(DateTimeOffset.UtcNow), l.Error)).ToArray();
        Changed?.Invoke(state);
    }
    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged; NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        stop.Cancel(); if (loop is not null) await loop;
        Task[] tasks;
        lock (gate) { foreach (var link in links.Values) link.Cancellation?.Cancel(); tasks = links.Values.Select(l => l.Work).OfType<Task>().ToArray(); }
        await Task.WhenAll(tasks);
        foreach (var link in links.Values) link.Cancellation?.Dispose();
        stop.Dispose();
    }
    private sealed class Link(LocalAdapter adapter)
    {
        public LocalAdapter Adapter = adapter;
        public readonly NeighborTracker Tracker = new();
        public CancellationTokenSource? Cancellation;
        public Task? Work;
        public bool Removed;
        public string Error = "";
        public DateTimeOffset RetryAfter;
    }
}
