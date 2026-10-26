using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ProtoSim.App.Controls;

/// <summary>Generic editor for plugin settings objects. Groups by [Display(GroupName)], validates with [Range] etc.</summary>
public partial class PropertyGrid : UserControl
{
    public static readonly DependencyProperty SelectedObjectProperty = DependencyProperty.Register(
        nameof(SelectedObject), typeof(object), typeof(PropertyGrid),
        new PropertyMetadata(null, (d, _) => ((PropertyGrid)d).Rebuild()));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(GridLength), typeof(PropertyGrid), new PropertyMetadata(new GridLength(190)));

    public PropertyGrid()
    {
        InitializeComponent();
    }

    public object? SelectedObject
    {
        get => GetValue(SelectedObjectProperty);
        set => SetValue(SelectedObjectProperty, value);
    }

    public GridLength LabelWidth
    {
        get => (GridLength)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    /// <summary>Raised whenever any property is changed through the grid.</summary>
    public event EventHandler? PropertyValueChanged;

    private void Rebuild()
    {
        var items = PropertyItem.For(SelectedObject);
        foreach (var item in items) item.ValueChanged += (_, _) => PropertyValueChanged?.Invoke(this, EventArgs.Empty);
        var view = new ListCollectionView(items.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PropertyItem.Category)));
        Items.ItemsSource = view;
    }
}
