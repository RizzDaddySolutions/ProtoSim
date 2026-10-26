using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ProtoSim.App.Mvvm;
using ProtoSim.App.Services;
using ProtoSim.Core;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;
using Microsoft.Win32;

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

        StartCommand = new AsyncRelayCommand(StartAsync, () => _server == null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _server != null);
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand(p => RemoveTag(p as TagViewModel ?? SelectedTag), p => (p ?? SelectedTag) != null);
        DuplicateTagCommand = new RelayCommand(DuplicateTag, () => SelectedTag != null);
        ExportTagsCommand = new RelayCommand(ExportTags, () => Tags.Count > 0);
        ImportTagsCommand = new RelayCommand(ImportTags);
        DropConnectionCommand = new RelayCommand(p => { if (p is ConnectionViewModel c) _server?.DropConnection(c.Id); });
        ResetStatisticsCommand = new RelayCommand(() => Statistics.Reset());
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
        Application.Current?.Dispatcher.BeginInvoke(() =>
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
        if (!isSelected) return;

        foreach (var t in Tags) t.Refresh();
        SelectedArea?.Refresh();
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
        });
    }

    private void RemoveTag(TagViewModel? vm)
    {
        if (vm == null) return;
        _engine.Remove(vm.Tag);
        vm.Detach();
        Tags.Remove(vm);
    }

    public void RemoveAllTags()
    {
        foreach (var t in Tags.ToList()) RemoveTag(t);
    }

    private void ExportTags()
    {
        var dlg = new SaveFileDialog { Title = "Export tags", Filter = "CSV (*.csv)|*.csv", FileName = $"{Name}-tags.csv" };
        if (dlg.ShowDialog() != true) return;
        TagCsv.Write(dlg.FileName, Tags.Select(t => t.Tag));
    }

    private void ImportTags()
    {
        var dlg = new OpenFileDialog { Title = "Import tags", Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var tags = TagCsv.Read(dlg.FileName);
            foreach (var t in tags) AddTag(t);
            MessageBox.Show($"Imported {tags.Count} tags.", "Import tags", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import tags", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
