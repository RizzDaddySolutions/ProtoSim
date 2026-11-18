using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using AvalonDock.Themes;
using ProtoSim.App.Services;
using ProtoSim.App.ViewModels;
using ProtoSim.Core.Logic;

namespace ProtoSim.App.Views;

/// <summary>
/// Function block designer: palette drag/drop, moving blocks, drawing wires, selection and zoom.
/// Every panel (each block group, project tags, properties) can be docked to any side or floated;
/// the arrangement is saved per user in <see cref="UiLayout"/>.
/// </summary>
public partial class LogicView : UserControl
{
    private const string PalettePrefix = "palette.";
    private static readonly List<WeakReference<LogicView>> Instances = [];
    private string? _defaultLayout;
    private bool _layoutReady;

    private Point _paletteDown;
    private BlockViewModel? _dragBlock;
    private Vector _dragOffset;
    private PortViewModel? _wireFrom;

    public LogicView()
    {
        InitializeComponent();
        lock (Instances) Instances.Add(new WeakReference<LogicView>(this));
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            App.ThemeChanged -= ApplyTheme;
            SaveLayout();
        };
        ShareDataContext();
    }

    /// <summary>Saves the panel layout of every open designer (called at shutdown, when Unloaded may not run).</summary>
    public static void SaveOpenLayouts()
    {
        lock (Instances)
            foreach (var r in Instances)
                if (r.TryGetTarget(out var view) && view.IsLoaded) view.SaveLayout();
    }

    // ---- Panels ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.ThemeChanged -= ApplyTheme;
        App.ThemeChanged += ApplyTheme;
        ApplyTheme();
        if (_layoutReady) return;
        _layoutReady = true;
        CreatePalettePanels();
        ShareDataContext();
        _defaultLayout = Serialize();
        if (UiLayout.Current.LogicPanels is { Length: > 0 } saved) Restore(saved);
        QueueContextRefresh();
    }

    /// <summary>One panel per block category, tabbed in two panes above the project tags by default.</summary>
    private void CreatePalettePanels()
    {
        if (Vm == null) return;
        var first = Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().First();
        var second = new LayoutAnchorablePane { DockHeight = first.DockHeight };
        ((LayoutAnchorablePaneGroup)first.Parent).InsertChildAt(1, second);
        var template = (DataTemplate)FindResource("PaletteItemTemplate");
        int half = (Vm.Palette.Count + 1) / 2;
        for (int i = 0; i < Vm.Palette.Count; i++)
        {
            var group = Vm.Palette[i];
            var list = new ItemsControl { ItemsSource = group.Items, ItemTemplate = template, Margin = new Thickness(6) };
            (i < half ? first : second).Children.Add(new LayoutAnchorable
            {
                ContentId = PalettePrefix + group.Name,
                Title = group.Name,
                CanClose = false,
                CanHide = false,
                Content = new ScrollViewer
                {
                    Content = list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });
        }
        if (first.ChildrenCount > 0) first.SelectedContentIndex = 0;
        if (second.ChildrenCount > 0) second.SelectedContentIndex = 0;
    }

    /// <summary>Panel contents are hosted by AvalonDock (possibly in floating windows), so they get the view model explicitly.</summary>
    private void ShareDataContext()
    {
        foreach (var content in Dock.Layout.Descendents().OfType<LayoutContent>())
            if (content.Content is FrameworkElement element && BindingOperations.GetBinding(element, DataContextProperty) == null)
                element.SetBinding(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });
    }

    private bool _refreshQueued;

    /// <summary>
    /// AvalonDock hosts panel contents with a logical parent (the DockingManager) that differs from their visual parent.
    /// WPF can then leave a stale inherited DataContext on elements deep inside a panel after the view model arrives or
    /// a panel moves; re-evaluating it once things settle keeps every binding live.
    /// </summary>
    private void QueueContextRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _refreshQueued = false;
            foreach (var content in Dock.Layout.Descendents().OfType<LayoutContent>())
                if (content.Content is DependencyObject root) RefreshContext(root);
        });
    }

    private static void RefreshContext(DependencyObject d)
    {
        if (d is FrameworkElement or FrameworkContentElement) d.InvalidateProperty(DataContextProperty);
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) RefreshContext(child);
    }

    private string Serialize()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(Dock).Serialize(writer);
        return writer.ToString();
    }

    private void Restore(string xml)
    {
        // Panels are matched by ContentId; the existing ones keep their content.
        var panels = Dock.Layout.Descendents().OfType<LayoutContent>()
            .Where(c => c.ContentId != null).ToDictionary(c => c.ContentId!, c => c.Content);
        var serializer = new XmlLayoutSerializer(Dock);
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (args.Model.ContentId is { } id && panels.TryGetValue(id, out var content)) args.Content = content;
            else args.Cancel = true; // a panel that no longer exists
        };
        try
        {
            using var reader = new StringReader(xml);
            serializer.Deserialize(reader);
        }
        catch (Exception)
        {
            if (_defaultLayout != null && !ReferenceEquals(xml, _defaultLayout)) Restore(_defaultLayout);
            return;
        }
        AddMissingPanels(panels);
        ShareDataContext();
        QueueContextRefresh();
    }

    /// <summary>Panels added in a newer version (not in the saved layout) go back to their default pane.</summary>
    private void AddMissingPanels(Dictionary<string, object> panels)
    {
        var present = Dock.Layout.Descendents().OfType<LayoutContent>().Select(c => c.ContentId).ToHashSet();
        foreach (var (id, content) in panels)
        {
            if (present.Contains(id)) continue;
            if (id == "canvas")
            {
                var docs = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
                if (docs == null) { Restore(_defaultLayout!); return; }
                docs.Children.Add(new LayoutDocument { ContentId = id, Title = "Canvas", CanClose = false, CanFloat = false, Content = content });
                continue;
            }
            var title = id.StartsWith(PalettePrefix) ? id[PalettePrefix.Length..] : id == "properties" ? "Properties" : "Project tags";
            var anchorable = new LayoutAnchorable { ContentId = id, Title = title, CanClose = false, CanHide = false, Content = content };
            anchorable.AddToLayout(Dock, AnchorableShowStrategy.Left);
        }
    }

    private void SaveLayout()
    {
        if (!_layoutReady) return;
        try
        {
            UiLayout.Current.LogicPanels = Serialize();
            UiLayout.Save();
        }
        catch (Exception)
        {
            // keep the previous layout
        }
    }

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        if (_defaultLayout == null) return;
        Restore(_defaultLayout);
        SaveLayout();
    }

    private void ApplyTheme()
    {
        Theme theme = App.IsDarkTheme ? new Vs2013DarkTheme() : new Vs2013LightTheme();
        if (Dock.Theme?.GetType() != theme.GetType()) Dock.Theme = theme;
    }

    private void OnDeleteSelection(object sender, RoutedEventArgs e) => Vm?.SelectedProgram?.DeleteSelection();

    private LogicViewModel? Vm => DataContext as LogicViewModel;

    private ProgramViewModel EnsureProgram()
    {
        var vm = Vm!;
        if (vm.SelectedProgram == null) vm.AddProgramCommand.Execute(null);
        return vm.SelectedProgram!;
    }

    // ---- Palette → canvas ----

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e) => _paletteDown = e.GetPosition(this);

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: PaletteItemViewModel item } element) return;
        var delta = e.GetPosition(this) - _paletteDown;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(element, new DataObject(typeof(PaletteItemViewModel), item), DragDropEffects.Copy);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PaletteItemViewModel)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm == null || e.Data.GetData(typeof(PaletteItemViewModel)) is not PaletteItemViewModel item) return;
        var program = EnsureProgram();
        var def = item.Def;
        if (item.TagReference != null && (e.KeyStates & DragDropKeyStates.ControlKey) != 0) def = BlockLibrary.Find("TAG_OUT")!;
        var at = e.GetPosition(Surface);
        program.AddBlock(def, new Point(at.X - 30, at.Y - 12), item.TagReference);
        Surface.Focus();
    }

    // ---- Moving blocks ----

    private void OnBlockDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BlockViewModel block }) return;
        block.Program.Selected = block;
        _dragBlock = block;
        _dragOffset = e.GetPosition(Surface) - new Point(block.X, block.Y);
        Surface.CaptureMouse();
        Surface.Focus();
        e.Handled = true;
    }

    // ---- Wiring ----

    private void OnPortDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PortViewModel port }) return;
        _wireFrom = port;
        TempWire.Data = WireViewModel.Curve(port.Anchor, port.Anchor);
        TempWire.Visibility = Visibility.Visible;
        Surface.CaptureMouse();
        Surface.Focus();
        e.Handled = true;
    }

    private void OnWireDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WireViewModel wire }) return;
        wire.Program.Selected = wire;
        Surface.Focus();
        e.Handled = true;
    }

    // ---- Surface ----

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.SelectedProgram is { } p)
        {
            // A click on a block's body (not its header or a port) selects it; empty canvas clears the selection.
            p.Selected = (e.OriginalSource as FrameworkElement)?.DataContext switch
            {
                BlockViewModel b => b,
                PortViewModel port => port.Block,
                _ => null,
            };
        }
        Surface.Focus();
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
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

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
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
            TempWire.Visibility = Visibility.Collapsed;
            if (PortAt(e.GetPosition(Surface)) is { } target) from.Block.Program.Connect(from, target);
        }
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
    }

    /// <summary>The port under a point, with a little tolerance so wires are easy to drop.</summary>
    private PortViewModel? PortAt(Point p)
    {
        PortViewModel? found = null;
        var area = new EllipseGeometry(p, 8, 8);
        VisualTreeHelper.HitTest(Surface, null, r =>
        {
            if (r.VisualHit is FrameworkElement { Tag: "port", DataContext: PortViewModel port })
            {
                found = port;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new GeometryHitTestParameters(area));
        return found;
    }

    private void OnSurfaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && Vm?.SelectedProgram?.Selected != null)
        {
            Vm.SelectedProgram.DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _wireFrom != null)
        {
            _wireFrom = null;
            TempWire.Visibility = Visibility.Collapsed;
            if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
        }
    }

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        double z = Math.Clamp(Zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.35, 2.5);
        Zoom.ScaleX = Zoom.ScaleY = z;
        e.Handled = true;
    }
}
