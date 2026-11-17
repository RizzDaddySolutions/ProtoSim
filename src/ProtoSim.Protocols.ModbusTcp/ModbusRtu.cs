using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Modbus RTU framing helpers: CRC-16 and request length prediction.</summary>
public static class ModbusRtu
{
    /// <summary>Largest RTU ADU: address + 253-byte PDU + CRC.</summary>
    public const int MaxAduLength = 256;

    /// <summary>Smallest RTU ADU: address + function code + CRC.</summary>
    public const int MinAduLength = 4;

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>True when the last two bytes hold the CRC of the rest (low byte first).</summary>
    public static bool CheckCrc(ReadOnlySpan<byte> adu)
    {
        if (adu.Length < MinAduLength) return false;
        ushort crc = Crc16(adu[..^2]);
        return adu[^2] == (byte)crc && adu[^1] == (byte)(crc >> 8);
    }

    /// <summary>Builds unit + PDU + CRC.</summary>
    public static byte[] BuildAdu(byte unit, ReadOnlySpan<byte> pdu)
    {
        var adu = new byte[pdu.Length + 3];
        adu[0] = unit;
        pdu.CopyTo(adu.AsSpan(1));
        ushort crc = Crc16(adu.AsSpan(0, adu.Length - 2));
        adu[^2] = (byte)crc;
        adu[^1] = (byte)(crc >> 8);
        return adu;
    }

    /// <summary>
    /// Total length (including CRC) of the request that starts the buffer, worked out from its function code.
    /// 0 means more bytes are needed to tell; -1 means the function code is unknown, so only the
    /// silent interval can end the frame.
    /// </summary>
    public static int ExpectedRequestLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return 0;
        switch (buffer[1])
        {
            case 1: case 2: case 3: case 4: case 5: case 6: case 8:
                return 8;
            case 7: case 11: case 12: case 17:
                return 4;
            case 15: case 16:
                return buffer.Length < 7 ? 0 : 9 + buffer[6];
            case 22:
                return 10;
            case 23:
                return buffer.Length < 11 ? 0 : 13 + buffer[10];
            case 43:
                return 7;
            default:
                return -1;
        }
    }

    public static string Describe(ReadOnlySpan<byte> adu, bool isRequest)
    {
        if (adu.Length < MinAduLength) return $"Incomplete frame ({adu.Length} bytes)";
        string head = $"Unit {adu[0]} · ";
        if (!CheckCrc(adu)) return head + "CRC error";
        var pdu = adu[1..^2];
        try
        {
            return head + ModbusFrameFormatter.DescribePdu(pdu, isRequest);
        }
        catch (Exception)
        {
            return head + ModbusNames.Describe(pdu[0]) + " (malformed)";
        }
    }
}

/// <summary>
/// Collects received bytes into RTU frames. A frame ends when its predicted length has arrived,
/// or when the line goes quiet (<see cref="TakeOnSilence"/>).
/// </summary>
public sealed class RtuFrameAssembler
{
    private readonly List<byte> _buffer = new(ModbusRtu.MaxAduLength);

    public int Count => _buffer.Count;

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _buffer.Add(b);
    }

    /// <summary>A complete frame whose length is known from its function code, or null.</summary>
    public byte[]? TryTakeComplete()
    {
        int expected = ModbusRtu.ExpectedRequestLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer));
        if (expected > 0 && _buffer.Count >= expected) return Take(expected);
        return null;
    }

    /// <summary>Called after the silent interval: everything buffered is one frame (or garbage).</summary>
    public byte[]? TakeOnSilence() => _buffer.Count == 0 ? null : Take(_buffer.Count);

    /// <summary>True when the buffer can't be a valid frame any more and should be resynchronised.</summary>
    public bool Overflowed => _buffer.Count > ModbusRtu.MaxAduLength;

    public void Clear() => _buffer.Clear();

    private byte[] Take(int count)
    {
        var frame = _buffer.GetRange(0, count).ToArray();
        _buffer.RemoveRange(0, count);
        return frame;
    }
}

/// <summary>What a slave does with a received RTU frame.</summary>
public enum RtuDisposition
{
    /// <summary>Too short or wrong CRC: ignored, as a real slave would.</summary>
    Corrupt,
    /// <summary>Addressed to a unit this device doesn't serve.</summary>
    OtherUnit,
    /// <summary>Unit 0 with broadcast enabled: processed, never answered.</summary>
    Broadcast,
    /// <summary>Addressed to this device.</summary>
    ForThisDevice,
}

/// <summary>Transport-independent RTU slave logic shared by the serial and RTU-over-TCP servers.</summary>
public sealed class RtuSlave
{
    private readonly ModbusDeviceSettings _settings;
    private readonly ModbusRequestHandler _handler;
    private readonly bool[] _units;

    public RtuSlave(ModbusDeviceSettings settings, ModbusDataModel model)
    {
        _settings = settings;
        _handler = new ModbusRequestHandler(model, settings);
        _units = UnitFilter.Parse(settings.UnitIds);
    }

    public RtuDisposition Classify(byte[] adu)
    {
        if (!ModbusRtu.CheckCrc(adu)) return RtuDisposition.Corrupt;
        byte unit = adu[0];
        if (unit == 0 && _settings.TreatUnitZeroAsBroadcast) return RtuDisposition.Broadcast;
        return _units[unit] ? RtuDisposition.ForThisDevice : RtuDisposition.OtherUnit;
    }

    /// <summary>Runs the request. Returns null for broadcasts.</summary>
    public byte[]? Execute(byte[] adu, RtuDisposition disposition)
    {
        var response = _handler.Process(adu.AsSpan(1, adu.Length - 3));
        return disposition == RtuDisposition.Broadcast ? null : ModbusRtu.BuildAdu(adu[0], response);
    }

    /// <summary>The reply for a unit this device doesn't serve, per <see cref="ModbusDeviceSettings.UnmatchedUnitBehavior"/>; null to stay silent.</summary>
    public byte[]? OtherUnitReply(byte[] adu) => _settings.UnmatchedUnitBehavior switch
    {
        UnmatchedUnitBehavior.Ignore => null,
        UnmatchedUnitBehavior.GatewayPathUnavailable => Exception(adu, ModbusExceptionCode.GatewayPathUnavailable),
        _ => Exception(adu, ModbusExceptionCode.GatewayTargetFailedToRespond),
    };

    public static byte[] Exception(byte[] adu, ModbusExceptionCode code) =>
        ModbusRtu.BuildAdu(adu[0], ModbusRequestHandler.BuildException(adu[1], code));

    public static bool IsException(byte[] adu) => adu.Length > 1 && (adu[1] & 0x80) != 0;
}

/// <summary>Logs the outcome of a frame that won't be answered normally.</summary>
internal static class RtuLog
{
    public static void NotAnswered(ITrafficLog log, ServerStatistics stats, string remote, byte[] adu, RtuDisposition d)
    {
        switch (d)
        {
            case RtuDisposition.Corrupt:
                stats.OnMalformed();
                log.Error(remote, adu.Length < ModbusRtu.MinAduLength ? $"Frame too short ({adu.Length} bytes); ignored" : "CRC error; frame ignored");
                break;
            case RtuDisposition.OtherUnit:
                stats.OnDropped();
                log.Info(remote, $"Unit {adu[0]} is not served by this device; no response");
                break;
            case RtuDisposition.Broadcast:
                log.Info(remote, "Broadcast request processed; no response sent");
                break;
        }
    }
}
