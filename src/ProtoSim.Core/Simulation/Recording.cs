using System.Diagnostics;
using System.Globalization;
using System.Text;
using ProtoSim.Core.Data;

namespace ProtoSim.Core.Simulation;

/// <summary>One recorded value: seconds from the start of the recording, the tag name, and the value as text.</summary>
public readonly record struct RecordedSample(double OffsetSeconds, string Tag, string Value);

/// <summary>A time series of tag values, recorded from a device or imported from CSV.</summary>
public sealed class TagRecording
{
    public TagRecording(IEnumerable<RecordedSample> samples, DateTime? startedUtc = null)
    {
        Samples = samples.OrderBy(s => s.OffsetSeconds).ToList();
        StartedUtc = startedUtc;
    }

    public IReadOnlyList<RecordedSample> Samples { get; }
    public DateTime? StartedUtc { get; }
    public double DurationSeconds => Samples.Count == 0 ? 0 : Samples[^1].OffsetSeconds;
    public IReadOnlyList<string> TagNames => Samples.Select(s => s.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Reads and writes recordings as CSV: <c>offset_s,tag,value</c>. The first column may instead hold
/// timestamps (ISO 8601 or any parseable date/time), which become offsets from the first row, so
/// historian exports can be replayed. Lines starting with # are comments.
/// </summary>
public static class RecordingCsv
{
    public const string Header = "offset_s,tag,value";

    public static void Write(string path, TagRecording recording)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        Write(w, recording);
    }

    public static void Write(TextWriter w, TagRecording recording)
    {
        w.WriteLine($"# ProtoSim recording{(recording.StartedUtc is { } t ? ", started " + t.ToString("o", CultureInfo.InvariantCulture) : "")}");
        w.WriteLine(Header);
        foreach (var s in recording.Samples)
            w.WriteLine($"{s.OffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture)},{Quote(s.Tag)},{Quote(s.Value)}");
    }

    public static TagRecording Read(string path)
    {
        using var r = new StreamReader(path, Encoding.UTF8, true);
        return Read(r);
    }

    public static TagRecording Read(TextReader reader)
    {
        var rows = new List<(string Time, string Tag, string Value, int Line)>();
        string? line;
        int n = 0;
        while ((line = reader.ReadLine()) != null)
        {
            n++;
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var f = Split(line);
            if (f.Count < 3) throw new FormatException($"Line {n}: expected time,tag,value.");
            rows.Add((f[0].Trim(), f[1].Trim(), f[2], n));
        }

        // A header row has a first column that is neither a number nor a date.
        if (rows.Count > 0 && !IsNumber(rows[0].Time) && !IsDate(rows[0].Time, out _)) rows.RemoveAt(0);
        if (rows.Count == 0) return new TagRecording([]);

        bool numeric = IsNumber(rows[0].Time);
        DateTime? first = null;
        var samples = new List<RecordedSample>(rows.Count);
        foreach (var (time, tag, value, line2) in rows)
        {
            if (tag.Length == 0) throw new FormatException($"Line {line2}: the tag name is empty.");
            double offset;
            if (numeric)
            {
                if (!double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out offset) || offset < 0)
                    throw new FormatException($"Line {line2}: '{time}' is not a time offset in seconds.");
            }
            else
            {
                if (!IsDate(time, out var t)) throw new FormatException($"Line {line2}: '{time}' is not a date/time.");
                first ??= t;
                offset = (t - first.Value).TotalSeconds;
                if (offset < 0) throw new FormatException($"Line {line2}: timestamps must not go backwards before the first row.");
            }
            samples.Add(new RecordedSample(offset, tag, value));
        }
        return new TagRecording(samples, first?.ToUniversalTime());
    }

    private static bool IsNumber(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool IsDate(string s, out DateTime t) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 || s.Length != s.Trim().Length ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        // Values may contain unquoted commas: everything after the second comma is the value.
        if (fields.Count > 3) fields = [fields[0], fields[1], string.Join(",", fields.Skip(2))];
        return fields;
    }
}

/// <summary>Records tag values by sampling them and keeping only changes.</summary>
public sealed class TagRecorder : IDisposable
{
    public const int MaxSamples = 5_000_000;

    private readonly Func<IReadOnlyList<SimTag>> _tags;
    private readonly int _intervalMs;
    private readonly List<RecordedSample> _samples = [];
    private readonly Dictionary<SimTag, string> _last = [];
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _startedUtc;

    /// <param name="tags">Called on every sample, so tags added while recording are included.</param>
    public TagRecorder(Func<IReadOnlyList<SimTag>> tags, int intervalMs = 100)
    {
        _tags = tags;
        _intervalMs = Math.Clamp(intervalMs, 10, 60_000);
    }

    public bool IsRecording => _loop != null;
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    public int Count { get { lock (_samples) return _samples.Count; } }
    public bool IsFull => Count >= MaxSamples;

    public void Start()
    {
        if (_loop != null) return;
        _startedUtc = DateTime.UtcNow;
        _clock.Restart();
        SampleNow();
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    /// <summary>Stops and returns everything recorded.</summary>
    public async Task<TagRecording> StopAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try { await (_loop ?? Task.CompletedTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
            SampleNow();
            _clock.Stop();
        }
        lock (_samples) return new TagRecording(_samples.ToList(), _startedUtc);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) SampleNow();
    }

    /// <summary>Takes one sample of every tag. Public for testing.</summary>
    public void SampleNow()
    {
        double t = Math.Round(_clock.Elapsed.TotalSeconds, 3);
        foreach (var tag in _tags())
        {
            var binding = tag.Binding;
            if (binding == null) continue;
            string value;
            try { value = ValueConverter.Format(binding.Read()); }
            catch (Exception) { continue; }
            lock (_samples)
            {
                if (_samples.Count >= MaxSamples) return;
                if (_last.TryGetValue(tag, out var last) && last == value) continue;
                _last[tag] = value;
                _samples.Add(new RecordedSample(t, tag.Name, value));
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>Plays a recording back into tags at its original pace (scaled by speed), optionally looping.</summary>
public sealed class TagReplayer : IDisposable
{
    private readonly TagRecording _recording;
    private readonly Func<string, SimTag?> _resolve;
    private readonly bool _loop;
    private readonly List<SimTag> _held = [];
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _positionTicks;

    /// <param name="resolve">Finds the device tag for a recorded tag name (null = not on this device).</param>
    public TagReplayer(TagRecording recording, Func<string, SimTag?> resolve, double speed = 1, bool loop = false)
    {
        _recording = recording;
        _resolve = resolve;
        Speed = Math.Clamp(speed, 0.01, 1000);
        _loop = loop;
        MissingTags = recording.TagNames.Where(n => resolve(n) == null).ToList();
    }

    public double Speed { get; }
    public bool Loop => _loop;
    public double DurationSeconds => _recording.DurationSeconds;

    /// <summary>Recorded tag names with no matching tag on the device; their samples are skipped.</summary>
    public IReadOnlyList<string> MissingTags { get; }

    public bool IsRunning => _task is { IsCompleted: false };
    public double PositionSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks)).TotalSeconds;
    public int Loops { get; private set; }
    public int Errors { get; private set; }

    /// <summary>Raised (on a worker thread) when playback ends without looping, or fails.</summary>
    public event Action<TagReplayer>? Completed;

    public void Start()
    {
        if (_task != null) return;
        foreach (var name in _recording.TagNames)
        {
            if (_resolve(name) is { } tag)
            {
                tag.IsReplaying = true;
                _held.Add(tag);
            }
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct), ct);
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { await (_task ?? Task.CompletedTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
        Release();
    }

    private void Release()
    {
        foreach (var t in _held) t.IsReplaying = false;
        _held.Clear();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var samples = _recording.Samples;
            do
            {
                var clock = Stopwatch.StartNew();
                foreach (var s in samples)
                {
                    var due = TimeSpan.FromSeconds(s.OffsetSeconds / Speed);
                    var wait = due - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Exchange(ref _positionTicks, TimeSpan.FromSeconds(s.OffsetSeconds).Ticks);
                    Apply(s);
                }
                if (_loop)
                {
                    Loops++;
                    // Keep the loop seam at the recording's own pace: a 1-sample recording still waits a beat.
                    if (DurationSeconds <= 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(10, 1000 / Speed)), ct).ConfigureAwait(false);
                }
            }
            while (_loop && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!ct.IsCancellationRequested) Release();
        }
        Completed?.Invoke(this);
    }

    private void Apply(RecordedSample s)
    {
        var tag = _resolve(s.Tag);
        var binding = tag?.Binding;
        if (binding == null) return;
        try
        {
            binding.Write(ValueConverter.Parse(s.Value, binding.DataType));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            Errors++;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        Release();
    }
}
