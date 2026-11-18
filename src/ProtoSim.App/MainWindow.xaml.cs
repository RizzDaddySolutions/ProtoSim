using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ProtoSim.App.Services;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _placementSaveTimer;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        LocationChanged += OnPlacementChanged;
        SizeChanged += OnPlacementChanged;
        StateChanged += OnWindowStateChanged;
    }

    private void OnPlacementChanged(object? sender, EventArgs e) => SchedulePlacementSave();

    private void OnWindowStateChanged(object? sender, EventArgs e) => SchedulePlacementSave();

    private void SchedulePlacementSave()
    {
        if (!IsLoaded || _shutdownComplete) return;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacement()
    {
        UiLayout.Current.MainWindow = WindowPlacement.From(this);
        UiLayout.Save();
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
        _placementSaveTimer.Stop();
        SavePlacement();

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
