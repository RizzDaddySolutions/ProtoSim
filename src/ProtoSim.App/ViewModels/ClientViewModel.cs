using System.Collections.ObjectModel;
using System.Windows.Input;
using ProtoSim.App.Platform;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.App.ViewModels;

/// <summary>One "copy this result row into that tag" rule of a client session.</summary>
public sealed class ClientMappingViewModel : ObservableObject
{
    private string _source;
    private string _target;
    private string _status = string.Empty;
    private bool _hasError;

    public ClientMappingViewModel(string source, string target)
    {
        _source = source;
        _target = target;
    }

    /// <summary>Result row address, e.g. "HR:0 (40001)" or "ns=2;s=Plant/Level".</summary>
    public string Source { get => _source; set => SetProperty(ref _source, value?.Trim() ?? string.Empty); }

    /// <summary>Project tag as "Device/Tag".</summary>
    public string Target { get => _target; set => SetProperty(ref _target, value?.Trim() ?? string.Empty); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    internal void SetStatus(string status, bool error)
    {
        Status = status;
        HasError = error;
    }

    public ClientTagMapping ToModel() => new() { Source = Source, Target = Target };
}

/// <summary>A client session that reads from (or writes to) an external server or device, real or simulated.</summary>
public sealed class ClientViewModel : ObservableObject
{
    private readonly ProjectTagDirectory _tags;
    private readonly HashSet<SimTag> _held = [];
    private IProtocolClient? _client;
    private CancellationTokenSource? _pollCts;
    private string _name;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isPolling;
    private int _pollIntervalMs = 1000;
    private long _successCount;
    private long _failureCount;
    private string _lastMessage = "Not connected";
    private bool _lastSuccess = true;
    private string _lastElapsed = string.Empty;
    private bool _autoPoll;
    private ClientResultRow? _selectedResult;
    private ClientMappingViewModel? _selectedMapping;
    private IReadOnlyList<string> _tagReferences = [];

    public ClientViewModel(IProtocolPlugin plugin, string name, object settings, object request, ProjectTagDirectory tags)
    {
        Plugin = plugin;
        _name = name;
        Settings = settings;
        Request = request;
        _tags = tags;
        _tagReferences = tags.References;
        Log = new LogViewModel(TrafficLog);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !_isBusy);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected || _isPolling);
        ExecuteCommand = new AsyncRelayCommand(ExecuteOnceAsync, () => !_isBusy && !_isPolling);
        StartPollingCommand = new RelayCommand(StartPolling, () => !_isPolling);
        StopPollingCommand = new RelayCommand(StopPolling, () => _isPolling);
        ResetCountersCommand = new RelayCommand(() => { SuccessCount = 0; FailureCount = 0; });
        AddMappingCommand = new RelayCommand(AddMapping);
        MapSelectedRowCommand = new RelayCommand(MapSelectedRow, () => SelectedResult != null);
        MapAllRowsCommand = new RelayCommand(MapAllRows, () => Results.Count > 0);
        RemoveMappingCommand = new RelayCommand(RemoveMapping, () => SelectedMapping != null);
    }

    public IProtocolPlugin Plugin { get; }
    public object Settings { get; }
    public object Request { get; }
    public TrafficLog TrafficLog { get; } = new();
    public LogViewModel Log { get; }
    public ObservableCollection<ClientResultRow> Results { get; } = [];
    public ObservableCollection<ClientMappingViewModel> Mappings { get; } = [];

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand StartPollingCommand { get; }
    public ICommand StopPollingCommand { get; }
    public ICommand ResetCountersCommand { get; }
    public ICommand AddMappingCommand { get; }
    public ICommand MapSelectedRowCommand { get; }
    public ICommand MapAllRowsCommand { get; }
    public ICommand RemoveMappingCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ProtocolName => Plugin.DisplayName;
    public string Endpoint => Plugin.DescribeClient(Settings);

    /// <summary>Project tags ("Device/Tag") for the mapping picker.</summary>
    public IReadOnlyList<string> TagReferences { get => _tagReferences; private set => SetProperty(ref _tagReferences, value); }

    /// <summary>Addresses of the latest result rows, for the mapping source picker.</summary>
    public IReadOnlyList<string> SourceOptions => Results.Select(r => r.Address).ToList();

    public ClientResultRow? SelectedResult
    {
        get => _selectedResult;
        set { if (SetProperty(ref _selectedResult, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public ClientMappingViewModel? SelectedMapping
    {
        get => _selectedMapping;
        set { if (SetProperty(ref _selectedMapping, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(State));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        private set
        {
            if (!SetProperty(ref _isPolling, value)) return;
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(State));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>For the status dot: running while connected or polling, faulted after a failure.</summary>
    public ServerState State => !_lastSuccess ? ServerState.Faulted : _isConnected || _isPolling ? ServerState.Running : ServerState.Stopped;

    public bool CanEditSettings => !_isConnected && !_isPolling;

    public int PollIntervalMs { get => _pollIntervalMs; set => SetProperty(ref _pollIntervalMs, Math.Clamp(value, 10, 3_600_000)); }

    /// <summary>Start polling automatically when the project opens.</summary>
    public bool AutoPoll { get => _autoPoll; set => SetProperty(ref _autoPoll, value); }

    public long SuccessCount { get => _successCount; private set => SetProperty(ref _successCount, value); }
    public long FailureCount { get => _failureCount; private set => SetProperty(ref _failureCount, value); }
    public string LastMessage { get => _lastMessage; private set => SetProperty(ref _lastMessage, value); }

    public bool LastSuccess
    {
        get => _lastSuccess;
        private set { if (SetProperty(ref _lastSuccess, value)) OnPropertyChanged(nameof(State)); }
    }

    public string LastElapsed { get => _lastElapsed; private set => SetProperty(ref _lastElapsed, value); }

    private IProtocolClient EnsureClient() => _client ??= Plugin.CreateClient(Settings, new ClientContext(TrafficLog));

    private async Task ConnectAsync()
    {
        _isBusy = true;
        try
        {
            await EnsureClient().ConnectAsync();
            LastMessage = $"Connected to {Endpoint}";
            LastSuccess = true;
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            LastSuccess = false;
        }
        finally
        {
            _isBusy = false;
            IsConnected = _client?.IsConnected == true;
        }
    }

    public async Task DisconnectAsync()
    {
        StopPolling();
        if (_client != null)
        {
            var client = _client;
            _client = null; // recreated on next use so edited settings apply
            try { await client.DisposeAsync(); }
            catch (Exception ex) { TrafficLog.Warn("", $"Disconnect: {ex.Message}"); }
        }
        IsConnected = false;
        LastMessage = "Disconnected";
        LastSuccess = true;
    }

    private Task ExecuteOnceAsync() => ExecuteAsync(CancellationToken.None);

    private async Task ExecuteAsync(CancellationToken ct)
    {
        _isBusy = true;
        try
        {
            ClientResult result;
            try { result = await EnsureClient().ExecuteAsync(Request, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { result = ClientResult.Fail(ex.Message, TimeSpan.Zero); }
            Apply(result);
        }
        finally
        {
            _isBusy = false;
            IsConnected = _client?.IsConnected == true;
        }
    }

    private void Apply(ClientResult result)
    {
        LastSuccess = result.Success;
        LastMessage = result.Message;
        LastElapsed = $"{result.Elapsed.TotalMilliseconds:F1} ms";
        if (result.Success) SuccessCount++; else FailureCount++;
        if (!result.Success) return;

        // Update in place where possible so the grid doesn't flicker while polling.
        bool shapeChanged = Results.Count != result.Rows.Count;
        for (int i = 0; i < result.Rows.Count; i++)
        {
            if (i < Results.Count)
            {
                if (Results[i] == result.Rows[i]) continue;
                if (Results[i].Address != result.Rows[i].Address) shapeChanged = true;
                bool selected = ReferenceEquals(SelectedResult, Results[i]);
                Results[i] = result.Rows[i];
                if (selected) SelectedResult = Results[i];
            }
            else Results.Add(result.Rows[i]);
        }
        while (Results.Count > result.Rows.Count) Results.RemoveAt(Results.Count - 1);
        if (shapeChanged) OnPropertyChanged(nameof(SourceOptions));

        ApplyMappings(result.Rows);
    }

    /// <summary>Copies mapped rows into their project tags.</summary>
    private void ApplyMappings(IReadOnlyList<ClientResultRow> rows)
    {
        if (Mappings.Count == 0) return;
        var byAddress = new Dictionary<string, ClientResultRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) byAddress.TryAdd(r.Address, r);

        var targets = new HashSet<SimTag>();
        foreach (var m in Mappings)
        {
            if (m.Source.Length == 0 || m.Target.Length == 0) { m.SetStatus("Pick a source row and a target tag", true); continue; }
            var tag = _tags.Find(m.Target);
            if (tag == null) { m.SetStatus($"No tag '{m.Target}'", true); continue; }
            if (_isPolling) targets.Add(tag);
            if (!byAddress.TryGetValue(m.Source, out var row)) { m.SetStatus("Not in the last result", true); continue; }
            var binding = tag.Binding;
            if (binding == null) { m.SetStatus("Target tag has an invalid address", true); continue; }
            try
            {
                binding.Write(row.Typed ?? row.Value);
                m.SetStatus($"{row.Value} → {ValueConverter.Format(binding.Read())}", false);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException)
            {
                m.SetStatus(ex.Message, true);
            }
        }
        if (_isPolling) HoldTargets(targets);
    }

    /// <summary>While polling, mapped tags are held so their own simulation doesn't fight the polled values.</summary>
    private void HoldTargets(HashSet<SimTag> targets)
    {
        foreach (var t in _held.Where(t => !targets.Contains(t)).ToList())
        {
            t.Release();
            _held.Remove(t);
        }
        foreach (var t in targets)
            if (_held.Add(t)) t.Hold();
    }

    private void ReleaseTargets()
    {
        foreach (var t in _held) t.Release();
        _held.Clear();
    }

    public async void StartPolling()
    {
        if (_isPolling) return;
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        IsPolling = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                await ExecuteAsync(ct);
                var wait = TimeSpan.FromMilliseconds(_pollIntervalMs) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                else await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            LastSuccess = false;
        }
        finally
        {
            IsPolling = false;
            ReleaseTargets();
        }
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private void AddMapping()
    {
        var m = new ClientMappingViewModel(SelectedResult?.Address ?? string.Empty, string.Empty);
        Mappings.Add(m);
        SelectedMapping = m;
    }

    private void MapSelectedRow()
    {
        if (SelectedResult is not { } row) return;
        var m = Mappings.FirstOrDefault(x => string.Equals(x.Source, row.Address, StringComparison.OrdinalIgnoreCase));
        if (m == null)
        {
            m = new ClientMappingViewModel(row.Address, string.Empty);
            Mappings.Add(m);
        }
        SelectedMapping = m;
    }

    private void MapAllRows()
    {
        foreach (var row in Results)
        {
            if (Mappings.Any(x => string.Equals(x.Source, row.Address, StringComparison.OrdinalIgnoreCase))) continue;
            Mappings.Add(new ClientMappingViewModel(row.Address, string.Empty));
        }
    }

    private void RemoveMapping()
    {
        if (SelectedMapping is not { } m) return;
        int i = Mappings.IndexOf(m);
        Mappings.Remove(m);
        SelectedMapping = Mappings.Count == 0 ? null : Mappings[Math.Min(i, Mappings.Count - 1)];
    }

    public void Refresh()
    {
        Log.Drain();
        OnPropertyChanged(nameof(Endpoint));
        if (!ReferenceEquals(_tagReferences, _tags.References)) TagReferences = _tags.References;
        if (!_isBusy) IsConnected = _client?.IsConnected == true;
    }
}
