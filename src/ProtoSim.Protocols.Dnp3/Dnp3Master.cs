using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using dnp3;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Dnp3;

public sealed class Dnp3MasterSettings
{
    [Display(Name = "Host", GroupName = "Endpoint", Order = 1, Description = "Outstation host name or IP address.")]
    public string Host { get; set; } = "127.0.0.1";

    [Display(Name = "TCP port", GroupName = "Endpoint", Order = 2)]
    [Range(1, 65535)]
    public int Port { get; set; } = 20000;

    [Display(Name = "Master address", GroupName = "DNP3", Order = 10)]
    [Range(1, 65519)]
    public int MasterAddress { get; set; } = 1;

    [Display(Name = "Outstation address", GroupName = "DNP3", Order = 11)]
    [Range(1, 65519)]
    public int OutstationAddress { get; set; } = 10;

    [Display(Name = "Response timeout (ms)", GroupName = "DNP3", Order = 12)]
    [Range(100, 60_000)]
    public int ResponseTimeoutMs { get; set; } = 5000;

    [Display(Name = "Accept unsolicited responses", GroupName = "DNP3", Order = 13, Description = "Enable unsolicited reporting of class 1-3 events by the outstation.")]
    public bool EnableUnsolicited { get; set; }

    [Display(Name = "Connect timeout (ms)", GroupName = "Endpoint", Order = 3)]
    [Range(100, 60_000)]
    public int ConnectTimeoutMs { get; set; } = 5000;
}

public enum Dnp3MasterOperation
{
    /// <summary>Class 0, 1, 2 and 3: every static value plus buffered events.</summary>
    IntegrityPoll,
    /// <summary>Class 1, 2 and 3 events only.</summary>
    EventPoll,
    /// <summary>A range of one point type.</summary>
    ReadRange,
    /// <summary>Control relay output block (binary output command).</summary>
    BinaryCommand,
    /// <summary>Analog output command.</summary>
    AnalogCommand,
}

public enum Dnp3PointKind
{
    BinaryInput,
    DoubleBitBinaryInput,
    BinaryOutputStatus,
    Counter,
    FrozenCounter,
    AnalogInput,
    AnalogOutputStatus,
}

public enum Dnp3BinaryCommand { LatchOn, LatchOff, PulseOn, PulseOff, Close, Trip }

public sealed class Dnp3MasterRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1, Description = "Polled values are kept, so events update the last integrity poll's rows.")]
    public Dnp3MasterOperation Operation { get; set; } = Dnp3MasterOperation.IntegrityPoll;

    [Display(Name = "Point type", GroupName = "Request", Order = 2, Description = "Read range: which points to read.")]
    public Dnp3PointKind PointType { get; set; } = Dnp3PointKind.AnalogInput;

    [Display(Name = "Start index", GroupName = "Request", Order = 3, Description = "Read range: first index. Commands: the point index.")]
    [Range(0, 65535)]
    public int Start { get; set; }

    [Display(Name = "Stop index", GroupName = "Request", Order = 4, Description = "Read range: last index.")]
    [Range(0, 65535)]
    public int Stop { get; set; } = 9;

    [Display(Name = "Binary command", GroupName = "Command", Order = 10)]
    public Dnp3BinaryCommand BinaryCommand { get; set; } = Dnp3BinaryCommand.LatchOn;

    [Display(Name = "Analog value", GroupName = "Command", Order = 11)]
    public double AnalogValue { get; set; }

    [Display(Name = "Select before operate", GroupName = "Command", Order = 12, Description = "Off = direct operate.")]
    public bool SelectBeforeOperate { get; set; } = true;
}

/// <summary>DNP3 TCP master: integrity and event polls, range reads, and binary/analog output commands.</summary>
public sealed class Dnp3Master(Dnp3MasterSettings settings, ClientContext context) : IProtocolClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(int Order, int Index), ClientResultRow> _points = new();
    private Runtime? _runtime;
    private MasterChannel? _channel;
    private AssociationId? _association;
    private volatile ClientState _state = ClientState.Disabled;

    private ITrafficLog Log => context.Log;
    private string Remote => $"{settings.Host}:{settings.Port}";
    public bool IsConnected => _state == ClientState.Connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureOpenAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<(MasterChannel, AssociationId)> EnsureOpenAsync(CancellationToken ct)
    {
        if (_channel != null && _association is { } a)
        {
            await WaitConnectedAsync(ct);
            return (_channel, a);
        }
        Close();
        try
        {
            _runtime = new Runtime(new RuntimeConfig { NumCoreThreads = 1 });
            var channelConfig = new MasterChannelConfig(checked((ushort)settings.MasterAddress));
            _channel = MasterChannel.CreateTcpChannel(_runtime, LinkErrorMode.Close, channelConfig,
                new EndpointList($"{settings.Host}:{settings.Port}"),
                new ConnectStrategy().WithMinConnectDelay(TimeSpan.FromSeconds(1)).WithMaxConnectDelay(TimeSpan.FromSeconds(5)).WithReconnectDelay(TimeSpan.FromSeconds(1)),
                new StateListener(this));
            var unsol = settings.EnableUnsolicited ? EventClasses.All() : EventClasses.None();
            var assocConfig = new AssociationConfig(EventClasses.All(), unsol, Classes.All(), EventClasses.None())
                .WithResponseTimeout(TimeSpan.FromMilliseconds(settings.ResponseTimeoutMs));
            _association = _channel.AddAssociation(checked((ushort)settings.OutstationAddress), assocConfig,
                new ReadHandler(this), new AssociationHandler(), new AssociationInformation(this));
            _channel.Enable();
            Log.Info(Remote, $"Connecting Â· master {settings.MasterAddress} â†’ outstation {settings.OutstationAddress}");
        }
        catch (Exception ex)
        {
            Log.Error(Remote, $"Open failed: {ex.Message}");
            Close();
            throw;
        }
        await WaitConnectedAsync(ct);
        return (_channel!, _association!);
    }

    private async Task WaitConnectedAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + settings.ConnectTimeoutMs;
        while (_state != ClientState.Connected)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Not connected to {Remote} after {settings.ConnectTimeoutMs} ms ({_state}).");
            await Task.Delay(50, ct);
        }
    }

    private void Close()
    {
        try { _channel?.Shutdown(); } catch { }
        try { _runtime?.Shutdown(); } catch { }
        _channel = null;
        _runtime = null;
        _association = null;
        _state = ClientState.Disabled;
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_channel != null) Log.Info(Remote, "Disconnected");
            Close();
            _points.Clear();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (Dnp3MasterRequest)request;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (channel, assoc) = await EnsureOpenAsync(cancellationToken);
            switch (r.Operation)
            {
                case Dnp3MasterOperation.IntegrityPoll:
                    Log.Info(Remote, "READ class 0,1,2,3");
                    await channel.Read(assoc, Request.ClassRequest(true, true, true, true));
                    break;
                case Dnp3MasterOperation.EventPoll:
                    Log.Info(Remote, "READ class 1,2,3");
                    await channel.Read(assoc, Request.ClassRequest(false, true, true, true));
                    break;
                case Dnp3MasterOperation.ReadRange:
                {
                    ushort start = (ushort)Math.Clamp(r.Start, 0, 65535), stop = (ushort)Math.Clamp(Math.Max(r.Start, r.Stop), 0, 65535);
                    Log.Info(Remote, $"READ {r.PointType} {start}-{stop}");
                    await channel.Read(assoc, Request.TwoByteRange(Variation0(r.PointType), start, stop));
                    break;
                }
                case Dnp3MasterOperation.BinaryCommand:
                {
                    var (op, tcc) = r.BinaryCommand switch
                    {
                        Dnp3BinaryCommand.LatchOff => (OpType.LatchOff, TripCloseCode.Nul),
                        Dnp3BinaryCommand.PulseOn => (OpType.PulseOn, TripCloseCode.Nul),
                        Dnp3BinaryCommand.PulseOff => (OpType.PulseOff, TripCloseCode.Nul),
                        Dnp3BinaryCommand.Close => (OpType.PulseOn, TripCloseCode.Close),
                        Dnp3BinaryCommand.Trip => (OpType.PulseOn, TripCloseCode.Trip),
                        _ => (OpType.LatchOn, TripCloseCode.Nul),
                    };
                    var commands = new CommandSet();
                    commands.AddG12V1U16((ushort)Math.Clamp(r.Start, 0, 65535),
                        Group12Var1.FromCode(ControlCode.FromTccAndOpType(tcc, op)).WithCount(1).WithOnTime(1000).WithOffTime(1000));
                    Log.Info(Remote, $"{Mode(r)} CROB {r.BinaryCommand} on BO:{r.Start}");
                    await channel.Operate(assoc, r.SelectBeforeOperate ? CommandMode.SelectBeforeOperate : CommandMode.DirectOperate, commands);
                    return ClientResult.Ok($"{r.BinaryCommand} on BO:{r.Start} accepted",
                        [new ClientResultRow($"BO:{r.Start}", "CROB", r.BinaryCommand.ToString()) { Typed = r.BinaryCommand is Dnp3BinaryCommand.LatchOn or Dnp3BinaryCommand.PulseOn or Dnp3BinaryCommand.Close }], sw.Elapsed);
                }
                case Dnp3MasterOperation.AnalogCommand:
                {
                    var commands = new CommandSet();
                    commands.AddG41V4U16((ushort)Math.Clamp(r.Start, 0, 65535), r.AnalogValue);
                    Log.Info(Remote, $"{Mode(r)} analog output AO:{r.Start} = {r.AnalogValue}");
                    await channel.Operate(assoc, r.SelectBeforeOperate ? CommandMode.SelectBeforeOperate : CommandMode.DirectOperate, commands);
                    return ClientResult.Ok($"AO:{r.Start} = {r.AnalogValue} accepted",
                        [new ClientResultRow($"AO:{r.Start}", "G41V4", Core.Data.ValueConverter.Format(r.AnalogValue)) { Typed = r.AnalogValue }], sw.Elapsed);
                }
            }

            var rows = _points.OrderBy(kv => kv.Key.Order).ThenBy(kv => kv.Key.Index).Select(kv => kv.Value).ToList();
            return ClientResult.Ok($"{r.Operation}: {rows.Count} points", rows, sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReadException ex)
        {
            Log.Error(Remote, $"Read failed: {ex.error}");
            return ClientResult.Fail($"Read failed: {ex.error}", sw.Elapsed);
        }
        catch (CommandException ex)
        {
            Log.Error(Remote, $"Command failed: {ex.error}");
            return ClientResult.Fail($"Command failed: {ex.error}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.Error(Remote, $"{r.Operation} failed: {ex.Message}");
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Mode(Dnp3MasterRequest r) => r.SelectBeforeOperate ? "SELECT/OPERATE" : "DIRECT OPERATE";

    private static Variation Variation0(Dnp3PointKind kind) => kind switch
    {
        Dnp3PointKind.BinaryInput => Variation.Group1Var0,
        Dnp3PointKind.DoubleBitBinaryInput => Variation.Group3Var0,
        Dnp3PointKind.BinaryOutputStatus => Variation.Group10Var0,
        Dnp3PointKind.Counter => Variation.Group20Var0,
        Dnp3PointKind.FrozenCounter => Variation.Group21Var0,
        Dnp3PointKind.AnalogInput => Variation.Group30Var0,
        _ => Variation.Group40Var0,
    };

    private void Store(int order, string prefix, ushort index, object value, string text, Flags flags, HeaderInfo info, Timestamp time)
    {
        string when = time.Value == 0 ? "" : $" Â· {DateTimeOffset.FromUnixTimeMilliseconds((long)time.Value).LocalDateTime:HH:mm:ss.fff}";
        string raw = $"{info.Variation} Â· flags 0x{flags.Value:X2}{(info.IsEvent ? " Â· event" : "")}{when}";
        _points[(order, index)] = new ClientResultRow($"{prefix}:{index}", raw, text) { Typed = value };
    }

    private sealed class StateListener(Dnp3Master owner) : IClientStateListener
    {
        public void OnChange(ClientState state)
        {
            owner._state = state;
            owner.Log.Info(owner.Remote, $"Channel {state}");
        }
    }

    private sealed class AssociationHandler : IAssociationHandler
    {
        public UtcTimestamp GetCurrentTime() => UtcTimestamp.Valid((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private sealed class AssociationInformation(Dnp3Master owner) : IAssociationInformation
    {
        public void TaskStart(TaskType taskType, FunctionCode functionCode, byte seq) { }
        public void TaskSuccess(TaskType taskType, FunctionCode functionCode, byte seq) =>
            owner.Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, owner.Remote, $"{taskType} {functionCode} OK (seq {seq})"));
        public void TaskFail(TaskType taskType, TaskError error) => owner.Log.Warn(owner.Remote, $"{taskType} failed: {error}");
        public void UnsolicitedResponse(bool isFragmentHandled, byte seq) =>
            owner.Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, owner.Remote, $"Unsolicited response (seq {seq})"));
    }

    private sealed class ReadHandler(Dnp3Master m) : IReadHandler
    {
        public void BeginFragment(ReadType readType, ResponseHeader header) { }
        public void EndFragment(ReadType readType, ResponseHeader header) { }

        public void HandleBinaryInput(HeaderInfo info, ICollection<BinaryInput> values)
        { foreach (var v in values) m.Store(0, "BI", v.Index, v.Value, v.Value ? "ON" : "OFF", v.Flags, info, v.Time); }

        public void HandleDoubleBitBinaryInput(HeaderInfo info, ICollection<DoubleBitBinaryInput> values)
        { foreach (var v in values) m.Store(1, "DBI", v.Index, v.Value.ToString(), v.Value.ToString(), v.Flags, info, v.Time); }

        public void HandleBinaryOutputStatus(HeaderInfo info, ICollection<BinaryOutputStatus> values)
        { foreach (var v in values) m.Store(2, "BO", v.Index, v.Value, v.Value ? "ON" : "OFF", v.Flags, info, v.Time); }

        public void HandleCounter(HeaderInfo info, ICollection<Counter> values)
        { foreach (var v in values) m.Store(3, "C", v.Index, v.Value, v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), v.Flags, info, v.Time); }

        public void HandleFrozenCounter(HeaderInfo info, ICollection<FrozenCounter> values)
        { foreach (var v in values) m.Store(4, "FC", v.Index, v.Value, v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), v.Flags, info, v.Time); }

        public void HandleAnalogInput(HeaderInfo info, ICollection<AnalogInput> values)
        { foreach (var v in values) m.Store(5, "AI", v.Index, v.Value, Core.Data.ValueConverter.Format(v.Value), v.Flags, info, v.Time); }

        public void HandleFrozenAnalogInput(HeaderInfo info, ICollection<FrozenAnalogInput> values) { }

        public void HandleAnalogOutputStatus(HeaderInfo info, ICollection<AnalogOutputStatus> values)
        { foreach (var v in values) m.Store(6, "AO", v.Index, v.Value, Core.Data.ValueConverter.Format(v.Value), v.Flags, info, v.Time); }

        public void HandleBinaryOutputCommandEvent(HeaderInfo info, ICollection<BinaryOutputCommandEvent> values) { }
        public void HandleAnalogOutputCommandEvent(HeaderInfo info, ICollection<AnalogOutputCommandEvent> values) { }
        public void HandleUnsignedInteger(HeaderInfo info, ICollection<UnsignedInteger> values) { }
        public void HandleAnalogInputDeadBand(HeaderInfo info, ICollection<AnalogInputDeadBand> values) { }
        public void HandleOctetString(HeaderInfo info, ICollection<OctetString> values) { }
        public void HandleAbsTime(HeaderInfo info, Timestamp time) { }
        public void HandleStringAttr(HeaderInfo info, StringAttr attr, byte set, byte variation, string value) { }
        public void HandleVariationListAttr(HeaderInfo info, VariationListAttr attr, byte set, byte variation, ICollection<AttrItem> value) { }
        public void HandleUintAttr(HeaderInfo info, UintAttr attr, byte set, byte variation, uint value) { }
        public void HandleBoolAttr(HeaderInfo info, BoolAttr attr, byte set, byte variation, bool value) { }
        public void HandleIntAttr(HeaderInfo info, IntAttr attr, byte set, byte variation, int value) { }
        public void HandleTimeAttr(HeaderInfo info, TimeAttr attr, byte set, byte variation, ulong value) { }
        public void HandleFloatAttr(HeaderInfo info, FloatAttr attr, byte set, byte variation, double value) { }
        public void HandleOctetStringAttr(HeaderInfo info, OctetStringAttr attr, byte set, byte variation, ICollection<byte> value) { }
        public void HandleBitStringAttr(HeaderInfo info, BitStringAttr attr, byte set, byte variation, ICollection<byte> value) { }
    }
}
