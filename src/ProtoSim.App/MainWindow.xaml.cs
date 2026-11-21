using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is MainViewModel vm) vm.PropertyChanged += OnViewModelChanged;
            ApplyPanels();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowClientsPanel) or nameof(MainViewModel.ShowLinksPanel) or nameof(MainViewModel.LeftPanelCollapsed))
            ApplyPanels();
    }

    /// <summary>Sizes the left panel and its lists from the view settings (hidden lists give their space to the others).</summary>
    private void ApplyPanels()
    {
        if (DataContext is not MainViewModel vm) return;
        ClientsRow.Height = vm.ShowClientsPanel ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        LinksRow.Height = vm.ShowLinksPanel ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        if (vm.LeftPanelCollapsed)
        {
            if (NavColumn.Width.IsAbsolute && NavColumn.ActualWidth > 0) RememberNavWidth(NavColumn.ActualWidth);
            NavColumn.MinWidth = 0;
            NavColumn.Width = GridLength.Auto;
        }
        else
        {
            NavColumn.MinWidth = 220;
            NavColumn.Width = new GridLength(Math.Clamp(UiLayout.Current.LeftPanelWidth, 220, 900));
        }
    }

    private void OnNavSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel { LeftPanelCollapsed: false }) RememberNavWidth(NavColumn.ActualWidth);
    }

    private static void RememberNavWidth(double width)
    {
        if (Math.Abs(UiLayout.Current.LeftPanelWidth - width) < 0.5) return;
        UiLayout.Current.LeftPanelWidth = width;
        UiLayout.Save();
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
