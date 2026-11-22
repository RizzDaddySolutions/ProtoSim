using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace ProtoSim.App.Services;

/// <summary>Saved window bounds.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public static WindowPlacement From(Window w) => new()
    {
        Left = w.Position.X,
        Top = w.Position.Y,
        Width = w.Width,
        Height = w.Height,
        Maximized = w.WindowState == WindowState.Maximized,
    };

    /// <summary>Restores the bounds, ignoring a position that no longer fits on the screens (e.g. a monitor was unplugged).</summary>
    public void ApplyTo(Window w)
    {
        if (Width < 200 || Height < 150) return;
        w.Width = Width;
        w.Height = Height;
        var position = new PixelPoint((int)Left, (int)Top);
        var target = new PixelRect(position.X + 40, position.Y + 10, Math.Max(1, (int)Width - 80), 30);
        if (w.Screens.All.Any(s => s.Bounds.Intersects(target)))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = position;
        }
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

    /// <summary>Whether the Clients list is shown in the left panel.</summary>
    public bool ShowClientsPanel { get; set; } = true;

    /// <summary>Whether the Network links list is shown in the left panel.</summary>
    public bool ShowLinksPanel { get; set; } = true;

    /// <summary>True when the left panel is collapsed to a narrow strip.</summary>
    public bool LeftPanelCollapsed { get; set; }

    /// <summary>Width of the left panel when expanded.</summary>
    public double LeftPanelWidth { get; set; } = 290;

    /// <summary>Whether this instance answers network wizards on other computers (LAN discovery).</summary>
    public bool ShareOnNetwork { get; set; } = true;

    public WindowPlacement? MainWindow { get; set; }

    /// <summary>Logic designer panels the user has hidden, by panel name.</summary>
    public List<string> LogicHiddenPanels { get; set; } = [];

    /// <summary>Width of the logic designer's panel column.</summary>
    public double LogicPanelWidth { get; set; } = 250;

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
