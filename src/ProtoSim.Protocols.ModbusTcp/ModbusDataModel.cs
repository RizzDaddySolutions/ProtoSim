using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Memory of a simulated Modbus device: coils, discrete inputs, input and holding registers.</summary>
public sealed class ModbusDataModel : IDeviceDataModel
{
    /// <summary>Lock guarding all four areas, so multi-register operations are atomic.</summary>
    internal readonly object Sync = new();

    public ModbusDataModel(ModbusServerSettings settings)
    {
        Coils = new ModbusDataArea(this, ModbusAreaKind.Coils, () => settings.CoilCount);
        DiscreteInputs = new ModbusDataArea(this, ModbusAreaKind.DiscreteInputs, () => settings.DiscreteInputCount);
        InputRegisters = new ModbusDataArea(this, ModbusAreaKind.InputRegisters, () => settings.InputRegisterCount);
        HoldingRegisters = new ModbusDataArea(this, ModbusAreaKind.HoldingRegisters, () => settings.HoldingRegisterCount);
        Areas = [HoldingRegisters, InputRegisters, Coils, DiscreteInputs];
    }

    public ModbusDataArea Coils { get; }
    public ModbusDataArea DiscreteInputs { get; }
    public ModbusDataArea InputRegisters { get; }
    public ModbusDataArea HoldingRegisters { get; }

    public IReadOnlyList<IDataArea> Areas { get; }

    public ModbusDataArea GetArea(ModbusAreaKind kind) => kind switch
    {
        ModbusAreaKind.Coils => Coils,
        ModbusAreaKind.DiscreteInputs => DiscreteInputs,
        ModbusAreaKind.InputRegisters => InputRegisters,
        _ => HoldingRegisters,
    };

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var a = ModbusAddress.Parse(address);
        if (type == TagDataType.String)
            throw new FormatException("Modbus tags cannot use the String type.");
        if (a.IsBitArea && type != TagDataType.Bool)
            throw new FormatException($"{ModbusNames.Prefix(a.Area)} holds single bits; use data type Bool.");
        if (a.Bit >= 0 && type != TagDataType.Bool)
            throw new FormatException("A register bit (e.g. HR:10.3) must use data type Bool.");

        int words = a.IsBitArea || a.Bit >= 0 ? 1 : RegisterCodec.WordCount(type);
        if (a.Index + words > ModbusLimits.AddressSpace)
            throw new FormatException($"{type} at offset {a.Index} runs past the end of the address space.");

        return new ModbusTagBinding(this, GetArea(a.Area), a, type, order);
    }
}

public sealed class ModbusDataArea : IDataArea
{
    private readonly ModbusDataModel _model;
    private readonly Func<int> _count;
    private readonly ushort[] _values = new ushort[ModbusLimits.AddressSpace];

    internal ModbusDataArea(ModbusDataModel model, ModbusAreaKind kind, Func<int> count)
    {
        _model = model;
        _count = count;
        AreaKind = kind;
    }

    public ModbusAreaKind AreaKind { get; }

    public string Name => AreaKind switch
    {
        ModbusAreaKind.Coils => "Coils",
        ModbusAreaKind.DiscreteInputs => "Discrete Inputs",
        ModbusAreaKind.InputRegisters => "Input Registers",
        _ => "Holding Registers",
    };

    public string ShortName => ModbusNames.Prefix(AreaKind);
    public DataElementKind Kind => AreaKind is ModbusAreaKind.Coils or ModbusAreaKind.DiscreteInputs ? DataElementKind.Bit : DataElementKind.Word;
    public int Count => Math.Clamp(_count(), 1, ModbusLimits.AddressSpace);
    public bool IsRemoteWritable => AreaKind is ModbusAreaKind.Coils or ModbusAreaKind.HoldingRegisters;

    public ushort Read(int index)
    {
        lock (_model.Sync) return _values[index];
    }

    public void Write(int index, ushort value)
    {
        lock (_model.Sync) SetUnsafe(index, value);
    }

    public void ReadRange(int start, Span<ushort> destination)
    {
        lock (_model.Sync) _values.AsSpan(start, destination.Length).CopyTo(destination);
    }

    public void WriteRange(int start, ReadOnlySpan<ushort> values)
    {
        lock (_model.Sync)
        {
            for (int i = 0; i < values.Length; i++) SetUnsafe(start + i, values[i]);
        }
    }

    public string FormatAddress(int index) => ModbusNames.Modicon(AreaKind, index);

    // Callers must hold _model.Sync.
    internal ushort GetUnsafe(int index) => _values[index];
    internal void SetUnsafe(int index, ushort value) =>
        _values[index] = Kind == DataElementKind.Bit ? (ushort)(value != 0 ? 1 : 0) : value;
}

internal sealed class ModbusTagBinding(ModbusDataModel model, ModbusDataArea area, ModbusAddress address, TagDataType type, WordOrder order) : ITagBinding
{
    public TagDataType DataType => type;

    public object Read()
    {
        if (address.IsBitArea) return area.Read(address.Index) != 0;
        if (address.Bit >= 0) return ((area.Read(address.Index) >> address.Bit) & 1) != 0;

        Span<ushort> words = stackalloc ushort[RegisterCodec.WordCount(type)];
        area.ReadRange(address.Index, words);
        return RegisterCodec.Decode(words, type, order);
    }

    public void Write(object value)
    {
        var v = ValueConverter.Coerce(value, type);
        if (address.IsBitArea)
        {
            area.Write(address.Index, (bool)v ? (ushort)1 : (ushort)0);
        }
        else if (address.Bit >= 0)
        {
            lock (model.Sync)
            {
                ushort mask = (ushort)(1 << address.Bit);
                ushort cur = area.GetUnsafe(address.Index);
                area.SetUnsafe(address.Index, (bool)v ? (ushort)(cur | mask) : (ushort)(cur & ~mask));
            }
        }
        else
        {
            area.WriteRange(address.Index, RegisterCodec.Encode(v, type, order));
        }
    }
}
