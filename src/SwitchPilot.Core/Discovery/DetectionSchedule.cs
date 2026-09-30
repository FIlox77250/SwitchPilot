namespace SwitchPilot.Core.Discovery;

// Network events coalesce; the timer is also a low-frequency fallback.
public sealed class DetectionSchedule
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);
    public bool Pending { get; private set; }
    private DateTimeOffset? last;
    public void Request() => Pending = true;
    public void MarkStarted(DateTimeOffset now) { last = now; Pending = false; }
    public bool TryStart(DateTimeOffset now)
    {
        if (last is not null && now - last < Interval) return false;
        MarkStarted(now); return true;
    }
}
