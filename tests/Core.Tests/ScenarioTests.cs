using System.Collections.Concurrent;
using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Tests;

public sealed class ScenarioParseTests
{
    [Fact]
    public void ParsesTimesActionsAndRepeat()
    {
        var s = ScenarioScript.Parse("""
            # comment
            0        set Pump running = true     # trailing comment
            1m30s    ramp Tank level from 20 to 80 over 30s
            +500ms   simulate Tank level on
            2h       fault error 50 code 6
            2h       fault delay 200 jitter 50
            2h       fault clear
            2h       device stop
            +10s     device start
            2h20s    log "quoted # not a comment"
            repeat
            """);
        Assert.True(s.Repeat);
        Assert.Equal(9, s.Steps.Count);

        var ramp = s.Steps.Single(x => x.Action == ScenarioAction.Ramp);
        Assert.Equal(90, ramp.AtSeconds);
        Assert.Equal("Tank level", ramp.Tag);
        Assert.Equal("20", ramp.From);
        Assert.Equal("80", ramp.Value);
        Assert.Equal(30, ramp.DurationSeconds);

        Assert.Equal(90.5, s.Steps.Single(x => x.Action == ScenarioAction.SimulateOn).AtSeconds);
        var error = s.Steps.Single(x => x is { Action: ScenarioAction.Fault, FaultKind: "error" });
        Assert.Equal((50.0, 6), (error.FaultValue, error.FaultCode!.Value));
        Assert.Equal(7210, s.Steps.Single(x => x.Action == ScenarioAction.DeviceStart).AtSeconds);
        Assert.Equal("\"quoted # not a comment\"", s.Steps[^1].Value);
        Assert.Equal(["Pump running", "Tank level"], s.TagNames);
    }

    [Theory]
    [InlineData("soon set A = 1", "not a time")]
    [InlineData("0 set A 1", "set <tag> = <value>")]
    [InlineData("0 ramp A to x over 5s", "numbers")]
    [InlineData("0 ramp A to 5", "ramp <tag>")]
    [InlineData("0 simulate A maybe", "simulate <tag> on")]
    [InlineData("0 fault error 150", "percentage")]
    [InlineData("0 fault boom 1", "unknown fault")]
    [InlineData("0 explode", "unknown action")]
    [InlineData("repeat\n0 log x", "last line")]
    public void ReportsTheBadLine(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => ScenarioScript.Parse(text));
        Assert.StartsWith("Line ", ex.Message);
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void ExampleParses() => Assert.NotEmpty(ScenarioScript.Parse(ScenarioScript.Example).Steps);
}

public sealed class ScenarioRunTests
{
    private sealed class Host(params SimTag[] tags) : IScenarioHost
    {
        public FaultSettings Faults { get; } = new() { ErrorCode = 4 };
        public ConcurrentQueue<bool> DeviceCalls { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();

        public SimTag? FindTag(string n) => tags.FirstOrDefault(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase));

        public Task SetDeviceRunningAsync(bool running)
        {
            DeviceCalls.Enqueue(running);
            return Task.CompletedTask;
        }

        public void Log(string message) => Messages.Enqueue(message);
    }

    private static async Task RunToEnd(ScenarioRunner runner)
    {
        var done = new TaskCompletionSource<Exception?>();
        runner.Completed += (_, e) => done.TrySetResult(e);
        runner.Start();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetsRampsFaultsAndDeviceInOrder()
    {
        var store = new NamedTagStore();
        var pump = TagFactory.Tag(store, "Pump", TagDataType.Bool, false);
        var level = TagFactory.Tag(store, "Level", TagDataType.Float64, 0.0);
        var host = new Host(pump, level);
        var levels = new ConcurrentQueue<double>();
        store.ValueChanged += t => { if (t.Name == "Level") levels.Enqueue((double)t.Value); };

        var script = ScenarioScript.Parse("""
            0     set Pump = on
            0     ramp Level from 0 to 100 over 10s
            5s    fault error 25 code 9
            10s   device stop
            +1s   device start
            """);
        using var runner = new ScenarioRunner(script, host, speed: 20);
        var run = RunToEnd(runner);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        bool heldDuringRun = level.IsReplaying && pump.IsReplaying;
        await run;

        Assert.True(heldDuringRun);
        Assert.Equal(true, pump.Binding!.Read());
        Assert.Equal(100.0, level.Binding!.Read());
        Assert.True(levels.Count >= 3);                       // the ramp moved in steps
        Assert.Equal(levels.OrderBy(v => v), levels);         // ...always upwards
        Assert.Equal([false, true], host.DeviceCalls);
        // Faults are put back and tags handed back to their simulation when the scenario ends.
        Assert.Equal(0, host.Faults.ErrorResponsePercent);
        Assert.Equal(4, host.Faults.ErrorCode);
        Assert.False(level.IsReplaying);
        Assert.False(pump.IsReplaying);
    }

    [Fact]
    public async Task FaultsApplyWhileRunningAndStopRestoresThem()
    {
        var host = new Host();
        var script = ScenarioScript.Parse("0 fault drop 40\n0 fault delay 300 jitter 20\n10m log never");
        using var runner = new ScenarioRunner(script, host, speed: 1);
        runner.Start();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(40, host.Faults.DropResponsePercent);
        Assert.Equal(300, host.Faults.ResponseDelayMs);
        Assert.Equal(20, host.Faults.DelayJitterMs);
        Assert.True(runner.IsRunning);

        await runner.StopAsync();
        Assert.Equal(0, host.Faults.DropResponsePercent);
        Assert.Equal(0, host.Faults.ResponseDelayMs);
    }

    [Fact]
    public async Task RepeatsAndCountsBadValues()
    {
        var store = new NamedTagStore();
        var n = TagFactory.Tag(store, "N", TagDataType.Int16, 0);
        var host = new Host(n);
        using var runner = new ScenarioRunner(ScenarioScript.Parse("0 set N = 1\n0.1 set N = banana\n0.2 set N = 2\nrepeat"), host, speed: 5);
        runner.Start();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(runner.Loops >= 2);
        Assert.True(runner.Errors >= 2);
        await runner.StopAsync();
        Assert.Contains(host.Messages, m => m.Contains("banana"));
    }

    [Fact]
    public void RefusesToStartWithUnknownTags()
    {
        using var runner = new ScenarioRunner(ScenarioScript.Parse("0 set Ghost = 1"), new Host());
        Assert.Equal(["Ghost"], runner.MissingTags);
        Assert.Throws<InvalidOperationException>(runner.Start);
    }

    [Fact]
    public async Task SimulateOnHandsTheTagBack()
    {
        var store = new NamedTagStore();
        var t = TagFactory.Tag(store, "T", TagDataType.Int32, 0);
        var host = new Host(t);
        using var runner = new ScenarioRunner(ScenarioScript.Parse("0 set T = 5\n0.1 simulate T on\n1 log end"), host, speed: 1);
        runner.Start();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(t.IsReplaying);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(t.IsReplaying);
        await runner.StopAsync();
    }
}
