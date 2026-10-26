using System.Globalization;

namespace ProtoSim.Core.Data;

/// <summary>Converts between user input, doubles and strongly typed tag values.</summary>
public static class ValueConverter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Converts any supported input (string, number, bool) to the CLR type for <paramref name="type"/>.</summary>
    public static object Coerce(object? value, TagDataType type)
    {
        if (type == TagDataType.String) return value is null ? string.Empty : Format(value);

        switch (value)
        {
            case null:
                return FromDouble(0, type);
            case string s:
                return Parse(s, type);
            case bool b:
                return type == TagDataType.Bool ? b : FromDouble(b ? 1 : 0, type);
            case float or double:
                return FromDouble(Convert.ToDouble(value, Inv), type);
        }

        if (type == TagDataType.Bool) return Convert.ToDecimal(value, Inv) != 0;
        if (type is TagDataType.Float32 or TagDataType.Float64) return FromDouble(Convert.ToDouble(value, Inv), type);

        decimal d = Convert.ToDecimal(value, Inv);
        return type switch
        {
            TagDataType.Int16 => (short)Math.Clamp(d, short.MinValue, short.MaxValue),
            TagDataType.UInt16 => (ushort)Math.Clamp(d, ushort.MinValue, ushort.MaxValue),
            TagDataType.Int32 => (int)Math.Clamp(d, int.MinValue, int.MaxValue),
            TagDataType.UInt32 => (uint)Math.Clamp(d, uint.MinValue, uint.MaxValue),
            TagDataType.Int64 => (long)Math.Clamp(d, long.MinValue, long.MaxValue),
            TagDataType.UInt64 => (ulong)Math.Clamp(d, ulong.MinValue, ulong.MaxValue),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    /// <summary>Parses text. Integers accept decimal or 0x-prefixed hex (hex is reinterpreted as raw bits).</summary>
    public static object Parse(string text, TagDataType type)
    {
        if (type == TagDataType.String) return text;
        var s = text.Trim();
        if (s.Length == 0) throw new FormatException("Value is empty.");

        if (type == TagDataType.Bool)
        {
            return s.ToLowerInvariant() switch
            {
                "1" or "true" or "on" or "yes" => true,
                "0" or "false" or "off" or "no" => false,
                _ => throw new FormatException($"'{s}' is not a boolean (use true/false, on/off or 1/0)."),
            };
        }

        if (type is TagDataType.Float32 or TagDataType.Float64)
        {
            if (!double.TryParse(s, NumberStyles.Float, Inv, out var f))
                throw new FormatException($"'{s}' is not a number.");
            return type == TagDataType.Float32 ? (float)f : f;
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, Inv, out var raw))
                throw new FormatException($"'{s}' is not a valid hex number.");
            ulong max = type switch
            {
                TagDataType.Int16 or TagDataType.UInt16 => ushort.MaxValue,
                TagDataType.Int32 or TagDataType.UInt32 => uint.MaxValue,
                _ => ulong.MaxValue,
            };
            if (raw > max) throw new FormatException($"'{s}' does not fit in {type}.");
            return type switch
            {
                TagDataType.Int16 => unchecked((short)raw),
                TagDataType.UInt16 => (ushort)raw,
                TagDataType.Int32 => unchecked((int)raw),
                TagDataType.UInt32 => (uint)raw,
                TagDataType.Int64 => unchecked((long)raw),
                _ => (object)raw,
            };
        }

        try
        {
            return type switch
            {
                TagDataType.Int16 => short.Parse(s, NumberStyles.Integer, Inv),
                TagDataType.UInt16 => ushort.Parse(s, NumberStyles.Integer, Inv),
                TagDataType.Int32 => int.Parse(s, NumberStyles.Integer, Inv),
                TagDataType.UInt32 => uint.Parse(s, NumberStyles.Integer, Inv),
                TagDataType.Int64 => long.Parse(s, NumberStyles.Integer, Inv),
                TagDataType.UInt64 => (object)ulong.Parse(s, NumberStyles.Integer, Inv),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }
        catch (OverflowException)
        {
            throw new FormatException($"'{s}' is out of range for {type}.");
        }
        catch (FormatException)
        {
            throw new FormatException($"'{s}' is not a valid {type}.");
        }
    }

    /// <summary>Converts a double to the type, rounding and clamping integers to their range.</summary>
    public static object FromDouble(double d, TagDataType type)
    {
        if (double.IsNaN(d)) d = 0;
        return type switch
        {
            TagDataType.Bool => d != 0,
            TagDataType.Int16 => (short)Math.Clamp(Math.Round(d), short.MinValue, short.MaxValue),
            TagDataType.UInt16 => (ushort)Math.Clamp(Math.Round(d), ushort.MinValue, ushort.MaxValue),
            TagDataType.Int32 => (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue),
            TagDataType.UInt32 => (uint)Math.Clamp(Math.Round(d), uint.MinValue, uint.MaxValue),
            TagDataType.Int64 => d >= 9.2233720368547758E18 ? long.MaxValue : d <= -9.2233720368547758E18 ? long.MinValue : (long)Math.Round(d),
            TagDataType.UInt64 => d <= 0 ? 0UL : d >= 1.8446744073709552E19 ? ulong.MaxValue : (ulong)Math.Round(d),
            TagDataType.Float32 => (float)d,
            TagDataType.Float64 => d,
            TagDataType.String => d.ToString("G15", Inv),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    public static double ToDouble(object? value) => value switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : 0,
        _ => Convert.ToDouble(value, Inv),
    };

    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        float f => f.ToString("G7", Inv),
        double d => d.ToString("G15", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => value.ToString() ?? string.Empty,
    };
}
