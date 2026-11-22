using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

// ---------------------------------------------------------------- Settings and requests

/// <summary>Timing shared by every Modbus client transport.</summary>
public abstract class ModbusClientTiming
{
    [Display(Name = "Response timeout (ms)", GroupName = "Timing", Order = 20)]
    [Range(10, 60_000)]
    public int ResponseTimeoutMs { get; set; } = 1000;

    [Display(Name = "Retries", GroupName = "Timing", Order = 21, Description = "Extra attempts after a timeout or connection error. The connection is re-opened before each retry.")]
    [Range(0, 10)]
    public int Retries { get; set; }
}

public class ModbusClientSettings : ModbusClientTiming
{
    [Display(Name = "Host", GroupName = "Connection", Order = 1, Description = "Host name or IP address of the Modbus server.")]
    public string Host { get; set; } = "127.0.0.1";

    [Display(Name = "TCP port", GroupName = "Connection", Order = 2)]
    [Range(1, 65535)]
    public int Port { get; set; } = 502;

    [Display(Name = "Connect timeout (ms)", GroupName = "Connection", Order = 3)]
    [Range(100, 60_000)]
    public int ConnectTimeoutMs { get; set; } = 3000;
}

/// <summary>RTU frames over TCP, e.g. to a serial device server.</summary>
public sealed class ModbusRtuOverTcpClientSettings : ModbusClientSettings;

public sealed class ModbusRtuSerialClientSettings : ModbusClientTiming
{
    [Display(Name = "Serial port", GroupName = "Serial", Order = 1, Description = SerialPorts.Help)]
    public string PortName { get; set; } = SerialPorts.DefaultName;

    [Display(Name = "Baud rate", GroupName = "Serial", Order = 2)]
    [Range(300, 921_600)]
    public int BaudRate { get; set; } = 19200;

    [Display(Name = "Data bits", GroupName = "Serial", Order = 3)]
    [Range(7, 8)]
    public int DataBits { get; set; } = 8;

    [Display(Name = "Parity", GroupName = "Serial", Order = 4)]
    public Parity Parity { get; set; } = Parity.Even;

    [Display(Name = "Stop bits", GroupName = "Serial", Order = 5)]
    public StopBits StopBits { get; set; } = StopBits.One;

    [Display(Name = "Frame gap (ms)", GroupName = "Serial", Order = 6, Description = "Silence that ends a response whose length can't be predicted. 0 = automatic.")]
    [Range(0, 1000)]
    public int FrameGapMs { get; set; }

    internal string Describe() => $"{PortName} {BaudRate} {DataBits}{Parity.ToString()[0]}{(StopBits == StopBits.Two ? 2 : StopBits == StopBits.OnePointFive ? 1.5 : 1)}";

    internal int EffectiveGapMs()
    {
        if (FrameGapMs > 0) return FrameGapMs;
        double charBits = 1 + DataBits + (Parity == Parity.None ? 0 : 1) + (StopBits == StopBits.Two ? 2 : 1);
        double t35 = BaudRate > 19200 ? 1.75 : 3.5 * charBits * 1000 / BaudRate;
        return Math.Max(20, (int)Math.Ceiling(t35) * 4);
    }
}

public enum ModbusClientFunction
{
    ReadCoils,
    ReadDiscreteInputs,
    ReadHoldingRegisters,
    ReadInputRegisters,
    WriteSingleCoil,
    WriteSingleRegister,
    WriteMultipleCoils,
    WriteMultipleRegisters,
    MaskWriteRegister,
    ReadWriteMultipleRegisters,
    ReadDeviceIdentification,
}

public sealed class ModbusClientRequest
{
    [Display(Name = "Function", GroupName = "Request", Order = 1)]
    public ModbusClientFunction Function { get; set; } = ModbusClientFunction.ReadHoldingRegisters;

    [Display(Name = "Unit ID", GroupName = "Request", Order = 2)]
    [Range(0, 255)]
    public int UnitId { get; set; } = 1;

    [Display(Name = "Address (0-based)", GroupName = "Request", Order = 3, Description = "Start offset. 40001 in Modicon notation is address 0.")]
    [Range(0, 65535)]
    public int Address { get; set; }

    [Display(Name = "Quantity", GroupName = "Request", Order = 4, Description = "Number of coils, or of values of the data type below (10 Float32 reads 20 registers). Ignored for writes (derived from Values).")]
    [Range(1, 2000)]
    public int Quantity { get; set; } = 10;

    [Display(Name = "Values", GroupName = "Request", Order = 5, Description = "Comma separated values to write. Coils: 1,0,true. Registers: numbers in the data type below. Mask write: AND mask, OR mask (e.g. 0xFF00,0x0012).")]
    public string Values { get; set; } = string.Empty;

    [Display(Name = "Write address", GroupName = "Request", Order = 6, Description = "Write start offset for Read/Write Multiple Registers.")]
    [Range(0, 65535)]
    public int WriteAddress { get; set; }

    [Display(Name = "Data type", GroupName = "Interpretation", Order = 10, Description = "How registers are decoded for display and encoded for writes.")]
    public TagDataType DataType { get; set; } = TagDataType.UInt16;

    [Display(Name = "Word order", GroupName = "Interpretation", Order = 11)]
    public WordOrder WordOrder { get; set; } = WordOrder.ABCD;
}

// ---------------------------------------------------------------- Master core

/// <summary>
/// Modbus master logic shared by every transport: typed reads and writes, and the generic request executor used by the UI.
/// Transports implement connecting and one request/response exchange.
/// </summary>
public abstract class ModbusMaster : IProtocolClient
{
    private readonly ModbusClientTiming _timing;
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected ModbusMaster(ModbusClientTiming timing, ClientContext context)
    {
        _timing = timing;
        Log = context.Log;
    }

    protected ITrafficLog Log { get; }
    protected int ResponseTimeoutMs => _timing.ResponseTimeoutMs;
    protected abstract string Remote { get; }
    public abstract bool IsConnected { get; }

    protected abstract Task OpenAsync(CancellationToken ct);
    protected abstract void Close();

    /// <summary>Sends one request PDU and returns the response PDU. Throws <see cref="TimeoutException"/> when nothing comes back.</summary>
    protected abstract Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken ct);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Close();
            await OpenAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsConnected) Log.Info(Remote, "Disconnected");
            Close();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Sends a request PDU and returns the response PDU (which may be an exception response).</summary>
    public async Task<byte[]> TransactAsync(byte unitId, byte[] pdu, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool last = attempt >= _timing.Retries;
                try
                {
                    if (!IsConnected) await OpenAsync(ct);
                    return await ExchangeAsync(unitId, pdu, ct);
                }
                catch (TimeoutException) when (!last)
                {
                    Log.Warn(Remote, $"Retry {attempt + 1}/{_timing.Retries}");
                    Close();
                }
                catch (Exception ex) when (!last && ex is IOException or SocketException or UnauthorizedAccessException)
                {
                    Log.Warn(Remote, $"Connection error ({ex.Message}); retry {attempt + 1}/{_timing.Retries}");
                    Close();
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    Close();
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] EnsureOk(byte fc, byte[] response)
    {
        if (response.Length >= 2 && response[0] == (fc | 0x80))
            throw new ModbusException((ModbusExceptionCode)response[1]);
        if (response.Length == 0 || response[0] != fc)
            throw new InvalidDataException($"Unexpected function code in response (0x{(response.Length > 0 ? response[0] : 0):X2}).");
        return response;
    }

    private static byte[] Pdu(ModbusFunctionCode fc, params ushort[] words)
    {
        var pdu = new byte[1 + words.Length * 2];
        pdu[0] = (byte)fc;
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1 + i * 2), words[i]);
        return pdu;
    }

    // ---- Typed API ----

    public Task<bool[]> ReadCoilsAsync(byte unit, ushort start, ushort quantity, CancellationToken ct = default) =>
        ReadBitsAsync(ModbusFunctionCode.ReadCoils, unit, start, quantity, ct);

    public Task<bool[]> ReadDiscreteInputsAsync(byte unit, ushort start, ushort quantity, CancellationToken ct = default) =>
        ReadBitsAsync(ModbusFunctionCode.ReadDiscreteInputs, unit, start, quantity, ct);

    public Task<ushort[]> ReadHoldingRegistersAsync(byte unit, ushort start, ushort quantity, CancellationToken ct = default) =>
        ReadRegistersAsync(ModbusFunctionCode.ReadHoldingRegisters, unit, start, quantity, ct);

    public Task<ushort[]> ReadInputRegistersAsync(byte unit, ushort start, ushort quantity, CancellationToken ct = default) =>
        ReadRegistersAsync(ModbusFunctionCode.ReadInputRegisters, unit, start, quantity, ct);

    private async Task<bool[]> ReadBitsAsync(ModbusFunctionCode fc, byte unit, ushort start, ushort qty, CancellationToken ct)
    {
        var resp = EnsureOk((byte)fc, await TransactAsync(unit, Pdu(fc, start, qty), ct));
        if (resp.Length < 2 + (qty + 7) / 8) throw new InvalidDataException("Response too short.");
        var bits = new bool[qty];
        for (int i = 0; i < qty; i++) bits[i] = ((resp[2 + i / 8] >> (i % 8)) & 1) != 0;
        return bits;
    }

    private async Task<ushort[]> ReadRegistersAsync(ModbusFunctionCode fc, byte unit, ushort start, ushort qty, CancellationToken ct)
    {
        var resp = EnsureOk((byte)fc, await TransactAsync(unit, Pdu(fc, start, qty), ct));
        return ParseRegisters(resp, qty);
    }

    private static ushort[] ParseRegisters(byte[] resp, int qty)
    {
        if (resp.Length < 2 + qty * 2 || resp[1] != qty * 2) throw new InvalidDataException("Response byte count does not match the request.");
        var regs = new ushort[qty];
        for (int i = 0; i < qty; i++) regs[i] = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(2 + i * 2));
        return regs;
    }

    public async Task WriteSingleCoilAsync(byte unit, ushort address, bool value, CancellationToken ct = default) =>
        EnsureOk(0x05, await TransactAsync(unit, Pdu(ModbusFunctionCode.WriteSingleCoil, address, value ? (ushort)0xFF00 : (ushort)0), ct));

    public async Task WriteSingleRegisterAsync(byte unit, ushort address, ushort value, CancellationToken ct = default) =>
        EnsureOk(0x06, await TransactAsync(unit, Pdu(ModbusFunctionCode.WriteSingleRegister, address, value), ct));

    public async Task WriteMultipleCoilsAsync(byte unit, ushort start, bool[] values, CancellationToken ct = default)
    {
        int byteCount = (values.Length + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = 0x0F;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)byteCount;
        for (int i = 0; i < values.Length; i++)
            if (values[i]) pdu[6 + i / 8] |= (byte)(1 << (i % 8));
        EnsureOk(0x0F, await TransactAsync(unit, pdu, ct));
    }

    public async Task WriteMultipleRegistersAsync(byte unit, ushort start, ushort[] values, CancellationToken ct = default)
    {
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        EnsureOk(0x10, await TransactAsync(unit, pdu, ct));
    }

    public async Task MaskWriteRegisterAsync(byte unit, ushort address, ushort andMask, ushort orMask, CancellationToken ct = default) =>
        EnsureOk(0x16, await TransactAsync(unit, Pdu(ModbusFunctionCode.MaskWriteRegister, address, andMask, orMask), ct));

    public async Task<ushort[]> ReadWriteMultipleRegistersAsync(byte unit, ushort readStart, ushort readQuantity, ushort writeStart, ushort[] values, CancellationToken ct = default)
    {
        var pdu = new byte[10 + values.Length * 2];
        pdu[0] = 0x17;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), readStart);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), readQuantity);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(5), writeStart);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(7), (ushort)values.Length);
        pdu[9] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(10 + i * 2), values[i]);
        var resp = EnsureOk(0x17, await TransactAsync(unit, pdu, ct));
        return ParseRegisters(resp, readQuantity);
    }

    /// <summary>Reads all identification objects for a category (1 basic, 2 regular, 3 extended), following "more follows".</summary>
    public async Task<IReadOnlyList<(byte Id, string Value)>> ReadDeviceIdentificationAsync(byte unit, byte category = 2, CancellationToken ct = default)
    {
        var result = new List<(byte, string)>();
        byte next = 0;
        for (int guard = 0; guard < 32; guard++)
        {
            var resp = EnsureOk(0x2B, await TransactAsync(unit, [0x2B, 0x0E, category, next], ct));
            if (resp.Length < 7) throw new InvalidDataException("Device identification response too short.");
            int count = resp[6], offset = 7;
            for (int i = 0; i < count && offset + 2 <= resp.Length; i++)
            {
                byte id = resp[offset];
                int len = resp[offset + 1];
                result.Add((id, Encoding.ASCII.GetString(resp, offset + 2, Math.Min(len, resp.Length - offset - 2))));
                offset += 2 + len;
            }
            if (resp[4] != 0xFF) break;
            next = resp[5];
        }
        return result;
    }

    // ---- Generic executor used by the UI ----

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (ModbusClientRequest)request;
        var sw = Stopwatch.StartNew();
        try
        {
            var rows = await ExecuteCoreAsync(r, cancellationToken);
            return ClientResult.Ok($"{r.Function}: OK ({rows.Count} rows)", rows, sw.Elapsed);
        }
        catch (ModbusException ex)
        {
            return ClientResult.Fail($"Exception {(byte)ex.Code:X2}: {ModbusNames.Describe(ex.Code)}", sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
    }

    private async Task<IReadOnlyList<ClientResultRow>> ExecuteCoreAsync(ModbusClientRequest r, CancellationToken ct)
    {
        byte unit = (byte)Math.Clamp(r.UnitId, 0, 255);
        ushort addr = (ushort)Math.Clamp(r.Address, 0, 65535);
        ushort qty = (ushort)Math.Clamp(r.Quantity, 1, 2000);

        switch (r.Function)
        {
            case ModbusClientFunction.ReadCoils:
                return BitRows(ModbusAreaKind.Coils, addr, await ReadCoilsAsync(unit, addr, qty, ct));
            case ModbusClientFunction.ReadDiscreteInputs:
                return BitRows(ModbusAreaKind.DiscreteInputs, addr, await ReadDiscreteInputsAsync(unit, addr, qty, ct));
            case ModbusClientFunction.ReadHoldingRegisters:
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, await ReadHoldingRegistersAsync(unit, addr, RegisterQuantity(r, qty), ct), r);
            case ModbusClientFunction.ReadInputRegisters:
                return RegisterRows(ModbusAreaKind.InputRegisters, addr, await ReadInputRegistersAsync(unit, addr, RegisterQuantity(r, qty), ct), r);

            case ModbusClientFunction.WriteSingleCoil:
            {
                var v = (bool)ValueConverter.Parse(FirstValue(r.Values), TagDataType.Bool);
                await WriteSingleCoilAsync(unit, addr, v, ct);
                return BitRows(ModbusAreaKind.Coils, addr, [v]);
            }
            case ModbusClientFunction.WriteSingleRegister:
            {
                var type = r.DataType == TagDataType.Int16 ? TagDataType.Int16 : TagDataType.UInt16;
                var words = RegisterCodec.Encode(ValueConverter.Parse(FirstValue(r.Values), type), type, WordOrder.ABCD);
                await WriteSingleRegisterAsync(unit, addr, words[0], ct);
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, words, new ModbusClientRequest { DataType = type });
            }
            case ModbusClientFunction.WriteMultipleCoils:
            {
                var bits = SplitValues(r.Values).Select(v => (bool)ValueConverter.Parse(v, TagDataType.Bool)).ToArray();
                if (bits.Length == 0) throw new FormatException("Enter at least one value.");
                await WriteMultipleCoilsAsync(unit, addr, bits, ct);
                return BitRows(ModbusAreaKind.Coils, addr, bits);
            }
            case ModbusClientFunction.WriteMultipleRegisters:
            {
                var words = EncodeValues(r);
                await WriteMultipleRegistersAsync(unit, addr, words, ct);
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, words, r);
            }
            case ModbusClientFunction.MaskWriteRegister:
            {
                var parts = SplitValues(r.Values);
                if (parts.Length != 2) throw new FormatException("Enter AND mask and OR mask, e.g. 0xFF00,0x0012.");
                var and = (ushort)ValueConverter.Parse(parts[0], TagDataType.UInt16);
                var or = (ushort)ValueConverter.Parse(parts[1], TagDataType.UInt16);
                await MaskWriteRegisterAsync(unit, addr, and, or, ct);
                return [new ClientResultRow(Format(ModbusAreaKind.HoldingRegisters, addr), $"AND 0x{and:X4} OR 0x{or:X4}", "Mask applied")];
            }
            case ModbusClientFunction.ReadWriteMultipleRegisters:
            {
                var words = EncodeValues(r);
                var regs = await ReadWriteMultipleRegistersAsync(unit, addr, (ushort)Math.Min((int)RegisterQuantity(r, qty), ModbusLimits.MaxReadRegisters),
                    (ushort)Math.Clamp(r.WriteAddress, 0, 65535), words, ct);
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, regs, r);
            }
            case ModbusClientFunction.ReadDeviceIdentification:
            {
                var objects = await ReadDeviceIdentificationAsync(unit, 2, ct);
                return objects.Select(o => new ClientResultRow($"Object {o.Id}", $"0x{o.Id:X2}", $"{ObjectName(o.Id)}: {o.Value}") { Typed = o.Value }).ToList();
            }
            default:
                throw new NotSupportedException(r.Function.ToString());
        }
    }

    /// <summary>Quantity counts values of the data type, so 10 Float32 reads 20 registers.</summary>
    private static ushort RegisterQuantity(ModbusClientRequest r, ushort qty)
    {
        var type = r.DataType is TagDataType.Bool or TagDataType.String ? TagDataType.UInt16 : r.DataType;
        return (ushort)Math.Min(qty * RegisterCodec.WordCount(type), ModbusLimits.MaxReadRegisters);
    }

    private static string ObjectName(byte id) => id switch
    {
        0 => "VendorName",
        1 => "ProductCode",
        2 => "MajorMinorRevision",
        3 => "VendorUrl",
        4 => "ProductName",
        5 => "ModelName",
        6 => "UserApplicationName",
        _ => "Object",
    };

    private static string[] SplitValues(string text) =>
        (text ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string FirstValue(string text) =>
        SplitValues(text).FirstOrDefault() ?? throw new FormatException("Enter a value to write.");

    private static ushort[] EncodeValues(ModbusClientRequest r)
    {
        var values = SplitValues(r.Values);
        if (values.Length == 0) throw new FormatException("Enter at least one value.");
        var type = r.DataType is TagDataType.Bool or TagDataType.String ? TagDataType.UInt16 : r.DataType;
        return values.SelectMany(v => RegisterCodec.Encode(ValueConverter.Parse(v, type), type, r.WordOrder)).ToArray();
    }

    private static string Format(ModbusAreaKind kind, int index) => $"{ModbusNames.Prefix(kind)}:{index} ({ModbusNames.Modicon(kind, index)})";

    private static List<ClientResultRow> BitRows(ModbusAreaKind kind, int start, bool[] bits) =>
        bits.Select((b, i) => new ClientResultRow(Format(kind, start + i), b ? "1" : "0", b ? "ON" : "OFF") { Typed = b }).ToList();

    private static List<ClientResultRow> RegisterRows(ModbusAreaKind kind, int start, ushort[] regs, ModbusClientRequest r)
    {
        var type = r.DataType is TagDataType.Bool or TagDataType.String ? TagDataType.UInt16 : r.DataType;
        int n = RegisterCodec.WordCount(type);
        var rows = new List<ClientResultRow>();
        int i = 0;
        for (; i + n <= regs.Length; i += n)
        {
            var slice = regs.AsSpan(i, n);
            string raw = string.Join(" ", slice.ToArray().Select(w => $"0x{w:X4}"));
            var value = RegisterCodec.Decode(slice, type, r.WordOrder);
            rows.Add(new ClientResultRow(Format(kind, start + i), raw, ValueConverter.Format(value)) { Typed = value });
        }
        for (; i < regs.Length; i++)
            rows.Add(new ClientResultRow(Format(kind, start + i), $"0x{regs[i]:X4}", regs[i].ToString(CultureInfo.InvariantCulture)) { Typed = regs[i] });
        return rows;
    }

    /// <summary>
    /// Length of an RTU response (unit + PDU + CRC) from its first bytes. 0 = need more bytes; -1 = can't be predicted
    /// (read until the line goes quiet).
    /// </summary>
    internal static int ExpectedRtuResponseLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return 0;
        byte fc = buffer[1];
        if ((fc & 0x80) != 0) return 5;
        switch (fc)
        {
            case 1: case 2: case 3: case 4: case 23:
                return buffer.Length < 3 ? 0 : 5 + buffer[2];
            case 5: case 6: case 8: case 15: case 16:
                return 8;
            case 22:
                return 10;
            default:
                return -1;
        }
    }
}

// ---------------------------------------------------------------- Transports

/// <summary>Modbus TCP client (MBAP framing).</summary>
public sealed class ModbusTcpClient(ModbusClientSettings settings, ClientContext context) : ModbusMaster(settings, context)
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public override bool IsConnected => _tcp?.Connected == true;
    protected override string Remote => $"{settings.Host}:{settings.Port}";

    protected override async Task OpenAsync(CancellationToken ct)
    {
        (_tcp, _stream) = await TcpConnect.OpenAsync(settings, Log, Remote, ct);
    }

    protected override void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    protected override async Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        ushort tid = unchecked(++_transactionId);

        var request = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), tid);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(pdu.Length + 1));
        request[6] = unitId;
        pdu.CopyTo(request, 7);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeoutMs);
        try
        {
            await stream.WriteAsync(request, timeout.Token);
            Log.Frame(TrafficDirection.Tx, Remote, request, () => ModbusFrameFormatter.Describe(request, true));

            var header = new byte[7];
            while (true)
            {
                await stream.ReadExactlyAsync(header, timeout.Token);
                ushort rtid = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0));
                ushort len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (len < 2 || len > ModbusLimits.MaxPduLength + 1)
                    throw new IOException($"Invalid MBAP length {len} in response.");

                var body = new byte[len - 1];
                await stream.ReadExactlyAsync(body, timeout.Token);

                var adu = new byte[7 + body.Length];
                header.CopyTo(adu, 0);
                body.CopyTo(adu, 7);
                Log.Frame(TrafficDirection.Rx, Remote, adu, () => ModbusFrameFormatter.Describe(adu, false));

                if (rtid != tid)
                {
                    Log.Warn(Remote, $"Ignoring response with unexpected transaction ID {rtid} (expected {tid})");
                    continue;
                }
                return body;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Error(Remote, $"No response within {ResponseTimeoutMs} ms");
            Close(); // the stream may now be out of sync
            throw new TimeoutException($"No response within {ResponseTimeoutMs} ms.");
        }
        catch (EndOfStreamException)
        {
            Close();
            throw new IOException("Connection closed by server.");
        }
    }
}

/// <summary>Modbus RTU frames (unit + PDU + CRC) over TCP.</summary>
public sealed class ModbusRtuOverTcpClient(ModbusRtuOverTcpClientSettings settings, ClientContext context) : ModbusMaster(settings, context)
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public override bool IsConnected => _tcp?.Connected == true;
    protected override string Remote => $"{settings.Host}:{settings.Port}";

    protected override async Task OpenAsync(CancellationToken ct)
    {
        (_tcp, _stream) = await TcpConnect.OpenAsync(settings, Log, Remote, ct);
    }

    protected override void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    protected override async Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        var request = ModbusRtu.BuildAdu(unitId, pdu);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeoutMs);
        try
        {
            await stream.WriteAsync(request, timeout.Token);
            Log.Frame(TrafficDirection.Tx, Remote, request, () => ModbusRtu.Describe(request, true));

            var buffer = new List<byte>(ModbusRtu.MaxAduLength);
            var chunk = new byte[ModbusRtu.MaxAduLength];
            while (true)
            {
                int expected = ExpectedRtuResponseLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
                if (expected > 0 && buffer.Count >= expected) break;
                if (buffer.Count > ModbusRtu.MaxAduLength) throw new IOException("Response longer than an RTU frame.");

                int n;
                if (expected < 0)
                {
                    // Unknown length (e.g. device identification): read until 100 ms of silence.
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    quiet.CancelAfter(100);
                    try { n = await stream.ReadAsync(chunk, quiet.Token); }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { break; }
                }
                else n = await stream.ReadAsync(chunk, timeout.Token);
                if (n == 0) throw new IOException("Connection closed by server.");
                buffer.AddRange(chunk.AsSpan(0, n));
            }

            var adu = buffer.ToArray();
            Log.Frame(TrafficDirection.Rx, Remote, adu, () => ModbusRtu.Describe(adu, false));
            return CheckRtuResponse(adu, unitId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Error(Remote, $"No response within {ResponseTimeoutMs} ms");
            Close();
            throw new TimeoutException($"No response within {ResponseTimeoutMs} ms.");
        }
    }

    internal static byte[] CheckRtuResponse(byte[] adu, byte unitId)
    {
        if (!ModbusRtu.CheckCrc(adu)) throw new InvalidDataException("Response CRC error.");
        if (adu[0] != unitId) throw new InvalidDataException($"Response from unit {adu[0]}, expected {unitId}.");
        return adu[1..^2];
    }
}

/// <summary>Modbus RTU master on a serial port.</summary>
public sealed class ModbusRtuSerialClient(ModbusRtuSerialClientSettings settings, ClientContext context) : ModbusMaster(settings, context)
{
    private SerialPort? _port;

    public override bool IsConnected => _port?.IsOpen == true;
    protected override string Remote => settings.PortName;

    protected override Task OpenAsync(CancellationToken ct)
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            ReadTimeout = settings.ResponseTimeoutMs,
            WriteTimeout = Math.Max(500, settings.ResponseTimeoutMs),
        };
        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            port.Dispose();
            Log.Error(Remote, $"Open failed: {ex.Message}");
            throw;
        }
        _port = port;
        Log.Info(Remote, $"Opened {settings.Describe()}");
        return Task.CompletedTask;
    }

    protected override void Close()
    {
        try { _port?.Close(); } catch (IOException) { }
        _port?.Dispose();
        _port = null;
    }

    protected override Task<byte[]> ExchangeAsync(byte unitId, byte[] pdu, CancellationToken ct) => Task.Run(() =>
    {
        var port = _port ?? throw new InvalidOperationException("Not open.");
        var request = ModbusRtu.BuildAdu(unitId, pdu);
        port.DiscardInBuffer();
        port.Write(request, 0, request.Length);
        Log.Frame(TrafficDirection.Tx, Remote, request, () => ModbusRtu.Describe(request, true));

        var buffer = new List<byte>(ModbusRtu.MaxAduLength);
        var deadline = Environment.TickCount64 + settings.ResponseTimeoutMs;
        int gap = settings.EffectiveGapMs();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int expected = ExpectedRtuResponseLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
            if (expected > 0 && buffer.Count >= expected) break;
            if (buffer.Count > ModbusRtu.MaxAduLength) throw new IOException("Response longer than an RTU frame.");

            // Before the first byte wait for the response timeout; afterwards a silent gap ends a frame of unknown length.
            int wait = buffer.Count == 0 ? (int)Math.Max(1, deadline - Environment.TickCount64) : expected < 0 ? gap : Math.Max(gap, 200);
            port.ReadTimeout = wait;
            try
            {
                int b = port.ReadByte();
                if (b < 0) throw new IOException("Port closed.");
                buffer.Add((byte)b);
            }
            catch (TimeoutException)
            {
                if (buffer.Count == 0)
                {
                    Log.Error(Remote, $"No response within {settings.ResponseTimeoutMs} ms");
                    throw new TimeoutException($"No response within {settings.ResponseTimeoutMs} ms.");
                }
                break; // silence ended the frame
            }
        }

        var adu = buffer.ToArray();
        Log.Frame(TrafficDirection.Rx, Remote, adu, () => ModbusRtu.Describe(adu, false));
        return ModbusRtuOverTcpClient.CheckRtuResponse(adu, unitId);
    }, ct);
}

internal static class TcpConnect
{
    public static async Task<(TcpClient, NetworkStream)> OpenAsync(ModbusClientSettings settings, ITrafficLog log, string remote, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.ConnectTimeoutMs);
        try
        {
            await tcp.ConnectAsync(settings.Host, settings.Port, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            log.Error(remote, "Connect timed out");
            throw new TimeoutException($"Connecting to {remote} timed out after {settings.ConnectTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            log.Error(remote, $"Connect failed: {ex.Message}");
            throw;
        }
        log.Info(remote, "Connected");
        return (tcp, tcp.GetStream());
    }
}
