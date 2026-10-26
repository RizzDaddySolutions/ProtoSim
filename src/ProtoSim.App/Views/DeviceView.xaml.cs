using System.Windows.Controls;
using ProtoSim.App.ViewModels;

namespace ProtoSim.App.Views;

public partial class DeviceView : UserControl
{
    public DeviceView()
    {
        InitializeComponent();
    }

    // Live values refresh several times a second; pause refreshing the row being edited
    // so the user's typing isn't overwritten.
    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.Row.Item is IEditableRow row) row.IsEditing = true;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is IEditableRow row) row.IsEditing = false;
    }
}
