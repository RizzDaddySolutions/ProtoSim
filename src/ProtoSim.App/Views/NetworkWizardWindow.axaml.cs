using Avalonia.Controls;
using Avalonia.Interactivity;
using ProtoSim.App.Platform;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class NetworkWizardWindow : Window
{
    private NetworkWizardViewModel? _vm;

    public NetworkWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.CloseRequested -= OnCloseRequested;
            _vm = DataContext as NetworkWizardViewModel;
            if (_vm != null) _vm.CloseRequested += OnCloseRequested;
        };
    }

    private void OnCloseRequested(bool ok) => Close(ok);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnFinish(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NetworkWizardViewModel vm || sender is not Control button) return;
        button.IsEnabled = false;
        try { await vm.FinishAsync(); }
        catch (Exception ex)
        {
            MessageDialog.Show(ex.Message, "Network wizard", MessageBoxButton.OK, MessageBoxImage.Error, this);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
