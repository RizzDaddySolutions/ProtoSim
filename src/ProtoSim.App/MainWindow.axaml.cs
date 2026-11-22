using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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
        PositionChanged += (_, _) => SchedulePlacementSave();
        SizeChanged += (_, _) => SchedulePlacementSave();
        DataContextChanged += OnDataContextChanged;
        NavSplitter.DragCompleted += (_, _) =>
        {
            if (DataContext is MainViewModel { LeftPanelCollapsed: false }) RememberNavWidth(NavColumn.ActualWidth);
        };

        // Shortcuts use Command on macOS and Ctrl elsewhere.
        if (OperatingSystem.IsMacOS())
        {
            foreach (var binding in KeyBindings.ToList())
                if (binding.Gesture is { } g && g.KeyModifiers.HasFlag(KeyModifiers.Control))
                    binding.Gesture = new KeyGesture(g.Key, (g.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        }
    }

    private ColumnDefinition NavColumn => ContentGrid.ColumnDefinitions[0];

    private MainViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.PropertyChanged += OnViewModelChanged;
        ApplyPanels();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowClientsPanel) or nameof(MainViewModel.ShowLinksPanel) or nameof(MainViewModel.LeftPanelCollapsed))
            ApplyPanels();
    }

    /// <summary>Sizes the left panel and its lists from the view settings (hidden lists give their space to the others).</summary>
    private void ApplyPanels()
    {
        if (_vm is not { } vm) return;
        NavGrid.RowDefinitions[3].Height = vm.ShowClientsPanel ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        NavGrid.RowDefinitions[5].Height = vm.ShowLinksPanel ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        if (vm.LeftPanelCollapsed)
        {
            if (NavColumn.Width.IsAbsolute && NavColumn.ActualWidth > 0) RememberNavWidth(NavColumn.ActualWidth);
            NavColumn.MinWidth = 0;
            NavColumn.Width = new GridLength(0);
        }
        else
        {
            NavColumn.MinWidth = 220;
            NavColumn.Width = new GridLength(Math.Clamp(UiLayout.Current.LeftPanelWidth, 220, 900));
        }
    }

    private static void RememberNavWidth(double width)
    {
        if (Math.Abs(UiLayout.Current.LeftPanelWidth - width) < 0.5) return;
        UiLayout.Current.LeftPanelWidth = width;
        UiLayout.Save();
    }

    private void SchedulePlacementSave()
    {
        if (!IsLoaded || _shutdownComplete) return;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacement()
    {
        if (WindowState == WindowState.Minimized) return;
        UiLayout.Current.MainWindow = WindowPlacement.From(this);
        UiLayout.Save();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
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
        Dispatcher.UIThread.Post(Close);
    }
}
