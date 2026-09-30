using SwitchPilot.Core;
using SwitchPilot.Core.Diagnostics;
namespace SwitchPilot.Tests;
public class CableTests
{
    private readonly LocalLinkMeasurement initial = new("NIC", DateTimeOffset.UtcNow, true, 1_000_000_000, 1_000_000_000, "Full", 0, 0, 100, 100, 0);
    private LocalLinkMeasurement Current => initial with { At = initial.At.AddSeconds(3), BytesReceived = 200 };
    [Fact] public void NoMeasurementsNeverMeansOkay() => Assert.Equal("À vérifier", CableAssessment.Evaluate(null).Verdict);
    [Fact] public void LinkAloneNeverMeansOkay() => Assert.Equal("À vérifier", CableAssessment.Evaluate(initial).Verdict);
    [Fact] public void MeasuredTrafficWithoutErrorsCanBeOkay() => Assert.Equal("Câble OK", CableAssessment.Evaluate(Current, initial).Verdict);
    [Fact] public void MissingCountersCannotBecomeZero() => Assert.Equal("À vérifier", CableAssessment.Evaluate(Current with { ReceiveErrors = null }, initial).Verdict);
    [Fact] public void MissingDuplexCannotBeAssumedFull() => Assert.Equal("À vérifier", CableAssessment.Evaluate(Current with { Duplex = null }, initial).Verdict);
    [Fact] public void CounterResetCannotBeInterpretedAsErrorFree() => Assert.Equal("À vérifier", CableAssessment.Evaluate(Current, initial with { ReceiveErrors = 4 }).Verdict);
    [Fact] public void IncreasingErrorsSuggestFault() => Assert.Equal("Défaut probable", CableAssessment.Evaluate(Current with { ReceiveErrors = 2 }, initial).Verdict);
    [Fact] public void GigabitAt100IsOnlyAnIndication() => Assert.Equal("À vérifier", CableAssessment.Evaluate(Current with { Speed = 100_000_000 }, initial).Verdict);
    [Fact] public void FastEthernetAt100IsNormal() => Assert.Equal("Câble OK", CableAssessment.Evaluate(Current with { Speed = 100_000_000, MaxSpeed = 100_000_000 }, initial).Verdict);
    [Fact] public void DuplexMismatchIsExplained()
    {
        var report = CableAssessment.Evaluate(Current, initial, new("Gi0/1", Current.At, new(0, 0, 0, "1000", "Half", "Actif")));
        Assert.Equal("À vérifier", report.Verdict); Assert.Contains("Duplex mismatch", report.Details);
    }
    [Theory]
    [InlineData("Inconnue", "Full")]
    [InlineData("1000", "Inconnu")]
    [InlineData("0", "Full")]
    public void IncompleteRemoteLinkCannotConfirmCable(string speed, string duplex)
    {
        var report = CableAssessment.Evaluate(Current, initial, new("Gi0/1", Current.At, new(0, 0, 0, speed, duplex, "Actif")));
        Assert.Equal("À vérifier", report.Verdict);
    }
    [Fact] public void LinkOscillationHasBoundedWindowAndExcludesTdr()
    {
        var tracker = new LinkStability(); tracker.Observe(true, initial.At);
        for (var i = 1; i <= 4; i++) tracker.Observe(i % 2 == 0, initial.At.AddSeconds(i));
        Assert.Equal(4, tracker.Observe(true, initial.At.AddSeconds(5)));
        Assert.Equal(4, tracker.Observe(false, initial.At.AddSeconds(6), true));
        Assert.Equal(0, tracker.Observe(false, initial.At.AddSeconds(70)));
        Assert.Equal("Défaut probable", CableAssessment.Evaluate(Current with { LinkTransitions = 4 }, initial).Verdict);
    }
    [Fact] public void ConsoleCanTestOwnCopperPortButNotTrunks()
    {
        var port = new PortInfo("Gi0/1", "", "connected", "10", "full", "1000", "1000BaseT", "access");
        SafetyPolicy.RequireSafeTdr(port, SafetyContext.Unknown, ConnectionKind.Serial);
        Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port with { Mode = "trunk" }, SafetyContext.Unknown, ConnectionKind.Serial));
        Assert.Throws<InvalidOperationException>(() => SafetyPolicy.RequireSafeTdr(port, SafetyContext.Unknown, ConnectionKind.Ssh));
    }
}
