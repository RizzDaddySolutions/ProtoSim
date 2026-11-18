using ProtoSim.Core.Data;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Tests;

internal static class TagFactory
{
    public static SimTag Tag(NamedTagStore store, string name, TagDataType type, object? value = null)
    {
        var tag = new SimTag { Name = name, Address = name, DataType = type, Binding = store.Bind(name, type) };
        if (value != null) tag.Binding.Write(value);
        return tag;
    }
}

public sealed class RecordingCsvTests
{
    [Fact]
    public void RoundTripsValuesWithCommasAndQuotes()
    {
        var rec = new TagRecording([
            new RecordedSample(0, "Temp", "21.5"),
            new RecordedSample(1.25, "Label", "a, \"quoted\" value"),
            new RecordedSample(2, "Flag", "True"),
        ], new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var sw = new StringWriter();
        RecordingCsv.Write(sw, rec);
        var back = RecordingCsv.Read(new StringReader(sw.ToString()));

        Assert.Equal(rec.Samples, back.Samples);
        Assert.Equal(2, back.DurationSeconds);
        Assert.Equal(["Temp", "Label", "Flag"], back.TagNames);
    }

    [Fact]
    public void ImportsTimestampedRowsAsOffsets()
    {
        var csv = """
            time,tag,value
            2026-03-01T08:00:00Z,Flow,10
            2026-03-01T08:00:30Z,Flow,12.5
            2026-03-01T08:01:00Z,Valve,open, partly
            """;
        var rec = RecordingCsv.Read(new StringReader(csv));
        Assert.Equal([0.0, 30.0, 60.0], rec.Samples.Select(s => s.OffsetSeconds));
        Assert.Equal("open, partly", rec.Samples[2].Value);
        Assert.Equal(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), rec.StartedUtc);
    }

    [Theory]
    [InlineData("0,Temp")]
    [InlineData("abc,Temp,1\nxyz,Temp,2")]
    [InlineData("1,,5")]
    [InlineData("-1,Temp,5")]
    public void RejectsBadRows(string csv) =>
        Assert.Throws<FormatException>(() => RecordingCsv.Read(new StringReader(csv)));
}

public sealed class RecorderTests
{
    [Fact]
    public async Task KeepsOnlyChanges()
    {
        var store = new NamedTagStore();
        var temp = TagFactory.Tag(store, "Temp", TagDataType.Float32, 1f);
        var flag = TagFactory.Tag(store, "Flag", TagDataType.Bool, false);
        using var recorder = new TagRecorder(() => [temp, flag]);

        recorder.SampleNow();
        recorder.SampleNow();               // nothing changed
        temp.Binding!.Write(2f);
        recorder.SampleNow();
        flag.Binding!.Write(true);
        temp.Binding.Write(2f);             // same value again
        recorder.SampleNow();

        var rec = await recorder.StopAsync();
        Assert.Equal(["Temp=1", "Flag=false", "Temp=2", "Flag=true"], rec.Samples.Select(s => $"{s.Tag}={s.Value}"));
    }

    [Fact]
    public async Task RecordsWhileRunning()
    {
        var store = new NamedTagStore();
        var temp = TagFactory.Tag(store, "Temp", TagDataType.Int32, 0);
        using var recorder = new TagRecorder(() => [temp], 10);
        recorder.Start();
        for (int i = 1; i <= 3; i++)
        {
            await Task.Delay(60, TestContext.Current.CancellationToken);
            temp.Binding!.Write(i);
        }
        await Task.Delay(60, TestContext.Current.CancellationToken);
        var rec = await recorder.StopAsync();
        Assert.Equal(["0", "1", "2", "3"], rec.Samples.Select(s => s.Value));
        Assert.True(rec.DurationSeconds > 0.1);
    }
}

public sealed class ReplayerTests
{
    private static TagRecording Recording(params (double T, string Tag, string Value)[] rows) =>
        new(rows.Select(r => new RecordedSample(r.T, r.Tag, r.Value)));

    [Fact]
    public async Task PlaysBackInOrderAndPausesSimulation()
    {
        var store = new NamedTagStore();
        var level = TagFactory.Tag(store, "Level", TagDataType.Float64, 0.0);
        level.Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Ramp, Min = 500, Max = 600, IntervalMs = 10 };
        var seen = new List<object>();
        store.ValueChanged += t => { lock (seen) seen.Add(t.Value); };

        using var replayer = new TagReplayer(Recording((0, "Level", "1"), (1, "Level", "2"), (2, "Level", "3")),
            n => n == "Level" ? level : null, speed: 20);
        var done = new TaskCompletionSource();
        replayer.Completed += _ => done.TrySetResult();
        replayer.Start();
        Assert.True(level.IsReplaying);

        // While replaying, the simulation engine leaves the tag alone.
        SimulationEngine.Step(level, 100);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(3.0, level.Binding!.Read());
        lock (seen) Assert.Equal([1.0, 2.0, 3.0], seen);
        Assert.False(level.IsReplaying);
        Assert.Equal(0, replayer.Errors);
    }

    [Fact]
    public async Task LoopsUntilStoppedAndReportsMissingTags()
    {
        var store = new NamedTagStore();
        var count = TagFactory.Tag(store, "Count", TagDataType.Int32, 0);
        using var replayer = new TagReplayer(Recording((0, "Count", "1"), (0.05, "Count", "2"), (0.05, "Ghost", "9")),
            n => n == "Count" ? count : null, speed: 1, loop: true);
        Assert.Equal(["Ghost"], replayer.MissingTags);

        replayer.Start();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.True(replayer.IsRunning);
        Assert.True(replayer.Loops >= 2);
        await replayer.StopAsync();
        Assert.False(replayer.IsRunning);
        Assert.False(count.IsReplaying);
    }

    [Fact]
    public async Task CountsValuesThatDontFit()
    {
        var store = new NamedTagStore();
        var n = TagFactory.Tag(store, "N", TagDataType.Int16, 0);
        using var replayer = new TagReplayer(Recording((0, "N", "not a number"), (0, "N", "5")), _ => n, speed: 10);
        var done = new TaskCompletionSource();
        replayer.Completed += _ => done.TrySetResult();
        replayer.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, replayer.Errors);
        Assert.Equal((short)5, n.Binding!.Read());
    }
}
