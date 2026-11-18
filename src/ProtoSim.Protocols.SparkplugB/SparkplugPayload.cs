using System.Buffers.Binary;
using System.Text;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.SparkplugB;

/// <summary>Sparkplug B metric data types (subset used by ProtoSim).</summary>
public enum SparkplugDataType : uint
{
    Unknown = 0,
    Int8 = 1,
    Int16 = 2,
    Int32 = 3,
    Int64 = 4,
    UInt8 = 5,
    UInt16 = 6,
    UInt32 = 7,
    UInt64 = 8,
    Float = 9,
    Double = 10,
    Boolean = 11,
    String = 12,
    DateTime = 13,
    Text = 14,
}

public sealed class SparkplugMetric
{
    public string? Name { get; set; }
    public ulong? Alias { get; set; }
    public ulong? Timestamp { get; set; }
    public SparkplugDataType DataType { get; set; }
    public bool IsNull { get; set; }
    /// <summary>bool, uint (int_value), ulong (long_value), float, double or string.</summary>
    public object? Value { get; set; }

    public static SparkplugDataType FromTagType(TagDataType t) => t switch
    {
        TagDataType.Bool => SparkplugDataType.Boolean,
        TagDataType.Int16 => SparkplugDataType.Int16,
        TagDataType.UInt16 => SparkplugDataType.UInt16,
        TagDataType.Int32 => SparkplugDataType.Int32,
        TagDataType.UInt32 => SparkplugDataType.UInt32,
        TagDataType.Int64 => SparkplugDataType.Int64,
        TagDataType.UInt64 => SparkplugDataType.UInt64,
        TagDataType.Float32 => SparkplugDataType.Float,
        TagDataType.Float64 => SparkplugDataType.Double,
        _ => SparkplugDataType.String,
    };

    /// <summary>Builds a metric carrying a tag value in the wire representation for its data type.</summary>
    public static SparkplugMetric FromTag(string? name, ulong? alias, TagDataType type, object value, DateTime timestampUtc) => new()
    {
        Name = name,
        Alias = alias,
        Timestamp = ToEpochMs(timestampUtc),
        DataType = FromTagType(type),
        Value = type switch
        {
            TagDataType.Bool => (bool)value,
            TagDataType.Int16 => unchecked((uint)(int)(short)value),
            TagDataType.UInt16 => (uint)(ushort)value,
            TagDataType.Int32 => unchecked((uint)(int)value),
            TagDataType.UInt32 => (uint)value,
            TagDataType.Int64 => unchecked((ulong)(long)value),
            TagDataType.UInt64 => (ulong)value,
            TagDataType.Float32 => (float)value,
            TagDataType.Float64 => (double)value,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        },
    };

    /// <summary>The metric value converted to a CLR value suitable for <see cref="ValueConverter.Coerce"/>.</summary>
    public object? ToClrValue() => (DataType, Value) switch
    {
        (_, null) => null,
        (SparkplugDataType.Int8, uint u) => (sbyte)unchecked((byte)u),
        (SparkplugDataType.Int16, uint u) => unchecked((short)u),
        (SparkplugDataType.Int32, uint u) => unchecked((int)u),
        (SparkplugDataType.Int64, ulong u) => unchecked((long)u),
        (_, var v) => v,
    };

    public static ulong ToEpochMs(DateTime utc) => (ulong)Math.Max(0, new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
}

public sealed class SparkplugPayload
{
    public ulong? Timestamp { get; set; }
    public List<SparkplugMetric> Metrics { get; } = [];
    public ulong? Seq { get; set; }
    public string? Uuid { get; set; }

    // ---- Encoding (protobuf, Sparkplug B schema field numbers) ----

    public byte[] Encode()
    {
        var w = new ProtoWriter();
        if (Timestamp is { } ts) w.Varint(1, ts);
        foreach (var m in Metrics) w.Bytes(2, EncodeMetric(m));
        if (Seq is { } seq) w.Varint(3, seq);
        if (Uuid != null) w.String(4, Uuid);
        return w.ToArray();
    }

    private static byte[] EncodeMetric(SparkplugMetric m)
    {
        var w = new ProtoWriter();
        if (m.Name != null) w.String(1, m.Name);
        if (m.Alias is { } alias) w.Varint(2, alias);
        if (m.Timestamp is { } ts) w.Varint(3, ts);
        if (m.DataType != SparkplugDataType.Unknown) w.Varint(4, (uint)m.DataType);
        if (m.IsNull || m.Value == null)
        {
            if (m.IsNull) w.Varint(7, 1);
            return w.ToArray();
        }
        switch (m.Value)
        {
            case uint u: w.Varint(10, u); break;
            case ulong ul: w.Varint(11, ul); break;
            case float f: w.Fixed32(12, BitConverter.SingleToUInt32Bits(f)); break;
            case double d: w.Fixed64(13, BitConverter.DoubleToUInt64Bits(d)); break;
            case bool b: w.Varint(14, b ? 1UL : 0UL); break;
            case string s: w.String(15, s); break;
            default: throw new NotSupportedException($"Metric value type {m.Value.GetType().Name} is not supported.");
        }
        return w.ToArray();
    }

    // ---- Decoding ----

    /// <summary>Decodes a payload. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static SparkplugPayload Decode(ReadOnlySpan<byte> data)
    {
        var p = new SparkplugPayload();
        var r = new ProtoReader(data);
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: p.Timestamp = r.ReadVarint(); break;
                case 2 when wire == 2: p.Metrics.Add(DecodeMetric(r.ReadBytes())); break;
                case 3 when wire == 0: p.Seq = r.ReadVarint(); break;
                case 4 when wire == 2: p.Uuid = Encoding.UTF8.GetString(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return p;
    }

    private static SparkplugMetric DecodeMetric(ReadOnlySpan<byte> data)
    {
        var m = new SparkplugMetric();
        var r = new ProtoReader(data);
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2: m.Name = Encoding.UTF8.GetString(r.ReadBytes()); break;
                case 2 when wire == 0: m.Alias = r.ReadVarint(); break;
                case 3 when wire == 0: m.Timestamp = r.ReadVarint(); break;
                case 4 when wire == 0: m.DataType = (SparkplugDataType)(uint)r.ReadVarint(); break;
                case 7 when wire == 0: m.IsNull = r.ReadVarint() != 0; break;
                case 10 when wire == 0: m.Value = (uint)r.ReadVarint(); break;
                case 11 when wire == 0: m.Value = r.ReadVarint(); break;
                case 12 when wire == 5: m.Value = BitConverter.UInt32BitsToSingle(r.ReadFixed32()); break;
                case 13 when wire == 1: m.Value = BitConverter.UInt64BitsToDouble(r.ReadFixed64()); break;
                case 14 when wire == 0: m.Value = r.ReadVarint() != 0; break;
                case 15 when wire == 2: m.Value = Encoding.UTF8.GetString(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return m;
    }

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _ms = new();

        public void Varint(int field, ulong value) { Tag(field, 0); Raw(value); }
        public void Fixed32(int field, uint value) { Tag(field, 5); Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _ms.Write(b); }
        public void Fixed64(int field, ulong value) { Tag(field, 1); Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); _ms.Write(b); }
        public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
        public void Bytes(int field, byte[] value) { Tag(field, 2); Raw((ulong)value.Length); _ms.Write(value); }
        public byte[] ToArray() => _ms.ToArray();

        private void Tag(int field, int wire) => Raw((ulong)((field << 3) | wire));

        private void Raw(ulong v)
        {
            while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
            _ms.WriteByte((byte)v);
        }
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public bool Next(out int field, out int wire)
        {
            field = wire = 0;
            if (_pos >= _data.Length) return false;
            ulong tag = ReadVarint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            if (field == 0) throw new FormatException("Invalid protobuf field number 0.");
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_pos >= _data.Length) throw new FormatException("Truncated varint.");
                byte b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("Varint too long.");
        }

        public uint ReadFixed32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]); _pos += 4; return v; }
        public ulong ReadFixed64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]); _pos += 8; return v; }

        public ReadOnlySpan<byte> ReadBytes()
        {
            ulong len = ReadVarint();
            if (len > (ulong)(_data.Length - _pos)) throw new FormatException("Truncated length-delimited field.");
            var s = _data.Slice(_pos, (int)len);
            _pos += (int)len;
            return s;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(); break;
                case 1: Need(8); _pos += 8; break;
                case 2: ReadBytes(); break;
                case 5: Need(4); _pos += 4; break;
                default: throw new FormatException($"Unsupported protobuf wire type {wire}.");
            }
        }

        private readonly void Need(int n)
        {
            if (_data.Length - _pos < n) throw new FormatException("Truncated fixed-size field.");
        }
    }
}
