using System.Collections.Concurrent;
using dnp3;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using CoreConnectionInfo = ProtoSim.Core.Protocols.ConnectionInfo;

namespace ProtoSim.Protocols.Dnp3;

public sealed class Dnp3Server : IProtocolServer
{
    private readonly Dnp3ServerSettings _settings;
    private readonly Dnp3DataModel _model;
    private readonly ServerContext _context;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<long, CoreConnectionInfo> _connections = new();
    private Runtime? _runtime;
    private OutstationServer? _server;
    private Outstation? _outstation;
    private long _nextConnection;

    public Dnp3Server(Dnp3ServerSettings settings, Dnp3DataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _context = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (State == ServerState.Running) return;

            SetState(ServerState.Starting);
            LastError = null;
            try
            {
                _runtime = new Runtime(new RuntimeConfig().WithNumCoreThreads(1));
                _server = OutstationServer.CreateTcpServer(_runtime, LinkErrorMode.Close,
                    $"{_settings.BindAddress}:{_settings.Port}");

                var application = new ApplicationCallbacks();
                var information = new InformationCallbacks();
                var controls = new ControlCallbacks(_settings.AllowWrites);
                var connection = new ConnectionCallbacks(this);
                var features = new OutstationFeatures()
                    .WithUnsolicited(_settings.AllowUnsolicited);
                var config = new OutstationConfig(
                    checked((ushort)_settings.OutstationAddress),
                    checked((ushort)_settings.MasterAddress),
                    EventBufferConfig.NoEvents())
                    .WithFeatures(features);

                _outstation = _server.AddOutstation(config, application, information, controls, connection, AddressFilter.Any());
                ConfigureDatabase(_outstation);
                _server.Bind();
                _outstation.Enable();

                _context.Log.Info("", $"Listening on {_settings.BindAddress}:{_settings.Port}");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _context.Log.Error("", $"Start failed: {LastError}");
                await StopCoreAsync();
                SetState(ServerState.Faulted);
                throw new InvalidOperationException(LastError, ex);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public IReadOnlyList<CoreConnectionInfo> GetConnections() => _connections.Values.ToList();

    public void DropConnection(long connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            _context.Log.Info(connection.RemoteEndPoint, "Connection dropped by user");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private async Task StopCoreAsync()
    {
        if (_server is null && _runtime is null)
        {
            if (State != ServerState.Faulted) SetState(ServerState.Stopped);
            return;
        }

        SetState(ServerState.Stopping);
        try { _outstation?.Disable(); } catch { }
        try { _server?.Shutdown(); } catch { }
        try { _runtime?.Shutdown(); } catch { }
        _outstation = null;
        _server = null;
        _runtime = null;
        _connections.Clear();
        SetState(ServerState.Stopped);
        await Task.CompletedTask;
    }

    private void ConfigureDatabase(Outstation outstation)
    {
        outstation.Transaction(database =>
        {
            foreach (var binding in _model.Bindings)
            {
                var index = checked((ushort)binding.Index);
                switch (binding.PointType)
                {
                    case Dnp3PointType.BinaryInput:
                        database.AddBinaryInput(index, EventClass.None, new BinaryInputConfig());
                        database.UpdateBinaryInput(
                            new BinaryInput(index, Convert.ToBoolean(binding.Read()), new Flags(1), Timestamp.InvalidTimestamp()),
                            UpdateOptions.NoEvent());
                        break;
                    case Dnp3PointType.AnalogInput:
                        database.AddAnalogInput(index, EventClass.None, new AnalogInputConfig());
                        database.UpdateAnalogInput(
                            new AnalogInput(index, Convert.ToDouble(binding.Read()), new Flags(1), Timestamp.InvalidTimestamp()),
                            UpdateOptions.NoEvent());
                        break;
                    case Dnp3PointType.Counter:
                        database.AddCounter(index, EventClass.None, new CounterConfig());
                        database.UpdateCounter(
                            new Counter(index, Convert.ToUInt32(binding.Read()), new Flags(1), Timestamp.InvalidTimestamp()),
                            UpdateOptions.NoEvent());
                        break;
                }
            }
        });
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ConnectionCallbacks(Dnp3Server owner) : IConnectionStateListener
    {
        public void OnChange(ConnectionState state)
        {
            if (state == ConnectionState.Connected)
            {
                var id = Interlocked.Increment(ref owner._nextConnection);
                var info = new CoreConnectionInfo(id, "DNP3 TCP");
                owner._connections[id] = info;
                owner._context.Statistics.OnConnected();
            }
            else
            {
                var connection = owner._connections.OrderBy(p => p.Key).FirstOrDefault();
                if (connection.Key != 0 && owner._connections.TryRemove(connection.Key, out _))
                    owner._context.Statistics.OnDisconnected();
            }
        }
    }

    private sealed class ApplicationCallbacks : IOutstationApplication
    {
        public ushort GetProcessingDelayMs() => 0;
        public WriteTimeResult WriteAbsoluteTime(ulong time) => WriteTimeResult.NotSupported;
        public ApplicationIin GetApplicationIin() => new();
        public RestartDelay ColdRestart() => RestartDelay.NotSupported();
        public RestartDelay WarmRestart() => RestartDelay.NotSupported();
        public FreezeResult FreezeCountersAll(FreezeType freezeType, DatabaseHandle database) => FreezeResult.NotSupported;
        public FreezeResult FreezeCountersAllAtTime(DatabaseHandle database, ulong time, uint interval) => FreezeResult.NotSupported;
        public FreezeResult FreezeCountersRange(ushort start, ushort stop, FreezeType freezeType, DatabaseHandle database) => FreezeResult.NotSupported;
        public FreezeResult FreezeCountersRangeAtTime(ushort start, ushort stop, DatabaseHandle database, ulong time, uint interval) => FreezeResult.NotSupported;
        public bool SupportWriteAnalogDeadBands() => false;
        public void BeginWriteAnalogDeadBands() { }
        public void WriteAnalogDeadBand(ushort index, double deadBand) { }
        public void EndWriteAnalogDeadBands() { }
        public bool SupportAssignClass() => false;
        public void BeginAssignClass() { }
        public void AssignClassAll(PointType pointType, EventClass eventClass) { }
        public void AssignClassRange(PointType pointType, ushort start, ushort stop, EventClass eventClass) { }
        public void EndAssignClass() { }
        public bool WriteStringAttr(byte set, byte variation, StringAttr attr, string value) => false;
        public bool WriteFloatAttr(byte set, byte variation, FloatAttr attr, float value) => false;
        public bool WriteDoubleAttr(byte set, byte variation, FloatAttr attr, double value) => false;
        public bool WriteUintAttr(byte set, byte variation, UintAttr attr, uint value) => false;
        public bool WriteIntAttr(byte set, byte variation, IntAttr attr, int value) => false;
        public bool WriteOctetStringAttr(byte set, byte variation, OctetStringAttr attr, ICollection<byte> value) => false;
        public bool WriteBitStringAttr(byte set, byte variation, BitStringAttr attr, ICollection<byte> value) => false;
        public bool WriteTimeAttr(byte set, byte variation, TimeAttr attr, ulong value) => false;
        public void BeginConfirm() { }
        public void EventCleared(ulong time) { }
        public void EndConfirm(BufferState state) { }
    }

    private sealed class InformationCallbacks : IOutstationInformation
    {
        public void ProcessRequestFromIdle(RequestHeader header) { }
        public void BroadcastReceived(FunctionCode functionCode, BroadcastAction action) { }
        public void EnterSolicitedConfirmWait(byte sequence) { }
        public void SolicitedConfirmTimeout(byte sequence) { }
        public void SolicitedConfirmReceived(byte sequence) { }
        public void SolicitedConfirmWaitNewRequest() { }
        public void WrongSolicitedConfirmSeq(byte expected, byte received) { }
        public void UnexpectedConfirm(bool unsolicited, byte sequence) { }
        public void EnterUnsolicitedConfirmWait(byte sequence) { }
        public void UnsolicitedConfirmTimeout(byte sequence, bool retry) { }
        public void UnsolicitedConfirmed(byte sequence) { }
        public void ClearRestartIin() { }
    }

    private sealed class ControlCallbacks(bool allowWrites) : IControlHandler
    {
        public void BeginFragment() { }
        public void EndFragment(DatabaseHandle database) { }
        public CommandStatus SelectG12v1(Group12Var1 command, ushort index, DatabaseHandle database) => Status;
        public CommandStatus OperateG12v1(Group12Var1 command, ushort index, OperateType opType, DatabaseHandle database) => Status;
        public CommandStatus SelectG41v1(int value, ushort index, DatabaseHandle database) => Status;
        public CommandStatus OperateG41v1(int value, ushort index, OperateType opType, DatabaseHandle database) => Status;
        public CommandStatus SelectG41v2(short value, ushort index, DatabaseHandle database) => Status;
        public CommandStatus OperateG41v2(short value, ushort index, OperateType opType, DatabaseHandle database) => Status;
        public CommandStatus SelectG41v3(float value, ushort index, DatabaseHandle database) => Status;
        public CommandStatus OperateG41v3(float value, ushort index, OperateType opType, DatabaseHandle database) => Status;
        public CommandStatus SelectG41v4(double value, ushort index, DatabaseHandle database) => Status;
        public CommandStatus OperateG41v4(double value, ushort index, OperateType opType, DatabaseHandle database) => Status;
        private CommandStatus Status => allowWrites ? CommandStatus.Success : CommandStatus.NotAuthorized;
    }
}