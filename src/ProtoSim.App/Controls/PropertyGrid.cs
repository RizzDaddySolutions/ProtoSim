using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProtoSim.App.Controls;

/// <summary>Generic editor for plugin settings objects. Groups by [Display(GroupName)], validates with [Range] etc.</summary>
public sealed class PropertyGrid : UserControl
{
    public static readonly StyledProperty<object?> SelectedObjectProperty =
        AvaloniaProperty.Register<PropertyGrid, object?>(nameof(SelectedObject));

    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<PropertyGrid, double>(nameof(LabelWidth), 190);

    private readonly StackPanel _items = new();

    static PropertyGrid()
    {
        SelectedObjectProperty.Changed.AddClassHandler<PropertyGrid>((g, _) => g.Rebuild());
        LabelWidthProperty.Changed.AddClassHandler<PropertyGrid>((g, _) => g.Rebuild());
    }

    public PropertyGrid() =>
        Content = new ScrollViewer { Content = _items, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public object? SelectedObject { get => GetValue(SelectedObjectProperty); set => SetValue(SelectedObjectProperty, value); }
    public double LabelWidth { get => GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    /// <summary>Raised whenever any property is changed through the grid.</summary>
    public event EventHandler? PropertyValueChanged;

    private void Rebuild()
    {
        _items.Children.Clear();
        foreach (var group in PropertyItem.For(SelectedObject).GroupBy(i => i.Category))
        {
            var header = new TextBlock { Text = group.Key, Margin = new Thickness(0, 14, 0, 6) };
            header.Classes.Add("sectionHeader");
            _items.Children.Add(header);
            foreach (var item in group)
            {
                item.ValueChanged += (_, _) => PropertyValueChanged?.Invoke(this, EventArgs.Empty);
                _items.Children.Add(Row(item));
            }
        }
    }

    private Control Row(PropertyItem item)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 3),
            DataContext = item,
            ColumnDefinitions = new ColumnDefinitions(LabelWidth.ToString(CultureInfo.InvariantCulture) + ",*"),
        };
        if (item.Description.Length > 0) ToolTip.SetTip(grid, item.Description);
        grid.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });

        Control editor;
        if (item.IsBool)
        {
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PropertyItem.BoolValue)) { Mode = BindingMode.TwoWay });
            editor = check;
        }
        else if (item.IsEnum)
        {
            var combo = new ComboBox { ItemsSource = item.EnumValues, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(PropertyItem.Value)) { Mode = BindingMode.TwoWay });
            editor = combo;
        }
        else
        {
            var box = new TextBox
            {
                MinHeight = item.IsMultiline ? 160 : 32,
                MaxHeight = item.IsMultiline ? 420 : double.PositiveInfinity,
                AcceptsReturn = item.IsMultiline,
                VerticalContentAlignment = item.IsMultiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            };
            if (item.IsMultiline && this.TryFindResource("MonoFont", out var mono) && mono is FontFamily family) box.FontFamily = family;
            box.Bind(TextBox.TextProperty, new Binding(nameof(PropertyItem.Text)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            editor = box;
        }
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }
}
