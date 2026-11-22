using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ProtoSim.App.Platform;

namespace ProtoSim.App.Views;

/// <summary>Themed message box: takes a message and buttons, and blocks the caller until the user answers.</summary>
public partial class MessageDialog : Window
{
    private MessageBoxResult _result;

    public MessageDialog() : this(string.Empty, string.Empty, MessageBoxButton.OK, MessageBoxImage.None) { }

    private MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Heading.Text = title;
        MessageText.Text = message;
        _result = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };
        SetIcon(image);
        AddButtons(buttons);
    }

    /// <summary>Shows a message owned by the active window and waits for the user's answer.</summary>
    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, Window? owner = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => Show(message, title, buttons, image, owner));

        var dialog = new MessageDialog(message, title, buttons, image);
        owner ??= Modal.ActiveWindow;
        if (owner is { IsVisible: true }) Modal.Wait(dialog.ShowDialog<bool>(owner));
        else Modal.ShowDialog(dialog);
        return dialog._result;
    }

    /// <summary>Yes/No question; true when the user chose Yes.</summary>
    public static bool Confirm(string message, string title = "Confirm") =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void SetIcon(MessageBoxImage image)
    {
        (string? key, Color? color) = image switch
        {
            MessageBoxImage.Error => ("IconClose", Color.FromRgb(0xD8, 0x3B, 0x3B)),
            MessageBoxImage.Warning => ("IconRecord", Color.FromRgb(0xE0, 0x9B, 0x1A)),
            MessageBoxImage.Question => ("IconSearch", (Color?)null),
            MessageBoxImage.Information => ("IconDevice", (Color?)null),
            _ => (null, null),
        };
        if (key == null || !Application.Current!.TryFindResource(key, out var geometry) || geometry is not Geometry g) return;
        IconGlyph.Data = g;
        if (color is { } c) IconGlyph.Foreground = new SolidColorBrush(c);
        else if (Application.Current!.TryGetResource("AccentFillColorDefaultBrush", ActualThemeVariant, out var brush) && brush is IBrush accent) IconGlyph.Foreground = accent;
        IconGlyph.IsVisible = true;
    }

    private void AddButtons(MessageBoxButton buttons)
    {
        var set = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)],
        };

        for (int i = 0; i < set.Length; i++)
        {
            var (label, result) = set[i];
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(20, 6, 20, 6),
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                // Esc maps to the same answer as closing the window.
                IsCancel = result == _result,
            };
            if (i == 0) button.Classes.Add("accent");
            button.Click += (_, _) => { _result = result; Close(true); };
            ButtonPanel.Children.Add(button);
        }
    }
}
