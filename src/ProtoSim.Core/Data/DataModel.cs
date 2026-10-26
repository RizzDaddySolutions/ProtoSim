namespace ProtoSim.Core.Data;

/// <summary>
/// A flat table of bits or 16-bit words exposed by a simulated device
/// (e.g. Modbus holding registers, DNP3 analog inputs, S7 data blocks).
/// </summary>
public interface IDataArea
{
    string Name { get; }
    string ShortName { get; }
    DataElementKind Kind { get; }

    /// <summary>Number of valid elements (addresses 0..Count-1).</summary>
    int Count { get; }

    /// <summary>True if remote clients may write to this area.</summary>
    bool IsRemoteWritable { get; }

    ushort Read(int index);
    void Write(int index, ushort value);

    /// <summary>Protocol-specific display address for an element (e.g. "40001").</summary>
    string FormatAddress(int index);
}

/// <summary>The memory of a simulated device. Created once per device and kept across server restarts.</summary>
public interface IDeviceDataModel
{
    IReadOnlyList<IDataArea> Areas { get; }

    /// <summary>Binds a tag to a protocol-specific address. Throws <see cref="FormatException"/> on bad input.</summary>
    ITagBinding Bind(string address, TagDataType type, WordOrder order);
}

/// <summary>A typed view onto one or more elements of a data model.</summary>
/// <remarks>
/// Disposed when the tag is removed or re-addressed, so node-based models can remove the node.
/// Register-based models need not override <see cref="IDisposable.Dispose"/>.
/// </remarks>
public interface ITagBinding : IDisposable
{
    TagDataType DataType { get; }
    object Read();
    void Write(object value);

    void IDisposable.Dispose() { }
}
