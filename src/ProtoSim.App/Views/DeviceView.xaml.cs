using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class DeviceView : UserControl
{
    private readonly DataGridColumn _wordOrderColumn;

    public DeviceView()
    {
        InitializeComponent();
        _wordOrderColumn = TagGrid.Columns.First(column => Equals(column.Header, "Order"));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _wordOrderColumn.Visibility = e.NewValue is DeviceViewModel { Plugin.SupportsWordOrder: true }
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void OnTagSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DeviceViewModel device)
            device.SelectedTags = TagGrid.SelectedItems.OfType<TagViewModel>().ToList();
    }

    private void OnTagContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGridRow row || row.Item is not TagViewModel) return;
        // Right-clicking a row outside the selection selects just that row; inside it keeps the multi-selection.
        if (!row.IsSelected) TagGrid.SelectedItem = row.Item;

        if (row.ContextMenu == null)
        {
            var menu = new ContextMenu();
            var deleteItem = new MenuItem();
            deleteItem.Click += (_, _) => RemoveSelectedTags();
            menu.Items.Add(deleteItem);
            row.ContextMenu = menu;
        }
        int n = TagGrid.SelectedItems.Count;
        ((MenuItem)row.ContextMenu.Items[0]).Header = n > 1 ? $"_Delete {n} tags" : "_Delete tag";
    }

    private void OnRemoveSelectedTags(object sender, RoutedEventArgs e) => RemoveSelectedTags();

    private void OnTagGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
        // While a cell is being edited, Delete belongs to the text box.
        if (TagGrid.SelectedItems.OfType<IEditableRow>().Any(r => r.IsEditing) || e.OriginalSource is TextBox { IsReadOnly: false }) return;
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
            if (MessageDialog.Show(message, "Remove tags?", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    Window.GetWindow(this)) != MessageBoxResult.Yes) return;
        }
        device.RemoveTags(tags);
    }

    private void OnClearAllTags(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceViewModel { Tags.Count: > 0 } device) return;
        string message = $"This will permanently remove all {device.Tags.Count} tags from '{device.Name}'. This cannot be undone. Continue?";
        if (MessageDialog.Show(message, "Clear all tags?", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                Window.GetWindow(this)) != MessageBoxResult.Yes) return;

        device.RemoveAllTags();
    }

    // Live values refresh several times a second; pause refreshing the row being edited
    // so the user's typing isn't overwritten.
    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.Row.Item is IEditableRow row) row.IsEditing = true;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            if (e.EditingElement is ComboBox comboBox)
                comboBox.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
            else if (e.EditingElement is CheckBox checkBox)
                checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        }

        if (e.Row.Item is IEditableRow row) row.IsEditing = false;
    }
}
