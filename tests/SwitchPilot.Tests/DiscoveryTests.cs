using SwitchPilot.Core;
using SwitchPilot.Core.Discovery;
namespace SwitchPilot.Tests;
public class DiscoveryTests
{
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private NeighborAnnouncement Announcement => new("LLDP", "SW", "Fa0/1", 10, 20, now);
    [Fact] public void ExpirationAndDisconnectClearAnnouncements()
    {
        var tracker = new NeighborTracker(); tracker.Accept(Announcement, tracker.Generation);
        Assert.Single(tracker.Current(now.AddSeconds(19))); Assert.Empty(tracker.Current(now.AddSeconds(20)));
        tracker.Accept(Announcement, tracker.Generation); tracker.Reset(); Assert.Empty(tracker.Current(now));
    }
    [Fact] public void LatePacketFromPreviousCableIsRejected()
    {
        var tracker = new NeighborTracker(); var generation = tracker.Generation; tracker.Reset();
        Assert.False(tracker.Accept(Announcement, generation)); Assert.Empty(tracker.Current(now));
    }
    [Fact] public void ZeroTtlWithdrawsNeighbor()
    {
        var tracker = new NeighborTracker(); tracker.Accept(Announcement, 0); tracker.Accept(Announcement with { TtlSeconds = 0 }, 0);
        Assert.Empty(tracker.Current(now));
    }
    [Fact] public void RenewalExtendsLifetimeAndDifferentSourcesRemainSeparate()
    {
        var tracker = new NeighborTracker(); tracker.Accept(Announcement, 0);
        tracker.Accept(Announcement with { ReceivedAt = now.AddSeconds(15) }, 0);
        tracker.Accept(Announcement with { Protocol = "CDP", Port = "Fa0/2", ReceivedAt = now.AddSeconds(15) }, 0);
        Assert.Equal(2, tracker.Current(now.AddSeconds(25)).Count);
    }
    [Fact] public void SamePortOnAnotherSwitchIsNotCorroboration()
    {
        var detection = new PortDetection("OTHER", new(10, "001122334455", "DYNAMIC", "Fa0/1"), new("Fa0/1", "", "connected", "10", "full", "100", "10/100BaseTX", "access"), true, "");
        Assert.Contains("différent", NeighborCorrelation.Compare(Announcement, detection));
        Assert.Contains("Incohérence", NeighborCorrelation.Compare(Announcement with { Port = "Fa0/2" }, detection with { SwitchName = "SW" }));
        Assert.Contains("concordant", NeighborCorrelation.Compare(Announcement, detection with { SwitchName = "SW" }));
    }
}
