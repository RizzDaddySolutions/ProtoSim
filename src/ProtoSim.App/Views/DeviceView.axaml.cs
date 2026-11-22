using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ProtoSim.App.Platform;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class DeviceView : UserControl
{
    private readonly DataGridColumn _wordOrderColumn;

    public DeviceView()
    {
        InitializeComponent();
        _wordOrderColumn = TagGrid.Columns.First(column => Equals(column.Header, "Order"));
        DataContextChanged += (_, _) => _wordOrderColumn.IsVisible = DataContext is DeviceViewModel { Plugin.SupportsWordOrder: true };
        TagGrid.AddHandler(KeyDownEvent, OnTagGridKeyDown, RoutingStrategies.Tunnel);
        TagGrid.AddHandler(PointerPressedEvent, OnTagGridPointerPressed, RoutingStrategies.Tunnel);
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void OnTagSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DeviceViewModel device)
            device.SelectedTags = TagGrid.SelectedItems.OfType<TagViewModel>().ToList();
    }

    /// <summary>Right-clicking a row outside the selection selects just that row; inside it keeps the multi-selection.</summary>
    private void OnTagGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TagGrid).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Control)?.FindAncestorOfType<DataGridRow>(true);
        if (row?.DataContext is TagViewModel tag && !TagGrid.SelectedItems.Contains(tag)) TagGrid.SelectedItem = tag;
    }

    private void OnTagContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        int n = TagGrid.SelectedItems.Count;
        if (n == 0) { e.Cancel = true; return; }
        DeleteTagsItem.Header = n > 1 ? $"_Delete {n} tags" : "_Delete tag";
    }

    private void OnRemoveSelectedTags(object? sender, RoutedEventArgs e) => RemoveSelectedTags();

    private void OnTagGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
        // While a cell is being edited, Delete belongs to the text box.
        if (TagGrid.SelectedItems.OfType<IEditableRow>().Any(r => r.IsEditing) || e.Source is TextBox { IsReadOnly: false }) return;
        RemoveSelectedTags();
        e.Handled = true;
    }

    /// <summary>Removes every selected tag; asks first when there is more than one.</summary>
    private void RemoveSelectedTags()
    {
        if (DataContext is not DeviceViewModel device) return;
        var tags = TagGrid.SelectedItems.OfType<TagViewModel>().ToList();
        if (tags.Count == 0) return;
        if (tags.Count > 1)
        {
            string message = $"Remove {tags.Count} tags from '{device.Name}'? This cannot be undone.";
            if (MessageDialog.Show(message, "Remove tags?", MessageBoxButton.YesNo, MessageBoxImage.Warning, OwnerWindow) != MessageBoxResult.Yes) return;
        }
        device.RemoveTags(tags);
    }

    private void OnClearAllTags(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel { Tags.Count: > 0 } device) return;
        string message = $"This will permanently remove all {device.Tags.Count} tags from '{device.Name}'. This cannot be undone. Continue?";
        if (MessageDialog.Show(message, "Clear all tags?", MessageBoxButton.YesNo, MessageBoxImage.Warning, OwnerWindow) != MessageBoxResult.Yes) return;
        device.RemoveAllTags();
    }

    // Live values refresh several times a second; pause refreshing the row being edited
    // so the user's typing isn't overwritten.
    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.Row.DataContext is IEditableRow row) row.IsEditing = true;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.DataContext is IEditableRow row) row.IsEditing = false;
    }
}
