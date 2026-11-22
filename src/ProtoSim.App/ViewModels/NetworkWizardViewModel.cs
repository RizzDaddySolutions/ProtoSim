using System.Collections.ObjectModel;
using System.Windows.Input;
using ProtoSim.App.Platform;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Data;
using ProtoSim.Core.Network;
using ProtoSim.Core.Project;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.App.ViewModels;

public enum WizardStep { Source, Points, Target, Finish }

public enum SourceKind { ThisComputer, OtherComputer, Manual }

/// <summary>A device the wizard can link from: on this computer, or in another instance's catalog.</summary>
public sealed class SourceDeviceOption
{
    public required string Name { get; init; }
    public required IProtocolPlugin Plugin { get; init; }
    public required object ServerSettings { get; init; }
    public required string Host { get; init; }
    public required bool Running { get; init; }
    public required string Machine { get; init; }
    public required IReadOnlyList<SharedTag> Tags { get; init; }

    /// <summary>The device on this computer; null for remote devices.</summary>
    public DeviceViewModel? Local { get; init; }

    public string Protocol => Plugin.DisplayName;
    public string Details => $"{Protocol} · {Tags.Count} tag{(Tags.Count == 1 ? "" : "s")} · {(Running ? "running" : "stopped")}";
}

/// <summary>One row of the Points and Target pages.</summary>
public sealed class WizardPoint : ObservableObject
{
    private bool _include = true;
    private string _name;
    private string _address;
    private TagDataType _dataType;
    private WordOrder _wordOrder;
    private LinkDirection _direction;
    private string _existingTarget = string.Empty;
    private string _newAddress = string.Empty;
    private string _check = string.Empty;
    private bool _checkFailed;

    public WizardPoint(string name, string address, TagDataType type, WordOrder order, bool writable, string description = "")
    {
        _name = name;
        _address = address;
        _dataType = type;
        _wordOrder = order;
        SourceWritable = writable;
        Description = description;
    }

    public bool Include { get => _include; set => SetProperty(ref _include, value); }

    /// <summary>Source tag name; also the name of the tag created in the target device.</summary>
    public string Name { get => _name; set => SetProperty(ref _name, value?.Trim() ?? string.Empty); }

    /// <summary>Address on the source, in the client's syntax.</summary>
    public string Address { get => _address; set => SetProperty(ref _address, value?.Trim() ?? string.Empty); }

    public TagDataType DataType { get => _dataType; set => SetProperty(ref _dataType, value); }
    public WordOrder WordOrder { get => _wordOrder; set => SetProperty(ref _wordOrder, value); }
    public LinkDirection Direction { get => _direction; set => SetProperty(ref _direction, value); }

    /// <summary>The source tag's Writeable box (always true for manual points).</summary>
    public bool SourceWritable { get; }
    public string Description { get; }

    /// <summary>"Map to existing tags": the project tag as Device/Tag.</summary>
    public string ExistingTarget { get => _existingTarget; set => SetProperty(ref _existingTarget, value?.Trim() ?? string.Empty); }

    /// <summary>"Create tags": the new tag's address in the target device.</summary>
    public string NewAddress { get => _newAddress; set => SetProperty(ref _newAddress, value?.Trim() ?? string.Empty); }

    public string Check { get => _check; set => SetProperty(ref _check, value); }
    public bool CheckFailed { get => _checkFailed; set => SetProperty(ref _checkFailed, value); }
}

/// <summary>
/// Network wizard: connects a simulator (a device on this computer, a device in another ProtoSim on the network,
/// or any server) to tags of a device here, by creating a <see cref="NetworkLink"/>.
/// </summary>
public sealed class NetworkWizardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private WizardStep _step = WizardStep.Source;
    private SourceKind _sourceKind = SourceKind.ThisComputer;
    private SourceDeviceOption? _selectedSource;
    private RemoteInstance? _selectedInstance;
    private string _hostText = string.Empty;
    private string _searchStatus = string.Empty;
    private bool _isSearching;
    private IProtocolPlugin? _manualPlugin;
    private object? _manualSettings;
    private DeviceViewModel? _targetDevice;
    private bool _createTags = true;
    private string _linkName = string.Empty;
    private int _pollIntervalMs = 1000;
    private bool _startNow = true;
    private bool _startSource = true;
    private string _error = string.Empty;
    private IReadOnlyList<SourceDeviceOption> _remoteDevices = [];

    public NetworkWizardViewModel(MainViewModel main)
    {
        _main = main;
        LocalDevices = main.Devices.Where(d => d.Plugin.SupportsLinks)
            .Select(d => new SourceDeviceOption
            {
                Name = d.Name, Plugin = d.Plugin, ServerSettings = d.Settings, Host = "127.0.0.1", Running = d.IsRunning, Machine = "This computer",
                Tags = MainViewModel.SharedTags(d), Local = d,
            }).ToList();
        ManualPlugins = main.Catalog.Plugins.Where(p => p.SupportsLinks).ToList();
        TargetDevices = main.Devices.ToList();
        _targetDevice = TargetDevices.FirstOrDefault();
        _manualPlugin = ManualPlugins.FirstOrDefault();
        _manualSettings = _manualPlugin?.CreateClientSettings();
        _selectedSource = LocalDevices.FirstOrDefault();
        if (LocalDevices.Count == 0) _sourceKind = SourceKind.OtherComputer;

        BackCommand = new RelayCommand(Back, () => Step != WizardStep.Source);
        NextCommand = new AsyncRelayCommand(NextAsync, () => Step != WizardStep.Finish);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching);
        SelectAllCommand = new RelayCommand(() => { foreach (var p in Points) p.Include = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var p in Points) p.Include = false; });
        AddManualPointCommand = new RelayCommand(() => Points.Add(new WizardPoint($"Point {Points.Count + 1}", string.Empty, TagDataType.Float32, WordOrder.ABCD, true)));
        RemoveManualPointCommand = new RelayCommand(p => { if (p is WizardPoint wp) Points.Remove(wp); });
        AllReadWriteCommand = new RelayCommand(() => SetDirections(LinkDirection.ReadWrite), () => CanWriteBack);
        AllReadCommand = new RelayCommand(() => SetDirections(LinkDirection.Read));
    }

    public event Action<bool>? CloseRequested;

    /// <summary>The link the wizard created, when it finished.</summary>
    public LinkViewModel? Result { get; private set; }

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand AddManualPointCommand { get; }
    public ICommand RemoveManualPointCommand { get; }
    public ICommand AllReadWriteCommand { get; }
    public ICommand AllReadCommand { get; }

    public IReadOnlyList<SourceDeviceOption> LocalDevices { get; }
    public ObservableCollection<RemoteInstance> Instances { get; } = [];
    public IReadOnlyList<IProtocolPlugin> ManualPlugins { get; }
    public IReadOnlyList<DeviceViewModel> TargetDevices { get; }
    public ObservableCollection<WizardPoint> Points { get; } = [];
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<WordOrder> WordOrders { get; } = Enum.GetValues<WordOrder>();
    public IReadOnlyList<LinkDirection> Directions => CanWriteBack ? Enum.GetValues<LinkDirection>() : [LinkDirection.Read];
    public IReadOnlyList<string> TagReferences => _main.TagDirectory.References;

    public WizardStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(StepNumber));
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(IsLastStep));
            OnPropertyChanged(nameof(IsSourceStep));
            OnPropertyChanged(nameof(IsPointsStep));
            OnPropertyChanged(nameof(IsTargetStep));
            OnPropertyChanged(nameof(IsFinishStep));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int StepNumber => (int)Step + 1;
    public bool IsLastStep => Step == WizardStep.Finish;
    public bool IsSourceStep => Step == WizardStep.Source;
    public bool IsPointsStep => Step == WizardStep.Points;
    public bool IsTargetStep => Step == WizardStep.Target;
    public bool IsFinishStep => Step == WizardStep.Finish;

    public string StepTitle => Step switch
    {
        WizardStep.Source => "Where do the values come from?",
        WizardStep.Points => "Which values?",
        WizardStep.Target => "Where do they go?",
        _ => "Name and start the link",
    };

    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    // ---- Step 1: source ----

    public SourceKind SourceKind
    {
        get => _sourceKind;
        set
        {
            if (!SetProperty(ref _sourceKind, value)) return;
            OnPropertyChanged(nameof(IsThisComputer));
            OnPropertyChanged(nameof(IsOtherComputer));
            OnPropertyChanged(nameof(IsManual));
            Error = string.Empty;
            SelectedSource = value switch
            {
                SourceKind.ThisComputer => LocalDevices.FirstOrDefault(),
                SourceKind.OtherComputer => RemoteDevices.FirstOrDefault(),
                _ => null,
            };
            if (value == SourceKind.OtherComputer && Instances.Count == 0 && !IsSearching) SearchCommand.Execute(null);
        }
    }

    public bool IsThisComputer { get => _sourceKind == SourceKind.ThisComputer; set { if (value) SourceKind = SourceKind.ThisComputer; } }
    public bool IsOtherComputer { get => _sourceKind == SourceKind.OtherComputer; set { if (value) SourceKind = SourceKind.OtherComputer; } }
    public bool IsManual { get => _sourceKind == SourceKind.Manual; set { if (value) SourceKind = SourceKind.Manual; } }

    public SourceDeviceOption? SelectedSource { get => _selectedSource; set => SetProperty(ref _selectedSource, value); }

    public RemoteInstance? SelectedInstance
    {
        get => _selectedInstance;
        set { if (SetProperty(ref _selectedInstance, value) && value != null) _ = LoadCatalogAsync(value); }
    }

    public IReadOnlyList<SourceDeviceOption> RemoteDevices { get => _remoteDevices; private set => SetProperty(ref _remoteDevices, value); }

    /// <summary>Computers to ask directly (for ProtoSim instances on other subnets), separated by commas.</summary>
    public string HostText { get => _hostText; set => SetProperty(ref _hostText, value ?? string.Empty); }
    public string SearchStatus { get => _searchStatus; private set => SetProperty(ref _searchStatus, value); }

    public bool IsSearching
    {
        get => _isSearching;
        private set { if (SetProperty(ref _isSearching, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public IProtocolPlugin? ManualPlugin
    {
        get => _manualPlugin;
        set
        {
            if (!SetProperty(ref _manualPlugin, value)) return;
            ManualSettings = value?.CreateClientSettings();
            OnPropertyChanged(nameof(ManualAddressHelp));
        }
    }

    public object? ManualSettings { get => _manualSettings; private set => SetProperty(ref _manualSettings, value); }
    public string ManualAddressHelp => _manualPlugin == null ? string.Empty : $"Addresses use the client's syntax. On a ProtoSim {_manualPlugin.DisplayName} device: {_manualPlugin.AddressHelp}";

    private async Task SearchAsync()
    {
        IsSearching = true;
        SearchStatus = "Searching the network…";
        try
        {
            var hosts = ClientValue.SplitList(HostText);
            var found = await DiscoveryService.DiscoverAsync(TimeSpan.FromSeconds(1.5), hosts);
            var keep = SelectedInstance?.Id;
            Instances.Clear();
            foreach (var i in found) Instances.Add(i);
            SearchStatus = found.Count == 0
                ? "No other ProtoSim found. Check that Network › Share devices on the network is on there, and that the firewall allows ProtoSim. Computers on another subnet: type their name or IP above."
                : $"Found {found.Count} ProtoSim instance{(found.Count == 1 ? "" : "s")}.";
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == keep) ?? Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task LoadCatalogAsync(RemoteInstance instance)
    {
        RemoteDevices = [];
        SelectedSource = null;
        SearchStatus = $"Reading the devices of {instance.Name}…";
        try
        {
            var catalog = await DiscoveryService.FetchCatalogAsync(instance.Host, instance.CatalogPort);
            if (!ReferenceEquals(SelectedInstance, instance)) return;
            var devices = new List<SourceDeviceOption>();
            var missing = new HashSet<string>();
            foreach (var d in catalog.Devices)
            {
                var plugin = _main.Catalog.Find(d.PluginId);
                if (plugin is not { SupportsLinks: true }) { missing.Add(d.Protocol.Length > 0 ? d.Protocol : d.PluginId); continue; }
                var settings = ProjectSerializer.FromElement(d.ServerSettings, plugin.ServerSettingsType) ?? plugin.CreateServerSettings([]);
                devices.Add(new SourceDeviceOption
                {
                    Name = d.Name, Plugin = plugin, ServerSettings = settings, Host = instance.Host, Running = d.Running, Machine = instance.Name, Tags = d.Tags,
                });
            }
            RemoteDevices = devices;
            SelectedSource = devices.FirstOrDefault();
            SearchStatus = $"{instance.Name} shares {devices.Count} device{(devices.Count == 1 ? "" : "s")}" +
                           (missing.Count > 0 ? $" (not installed here: {string.Join(", ", missing)})." : ".");
        }
        catch (Exception ex)
        {
            SearchStatus = $"Could not read the devices of {instance.Name} ({instance.Host}): {ex.Message}";
        }
    }

    // ---- Step 2: points ----

    private IProtocolPlugin? SourcePlugin => IsManual ? _manualPlugin : _selectedSource?.Plugin;
    public bool CanWriteBack => SourcePlugin?.SupportsLinkWrites == true;
    public bool IsDeviceSource => !IsManual;
    public string PointsHint => IsManual
        ? "Add the values to read. " + ManualAddressHelp
        : "Tick the tags to bring over. Read + write sends changes made here back to the source; the source tag must be Writeable.";

    private void SetDirections(LinkDirection d)
    {
        foreach (var p in Points.Where(p => p.Include))
            p.Direction = d == LinkDirection.ReadWrite && p.SourceWritable && CanWrite(p) ? LinkDirection.ReadWrite : LinkDirection.Read;
    }

    private bool CanWrite(WizardPoint p) =>
        SourcePlugin is { } plugin && p.Address.Length > 0 && LinkPoints.CanWrite(plugin, new LinkPoint(p.Address, p.DataType, p.WordOrder));

    // ---- Step 3: target ----

    public DeviceViewModel? TargetDevice
    {
        get => _targetDevice;
        set { if (SetProperty(ref _targetDevice, value)) SuggestTargets(); }
    }

    public bool CreateTags
    {
        get => _createTags;
        set
        {
            if (!SetProperty(ref _createTags, value)) return;
            OnPropertyChanged(nameof(MapExisting));
            SuggestTargets();
        }
    }

    public bool MapExisting { get => !_createTags; set => CreateTags = !value; }
    public IEnumerable<WizardPoint> IncludedPoints => Points.Where(p => p.Include);
    public string TargetHint => TargetDevice == null ? string.Empty : $"Addresses in {TargetDevice.Name}: {TargetDevice.AddressHelp}";

    /// <summary>Fills in addresses for new tags, or existing tags with the same name.</summary>
    private void SuggestTargets()
    {
        OnPropertyChanged(nameof(TargetHint));
        if (TargetDevice is not { } device) return;
        var existing = device.Tags.Select(t => (t.Tag.Address, t.Tag.DataType)).ToList();
        foreach (var p in IncludedPoints)
        {
            if (_createTags)
            {
                p.NewAddress = device.Plugin.SuggestTagAddress(existing, p.DataType, p.Name);
                existing.Add((p.NewAddress, p.DataType));
            }
            else if (p.ExistingTarget.Length == 0 && device.Tags.FirstOrDefault(t => string.Equals(t.Tag.Name, p.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                p.ExistingTarget = ProjectTagDirectory.Reference(device, same.Tag);
            }
            p.Check = string.Empty;
            p.CheckFailed = false;
        }
    }

    /// <summary>Checks every target: new addresses must bind in the target device, existing tags must exist.</summary>
    private bool CheckTargets()
    {
        bool ok = true;
        var device = TargetDevice!;
        var names = device.Tags.Select(t => t.Tag.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in IncludedPoints)
        {
            string? problem = null;
            if (_createTags)
            {
                if (p.NewAddress.Length == 0) problem = "Enter an address";
                else
                {
                    try { device.DataModel.Bind(p.NewAddress, p.DataType, p.WordOrder).Dispose(); }
                    catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or InvalidOperationException) { problem = ex.Message; }
                }
            }
            else if (p.ExistingTarget.Length == 0) problem = "Pick a tag";
            else if (_main.TagDirectory.Find(p.ExistingTarget) == null) problem = $"No tag '{p.ExistingTarget}'";
            p.Check = problem ?? (_createTags ? $"New tag '{UniqueName(p.Name, names, false)}'" : "OK");
            p.CheckFailed = problem != null;
            ok &= problem == null;
        }
        return ok;
    }

    private static string UniqueName(string name, HashSet<string> used, bool reserve)
    {
        var baseName = name.Length == 0 ? "Linked value" : name;
        var candidate = baseName;
        for (int n = 2; used.Contains(candidate); n++) candidate = $"{baseName} {n}";
        if (reserve) used.Add(candidate);
        return candidate;
    }

    // ---- Step 4: finish ----

    public string LinkName { get => _linkName; set => SetProperty(ref _linkName, value ?? string.Empty); }
    public int PollIntervalMs { get => _pollIntervalMs; set => SetProperty(ref _pollIntervalMs, Math.Clamp(value, 50, 3_600_000)); }
    public bool StartNow { get => _startNow; set => SetProperty(ref _startNow, value); }
    public bool StartSource { get => _startSource; set => SetProperty(ref _startSource, value); }
    public bool CanStartSource => !IsManual && _selectedSource is { Local: { IsRunning: false } };

    public string Summary
    {
        get
        {
            var from = IsManual ? $"{_manualPlugin?.DisplayName} server at {(_manualPlugin != null && _manualSettings != null ? _manualPlugin.DescribeClient(_manualSettings) : "")}"
                : $"{_selectedSource?.Name} ({_selectedSource?.Protocol}) on {_selectedSource?.Machine}";
            int n = IncludedPoints.Count();
            int rw = IncludedPoints.Count(p => p.Direction == LinkDirection.ReadWrite);
            return $"{n} value{(n == 1 ? "" : "s")} from {from} into {TargetDevice?.Name}" +
                   (_createTags ? " (new tags)" : "") + (rw > 0 ? $", {rw} written back to the source" : "") + ".";
        }
    }

    // ---- Navigation ----

    private void Back()
    {
        Error = string.Empty;
        if (Step > WizardStep.Source) Step--;
    }

    private async Task NextAsync()
    {
        Error = string.Empty;
        switch (Step)
        {
            case WizardStep.Source:
                if (!PreparePoints()) return;
                Step = WizardStep.Points;
                break;
            case WizardStep.Points:
                if (!IncludedPoints.Any()) { Error = "Tick at least one value."; return; }
                if (IncludedPoints.FirstOrDefault(p => p.Address.Length == 0) is { } empty) { Error = $"'{empty.Name}' has no source address."; return; }
                if (TargetDevices.Count == 0) { Error = "Add a device to this project first; links copy values into its tags."; return; }
                try { SourcePlugin!.CreateLinkReads(IncludedPoints.Select(p => new LinkPoint(p.Address, p.DataType, p.WordOrder)).ToList()); }
                catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { Error = ex.Message; return; }
                SuggestTargets();
                OnPropertyChanged(nameof(IncludedPoints));
                Step = WizardStep.Target;
                break;
            case WizardStep.Target:
                if (TargetDevice == null) { Error = "Pick the device that receives the values."; return; }
                if (!CheckTargets()) { Error = "Fix the rows marked in red."; return; }
                if (LinkName.Length == 0) LinkName = DefaultLinkName();
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(CanStartSource));
                Step = WizardStep.Finish;
                break;
        }
        await Task.CompletedTask;
    }

    private string DefaultLinkName()
    {
        var from = IsManual ? _manualPlugin?.DisplayName ?? "Server" : _selectedSource?.Name ?? "Source";
        return $"{from} → {TargetDevice?.Name}";
    }

    /// <summary>Fills the points list from the chosen source.</summary>
    private bool PreparePoints()
    {
        if (IsManual)
        {
            if (_manualPlugin == null || _manualSettings == null) { Error = "Pick a protocol."; return false; }
            if (Points.Count == 0 || Points.Any(p => !p.SourceWritable))
            {
                Points.Clear();
                Points.Add(new WizardPoint("Point 1", string.Empty, TagDataType.Float32, WordOrder.ABCD, true));
            }
        }
        else
        {
            if (_selectedSource == null) { Error = IsOtherComputer ? "Search the network and pick a device." : "Pick a device."; return false; }
            if (_selectedSource.Tags.Count == 0) { Error = $"{_selectedSource.Name} has no tags to link."; return false; }
            if (_selectedSource.Local != null && ReferenceEquals(_selectedSource.Local, TargetDevice) && TargetDevices.Count > 1)
                TargetDevice = TargetDevices.First(d => !ReferenceEquals(d, _selectedSource.Local));
            Points.Clear();
            foreach (var t in _selectedSource.Tags)
                Points.Add(new WizardPoint(t.Name, t.Address, t.DataType, t.WordOrder, t.Writable, t.Description));
        }
        OnPropertyChanged(nameof(IsDeviceSource));
        OnPropertyChanged(nameof(PointsHint));
        OnPropertyChanged(nameof(CanWriteBack));
        OnPropertyChanged(nameof(Directions));
        return true;
    }

    /// <summary>Creates the tags and the link. Called by the Finish button.</summary>
    public async Task FinishAsync()
    {
        Error = string.Empty;
        var device = TargetDevice!;
        var plugin = SourcePlugin!;
        object settings;
        string description;
        if (IsManual)
        {
            settings = _manualSettings!;
            description = $"{plugin.DescribeClient(settings)}";
        }
        else
        {
            var s = _selectedSource!;
            // Copy the server settings so the link's client settings never share objects with a local device.
            var serverCopy = ProjectSerializer.FromElement(ProjectSerializer.ToElement(s.ServerSettings), plugin.ServerSettingsType) ?? s.ServerSettings;
            settings = plugin.CreateLinkClientSettings(serverCopy, s.Host);
            description = s.Local != null ? $"{s.Name} (this computer)" : $"{s.Machine} › {s.Name}";
        }

        var link = new NetworkLink(plugin, LinkName.Trim().Length > 0 ? LinkName.Trim() : DefaultLinkName(), settings, _main.TagDirectory, description)
        {
            PollIntervalMs = PollIntervalMs,
            AutoStart = StartNow,
        };

        var names = device.Tags.Select(t => t.Tag.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in IncludedPoints.ToList())
        {
            string target;
            if (_createTags)
            {
                var name = UniqueName(p.Name, names, true);
                device.AddTag(new SimTag
                {
                    Name = name, Address = p.NewAddress, DataType = p.DataType, WordOrder = p.WordOrder,
                    Description = $"From {description}: {p.Name}",
                    // Values written back to the source start here, so local clients may change read + write points.
                    Writable = p.Direction == LinkDirection.ReadWrite && CanWrite(p),
                });
                target = $"{device.Name}/{name}";
            }
            else target = p.ExistingTarget;
            link.AddPoint(new LinkPointDocument
            {
                Source = p.Address, SourceName = IsManual ? string.Empty : p.Name, DataType = p.DataType, WordOrder = p.WordOrder,
                Target = target, Direction = p.Direction == LinkDirection.ReadWrite && CanWrite(p) ? LinkDirection.ReadWrite : LinkDirection.Read,
            });
        }

        if (StartSource && CanStartSource) await _selectedSource!.Local!.StartAsync();
        Result = _main.AddLink(link, StartNow);
        CloseRequested?.Invoke(true);
    }
}
