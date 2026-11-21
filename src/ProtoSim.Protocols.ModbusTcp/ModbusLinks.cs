using System.Globalization;
using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>
/// Network link support shared by the Modbus plugins. Link addresses are tag addresses (HR:0, 40001, CO:5, HR:10.3),
/// optionally prefixed with a unit ID: "U2:HR:0". Contiguous points of the same area and type are read with one request.
/// </summary>
internal static class ModbusLinks
{
    private readonly record struct Parsed(int Unit, ModbusAddress Address, TagDataType Type, WordOrder Order)
    {
        public int Words => Address.IsBitArea || Address.Bit >= 0 ? 1 : RegisterCodec.WordCount(Type);
        public bool Bitwise => Address.Bit >= 0;
    }

    /// <summary>Tag address as a link address; adds the unit when the device answers on one specific unit other than 1.</summary>
    public static string SourceAddress(object serverSettings, string tagAddress)
    {
        var address = tagAddress.Trim();
        if (serverSettings is not ModbusDeviceSettings s) return address;
        var first = (s.UnitIds ?? "*").Split([',', ';', ' ', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first != null && first != "*" && int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var unit) && unit != 1
            ? $"U{unit}:{address}"
            : address;
    }

    private static Parsed Parse(LinkPoint p)
    {
        var text = p.Address.Trim();
        int unit = 1;
        if (text.Length > 1 && (text[0] is 'U' or 'u') && text.IndexOf(':') is var colon and > 1 &&
            int.TryParse(text.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var u))
        {
            if (u > 255) throw new FormatException("Unit ID must be 0-255.");
            unit = u;
            text = text[(colon + 1)..];
        }
        var a = ModbusAddress.Parse(text);
        var type = p.DataType;
        if (type == TagDataType.String) throw new FormatException("Modbus points cannot use the String type.");
        if ((a.IsBitArea || a.Bit >= 0) && type != TagDataType.Bool) throw new FormatException($"{p.Address} is a bit; use the Bool type.");
        return new Parsed(unit, a, type, p.WordOrder);
    }

    /// <summary>Bools go to the next free coil, other types to the first free holding registers after those in use.</summary>
    public static string SuggestAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type)
    {
        int nextCoil = 0, nextRegister = 0;
        foreach (var (address, t) in existing)
        {
            if (!ModbusAddress.TryParse(address, out var a, out _)) continue;
            if (a.Area == ModbusAreaKind.Coils) nextCoil = Math.Max(nextCoil, a.Index + 1);
            else if (a.Area == ModbusAreaKind.HoldingRegisters)
                nextRegister = Math.Max(nextRegister, a.Index + (a.Bit >= 0 || t is TagDataType.Bool or TagDataType.String ? 1 : RegisterCodec.WordCount(t)));
        }
        return type == TagDataType.Bool ? $"CO:{nextCoil}" : $"HR:{nextRegister}";
    }

    public static IReadOnlyList<LinkRead> CreateReads(IReadOnlyList<LinkPoint> points)
    {
        var parsed = points.Select(Parse).ToList();
        var reads = new List<LinkRead>();

        // Bits inside registers: one register read each, then pick the bit.
        foreach (var (p, i) in parsed.Select((p, i) => (p, i)).Where(x => x.p.Bitwise))
        {
            int bit = p.Address.Bit;
            reads.Add(new LinkRead(Request(p, p.Address.Index, 1, TagDataType.UInt16), [i])
            {
                Value = (_, row) => ((Convert.ToInt32(row.Typed ?? 0, CultureInfo.InvariantCulture) >> bit) & 1) != 0,
            });
        }

        // Runs of contiguous values with the same unit, area, type and word order.
        var whole = parsed.Select((p, i) => (p, i)).Where(x => !x.p.Bitwise)
            .OrderBy(x => x.p.Unit).ThenBy(x => x.p.Address.Area).ThenBy(x => x.p.Type).ThenBy(x => x.p.Order).ThenBy(x => x.p.Address.Index)
            .ToList();
        int max(Parsed p) => p.Address.IsBitArea ? ModbusLimits.MaxReadBits : ModbusLimits.MaxReadRegisters;
        for (int s = 0; s < whole.Count;)
        {
            var first = whole[s].p;
            var run = new List<int> { whole[s].i };
            int next = first.Address.Index + first.Words;
            int e = s + 1;
            while (e < whole.Count)
            {
                var q = whole[e].p;
                if (q.Unit != first.Unit || q.Address.Area != first.Address.Area || q.Type != first.Type || q.Order != first.Order) break;
                if (q.Address.Index != next || next + q.Words - first.Address.Index > max(first)) break;
                run.Add(whole[e].i);
                next += q.Words;
                e++;
            }
            var bool16 = first.Type == TagDataType.Bool && !first.Address.IsBitArea;
            reads.Add(new LinkRead(Request(first, first.Address.Index, run.Count, bool16 ? TagDataType.UInt16 : first.Type), run)
            {
                Value = bool16 ? (_, row) => Convert.ToInt32(row.Typed ?? 0, CultureInfo.InvariantCulture) != 0 : null,
            });
            s = e;
        }
        return reads;
    }

    private static ModbusClientRequest Request(Parsed p, int start, int quantity, TagDataType type) => new()
    {
        Function = p.Address.Area switch
        {
            ModbusAreaKind.Coils => ModbusClientFunction.ReadCoils,
            ModbusAreaKind.DiscreteInputs => ModbusClientFunction.ReadDiscreteInputs,
            ModbusAreaKind.InputRegisters => ModbusClientFunction.ReadInputRegisters,
            _ => ModbusClientFunction.ReadHoldingRegisters,
        },
        UnitId = p.Unit,
        Address = start,
        Quantity = quantity,
        DataType = type,
        WordOrder = p.Order,
    };

    public static object CreateWrite(LinkPoint point, object value)
    {
        var p = Parse(point);
        var a = p.Address;
        if (a.Area is ModbusAreaKind.DiscreteInputs or ModbusAreaKind.InputRegisters)
            throw new NotSupportedException($"{point.Address} is in a read-only area.");

        if (a.Area == ModbusAreaKind.Coils)
        {
            bool on = (bool)ValueConverter.Coerce(value, TagDataType.Bool);
            return new ModbusClientRequest { Function = ModbusClientFunction.WriteSingleCoil, UnitId = p.Unit, Address = a.Index, Values = on ? "1" : "0" };
        }
        if (a.Bit >= 0)
        {
            bool on = (bool)ValueConverter.Coerce(value, TagDataType.Bool);
            ushort mask = (ushort)(1 << a.Bit);
            return new ModbusClientRequest
            {
                Function = ModbusClientFunction.MaskWriteRegister, UnitId = p.Unit, Address = a.Index,
                Values = string.Create(CultureInfo.InvariantCulture, $"0x{(ushort)~mask:X4},0x{(on ? mask : 0):X4}"),
            };
        }
        var type = p.Type == TagDataType.Bool ? TagDataType.UInt16 : p.Type;
        var coerced = p.Type == TagDataType.Bool ? ((bool)ValueConverter.Coerce(value, TagDataType.Bool) ? 1 : 0) : ValueConverter.Coerce(value, type);
        return new ModbusClientRequest
        {
            Function = ModbusClientFunction.WriteMultipleRegisters, UnitId = p.Unit, Address = a.Index,
            DataType = type, WordOrder = p.Order, Values = ValueConverter.Format(coerced),
        };
    }
}
