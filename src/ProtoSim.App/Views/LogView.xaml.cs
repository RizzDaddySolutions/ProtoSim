using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _vm;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as LogViewModel);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Attach(e.NewValue as LogViewModel);

    private void Attach(LogViewModel? vm)
    {
        if (_vm != null) _vm.EntriesAppended -= OnEntriesAppended;
        _vm = vm;
        if (_vm != null) _vm.EntriesAppended += OnEntriesAppended;
    }

    private void OnEntriesAppended(object? sender, EventArgs e)
    {
        if (_vm is not { AutoScroll: true } || Grid.Items.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Grid.Items.Count > 0) Grid.ScrollIntoView(Grid.Items[^1]);
        });
    }
}
