using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ProtoSim.App.Platform;

public enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }

public enum MessageBoxImage { None, Error, Question, Warning, Information }

public enum MessageBoxResult { None, OK, Cancel, Yes, No }

/// <summary>Re-evaluates every command's CanExecute. Raised on the UI thread by the app's refresh timer and on state changes.</summary>
public static class CommandManager
{
    public static event EventHandler? RequerySuggested;

    public static void InvalidateRequerySuggested()
    {
        if (Dispatcher.UIThread.CheckAccess()) RequerySuggested?.Invoke(null, EventArgs.Empty);
        else Dispatcher.UIThread.Post(() => RequerySuggested?.Invoke(null, EventArgs.Empty));
    }
}

/// <summary>Blocking modal helpers, so view models can ask a question and use the answer on the next line on every OS.</summary>
public static class Modal
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>The window that should own a new dialog.</summary>
    public static Window? ActiveWindow
    {
        get
        {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            return desktop?.Windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? desktop?.MainWindow;
        }
    }

    /// <summary>Runs the UI loop until the task finishes, then returns its result.</summary>
    public static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Shows a dialog over the active window. True when it closed with <c>Close(true)</c>.</summary>
    public static bool ShowDialog(Window dialog)
    {
        var owner = ActiveWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
            return Wait(dialog.ShowDialog<bool>(owner));

        // No window to own it (startup, tests).
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource<bool>();
        bool result = false;
        dialog.Closed += (_, _) => closed.TrySetResult(result);
        dialog.Show();
        return Wait(closed.Task);
    }

    public static void SetClipboardText(string text)
    {
        var clipboard = (ActiveWindow as TopLevel)?.Clipboard
            ?? throw new InvalidOperationException("The clipboard isn't available.");
        Wait(SetTextAsync(clipboard, text));
    }

    private static async Task<bool> SetTextAsync(IClipboard clipboard, string text)
    {
        await clipboard.SetTextAsync(text);
        return true;
    }
}

/// <summary>Opens files and folders with the operating system's default app.</summary>
public static class Shell
{
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Shows a file in the file manager (selected where the OS supports it).</summary>
    public static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
        else Open(Path.GetDirectoryName(path) ?? path);
    }
}

/// <summary>Common base of the file pickers; <see cref="Filter"/> uses the "Name (*.ext)|*.ext|..." form.</summary>
public abstract class FileDialogBase
{
    public string Title { get; set; } = string.Empty;
    public string Filter { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    protected IStorageProvider Storage =>
        (Modal.ActiveWindow as TopLevel)?.StorageProvider ?? throw new InvalidOperationException("File dialogs aren't available.");

    protected List<FilePickerFileType> FileTypes()
    {
        var parts = Filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.TrimEntries) });
        return types;
    }

    protected static string? LocalPath(IStorageItem? item) => item?.TryGetLocalPath();
}

public sealed class OpenFileDialog : FileDialogBase
{
    public bool? ShowDialog()
    {
        var files = Modal.Wait(Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Title,
            AllowMultiple = false,
            FileTypeFilter = FileTypes(),
        }));
        if (LocalPath(files.FirstOrDefault()) is not { } path) return false;
        FileName = path;
        return true;
    }
}

public sealed class SaveFileDialog : FileDialogBase
{
    public bool? ShowDialog()
    {
        var types = FileTypes();
        var extension = types.FirstOrDefault()?.Patterns?.FirstOrDefault()?.TrimStart('*', '.');
        var file = Modal.Wait(Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Title,
            SuggestedFileName = FileName,
            DefaultExtension = extension is null or "" ? null : extension,
            FileTypeChoices = types,
            ShowOverwritePrompt = true,
        }));
        if (LocalPath(file) is not { } path) return false;
        FileName = path;
        return true;
    }
}
