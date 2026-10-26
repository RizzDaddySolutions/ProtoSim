using System.Buffers.Binary;
using System.Text;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>
/// Executes a request PDU against a data model and returns the response PDU.
/// Transport-independent, so it can serve Modbus TCP, RTU-over-TCP or serial transports.
/// </summary>
public sealed class ModbusRequestHandler(ModbusDataModel model, ModbusServerSettings settings)
{
    public byte[] Process(ReadOnlySpan<byte> pdu)
    {
        if (pdu.IsEmpty) return BuildException(0, ModbusExceptionCode.IllegalFunction);
        byte fc = pdu[0];
        try
        {
            return (ModbusFunctionCode)fc switch
            {
                ModbusFunctionCode.ReadCoils => ReadBits(pdu, model.Coils),
                ModbusFunctionCode.ReadDiscreteInputs => ReadBits(pdu, model.DiscreteInputs),
                ModbusFunctionCode.ReadHoldingRegisters => ReadRegisters(pdu, model.HoldingRegisters),
                ModbusFunctionCode.ReadInputRegisters => ReadRegisters(pdu, model.InputRegisters),
                ModbusFunctionCode.WriteSingleCoil => WriteSingleCoil(pdu),
                ModbusFunctionCode.WriteSingleRegister => WriteSingleRegister(pdu),
                ModbusFunctionCode.Diagnostics => Diagnostics(pdu),
                ModbusFunctionCode.WriteMultipleCoils => WriteMultipleCoils(pdu),
                ModbusFunctionCode.WriteMultipleRegisters => WriteMultipleRegisters(pdu),
                ModbusFunctionCode.MaskWriteRegister => MaskWriteRegister(pdu),
                ModbusFunctionCode.ReadWriteMultipleRegisters => ReadWriteMultipleRegisters(pdu),
                ModbusFunctionCode.EncapsulatedInterface => EncapsulatedInterface(pdu),
                _ => throw new ModbusException(ModbusExceptionCode.IllegalFunction),
            };
        }
        catch (ModbusException ex)
        {
            return BuildException(fc, ex.Code);
        }
        catch (Exception)
        {
            return BuildException(fc, ModbusExceptionCode.ServerDeviceFailure);
        }
    }

    public static byte[] BuildException(byte functionCode, ModbusExceptionCode code) =>
        [(byte)(functionCode | 0x80), (byte)code];

    private static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16BigEndian(s[offset..]);

    private static void Require(ReadOnlySpan<byte> pdu, int length)
    {
        if (pdu.Length < length) throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
    }

    private static void CheckQuantity(int qty, int max)
    {
        if (qty < 1 || qty > max) throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
    }

    private static void CheckRange(ModbusDataArea area, int start, int qty)
    {
        if (start + qty > area.Count) throw new ModbusException(ModbusExceptionCode.IllegalDataAddress);
    }

    private void CheckWritable()
    {
        if (!settings.AllowWrites) throw new ModbusException(ModbusExceptionCode.IllegalFunction);
    }

    private byte[] ReadBits(ReadOnlySpan<byte> pdu, ModbusDataArea area)
    {
        Require(pdu, 5);
        int start = U16(pdu, 1), qty = U16(pdu, 3);
        CheckQuantity(qty, ModbusLimits.MaxReadBits);
        CheckRange(area, start, qty);

        int byteCount = (qty + 7) / 8;
        var resp = new byte[2 + byteCount];
        resp[0] = pdu[0];
        resp[1] = (byte)byteCount;
        lock (model.Sync)
        {
            for (int i = 0; i < qty; i++)
                if (area.GetUnsafe(start + i) != 0) resp[2 + i / 8] |= (byte)(1 << (i % 8));
        }
        return resp;
    }

    private byte[] ReadRegisters(ReadOnlySpan<byte> pdu, ModbusDataArea area)
    {
        Require(pdu, 5);
        int start = U16(pdu, 1), qty = U16(pdu, 3);
        CheckQuantity(qty, ModbusLimits.MaxReadRegisters);
        CheckRange(area, start, qty);
        return BuildRegisterResponse(pdu[0], area, start, qty);
    }

    private byte[] BuildRegisterResponse(byte fc, ModbusDataArea area, int start, int qty)
    {
        var resp = new byte[2 + qty * 2];
        resp[0] = fc;
        resp[1] = (byte)(qty * 2);
        lock (model.Sync)
        {
            for (int i = 0; i < qty; i++)
                BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2 + i * 2), area.GetUnsafe(start + i));
        }
        return resp;
    }

    private byte[] WriteSingleCoil(ReadOnlySpan<byte> pdu)
    {
        CheckWritable();
        Require(pdu, 5);
        int addr = U16(pdu, 1), value = U16(pdu, 3);
        if (value != 0xFF00 && value != 0x0000) throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
        CheckRange(model.Coils, addr, 1);
        model.Coils.Write(addr, value == 0xFF00 ? (ushort)1 : (ushort)0);
        return pdu[..5].ToArray();
    }

    private byte[] WriteSingleRegister(ReadOnlySpan<byte> pdu)
    {
        CheckWritable();
        Require(pdu, 5);
        int addr = U16(pdu, 1);
        CheckRange(model.HoldingRegisters, addr, 1);
        model.HoldingRegisters.Write(addr, U16(pdu, 3));
        return pdu[..5].ToArray();
    }

    private byte[] WriteMultipleCoils(ReadOnlySpan<byte> pdu)
    {
        CheckWritable();
        Require(pdu, 6);
        int start = U16(pdu, 1), qty = U16(pdu, 3), byteCount = pdu[5];
        CheckQuantity(qty, ModbusLimits.MaxWriteBits);
        if (byteCount != (qty + 7) / 8 || pdu.Length < 6 + byteCount) throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
        CheckRange(model.Coils, start, qty);

        lock (model.Sync)
        {
            for (int i = 0; i < qty; i++)
                model.Coils.SetUnsafe(start + i, (ushort)((pdu[6 + i / 8] >> (i % 8)) & 1));
        }
        return pdu[..5].ToArray();
    }

    private byte[] WriteMultipleRegisters(ReadOnlySpan<byte> pdu)
    {
        CheckWritable();
        Require(pdu, 6);
        int start = U16(pdu, 1), qty = U16(pdu, 3), byteCount = pdu[5];
        CheckQuantity(qty, ModbusLimits.MaxWriteRegisters);
        if (byteCount != qty * 2 || pdu.Length < 6 + byteCount) throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
        CheckRange(model.HoldingRegisters, start, qty);

        lock (model.Sync)
        {
            for (int i = 0; i < qty; i++)
                model.HoldingRegisters.SetUnsafe(start + i, U16(pdu, 6 + i * 2));
        }
        return pdu[..5].ToArray();
    }

    private byte[] MaskWriteRegister(ReadOnlySpan<byte> pdu)
    {
        CheckWritable();
        Require(pdu, 7);
        int addr = U16(pdu, 1);
        ushort and = U16(pdu, 3), or = U16(pdu, 5);
        CheckRange(model.HoldingRegisters, addr, 1);
        lock (model.Sync)
        {
            ushort cur = model.HoldingRegisters.GetUnsafe(addr);
            model.HoldingRegisters.SetUnsafe(addr, (ushort)((cur & and) | (or & ~and)));
        }
        return pdu[..7].ToArray();
    }

    private byte[] ReadWriteMultipleRegisters(ReadOnlySpan<byte> pdu)
    {
        CheckWritable();
        Require(pdu, 10);
        int readStart = U16(pdu, 1), readQty = U16(pdu, 3);
        int writeStart = U16(pdu, 5), writeQty = U16(pdu, 7), byteCount = pdu[9];
        CheckQuantity(readQty, ModbusLimits.MaxReadRegisters);
        CheckQuantity(writeQty, ModbusLimits.MaxReadWriteWriteRegisters);
        if (byteCount != writeQty * 2 || pdu.Length < 10 + byteCount) throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
        CheckRange(model.HoldingRegisters, readStart, readQty);
        CheckRange(model.HoldingRegisters, writeStart, writeQty);

        lock (model.Sync)
        {
            // The spec says the write is performed before the read.
            for (int i = 0; i < writeQty; i++)
                model.HoldingRegisters.SetUnsafe(writeStart + i, U16(pdu, 10 + i * 2));
            return BuildRegisterResponse(pdu[0], model.HoldingRegisters, readStart, readQty);
        }
    }

    private static byte[] Diagnostics(ReadOnlySpan<byte> pdu)
    {
        Require(pdu, 3);
        int sub = U16(pdu, 1);
        if (sub != 0x0000) throw new ModbusException(ModbusExceptionCode.IllegalFunction);
        return pdu.ToArray(); // Return Query Data: echo
    }

    private byte[] EncapsulatedInterface(ReadOnlySpan<byte> pdu)
    {
        Require(pdu, 2);
        if (pdu[1] != 0x0E) throw new ModbusException(ModbusExceptionCode.IllegalFunction);
        Require(pdu, 4);
        byte code = pdu[2], objectId = pdu[3];

        string[] objects =
        [
            settings.VendorName, settings.ProductCode, settings.Revision,
            settings.VendorUrl, settings.ProductName, settings.ModelName, settings.UserApplicationName,
        ];

        int first, last;
        switch (code)
        {
            case 0x01: first = 0; last = 2; break;
            case 0x02:
            case 0x03: first = 0; last = 6; break; // no extended objects, so extended = regular
            case 0x04:
                if (objectId > 6) throw new ModbusException(ModbusExceptionCode.IllegalDataAddress);
                first = last = objectId;
                break;
            default: throw new ModbusException(ModbusExceptionCode.IllegalDataValue);
        }
        if (code != 0x04 && objectId >= first && objectId <= last) first = objectId;

        var resp = new List<byte>(ModbusLimits.MaxPduLength) { 0x2B, 0x0E, code, 0x82, 0x00, 0x00, 0x00 };
        byte count = 0;
        for (int id = first; id <= last; id++)
        {
            var bytes = Encoding.ASCII.GetBytes(objects[id] ?? string.Empty);
            if (bytes.Length > 240) bytes = bytes[..240];
            if (resp.Count + 2 + bytes.Length > ModbusLimits.MaxPduLength)
            {
                resp[4] = 0xFF;          // more follows
                resp[5] = (byte)id;      // next object id
                break;
            }
            resp.Add((byte)id);
            resp.Add((byte)bytes.Length);
            resp.AddRange(bytes);
            count++;
        }
        resp[6] = count;
        return resp.ToArray();
    }
}
