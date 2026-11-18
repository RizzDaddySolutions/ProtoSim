using System.Buffers.Binary;
using System.Text;

namespace ProtoSim.Core.Data;

/// <summary>Encodes typed values as raw bytes (big- or little-endian).</summary>
public static class ByteCodec
{
    public static int Size(TagDataType type) => type switch
    {
        TagDataType.Bool => 1,
        TagDataType.Int16 or TagDataType.UInt16 => 2,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 4,
        TagDataType.String => throw new NotSupportedException("String has no fixed size."),
        _ => 8,
    };

    public static void Encode(object value, TagDataType type, Span<byte> dest, bool bigEndian)
    {
        var v = ValueConverter.Coerce(value, type);
        switch (type)
        {
            case TagDataType.Bool: dest[0] = (bool)v ? (byte)1 : (byte)0; break;
            case TagDataType.Int16: if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(dest, (short)v); else BinaryPrimitives.WriteInt16LittleEndian(dest, (short)v); break;
            case TagDataType.UInt16: if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)v); break;
            case TagDataType.Int32: if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(dest, (int)v); else BinaryPrimitives.WriteInt32LittleEndian(dest, (int)v); break;
            case TagDataType.UInt32: if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(dest, (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)v); break;
            case TagDataType.Int64: if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(dest, (long)v); else BinaryPrimitives.WriteInt64LittleEndian(dest, (long)v); break;
            case TagDataType.UInt64: if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(dest, (ulong)v); else BinaryPrimitives.WriteUInt64LittleEndian(dest, (ulong)v); break;
            case TagDataType.Float32: if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(dest, (float)v); else BinaryPrimitives.WriteSingleLittleEndian(dest, (float)v); break;
            case TagDataType.Float64: if (bigEndian) BinaryPrimitives.WriteDoubleBigEndian(dest, (double)v); else BinaryPrimitives.WriteDoubleLittleEndian(dest, (double)v); break;
            default: throw new NotSupportedException($"{type} can't be encoded as fixed-size bytes.");
        }
    }

    public static byte[] Encode(object value, TagDataType type, bool bigEndian)
    {
        var b = new byte[Size(type)];
        Encode(value, type, b, bigEndian);
        return b;
    }

    public static object Decode(ReadOnlySpan<byte> src, TagDataType type, bool bigEndian) => type switch
    {
        TagDataType.Bool => src[0] != 0,
        TagDataType.Int16 => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(src) : BinaryPrimitives.ReadInt16LittleEndian(src),
        TagDataType.UInt16 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(src) : BinaryPrimitives.ReadUInt16LittleEndian(src),
        TagDataType.Int32 => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(src) : BinaryPrimitives.ReadInt32LittleEndian(src),
        TagDataType.UInt32 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(src) : BinaryPrimitives.ReadUInt32LittleEndian(src),
        TagDataType.Int64 => bigEndian ? BinaryPrimitives.ReadInt64BigEndian(src) : BinaryPrimitives.ReadInt64LittleEndian(src),
        TagDataType.UInt64 => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(src) : BinaryPrimitives.ReadUInt64LittleEndian(src),
        TagDataType.Float32 => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(src) : BinaryPrimitives.ReadSingleLittleEndian(src),
        TagDataType.Float64 => bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(src) : BinaryPrimitives.ReadDoubleLittleEndian(src),
        _ => throw new NotSupportedException($"{type} can't be decoded from fixed-size bytes."),
    };
}

/// <summary>
/// A table of 16-bit words or single bits with a fixed capacity, used by word-addressed PLC protocols
/// (Omron FINS, Mitsubishi MC...). All areas of one device share a lock so multi-word operations are atomic.
/// </summary>
public sealed class WordArea : IDataArea
{
    private readonly ushort[] _values;
    private readonly Func<int> _count;
    private readonly Func<int, string> _format;

    public WordArea(object sync, string name, string shortName, DataElementKind kind, int capacity,
        bool remoteWritable, Func<int, string> format, Func<int>? count = null)
    {
        Sync = sync;
        Name = name;
        ShortName = shortName;
        Kind = kind;
        Capacity = capacity;
        IsRemoteWritable = remoteWritable;
        _values = new ushort[capacity];
        _format = format;
        _count = count ?? (() => capacity);
    }

    public object Sync { get; }
    public string Name { get; }
    public string ShortName { get; }
    public DataElementKind Kind { get; }
    public int Capacity { get; }
    public int Count => Math.Clamp(_count(), 1, Capacity);
    public bool IsRemoteWritable { get; }

    public ushort Read(int index)
    {
        lock (Sync) return _values[index];
    }

    public void Write(int index, ushort value)
    {
        lock (Sync) SetUnsafe(index, value);
    }

    public string FormatAddress(int index) => _format(index);

    /// <summary>True when [start, start+length) lies inside the area.</summary>
    public bool InRange(int start, int length) => start >= 0 && length >= 0 && start + length <= Count;

    // Callers must hold Sync.
    public ushort GetUnsafe(int index) => _values[index];
    public void SetUnsafe(int index, ushort value) =>
        _values[index] = Kind == DataElementKind.Bit ? (ushort)(value != 0 ? 1 : 0) : value;

    public bool GetBitUnsafe(int index, int bit) => Kind == DataElementKind.Bit ? _values[index] != 0 : ((_values[index] >> bit) & 1) != 0;

    public void SetBitUnsafe(int index, int bit, bool on)
    {
        if (Kind == DataElementKind.Bit) { _values[index] = on ? (ushort)1 : (ushort)0; return; }
        ushort mask = (ushort)(1 << bit);
        _values[index] = on ? (ushort)(_values[index] | mask) : (ushort)(_values[index] & ~mask);
    }
}

/// <summary>Binds a tag to one or more words (or one bit) of a <see cref="WordArea"/>.</summary>
public sealed class WordAreaBinding : ITagBinding
{
    private readonly WordArea _area;
    private readonly int _index;
    private readonly int _bit;
    private readonly WordOrder _order;

    /// <param name="bit">Bit within the word (0-15) for Bool tags on word areas; -1 otherwise.</param>
    public WordAreaBinding(WordArea area, int index, int bit, TagDataType type, WordOrder order)
    {
        if (type == TagDataType.String) throw new FormatException($"{area.Name} can't hold String tags.");
        if (area.Kind == DataElementKind.Bit && type != TagDataType.Bool)
            throw new FormatException($"{area.Name} holds single bits; use data type Bool.");
        if (bit >= 0 && type != TagDataType.Bool)
            throw new FormatException("A bit address (word.bit) must use data type Bool.");
        if (bit > 15) throw new FormatException("Bit numbers run from 0 to 15.");
        int words = area.Kind == DataElementKind.Bit || bit >= 0 ? 1 : RegisterCodec.WordCount(type);
        if (index < 0 || index + words > area.Capacity)
            throw new FormatException($"{type} at {area.FormatAddress(Math.Max(0, index))} runs past the end of {area.Name}.");
        _area = area;
        _index = index;
        _bit = bit;
        _order = order;
        DataType = type;
    }

    public TagDataType DataType { get; }

    public object Read()
    {
        lock (_area.Sync)
        {
            if (_area.Kind == DataElementKind.Bit || _bit >= 0) return _area.GetBitUnsafe(_index, Math.Max(0, _bit));
            Span<ushort> words = stackalloc ushort[RegisterCodec.WordCount(DataType)];
            for (int i = 0; i < words.Length; i++) words[i] = _area.GetUnsafe(_index + i);
            return RegisterCodec.Decode(words, DataType, _order);
        }
    }

    public void Write(object value)
    {
        var v = ValueConverter.Coerce(value, DataType);
        lock (_area.Sync)
        {
            if (_area.Kind == DataElementKind.Bit || _bit >= 0)
            {
                _area.SetBitUnsafe(_index, Math.Max(0, _bit), (bool)v);
                return;
            }
            var words = RegisterCodec.Encode(v, DataType, _order);
            for (int i = 0; i < words.Length; i++) _area.SetUnsafe(_index + i, words[i]);
        }
    }
}

/// <summary>
/// Byte-addressed memory (e.g. Siemens S7 inputs, outputs, flags and data blocks).
/// The UI sees it as big-endian 16-bit words: element n covers bytes 2n and 2n+1.
/// </summary>
public sealed class ByteArea : IDataArea
{
    private readonly byte[] _bytes;
    private readonly Func<int> _size;
    private readonly Func<int, string> _format;

    public ByteArea(object sync, string name, string shortName, int capacityBytes, bool remoteWritable,
        Func<int, string> formatByteOffset, Func<int>? sizeBytes = null)
    {
        Sync = sync;
        Name = name;
        ShortName = shortName;
        _bytes = new byte[capacityBytes + (capacityBytes & 1)];
        IsRemoteWritable = remoteWritable;
        _format = formatByteOffset;
        _size = sizeBytes ?? (() => capacityBytes);
    }

    public object Sync { get; }
    public string Name { get; }
    public string ShortName { get; }
    public DataElementKind Kind => DataElementKind.Word;
    public int CapacityBytes => _bytes.Length;
    public int SizeBytes => Math.Clamp(_size(), 0, _bytes.Length);
    public int Count => Math.Max(1, (SizeBytes + 1) / 2);
    public bool IsRemoteWritable { get; }

    public ushort Read(int index)
    {
        lock (Sync) return BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(index * 2));
    }

    public void Write(int index, ushort value)
    {
        lock (Sync) BinaryPrimitives.WriteUInt16BigEndian(_bytes.AsSpan(index * 2), value);
    }

    public string FormatAddress(int index) => _format(index * 2);

    public bool InRange(int offset, int length) => offset >= 0 && length >= 0 && offset + length <= SizeBytes;

    // Callers must hold Sync.
    public Span<byte> SpanUnsafe(int offset, int length) => _bytes.AsSpan(offset, length);

    public void ReadBytes(int offset, Span<byte> dest)
    {
        lock (Sync) _bytes.AsSpan(offset, dest.Length).CopyTo(dest);
    }

    public void WriteBytes(int offset, ReadOnlySpan<byte> src)
    {
        lock (Sync) src.CopyTo(_bytes.AsSpan(offset));
    }
}

/// <summary>Binds a tag to bytes of a <see cref="ByteArea"/> (big-endian), a bit, or an S7-style string.</summary>
public sealed class ByteAreaBinding : ITagBinding
{
    private readonly ByteArea _area;
    private readonly int _offset;
    private readonly int _bit;
    private readonly int _stringMax;

    /// <param name="bit">Bit (0-7) for Bool tags, or -1 for a whole byte.</param>
    /// <param name="stringMax">For String tags: S7 STRING maximum length (1-254).</param>
    public ByteAreaBinding(ByteArea area, int offset, int bit, TagDataType type, int stringMax = 254)
    {
        if (bit > 7) throw new FormatException("Bit numbers run from 0 to 7.");
        if (bit >= 0 && type != TagDataType.Bool) throw new FormatException("A bit address (byte.bit) must use data type Bool.");
        int size = type == TagDataType.String ? stringMax + 2 : bit >= 0 ? 1 : ByteCodec.Size(type);
        if (type == TagDataType.String && stringMax is < 1 or > 254) throw new FormatException("String length must be 1-254.");
        if (offset < 0 || offset + size > area.CapacityBytes)
            throw new FormatException($"{type} at byte {offset} runs past the end of {area.Name}.");
        _area = area;
        _offset = offset;
        _bit = bit;
        _stringMax = stringMax;
        DataType = type;
    }

    public TagDataType DataType { get; }

    public object Read()
    {
        lock (_area.Sync)
        {
            if (_bit >= 0) return ((_area.SpanUnsafe(_offset, 1)[0] >> _bit) & 1) != 0;
            if (DataType == TagDataType.String)
            {
                var s = _area.SpanUnsafe(_offset, _stringMax + 2);
                int len = Math.Min(s[1], Math.Min(s[0] == 0 ? _stringMax : s[0], _stringMax));
                return Encoding.Latin1.GetString(s.Slice(2, len));
            }
            return ByteCodec.Decode(_area.SpanUnsafe(_offset, ByteCodec.Size(DataType)), DataType, true);
        }
    }

    public void Write(object value)
    {
        var v = ValueConverter.Coerce(value, DataType);
        lock (_area.Sync)
        {
            if (_bit >= 0)
            {
                ref byte b = ref _area.SpanUnsafe(_offset, 1)[0];
                b = (bool)v ? (byte)(b | (1 << _bit)) : (byte)(b & ~(1 << _bit));
            }
            else if (DataType == TagDataType.String)
            {
                var s = _area.SpanUnsafe(_offset, _stringMax + 2);
                var text = Encoding.Latin1.GetBytes((string)v);
                int len = Math.Min(text.Length, _stringMax);
                s.Clear();
                s[0] = (byte)_stringMax;
                s[1] = (byte)len;
                text.AsSpan(0, len).CopyTo(s[2..]);
            }
            else
            {
                ByteCodec.Encode(v, DataType, _area.SpanUnsafe(_offset, ByteCodec.Size(DataType)), true);
            }
        }
    }
}
