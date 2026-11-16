using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProtoSim.App.Views;

/// <summary>
/// Themed replacement for <see cref="MessageBox"/> so messages and confirmations match the rest of the app.
/// Takes the same arguments and returns the same results as <c>MessageBox.Show</c>.
/// </summary>
public partial class MessageDialog : Window
{
    private static readonly Brush WarningBrush = Freeze(Color.FromRgb(0xE0, 0x9B, 0x1A));
    private static readonly Brush ErrorBrush = Freeze(Color.FromRgb(0xD8, 0x3B, 0x3B));

    private MessageBoxResult _result;

    private MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = "ProtoSim";
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
        var app = Application.Current;
        if (app == null) return MessageBox.Show(message, title, buttons, image);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(message, title, buttons, image, owner));

        var dialog = new MessageDialog(message, title, buttons, image);
        owner ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        if (owner is { IsLoaded: true, IsVisible: true } && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>Yes/No question; true when the user chose Yes.</summary>
    public static bool Confirm(string message, string title = "Confirm") =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void SetIcon(MessageBoxImage image)
    {
        // MessageBoxImage has aliased values (Error == Hand == Stop, Warning == Exclamation, Information == Asterisk).
        (string? glyph, Brush? brush) = image switch
        {
            MessageBoxImage.Error => ("", ErrorBrush),
            MessageBoxImage.Warning => ("", WarningBrush),
            MessageBoxImage.Question => ("", null),
            MessageBoxImage.Information => ("", null),
            _ => (null, null),
        };
        if (glyph == null) return;

        IconGlyph.Text = glyph;
        if (brush != null) IconGlyph.Foreground = brush;
        else IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
        IconGlyph.Visibility = Visibility.Visible;
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
                Padding = new Thickness(20, 6, 20, 6),
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                // Esc maps to the same answer as closing the window.
                IsCancel = result == _result,
            };
            if (i == 0) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
            button.Click += (_, _) => { _result = result; DialogResult = true; };
            ButtonPanel.Children.Add(button);
        }
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
