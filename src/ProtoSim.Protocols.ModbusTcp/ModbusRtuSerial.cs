using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Settings for a Modbus RTU slave on a serial port (RS-232/RS-485, or a virtual COM port pair).</summary>
public sealed class ModbusRtuSerialSettings : ModbusDeviceSettings
{
    public ModbusRtuSerialSettings()
    {
        UnitIds = "1";
        UnmatchedUnitBehavior = UnmatchedUnitBehavior.Ignore;
        TreatUnitZeroAsBroadcast = true;
        ProductName = "Modbus RTU Simulator";
    }

    [Display(Name = "Serial port", GroupName = "Serial", Order = 1,
        Description = "COM port name, e.g. COM3. To test without hardware, create a virtual COM port pair (e.g. com0com) and connect the master to the other end.")]
    public string PortName { get; set; } = "COM1";

    [Display(Name = "Baud rate", GroupName = "Serial", Order = 2)]
    [Range(300, 921_600)]
    public int BaudRate { get; set; } = 19200;

    [Display(Name = "Data bits", GroupName = "Serial", Order = 3, Description = "Modbus RTU uses 8.")]
    [Range(7, 8)]
    public int DataBits { get; set; } = 8;

    [Display(Name = "Parity", GroupName = "Serial", Order = 4, Description = "The Modbus default is Even. With None, the standard asks for two stop bits, but many devices use one.")]
    public Parity Parity { get; set; } = Parity.Even;

    [Display(Name = "Stop bits", GroupName = "Serial", Order = 5)]
    public StopBits StopBits { get; set; } = StopBits.One;

    [Display(Name = "Frame gap (ms)", GroupName = "Serial", Order = 6,
        Description = "Silence that ends a frame. 0 = automatic: 3.5 character times (at least 2 ms; the PC's serial driver adds its own latency).")]
    [Range(0, 1000)]
    public int FrameGapMs { get; set; }

    [Display(Name = "Turnaround delay (ms)", GroupName = "Serial", Order = 7,
        Description = "Wait before replying, so RS-485 masters have time to switch to receive.")]
    [Range(0, 1000)]
    public int TurnaroundDelayMs { get; set; }

    internal string Describe() => $"{PortName} {BaudRate} {DataBits}{Parity.ToString()[0]}{(StopBits == StopBits.Two ? 2 : StopBits == StopBits.OnePointFive ? 1.5 : 1)}";

    /// <summary>The silent interval that ends a frame, in milliseconds.</summary>
    internal int EffectiveGapMs()
    {
        if (FrameGapMs > 0) return FrameGapMs;
        double charBits = 1 + DataBits + (Parity == Parity.None ? 0 : 1) + (StopBits == StopBits.Two ? 2 : 1);
        double t35 = BaudRate > 19200 ? 1.75 : 3.5 * charBits * 1000 / BaudRate;
        return Math.Max(2, (int)Math.Ceiling(t35));
    }
}

/// <summary>Modbus RTU slave on a serial port.</summary>
public sealed class ModbusRtuSerialServer : IProtocolServer
{
    private readonly ModbusRtuSerialSettings _settings;
    private readonly ModbusDataModel _model;
    private readonly ServerContext _ctx;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private SerialPort? _port;
    private Thread? _thread;
    private volatile bool _running;
    private ConnectionInfo? _connection;
    private long _nextConnectionId;

    public ModbusRtuSerialServer(ModbusRtuSerialSettings settings, ModbusDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _ctx = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    private ITrafficLog Log => _ctx.Log;

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
                var slave = new RtuSlave(_settings, _model);
                var port = new SerialPort(_settings.PortName.Trim(), _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = _settings.EffectiveGapMs(),
                    WriteTimeout = 2000,
                    ReadBufferSize = 4096,
                };
                port.Open();
                port.DiscardInBuffer();
                _port = port;
                _running = true;
                _connection = new ConnectionInfo(Interlocked.Increment(ref _nextConnectionId), _settings.Describe());
                _ctx.Statistics.OnConnected();
                _thread = new Thread(() => Run(port, slave)) { IsBackground = true, Name = $"Modbus RTU {_settings.PortName}" };
                _thread.Start();
                Log.Info(_settings.PortName, $"Modbus RTU slave on {_settings.Describe()} (units {_settings.UnitIds}, frame gap {_settings.EffectiveGapMs()} ms)");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                _running = false;
                _port?.Dispose();
                _port = null;
                LastError = ex switch
                {
                    UnauthorizedAccessException => $"{_settings.PortName} is in use by another program.",
                    FileNotFoundException or IOException when !SerialPort.GetPortNames().Contains(_settings.PortName.Trim(), StringComparer.OrdinalIgnoreCase) =>
                        $"{_settings.PortName} doesn't exist. Available ports: {AvailablePorts()}.",
                    _ => ex.Message,
                };
                Log.Error("", $"Start failed: {LastError}");
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
            if (_port == null) return;
            SetState(ServerState.Stopping);
            _running = false;
            var thread = _thread;
            if (thread != null) await Task.Run(() => thread.Join(TimeSpan.FromSeconds(3)));
            try { _port.Close(); } catch (Exception) { }
            _port.Dispose();
            _port = null;
            _thread = null;
            if (_connection != null)
            {
                _connection = null;
                _ctx.Statistics.OnDisconnected();
            }
            Log.Info("", "Serial port closed");
            SetState(ServerState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections() => _connection is { } c ? [c] : [];

    /// <summary>A serial line has no connection to drop.</summary>
    public void DropConnection(long connectionId) => Log.Warn(_settings.PortName, "A serial port has no connection to drop; stop the device to close the port.");

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private static string AvailablePorts()
    {
        var names = SerialPort.GetPortNames();
        return names.Length == 0 ? "none" : string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Run(SerialPort port, RtuSlave slave)
    {
        var assembler = new RtuFrameAssembler();
        var buffer = new byte[ModbusRtu.MaxAduLength];
        string remote = _settings.PortName;

        while (_running)
        {
            byte[]? frame = assembler.TryTakeComplete();
            if (frame == null)
            {
                if (assembler.Overflowed)
                {
                    _ctx.Statistics.OnMalformed();
                    Log.Error(remote, $"No valid frame within {ModbusRtu.MaxAduLength} bytes; discarding");
                    assembler.Clear();
                }
                try
                {
                    int read = port.Read(buffer, 0, buffer.Length);
                    assembler.Append(buffer.AsSpan(0, read));
                    continue;
                }
                catch (TimeoutException)
                {
                    // Silence: whatever is buffered is one frame.
                    frame = assembler.TakeOnSilence();
                    if (frame == null) continue;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (!_running) break;
                    LastError = $"Serial port error: {ex.Message}";
                    Log.Error(remote, LastError);
                    SetState(ServerState.Faulted);
                    break;
                }
            }

            try
            {
                HandleFrame(port, slave, remote, frame);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                if (!_running) break;
                Log.Error(remote, $"Write failed: {ex.Message}");
            }
        }
    }

    private void HandleFrame(SerialPort port, RtuSlave slave, string remote, byte[] frame)
    {
        _ctx.Statistics.OnRequest(frame.Length);
        _connection?.OnRequest();
        Log.Frame(TrafficDirection.Rx, remote, frame, () => ModbusRtu.Describe(frame, true));

        var d = slave.Classify(frame);
        byte[]? response = null;
        if (d == RtuDisposition.ForThisDevice)
        {
            var faults = _ctx.Faults;
            if (FaultSettings.Roll(faults.DropResponsePercent))
            {
                _ctx.Statistics.OnDropped();
                Log.Warn(remote, "Fault injection: response dropped");
                return;
            }
            int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
            if (delay > 0) Thread.Sleep(delay);

            if (FaultSettings.Roll(faults.ErrorResponsePercent))
            {
                Log.Warn(remote, $"Fault injection: exception {Math.Clamp(faults.ErrorCode, 1, 255):X2}");
                response = RtuSlave.Exception(frame, (ModbusExceptionCode)Math.Clamp(faults.ErrorCode, 1, 255));
            }
            else
            {
                response = slave.Execute(frame, d);
            }
        }
        else if (d == RtuDisposition.OtherUnit)
        {
            response = slave.OtherUnitReply(frame);
        }
        else if (d == RtuDisposition.Broadcast)
        {
            slave.Execute(frame, d);
        }

        if (response == null)
        {
            RtuLog.NotAnswered(Log, _ctx.Statistics, remote, frame, d);
            return;
        }

        if (_settings.TurnaroundDelayMs > 0) Thread.Sleep(_settings.TurnaroundDelayMs);
        port.Write(response, 0, response.Length);
        _ctx.Statistics.OnResponse(response.Length, RtuSlave.IsException(response));
        Log.Frame(TrafficDirection.Tx, remote, response, () => ModbusRtu.Describe(response, false));
    }
}
