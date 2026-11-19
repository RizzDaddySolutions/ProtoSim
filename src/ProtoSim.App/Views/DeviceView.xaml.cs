using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private void OnTagContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGridRow row || row.Item is not TagViewModel) return;
        TagGrid.SelectedItem = row.Item;
        if (row.ContextMenu != null) return;

        var menu = new ContextMenu();
        var deleteItem = new MenuItem { Header = "_Delete tag" };
        deleteItem.Click += OnDeleteTag;
        menu.Items.Add(deleteItem);
        row.ContextMenu = menu;
    }

    private void OnDeleteTag(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: TagViewModel tag } } } ||
            DataContext is not DeviceViewModel device) return;

        TagGrid.SelectedItem = tag;
        device.RemoveTagCommand.Execute(tag);
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
