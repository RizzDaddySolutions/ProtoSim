using System.Windows;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class NetworkWizardWindow : Window
{
    public NetworkWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is NetworkWizardViewModel old) old.CloseRequested -= OnCloseRequested;
            if (e.NewValue is NetworkWizardViewModel vm) vm.CloseRequested += OnCloseRequested;
        };
    }

    private void OnCloseRequested(bool ok)
    {
        try { DialogResult = ok; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }

    private async void OnFinish(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NetworkWizardViewModel vm) return;
        ((FrameworkElement)sender).IsEnabled = false;
        try { await vm.FinishAsync(); }
        catch (Exception ex)
        {
            MessageDialog.Show(ex.Message, "Network wizard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ((FrameworkElement)sender).IsEnabled = true;
        }
    }
}
