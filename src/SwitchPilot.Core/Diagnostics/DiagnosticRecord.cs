namespace SwitchPilot.Core.Diagnostics;
public record DiagnosticRecord(DateTimeOffset At, string Switch, string Port, string Verdict, string Details,
    LocalLinkMeasurement? Local = null, SwitchMeasurement? Remote = null, TdrResult? Tdr = null)
{
    public string TimeLabel => At.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
