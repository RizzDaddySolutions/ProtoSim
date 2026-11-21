using System.Collections.ObjectModel;
using System.Diagnostics;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Logic;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Network;

/// <summary>One value of a network link: a point on the source server and the project tag it feeds.</summary>
public sealed class LinkPointState : ObservableObject
{
    private string _source;
    private string _target;
    private TagDataType _dataType;
    private WordOrder _wordOrder;
    private LinkDirection _direction;
    private string _sourceName;
    private string _value = string.Empty;
    private string _status = string.Empty;
    private bool _hasError;

    public LinkPointState(LinkPointDocument doc)
    {
        _source = doc.Source;
        _sourceName = doc.SourceName;
        _target = doc.Target;
        _dataType = doc.DataType;
        _wordOrder = doc.WordOrder;
        _direction = doc.Direction;
    }

    /// <summary>Address on the source server, in the client's syntax.</summary>
    public string Source { get => _source; set { if (SetProperty(ref _source, value?.Trim() ?? string.Empty)) Reset(); } }

    /// <summary>Tag name on the source device, for display (empty for manual points).</summary>
    public string SourceName { get => _sourceName; set => SetProperty(ref _sourceName, value ?? string.Empty); }

    /// <summary>Project tag as "Device/Tag".</summary>
    public string Target { get => _target; set { if (SetProperty(ref _target, value?.Trim() ?? string.Empty)) Reset(); } }

    public TagDataType DataType { get => _dataType; set { if (SetProperty(ref _dataType, value)) Reset(); } }
    public WordOrder WordOrder { get => _wordOrder; set { if (SetProperty(ref _wordOrder, value)) Reset(); } }
    public LinkDirection Direction { get => _direction; set => SetProperty(ref _direction, value); }

    /// <summary>The last value seen on the link.</summary>
    public string Value { get => _value; private set => SetProperty(ref _value, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    /// <summary>The target's value right after the link last synchronised it; a different value means it changed locally.</summary>
    internal string? Synced { get; set; }

    internal void Reset() => Synced = null;

    internal void Set(string value, string status, bool error)
    {
        Value = value;
        Status = status;
        HasError = error;
    }

    internal void Fail(string status)
    {
        Status = status;
        HasError = true;
    }

    public LinkPoint ToPoint() => new(Source, DataType, WordOrder);

    public LinkPointDocument ToDocument() => new()
    {
        Source = Source,
        SourceName = SourceName,
        Target = Target,
        DataType = DataType,
        WordOrder = WordOrder,
        Direction = Direction,
    };
}

public enum LinkState
{
    Stopped,
    Running,
    Faulted,
}

/// <summary>
/// A connection between two simulators: a protocol client that polls points on a source server (another ProtoSim device,
/// on this computer or another, or any real device) and copies them into project tags, optionally writing local changes back.
/// Runs on the thread that starts it; in the app that is the UI thread, like client polling.
/// </summary>
public sealed class NetworkLink : ObservableObject
{
    private readonly ITagDirectory _tags;
    private readonly HashSet<SimTag> _held = [];
    private IProtocolClient? _client;
    private CancellationTokenSource? _cts;
    private string _name;
    private string _sourceDescription;
    private int _pollIntervalMs = 1000;
    private bool _autoStart;
    private bool _isRunning;
    private bool _lastSuccess = true;
    private string _lastMessage = "Stopped";
    private long _cycles;
    private long _failures;
    private long _writes;
    private string _lastElapsed = string.Empty;

    public NetworkLink(IProtocolPlugin plugin, string name, object settings, ITagDirectory tags, string sourceDescription = "")
    {
        if (!plugin.SupportsLinks) throw new NotSupportedException($"{plugin.DisplayName} does not support network links.");
        Plugin = plugin;
        Settings = settings;
        _name = name;
        _tags = tags;
        _sourceDescription = sourceDescription;
    }

    public IProtocolPlugin Plugin { get; }
    public object Settings { get; }
    public TrafficLog TrafficLog { get; } = new();
    public ObservableCollection<LinkPointState> Points { get; } = [];

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>Where the values come from, e.g. "PC-2 › Tank farm (OPC UA)".</summary>
    public string SourceDescription { get => _sourceDescription; set => SetProperty(ref _sourceDescription, value ?? string.Empty); }

    public string Endpoint => Plugin.DescribeClient(Settings);
    public int PollIntervalMs { get => _pollIntervalMs; set => SetProperty(ref _pollIntervalMs, Math.Clamp(value, 50, 3_600_000)); }
    public bool AutoStart { get => _autoStart; set => SetProperty(ref _autoStart, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(State)); }
    }

    public LinkState State => !_lastSuccess ? LinkState.Faulted : _isRunning ? LinkState.Running : LinkState.Stopped;
    public bool LastSuccess { get => _lastSuccess; private set { if (SetProperty(ref _lastSuccess, value)) OnPropertyChanged(nameof(State)); } }
    public string LastMessage { get => _lastMessage; private set => SetProperty(ref _lastMessage, value); }
    public long Cycles { get => _cycles; private set => SetProperty(ref _cycles, value); }
    public long Failures { get => _failures; private set => SetProperty(ref _failures, value); }
    public long Writes { get => _writes; private set => SetProperty(ref _writes, value); }
    public string LastElapsed { get => _lastElapsed; private set => SetProperty(ref _lastElapsed, value); }

    public LinkPointState AddPoint(LinkPointDocument doc)
    {
        var p = new LinkPointState(doc);
        Points.Add(p);
        return p;
    }

    private IProtocolClient Client => _client ??= Plugin.CreateClient(Settings, new ClientContext(TrafficLog));

    /// <summary>Starts polling; returns when <see cref="StopAsync"/> is called.</summary>
    public async Task RunAsync()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        LastMessage = $"Connecting to {Endpoint}";
        foreach (var p in Points) p.Reset();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                await CycleAsync(cts.Token);
                var wait = TimeSpan.FromMilliseconds(_pollIntervalMs) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                else await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            LastSuccess = false;
        }
        finally
        {
            ReleaseTargets();
            IsRunning = false;
        }
    }

    /// <summary>Starts polling in the background (fire and forget, on the caller's synchronization context).</summary>
    public async void Start()
    {
        try { await RunAsync(); }
        catch (Exception ex) { LastMessage = ex.Message; LastSuccess = false; }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        ReleaseTargets();
        if (_client is { } client)
        {
            _client = null; // recreated on the next start so edited settings apply
            try { await client.DisposeAsync(); }
            catch (Exception ex) { TrafficLog.Warn("", $"Disconnect: {ex.Message}"); }
        }
        IsRunning = false;
        LastMessage = "Stopped";
        LastSuccess = true;
    }

    /// <summary>Reads every point once, copies values into the target tags and writes local changes back.</summary>
    public async Task<bool> CycleAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var points = Points.ToList();
        var values = new object?[points.Count];
        var have = new bool[points.Count];
        var errors = new string?[points.Count];
        bool ok = true;
        string message = points.Count == 0 ? "No points" : string.Empty;

        // ---- Read the source ----
        var reads = Plan(points, errors);
        foreach (var read in reads)
        {
            ClientResult result;
            try { result = await Client.ExecuteAsync(read.Request, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { result = ClientResult.Fail(ex.Message, TimeSpan.Zero); }

            if (!result.Success)
            {
                ok = false;
                message = result.Message;
                foreach (var i in read.Points) errors[i] = result.Message;
                continue;
            }
            Dictionary<string, ClientResultRow>? byAddress = null;
            if (read.RowAddresses != null)
            {
                byAddress = new(StringComparer.OrdinalIgnoreCase);
                foreach (var r in result.Rows) byAddress.TryAdd(r.Address, r);
            }
            for (int k = 0; k < read.Points.Count; k++)
            {
                int i = read.Points[k];
                ClientResultRow? row = read.Row != null ? read.Row(result.Rows, k)
                    : byAddress != null
                    ? byAddress.GetValueOrDefault(read.RowAddresses![k])
                    : k < result.Rows.Count ? result.Rows[k] : null;
                if (row == null) { errors[i] = "No value from the source yet"; continue; }
                try
                {
                    values[i] = read.Value != null ? read.Value(k, row) : row.Typed ?? ClientValue.Parse(row.Value);
                    have[i] = values[i] != null;
                    if (!have[i]) errors[i] = row.Value.Length > 0 && row.Value != row.Raw ? $"{row.Raw}: {row.Value}" : row.Raw.Length > 0 ? row.Raw : "No value";
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException)
                {
                    errors[i] = ex.Message;
                }
            }
        }

        // ---- Copy into target tags, or write local changes back ----
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.Target.Length == 0) { p.Fail("Pick a target tag"); continue; }
            var tag = _tags.Find(p.Target);
            if (tag?.Binding is not { } binding) { p.Fail(tag == null ? $"No tag '{p.Target}'" : "Target tag has an invalid address"); ok = false; continue; }
            HoldIf(tag, p.Direction == LinkDirection.Read);

            string current;
            try { current = ValueConverter.Format(binding.Read()); }
            catch (Exception ex) { p.Fail(ex.Message); continue; }

            if (p.Direction == LinkDirection.ReadWrite && p.Synced != null && current != p.Synced)
            {
                // Changed here since the last sync: send it to the source.
                try
                {
                    var request = Plugin.CreateLinkWrite(p.ToPoint(), binding.Read());
                    var result = await Client.ExecuteAsync(request, ct);
                    if (!result.Success) { p.Fail($"Write-back failed: {result.Message}"); ok = false; message = result.Message; continue; }
                    p.Synced = current;
                    Writes++;
                    p.Set(current, $"{current} → source", false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { p.Fail($"Write-back failed: {ex.Message}"); ok = false; }
                continue;
            }

            if (!have[i]) { p.Fail(errors[i] ?? "No value"); continue; }
            try
            {
                binding.Write(values[i]!);
                p.Synced = ValueConverter.Format(binding.Read());
                p.Set(p.Synced, p.Direction == LinkDirection.ReadWrite ? "In sync" : "OK", false);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException or ArgumentException)
            {
                p.Fail(ex.Message);
            }
        }

        Cycles++;
        if (!ok) Failures++;
        LastSuccess = ok;
        int good = points.Count(p => !p.HasError);
        LastMessage = ok ? $"{good}/{points.Count} points OK" : message.Length > 0 ? message : $"{good}/{points.Count} points OK";
        LastElapsed = $"{sw.Elapsed.TotalMilliseconds:F0} ms";
        return ok;
    }

    /// <summary>Requests for all valid points. Points the plugin rejects get an error instead of failing the whole link.</summary>
    private List<LinkRead> Plan(List<LinkPointState> points, string?[] errors)
    {
        var valid = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Source.Length == 0) { errors[i] = "Enter a source address"; continue; }
            try { Plugin.CreateLinkReads([points[i].ToPoint()]); valid.Add(i); }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { errors[i] = ex.Message; }
        }
        if (valid.Count == 0) return [];
        var reads = Plugin.CreateLinkReads(valid.Select(i => points[i].ToPoint()).ToList());
        // Map plan indexes back to positions in the full list.
        return reads.Select(r => r with { Points = r.Points.Select(k => valid[k]).ToList() }).ToList();
    }

    private void HoldIf(SimTag tag, bool hold)
    {
        if (!IsRunning) return;
        if (hold && _held.Add(tag)) tag.Hold();
        else if (!hold && _held.Remove(tag)) tag.Release();
    }

    private void ReleaseTargets()
    {
        foreach (var t in _held) t.Release();
        _held.Clear();
    }

    public LinkDocument ToDocument() => new()
    {
        PluginId = Plugin.Id,
        Name = Name,
        SourceDescription = SourceDescription,
        Settings = Project.ProjectSerializer.ToElement(Settings),
        PollIntervalMs = PollIntervalMs,
        AutoStart = AutoStart,
        Points = Points.Select(p => p.ToDocument()).ToList(),
    };
}

/// <summary>A network link as saved in the project file.</summary>
public sealed class LinkDocument
{
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceDescription { get; set; } = string.Empty;
    public System.Text.Json.JsonElement? Settings { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    public bool AutoStart { get; set; }
    public List<LinkPointDocument> Points { get; set; } = [];
}

public sealed class LinkPointDocument
{
    public string Source { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public TagDataType DataType { get; set; } = TagDataType.Float32;
    public WordOrder WordOrder { get; set; }
    public string Target { get; set; } = string.Empty;
    public LinkDirection Direction { get; set; }
}
