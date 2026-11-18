using System.Globalization;
using ProtoSim.Core.Data;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Logic;

public enum PortKind
{
    Bool,
    Number,
}

public enum ParamKind
{
    Number,
    Bool,
    Text,
    /// <summary>A "Device/Tag" reference.</summary>
    Tag,
    Choice,
}

/// <param name="FallbackParam">When the input isn't wired, this parameter supplies its value.</param>
/// <param name="Default">Value of an unwired input with no fallback parameter.</param>
public sealed record PortDef(string Name, PortKind Kind, string? FallbackParam = null, double Default = 0, string Help = "");

public sealed record ParamDef(string Name, ParamKind Kind, string Default, string Help = "", string[]? Choices = null);

/// <summary>A block type in the palette.</summary>
public sealed class BlockDef(string type, string displayName, string category, string description,
    PortDef[] inputs, PortDef[] outputs, ParamDef[] parameters, Func<BlockRuntime> create)
{
    public string Type { get; } = type;
    public string DisplayName { get; } = displayName;
    public string Category { get; } = category;
    public string Description { get; } = description;
    public IReadOnlyList<PortDef> Inputs { get; } = inputs;
    public IReadOnlyList<PortDef> Outputs { get; } = outputs;
    public IReadOnlyList<ParamDef> Parameters { get; } = parameters;
    internal Func<BlockRuntime> Create { get; } = create;

    public int InputIndex(string name) => IndexOf(Inputs, name);
    public int OutputIndex(string name) => IndexOf(Outputs, name);

    private static int IndexOf(IReadOnlyList<PortDef> ports, string name)
    {
        for (int i = 0; i < ports.Count; i++)
            if (string.Equals(ports[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>What a block sees during one scan.</summary>
public sealed class ScanContext
{
    /// <summary>Seconds since the previous scan of this program.</summary>
    public double Dt { get; internal set; }

    /// <summary>Seconds since the program started.</summary>
    public double Time { get; internal set; }

    public required ITagDirectory Tags { get; init; }
}

/// <summary>Executable state of one block. Inputs and outputs are numbers; Bool signals are 0 or 1.</summary>
public abstract class BlockRuntime : IDisposable
{
    private readonly Dictionary<string, double> _numbers = new(StringComparer.Ordinal);

    public LogicBlock Block { get; internal set; } = null!;
    public BlockDef Def { get; internal set; } = null!;
    public double[] In { get; internal set; } = [];
    public double[] Out { get; internal set; } = [];

    /// <summary>A problem with this block (bad parameter, unknown tag...), shown on the canvas.</summary>
    public string? Error { get; protected internal set; }

    /// <summary>Called after creation and whenever parameters change. State (timers, counters...) is kept.</summary>
    public virtual void Configure()
    {
        Error = null;
        _numbers.Clear();
        foreach (var p in Def.Parameters.Where(p => p.Kind == ParamKind.Number))
        {
            var text = Block.Param(p.Name, p.Default);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                Error = $"{p.Name}: '{text}' is not a number";
                v = double.Parse(p.Default, CultureInfo.InvariantCulture);
            }
            _numbers[p.Name] = v;
        }
    }

    public abstract void Execute(ScanContext ctx);

    /// <summary>Clears time-based state when the program is (re)started.</summary>
    public virtual void Reset() { }

    public virtual void Dispose() { }

    protected double P(string name) => _numbers.TryGetValue(name, out var v) ? v : 0;
    protected string S(string name) => Block.Param(name, Def.Parameters.FirstOrDefault(p => p.Name == name)?.Default ?? string.Empty);
    protected bool PB(string name) => S(name).Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
    protected bool B(int input) => In[input] != 0;
    protected static double Bit(bool b) => b ? 1 : 0;
}

/// <summary>The built-in block types.</summary>
public static class BlockLibrary
{
    private static readonly PortDef[] Q = [new("Q", PortKind.Bool)];
    private static readonly PortDef[] OutN = [new("Out", PortKind.Number)];

    private static double Bit(bool b) => b ? 1 : 0;
    private static PortDef BIn(string n, double def = 0) => new(n, PortKind.Bool, Default: def);
    private static PortDef NIn(string n, string? fallback = null, double def = 0) => new(n, PortKind.Number, fallback, def);
    private static ParamDef Num(string n, string def, string help = "") => new(n, ParamKind.Number, def, help);

    public static IReadOnlyList<BlockDef> All { get; } =
    [
        // ---- Tags & constants ----
        new("TAG_IN", "Tag in", "Tags", "Reads a tag from any device in the project.",
            [], [new("Value", PortKind.Number)], [new("Tag", ParamKind.Tag, "", "Device/Tag")], () => new TagInBlock()),
        new("TAG_OUT", "Tag out", "Tags", "Writes a tag on any device. The tag's own simulation pauses while the program runs. Writes only when the value changes.",
            [NIn("In"), BIn("EN", 1)], [], [new("Tag", ParamKind.Tag, "", "Device/Tag")], () => new TagOutBlock()),
        new("CONST", "Constant", "Tags", "A fixed number (use 1/0 for true/false).",
            [], OutN, [Num("Value", "0")], () => new ConstBlock()),
        new("CLOCK", "Clock", "Tags", "Pulses: true for Duty % of every Period.",
            [], Q, [Num("Period", "1000", "ms"), Num("Duty", "50", "%")], () => new ClockBlock()),

        // ---- Bit logic ----
        new("AND", "AND", "Logic", "True when every wired input is true (unwired inputs are ignored).",
            [BIn("A", 1), BIn("B", 1), BIn("C", 1), BIn("D", 1)], Q, [], () => new Fn(i => Bit(i[0] != 0 && i[1] != 0 && i[2] != 0 && i[3] != 0))),
        new("OR", "OR", "Logic", "True when any input is true.",
            [BIn("A"), BIn("B"), BIn("C"), BIn("D")], Q, [], () => new Fn(i => Bit(i[0] != 0 || i[1] != 0 || i[2] != 0 || i[3] != 0))),
        new("XOR", "XOR", "Logic", "True when exactly one input is true.",
            [BIn("A"), BIn("B")], Q, [], () => new Fn(i => Bit((i[0] != 0) ^ (i[1] != 0)))),
        new("NOT", "NOT", "Logic", "Inverts the input.",
            [BIn("In")], Q, [], () => new Fn(i => Bit(i[0] == 0))),
        new("SR", "SR latch", "Logic", "Set-dominant latch: S1 sets Q1, R resets it.",
            [BIn("S1"), BIn("R")], [new("Q1", PortKind.Bool)], [], () => new LatchBlock(setDominant: true)),
        new("RS", "RS latch", "Logic", "Reset-dominant latch: S sets Q1, R1 resets it.",
            [BIn("S"), BIn("R1")], [new("Q1", PortKind.Bool)], [], () => new LatchBlock(setDominant: false)),
        new("R_TRIG", "Rising edge", "Logic", "True for one scan when CLK goes from false to true.",
            [BIn("CLK")], Q, [], () => new EdgeBlock(rising: true)),
        new("F_TRIG", "Falling edge", "Logic", "True for one scan when CLK goes from true to false.",
            [BIn("CLK")], Q, [], () => new EdgeBlock(rising: false)),

        // ---- Compare ----
        new("GT", "A > B", "Compare", "", [NIn("A"), NIn("B", "B")], Q, [Num("B", "0", "used when B isn't wired")], () => new Fn(i => Bit(i[0] > i[1]))),
        new("GE", "A ≥ B", "Compare", "", [NIn("A"), NIn("B", "B")], Q, [Num("B", "0", "used when B isn't wired")], () => new Fn(i => Bit(i[0] >= i[1]))),
        new("LT", "A < B", "Compare", "", [NIn("A"), NIn("B", "B")], Q, [Num("B", "0", "used when B isn't wired")], () => new Fn(i => Bit(i[0] < i[1]))),
        new("LE", "A ≤ B", "Compare", "", [NIn("A"), NIn("B", "B")], Q, [Num("B", "0", "used when B isn't wired")], () => new Fn(i => Bit(i[0] <= i[1]))),
        new("EQ", "A = B", "Compare", "Equal within 1e-9.", [NIn("A"), NIn("B", "B")], Q, [Num("B", "0", "used when B isn't wired")], () => new Fn(i => Bit(Math.Abs(i[0] - i[1]) < 1e-9))),
        new("NE", "A ≠ B", "Compare", "", [NIn("A"), NIn("B", "B")], Q, [Num("B", "0", "used when B isn't wired")], () => new Fn(i => Bit(Math.Abs(i[0] - i[1]) >= 1e-9))),
        new("HYST", "Hysteresis", "Compare",
            "On/off switch with a dead band. On ≥ Off: Q turns on when In ≥ On and off when In ≤ Off (high alarm). On < Off: Q turns on when In ≤ On and off when In ≥ Off (start a pump when low).",
            [NIn("In")], Q, [Num("On", "80"), Num("Off", "70")], () => new HysteresisBlock()),

        // ---- Math ----
        new("ADD", "Add", "Math", "A + B + C + D.", [NIn("A"), NIn("B"), NIn("C"), NIn("D")], OutN, [], () => new Fn(i => i[0] + i[1] + i[2] + i[3])),
        new("SUB", "Subtract", "Math", "A − B.", [NIn("A"), NIn("B", "B")], OutN, [Num("B", "0")], () => new Fn(i => i[0] - i[1])),
        new("MUL", "Multiply", "Math", "A × B.", [NIn("A"), NIn("B", "B")], OutN, [Num("B", "1")], () => new Fn(i => i[0] * i[1])),
        new("DIV", "Divide", "Math", "A ÷ B (0 when B is 0).", [NIn("A"), NIn("B", "B")], OutN, [Num("B", "1")], () => new Fn(i => i[1] == 0 ? 0 : i[0] / i[1])),
        new("MIN", "Min", "Math", "", [NIn("A"), NIn("B")], OutN, [], () => new Fn(i => Math.Min(i[0], i[1]))),
        new("MAX", "Max", "Math", "", [NIn("A"), NIn("B")], OutN, [], () => new Fn(i => Math.Max(i[0], i[1]))),
        new("ABS", "Absolute", "Math", "", [NIn("In")], OutN, [], () => new Fn(i => Math.Abs(i[0]))),
        new("LIMIT", "Limit", "Math", "Clamps In between Min and Max.", [NIn("In"), NIn("Min", "Min"), NIn("Max", "Max")], OutN,
            [Num("Min", "0"), Num("Max", "100")], () => new Fn(i => Math.Clamp(i[0], Math.Min(i[1], i[2]), Math.Max(i[1], i[2])))),
        new("SCALE", "Scale", "Math", "Maps In from [InMin, InMax] to [OutMin, OutMax], e.g. 4-20 mA to 0-100 %.",
            [NIn("In")], OutN, [Num("InMin", "0"), Num("InMax", "100"), Num("OutMin", "0"), Num("OutMax", "100"), new("Clamp", ParamKind.Bool, "true")], () => new ScaleBlock()),
        new("SEL", "Select", "Math", "Out = In1 when G is true, otherwise In0.", [BIn("G"), NIn("In0"), NIn("In1")], OutN, [], () => new Fn(i => i[0] != 0 ? i[2] : i[1])),

        // ---- Timers & counters ----
        new("TON", "On delay (TON)", "Timers", "Q turns on when In has been true for PT ms.",
            [BIn("In"), NIn("PT", "PT")], [new("Q", PortKind.Bool), new("ET", PortKind.Number)], [Num("PT", "1000", "ms")], () => new TimerBlock(TimerKind.On)),
        new("TOF", "Off delay (TOF)", "Timers", "Q follows In on, and turns off PT ms after In goes false.",
            [BIn("In"), NIn("PT", "PT")], [new("Q", PortKind.Bool), new("ET", PortKind.Number)], [Num("PT", "1000", "ms")], () => new TimerBlock(TimerKind.Off)),
        new("TP", "Pulse (TP)", "Timers", "A rising edge on In gives a PT ms pulse on Q.",
            [BIn("In"), NIn("PT", "PT")], [new("Q", PortKind.Bool), new("ET", PortKind.Number)], [Num("PT", "1000", "ms")], () => new TimerBlock(TimerKind.Pulse)),
        new("CTU", "Count up (CTU)", "Timers", "Counts rising edges on CU; R resets. Q when CV ≥ PV.",
            [BIn("CU"), BIn("R"), NIn("PV", "PV")], [new("Q", PortKind.Bool), new("CV", PortKind.Number)], [Num("PV", "10")], () => new CounterBlock(up: true)),
        new("CTD", "Count down (CTD)", "Timers", "Counts down on CD rising edges; LD loads PV. Q when CV ≤ 0.",
            [BIn("CD"), BIn("LD"), NIn("PV", "PV")], [new("Q", PortKind.Bool), new("CV", PortKind.Number)], [Num("PV", "10")], () => new CounterBlock(up: false)),

        // ---- Process ----
        new("PID", "PID", "Process", "PID controller (Kp, integral time Ti, derivative time Td). Reverse acting for cooling/level-out loops. EN false holds the output.",
            [NIn("PV"), NIn("SP", "SP"), BIn("EN", 1)], OutN,
            [Num("SP", "50"), Num("Kp", "1"), Num("Ti", "10", "s, 0 = no integral"), Num("Td", "0", "s"), Num("OutMin", "0"), Num("OutMax", "100"),
             new("Action", ParamKind.Choice, "Direct", "Direct: output rises when PV < SP", ["Direct", "Reverse"])], () => new PidBlock()),
        new("LAG", "First-order lag", "Process", "Out follows In with time constant Tau, like a temperature or a filter.",
            [NIn("In"), NIn("Tau", "Tau")], OutN, [Num("Tau", "5", "s"), Num("Initial", "0")], () => new LagBlock()),
        new("INTEG", "Integrator / tank", "Process", "Out += Rate × dt, clamped to [Min, Max]. Wire inflow − outflow to Rate to model a tank level. R resets to Initial.",
            [NIn("Rate"), BIn("R")], OutN, [Num("Min", "0"), Num("Max", "100"), Num("Initial", "0")], () => new IntegratorBlock()),
        new("RAMP", "Rate limiter", "Process", "Out moves towards In by at most Rate units per second (valve travel, motor speed).",
            [NIn("In"), NIn("Rate", "Rate")], OutN, [Num("Rate", "10", "units/s")], () => new RampBlock()),
        new("NOISE", "Noise", "Process", "Adds random noise of ±Amplitude to In, like a real sensor.",
            [NIn("In")], OutN, [Num("Amplitude", "0.5")], () => new NoiseBlock()),
        new("DEADTIME", "Dead time", "Process", "Delays In by Delay ms (transport delay through a pipe or conveyor).",
            [NIn("In")], OutN, [Num("Delay", "1000", "ms")], () => new DeadTimeBlock()),
    ];

    private static readonly Dictionary<string, BlockDef> ByType = All.ToDictionary(d => d.Type, StringComparer.OrdinalIgnoreCase);

    public static BlockDef? Find(string type) => ByType.GetValueOrDefault(type);

    public static IReadOnlyList<string> Categories { get; } = All.Select(d => d.Category).Distinct().ToList();

    internal static BlockRuntime Instantiate(BlockDef def, LogicBlock block, BlockRuntime? previous)
    {
        var rt = previous is { } p && p.Def == def ? p : def.Create();
        rt.Block = block;
        rt.Def = def;
        if (rt.In.Length != def.Inputs.Count) rt.In = new double[def.Inputs.Count];
        if (rt.Out.Length != def.Outputs.Count) rt.Out = new double[def.Outputs.Count];
        rt.Configure();
        return rt;
    }

    // ---- Runtimes ----

    private sealed class Fn(Func<double[], double> f) : BlockRuntime
    {
        public override void Execute(ScanContext ctx) => Out[0] = f(In);
    }

    private sealed class ConstBlock : BlockRuntime
    {
        public override void Execute(ScanContext ctx) => Out[0] = P("Value");
    }

    private sealed class ClockBlock : BlockRuntime
    {
        public override void Execute(ScanContext ctx)
        {
            double period = Math.Max(1, P("Period")) / 1000;
            double phase = ctx.Time / period - Math.Floor(ctx.Time / period);
            Out[0] = Bit(phase < Math.Clamp(P("Duty"), 0, 100) / 100);
        }
    }

    private sealed class TagInBlock : BlockRuntime
    {
        public override void Execute(ScanContext ctx)
        {
            var reference = S("Tag");
            if (string.IsNullOrWhiteSpace(reference)) { Error = "choose a tag"; Out[0] = 0; return; }
            var tag = ctx.Tags.Find(reference);
            if (tag?.Binding is not { } b) { Error = tag == null ? $"no tag '{reference}'" : $"'{reference}' has an invalid address"; Out[0] = 0; return; }
            Error = null;
            Out[0] = ValueConverter.ToDouble(b.Read());
        }
    }

    private sealed class TagOutBlock : BlockRuntime
    {
        private SimTag? _held;
        private double _last = double.NaN;

        public override void Execute(ScanContext ctx)
        {
            var reference = S("Tag");
            var tag = string.IsNullOrWhiteSpace(reference) ? null : ctx.Tags.Find(reference);
            if (!ReferenceEquals(tag, _held))
            {
                _held?.Release();
                _held = tag;
                _held?.Hold();
                _last = double.NaN;
            }
            if (string.IsNullOrWhiteSpace(reference)) { Error = "choose a tag"; return; }
            if (tag?.Binding is not { } b) { Error = tag == null ? $"no tag '{reference}'" : $"'{reference}' has an invalid address"; return; }
            Error = null;
            if (!B(1)) return;
            double v = In[0];
            if (v.Equals(_last)) return;
            try
            {
                b.Write(ValueConverter.FromDouble(v, b.DataType));
                _last = v;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }

        public override void Reset() => _last = double.NaN;

        public override void Dispose()
        {
            _held?.Release();
            _held = null;
        }
    }

    private sealed class LatchBlock(bool setDominant) : BlockRuntime
    {
        public override void Execute(ScanContext ctx)
        {
            bool s = B(0), r = B(1), q = Out[0] != 0;
            q = setDominant ? s || (q && !r) : !r && (s || q);
            Out[0] = Bit(q);
        }

        public override void Reset() => Out[0] = 0;
    }

    private sealed class EdgeBlock(bool rising) : BlockRuntime
    {
        private bool _prev;

        public override void Execute(ScanContext ctx)
        {
            bool clk = B(0);
            Out[0] = Bit(rising ? clk && !_prev : !clk && _prev);
            _prev = clk;
        }

        public override void Reset() => _prev = false;
    }

    private sealed class HysteresisBlock : BlockRuntime
    {
        public override void Execute(ScanContext ctx)
        {
            double on = P("On"), off = P("Off"), x = In[0];
            bool q = Out[0] != 0;
            if (on >= off) q = x >= on || (q && x > off);
            else q = x <= on || (q && x < off);
            Out[0] = Bit(q);
        }
    }

    private sealed class ScaleBlock : BlockRuntime
    {
        public override void Execute(ScanContext ctx)
        {
            double inMin = P("InMin"), inMax = P("InMax"), outMin = P("OutMin"), outMax = P("OutMax");
            double span = inMax - inMin;
            double v = span == 0 ? outMin : outMin + (In[0] - inMin) / span * (outMax - outMin);
            if (PB("Clamp")) v = Math.Clamp(v, Math.Min(outMin, outMax), Math.Max(outMin, outMax));
            Out[0] = v;
        }
    }

    private enum TimerKind { On, Off, Pulse }

    private sealed class TimerBlock(TimerKind kind) : BlockRuntime
    {
        private double _elapsed;
        private bool _prevIn, _running;

        public override void Execute(ScanContext ctx)
        {
            bool input = B(0);
            double pt = Math.Max(0, In[1]) / 1000;
            bool q;
            switch (kind)
            {
                case TimerKind.On:
                    _elapsed = input ? Math.Min(pt, _elapsed + ctx.Dt) : 0;
                    q = input && _elapsed >= pt;
                    break;
                case TimerKind.Off:
                    if (input)
                    {
                        _elapsed = 0;
                        _running = true;
                        q = true;
                    }
                    else if (_running)
                    {
                        _elapsed = Math.Min(pt, _elapsed + ctx.Dt);
                        if (_elapsed >= pt) _running = false;
                        q = _running;
                    }
                    else q = false;
                    break;
                default:
                    // IEC TP: a rising edge starts a fixed-length pulse that can't be retriggered until it ends and In is false again.
                    if (!_running && input && !_prevIn && _elapsed == 0) _running = true;
                    if (_running)
                    {
                        _elapsed = Math.Min(pt, _elapsed + ctx.Dt);
                        if (_elapsed >= pt) _running = false;
                        q = _running || _elapsed < pt;
                    }
                    else
                    {
                        if (!input) _elapsed = 0;
                        q = false;
                    }
                    break;
            }
            _prevIn = input;
            Out[0] = Bit(q);
            Out[1] = Math.Round(_elapsed * 1000);
        }

        public override void Reset()
        {
            _elapsed = 0;
            _prevIn = _running = false;
        }
    }

    private sealed class CounterBlock(bool up) : BlockRuntime
    {
        private bool _prev;
        private double _cv;

        public override void Execute(ScanContext ctx)
        {
            bool clk = B(0), resetOrLoad = B(1);
            double pv = In[2];
            if (up)
            {
                if (resetOrLoad) _cv = 0;
                else if (clk && !_prev) _cv++;
            }
            else
            {
                if (resetOrLoad) _cv = pv;
                else if (clk && !_prev) _cv--;
            }
            _prev = clk;
            Out[0] = Bit(up ? _cv >= pv : _cv <= 0);
            Out[1] = _cv;
        }

        public override void Reset()
        {
            _prev = false;
            _cv = up ? 0 : In.Length > 2 ? In[2] : 0;
        }
    }

    private sealed class PidBlock : BlockRuntime
    {
        private double _integral, _lastPv;
        private bool _first = true;

        public override void Execute(ScanContext ctx)
        {
            double pv = In[0], sp = In[1];
            double lo = Math.Min(P("OutMin"), P("OutMax")), hi = Math.Max(P("OutMin"), P("OutMax"));
            if (!B(2) || ctx.Dt <= 0) { _lastPv = pv; return; }

            bool reverse = string.Equals(S("Action"), "Reverse", StringComparison.OrdinalIgnoreCase);
            double e = reverse ? pv - sp : sp - pv;
            double kp = P("Kp"), ti = P("Ti"), td = P("Td");
            double dPv = _first ? 0 : (pv - _lastPv) / ctx.Dt;
            if (reverse) dPv = -dPv;
            _first = false;
            _lastPv = pv;

            double candidate = _integral + (ti > 0 ? e * ctx.Dt / ti : 0);
            double output = kp * (e + candidate - td * dPv);
            // Anti-windup: only integrate while the output isn't saturated in the same direction.
            if (output > hi && e > 0 || output < lo && e < 0) output = kp * (e + _integral - td * dPv);
            else _integral = candidate;
            Out[0] = Math.Clamp(output, lo, hi);
        }

        public override void Reset()
        {
            _integral = 0;
            _first = true;
        }
    }

    private sealed class LagBlock : BlockRuntime
    {
        private bool _started;

        public override void Execute(ScanContext ctx)
        {
            if (!_started) { Out[0] = P("Initial"); _started = true; }
            double tau = Math.Max(1e-3, In[1]);
            Out[0] += (In[0] - Out[0]) * Math.Min(1, ctx.Dt / tau);
        }

        public override void Reset() => _started = false;
    }

    private sealed class IntegratorBlock : BlockRuntime
    {
        private bool _started;

        public override void Execute(ScanContext ctx)
        {
            double lo = Math.Min(P("Min"), P("Max")), hi = Math.Max(P("Min"), P("Max"));
            if (!_started || B(1)) { Out[0] = Math.Clamp(P("Initial"), lo, hi); _started = true; return; }
            Out[0] = Math.Clamp(Out[0] + In[0] * ctx.Dt, lo, hi);
        }

        public override void Reset() => _started = false;
    }

    private sealed class RampBlock : BlockRuntime
    {
        private bool _started;

        public override void Execute(ScanContext ctx)
        {
            if (!_started) { Out[0] = In[0]; _started = true; return; }
            double step = Math.Abs(In[1]) * ctx.Dt;
            Out[0] += Math.Clamp(In[0] - Out[0], -step, step);
        }

        public override void Reset() => _started = false;
    }

    private sealed class NoiseBlock : BlockRuntime
    {
        public override void Execute(ScanContext ctx) =>
            Out[0] = In[0] + (Random.Shared.NextDouble() * 2 - 1) * Math.Abs(P("Amplitude"));
    }

    private sealed class DeadTimeBlock : BlockRuntime
    {
        private readonly Queue<(double Time, double Value)> _queue = new();

        public override void Execute(ScanContext ctx)
        {
            double delay = Math.Max(0, P("Delay")) / 1000;
            _queue.Enqueue((ctx.Time, In[0]));
            while (_queue.Count > 1 && _queue.ElementAt(1).Time <= ctx.Time - delay) _queue.Dequeue();
            var head = _queue.Peek();
            if (head.Time <= ctx.Time - delay || delay == 0) Out[0] = head.Value;
            if (_queue.Count > 100_000) _queue.Dequeue();
        }

        public override void Reset() => _queue.Clear();
    }
}
