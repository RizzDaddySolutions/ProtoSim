using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using ProtoSim.App.Platform;
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
        _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        PositionChanged += (_, _) => SchedulePlacementSave();
        SizeChanged += (_, _) => SchedulePlacementSave();
        Icon = Modal.MainWindow?.Icon;
        DataContext = main.Logic;
        Content = new Border { Padding = new Thickness(12), Child = new LogicView() };
        Bind(TitleProperty, new Binding(nameof(MainViewModel.Title)) { Source = main, StringFormat = "Logic designer · {0}" });
        UiLayout.Current.LogicWindow?.ApplyTo(this);

        // The main window's shortcuts work here too.
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyBindings.Add(new KeyBinding { Command = main.SaveCommand, Gesture = new KeyGesture(Key.S, command) });
        KeyBindings.Add(new KeyBinding { Command = main.SaveAsCommand, Gesture = new KeyGesture(Key.S, command | KeyModifiers.Shift) });
        KeyBindings.Add(new KeyBinding { Command = main.OpenCommand, Gesture = new KeyGesture(Key.O, command) });
        KeyBindings.Add(new KeyBinding { Command = main.StartAllCommand, Gesture = new KeyGesture(Key.F5) });
        KeyBindings.Add(new KeyBinding { Command = main.StopAllCommand, Gesture = new KeyGesture(Key.F5, KeyModifiers.Shift) });
        KeyBindings.Add(new KeyBinding { Command = main.HelpCommand, Gesture = new KeyGesture(Key.F1) });
        KeyBindings.Add(new KeyBinding { Command = main.ToggleLogicDockCommand, Gesture = new KeyGesture(Key.L, command | KeyModifiers.Shift) });
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _placementSaveTimer.Stop();
        LogicView.SaveOpenLayouts();
        SavePlacement();
    }
}
