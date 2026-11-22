using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ProtoSim.App.Services;
using ProtoSim.App.ViewModels;
using ProtoSim.Core.Logic;

namespace ProtoSim.App.Views;

/// <summary>
/// Function block designer: palette drag/drop, moving blocks, drawing wires, selection and zoom.
/// Palette and project-tag panels can be shown or hidden; the choice is saved per user in <see cref="UiLayout"/>.
/// </summary>
public partial class LogicView : UserControl
{
    private static readonly DataFormat<PaletteItemViewModel> PaletteDataFormat =
        DataFormat.CreateInProcessFormat<PaletteItemViewModel>("protosim-palette-item");

    private const string ProjectTagsId = "Project tags";
    private static readonly List<WeakReference<LogicView>> Instances = [];

    private readonly Dictionary<string, Expander> _panels = [];
    private bool _palettePanelsCreated;
    private LogicViewModel? _hintVm;

    private Point _paletteDown;
    private PaletteItemViewModel? _dragItem;
    private PointerPressedEventArgs? _dragPress;
    private BlockViewModel? _dragBlock;
    private Vector _dragOffset;
    private PortViewModel? _wireFrom;
    private double _zoom = 1;

    public LogicView()
    {
        InitializeComponent();
        lock (Instances) Instances.Add(new WeakReference<LogicView>(this));
        _panels[ProjectTagsId] = ProjectTagsPanel;
        if (OperatingSystem.IsMacOS())
        {
            DropHint.Text = "Drop = Tag in · ⌘+drop = Tag out";
            ZoomHint.Text = "⌘ + wheel to zoom · Delete removes the selection";
        }
        var width = Math.Clamp(UiLayout.Current.LogicPanelWidth, 160, 600);
        Body.ColumnDefinitions[0].Width = new GridLength(width);
        DataContextChanged += (_, _) => OnViewModelChanged();
        DetachedFromVisualTree += (_, _) => SaveLayout();
        Surface.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Surface.AddHandler(DragDrop.DropEvent, OnDrop);
        Scroller.AddHandler(PointerWheelChangedEvent, OnCanvasWheel, RoutingStrategies.Tunnel);
        BuildPanelsMenu();
        ApplyHiddenPanels();
    }

    /// <summary>Saves the panel layout of every open designer (called at shutdown).</summary>
    public static void SaveOpenLayouts()
    {
        lock (Instances)
            foreach (var r in Instances)
                if (r.TryGetTarget(out var view) && view.IsAttachedToVisualTree()) view.SaveLayout();
    }

    private LogicViewModel? Vm => DataContext as LogicViewModel;

    private void OnViewModelChanged()
    {
        if (_hintVm != null) _hintVm.PropertyChanged -= OnVmPropertyChanged;
        _hintVm = Vm;
        if (_hintVm != null) _hintVm.PropertyChanged += OnVmPropertyChanged;
        CreatePalettePanels();
        UpdateEmptyHint();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicViewModel.SelectedProgram)) WatchProgram();
    }

    private System.Collections.Specialized.INotifyCollectionChanged? _watchedBlocks;

    private void WatchProgram()
    {
        if (_watchedBlocks != null) _watchedBlocks.CollectionChanged -= OnBlocksChanged;
        _watchedBlocks = Vm?.SelectedProgram?.Blocks;
        if (_watchedBlocks != null) _watchedBlocks.CollectionChanged += OnBlocksChanged;
        UpdateEmptyHint();
    }

    private void OnBlocksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateEmptyHint();

    private void UpdateEmptyHint() => EmptyHint.IsVisible = Vm?.SelectedProgram is not { Blocks.Count: > 0 };

    // ---- Panels ----

    /// <summary>One collapsible panel per block category, above the project tags.</summary>
    private void CreatePalettePanels()
    {
        if (Vm == null || _palettePanelsCreated) return;
        _palettePanelsCreated = true;
        var template = (Avalonia.Controls.Templates.IDataTemplate)Resources["PaletteItemTemplate"]!;
        for (int i = 0; i < Vm.Palette.Count; i++)
        {
            var group = Vm.Palette[i];
            var panel = new Expander
            {
                Header = group.Name,
                IsExpanded = i == 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ItemsControl { ItemsSource = group.Items, ItemTemplate = template },
            };
            _panels[group.Name] = panel;
            PanelStack.Children.Insert(i, panel);
        }
        WatchProgram();
        BuildPanelsMenu();
        ApplyHiddenPanels();
    }

    private void BuildPanelsMenu()
    {
        PanelsMenu.Items.Clear();
        foreach (var (id, panel) in _panels.OrderBy(p => p.Key))
        {
            var item = new MenuItem { Header = id, ToggleType = MenuItemToggleType.CheckBox, IsChecked = panel.IsVisible };
            item.Click += (_, _) =>
            {
                panel.IsVisible = !panel.IsVisible;
                item.IsChecked = panel.IsVisible;
                SaveLayout();
            };
            PanelsMenu.Items.Add(item);
        }
    }

    private void ApplyHiddenPanels()
    {
        var hidden = UiLayout.Current.LogicHiddenPanels;
        foreach (var (id, panel) in _panels) panel.IsVisible = !hidden.Contains(id);
        foreach (var item in PanelsMenu.Items.OfType<MenuItem>())
            if (item.Header is string id && _panels.TryGetValue(id, out var panel)) item.IsChecked = panel.IsVisible;
    }

    private void SaveLayout()
    {
        if (!_palettePanelsCreated) return;
        UiLayout.Current.LogicHiddenPanels = _panels.Where(p => !p.Value.IsVisible).Select(p => p.Key).ToList();
        UiLayout.Current.LogicPanelWidth = Body.ColumnDefinitions[0].ActualWidth > 0 ? Body.ColumnDefinitions[0].ActualWidth : UiLayout.Current.LogicPanelWidth;
        UiLayout.Save();
    }

    private void OnPanelSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e) => SaveLayout();

    private void OnResetLayout(object? sender, RoutedEventArgs e)
    {
        UiLayout.Current.LogicHiddenPanels = [];
        Body.ColumnDefinitions[0].Width = new GridLength(250);
        ApplyHiddenPanels();
        SaveLayout();
    }

    private void OnSelectionEditorToggled(object? sender, RoutedEventArgs e) => SetSelectionEditor(!SelectionEditor.IsVisible);

    private void OnSelectionEditorClose(object? sender, RoutedEventArgs e) => SetSelectionEditor(false);

    private void SetSelectionEditor(bool open)
    {
        SelectionEditor.IsVisible = open;
        EditSelectionMenuItem.IsChecked = open;
    }

    private void OnDeleteSelection(object? sender, RoutedEventArgs e) => Vm?.SelectedProgram?.DeleteSelection();

    private void OnBlockContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: BlockViewModel block } || Vm is not { } vm) return;
        vm.SelectedProgram = block.Program;
        block.Program.Selected = block;
    }

    private void OnEditBlock(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: BlockViewModel block } || Vm is not { } vm) return;
        vm.SelectedProgram = block.Program;
        block.Program.Selected = block;
        SetSelectionEditor(true);
    }

    private void OnDeleteBlock(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: BlockViewModel block }) return;
        block.Program.Selected = block;
        block.Program.DeleteSelection();
    }

    private ProgramViewModel EnsureProgram()
    {
        var vm = Vm!;
        if (vm.SelectedProgram == null) vm.AddProgramCommand.Execute(null);
        return vm.SelectedProgram!;
    }

    /// <summary>Ctrl on Windows and Linux, Command on macOS.</summary>
    private static bool HasCommandKey(KeyModifiers m) => (m & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;

    // ---- Palette → canvas ----

    private void OnPalettePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PaletteItemViewModel item } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _paletteDown = e.GetPosition(this);
        _dragItem = item;
        _dragPress = e; // DoDragDropAsync starts from the press, once the pointer has moved far enough
    }

    private async void OnPaletteMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is not { } item || _dragPress is not { } press) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragItem = null; _dragPress = null; return; }
        var delta = e.GetPosition(this) - _paletteDown;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        _dragItem = null;
        _dragPress = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PaletteDataFormat, item));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(PaletteDataFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null || e.DataTransfer.TryGetValue(PaletteDataFormat) is not { } item) return;
        var program = EnsureProgram();
        var def = item.Def;
        if (item.TagReference != null && HasCommandKey(e.KeyModifiers)) def = BlockLibrary.Find("TAG_OUT")!;
        var at = e.GetPosition(Surface);
        program.AddBlock(def, new Point(at.X - 30, at.Y - 12), item.TagReference);
        UpdateEmptyHint();
        Surface.Focus();
    }

    // ---- Moving blocks ----

    private void OnBlockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: BlockViewModel block } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        block.Program.Selected = block;
        _dragBlock = block;
        _dragOffset = e.GetPosition(Surface) - new Point(block.X, block.Y);
        e.Pointer.Capture(Surface);
        Surface.Focus();
        e.Handled = true;
    }

    // ---- Wiring ----

    private void OnPortPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PortViewModel port } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _wireFrom = port;
        TempWire.Data = WireViewModel.Curve(port.Anchor, port.Anchor);
        TempWire.IsVisible = true;
        e.Pointer.Capture(Surface);
        Surface.Focus();
        e.Handled = true;
    }

    private void OnWirePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: WireViewModel wire }) return;
        wire.Program.Selected = wire;
        Surface.Focus();
        e.Handled = true;
    }

    // ---- Surface ----

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (Vm?.SelectedProgram is { } p)
        {
            // A click on a block's body (not its header or a port) selects it; empty canvas clears the selection.
            p.Selected = (e.Source as StyledElement)?.DataContext switch
            {
                BlockViewModel b => b,
                PortViewModel port => port.Block,
                _ => null,
            };
        }
        Surface.Focus();
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(Surface);
        if (_dragBlock != null)
        {
            _dragBlock.X = Math.Max(0, pos.X - _dragOffset.X);
            _dragBlock.Y = Math.Max(0, pos.Y - _dragOffset.Y);
        }
        else if (_wireFrom != null)
        {
            TempWire.Data = _wireFrom.IsInput ? WireViewModel.Curve(pos, _wireFrom.Anchor) : WireViewModel.Curve(_wireFrom.Anchor, pos);
        }
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragBlock != null)
        {
            _dragBlock.X = ProgramViewModel.Snap(_dragBlock.X);
            _dragBlock.Y = ProgramViewModel.Snap(_dragBlock.Y);
            _dragBlock = null;
        }
        else if (_wireFrom != null)
        {
            var from = _wireFrom;
            _wireFrom = null;
            TempWire.IsVisible = false;
            if (PortAt(e.GetPosition(Surface)) is { } target) from.Block.Program.Connect(from, target);
        }
        e.Pointer.Capture(null);
    }

    /// <summary>The port under a point, with a little tolerance so wires are easy to drop.</summary>
    private PortViewModel? PortAt(Point p)
    {
        const double tolerance = 8 + 5.5;
        foreach (var dot in Surface.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>())
        {
            if (dot.Tag is not "port" || dot.DataContext is not PortViewModel port) continue;
            if (dot.TranslatePoint(new Point(dot.Bounds.Width / 2, dot.Bounds.Height / 2), Surface) is not { } centre) continue;
            var d = centre - p;
            if (d.X * d.X + d.Y * d.Y <= tolerance * tolerance) return port;
        }
        return null;
    }

    private void OnSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && Vm?.SelectedProgram?.Selected != null)
        {
            Vm.SelectedProgram.DeleteSelection();
            UpdateEmptyHint();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _wireFrom != null)
        {
            _wireFrom = null;
            TempWire.IsVisible = false;
        }
    }

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!HasCommandKey(e.KeyModifiers)) return;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 0.35, 2.5);
        ZoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        e.Handled = true;
    }
}
