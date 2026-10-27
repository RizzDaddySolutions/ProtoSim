using System.ComponentModel;
using System.Windows;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_shutdownComplete || DataContext is not MainViewModel vm)
            return;

        if (_shutdownStarted)
        {
            e.Cancel = true;
            return;
        }

        _shutdownStarted = true;
        e.Cancel = true;
        IsEnabled = false;

        try
        {
            await vm.ShutdownAsync();
        }
        catch (Exception)
        {
            // exiting anyway
        }
        finally
        {
            _shutdownComplete = true;
        }

        // Close after the current closing event has exited.
        await Dispatcher.BeginInvoke(new Action(() =>
        {
            Close();
        }));
    }
}
