using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Core.Simulation;

public enum ScenarioAction
{
    Set,
    Ramp,
    SimulateOn,
    SimulateOff,
    Fault,
    FaultClear,
    DeviceStart,
    DeviceStop,
    Log,
}

/// <summary>One timed step of a scenario.</summary>
public sealed record ScenarioStep(int Line, double AtSeconds, ScenarioAction Action, string? Tag = null, string? Value = null,
    string? From = null, double DurationSeconds = 0, string? FaultKind = null, double FaultValue = 0, int? FaultCode = null, double FaultJitter = 0)
{
    public string Describe() => Action switch
    {
        ScenarioAction.Set => $"set {Tag} = {Value}",
        ScenarioAction.Ramp => $"ramp {Tag}{(From != null ? " from " + From : "")} to {Value} over {DurationSeconds:0.###} s",
        ScenarioAction.SimulateOn => $"simulate {Tag} on",
        ScenarioAction.SimulateOff => $"simulate {Tag} off",
        ScenarioAction.Fault => $"fault {FaultKind} {FaultValue}{(FaultCode is { } c ? $" code {c}" : "")}",
        ScenarioAction.FaultClear => "fault clear",
        ScenarioAction.DeviceStart => "device start",
        ScenarioAction.DeviceStop => "device stop",
        _ => $"log {Value}",
    };
}

/// <summary>A parsed scenario: steps in time order, optionally repeating.</summary>
public sealed class ScenarioScript
{
    public const string Help = """
        # One step per line:  <time>  <action>
        # Times: 0, 12.5, 30s, 500ms, 1m30s, 2h. A leading + is relative to the previous step (+5s).
        # Actions:
        #   set <tag> = <value>
        #   ramp <tag> [from <value>] to <value> over <time>
        #   simulate <tag> on|off          (hand the tag back to its simulation, or hold it)
        #   fault delay <ms> [jitter <ms>] | fault drop <%> | fault error <%> [code <n>] | fault disconnect <%> | fault clear
        #   device stop | device start
        #   log <text>
        # Last line "repeat" runs the scenario again from the start.
        """;

    public const string Example = Help + """

        0       set Pump running = true
        0       ramp Tank level from 20 to 80 over 30s
        30s     log Tank full, pump trips
        30s     set Pump running = false
        +5s     fault error 50 code 4
        +10s    fault clear
        60s     ramp Tank level to 20 over 20s
        """;

    private static readonly Regex TimeToken = new(@"^\+?(?:\d+(?:\.\d+)?(?:ms|s|m|h)?)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TimePart = new(@"(\d+(?:\.\d+)?)(ms|s|m|h)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ScenarioScript(IReadOnlyList<ScenarioStep> steps, bool repeat)
    {
        Steps = steps;
        Repeat = repeat;
    }

    public IReadOnlyList<ScenarioStep> Steps { get; }
    public bool Repeat { get; }
    public double DurationSeconds => Steps.Count == 0 ? 0 : Steps.Max(s => s.AtSeconds + s.DurationSeconds);
    public IReadOnlyList<string> TagNames => Steps.Where(s => s.Tag != null).Select(s => s.Tag!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Parses a scenario. Throws <see cref="FormatException"/> naming the bad line.</summary>
    public static ScenarioScript Parse(string? text)
    {
        var steps = new List<ScenarioStep>();
        bool repeat = false;
        double previous = 0;
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int ln = i + 1;
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;
            if (repeat) throw new FormatException($"Line {ln}: 'repeat' must be the last line.");
            if (line.Equals("repeat", StringComparison.OrdinalIgnoreCase) || line.Equals("loop", StringComparison.OrdinalIgnoreCase))
            {
                repeat = true;
                continue;
            }

            int space = line.IndexOfAny([' ', '\t']);
            if (space < 0) throw new FormatException($"Line {ln}: expected '<time> <action>'.");
            var timeText = line[..space];
            if (!TimeToken.IsMatch(timeText)) throw new FormatException($"Line {ln}: '{timeText}' is not a time such as 0, 30s, 1m30s or +5s.");
            double t = ParseDuration(timeText.TrimStart('+'));
            double at = timeText.StartsWith('+') ? previous + t : t;
            previous = at;

            var step = ParseAction(line[(space + 1)..].Trim(), at, ln);
            steps.Add(step);
        }
        return new ScenarioScript(steps.OrderBy(s => s.AtSeconds).ToList(), repeat);
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }

    public static double ParseDuration(string text)
    {
        if (!TimeToken.IsMatch(text)) throw new FormatException($"'{text}' is not a time such as 500ms, 30s or 1m30s.");
        double total = 0;
        foreach (Match m in TimePart.Matches(text))
        {
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => v / 1000,
                "m" => v * 60,
                "h" => v * 3600,
                _ => v,
            };
        }
        return total;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    private static ScenarioStep ParseAction(string text, double at, int ln)
    {
        int space = text.IndexOfAny([' ', '\t']);
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        switch (verb)
        {
            case "set":
            {
                int eq = rest.IndexOf('=');
                if (eq <= 0) throw new FormatException($"Line {ln}: use 'set <tag> = <value>'.");
                var tag = Unquote(rest[..eq]);
                var value = Unquote(rest[(eq + 1)..]);
                if (tag.Length == 0) throw new FormatException($"Line {ln}: the tag name is empty.");
                return new ScenarioStep(ln, at, ScenarioAction.Set, tag, value);
            }
            case "ramp":
            {
                var m = Regex.Match(rest, @"^(?<tag>.+?)\s+(?:from\s+(?<from>\S+)\s+)?to\s+(?<to>\S+)\s+over\s+(?<dur>\S+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!m.Success) throw new FormatException($"Line {ln}: use 'ramp <tag> [from <value>] to <value> over <time>'.");
                double dur;
                try { dur = ParseDuration(m.Groups["dur"].Value); }
                catch (FormatException ex) { throw new FormatException($"Line {ln}: {ex.Message}"); }
                foreach (var g in new[] { "from", "to" })
                    if (m.Groups[g].Success && !double.TryParse(m.Groups[g].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        throw new FormatException($"Line {ln}: ramp values must be numbers, not '{m.Groups[g].Value}'.");
                return new ScenarioStep(ln, at, ScenarioAction.Ramp, Unquote(m.Groups["tag"].Value), m.Groups["to"].Value,
                    m.Groups["from"].Success ? m.Groups["from"].Value : null, dur);
            }
            case "simulate":
            {
                int last = rest.LastIndexOfAny([' ', '\t']);
                var state = last < 0 ? string.Empty : rest[(last + 1)..].ToLowerInvariant();
                if (last < 0 || state is not ("on" or "off")) throw new FormatException($"Line {ln}: use 'simulate <tag> on' or 'simulate <tag> off'.");
                return new ScenarioStep(ln, at, state == "on" ? ScenarioAction.SimulateOn : ScenarioAction.SimulateOff, Unquote(rest[..last]));
            }
            case "fault":
                return ParseFault(rest, at, ln);
            case "device":
                return rest.ToLowerInvariant() switch
                {
                    "start" => new ScenarioStep(ln, at, ScenarioAction.DeviceStart),
                    "stop" => new ScenarioStep(ln, at, ScenarioAction.DeviceStop),
                    _ => throw new FormatException($"Line {ln}: use 'device start' or 'device stop'."),
                };
            case "log":
                return new ScenarioStep(ln, at, ScenarioAction.Log, Value: rest);
            default:
                throw new FormatException($"Line {ln}: unknown action '{verb}'. Use set, ramp, simulate, fault, device or log.");
        }
    }

    private static ScenarioStep ParseFault(string rest, double at, int ln)
    {
        var words = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1 && words[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            return new ScenarioStep(ln, at, ScenarioAction.FaultClear);
        if (words.Length < 2) throw new FormatException($"Line {ln}: use e.g. 'fault error 50', 'fault delay 200' or 'fault clear'.");

        var kind = words[0].ToLowerInvariant();
        if (kind is not ("delay" or "drop" or "error" or "disconnect"))
            throw new FormatException($"Line {ln}: unknown fault '{words[0]}'. Use delay, drop, error, disconnect or clear.");
        if (!double.TryParse(words[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0
            || (kind != "delay" && value > 100))
            throw new FormatException($"Line {ln}: '{words[1]}' is not a valid {(kind == "delay" ? "delay in ms" : "percentage (0-100)")}.");

        int? code = null;
        double jitter = 0;
        for (int i = 2; i + 1 < words.Length; i += 2)
        {
            var key = words[i].ToLowerInvariant();
            if (key == "code" && kind == "error" && int.TryParse(words[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c is >= 1 and <= 65535) code = c;
            else if (key == "jitter" && kind == "delay" && double.TryParse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var j) && j >= 0) jitter = j;
            else throw new FormatException($"Line {ln}: unexpected '{words[i]} {words[i + 1]}'.");
        }
        if ((words.Length - 2) % 2 != 0) throw new FormatException($"Line {ln}: '{words[^1]}' needs a value.");
        return new ScenarioStep(ln, at, ScenarioAction.Fault, FaultKind: kind, FaultValue: value, FaultCode: code, FaultJitter: jitter);
    }
}

/// <summary>What a scenario acts on: the device's tags, faults and server.</summary>
public interface IScenarioHost
{
    SimTag? FindTag(string nameOrAddress);
    FaultSettings Faults { get; }
    /// <summary>Starts or stops the device's server; called on a worker thread.</summary>
    Task SetDeviceRunningAsync(bool running);
    void Log(string message);
}

/// <summary>Runs a <see cref="ScenarioScript"/> against a device in real time (scaled by speed).</summary>
public sealed class ScenarioRunner : IDisposable
{
    private sealed record Ramp(SimTag Tag, double From, double To, double StartSeconds, double DurationSeconds);

    private readonly ScenarioScript _script;
    private readonly IScenarioHost _host;
    private readonly HashSet<SimTag> _held = [];
    private readonly List<Ramp> _ramps = [];
    private FaultSettings? _savedFaults;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _elapsedTicks;
    private volatile string _current = string.Empty;

    public ScenarioRunner(ScenarioScript script, IScenarioHost host, double speed = 1)
    {
        _script = script;
        _host = host;
        Speed = Math.Clamp(speed, 0.01, 1000);
        MissingTags = script.TagNames.Where(n => host.FindTag(n) == null).ToList();
    }

    public double Speed { get; }
    public IReadOnlyList<string> MissingTags { get; }
    public bool IsRunning => _task is { IsCompleted: false };
    public double ElapsedSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks)).TotalSeconds;
    public double DurationSeconds => _script.DurationSeconds;
    public int Loops { get; private set; }
    public int Errors { get; private set; }

    /// <summary>The last step that ran.</summary>
    public string CurrentStep => _current;

    /// <summary>Raised (on a worker thread) when the scenario ends by itself or fails.</summary>
    public event Action<ScenarioRunner, Exception?>? Completed;

    public void Start()
    {
        if (_task != null) return;
        if (MissingTags.Count > 0) throw new InvalidOperationException($"Unknown tag(s): {string.Join(", ", MissingTags)}.");
        _savedFaults = Copy(_host.Faults);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct), ct);
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { await (_task ?? Task.CompletedTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
        Finish();
    }

    private void Finish()
    {
        lock (_held)
        {
            foreach (var t in _held) t.IsReplaying = false;
            _held.Clear();
        }
        if (_savedFaults is { } f)
        {
            Restore(_host.Faults, f);
            _savedFaults = null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            do
            {
                var clock = Stopwatch.StartNew();
                double Now() => clock.Elapsed.TotalSeconds * Speed;
                int next = 0;
                var steps = _script.Steps;
                while (!ct.IsCancellationRequested)
                {
                    double now = Now();
                    Interlocked.Exchange(ref _elapsedTicks, TimeSpan.FromSeconds(now).Ticks);
                    while (next < steps.Count && steps[next].AtSeconds <= now)
                    {
                        await ExecuteAsync(steps[next], now).ConfigureAwait(false);
                        next++;
                    }
                    UpdateRamps(now);
                    if (next >= steps.Count && _ramps.Count == 0) break;

                    double nextDue = next < steps.Count ? steps[next].AtSeconds : double.MaxValue;
                    double waitScenario = _ramps.Count > 0 ? 0.05 * Speed : Math.Max(0, nextDue - now);
                    var wait = TimeSpan.FromSeconds(Math.Clamp(waitScenario / Speed, 0.01, 0.25));
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                if (_script.Repeat && !ct.IsCancellationRequested)
                {
                    Loops++;
                    _host.Log($"Scenario: repeat {Loops + 1}");
                    if (_script.DurationSeconds <= 0) await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.01, 1 / Speed)), ct).ConfigureAwait(false);
                }
            }
            while (_script.Repeat && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Finish();
        _host.Log(error == null ? "Scenario finished" : $"Scenario stopped: {error.Message}");
        Completed?.Invoke(this, error);
    }

    private void Hold(SimTag tag)
    {
        lock (_held)
        {
            if (_held.Add(tag)) tag.IsReplaying = true;
        }
    }

    private void Release(SimTag tag)
    {
        lock (_held)
        {
            if (_held.Remove(tag)) tag.IsReplaying = false;
        }
    }

    private async Task ExecuteAsync(ScenarioStep step, double now)
    {
        _current = $"line {step.Line}: {step.Describe()}";
        _host.Log($"Scenario {TimeSpan.FromSeconds(step.AtSeconds):mm\\:ss\\.f} · {step.Describe()}");
        var tag = step.Tag == null ? null : _host.FindTag(step.Tag);
        switch (step.Action)
        {
            case ScenarioAction.Set when tag?.Binding is { } b:
                _ramps.RemoveAll(r => r.Tag == tag);
                Hold(tag);
                try { b.Write(ValueConverter.Parse(step.Value!, b.DataType)); }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                {
                    Errors++;
                    _host.Log($"Scenario line {step.Line}: can't set {step.Tag} to '{step.Value}': {ex.Message}");
                }
                break;

            case ScenarioAction.Ramp when tag?.Binding is { } b:
                _ramps.RemoveAll(r => r.Tag == tag);
                Hold(tag);
                double from = step.From != null ? double.Parse(step.From, CultureInfo.InvariantCulture) : ValueConverter.ToDouble(b.Read());
                double to = double.Parse(step.Value!, CultureInfo.InvariantCulture);
                _ramps.Add(new Ramp(tag, from, to, step.AtSeconds, step.DurationSeconds));
                UpdateRamps(now);
                break;

            case ScenarioAction.SimulateOn when tag != null:
                _ramps.RemoveAll(r => r.Tag == tag);
                Release(tag);
                break;

            case ScenarioAction.SimulateOff when tag != null:
                Hold(tag);
                break;

            case ScenarioAction.Fault:
                var f = _host.Faults;
                switch (step.FaultKind)
                {
                    case "delay": f.ResponseDelayMs = (int)step.FaultValue; f.DelayJitterMs = (int)step.FaultJitter; break;
                    case "drop": f.DropResponsePercent = step.FaultValue; break;
                    case "error": f.ErrorResponsePercent = step.FaultValue; if (step.FaultCode is { } c) f.ErrorCode = c; break;
                    case "disconnect": f.DisconnectPercent = step.FaultValue; break;
                }
                break;

            case ScenarioAction.FaultClear:
                var faults = _host.Faults;
                faults.ResponseDelayMs = 0;
                faults.DelayJitterMs = 0;
                faults.DropResponsePercent = 0;
                faults.ErrorResponsePercent = 0;
                faults.DisconnectPercent = 0;
                break;

            case ScenarioAction.DeviceStart:
                await _host.SetDeviceRunningAsync(true).ConfigureAwait(false);
                break;

            case ScenarioAction.DeviceStop:
                await _host.SetDeviceRunningAsync(false).ConfigureAwait(false);
                break;

            case ScenarioAction.Log:
                _host.Log($"Scenario: {step.Value}");
                break;

            default:
                Errors++;
                _host.Log($"Scenario line {step.Line}: tag '{step.Tag}' isn't bound to the device (check its address)");
                break;
        }
    }

    private void UpdateRamps(double now)
    {
        for (int i = _ramps.Count - 1; i >= 0; i--)
        {
            var r = _ramps[i];
            var b = r.Tag.Binding;
            if (b == null) { _ramps.RemoveAt(i); continue; }
            double p = r.DurationSeconds <= 0 ? 1 : Math.Clamp((now - r.StartSeconds) / r.DurationSeconds, 0, 1);
            double v = r.From + (r.To - r.From) * p;
            try { b.Write(ValueConverter.FromDouble(v, b.DataType)); }
            catch (Exception) { Errors++; _ramps.RemoveAt(i); continue; }
            if (p >= 1) _ramps.RemoveAt(i);
        }
    }

    private static FaultSettings Copy(FaultSettings f) => new()
    {
        ResponseDelayMs = f.ResponseDelayMs,
        DelayJitterMs = f.DelayJitterMs,
        DropResponsePercent = f.DropResponsePercent,
        ErrorResponsePercent = f.ErrorResponsePercent,
        ErrorCode = f.ErrorCode,
        DisconnectPercent = f.DisconnectPercent,
    };

    private static void Restore(FaultSettings target, FaultSettings saved)
    {
        target.ResponseDelayMs = saved.ResponseDelayMs;
        target.DelayJitterMs = saved.DelayJitterMs;
        target.DropResponsePercent = saved.DropResponsePercent;
        target.ErrorResponsePercent = saved.ErrorResponsePercent;
        target.ErrorCode = saved.ErrorCode;
        target.DisconnectPercent = saved.DisconnectPercent;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        Finish();
    }
}
