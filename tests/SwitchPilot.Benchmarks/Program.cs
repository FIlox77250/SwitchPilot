using System.Diagnostics;
using System.Text.Json;
using SwitchPilot.Core;
using SwitchPilot.Infrastructure.Cisco;
using SwitchPilot.Infrastructure.Ssh;

var channel = new BenchChannel();
var cli = new CliConversation(channel);
await cli.InitializeAsync(default);
GC.Collect();
var allocated = GC.GetTotalAllocatedBytes(true);
var clock = Stopwatch.StartNew();
var text = await cli.CommandAsync("show benchmark", default);
var cliElapsed = clock.Elapsed.TotalMilliseconds;
var cliBytes = GC.GetTotalAllocatedBytes(true) - allocated;
var session = new BenchSession();
await using var driver = new CiscoIosDriver(session, new NullAudit());
await driver.ReadSnapshotAsync(); // warm up and identify the switch once
session.Count = 0;
clock.Restart();
for (var i = 0; i < 10; i++)
{
    await driver.ReadDetectionAsync("001122334455");
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    CliPayloadChars = text.Length, CliElapsedMs = Math.Round(cliElapsed, 2), CliAllocatedBytes = cliBytes,
    DetectionIterations = 10, DetectionCommands = session.Count, DetectionElapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 2),
    SimulatedLatencyPerCommandMs = 10
}, new JsonSerializerOptions { WriteIndented = true }));

sealed class BenchChannel : ITerminalChannel
{
    private readonly Queue<string> chunks = new(["SW#"]);
    public bool IsOpen => true;
    public string ReadAvailable() => chunks.TryDequeue(out var s) ? s : "";
    public void Send(string text)
    {
        if (text != "show benchmark\n") { chunks.Enqueue("SW#"); return; }
        chunks.Enqueue(text);
        for (var i = 0; i < 64; i++) chunks.Enqueue(new string('x', 16383) + "\n");
        chunks.Enqueue("SW#");
    }
    public void Dispose() { }
}
sealed class NullAudit : IAuditSink { public void Write(string a, string b) { } }
sealed class BenchSession : ICliSession
{
    public int Count;
    public bool IsConnected => true;
    public string Hostname => "SW";
    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        Count++; await Task.Delay(10, cancellationToken);
        var file = command switch
        {
            "show interfaces status" => "interfaces-status.txt",
            "show vlan brief" => "vlans.txt",
            _ when command.StartsWith("show mac") => "mac-table.txt",
            _ => null
        };
        return file is not null ? File.ReadAllText(Path.Combine("tests/SwitchPilot.Tests/Fixtures", file)) :
            command == "show version" ? "Cisco IOS, Version 15.2(4)E\nModel number : WS-C2960+24TC-L" :
            command.EndsWith("switchport") ? "Name: Fa0/14\nAdministrative Mode: static access\nOperational Mode: static access" : "";
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
