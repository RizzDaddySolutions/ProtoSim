using System.ComponentModel;
using System.Windows;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
    }

    private bool _shutdownStarted;

    protected override async void OnClosing(CancelEventArgs e)
    {
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
            if (DataContext is MainViewModel vm)
                await vm.ShutdownAsync();
        }
        catch
        {
            // exiting anyway
        }

        // Do not call Close() from inside OnClosing.
        await Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Application.Current.Shutdown();
        });
    }
}
