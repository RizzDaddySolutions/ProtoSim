using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Dnp3;

public sealed class Dnp3DataModel : IDeviceDataModel
{
    private readonly Dictionary<string, Dnp3Binding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDataArea> _areas = [];

    public Dnp3DataModel(Dnp3ServerSettings settings) { }
    public IReadOnlyList<IDataArea> Areas => _areas;

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var parts = address.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var index) || index < 0)
            throw new FormatException("DNP3 addresses must use BI:<index>, AI:<index>, or C:<index>.");
        var kind = parts[0].ToUpperInvariant() switch
        {
            "BI" when type == TagDataType.Bool => Dnp3PointType.BinaryInput,
            "AI" when type is TagDataType.Float32 or TagDataType.Float64 or TagDataType.Int16 or TagDataType.Int32 or TagDataType.Int64 or TagDataType.UInt16 or TagDataType.UInt32 or TagDataType.UInt64 => Dnp3PointType.AnalogInput,
            "C" when type is TagDataType.UInt32 or TagDataType.UInt64 or TagDataType.Int32 or TagDataType.Int64 => Dnp3PointType.Counter,
            _ => throw new FormatException("DNP3 addresses must be BI:<index> (Bool), AI:<index> (numeric), or C:<index> (integer)."),
        };
        var key = $"{kind}:{index}";
        Dnp3Binding binding;
        lock (_bindings)
        {
            if (_bindings.TryGetValue(key, out var existing)) return existing;
            binding = new Dnp3Binding(this, kind, index, type);
            _bindings.Add(key, binding);
        }
        BindingAdded?.Invoke(binding);
        return binding;
    }

    internal IEnumerable<Dnp3Binding> Bindings
    {
        get { lock (_bindings) return _bindings.Values.ToList(); }
    }

    /// <summary>A tag was added; a running outstation adds the point to its database.</summary>
    internal event Action<Dnp3Binding>? BindingAdded;

    /// <summary>A value changed; a running outstation updates its database so masters see it.</summary>
    internal event Action<Dnp3Binding>? ValueChanged;

    internal void OnValueChanged(Dnp3Binding binding) => ValueChanged?.Invoke(binding);
}

internal enum Dnp3PointType { BinaryInput, AnalogInput, Counter }

internal sealed class Dnp3Binding(Dnp3DataModel model, Dnp3PointType pointType, int index, TagDataType dataType) : ITagBinding
{
    private object _value = ValueConverter.Coerce(null, dataType);
    public Dnp3PointType PointType { get; } = pointType;
    public int Index { get; } = index;
    public TagDataType DataType { get; } = dataType;
    public object Read() => _value;
    public void Write(object value)
    {
        _value = ValueConverter.Coerce(value, DataType);
        model.OnValueChanged(this);
    }
}
