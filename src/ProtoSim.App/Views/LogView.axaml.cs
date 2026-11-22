using Avalonia.Controls;
using Avalonia.Threading;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _vm;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LogViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
        AttachedToVisualTree += (_, _) => Attach(DataContext as LogViewModel);
    }

    private void Attach(LogViewModel? vm)
    {
        if (_vm != null) _vm.EntriesAppended -= OnEntriesAppended;
        _vm = vm;
        if (_vm != null) _vm.EntriesAppended += OnEntriesAppended;
    }

    private void OnEntriesAppended(object? sender, EventArgs e)
    {
        if (_vm is not { AutoScroll: true }) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is { View.Count: > 0 } vm) Grid.ScrollIntoView(vm.View[vm.View.Count - 1], null);
        }, DispatcherPriority.Background);
    }
}
