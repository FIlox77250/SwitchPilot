using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Serial;
using SwitchPilot.Infrastructure.Terminal;
namespace SwitchPilot.Tests;
public class SerialTests
{
    private static ConnectionProfile Profile => new("", 22, "tech", false, "test-password") { Kind = ConnectionKind.Serial, SerialPort = "COM3" };
    [Theory][InlineData("SW#")][InlineData("SW(config-if)#")][InlineData("Press RETURN to get started.")][InlineData("Would you like to enter the initial configuration dialog? [yes/no]:")][InlineData("Username:")][InlineData("Password:")]
    public async Task ConsoleStartupReusesTheCli(string initial)
    {
        var terminal = new Emulator(initial);
        await using var session = await SerialSession.ConnectAsync(Profile, default, (_, _) => terminal);
        Assert.Equal(ConnectionKind.Serial, session.Kind); Assert.True(session.IsConnected);
        Assert.Contains("100", await session.ExecuteAsync("show interfaces Fa0/1"));
        if (initial.Contains("config-if")) Assert.Contains("end\n", terminal.Sent);
        if (initial.StartsWith("Would")) Assert.Contains("no\n", terminal.Sent);
        else Assert.DoesNotContain("no\n", terminal.Sent);
    }
    [Fact] public async Task OtherYesNoQuestionIsNeverAnswered()
    {
        var terminal = new Emulator("Erase startup configuration? [yes/no]");
        await Assert.ThrowsAsync<CliProtocolException>(() => SerialSession.ConnectAsync(Profile, default, (_, _) => terminal));
        Assert.DoesNotContain("no\n", terminal.Sent); Assert.False(terminal.IsOpen);
    }
    [Fact] public async Task AutomaticBaudDisposesFailedAttempts()
    {
        var attempts = new List<Emulator>();
        await using var session = await SerialSession.ConnectAsync(Profile with { AutoBaud = true }, default, (_, baud) =>
        {
            var emulator = new Emulator(baud == 19200 ? "SW#" : "garbled") { Garbled = baud != 19200 }; attempts.Add(emulator); return emulator;
        }, TimeSpan.FromMilliseconds(180));
        Assert.Equal(19200, session.BaudRate); Assert.False(attempts[0].IsOpen); Assert.True(attempts[1].IsOpen);
        Assert.DoesNotContain("test-password\n", attempts[0].Sent);
    }
    [Fact] public async Task SyslogAfterPromptDoesNotCauseTimeout()
    {
        var channel = new Emulator("SW#"); var cli = new CliConversation(channel); await cli.InitializeConsoleAsync(Profile, default);
        channel.AfterPrompt = true;
        Assert.Contains("100", await cli.CommandAsync("show interfaces Fa0/1", default));
    }
    [Fact] public void SerialProfileCannotInjectTerminalControls() => Assert.Throws<ArgumentException>(() => (Profile with { Password = "bad\nreload" }).Validate());
    [Fact] public void SerialProfileStringDoesNotExposeCredentials() => Assert.Equal("Console COM3 · 9600", Profile.ToString());
    private sealed class Emulator(string initial) : ITerminalChannel
    {
        private readonly Queue<string> pending = new([initial]);
        private string state = initial;
        public bool Garbled, AfterPrompt;
        public List<string> Sent { get; } = [];
        public bool IsOpen { get; private set; } = true;
        public string ReadAvailable() => pending.TryDequeue(out var chunk) ? chunk : "";
        public void Send(string text)
        {
            Sent.Add(text); if (Garbled) return;
            if (text == "\n" && Sent.Count == 1) return;
            if (state.StartsWith("Press") && text == "\n" || state.StartsWith("Would") && text == "no\n") state = "SW#";
            else if (state == "Username:" && text == "tech\n") state = "Password:";
            else if (state == "Password:" && text == "test-password\n") state = "SW#";
            else if (text == "end\n") state = "SW#";
            pending.Enqueue(text.StartsWith("show ") ? text + "\n*Mar 1 00:00:01: %LINK-3-UPDOWN: Interface Fa0/2\nFull-duplex, 100Mb/s\nSW#" + (AfterPrompt ? "\n%SYS-5-CONFIG_I: Configured from console\n" : "") : state);
        }
        public void Dispose() => IsOpen = false;
    }
}
