using System.Diagnostics;

namespace ProtoSim.Core.Logic;

/// <summary>Live values of one block after a scan.</summary>
public sealed record BlockSnapshot(double[] Inputs, double[] Outputs, string? Error);

/// <summary>Live state of one program, read by the designer.</summary>
public sealed record ProgramSnapshot(long Scans, double LastScanMs, IReadOnlyDictionary<string, BlockSnapshot> Blocks, IReadOnlyList<string> Problems);

/// <summary>A program compiled for execution: blocks in evaluation order with their wiring resolved.</summary>
public sealed class CompiledProgram : IDisposable
{
    private readonly struct Source(int block, int port)
    {
        public readonly int Block = block;
        public readonly int Port = port;
    }

    private readonly BlockRuntime[] _order;
    private readonly Source?[][] _inputs;
    private readonly object _snapLock = new();
    private ProgramSnapshot _snapshot;
    private long _scans;
    private readonly Stopwatch _clock = new();
    private double _lastTime;

    private CompiledProgram(LogicProgram program, BlockRuntime[] order, Source?[][] inputs, IReadOnlyList<string> problems)
    {
        Program = program;
        _order = order;
        _inputs = inputs;
        Problems = problems;
        _snapshot = new ProgramSnapshot(0, 0, new Dictionary<string, BlockSnapshot>(), problems);
    }

    public LogicProgram Program { get; }

    /// <summary>Structural problems found while compiling (unknown block types, dangling wires...).</summary>
    public IReadOnlyList<string> Problems { get; }

    internal double DueAt { get; set; }

    public ProgramSnapshot Snapshot { get { lock (_snapLock) return _snapshot; } }

    /// <summary>
    /// Compiles a program. Blocks that also exist (same id and type) in <paramref name="previous"/> keep their
    /// state, so a running program can be edited without resetting timers and counters.
    /// Blocks are evaluated in data-flow order; blocks in a feedback loop see the previous scan's values.
    /// </summary>
    public static CompiledProgram Compile(LogicProgram program, CompiledProgram? previous = null)
    {
        var problems = new List<string>();
        var old = previous?._order.ToDictionary(r => r.Block.Id, StringComparer.Ordinal) ?? [];
        var runtimes = new List<BlockRuntime>();
        foreach (var block in program.Blocks)
        {
            var def = BlockLibrary.Find(block.Type);
            if (def == null)
            {
                problems.Add($"Unknown block type '{block.Type}'");
                continue;
            }
            runtimes.Add(BlockLibrary.Instantiate(def, block, old.GetValueOrDefault(block.Id)));
            old.Remove(block.Id);
        }
        foreach (var removed in old.Values) removed.Dispose();

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < runtimes.Count; i++) index[runtimes[i].Block.Id] = i;

        // Resolve wires: each input has at most one source.
        var sources = runtimes.Select(r => new Source?[r.Def.Inputs.Count]).ToArray();
        foreach (var w in program.Wires)
        {
            if (!index.TryGetValue(w.FromBlock, out var from) || !index.TryGetValue(w.ToBlock, out var to))
            {
                problems.Add("A wire connects to a block that no longer exists");
                continue;
            }
            int outPort = runtimes[from].Def.OutputIndex(w.FromPort);
            int inPort = runtimes[to].Def.InputIndex(w.ToPort);
            if (outPort < 0 || inPort < 0)
            {
                problems.Add($"Wire {w.FromPort} → {w.ToPort} uses a port that doesn't exist");
                continue;
            }
            sources[to][inPort] = new Source(from, outPort);
        }

        // Topological order (Kahn); whatever remains is part of a loop and runs in canvas order.
        var dependents = runtimes.Select(_ => new List<int>()).ToArray();
        var pending = new int[runtimes.Count];
        for (int to = 0; to < runtimes.Count; to++)
        {
            foreach (var s in sources[to].Where(s => s.HasValue).Select(s => s!.Value.Block).Distinct())
            {
                if (s == to) continue;
                dependents[s].Add(to);
                pending[to]++;
            }
        }
        var ready = new Queue<int>(Enumerable.Range(0, runtimes.Count).Where(i => pending[i] == 0));
        var order = new List<int>(runtimes.Count);
        while (ready.Count > 0)
        {
            int i = ready.Dequeue();
            order.Add(i);
            foreach (var d in dependents[i])
                if (--pending[d] == 0) ready.Enqueue(d);
        }
        order.AddRange(Enumerable.Range(0, runtimes.Count).Where(i => !order.Contains(i)));

        var map = new int[runtimes.Count];
        for (int k = 0; k < order.Count; k++) map[order[k]] = k;
        var ordered = order.Select(i => runtimes[i]).ToArray();
        var inputs = order.Select(i => sources[i].Select(s => s is { } v ? new Source(map[v.Block], v.Port) : (Source?)null).ToArray()).ToArray();
        return new CompiledProgram(program, ordered, inputs, problems);
    }

    internal void Start()
    {
        foreach (var r in _order) r.Reset();
        _clock.Restart();
        _clockOffset = 0;
        _lastTime = 0;
        DueAt = 0;
    }

    /// <summary>Runs one scan. Public for testing.</summary>
    public void Scan(ITagDirectory tags, double? dtOverride = null)
    {
        var watch = Stopwatch.StartNew();
        double now = _clock.IsRunning ? _clockOffset + _clock.Elapsed.TotalSeconds : _lastTime + (dtOverride ?? 0);
        double dt = dtOverride ?? Math.Max(0, now - _lastTime);
        if (dtOverride is { } fixedDt) now = _lastTime + fixedDt;
        _lastTime = now;
        var ctx = new ScanContext { Tags = tags, Dt = dt, Time = now };

        for (int b = 0; b < _order.Length; b++)
        {
            var rt = _order[b];
            var srcs = _inputs[b];
            for (int i = 0; i < srcs.Length; i++)
            {
                if (srcs[i] is { } s) rt.In[i] = _order[s.Block].Out[s.Port];
                else
                {
                    var port = rt.Def.Inputs[i];
                    rt.In[i] = port.FallbackParam != null && double.TryParse(rt.Block.Param(port.FallbackParam, rt.Def.Parameters.First(p => p.Name == port.FallbackParam).Default),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : port.Default;
                }
            }
            try
            {
                rt.Execute(ctx);
                for (int o = 0; o < rt.Out.Length; o++)
                    if (!double.IsFinite(rt.Out[o])) rt.Out[o] = 0;
            }
            catch (Exception ex)
            {
                rt.Error = ex.Message;
            }
        }

        var blocks = new Dictionary<string, BlockSnapshot>(_order.Length, StringComparer.Ordinal);
        foreach (var rt in _order) blocks[rt.Block.Id] = new BlockSnapshot((double[])rt.In.Clone(), (double[])rt.Out.Clone(), rt.Error);
        long scans = Interlocked.Increment(ref _scans);
        lock (_snapLock) _snapshot = new ProgramSnapshot(scans, watch.Elapsed.TotalMilliseconds, blocks, Problems);
    }

    /// <summary>Continues the program clock of the version this one replaces (live edits keep time running).</summary>
    internal void AdoptClock(CompiledProgram previous)
    {
        if (previous._clock.IsRunning)
        {
            _clock.Restart();
            _clockOffset = previous._clockOffset + previous._clock.Elapsed.TotalSeconds;
        }
        _lastTime = previous._lastTime;
        DueAt = previous.DueAt;
    }

    private double _clockOffset;

    /// <summary>Releases tag holds (Tag out blocks).</summary>
    public void Dispose()
    {
        foreach (var r in _order) r.Dispose();
    }
}

/// <summary>Runs logic programs on a background scan loop, each at its own scan interval.</summary>
public sealed class LogicEngine : IDisposable
{
    private readonly ITagDirectory _tags;
    private readonly object _gate = new();
    private Dictionary<string, CompiledProgram> _programs = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Stopwatch _clock = new();

    public LogicEngine(ITagDirectory tags) => _tags = tags;

    public bool IsRunning => _loop != null;

    /// <summary>Raised on the scan thread when a whole scan fails unexpectedly.</summary>
    public event Action<LogicProgram, Exception>? ScanFailed;

    /// <summary>
    /// Sets the programs to run. Pass copies (<see cref="LogicProgram.Clone"/>): the engine keeps them.
    /// Blocks of a program with the same <see cref="LogicProgram.Id"/> keep their state (timers, counters, latches),
    /// so programs can be edited while running. Disabled programs are not run.
    /// </summary>
    public void Load(IEnumerable<LogicProgram> programs)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, CompiledProgram>(StringComparer.Ordinal);
            foreach (var p in programs.Where(p => p.Enabled))
            {
                var previous = _programs.GetValueOrDefault(p.Id);
                var compiled = CompiledProgram.Compile(p, previous);
                if (previous != null) compiled.AdoptClock(previous);
                else if (IsRunning)
                {
                    compiled.Start();
                    compiled.DueAt = _clock.Elapsed.TotalSeconds;
                }
                next[p.Id] = compiled;
            }
            // Compile handed surviving block runtimes over; only programs that are gone get disposed.
            foreach (var (id, c) in _programs)
                if (!next.ContainsKey(id)) c.Dispose();
            _programs = next;
        }
    }

    public ProgramSnapshot? Snapshot(string programId)
    {
        lock (_gate) return _programs.TryGetValue(programId, out var c) ? c.Snapshot : null;
    }

    public void Start()
    {
        if (_loop != null) return;
        lock (_gate)
            foreach (var c in _programs.Values) c.Start();
        _clock.Restart();
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { await (_loop ?? Task.CompletedTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        lock (_gate)
        {
            // Fresh runtimes: Tag out holds are released and the next start begins from scratch.
            var programs = _programs.Values.Select(c => c.Program).ToList();
            foreach (var c in _programs.Values) c.Dispose();
            _programs = programs.ToDictionary(p => p.Id, p => CompiledProgram.Compile(p), StringComparer.Ordinal);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                double now = _clock.Elapsed.TotalSeconds;
                lock (_gate)
                {
                    foreach (var c in _programs.Values)
                    {
                        if (now < c.DueAt) continue;
                        c.DueAt = now + Math.Max(5, c.Program.ScanMs) / 1000.0;
                        try { c.Scan(_tags); }
                        catch (Exception ex) { ScanFailed?.Invoke(c.Program, ex); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        lock (_gate)
        {
            foreach (var c in _programs.Values) c.Dispose();
            _programs = new(StringComparer.Ordinal);
        }
    }
}
