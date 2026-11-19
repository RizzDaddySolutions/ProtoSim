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

    /// <summary>
    /// Whether external clients may write this tag's value. Protocols that can't enforce it keep the default (always
    /// writable). Local writes (UI, simulation, scenarios, logic) are never blocked.
    /// </summary>
    bool RemoteWritable { get => true; set { } }

    void IDisposable.Dispose() { }
}

/// <summary>
/// Shared by every tag bound to one value: counts the tags that forbid client writes.
/// The value accepts client writes only while no bound tag forbids them.
/// </summary>
public sealed class RemoteWriteGuard
{
    private int _readOnly;

    public bool AllowsWrites => Volatile.Read(ref _readOnly) <= 0;

    public void Change(int delta) => Interlocked.Add(ref _readOnly, delta);
}

/// <summary>One binding's Writeable flag; keeps a shared <see cref="RemoteWriteGuard"/> up to date.</summary>
public sealed class RemoteWriteFlag(Action<int> change)
{
    private int _readOnly;

    public RemoteWriteFlag(RemoteWriteGuard guard) : this(guard.Change) { }

    public bool Writable
    {
        get => Volatile.Read(ref _readOnly) == 0;
        set
        {
            int next = value ? 0 : 1;
            int old = Interlocked.Exchange(ref _readOnly, next);
            if (old != next) change(next - old);
        }
    }

    /// <summary>Call when the binding is disposed so it stops counting.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _readOnly, 0) == 1) change(-1);
    }
}
