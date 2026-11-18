using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Logic;
using ProtoSim.Core.Simulation;

namespace ProtoSim.App.ViewModels;

/// <summary>
/// Every tag in the project as "Device/Tag" (and "Device/Address"). Rebuilt on the UI thread; read from the scan thread.
/// </summary>
public sealed class ProjectTagDirectory : ITagDirectory
{
    private volatile Dictionary<string, SimTag> _map = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = string.Empty;

    public static string Reference(DeviceViewModel device, SimTag tag) => $"{device.Name}/{tag.Name}";

    public SimTag? Find(string reference) => _map.GetValueOrDefault(reference.Trim());

    /// <summary>Tag references for pickers, by device then tag name.</summary>
    public IReadOnlyList<string> References { get; private set; } = [];

    /// <summary>Rebuilds the map when devices or tags changed. Returns true when it did.</summary>
    public bool Refresh(IEnumerable<DeviceViewModel> devices)
    {
        var list = devices.ToList();
        var signature = string.Join("\n", list.Select(d => d.Name + "|" + string.Join("|", d.Tags.Select(t => t.Tag.Name + "@" + t.Tag.Address))));
        if (signature == _signature) return false;
        _signature = signature;

        var map = new Dictionary<string, SimTag>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<string>();
        foreach (var d in list)
        {
            foreach (var t in d.Tags.Select(t => t.Tag))
            {
                var r = Reference(d, t);
                if (map.TryAdd(r, t)) refs.Add(r);
            }
            foreach (var t in d.Tags.Select(t => t.Tag)) map.TryAdd($"{d.Name}/{t.Address}", t);
        }
        _map = map;
        References = refs;
        return true;
    }
}

/// <summary>A palette entry: a block type, or a project tag that becomes a Tag in/out block.</summary>
public sealed class PaletteItemViewModel(string title, string description, BlockDef def, string? tagReference = null)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public BlockDef Def { get; } = def;
    public string? TagReference { get; } = tagReference;
}

public sealed class PaletteGroupViewModel(string name, IReadOnlyList<PaletteItemViewModel> items)
{
    public string Name { get; } = name;
    public IReadOnlyList<PaletteItemViewModel> Items { get; } = items;
}

/// <summary>The Logic page: programs, the palette, and the engine that runs them.</summary>
public sealed class LogicViewModel : ObservableObject
{
    private readonly ProjectTagDirectory _directory;
    private readonly LogicEngine _engine;
    private readonly DispatcherTimer _reload;
    private ProgramViewModel? _selectedProgram;
    private string _status = "Stopped";
    private bool _autoRun;
    private string _tagFilter = string.Empty;
    private IReadOnlyList<PaletteItemViewModel> _tagItems = [];

    public LogicViewModel(ProjectTagDirectory directory)
    {
        _directory = directory;
        _engine = new LogicEngine(directory);
        _reload = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => ReloadEngine(), Dispatcher.CurrentDispatcher);

        Palette = BlockLibrary.Categories
            .Select(c => new PaletteGroupViewModel(c, BlockLibrary.All.Where(d => d.Category == c)
                .Select(d => new PaletteItemViewModel(d.DisplayName, d.Description, d)).ToList()))
            .ToList();

        AddProgramCommand = new RelayCommand(() => SelectedProgram = AddProgram(new LogicProgram { Name = UniqueName() }));
        RemoveProgramCommand = new RelayCommand(RemoveProgram, () => SelectedProgram != null);
        RunCommand = new RelayCommand(Run, () => !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);
        DeleteSelectionCommand = new RelayCommand(() => SelectedProgram?.DeleteSelection(), () => SelectedProgram?.Selected != null);
    }

    public ObservableCollection<ProgramViewModel> Programs { get; } = [];
    public IReadOnlyList<PaletteGroupViewModel> Palette { get; }

    public ICommand AddProgramCommand { get; }
    public ICommand RemoveProgramCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DeleteSelectionCommand { get; }

    public ProgramViewModel? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (SetProperty(ref _selectedProgram, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => _engine.IsRunning;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Start the logic automatically when the project opens.</summary>
    public bool AutoRun { get => _autoRun; set => SetProperty(ref _autoRun, value); }

    public IReadOnlyList<string> TagReferences => _directory.References;

    /// <summary>Project tags in the palette, filtered by <see cref="TagFilter"/>.</summary>
    public IReadOnlyList<PaletteItemViewModel> TagItems { get => _tagItems; private set => SetProperty(ref _tagItems, value); }

    public string TagFilter
    {
        get => _tagFilter;
        set { if (SetProperty(ref _tagFilter, value ?? string.Empty)) RebuildTagItems(); }
    }

    private void RebuildTagItems()
    {
        var tagIn = BlockLibrary.Find("TAG_IN")!;
        TagItems = _directory.References
            .Where(r => _tagFilter.Length == 0 || r.Contains(_tagFilter, StringComparison.OrdinalIgnoreCase))
            .Select(r => new PaletteItemViewModel(r, "Drag onto the canvas: a Tag in block. Hold Ctrl while dropping for a Tag out block.", tagIn, r))
            .ToList();
    }

    private string UniqueName()
    {
        var used = Programs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++) if (!used.Contains($"Program {i}")) return $"Program {i}";
    }

    public ProgramViewModel AddProgram(LogicProgram model)
    {
        var p = new ProgramViewModel(model, this);
        p.Changed += () => { if (IsRunning) { _reload.Stop(); _reload.Start(); } };
        Programs.Add(p);
        SelectedProgram ??= p;
        if (IsRunning) ReloadEngine();
        return p;
    }

    private void RemoveProgram()
    {
        if (SelectedProgram is not { } p) return;
        if (p.Blocks.Count > 0 && !Views.MessageDialog.Confirm($"Delete program '{p.Name}' and its {p.Blocks.Count} blocks?")) return;
        Programs.Remove(p);
        SelectedProgram = Programs.FirstOrDefault();
        if (IsRunning) ReloadEngine();
    }

    private void ReloadEngine()
    {
        _reload.Stop();
        _engine.Load(Programs.Select(p => p.ToModel().Clone()));
    }

    public void Run()
    {
        if (IsRunning) return;
        ReloadEngine();
        _engine.Start();
        OnPropertyChanged(nameof(IsRunning));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        await _engine.StopAsync();
        OnPropertyChanged(nameof(IsRunning));
        foreach (var p in Programs) p.ApplyLive(null);
        Status = "Stopped";
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Called by the UI timer.</summary>
    public void Refresh(IEnumerable<DeviceViewModel> devices, bool isSelected)
    {
        if (_directory.Refresh(devices))
        {
            OnPropertyChanged(nameof(TagReferences));
            RebuildTagItems();
        }
        if (!IsRunning) return;

        int enabled = Programs.Count(p => p.Enabled);
        var snaps = Programs.Where(p => p.Enabled).Select(p => _engine.Snapshot(p.Id)).OfType<ProgramSnapshot>().ToList();
        int errors = snaps.Sum(s => s.Blocks.Values.Count(b => b.Error != null) + s.Problems.Count);
        Status = $"Running · {enabled} program{(enabled == 1 ? "" : "s")} · {snaps.Sum(s => s.Scans):N0} scans · {snaps.Select(s => s.LastScanMs).DefaultIfEmpty().Max():0.00} ms/scan" +
                 (errors > 0 ? $" · {errors} problem{(errors == 1 ? "" : "s")}" : "");
        if (isSelected && SelectedProgram is { } sel) sel.ApplyLive(_engine.Snapshot(sel.Id));
    }

    // ---- Project persistence ----

    public List<LogicProgram> ToModels() => Programs.Select(p => p.ToModel().Clone()).ToList();

    public void Load(IEnumerable<LogicProgram> programs, bool autoRun)
    {
        Clear();
        foreach (var p in programs) AddProgram(p);
        SelectedProgram = Programs.FirstOrDefault();
        AutoRun = autoRun;
    }

    public void Clear()
    {
        Programs.Clear();
        SelectedProgram = null;
        AutoRun = false;
    }

    public async Task ShutdownAsync()
    {
        await StopAsync();
        _engine.Dispose();
    }
}

/// <summary>One program on the canvas.</summary>
public sealed class ProgramViewModel : ObservableObject
{
    private readonly LogicProgram _model;
    private object? _selected;

    public ProgramViewModel(LogicProgram model, LogicViewModel owner)
    {
        _model = model;
        Owner = owner;
        foreach (var b in model.Blocks)
        {
            var def = BlockLibrary.Find(b.Type);
            if (def != null) Blocks.Add(new BlockViewModel(b, def, this));
        }
        foreach (var w in model.Wires)
        {
            var from = FindPort(w.FromBlock, w.FromPort, input: false);
            var to = FindPort(w.ToBlock, w.ToPort, input: true);
            if (from != null && to != null) Wires.Add(new WireViewModel(from, to, this));
        }
    }

    public LogicViewModel Owner { get; }
    public string Id => _model.Id;
    public ObservableCollection<BlockViewModel> Blocks { get; } = [];
    public ObservableCollection<WireViewModel> Wires { get; } = [];

    /// <summary>Raised when anything the engine cares about changes.</summary>
    public event Action? Changed;

    internal void OnChanged() => Changed?.Invoke();

    public string Name
    {
        get => _model.Name;
        set { if (_model.Name != value) { _model.Name = string.IsNullOrWhiteSpace(value) ? "Program" : value.Trim(); OnPropertyChanged(); OnChanged(); } }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set { if (_model.Enabled != value) { _model.Enabled = value; OnPropertyChanged(); OnChanged(); } }
    }

    public int ScanMs
    {
        get => _model.ScanMs;
        set { int v = Math.Clamp(value, 5, 60_000); if (_model.ScanMs != v) { _model.ScanMs = v; OnPropertyChanged(); OnChanged(); } }
    }

    public string Description
    {
        get => _model.Description;
        set { if (_model.Description != value) { _model.Description = value ?? string.Empty; OnPropertyChanged(); } }
    }

    /// <summary>The selected block or wire.</summary>
    public object? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is BlockViewModel ob) ob.IsSelected = false;
            if (_selected is WireViewModel ow) ow.IsSelected = false;
            _selected = value;
            if (value is BlockViewModel nb) nb.IsSelected = true;
            if (value is WireViewModel nw) nw.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBlock));
            OnPropertyChanged(nameof(SelectedWire));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public BlockViewModel? SelectedBlock => _selected as BlockViewModel;
    public WireViewModel? SelectedWire => _selected as WireViewModel;

    public LogicProgram ToModel()
    {
        _model.Blocks = Blocks.Select(b => b.Model).ToList();
        _model.Wires = Wires.Select(w => new LogicWire { FromBlock = w.From.Block.Model.Id, FromPort = w.From.Name, ToBlock = w.To.Block.Model.Id, ToPort = w.To.Name }).ToList();
        return _model;
    }

    private PortViewModel? FindPort(string blockId, string port, bool input) =>
        Blocks.FirstOrDefault(b => b.Model.Id == blockId) is { } b
            ? (input ? b.Inputs : b.Outputs).FirstOrDefault(p => string.Equals(p.Name, port, StringComparison.OrdinalIgnoreCase))
            : null;

    public BlockViewModel AddBlock(BlockDef def, Point at, string? tagReference = null)
    {
        var model = new LogicBlock { Type = def.Type, X = Snap(at.X), Y = Snap(at.Y) };
        foreach (var p in def.Parameters) model.Params[p.Name] = p.Default;
        if (tagReference != null && model.Params.ContainsKey("Tag")) model.Params["Tag"] = tagReference;
        var vm = new BlockViewModel(model, def, this);
        Blocks.Add(vm);
        Selected = vm;
        OnChanged();
        return vm;
    }

    public static double Snap(double v) => Math.Max(0, Math.Round(v / 10) * 10);

    /// <summary>Connects two ports (in either order). An input takes one wire; an existing one is replaced.</summary>
    public WireViewModel? Connect(PortViewModel a, PortViewModel b)
    {
        if (a.IsInput == b.IsInput || ReferenceEquals(a.Block, b.Block)) return null;
        var from = a.IsInput ? b : a;
        var to = a.IsInput ? a : b;
        foreach (var old in Wires.Where(w => ReferenceEquals(w.To, to)).ToList()) RemoveWire(old);
        var wire = new WireViewModel(from, to, this);
        Wires.Add(wire);
        Selected = null;
        OnChanged();
        return wire;
    }

    private void RemoveWire(WireViewModel w)
    {
        w.Detach();
        Wires.Remove(w);
    }

    public void DeleteSelection()
    {
        switch (_selected)
        {
            case WireViewModel w:
                RemoveWire(w);
                break;
            case BlockViewModel b:
                foreach (var w in Wires.Where(w => ReferenceEquals(w.From.Block, b) || ReferenceEquals(w.To.Block, b)).ToList()) RemoveWire(w);
                Blocks.Remove(b);
                break;
            default:
                return;
        }
        Selected = null;
        OnChanged();
    }

    public void ApplyLive(ProgramSnapshot? snapshot)
    {
        foreach (var b in Blocks) b.ApplyLive(snapshot?.Blocks.GetValueOrDefault(b.Model.Id));
        foreach (var w in Wires) w.Refresh();
        Problems = snapshot?.Problems.Count > 0 ? string.Join("; ", snapshot.Problems.Distinct()) : null;
    }

    private string? _problems;
    public string? Problems { get => _problems; private set => SetProperty(ref _problems, value); }
}

/// <summary>A block on the canvas. Layout constants are shared with the XAML template.</summary>
public sealed class BlockViewModel : ObservableObject
{
    public const double Width = 176;
    public const double HeaderHeight = 44;
    public const double RowHeight = 22;

    private bool _isSelected;
    private string? _error;
    private string _liveText = string.Empty;

    public BlockViewModel(LogicBlock model, BlockDef def, ProgramViewModel program)
    {
        Model = model;
        Def = def;
        Program = program;
        Inputs = def.Inputs.Select((p, i) => new PortViewModel(this, p, i, true)).ToList();
        Outputs = def.Outputs.Select((p, i) => new PortViewModel(this, p, i, false)).ToList();
        Params = def.Parameters.Select(p => new ParamViewModel(this, p)).ToList();
    }

    public LogicBlock Model { get; }
    public BlockDef Def { get; }
    public ProgramViewModel Program { get; }
    public IReadOnlyList<PortViewModel> Inputs { get; }
    public IReadOnlyList<PortViewModel> Outputs { get; }
    public IReadOnlyList<ParamViewModel> Params { get; }
    public double BlockWidth => Width;
    public double Height => HeaderHeight + Math.Max(1, Math.Max(Inputs.Count, Outputs.Count)) * RowHeight + 8;

    public double X
    {
        get => Model.X;
        set { if (Model.X != value) { Model.X = value; OnPropertyChanged(); MovePorts(); } }
    }

    public double Y
    {
        get => Model.Y;
        set { if (Model.Y != value) { Model.Y = value; OnPropertyChanged(); MovePorts(); } }
    }

    private void MovePorts()
    {
        foreach (var p in Inputs) p.OnMoved();
        foreach (var p in Outputs) p.OnMoved();
    }

    public string Title => string.IsNullOrWhiteSpace(Model.Label) ? Def.DisplayName : Model.Label;

    public string Label
    {
        get => Model.Label;
        set { Model.Label = value?.Trim() ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); }
    }

    /// <summary>A one-line summary of the parameters, shown under the title.</summary>
    public string Subtitle
    {
        get
        {
            if (Model.Params.TryGetValue("Tag", out var tag)) return string.IsNullOrWhiteSpace(tag) ? "(choose a tag)" : tag;
            var shown = Def.Parameters.Where(p => p.Kind is ParamKind.Number or ParamKind.Choice)
                .Select(p => $"{p.Name} {Model.Param(p.Name, p.Default)}").Take(3);
            return string.Join(" · ", shown);
        }
    }

    public string Category => Def.Category;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error != null;

    /// <summary>Value shown in the header of tag/constant blocks while running.</summary>
    public string LiveText { get => _liveText; private set => SetProperty(ref _liveText, value); }

    internal void OnParamChanged()
    {
        OnPropertyChanged(nameof(Subtitle));
        Program.OnChanged();
    }

    public void ApplyLive(BlockSnapshot? s)
    {
        Error = s?.Error;
        for (int i = 0; i < Inputs.Count; i++) Inputs[i].SetLive(s == null ? null : s.Inputs[i]);
        for (int i = 0; i < Outputs.Count; i++) Outputs[i].SetLive(s == null ? null : s.Outputs[i]);
        LiveText = s == null ? string.Empty
            : Def.Type == "TAG_OUT" && s.Inputs.Length > 0 ? "→ " + PortViewModel.Format(s.Inputs[0], Def.Inputs[0].Kind)
            : s.Outputs.Length > 0 ? PortViewModel.Format(s.Outputs[0], Def.Outputs[0].Kind) : string.Empty;
    }
}

public sealed class PortViewModel : ObservableObject
{
    private string _valueText = string.Empty;
    private bool _isOn;
    private bool _hasLive;

    public PortViewModel(BlockViewModel block, PortDef def, int index, bool isInput)
    {
        Block = block;
        Def = def;
        Index = index;
        IsInput = isInput;
    }

    public BlockViewModel Block { get; }
    public PortDef Def { get; }
    public int Index { get; }
    public bool IsInput { get; }
    public string Name => Def.Name;
    public bool IsBool => Def.Kind == PortKind.Bool;
    public string ToolTip => $"{Def.Name} ({(IsBool ? "Bool" : "Number")}){(Def.FallbackParam != null ? $" · uses the {Def.FallbackParam} parameter when not wired" : "")}";

    /// <summary>Where wires attach, in canvas coordinates.</summary>
    public Point Anchor => new(
        Block.X + (IsInput ? 0 : BlockViewModel.Width),
        Block.Y + BlockViewModel.HeaderHeight + Index * BlockViewModel.RowHeight + BlockViewModel.RowHeight / 2);

    internal void OnMoved() => OnPropertyChanged(nameof(Anchor));

    public string ValueText { get => _valueText; private set => SetProperty(ref _valueText, value); }
    public bool IsOn { get => _isOn; private set => SetProperty(ref _isOn, value); }
    public bool HasLive { get => _hasLive; private set => SetProperty(ref _hasLive, value); }
    public double LiveValue { get; private set; }

    internal void SetLive(double? v)
    {
        HasLive = v.HasValue;
        LiveValue = v ?? 0;
        IsOn = v is { } x && IsBool && x != 0;
        ValueText = v is { } y ? Format(y, Def.Kind) : string.Empty;
    }

    public static string Format(double v, PortKind kind) =>
        kind == PortKind.Bool ? (v != 0 ? "1" : "0")
        : Math.Abs(v) >= 1e6 || (v != 0 && Math.Abs(v) < 1e-3) ? v.ToString("0.###E0", CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class WireViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isActive;

    public WireViewModel(PortViewModel from, PortViewModel to, ProgramViewModel program)
    {
        From = from;
        To = to;
        Program = program;
        From.PropertyChanged += OnPortChanged;
        To.PropertyChanged += OnPortChanged;
    }

    public PortViewModel From { get; }
    public PortViewModel To { get; }
    public ProgramViewModel Program { get; }

    public string Description => $"{From.Block.Title}.{From.Name} → {To.Block.Title}.{To.Name}";

    public Geometry Geometry => Curve(From.Anchor, To.Anchor);

    public static Geometry Curve(Point a, Point b)
    {
        double dx = Math.Max(40, Math.Abs(b.X - a.X) / 2);
        var figure = new PathFigure { StartPoint = a };
        figure.Segments.Add(new BezierSegment(new Point(a.X + dx, a.Y), new Point(b.X - dx, b.Y), b, true));
        var g = new PathGeometry([figure]);
        g.Freeze();
        return g;
    }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>A Bool signal that is currently true (drawn highlighted).</summary>
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }

    public string ValueText => From.HasLive ? From.ValueText : string.Empty;

    private void OnPortChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PortViewModel.Anchor)) OnPropertyChanged(nameof(Geometry));
    }

    internal void Refresh()
    {
        IsActive = From.IsOn;
        OnPropertyChanged(nameof(ValueText));
    }

    internal void Detach()
    {
        From.PropertyChanged -= OnPortChanged;
        To.PropertyChanged -= OnPortChanged;
    }
}

public sealed class ParamViewModel : ObservableObject
{
    private readonly BlockViewModel _block;
    private string? _error;

    public ParamViewModel(BlockViewModel block, ParamDef def)
    {
        _block = block;
        Def = def;
    }

    public ParamDef Def { get; }
    public string Name => Def.Name;
    public string Help => Def.Help;
    public bool IsNumber => Def.Kind == ParamKind.Number;
    public bool IsText => Def.Kind is ParamKind.Number or ParamKind.Text;
    public bool IsBool => Def.Kind == ParamKind.Bool;
    public bool IsChoice => Def.Kind == ParamKind.Choice;
    public bool IsTag => Def.Kind == ParamKind.Tag;
    public IReadOnlyList<string> Choices => Def.Choices ?? [];
    public IReadOnlyList<string> TagReferences => _block.Program.Owner.TagReferences;

    public string Value
    {
        get => _block.Model.Param(Def.Name, Def.Default);
        set
        {
            value = (value ?? string.Empty).Trim();
            if (IsNumber && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                Error = $"'{value}' is not a number";
                return;
            }
            Error = null;
            if (Value == value) return;
            _block.Model.Params[Def.Name] = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoolValue));
            _block.OnParamChanged();
        }
    }

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase) || Value == "1";
        set => Value = value ? "true" : "false";
    }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
}
