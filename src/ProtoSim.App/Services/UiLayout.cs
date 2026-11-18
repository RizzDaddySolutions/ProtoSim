using System.Text.Json;
using System.Windows;

namespace ProtoSim.App.Services;

/// <summary>Saved window bounds.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public static WindowPlacement From(Window w)
    {
        var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
        return new WindowPlacement { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = w.WindowState == WindowState.Maximized };
    }

    /// <summary>Restores the bounds, ignoring them if they no longer fit on the screens (e.g. a monitor was unplugged).</summary>
    public void ApplyTo(Window w)
    {
        if (Width < 200 || Height < 150) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(Left + 40, Top + 10, Math.Max(1, Width - 80), 30))) return;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = Left;
        w.Top = Top;
        w.Width = Width;
        w.Height = Height;
        if (Maximized) w.WindowState = WindowState.Maximized;
    }
}

/// <summary>
/// Per-user UI layout (not part of a project): where the logic designer's panels are docked, whether the designer
/// is docked in the main window or in its own window, and that window's bounds.
/// Stored in %LocalAppData%\ProtoSim\ui-layout.json.
/// </summary>
public sealed class UiLayout
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtoSim", "ui-layout.json");

    private static UiLayout? _current;

    /// <summary>Selected theme mode: System, Light, or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Whether tag simulation is enabled.</summary>
    public bool SimulationEnabled { get; set; }

    public WindowPlacement? MainWindow { get; set; }

    /// <summary>AvalonDock layout XML of the logic designer's panels.</summary>
    public string? LogicPanels { get; set; }

    /// <summary>True when the designer opens in its own window instead of inside the main window.</summary>
    public bool LogicUndocked { get; set; }

    /// <summary>True when the undocked designer window was open at exit (it reopens on the next start).</summary>
    public bool LogicWindowOpen { get; set; }

    public WindowPlacement? LogicWindow { get; set; }

    public static UiLayout Current => _current ??= Load();

    private static UiLayout Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiLayout>(File.ReadAllText(FilePath)) ?? new UiLayout();
        }
        catch (Exception)
        {
            // A corrupt layout file just means default layout.
        }
        return new UiLayout();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Layout is a convenience; never fail the app over it.
        }
    }
}
