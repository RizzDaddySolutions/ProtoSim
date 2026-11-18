using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ProtoSim.App.Services;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

/// <summary>The logic designer undocked from the main window. Its bounds are remembered between runs.</summary>
public sealed class LogicWindow : Window
{
    private readonly DispatcherTimer _placementSaveTimer;

    public LogicWindow(MainViewModel main)
    {
        Width = 1280;
        Height = 820;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "ApplicationWindowBackgroundBrush");
        _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        LocationChanged += (_, _) => SchedulePlacementSave();
        SizeChanged += (_, _) => SchedulePlacementSave();
        StateChanged += (_, _) => SchedulePlacementSave();
        Icon = Application.Current.MainWindow?.Icon;
        DataContext = main.Logic;
        Content = new Border { Padding = new Thickness(12), Child = new LogicView() };
        SetBinding(TitleProperty, new Binding(nameof(MainViewModel.Title)) { Source = main, StringFormat = "Logic designer · {0}" });
        UiLayout.Current.LogicWindow?.ApplyTo(this);

        // The main window's shortcuts work here too.
        InputBindings.Add(new KeyBinding(main.SaveCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(main.SaveAsCommand, Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(main.OpenCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(main.StartAllCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(main.StopAllCommand, Key.F5, ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(main.HelpCommand, Key.F1, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(main.ToggleLogicDockCommand, Key.L, ModifierKeys.Control | ModifierKeys.Shift));
    }

    private void SchedulePlacementSave()
    {
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacement()
    {
        UiLayout.Current.LogicWindow = WindowPlacement.From(this);
        UiLayout.Save();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        _placementSaveTimer.Stop();
        LogicView.SaveOpenLayouts();
        SavePlacement();
    }
}
