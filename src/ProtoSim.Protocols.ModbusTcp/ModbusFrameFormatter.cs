using System.Buffers.Binary;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Produces one-line, human readable descriptions of Modbus TCP frames for the traffic log.</summary>
public static class ModbusFrameFormatter
{
    public static string Describe(ReadOnlySpan<byte> adu, bool isRequest)
    {
        if (adu.Length < 8) return "Incomplete frame";
        ushort tid = BinaryPrimitives.ReadUInt16BigEndian(adu);
        byte unit = adu[6];
        var pdu = adu[7..];
        string head = $"TID {tid} · Unit {unit} · ";
        try
        {
            return head + DescribePdu(pdu, isRequest);
        }
        catch (Exception)
        {
            return head + ModbusNames.Describe(pdu[0]) + " (malformed)";
        }
    }

    public static string DescribePdu(ReadOnlySpan<byte> pdu, bool isRequest)
    {
        byte fc = pdu[0];
        if ((fc & 0x80) != 0)
        {
            var code = (ModbusExceptionCode)pdu[1];
            return $"EXCEPTION {ModbusNames.Describe((byte)(fc & 0x7F))}: {(byte)code:X2} {ModbusNames.Describe(code)}";
        }

        string name = ModbusNames.Describe(fc);

        switch ((ModbusFunctionCode)fc)
        {
            case ModbusFunctionCode.ReadCoils:
            case ModbusFunctionCode.ReadDiscreteInputs:
                return isRequest ? $"{name} addr {R(pdu, 1)} qty {R(pdu, 3)}" : $"{name} → {pdu[1]} bytes";

            case ModbusFunctionCode.ReadHoldingRegisters:
            case ModbusFunctionCode.ReadInputRegisters:
            case ModbusFunctionCode.ReadWriteMultipleRegisters when !isRequest:
                if (isRequest) return $"{name} addr {R(pdu, 1)} qty {R(pdu, 3)}";
                return $"{name} → {RegisterPreview(pdu[2..(2 + pdu[1])])}";

            case ModbusFunctionCode.ReadWriteMultipleRegisters:
                return $"{name} read {R(pdu, 1)}×{R(pdu, 3)} write {R(pdu, 5)}×{R(pdu, 7)}";

            case ModbusFunctionCode.WriteSingleCoil:
                return $"{name} addr {R(pdu, 1)} = {(R(pdu, 3) == 0xFF00 ? "ON" : R(pdu, 3) == 0 ? "OFF" : $"0x{R(pdu, 3):X4}")}";

            case ModbusFunctionCode.WriteSingleRegister:
                return $"{name} addr {R(pdu, 1)} = {R(pdu, 3)}";

            case ModbusFunctionCode.WriteMultipleCoils:
            case ModbusFunctionCode.WriteMultipleRegisters:
                return $"{name} addr {R(pdu, 1)} qty {R(pdu, 3)}";

            case ModbusFunctionCode.MaskWriteRegister:
                return $"{name} addr {R(pdu, 1)} AND 0x{R(pdu, 3):X4} OR 0x{R(pdu, 5):X4}";

            case ModbusFunctionCode.Diagnostics:
                return $"{name} sub-function 0x{R(pdu, 1):X4}";

            case ModbusFunctionCode.EncapsulatedInterface:
                return isRequest
                    ? $"{name} code {pdu[2]} object {pdu[3]}"
                    : $"{name} code {pdu[2]} · {pdu[6]} objects{(pdu[4] == 0xFF ? " (more follow)" : "")}";

            default:
                return $"{name} ({pdu.Length} bytes)";
        }
    }

    private static ushort R(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadUInt16BigEndian(p[offset..]);

    private static string RegisterPreview(ReadOnlySpan<byte> data)
    {
        int n = data.Length / 2;
        const int max = 8;
        var values = new List<string>(Math.Min(n, max));
        for (int i = 0; i < Math.Min(n, max); i++)
            values.Add(BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]).ToString());
        return $"[{string.Join(", ", values)}{(n > max ? $", … +{n - max}" : "")}]";
    }
}
