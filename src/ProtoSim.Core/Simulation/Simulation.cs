using System.Diagnostics;
using ProtoSim.Core.Data;

namespace ProtoSim.Core.Simulation;

public enum SimulationKind
{
    None,
    Ramp,
    Sine,
    Triangle,
    Square,
    Random,
    RandomWalk,
    Counter,
    Toggle,
}

/// <summary>How a tag's value changes over time.</summary>
public sealed class SimulationSettings : ObservableObject
{
    private SimulationKind _kind;
    private double _min;
    private double _max = 100;
    private double _periodSeconds = 10;
    private double _step = 1;
    private int _intervalMs = 500;

    public SimulationKind Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public double Min { get => _min; set => SetProperty(ref _min, value); }
    public double Max { get => _max; set => SetProperty(ref _max, value); }
    public double PeriodSeconds { get => _periodSeconds; set => SetProperty(ref _periodSeconds, value); }
    public double Step { get => _step; set => SetProperty(ref _step, value); }
    public int IntervalMs { get => _intervalMs; set => SetProperty(ref _intervalMs, value); }

    public SimulationSettings Clone() => new()
    {
        Kind = Kind,
        Min = Min,
        Max = Max,
        PeriodSeconds = PeriodSeconds,
        Step = Step,
        IntervalMs = IntervalMs,
    };

    /// <summary>Computes the next value. <paramref name="t"/> is seconds since the engine started.</summary>
    public double Evaluate(double t, double current, Random rng)
    {
        double lo = Math.Min(Min, Max), hi = Math.Max(Min, Max);
        double span = hi - lo;
        double period = PeriodSeconds > 0.001 ? PeriodSeconds : 0.001;
        double phase = t / period - Math.Floor(t / period);

        return Kind switch
        {
            SimulationKind.Ramp => lo + span * phase,
            SimulationKind.Sine => lo + span / 2 + span / 2 * Math.Sin(2 * Math.PI * phase),
            SimulationKind.Triangle => lo + span * (phase < 0.5 ? 2 * phase : 2 - 2 * phase),
            SimulationKind.Square => phase < 0.5 ? lo : hi,
            SimulationKind.Random => lo + span * rng.NextDouble(),
            SimulationKind.RandomWalk => Math.Clamp(current + (rng.NextDouble() * 2 - 1) * Step, lo, hi),
            SimulationKind.Counter => current + Step > hi ? lo : Math.Max(current + Step, lo),
            SimulationKind.Toggle => current != 0 ? 0 : 1,
            _ => current,
        };
    }
}

/// <summary>A named, typed value mapped onto a device address, optionally driven by a simulation.</summary>
public sealed class SimTag : ObservableObject
{
    private string _name = "Tag";
    private string _address = string.Empty;
    private TagDataType _dataType = TagDataType.UInt16;
    private WordOrder _wordOrder = WordOrder.ABCD;
    private string _description = string.Empty;
    private SimulationSettings _simulation = new();
    private volatile ITagBinding? _binding;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Address { get => _address; set => SetProperty(ref _address, value?.Trim() ?? string.Empty); }
    public TagDataType DataType { get => _dataType; set => SetProperty(ref _dataType, value); }
    public WordOrder WordOrder { get => _wordOrder; set => SetProperty(ref _wordOrder, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public SimulationSettings Simulation { get => _simulation; set => SetProperty(ref _simulation, value ?? new()); }

    /// <summary>Current binding to the device memory; null when the address is invalid.</summary>
    public ITagBinding? Binding { get => _binding; set { _binding = value; OnPropertyChanged(); } }

    internal double LastSimTime = double.NegativeInfinity;
}

/// <summary>Background engine that drives simulated tags at their configured intervals.</summary>
public sealed class SimulationEngine : IDisposable
{
    private readonly object _gate = new();
    private SimTag[] _tags = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>When false, tags keep their current values.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Raised on the engine thread when a tag update throws.</summary>
    public event Action<SimTag, Exception>? TagFaulted;

    public void Add(SimTag tag)
    {
        lock (_gate) _tags = [.. _tags, tag];
    }

    public void Remove(SimTag tag)
    {
        lock (_gate) _tags = _tags.Where(t => !ReferenceEquals(t, tag)).ToArray();
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var clock = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!IsEnabled) continue;
                double t = clock.Elapsed.TotalSeconds;
                foreach (var tag in Volatile.Read(ref _tags))
                {
                    try { Step(tag, t); }
                    catch (Exception ex) { TagFaulted?.Invoke(tag, ex); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Advances one tag. Public for testing.</summary>
    public static void Step(SimTag tag, double t)
    {
        var sim = tag.Simulation;
        var binding = tag.Binding;
        if (sim.Kind == SimulationKind.None || binding == null) return;

        double interval = Math.Max(sim.IntervalMs, 10) / 1000.0;
        if (t - tag.LastSimTime < interval) return;
        tag.LastSimTime = t;

        double current = ValueConverter.ToDouble(binding.Read());
        double next = sim.Evaluate(t, current, Random.Shared);

        // For booleans, analog shapes become "above midpoint" so e.g. Square toggles nicely.
        if (binding.DataType == TagDataType.Bool && sim.Kind != SimulationKind.Toggle)
            next = next >= (sim.Min + sim.Max) / 2 ? 1 : 0;

        binding.Write(ValueConverter.FromDouble(next, binding.DataType));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}
