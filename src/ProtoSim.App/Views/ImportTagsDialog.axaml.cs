using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProtoSim.App.Views;

public enum TagImportMode
{
    Append,
    Overwrite,
}

/// <summary>Asks whether imported CSV tags should be appended to, or replace, the device's tags.</summary>
public partial class ImportTagsDialog : Window
{
    public ImportTagsDialog() : this(string.Empty, 0, 0) { }

    public ImportTagsDialog(string fileName, int importCount, int existingCount)
    {
        FileName = fileName;
        ImportCount = importCount;
        ExistingCount = existingCount;
        InitializeComponent();
        DataContext = this;
    }

    public string FileName { get; }
    public int ImportCount { get; }
    public int ExistingCount { get; }

    public TagImportMode Mode => OverwriteOption.IsChecked == true ? TagImportMode.Overwrite : TagImportMode.Append;

    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
