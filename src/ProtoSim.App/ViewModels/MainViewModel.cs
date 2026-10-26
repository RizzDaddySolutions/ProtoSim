using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Project;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;
using Microsoft.Win32;

namespace ProtoSim.App.ViewModels;

public sealed record PluginActionViewModel(string DisplayName, string Description, ICommand Command);

public sealed class MainViewModel : ObservableObject
{
    private const string FileFilter = "Simulator project (*.psim)|*.psim|JSON (*.json)|*.json|All files (*.*)|*.*";

    private readonly DispatcherTimer _timer;
    private object? _selected;
    private DeviceViewModel? _selectedDevice;
    private ClientViewModel? _selectedClient;
    private string? _filePath;
    private bool _simulationPaused;
    private string _statusText = "Ready";

    public MainViewModel(PluginCatalog catalog, IReadOnlyList<string> pluginErrors)
    {
        Catalog = catalog;
        PluginErrors = pluginErrors;
        Engine.Start();

        AddDeviceActions = catalog.Plugins.Where(p => p.SupportsServer)
            .Select(p => new PluginActionViewModel(p.DisplayName, p.Description, new RelayCommand(() => AddDevice(p)))).ToList();
        AddClientActions = catalog.Plugins.Where(p => p.SupportsClient)
            .Select(p => new PluginActionViewModel(p.DisplayName, p.Description, new RelayCommand(() => AddClient(p)))).ToList();

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
        ExitCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());
        DemoActions = catalog.Plugins.Where(p => p.SupportsServer && p.SupportsClient)
            .Select(p => new PluginActionViewModel($"{p.DisplayName} demo", p.Description, new AsyncRelayCommand(() => QuickStartAsync(p.Id)))).ToList();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public PluginCatalog Catalog { get; }
    public IReadOnlyList<string> PluginErrors { get; }
    public SimulationEngine Engine { get; } = new();
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<ClientViewModel> Clients { get; } = [];
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
    public ICommand AboutCommand { get; }
    public ICommand ExitCommand { get; }
    /// <summary>One "start a demo" action per protocol that has both a server and a client.</summary>
    public IReadOnlyList<PluginActionViewModel> DemoActions { get; }

    public object? Selected { get => _selected; private set => SetProperty(ref _selected, value); }

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value) || value == null) return;
            SelectedClient = null;
            Selected = value;
        }
    }

    public ClientViewModel? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (!SetProperty(ref _selectedClient, value) || value == null) return;
            SelectedDevice = null;
            Selected = value;
        }
    }

    public bool SimulationPaused
    {
        get => _simulationPaused;
        set { if (SetProperty(ref _simulationPaused, value)) Engine.IsEnabled = !value; }
    }

    public string Title => $"{(_filePath == null ? "Untitled" : Path.GetFileNameWithoutExtension(_filePath))} – ProtoSim";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private void Tick()
    {
        foreach (var d in Devices) d.Refresh(ReferenceEquals(d, _selected));
        foreach (var c in Clients) c.Refresh();

        int running = Devices.Count(d => d.IsRunning);
        long connections = Devices.Sum(d => d.Stats.ActiveConnections);
        double rate = Devices.Sum(d => d.Stats.RequestsPerSecond);
        StatusText = $"{running}/{Devices.Count} devices running · {connections} client connections · {rate:F1} req/s · simulation {(SimulationPaused ? "paused" : "running")}";
    }

    // ---- Devices & clients ----

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
        var client = new ClientViewModel(plugin, UniqueName($"{plugin.DisplayName} client", Clients.Select(c => c.Name)),
            settings ?? plugin.CreateClientSettings(), plugin.CreateClientRequest());
        Clients.Add(client);
        SelectedClient = client;
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
        // Take the new port (if any) from freshly generated settings to avoid a clash.
        var portProp = settings.GetType().GetProperty("Port");
        if (portProp?.PropertyType == typeof(int)) portProp.SetValue(settings, portProp.GetValue(fresh));

        var copy = CreateDevice(src.Plugin, UniqueName(src.Name + " copy", Devices.Select(d => d.Name)), settings,
            ProjectSerializer.FromElement(ProjectSerializer.ToElement(src.Faults), typeof(FaultSettings)) as FaultSettings);
        foreach (var t in src.Tags) copy.AddTag(TagDocument.From(t.Tag).ToTag());
        CopyData(src, copy);
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
                await d.StopAsync();
                d.RemoveAllTags();
                Devices.Remove(d);
                break;
            case ClientViewModel c:
                if (!Confirm($"Remove client '{c.Name}'?")) return;
                await c.DisconnectAsync();
                Clients.Remove(c);
                break;
        }
        _selectedDevice = null;
        _selectedClient = null;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedClient));
        Selected = null;
    }

    private static bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private async Task StartAllAsync()
    {
        foreach (var d in Devices.Where(d => !d.IsRunning).ToList()) await d.StartAsync();
    }

    private async Task StopAllAsync()
    {
        foreach (var d in Devices.Where(d => d.IsRunning).ToList()) await d.StopAsync();
    }

    /// <summary>Creates a ready-to-use demo: a Modbus device with sample tags plus a client pointed at it.</summary>
    /// <summary>Creates a ready-to-use demo: a device with sample tags plus a client pointed at it.</summary>
    /// <param name="pluginId">Protocol to demo; the first protocol with server and client support when null.</param>
    public async Task QuickStartAsync(string? pluginId = null)
    {
        var plugin = pluginId == null
            ? Catalog.Plugins.FirstOrDefault(p => p.SupportsServer && p.SupportsClient)
            : Catalog.Find(pluginId);
        if (plugin == null) return;
        var device = AddDevice(plugin);
        var client = AddClient(plugin, plugin.CreateClientSettingsFor(device.Settings));
        SelectedDevice = device;
        await device.StartAsync();
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        foreach (var c in Clients) await c.DisconnectAsync();
        await StopAllAsync();
        Engine.Dispose();
    }

    // ---- Project files ----

    private async Task NewProjectAsync()
    {
        if (Devices.Count + Clients.Count > 0 && !Confirm("Close the current project? Unsaved changes will be lost.")) return;
        await ClearAsync();
        _filePath = null;
        OnPropertyChanged(nameof(Title));
    }

    private async Task ClearAsync()
    {
        foreach (var c in Clients.ToList()) await c.DisconnectAsync();
        foreach (var d in Devices.ToList())
        {
            await d.StopAsync();
            d.RemoveAllTags();
        }
        Clients.Clear();
        Devices.Clear();
        _selectedDevice = null;
        _selectedClient = null;
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
        var doc = await ProjectSerializer.LoadAsync(path);
        await ClearAsync();
        var missing = new List<string>();

        foreach (var dd in doc.Devices)
        {
            var plugin = Catalog.Find(dd.PluginId);
            if (plugin == null) { missing.Add(dd.PluginId); continue; }
            var settings = ProjectSerializer.FromElement(dd.Settings, plugin.ServerSettingsType) ?? plugin.CreateServerSettings([]);
            var device = CreateDevice(plugin, dd.Name, settings, dd.Faults);
            device.AutoStart = dd.AutoStart;
            var restoreValues = new List<(TagViewModel Tag, string Value)>();
            foreach (var td in dd.Tags)
            {
                var tvm = device.AddTag(td.ToTag());
                if (td.Value != null) restoreValues.Add((tvm, td.Value));
            }
            foreach (var area in device.DataModel.Areas)
            {
                if (!dd.Data.TryGetValue(area.ShortName, out var values)) continue;
                foreach (var (index, value) in values)
                    if (index >= 0 && index < area.Count) area.Write(index, value);
            }
            foreach (var (tvm, value) in restoreValues) tvm.ValueText = value;
        }

        foreach (var cd in doc.Clients)
        {
            var plugin = Catalog.Find(cd.PluginId);
            if (plugin == null) { missing.Add(cd.PluginId); continue; }
            var settings = ProjectSerializer.FromElement(cd.Settings, plugin.ClientSettingsType) ?? plugin.CreateClientSettings();
            var request = ProjectSerializer.FromElement(cd.Request, plugin.ClientRequestType) ?? plugin.CreateClientRequest();
            Clients.Add(new ClientViewModel(plugin, cd.Name, settings, request) { PollIntervalMs = cd.PollIntervalMs });
        }

        _filePath = path;
        OnPropertyChanged(nameof(Title));
        if (Devices.Count > 0) SelectedDevice = Devices[0];
        else if (Clients.Count > 0) SelectedClient = Clients[0];

        foreach (var d in Devices.Where(d => d.AutoStart)) await d.StartAsync();

        if (missing.Count > 0)
            MessageBox.Show($"These protocols are not installed and were skipped: {string.Join(", ", missing.Distinct())}",
                "Open project", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            });
        }

        await ProjectSerializer.SaveAsync(doc, path);
        _filePath = path;
        OnPropertyChanged(nameof(Title));
        StatusText = $"Saved {path}";
    }

    // ---- Misc ----

    private static void SetTheme(string? theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    private void ShowAbout()
    {
        var protocols = string.Join("\n", Catalog.Plugins.Select(p => $"  • {p.DisplayName} — {p.Description}"));
        var errors = PluginErrors.Count > 0 ? "\n\nPlugin load errors:\n" + string.Join("\n", PluginErrors) : "";
        MessageBox.Show(Application.Current.MainWindow!,
            $"ProtoSim 1.0\n\nInstalled protocols:\n{protocols}\n\nAdd protocols by dropping plugin DLLs into the 'Plugins' folder next to the executable.{errors}",
            "About", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
