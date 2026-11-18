using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProtoSim.App.ViewModels;
using ProtoSim.Core.Logic;

namespace ProtoSim.App.Views;

/// <summary>Function block designer: palette drag/drop, moving blocks, drawing wires, selection and zoom.</summary>
public partial class LogicView : UserControl
{
    private Point _paletteDown;
    private BlockViewModel? _dragBlock;
    private Vector _dragOffset;
    private PortViewModel? _wireFrom;

    public LogicView()
    {
        InitializeComponent();
    }

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
