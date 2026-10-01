using SwitchPilot.Infrastructure.Ssh;
using SwitchPilot.Infrastructure.Terminal;

namespace SwitchPilot.Tests;

public class CliConversationTests
{
    [Fact] public async Task UserAndEnableAndConfigurationPromptsAreRecognized()
    {
        var channel = new FakeTerminal("\r\nSW>");
        channel.OnSend = command => command switch
        {
            "enable\n" => ["Password:"],
            "enable-secret\n" => ["\r\nSW#"],
            "configure terminal\n" => ["configure terminal\r\nEnter configuration commands.\r\nSW(config)#"],
            "interface Fa0/1\n" => ["interface Fa0/1\r\nSW(config-if)#"],
            _ => [command + "\r\nSW>"]
        };
        var cli = new CliConversation(channel);
        await cli.InitializeAsync(default); Assert.False(cli.Privileged);
        await cli.EnsurePrivilegedAsync("enable-secret", default); Assert.True(cli.Privileged);
        await cli.CommandAsync("configure terminal", default); Assert.Equal("SW(config)#", cli.Prompt);
        await cli.CommandAsync("interface Fa0/1", default); Assert.Equal("SW(config-if)#", cli.Prompt);
    }
    [Fact] public async Task PaginationWorksAcrossNetworkChunks()
    {
        var channel = new FakeTerminal("SW#");
        channel.OnSend = command => command switch
        {
            "show example\n" => ["show example\r\nrow1\r\n--Mo", "re--"],
            " " => ["\b\b\b\b\b\b\b\b        \b\b\b\b\b\b\b\brow2\r\nSW", "#"],
            _ => [command + "\r\nSW#"]
        };
        var cli = new CliConversation(channel); await cli.InitializeAsync(default);
        var output = await cli.CommandAsync("show example", default);
        Assert.Contains("row1", output); Assert.Contains("row2", output); Assert.DoesNotContain("--More--", output);
        Assert.Contains(" ", channel.Sent);
    }
    [Theory]
    [InlineData("% Invalid input detected at '^' marker.")]
    [InlineData("% Authorization failed.")]
    [InlineData("% Incomplete command.")]
    [InlineData("TDR is not supported on this interface")]
    public async Task IosErrorsAreNotSuccess(string error)
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel); await cli.InitializeAsync(default);
        channel.OnSend = _ => [error + "\r\nSW#"];
        await Assert.ThrowsAsync<CliException>(() => cli.CommandAsync("show broken", default));
    }
    [Fact] public async Task NeverAnswersAnUnexpectedConfirmation()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel); await cli.InitializeAsync(default);
        channel.OnSend = _ => ["Proceed? [confirm]"];
        await Assert.ThrowsAsync<CliProtocolException>(() => cli.CommandAsync("show test", default));
        Assert.Equal("show test\n", channel.Sent[^1]);
    }
    [Fact] public async Task TimeoutIsBounded()
    {
        var channel = new FakeTerminal(); var cli = new CliConversation(channel) { Timeout = TimeSpan.FromMilliseconds(80) };
        await Assert.ThrowsAsync<TimeoutException>(() => cli.InitializeAsync(default));
    }
    [Fact] public async Task SlowCommandOutputKeepsTheConversationAliveUntilItsPrompt()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel);
        await cli.InitializeAsync(default);
        cli.Timeout = TimeSpan.FromMilliseconds(300);
        // Each chunk arrives on a separate receive iteration, like a long switchport
        // report over a 9600-baud console. Total duration exceeds the idle timeout.
        channel.OnSend = _ => [.. Enumerable.Repeat("Name: Gi0/1\r\nSwitchport: Enabled\r\n", 30), "SW#"];

        var output = await cli.CommandAsync("show interfaces switchport", default);

        Assert.Equal(30, output.Split("Switchport: Enabled").Length - 1);
        Assert.DoesNotContain("SW#", output);
        channel.OnSend = _ => ["next response\nSW#"];
        Assert.Equal("next response", await cli.CommandAsync("show vlan brief", default));
    }
    [Fact] public async Task StalledCommandOutputStillTimesOut()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel);
        await cli.InitializeAsync(default);
        cli.Timeout = TimeSpan.FromMilliseconds(300);
        channel.OnSend = _ => ["incomplete report\n"];

        await Assert.ThrowsAsync<TimeoutException>(() => cli.CommandAsync("show interfaces switchport", default));
    }
    [Fact] public async Task SilentCommandDoesNotSendAnExtraEnter()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel);
        await cli.InitializeAsync(default);
        cli.Timeout = TimeSpan.FromMilliseconds(1800);
        channel.OnSend = command => command == "\n" ? ["SW#"] : [];

        await Assert.ThrowsAsync<TimeoutException>(() => cli.CommandAsync("show interfaces switchport", default));

        Assert.DoesNotContain("\n", channel.Sent);
    }
    [Fact] public async Task SilentStartupCanStillWakeTheTerminal()
    {
        var channel = new FakeTerminal { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel) { Timeout = TimeSpan.FromSeconds(3) };

        await cli.InitializeAsync(default);

        Assert.Equal("SW", cli.Hostname);
        Assert.Contains("\n", channel.Sent);
    }
    [Fact] public async Task StartupNoiseDoesNotExtendTheBaudProbeDeadline()
    {
        var channel = new FakeTerminal([.. Enumerable.Repeat("unreadable startup bytes", 100)]);
        var cli = new CliConversation(channel) { Timeout = TimeSpan.FromMilliseconds(300) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() => cli.InitializeAsync(deadline.Token));
    }
    [Fact] public async Task StreamingCommandCanStillBeCancelled()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel);
        await cli.InitializeAsync(default);
        cli.Timeout = TimeSpan.FromMilliseconds(300);
        channel.OnSend = _ => [.. Enumerable.Repeat("partial output\n", 100)];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.CommandAsync("show interfaces switchport", deadline.Token));
    }
    [Fact] public async Task CancellationIsObserved()
    {
        var channel = new FakeTerminal(); var cli = new CliConversation(channel);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.InitializeAsync(cts.Token));
    }
    [Fact] public async Task CancelledCommandIsNeverSent()
    {
        var channel = new FakeTerminal(); var cli = new CliConversation(channel);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.CommandAsync("shutdown", cts.Token));
        Assert.Empty(channel.Sent);
    }
    [Fact] public async Task OversizeResponseInvalidatesTheProtocol()
    {
        var channel = new FakeTerminal(new string('x', 8 * 1024 * 1024 + 1));
        await Assert.ThrowsAsync<CliProtocolException>(() => new CliConversation(channel).InitializeAsync(default));
    }
    [Fact] public async Task FragmentedAnsiAndMultiplePagesPreserveOutput()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel); await cli.InitializeAsync(default);
        var page = 0;
        channel.OnSend = command => command != " " ? ["show data\n\x1b[3", "2mfirst\x1b[0m\n--Mo", "re--"] :
            ++page == 1 ? ["\b\b\b\b\b\b\b\bsecond\n--More--"] : ["\b\b\b\b\b\b\b\bthird\nSW#"];
        var output = await cli.CommandAsync("show data", default);
        Assert.Equal(2, page); Assert.Contains("first", output); Assert.Contains("second", output); Assert.Contains("third", output); Assert.DoesNotContain('\x1b', output);
    }
    [Theory]
    [InlineData("% Cannot modify VLAN in VTP client mode")]
    [InlineData("% Failed to apply configuration")]
    [InlineData("% VLAN 30 does not exist")]
    public async Task MoreIosRejectionsAreNotSuccess(string text)
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel); await cli.InitializeAsync(default);
        channel.OnSend = _ => [text + "\nSW#"];
        await Assert.ThrowsAsync<CliException>(() => cli.CommandAsync("vlan 30", default));
    }
    [Fact] public async Task SyslogIsNotACommandRejection()
    {
        var channel = new FakeTerminal("SW#") { OnSend = _ => ["SW#"] };
        var cli = new CliConversation(channel); await cli.InitializeAsync(default);
        channel.OnSend = _ => ["%LINK-3-UPDOWN: Interface Fa0/2 changed state\nOK\nSW#"];
        Assert.Contains("OK", await cli.CommandAsync("show test", default));
    }
    private sealed class FakeTerminal(params string[] initial) : ITerminalChannel
    {
        private readonly Queue<string> queue = new(initial);
        public List<string> Sent { get; } = [];
        public Func<string, string[]> OnSend { get; set; } = _ => [];
        public bool IsOpen => true;
        public string ReadAvailable() => queue.TryDequeue(out var value) ? value : "";
        public void Send(string text) { Sent.Add(text); foreach (var chunk in OnSend(text)) queue.Enqueue(chunk); }
        public void Dispose() { }
    }
}
