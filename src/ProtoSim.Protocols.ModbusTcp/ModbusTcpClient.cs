using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Modbus TCP client (master) with typed helpers and a generic request executor for the UI.</summary>
public sealed class ModbusTcpClient : IProtocolClient
{
    private readonly ModbusClientSettings _settings;
    private readonly ITrafficLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public ModbusTcpClient(ModbusClientSettings settings, ClientContext context)
    {
        _settings = settings;
        _log = context.Log;
    }

    public bool IsConnected => _tcp?.Connected == true;

    private string Remote => $"{_settings.Host}:{_settings.Port}";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await ConnectCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_tcp != null) _log.Info(Remote, "Disconnected");
            CloseCore();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        CloseCore();
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_settings.ConnectTimeoutMs);
        try
        {
            await tcp.ConnectAsync(_settings.Host, _settings.Port, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            _log.Error(Remote, "Connect timed out");
            throw new TimeoutException($"Connecting to {Remote} timed out after {_settings.ConnectTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            _log.Error(Remote, $"Connect failed: {ex.Message}");
            throw;
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _log.Info(Remote, "Connected");
    }

    private void CloseCore()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    /// <summary>Sends a request PDU and returns the response PDU (which may be an exception response).</summary>
    public async Task<byte[]> TransactAsync(byte unitId, byte[] pdu, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool last = attempt >= _settings.Retries;
                try
                {
                    if (!IsConnected) await ConnectCoreAsync(ct);
                    return await TransactOnceAsync(unitId, pdu, ct);
                }
                catch (TimeoutException) when (!last)
                {
                    _log.Warn(Remote, $"Retry {attempt + 1}/{_settings.Retries}");
                    CloseCore();
                }
                catch (Exception ex) when (!last && ex is IOException or SocketException)
                {
                    _log.Warn(Remote, $"Connection error ({ex.Message}); retry {attempt + 1}/{_settings.Retries}");
                    CloseCore();
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    CloseCore();
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> TransactOnceAsync(byte unitId, byte[] pdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        ushort tid = unchecked(++_transactionId);

        var request = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), tid);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(pdu.Length + 1));
        request[6] = unitId;
        pdu.CopyTo(request, 7);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_settings.ResponseTimeoutMs);
        try
        {
            await stream.WriteAsync(request, timeout.Token);
            _log.Frame(TrafficDirection.Tx, Remote, request, () => ModbusFrameFormatter.Describe(request, true));

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
                _log.Frame(TrafficDirection.Rx, Remote, adu, () => ModbusFrameFormatter.Describe(adu, false));

                if (rtid != tid)
                {
                    _log.Warn(Remote, $"Ignoring response with unexpected transaction ID {rtid} (expected {tid})");
                    continue;
                }
                return body;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Error(Remote, $"No response within {_settings.ResponseTimeoutMs} ms");
            CloseCore(); // the stream may now be out of sync
            throw new TimeoutException($"No response within {_settings.ResponseTimeoutMs} ms.");
        }
        catch (EndOfStreamException)
        {
            CloseCore();
            throw new IOException("Connection closed by server.");
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
            return new ClientResult(true, $"{r.Function}: OK ({rows.Count} rows)", rows, sw.Elapsed);
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
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, await ReadHoldingRegistersAsync(unit, addr, qty, ct), r);
            case ModbusClientFunction.ReadInputRegisters:
                return RegisterRows(ModbusAreaKind.InputRegisters, addr, await ReadInputRegistersAsync(unit, addr, qty, ct), r);

            case ModbusClientFunction.WriteSingleCoil:
            {
                var v = (bool)ValueConverter.Parse(FirstValue(r.Values), TagDataType.Bool);
                await WriteSingleCoilAsync(unit, addr, v, ct);
                return BitRows(ModbusAreaKind.Coils, addr, [v]);
            }
            case ModbusClientFunction.WriteSingleRegister:
            {
                var words = RegisterCodec.Encode(ValueConverter.Parse(FirstValue(r.Values), r.DataType == TagDataType.Int16 ? TagDataType.Int16 : TagDataType.UInt16), TagDataType.UInt16, WordOrder.ABCD);
                await WriteSingleRegisterAsync(unit, addr, words[0], ct);
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, words, new ModbusClientRequest { DataType = TagDataType.UInt16 });
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
                var regs = await ReadWriteMultipleRegistersAsync(unit, addr, (ushort)Math.Min((int)qty, ModbusLimits.MaxReadRegisters), (ushort)Math.Clamp(r.WriteAddress, 0, 65535), words, ct);
                return RegisterRows(ModbusAreaKind.HoldingRegisters, addr, regs, r);
            }
            case ModbusClientFunction.ReadDeviceIdentification:
            {
                var objects = await ReadDeviceIdentificationAsync(unit, 2, ct);
                return objects.Select(o => new ClientResultRow($"Object {o.Id}", $"0x{o.Id:X2}", $"{ObjectName(o.Id)}: {o.Value}")).ToList();
            }
            default:
                throw new NotSupportedException(r.Function.ToString());
        }
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
        var type = r.DataType == TagDataType.Bool ? TagDataType.UInt16 : r.DataType;
        return values.SelectMany(v => RegisterCodec.Encode(ValueConverter.Parse(v, type), type, r.WordOrder)).ToArray();
    }

    private static string Format(ModbusAreaKind kind, int index) => $"{ModbusNames.Prefix(kind)}:{index} ({ModbusNames.Modicon(kind, index)})";

    private static List<ClientResultRow> BitRows(ModbusAreaKind kind, int start, bool[] bits) =>
        bits.Select((b, i) => new ClientResultRow(Format(kind, start + i), b ? "1" : "0", b ? "ON" : "OFF")).ToList();

    private static List<ClientResultRow> RegisterRows(ModbusAreaKind kind, int start, ushort[] regs, ModbusClientRequest r)
    {
        var type = r.DataType == TagDataType.Bool ? TagDataType.UInt16 : r.DataType;
        int n = RegisterCodec.WordCount(type);
        var rows = new List<ClientResultRow>();
        int i = 0;
        for (; i + n <= regs.Length; i += n)
        {
            var slice = regs.AsSpan(i, n);
            string raw = string.Join(" ", slice.ToArray().Select(w => $"0x{w:X4}"));
            rows.Add(new ClientResultRow(Format(kind, start + i), raw, ValueConverter.Format(RegisterCodec.Decode(slice, type, r.WordOrder))));
        }
        for (; i < regs.Length; i++)
            rows.Add(new ClientResultRow(Format(kind, start + i), $"0x{regs[i]:X4}", regs[i].ToString(CultureInfo.InvariantCulture)));
        return rows;
    }
}
