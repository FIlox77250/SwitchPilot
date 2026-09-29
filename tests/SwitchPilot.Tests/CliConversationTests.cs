using SwitchPilot.Infrastructure.Ssh;

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
