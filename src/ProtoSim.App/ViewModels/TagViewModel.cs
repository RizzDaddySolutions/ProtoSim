using System.ComponentModel;
using ProtoSim.Core;
using ProtoSim.Core.Data;
using ProtoSim.Core.Simulation;

namespace ProtoSim.App.ViewModels;

public static class EnumLists
{
    public static TagDataType[] DataTypes { get; } = Enum.GetValues<TagDataType>();
    public static WordOrder[] WordOrders { get; } = Enum.GetValues<WordOrder>();
    public static SimulationKind[] SimulationKinds { get; } = Enum.GetValues<SimulationKind>();
}

/// <summary>Grid row for a tag: keeps the binding in sync with address/type edits and shows the live value.</summary>
public sealed class TagViewModel : ObservableObject, IEditableRow
{
    private readonly IDeviceDataModel _model;
    private string _valueText = string.Empty;
    private string? _error;

    public TagViewModel(SimTag tag, IDeviceDataModel model)
    {
        Tag = tag;
        _model = model;
        Tag.PropertyChanged += OnTagChanged;
        _simulation = tag.Simulation;
        _simulation.PropertyChanged += OnSimulationChanged;
        Rebind();
    }

    private SimulationSettings _simulation;

    // Which simulation parameters apply to this row's simulation kind (others are greyed out).
    public bool UsesRange => SimulationSettings.UsesRange(Tag.Simulation.Kind);
    public bool UsesPeriod => SimulationSettings.UsesPeriod(Tag.Simulation.Kind);
    public bool UsesStep => SimulationSettings.UsesStep(Tag.Simulation.Kind);
    public bool UsesInterval => SimulationSettings.UsesInterval(Tag.Simulation.Kind);

    /// <summary>Word order only matters for values that span more than one 16-bit register.</summary>
    public bool UsesWordOrder => Tag.DataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Int64
        or TagDataType.UInt64 or TagDataType.Float32 or TagDataType.Float64;

    private void OnSimulationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SimulationSettings.Kind) or null) RaiseSimulationShape();
    }

    private void RaiseSimulationShape()
    {
        OnPropertyChanged(nameof(UsesRange));
        OnPropertyChanged(nameof(UsesPeriod));
        OnPropertyChanged(nameof(UsesStep));
        OnPropertyChanged(nameof(UsesInterval));
    }

    public SimTag Tag { get; }
    public bool IsEditing { get; set; }

    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error != null;

    public string ValueText
    {
        get => _valueText;
        set
        {
            var binding = Tag.Binding;
            if (binding == null) return;
            try
            {
                binding.Write(value);
                Error = null;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                Error = ex.Message;
            }
            Refresh();
        }
    }

    private void OnTagChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SimTag.Address) or nameof(SimTag.DataType) or nameof(SimTag.WordOrder))
            Rebind();
        if (e.PropertyName == nameof(SimTag.DataType)) OnPropertyChanged(nameof(UsesWordOrder));
        else if (e.PropertyName == nameof(SimTag.Simulation) && !ReferenceEquals(_simulation, Tag.Simulation))
        {
            _simulation.PropertyChanged -= OnSimulationChanged;
            _simulation = Tag.Simulation;
            _simulation.PropertyChanged += OnSimulationChanged;
            RaiseSimulationShape();
        }
    }

    public void Rebind()
    {
        var old = Tag.Binding;
        Tag.Binding = null;
        old?.Dispose();
        try
        {
            Tag.Binding = _model.Bind(Tag.Address, Tag.DataType, Tag.WordOrder);
            Error = null;
        }
        catch (Exception ex)
        {
            Tag.Binding = null;
            Error = ex.Message;
        }
        Refresh();
    }

    public void Refresh()
    {
        if (IsEditing) return;
        var b = Tag.Binding;
        string text;
        try { text = b == null ? "—" : ValueConverter.Format(b.Read()); }
        catch (Exception ex) { text = "—"; Error = ex.Message; }
        if (text != _valueText)
        {
            _valueText = text;
            OnPropertyChanged(nameof(ValueText));
        }
    }

    /// <summary>Stops tracking the tag and releases its binding (removes the node for node-based protocols).</summary>
    public void Detach()
    {
        Tag.PropertyChanged -= OnTagChanged;
        _simulation.PropertyChanged -= OnSimulationChanged;
        var b = Tag.Binding;
        Tag.Binding = null;
        b?.Dispose();
    }
}
