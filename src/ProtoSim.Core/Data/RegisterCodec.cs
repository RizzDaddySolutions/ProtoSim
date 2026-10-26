using System.Buffers.Binary;

namespace ProtoSim.Core.Data;

/// <summary>Encodes and decodes typed values to/from 16-bit registers with a configurable word order.</summary>
public static class RegisterCodec
{
    public static int WordCount(TagDataType type) => type switch
    {
        TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.String => throw new NotSupportedException("String values cannot be stored in registers."),
        _ => 4,
    };

    public static ushort[] Encode(object value, TagDataType type, WordOrder order)
    {
        var v = ValueConverter.Coerce(value, type);
        var bytes = new byte[WordCount(type) * 2];
        switch (type)
        {
            case TagDataType.Bool: BinaryPrimitives.WriteUInt16BigEndian(bytes, (bool)v ? (ushort)1 : (ushort)0); break;
            case TagDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(bytes, (short)v); break;
            case TagDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)v); break;
            case TagDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(bytes, (int)v); break;
            case TagDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)v); break;
            case TagDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(bytes, (long)v); break;
            case TagDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)v); break;
            case TagDataType.Float32: BinaryPrimitives.WriteSingleBigEndian(bytes, (float)v); break;
            case TagDataType.Float64: BinaryPrimitives.WriteDoubleBigEndian(bytes, (double)v); break;
        }
        return BytesToWords(bytes, order);
    }

    public static object Decode(ReadOnlySpan<ushort> words, TagDataType type, WordOrder order)
    {
        int n = WordCount(type);
        if (words.Length < n) throw new ArgumentException($"{type} needs {n} registers, got {words.Length}.", nameof(words));
        var b = WordsToBytes(words[..n], order);
        return type switch
        {
            TagDataType.Bool => BinaryPrimitives.ReadUInt16BigEndian(b) != 0,
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(b),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(b),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(b),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(b),
            TagDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(b),
            TagDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(b),
            TagDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(b),
            TagDataType.Float64 => (object)BinaryPrimitives.ReadDoubleBigEndian(b),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static bool ReversesWords(WordOrder o) => o is WordOrder.CDAB or WordOrder.DCBA;
    private static bool SwapsBytes(WordOrder o) => o is WordOrder.BADC or WordOrder.DCBA;

    private static ushort[] BytesToWords(byte[] bigEndian, WordOrder order)
    {
        int n = bigEndian.Length / 2;
        var words = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            int src = ReversesWords(order) ? n - 1 - i : i;
            byte hi = bigEndian[src * 2], lo = bigEndian[src * 2 + 1];
            if (SwapsBytes(order)) (hi, lo) = (lo, hi);
            words[i] = (ushort)((hi << 8) | lo);
        }
        return words;
    }

    private static byte[] WordsToBytes(ReadOnlySpan<ushort> words, WordOrder order)
    {
        int n = words.Length;
        var be = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            int dst = ReversesWords(order) ? n - 1 - i : i;
            byte hi = (byte)(words[i] >> 8), lo = (byte)words[i];
            if (SwapsBytes(order)) (hi, lo) = (lo, hi);
            be[dst * 2] = hi;
            be[dst * 2 + 1] = lo;
        }
        return be;
    }
}
