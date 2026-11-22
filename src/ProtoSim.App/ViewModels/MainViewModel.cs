using ProtoSim.App.Mvvm;
using ProtoSim.App.Services;
using ProtoSim.Core;
using ProtoSim.Core.Network;
using ProtoSim.Core.Project;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using ProtoSim.App.Platform;
using WindowState = Avalonia.Controls.WindowState;
using Avalonia.Threading;

namespace ProtoSim.App.ViewModels;

public sealed record PluginActionViewModel(string DisplayName, string Description, ICommand Command);

public sealed class MainViewModel : ObservableObject
{
    private const string FileFilter = "Simulator project (*.psim)|*.psim|JSON (*.json)|*.json|All files (*.*)|*.*";

    private readonly DispatcherTimer _timer;
    private object? _selected;
    private DeviceViewModel? _selectedDevice;
    private ClientViewModel? _selectedClient;
    private LinkViewModel? _selectedLink;
    private readonly DiscoveryService _discovery;
    private bool _shareOnNetwork;
    private bool _showClientsPanel = UiLayout.Current.ShowClientsPanel;
    private bool _showLinksPanel = UiLayout.Current.ShowLinksPanel;
    private bool _leftPanelCollapsed = UiLayout.Current.LeftPanelCollapsed;
    private string? _filePath;
    private bool _simulationEnabled;
    private string _statusText = "Ready";
    private bool _isLoadingProject;

    public MainViewModel(PluginCatalog catalog, IReadOnlyList<string> pluginErrors)
    {
        Catalog = catalog;
        PluginErrors = pluginErrors;
        Engine.IsEnabled = false;
        Engine.Start();

        AddDeviceActions = catalog.Plugins
            .Select(p => new PluginActionViewModel(p.DisplayName, p.Description, new RelayCommand(() => AddDevice(p)))).ToList();
        AddClientActions = catalog.Plugins.Where(p => p.SupportsClient)
            .Select(p => new PluginActionViewModel(p.DisplayName, $"Connect to an external {p.DisplayName} server or device", new RelayCommand(() => AddClient(p)))).ToList();
        Logic = new LogicViewModel(TagDirectory);

        NewCommand = new AsyncRelayCommand(NewProjectAsync);
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(false));
        SaveAsCommand = new AsyncRelayCommand(() => SaveAsync(true));
        StartAllCommand = new AsyncRelayCommand(StartAllAsync, () => Devices.Any(d => !d.IsRunning));
        StopAllCommand = new AsyncRelayCommand(StopAllAsync, () => Devices.Any(d => d.IsRunning));
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => _selected != null);
        DuplicateSelectedCommand = new RelayCommand(DuplicateSelected, () => _selectedDevice != null);
        SetThemeCommand = new RelayCommand(p => SetTheme(p as string));
        AboutCommand = new RelayCommand(ShowAbout);
        HelpCommand = new RelayCommand(ShowHelp);
        ExitCommand = new RelayCommand(() => Modal.MainWindow?.Close());
        ShowLogicCommand = new RelayCommand(ShowLogic);
        NetworkWizardCommand = new RelayCommand(ShowNetworkWizard);
        ToggleLeftPanelCommand = new RelayCommand(() => LeftPanelCollapsed = !LeftPanelCollapsed);
        StartAllLinksCommand = new RelayCommand(() => { TagDirectory.Refresh(Devices); foreach (var l in Links) l.Start(); }, () => Links.Any(l => !l.Link.IsRunning));
        StopAllLinksCommand = new AsyncRelayCommand(StopAllLinksAsync, () => Links.Any(l => l.Link.IsRunning));
        ToggleLogicDockCommand = new RelayCommand(ToggleLogicDock);
        Logic.ToggleDockCommand = ToggleLogicDockCommand;
        Logic.IsUndocked = UiLayout.Current.LogicUndocked;
        _simulationEnabled = UiLayout.Current.SimulationEnabled;
        Engine.IsEnabled = _simulationEnabled;
        var dispatcher = Dispatcher.UIThread;
        _discovery = new DiscoveryService(() => dispatcher.InvokeAsync(BuildCatalog).GetTask());
        _shareOnNetwork = UiLayout.Current.ShareOnNetwork;
        if (_shareOnNetwork) StartSharing(quiet: true);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    public PluginCatalog Catalog { get; }
    public IReadOnlyList<string> PluginErrors { get; }
    public SimulationEngine Engine { get; } = new();

    /// <summary>Every project tag by "Device/Tag"; shared by the logic designer and client tag mappings.</summary>
    public ProjectTagDirectory TagDirectory { get; } = new();

    /// <summary>Function block programs across devices (the Logic page).</summary>
    public LogicViewModel Logic { get; }
    public ICommand ShowLogicCommand { get; }
    public ICommand ToggleLogicDockCommand { get; }

    public string LogicSummary =>
        $"{Logic.Programs.Count} program{(Logic.Programs.Count == 1 ? "" : "s")} · {(Logic.IsRunning ? "running" : "stopped")}";

    public bool IsLogicSelected => ReferenceEquals(_selected, Logic);
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<ClientViewModel> Clients { get; } = [];

    /// <summary>Network links that feed tags here from other simulators (this computer or others).</summary>
    public ObservableCollection<LinkViewModel> Links { get; } = [];
    public ICommand NetworkWizardCommand { get; }
    public ICommand ToggleLeftPanelCommand { get; }

    /// <summary>Show the Clients list in the left panel (remembered between runs).</summary>
    public bool ShowClientsPanel
    {
        get => _showClientsPanel;
        set
        {
            if (!SetProperty(ref _showClientsPanel, value)) return;
            UiLayout.Current.ShowClientsPanel = value;
            UiLayout.Save();
        }
    }

    /// <summary>Show the Network links list in the left panel (remembered between runs).</summary>
    public bool ShowLinksPanel
    {
        get => _showLinksPanel;
        set
        {
            if (!SetProperty(ref _showLinksPanel, value)) return;
            UiLayout.Current.ShowLinksPanel = value;
            UiLayout.Save();
        }
    }

    /// <summary>Collapse the left panel to a narrow strip (remembered between runs).</summary>
    public bool LeftPanelCollapsed
    {
        get => _leftPanelCollapsed;
        set
        {
            if (!SetProperty(ref _leftPanelCollapsed, value)) return;
            OnPropertyChanged(nameof(LeftPanelExpanded));
            UiLayout.Current.LeftPanelCollapsed = value;
            UiLayout.Save();
        }
    }

    public bool LeftPanelExpanded => !_leftPanelCollapsed;
    public ICommand StartAllLinksCommand { get; }
    public ICommand StopAllLinksCommand { get; }
    public IReadOnlyList<PluginActionViewModel> AddDeviceActions { get; }
    public IReadOnlyList<PluginActionViewModel> AddClientActions { get; }

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand StartAllCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand DuplicateSelectedCommand { get; }
    public ICommand SetThemeCommand { get; }
    public bool IsSystemTheme => UiLayout.Current.Theme is not ("Light" or "Dark");
    public bool IsLightTheme => UiLayout.Current.Theme == "Light";
    public bool IsDarkTheme => UiLayout.Current.Theme == "Dark";
    public ICommand AboutCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ExitCommand { get; }
    public object? Selected
    {
        get => _selected;
        private set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(IsLogicSelected)); }
    }

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value) || value == null) return;
            if (_selectedClient != null) { _selectedClient = null; OnPropertyChanged(nameof(SelectedClient)); }
            if (_selectedLink != null) { _selectedLink = null; OnPropertyChanged(nameof(SelectedLink)); }
            Selected = value;
        }
    }

    public ClientViewModel? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (!SetProperty(ref _selectedClient, value) || value == null) return;
            if (_selectedDevice != null) { _selectedDevice = null; OnPropertyChanged(nameof(SelectedDevice)); }
            if (_selectedLink != null) { _selectedLink = null; OnPropertyChanged(nameof(SelectedLink)); }
            Selected = value;
        }
    }

    public LinkViewModel? SelectedLink
    {
        get => _selectedLink;
        set
        {
            if (!SetProperty(ref _selectedLink, value) || value == null) return;
            if (_selectedDevice != null) { _selectedDevice = null; OnPropertyChanged(nameof(SelectedDevice)); }
            if (_selectedClient != null) { _selectedClient = null; OnPropertyChanged(nameof(SelectedClient)); }
            Selected = value;
        }
    }

    /// <summary>Answer network wizards on other computers with this project's devices and tags.</summary>
    public bool ShareOnNetwork
    {
        get => _shareOnNetwork;
        set
        {
            if (!SetProperty(ref _shareOnNetwork, value)) return;
            if (value) StartSharing(quiet: false);
            else _discovery.Stop();
            UiLayout.Current.ShareOnNetwork = value;
            UiLayout.Save();
            OnPropertyChanged(nameof(SharingStatus));
        }
    }

    public string SharingStatus => _discovery.IsRunning
        ? $"Shared on the network (UDP {DiscoveryService.Port}, catalog TCP {_discovery.CatalogPort})"
        : _discovery.LastError ?? "Not shared on the network";

    public bool SimulationEnabled
    {
        get => _simulationEnabled;
        set
        {
            if (!SetProperty(ref _simulationEnabled, value)) return;
            Engine.IsEnabled = value;
            UiLayout.Current.SimulationEnabled = value;
            UiLayout.Save();
        }
    }

    public string Title => $"{(_filePath == null ? "Untitled" : Path.GetFileNameWithoutExtension(_filePath))} – ProtoSim";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsLoadingProject { get => _isLoadingProject; private set => SetProperty(ref _isLoadingProject, value); }

    private void Tick()
    {
        CommandManager.InvalidateRequerySuggested(); // keeps button enabled states current
        foreach (var d in Devices) d.Refresh(ReferenceEquals(d, _selected));
        Logic.Refresh(Devices, IsLogicSelected || _logicWindow != null);
        foreach (var c in Clients) c.Refresh();
        if (Links.Count > 0) TagDirectory.Refresh(Devices);
        foreach (var l in Links) l.Refresh();
        OnPropertyChanged(nameof(LogicSummary));

        int running = Devices.Count(d => d.IsRunning);
        long connections = Devices.Sum(d => d.Stats.ActiveConnections);
        double rate = Devices.Sum(d => d.Stats.RequestsPerSecond);
        int polling = Clients.Count(c => c.IsPolling);
        int linked = Links.Count(l => l.Link.IsRunning);
        StatusText = $"{running}/{Devices.Count} devices running · {connections} connections · {rate:F1} req/s · " +
                     (Clients.Count > 0 ? $"{polling}/{Clients.Count} clients polling · " : "") +
                     (Links.Count > 0 ? $"{linked}/{Links.Count} links running · " : "") +
                     $"simulation {(SimulationEnabled ? "enabled" : "disabled")}";
    }

    // ---- Devices ----

    private DeviceViewModel CreateDevice(IProtocolPlugin plugin, string name, object settings, FaultSettings? faults = null)
    {
        var device = new DeviceViewModel(plugin, name, settings, Engine) { Faults = faults ?? new FaultSettings() };
        Devices.Add(device);
        return device;
    }

    public DeviceViewModel AddDevice(IProtocolPlugin plugin)
    {
        var settings = plugin.CreateServerSettings(Devices.Where(d => d.Plugin == plugin).Select(d => d.Settings));
        var device = CreateDevice(plugin, UniqueName($"{plugin.DisplayName} device", Devices.Select(d => d.Name)), settings);
        foreach (var tag in plugin.CreateSampleTags()) device.AddTag(tag);
        SelectedDevice = device;
        return device;
    }

    public ClientViewModel AddClient(IProtocolPlugin plugin, object? settings = null)
    {
        var client = CreateClient(plugin, UniqueName($"{plugin.DisplayName} client", Clients.Select(c => c.Name)),
            settings ?? plugin.CreateClientSettings(), plugin.CreateClientRequest());
        SelectedClient = client;
        return client;
    }

    private ClientViewModel CreateClient(IProtocolPlugin plugin, string name, object settings, object request)
    {
        var client = new ClientViewModel(plugin, name, settings, request, TagDirectory);
        Clients.Add(client);
        return client;
    }

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
        {
            var name = $"{baseName} {i}";
            if (!used.Contains(name)) return name;
        }
    }

    private void DuplicateSelected()
    {
        if (_selectedDevice is not { } src) return;
        var settingsJson = ProjectSerializer.ToElement(src.Settings);
        var settings = ProjectSerializer.FromElement(settingsJson, src.Plugin.ServerSettingsType)!;
        var fresh = src.Plugin.CreateServerSettings(Devices.Where(d => d.Plugin == src.Plugin).Select(d => d.Settings));
        // The plugin takes e.g. the new port from freshly generated settings to avoid a clash.
        settings = src.Plugin.PrepareDuplicateSettings(src.Settings, settings, fresh);

        var copy = CreateDevice(src.Plugin, UniqueName(src.Name + " copy", Devices.Select(d => d.Name)), settings,
            ProjectSerializer.FromElement(ProjectSerializer.ToElement(src.Faults), typeof(FaultSettings)) as FaultSettings);
        foreach (var t in src.Tags) copy.AddTag(TagDocument.From(t.Tag).ToTag());
        CopyData(src, copy);
        copy.ScenarioText = src.ScenarioText;
        SelectedDevice = copy;
    }

    private static void CopyData(DeviceViewModel from, DeviceViewModel to)
    {
        for (int a = 0; a < from.DataModel.Areas.Count && a < to.DataModel.Areas.Count; a++)
        {
            var src = from.DataModel.Areas[a];
            var dst = to.DataModel.Areas[a];
            int n = Math.Min(src.Count, dst.Count);
            for (int i = 0; i < n; i++) dst.Write(i, src.Read(i));
        }
    }

    private async Task RemoveSelectedAsync()
    {
        switch (_selected)
        {
            case DeviceViewModel d:
                if (!Confirm($"Remove device '{d.Name}'?")) return;
                await d.StopRecordAndReplayAsync();
                await d.StopAsync();
                d.RemoveAllTags();
                Devices.Remove(d);
                break;
            case ClientViewModel c:
                if (!Confirm($"Remove client '{c.Name}'?")) return;
                await c.DisconnectAsync();
                Clients.Remove(c);
                break;
            case LinkViewModel l:
                if (!Confirm($"Remove network link '{l.Name}'? Tags it created stay in their device.")) return;
                await l.Link.StopAsync();
                Links.Remove(l);
                break;
            default:
                return;
        }
        _selectedDevice = null;
        _selectedClient = null;
        _selectedLink = null;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedClient));
        OnPropertyChanged(nameof(SelectedLink));
        Selected = null;
    }

    private static bool Confirm(string message) => Views.MessageDialog.Confirm(message);

    private async Task StartAllAsync()
    {
        foreach (var d in Devices.Where(d => !d.IsRunning).ToList()) await d.StartAsync();
    }

    private async Task StopAllAsync()
    {
        foreach (var d in Devices.Where(d => d.IsRunning).ToList()) await d.StopAsync();
    }

    private bool _shutDown;

    public async Task ShutdownAsync()
    {
        if (_shutDown) return;
        _shutDown = true;
        _shuttingDown = true;
        _timer.Stop();
        Views.LogicView.SaveOpenLayouts();
        UiLayout.Current.LogicWindowOpen = _logicWindow != null;
        CloseLogicWindow();
        UiLayout.Save();
        await Logic.ShutdownAsync();
        _discovery.Stop();
        await StopAllLinksAsync();
        foreach (var c in Clients) await c.DisconnectAsync();
        await StopAllAsync();
        Engine.Dispose();
    }

    // ---- Logic designer window ----

    private Views.LogicWindow? _logicWindow;
    private bool _shuttingDown;

    /// <summary>Shows the designer where it lives: in the main window, or in its own window when undocked.</summary>
    private void ShowLogic()
    {
        if (Logic.IsUndocked)
        {
            OpenLogicWindow();
            return;
        }
        _selectedDevice = null;
        _selectedClient = null;
        _selectedLink = null;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedClient));
        OnPropertyChanged(nameof(SelectedLink));
        Selected = Logic;
    }

    private void ToggleLogicDock()
    {
        Views.LogicView.SaveOpenLayouts(); // the designer is recreated in its new host with the same panel layout
        bool undock = !Logic.IsUndocked;
        Logic.IsUndocked = undock;
        UiLayout.Current.LogicUndocked = undock;
        if (undock)
        {
            if (IsLogicSelected)
            {
                if (Devices.Count > 0) SelectedDevice = Devices[0];
                else Selected = null;
            }
            OpenLogicWindow();
        }
        else
        {
            CloseLogicWindow();
            ShowLogic();
            Modal.MainWindow?.Activate();
        }
        UiLayout.Save();
    }

    private void OpenLogicWindow()
    {
        if (_logicWindow != null)
        {
            if (_logicWindow.WindowState == WindowState.Minimized) _logicWindow.WindowState = WindowState.Normal;
            _logicWindow.Activate();
            return;
        }
        var window = new Views.LogicWindow(this);
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_logicWindow, window)) return;
            _logicWindow = null;
            if (_shuttingDown) return;
            UiLayout.Current.LogicWindowOpen = false;
            UiLayout.Save();
        };
        _logicWindow = window;
        UiLayout.Current.LogicWindowOpen = true;
        UiLayout.Save();
        window.Show();
    }

    private void CloseLogicWindow()
    {
        var window = _logicWindow;
        _logicWindow = null;
        window?.Close();
    }

    /// <summary>Reopens the undocked designer if it was open when the app last closed.</summary>
    public void RestoreUi()
    {
        if (UiLayout.Current.LogicUndocked && UiLayout.Current.LogicWindowOpen) OpenLogicWindow();
    }

    // ---- Project files ----

    private async Task NewProjectAsync()
    {
        if ((Devices.Count > 0 || Clients.Count > 0 || Links.Count > 0 || Logic.Programs.Count > 0) && !Confirm("Close the current project? Unsaved changes will be lost.")) return;
        await ClearAsync();
        _filePath = null;
        OnPropertyChanged(nameof(Title));
    }

    private async Task ClearAsync()
    {
        await Logic.StopAsync();
        Logic.Clear();
        await StopAllLinksAsync();
        Links.Clear();
        _selectedLink = null;
        foreach (var c in Clients.ToList()) await c.DisconnectAsync();
        Clients.Clear();
        _selectedClient = null;
        foreach (var d in Devices.ToList())
        {
            await d.StopRecordAndReplayAsync();
            await d.StopAsync();
            d.RemoveAllTags();
        }
        Devices.Clear();
        _selectedDevice = null;
        Selected = null;
    }

    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog { Title = "Open project", Filter = FileFilter };
        if (dlg.ShowDialog() != true) return;
        await LoadAsync(dlg.FileName);
    }

    public async Task LoadAsync(string path)
    {
        if (IsLoadingProject) return;
        IsLoadingProject = true;
        try
        {
            // JSON parsing and deserialization can be substantial for large projects. Keep it
            // off the dispatcher so the loading overlay can render and animate immediately.
            var doc = await Task.Run(() => ProjectSerializer.LoadAsync(path)).ConfigureAwait(true);
            await ClearAsync();
            var missing = new List<string>();

            foreach (var dd in doc.Devices)
            {
                await Task.Delay(1);
                var plugin = Catalog.Find(dd.PluginId);
                if (plugin == null) { missing.Add(dd.PluginId); continue; }
                var settings = ProjectSerializer.FromElement(dd.Settings, plugin.ServerSettingsType) ?? plugin.CreateServerSettings([]);
                var device = CreateDevice(plugin, dd.Name, settings, dd.Faults);
                device.AutoStart = dd.AutoStart;
                if (dd.Scenario != null) device.ScenarioText = dd.Scenario;
                var restoreValues = new List<(TagViewModel Tag, string Value)>();
                foreach (var td in dd.Tags)
                {
                    // Task.Yield alone can repeatedly resume before WPF gets a render pass.
                    // A short real delay gives the dispatcher time to paint and animate the overlay.
                    if ((restoreValues.Count & 15) == 0) await Task.Delay(1);
                    var tvm = device.AddTag(td.ToTag());
                    if (td.Value != null) restoreValues.Add((tvm, td.Value));
                }
                foreach (var area in device.DataModel.Areas)
                {
                    if (!dd.Data.TryGetValue(area.ShortName, out var values)) continue;
                    foreach (var (index, value) in values)
                    {
                        if (index >= 0 && index < area.Count) area.Write(index, value);
                        if ((index & 255) == 0) await Task.Delay(1);
                    }
                }
                foreach (var (tvm, value) in restoreValues) tvm.ValueText = value;
            }

            foreach (var cd in doc.Clients)
            {
                var plugin = Catalog.Find(cd.PluginId);
                if (plugin == null || !plugin.SupportsClient) { missing.Add(cd.PluginId); continue; }
                var settings = ProjectSerializer.FromElement(cd.Settings, plugin.ClientSettingsType!) ?? plugin.CreateClientSettings();
                var request = ProjectSerializer.FromElement(cd.Request, plugin.ClientRequestType!) ?? plugin.CreateClientRequest();
                var client = CreateClient(plugin, cd.Name, settings, request);
                client.PollIntervalMs = cd.PollIntervalMs;
                client.AutoPoll = cd.AutoPoll;
                foreach (var m in cd.Mappings) client.Mappings.Add(new ClientMappingViewModel(m.Source, m.Target));
            }

            foreach (var ld in doc.Links)
            {
                var plugin = Catalog.Find(ld.PluginId);
                if (plugin is not { SupportsLinks: true }) { missing.Add(ld.PluginId); continue; }
                var settings = ProjectSerializer.FromElement(ld.Settings, plugin.ClientSettingsType!) ?? plugin.CreateClientSettings();
                var link = new NetworkLink(plugin, ld.Name, settings, TagDirectory, ld.SourceDescription) { PollIntervalMs = ld.PollIntervalMs, AutoStart = ld.AutoStart };
                foreach (var p in ld.Points) link.AddPoint(p);
                Links.Add(new LinkViewModel(link, TagDirectory));
            }

            Logic.Load(doc.Logic, doc.LogicAutoRun);

            _filePath = path;
            OnPropertyChanged(nameof(Title));
            if (Devices.Count > 0) SelectedDevice = Devices[0];
            else if (Clients.Count > 0) SelectedClient = Clients[0];

            foreach (var d in Devices.Where(d => d.AutoStart)) await d.StartAsync();
            if (Clients.Any(c => c.AutoPoll))
            {
                TagDirectory.Refresh(Devices);
                foreach (var c in Clients.Where(c => c.AutoPoll)) c.StartPolling();
            }
            if (Links.Any(l => l.Link.AutoStart))
            {
                TagDirectory.Refresh(Devices);
                foreach (var l in Links.Where(l => l.Link.AutoStart)) l.Start();
            }
            if (Logic.AutoRun && Logic.Programs.Count > 0)
            {
                Logic.Refresh(Devices, false);
                Logic.Run();
            }

            if (missing.Count > 0)
                Views.MessageDialog.Show($"These protocols are not installed and were skipped: {string.Join(", ", missing.Distinct())}",
                    "Open project", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsLoadingProject = false;
        }
    }

    private async Task SaveAsync(bool saveAs)
    {
        var path = _filePath;
        if (saveAs || path == null)
        {
            var dlg = new SaveFileDialog { Title = "Save project", Filter = FileFilter, FileName = path == null ? "Simulation.psim" : Path.GetFileName(path) };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }

        var doc = new ProjectDocument();
        foreach (var d in Devices)
        {
            var dd = new DeviceDocument
            {
                PluginId = d.Plugin.Id,
                Name = d.Name,
                AutoStart = d.AutoStart,
                Settings = ProjectSerializer.ToElement(d.Settings),
                Faults = d.Faults,
                Tags = d.Tags.Select(t => TagDocument.From(t.Tag)).ToList(),
                Scenario = d.ScenarioText == ScenarioScript.Help || string.IsNullOrWhiteSpace(d.ScenarioText) ? null : d.ScenarioText,
            };
            foreach (var area in d.DataModel.Areas)
            {
                var values = new Dictionary<int, ushort>();
                for (int i = 0; i < area.Count; i++)
                {
                    var v = area.Read(i);
                    if (v != 0) values[i] = v;
                }
                if (values.Count > 0) dd.Data[area.ShortName] = values;
            }
            doc.Devices.Add(dd);
        }
        foreach (var c in Clients)
        {
            doc.Clients.Add(new ClientDocument
            {
                PluginId = c.Plugin.Id,
                Name = c.Name,
                Settings = ProjectSerializer.ToElement(c.Settings),
                Request = ProjectSerializer.ToElement(c.Request),
                PollIntervalMs = c.PollIntervalMs,
                AutoPoll = c.AutoPoll,
                Mappings = c.Mappings.Select(m => m.ToModel()).ToList(),
            });
        }
        doc.Links = Links.Select(l => l.Link.ToDocument()).ToList();
        doc.Logic = Logic.ToModels();
        doc.LogicAutoRun = Logic.AutoRun;
        await ProjectSerializer.SaveAsync(doc, path);
        _filePath = path;
        OnPropertyChanged(nameof(Title));
        StatusText = $"Saved {path}";
    }

    // ---- Network ----

    private void ShowNetworkWizard()
    {
        TagDirectory.Refresh(Devices);
        var vm = new NetworkWizardViewModel(this);
        Modal.ShowDialog(new Views.NetworkWizardWindow { DataContext = vm });
    }

    /// <summary>Adds a link made by the wizard, selects it and optionally starts it.</summary>
    public LinkViewModel AddLink(NetworkLink link, bool start)
    {
        var used = Links.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = link.Name;
        for (int n = 2; used.Contains(name); n++) name = $"{link.Name} {n}";
        link.Name = name;
        var vm = new LinkViewModel(link, TagDirectory);
        Links.Add(vm);
        SelectedLink = vm;
        TagDirectory.Refresh(Devices);
        if (start) vm.Start();
        return vm;
    }

    private async Task StopAllLinksAsync()
    {
        foreach (var l in Links.ToList()) await l.Link.StopAsync();
    }

    private void StartSharing(bool quiet)
    {
        if (_discovery.Start() || quiet) return;
        Views.MessageDialog.Show(_discovery.LastError ?? "Network sharing could not start.", "Share devices on the network",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Tags of a device as other computers' wizards see them; tags whose address the client can't read are left out.</summary>
    public static List<SharedTag> SharedTags(DeviceViewModel d)
    {
        var tags = new List<SharedTag>();
        foreach (var t in d.Tags.Select(t => t.Tag))
        {
            string address;
            try { address = d.Plugin.LinkSourceAddress(d.Settings, t.Address); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException) { continue; }
            tags.Add(new SharedTag { Name = t.Name, Address = address, DataType = t.DataType, WordOrder = t.WordOrder, Writable = t.Writable, Description = t.Description });
        }
        return tags;
    }

    /// <summary>What network wizards on other computers see. Runs on the UI thread.</summary>
    private DiscoveryCatalog BuildCatalog() => new()
    {
        InstanceId = DiscoveryService.InstanceId,
        Name = Environment.MachineName,
        Project = _filePath == null ? "Untitled" : Path.GetFileNameWithoutExtension(_filePath),
        Version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty,
        Devices = Devices.Where(d => d.Plugin.SupportsLinks && d.Plugin.AcceptsRemoteLinks).Select(d => new SharedDevice
        {
            Name = d.Name,
            PluginId = d.Plugin.Id,
            Protocol = d.Plugin.DisplayName,
            Running = d.IsRunning,
            Endpoint = d.Endpoint,
            ServerSettings = ProjectSerializer.ToElement(d.Settings),
            SupportsWrites = d.Plugin.SupportsLinkWrites,
            Tags = SharedTags(d),
        }).ToList(),
    };

    // ---- Misc ----

    private void SetTheme(string? theme)
    {
        UiLayout.Current.Theme = theme is "Light" or "Dark" ? theme : "System";
        UiLayout.Save();
        App.ApplyTheme();
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private void ShowAbout()
    {
        Modal.ShowDialog(new Views.AboutWindow(Catalog, PluginErrors));
    }

    private static void ShowHelp()
    {
        // Extract the HTML resource to a temporary file
        string tempFilePath = Path.Combine(Path.GetTempPath(), "ProtoSim Help.html");

        using (Stream? resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProtoSim.App.Help.index.html"))
        {
            if (resourceStream == null)
            {
                Views.MessageDialog.Show("The help is missing from this build.", "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Closed before the browser starts, so it never reads a half-written file.
            using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
            resourceStream.CopyTo(fileStream);
        }

        Shell.Open(tempFilePath);
    }
}
