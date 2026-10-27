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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownComplete || DataContext is not MainViewModel vm) return;

        // Stop servers and close sockets cleanly before the process exits.
        e.Cancel = true;
        IsEnabled = false;
        try { await vm.ShutdownAsync(); }
        catch (Exception) { /* exiting anyway */ }
        _shutdownComplete = true;
        Close();
    }
}
