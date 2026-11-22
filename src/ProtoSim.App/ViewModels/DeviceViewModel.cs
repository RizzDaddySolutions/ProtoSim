using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using ProtoSim.App.Platform;
using ProtoSim.App.Mvvm;
using ProtoSim.App.Services;
using ProtoSim.App.Views;
using ProtoSim.Core;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.App.ViewModels;

public sealed class ConnectionViewModel(ConnectionInfo info) : ObservableObject
{
    public ConnectionInfo Info { get; } = info;
    public long Id => Info.Id;
    public string RemoteEndPoint => Info.RemoteEndPoint;
    public string ConnectedAt => Info.ConnectedAt.ToString("HH:mm:ss");
    public long Requests => Info.Requests;
    public string LastActivity => Info.LastActivity.ToString("HH:mm:ss.fff");

    public void Refresh()
    {
        OnPropertyChanged(nameof(Requests));
        OnPropertyChanged(nameof(LastActivity));
    }
}

public sealed class StatisticsViewModel : ObservableObject
{
    private long _requests, _responses, _exceptions, _dropped, _malformed, _bytesIn, _bytesOut, _active, _total;
    private double _rate;
    private long _lastRequests;
    private DateTime _lastSample = DateTime.UtcNow;

    public long Requests { get => _requests; private set => SetProperty(ref _requests, value); }
    public long Responses { get => _responses; private set => SetProperty(ref _responses, value); }
    public long Exceptions { get => _exceptions; private set => SetProperty(ref _exceptions, value); }
    public long Dropped { get => _dropped; private set => SetProperty(ref _dropped, value); }
    public long Malformed { get => _malformed; private set => SetProperty(ref _malformed, value); }
    public long BytesIn { get => _bytesIn; private set => SetProperty(ref _bytesIn, value); }
    public long BytesOut { get => _bytesOut; private set => SetProperty(ref _bytesOut, value); }
    public long ActiveConnections { get => _active; private set => SetProperty(ref _active, value); }
    public long TotalConnections { get => _total; private set => SetProperty(ref _total, value); }
    public double RequestsPerSecond { get => _rate; private set => SetProperty(ref _rate, value); }

    public void Update(ServerStatistics s)
    {
        Requests = s.Requests;
        Responses = s.Responses;
        Exceptions = s.Exceptions;
        Dropped = s.Dropped;
        Malformed = s.Malformed;
        BytesIn = s.BytesIn;
        BytesOut = s.BytesOut;
        ActiveConnections = s.ActiveConnections;
        TotalConnections = s.TotalConnections;

        var now = DateTime.UtcNow;
        double secs = (now - _lastSample).TotalSeconds;
        if (secs >= 1)
        {
            RequestsPerSecond = Math.Round(Math.Max(0, s.Requests - _lastRequests) / secs, 1);
            _lastRequests = s.Requests;
            _lastSample = now;
        }
    }
}

/// <summary>A simulated device (server) of any protocol.</summary>
public sealed class DeviceViewModel : ObservableObject
{
    private readonly SimulationEngine _engine;
    private IProtocolServer? _server;
    private string _name;
    private bool _autoStart;
    private ServerState _state = ServerState.Stopped;
    private string? _lastError;
    private string _endpoint = string.Empty;
    private AreaViewModel? _selectedArea;
    private TagViewModel? _selectedTag;
    private bool _isImporting;

    public DeviceViewModel(IProtocolPlugin plugin, string name, object settings, SimulationEngine engine)
    {
        Plugin = plugin;
        _name = name;
        Settings = settings;
        _engine = engine;
        DataModel = plugin.CreateDataModel(settings);
        Areas = DataModel.Areas.Select(a => new AreaViewModel(a)).ToList();
        _selectedArea = Areas.FirstOrDefault();
        _selectedTabIndex = Areas.Count > 0 ? 0 : 1;
        Log = new LogViewModel(TrafficLog);
        _endpoint = plugin.DescribeServer(settings);
        if (DataModel is ICredentialProvider credentials) ApiClients = new ApiClientsViewModel(credentials);
        if (DataModel is IJsonDatabaseHost database) Database = new DatabaseViewModel(database, () => Name);

        StartCommand = new AsyncRelayCommand(StartAsync, () => _server == null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _server != null);
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand(p => RemoveTag(p as TagViewModel ?? SelectedTag), p => (p ?? SelectedTag) != null);
        DuplicateTagCommand = new RelayCommand(DuplicateTag, () => SelectedTag != null);
        ExportTagsCommand = new RelayCommand(ExportTags, () => Tags.Count > 0);
        ImportTagsCommand = new AsyncRelayCommand(ImportTagsAsync, () => !IsImporting);
        DropConnectionCommand = new RelayCommand(p => { if (p is ConnectionViewModel c) _server?.DropConnection(c.Id); });
        ResetStatisticsCommand = new RelayCommand(() => Statistics.Reset());
        Tags.CollectionChanged += (_, _) => _tagSnapshot = Tags.Select(t => t.Tag).ToArray();
        StartRecordingCommand = new RelayCommand(StartRecording, () => _recorder == null && Tags.Count > 0);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, () => _recorder != null);
        ReplayCommand = new AsyncRelayCommand(ReplayAsync, () => _replayer == null && Tags.Count > 0);
        StopReplayCommand = new AsyncRelayCommand(StopReplayAsync, () => _replayer != null);
        RunScenarioCommand = new RelayCommand(RunScenario, () => _scenario == null);
        StopScenarioCommand = new AsyncRelayCommand(StopScenarioAsync, () => _scenario != null);
        InsertScenarioExampleCommand = new RelayCommand(() => ScenarioText = ScenarioScript.Example, () => _scenario == null);
    }

    // ---- Scenario ----

    private ScenarioRunner? _scenario;
    private string _scenarioText = ScenarioScript.Help;
    private string _scenarioStatus = string.Empty;
    private string? _scenarioError;
    private double _scenarioSpeed = 1;

    public ICommand RunScenarioCommand { get; }
    public ICommand StopScenarioCommand { get; }
    public ICommand InsertScenarioExampleCommand { get; }

    /// <summary>The scenario script, saved with the project.</summary>
    public string ScenarioText
    {
        get => _scenarioText;
        set
        {
            if (!SetProperty(ref _scenarioText, value ?? string.Empty)) return;
            ScenarioError = null;
        }
    }

    public double ScenarioSpeed { get => _scenarioSpeed; set => SetProperty(ref _scenarioSpeed, value); }
    public bool IsScenarioRunning => _scenario != null;
    public string ScenarioStatus { get => _scenarioStatus; private set => SetProperty(ref _scenarioStatus, value); }
    public string? ScenarioError { get => _scenarioError; private set => SetProperty(ref _scenarioError, value); }

    private void RunScenario()
    {
        ScenarioScript script;
        try
        {
            script = ScenarioScript.Parse(ScenarioText);
        }
        catch (FormatException ex)
        {
            ScenarioError = ex.Message;
            return;
        }
        if (script.Steps.Count == 0)
        {
            ScenarioError = "The scenario has no steps.";
            return;
        }

        var runner = new ScenarioRunner(script, new ScenarioHost(this, Tags.Select(t => t.Tag).ToList()), ScenarioSpeed);
        if (runner.MissingTags.Count > 0)
        {
            ScenarioError = $"No tag named {string.Join(", ", runner.MissingTags.Select(t => $"'{t}'"))} on this device (tags are matched by name or address).";
            return;
        }

        runner.Completed += (r, error) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(r, _scenario)) return;
            _scenario = null;
            r.Dispose();
            if (error != null) ScenarioError = error.Message;
            ScenarioStateChanged();
        });
        ScenarioError = null;
        _scenario = runner;
        runner.Start();
        ScenarioStateChanged();
    }

    private async Task StopScenarioAsync()
    {
        var runner = _scenario;
        if (runner == null) return;
        _scenario = null;
        await runner.StopAsync();
        runner.Dispose();
        TrafficLog.Info("", "Scenario stopped by user");
        ScenarioStateChanged();
    }

    private void ScenarioStateChanged()
    {
        OnPropertyChanged(nameof(IsScenarioRunning));
        UpdateScenarioStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateScenarioStatus()
    {
        if (_scenario is not { } s)
        {
            ScenarioStatus = string.Empty;
            return;
        }
        static string T(double v) => TimeSpan.FromSeconds(v).ToString(v >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        ScenarioStatus = $"▶ {T(s.ElapsedSeconds)} / {T(s.DurationSeconds)} · {s.Speed:0.##}×" +
                         (s.Loops > 0 ? $" · repeat {s.Loops + 1}" : "") +
                         (s.CurrentStep.Length > 0 ? $" · {s.CurrentStep}" : "") +
                         (s.Errors > 0 ? $" · {s.Errors} problem(s), see Traffic" : "");
    }

    /// <summary>Gives a scenario access to this device. Tags are snapshotted so worker threads never touch the collection.</summary>
    private sealed class ScenarioHost(DeviceViewModel device, IReadOnlyList<SimTag> tags) : IScenarioHost
    {
        public SimTag? FindTag(string nameOrAddress) =>
            tags.FirstOrDefault(t => string.Equals(t.Name, nameOrAddress, StringComparison.OrdinalIgnoreCase))
            ?? tags.FirstOrDefault(t => string.Equals(t.Address, nameOrAddress, StringComparison.OrdinalIgnoreCase));

        public FaultSettings Faults => device.Faults;

        public Task SetDeviceRunningAsync(bool running)
        {
            return Dispatcher.UIThread.InvokeAsync(() => running ? device.StartAsync() : device.StopAsync());
        }

        public void Log(string message) => device.TrafficLog.Info("", message);
    }

    // ---- Record & replay ----

    private TagRecorder? _recorder;
    private TagReplayer? _replayer;
    private string _replayName = string.Empty;
    private string _recordReplayStatus = string.Empty;
    private double _replaySpeed = 1;
    private bool _replayLoop;
    private int _recordIntervalMs = 100;

    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand StopReplayCommand { get; }

    public static double[] ReplaySpeeds { get; } = [0.25, 0.5, 1, 2, 5, 10, 60];
    public double ReplaySpeed { get => _replaySpeed; set => SetProperty(ref _replaySpeed, value); }
    public bool ReplayLoop { get => _replayLoop; set => SetProperty(ref _replayLoop, value); }

    /// <summary>How often tags are sampled while recording (only changes are kept).</summary>
    public int RecordIntervalMs { get => _recordIntervalMs; set => SetProperty(ref _recordIntervalMs, Math.Clamp(value, 10, 60_000)); }

    public bool IsRecording => _recorder != null;
    public bool IsReplaying => _replayer != null;
    public string RecordReplayStatus { get => _recordReplayStatus; private set => SetProperty(ref _recordReplayStatus, value); }

    /// <summary>Tags as an array the recorder's worker thread can read safely; refreshed when the collection changes.</summary>
    private volatile SimTag[] _tagSnapshot = [];

    private void StartRecording()
    {
        _tagSnapshot = Tags.Select(t => t.Tag).ToArray();
        _recorder = new TagRecorder(() => _tagSnapshot, RecordIntervalMs);
        _recorder.Start();
        RecordStateChanged();
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder == null) return;
        var recording = await recorder.StopAsync();
        recorder.Dispose();
        _recorder = null;
        RecordStateChanged();

        if (recording.Samples.Count == 0)
        {
            MessageDialog.Show("Nothing was recorded.", "Recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Save recording",
            Filter = "Recording CSV (*.csv)|*.csv",
            FileName = $"{Name}-recording-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            RecordingCsv.Write(dlg.FileName, recording);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(ex.Message, "Save recording", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ReplayAsync()
    {
        var dlg = new OpenFileDialog { Title = "Replay recording", Filter = "Recording CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        TagRecording recording;
        try
        {
            recording = await Task.Run(() => RecordingCsv.Read(dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageDialog.Show(ex.Message, "Replay recording", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (recording.Samples.Count == 0)
        {
            MessageDialog.Show("The file contains no values.", "Replay recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Match recorded names to tags by name, then by address; snapshot so the worker never touches the collection.
        var byName = new Dictionary<string, SimTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tags) byName.TryAdd(t.Tag.Name, t.Tag);
        foreach (var t in Tags) byName.TryAdd(t.Tag.Address, t.Tag);
        var replayer = new TagReplayer(recording, n => byName.GetValueOrDefault(n), ReplaySpeed, ReplayLoop);

        if (replayer.MissingTags.Count == recording.TagNames.Count)
        {
            MessageDialog.Show($"None of the recorded tags ({string.Join(", ", recording.TagNames.Take(10))}) exist on this device. Tags are matched by name or address.",
                "Replay recording", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (replayer.MissingTags.Count > 0)
        {
            var answer = MessageDialog.Show(
                $"These recorded tags aren't on this device and will be skipped:\n{string.Join(", ", replayer.MissingTags.Take(20))}{(replayer.MissingTags.Count > 20 ? " …" : "")}\n\nReplay the rest?",
                "Replay recording", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        replayer.Completed += r => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(r, _replayer)) return;
            _replayer = null;
            r.Dispose();
            RecordStateChanged();
        });
        _replayName = Path.GetFileName(dlg.FileName);
        _replayer = replayer;
        replayer.Start();
        RecordStateChanged();
    }

    private async Task StopReplayAsync()
    {
        var replayer = _replayer;
        if (replayer == null) return;
        _replayer = null;
        await replayer.StopAsync();
        replayer.Dispose();
        RecordStateChanged();
    }

    /// <summary>Stops recording (discarding it) and replay, e.g. when the device is removed.</summary>
    public async Task StopRecordAndReplayAsync()
    {
        await StopScenarioAsync();
        if (_recorder is { } rec)
        {
            _recorder = null;
            await rec.StopAsync();
            rec.Dispose();
        }
        await StopReplayAsync();
    }

    private void RecordStateChanged()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsReplaying));
        UpdateRecordReplayStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateRecordReplayStatus()
    {
        static string T(double s) => TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        var parts = new List<string>();
        if (_recorder is { } rec)
            parts.Add($"● Recording · {rec.Count:N0} values · {T(rec.ElapsedSeconds)}{(rec.IsFull ? " · full" : "")}");
        if (_replayer is { } rep)
            parts.Add($"▶ Replaying {_replayName} · {T(rep.PositionSeconds)} / {T(rep.DurationSeconds)} · {rep.Speed:0.##}×" +
                      (rep.Loop ? $" · loop {rep.Loops + 1}" : "") + (rep.Errors > 0 ? $" · {rep.Errors} bad values" : ""));
        RecordReplayStatus = string.Join("    ", parts);
    }

    public IProtocolPlugin Plugin { get; }
    public object Settings { get; }
    public FaultSettings Faults { get; init; } = new();
    public IDeviceDataModel DataModel { get; }
    public ServerStatistics Statistics { get; } = new();
    public TrafficLog TrafficLog { get; } = new();
    public LogViewModel Log { get; }
    public StatisticsViewModel Stats { get; } = new();
    public IReadOnlyList<AreaViewModel> Areas { get; }

    /// <summary>False for node-based protocols (e.g. OPC UA), which have no register tables.</summary>
    public bool HasDataAreas => Areas.Count > 0;

    /// <summary>Set for devices that authenticate clients (e.g. REST API); shows the API clients tab.</summary>
    public ApiClientsViewModel? ApiClients { get; }
    public bool HasApiClients => ApiClients != null;

    /// <summary>Set for devices backed by a JSON database (e.g. REST API); shows the Database tab.</summary>
    public DatabaseViewModel? Database { get; }
    public bool HasDatabase => Database != null;

    /// <summary>Opens on the Data tab when there is one, otherwise on Tags.</summary>
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    private int _selectedTabIndex;
    public ObservableCollection<TagViewModel> Tags { get; } = [];
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }
    public ICommand DuplicateTagCommand { get; }
    public ICommand ExportTagsCommand { get; }
    public ICommand ImportTagsCommand { get; }
    public ICommand DropConnectionCommand { get; }
    public ICommand ResetStatisticsCommand { get; }

    public bool IsImporting
    {
        get => _isImporting;
        private set => SetProperty(ref _isImporting, value);
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool AutoStart { get => _autoStart; set => SetProperty(ref _autoStart, value); }
    public string ProtocolName => Plugin.DisplayName;
    public string AddressHelp => Plugin.AddressHelp;
    public string Endpoint { get => _endpoint; private set => SetProperty(ref _endpoint, value); }

    public ServerState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(StatusText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? LastError
    {
        get => _lastError;
        private set { if (SetProperty(ref _lastError, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsRunning => _state == ServerState.Running;
    public bool CanEditSettings => _state is ServerState.Stopped or ServerState.Faulted;

    public string StatusText => _state switch
    {
        ServerState.Faulted => $"Faulted: {_lastError}",
        _ => _state.ToString(),
    };

    public AreaViewModel? SelectedArea { get => _selectedArea; set => SetProperty(ref _selectedArea, value); }
    public TagViewModel? SelectedTag { get => _selectedTag; set => SetProperty(ref _selectedTag, value); }

    /// <summary>Every selected row of the tag grid (Shift+click for a range, Ctrl+click for single rows).</summary>
    public IReadOnlyList<TagViewModel> SelectedTags { get; set; } = [];

    public async Task StartAsync()
    {
        if (_server != null) return;
        var server = Plugin.CreateServer(Settings, DataModel, new ServerContext(TrafficLog, Faults, Statistics));
        server.StateChanged += OnServerStateChanged;
        _server = server;
        State = ServerState.Starting;
        try
        {
            await server.StartAsync();
            LastError = null;
            State = ServerState.Running;
        }
        catch (Exception ex)
        {
            server.StateChanged -= OnServerStateChanged;
            _server = null;
            await server.DisposeAsync();
            LastError = ex.Message;
            State = ServerState.Faulted;
        }
    }

    public async Task StopAsync()
    {
        var server = _server;
        if (server == null) return;
        State = ServerState.Stopping;
        try
        {
            await server.StopAsync();
        }
        finally
        {
            server.StateChanged -= OnServerStateChanged;
            await server.DisposeAsync();
            _server = null;
            Connections.Clear();
            State = ServerState.Stopped;
        }
    }

    private void OnServerStateChanged(object? sender, EventArgs e)
    {
        var server = (IProtocolServer)sender!;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(server, _server)) return;
            if (server.State == ServerState.Faulted) LastError = server.LastError;
            State = server.State;
        });
    }

    /// <summary>Called by the UI timer.</summary>
    public void Refresh(bool isSelected)
    {
        Log.Drain();
        Stats.Update(Statistics);
        Endpoint = Plugin.DescribeServer(Settings);
        if (_recorder != null || _replayer != null) UpdateRecordReplayStatus();
        if (_scenario != null) UpdateScenarioStatus();
        if (!isSelected) return;

        foreach (var t in Tags) t.Refresh();
        SelectedArea?.Refresh();
        ApiClients?.Refresh();
        Database?.Refresh();
        SyncConnections();
    }

    private void SyncConnections()
    {
        var current = _server?.GetConnections() ?? [];
        var ids = current.Select(c => c.Id).ToHashSet();
        for (int i = Connections.Count - 1; i >= 0; i--)
            if (!ids.Contains(Connections[i].Id)) Connections.RemoveAt(i);
        foreach (var c in current)
        {
            var existing = Connections.FirstOrDefault(x => x.Id == c.Id);
            if (existing == null) Connections.Add(new ConnectionViewModel(c));
            else existing.Refresh();
        }
    }

    public TagViewModel AddTag(SimTag tag)
    {
        var vm = new TagViewModel(tag, DataModel);
        Tags.Add(vm);
        _engine.Add(tag);
        return vm;
    }

    private void AddTag()
    {
        var used = Tags.Select(t => t.Tag.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = Tags.Count + 1;
        while (used.Contains($"Tag {n}")) n++;
        SelectedTag = AddTag(new SimTag { Name = $"Tag {n}", Address = Plugin.DefaultTagAddress(n) });
    }

    private void DuplicateTag()
    {
        if (SelectedTag == null) return;
        var src = SelectedTag.Tag;
        SelectedTag = AddTag(new SimTag
        {
            Name = src.Name + " (copy)",
            Address = src.Address,
            DataType = src.DataType,
            WordOrder = src.WordOrder,
            Description = src.Description,
            Simulation = src.Simulation.Clone(),
            Writable = src.Writable,
        });
    }

    private void RemoveTag(TagViewModel? vm)
    {
        if (vm == null) return;
        _engine.Remove(vm.Tag);
        vm.Detach();
        Tags.Remove(vm);
        if (ReferenceEquals(SelectedTag, vm)) SelectedTag = null;
    }

    /// <summary>Removes several tags at once (the grid's multi-selection).</summary>
    public void RemoveTags(IEnumerable<TagViewModel> tags)
    {
        foreach (var t in tags.ToList()) RemoveTag(t);
        SelectedTags = [];
    }

    public void RemoveAllTags()
    {
        foreach (var t in Tags.ToList()) RemoveTag(t);
        SelectedTags = [];
    }

    private void ExportTags()
    {
        var dlg = new SaveFileDialog { Title = "Export tags", Filter = "CSV (*.csv)|*.csv", FileName = $"{Name}-tags.csv" };
        if (dlg.ShowDialog() != true) return;
        TagCsv.Write(dlg.FileName, Tags.Select(t => t.Tag));
    }

    private async Task ImportTagsAsync()
    {
        var dlg = new OpenFileDialog { Title = "Import tags", Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        IsImporting = true;
        try
        {
            var tags = await Task.Run(() => TagCsv.Read(dlg.FileName));
            if (tags.Count == 0)
            {
                MessageDialog.Show("The file contains no tags.", "Import tags", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Only ask how to merge when there is something to merge with.
            if (Tags.Count > 0)
            {
                IsImporting = false;
                var choice = new ImportTagsDialog(Path.GetFileName(dlg.FileName), tags.Count, Tags.Count);
                if (!Modal.ShowDialog(choice)) return;
                IsImporting = true;
                if (choice.Mode == TagImportMode.Overwrite) RemoveAllTags();
            }

            for (int i = 0; i < tags.Count; i++)
            {
                AddTag(tags[i]);
                if ((i & 63) == 63) await Task.Delay(1); // let the loading overlay render on large imports
            }
            IsImporting = false;
            MessageDialog.Show($"Imported {tags.Count} tags.", "Import tags", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(ex.Message, "Import tags", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsImporting = false;
        }
    }
}
