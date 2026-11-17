using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Script;

/// <summary>Scripted device over TCP: one request/reply conversation per connection, plus greeting and periodic output.</summary>
public sealed class ScriptTcpServer : TcpFrameServer
{
    private readonly ScriptSettings _settings;
    private readonly ScriptDataModel _model;
    private readonly ConcurrentDictionary<Session, byte> _sessions = new();
    private ScriptRuntime? _runtime;
    private Task? _periodic;

    public ScriptTcpServer(ScriptSettings settings, ScriptDataModel model, ServerContext context) : base(settings, context)
    {
        _settings = settings;
        _model = model;
    }

    protected override string ProtocolName => "Scripted device (TCP)";

    protected override Task OnStartedAsync(IPAddress bindAddress, int port, CancellationToken serverToken)
    {
        _runtime = new ScriptRuntime(_settings, _model, Context);
        Log.Info("", $"{_runtime.Engine.Rules.Count} rule(s) loaded");
        if (_settings.PeriodMs > 0 && !string.IsNullOrEmpty(_settings.PeriodicMessage))
            _periodic = Task.Run(() => PeriodicAsync(serverToken));
        return Task.CompletedTask;
    }

    protected override async Task OnStoppingAsync()
    {
        if (_periodic != null)
        {
            try { await _periodic.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            _periodic = null;
        }
    }

    private async Task PeriodicAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, _settings.PeriodMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_sessions.IsEmpty || _runtime?.Message(_settings.PeriodicMessage, "periodic") is not { } bytes) continue;
                foreach (var s in _sessions.Keys)
                {
                    if (s.Push is not { } push) continue;
                    try { await push(bytes, ct); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    protected override TcpSession CreateSession(string remoteEndPoint)
    {
        var s = new Session(this, _runtime!, remoteEndPoint);
        _sessions[s] = 0;
        return s;
    }

    private sealed class Session(ScriptTcpServer server, ScriptRuntime runtime, string remote) : TcpSession
    {
        private readonly ScriptFramer _framer = runtime.NewFramer();
        private readonly byte[] _buffer = new byte[4096];

        public override byte[]? Greeting => runtime.Message(server._settings.Greeting, remote);

        public override void OnClosed() => server._sessions.TryRemove(this, out _);

        public override async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_framer.TakeComplete() is { } frame) return frame;
                if (_framer.Count > ScriptFramer.MaxRequest)
                {
                    _framer.Clear();
                    throw new ProtocolFrameException($"No request terminator within {ScriptFramer.MaxRequest} bytes");
                }

                int read;
                if (_framer.UsesSilence && _framer.Count > 0)
                {
                    using var gap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gap.CancelAfter(server._settings.FrameGapMs);
                    try { read = await stream.ReadAsync(_buffer, gap.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return _framer.TakeOnSilence(); }
                }
                else
                {
                    read = await stream.ReadAsync(_buffer, cancellationToken);
                }
                if (read == 0) return _framer.TakeOnSilence();
                _framer.Append(_buffer.AsSpan(0, read));
            }
        }

        public override FrameResult Process(byte[] request)
        {
            var (bytes, isError) = runtime.Reply(request, remote);
            return new FrameResult(bytes, isError);
        }

        public override byte[]? BuildFault(byte[] request, int errorCode) => runtime.Fault(errorCode);

        public override string Describe(byte[] frame, bool isRequest) => runtime.Describe(frame, isRequest);
    }
}

/// <summary>Scripted device over UDP: each datagram (or each line in it) is a request; replies go back to the sender.</summary>
public sealed class ScriptUdpServer : IProtocolServer
{
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromMinutes(10);

    private readonly ScriptSettings _settings;
    private readonly ScriptDataModel _model;
    private readonly ServerContext _ctx;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, (IPEndPoint EndPoint, ConnectionInfo Info)> _peers = new(StringComparer.Ordinal);
    private long _nextPeerId;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop, _periodic;
    private ScriptRuntime? _runtime;

    public ScriptUdpServer(ScriptSettings settings, ScriptDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _ctx = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;
    public IPEndPoint? LocalEndPoint => _udp?.Client.LocalEndPoint as IPEndPoint;

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
                _runtime = new ScriptRuntime(_settings, _model, _ctx);
                var ip = TcpFrameServer.ParseBindAddress(_settings.BindAddress);
                var udp = new UdpClient(ip.AddressFamily);
                if (OperatingSystem.IsWindows()) udp.Client.IOControl(-1744830452, [0, 0, 0, 0], null); // ignore ICMP port unreachable
                udp.Client.Bind(new IPEndPoint(ip, _settings.Port));
                _udp = udp;
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => ReceiveAsync(udp, _cts.Token));
                if (_settings.PeriodMs > 0 && !string.IsNullOrEmpty(_settings.PeriodicMessage)) _periodic = Task.Run(() => PeriodicAsync(udp, _cts.Token));
                _ctx.Log.Info("", $"Scripted device listening on {udp.Client.LocalEndPoint}/udp, {_runtime.Engine.Rules.Count} rule(s)");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                _udp?.Dispose();
                _udp = null;
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                    ? $"UDP port {_settings.Port} is already in use." : ex.Message;
                _ctx.Log.Error("", $"Start failed: {LastError}");
                SetState(ServerState.Faulted);
                throw new InvalidOperationException(LastError, ex);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_udp == null) return;
            SetState(ServerState.Stopping);
            _cts?.Cancel();
            _udp.Dispose();
            try { await Task.WhenAll(new[] { _loop, _periodic }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            _udp = null;
            _cts?.Dispose();
            _cts = null;
            _peers.Clear();
            _ctx.Log.Info("", "Stopped");
            SetState(ServerState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections()
    {
        var cutoff = DateTime.Now - PeerExpiry;
        foreach (var (k, p) in _peers) if (p.Info.LastActivity < cutoff) _peers.TryRemove(k, out _);
        return _peers.Values.Select(p => p.Info).OrderBy(i => i.Id).ToList();
    }

    public void DropConnection(long connectionId)
    {
        var e = _peers.FirstOrDefault(p => p.Value.Info.Id == connectionId);
        if (e.Key != null) _peers.TryRemove(e.Key, out _);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private void SetState(ServerState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReceiveAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            string remote = packet.RemoteEndPoint.ToString();
            var peer = _peers.GetOrAdd(remote, _ => (packet.RemoteEndPoint, new ConnectionInfo(Interlocked.Increment(ref _nextPeerId), remote)));
            var framer = _runtime!.NewFramer();
            framer.Append(packet.Buffer);
            var frames = new List<byte[]>();
            while (framer.TakeComplete() is { } f) frames.Add(f);
            if (framer.TakeOnSilence() is { } last) frames.Add(last);

            foreach (var frame in frames)
            {
                _ctx.Statistics.OnRequest(frame.Length);
                peer.Info.OnRequest();
                _ctx.Log.Frame(TrafficDirection.Rx, remote, frame, () => _runtime.Describe(frame, true));
                try
                {
                    var (bytes, isError) = await _runtime.ReplyWithFaultsAsync(frame, remote, ct);
                    if (bytes == null) continue;
                    await udp.SendAsync(bytes, packet.RemoteEndPoint, ct);
                    _ctx.Statistics.OnResponse(bytes.Length, isError);
                    _ctx.Log.Frame(TrafficDirection.Tx, remote, bytes, () => _runtime.Describe(bytes, false));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
        }
    }

    private async Task PeriodicAsync(UdpClient udp, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, _settings.PeriodMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var peers = GetConnections();
                if (peers.Count == 0 || _runtime?.Message(_settings.PeriodicMessage, "periodic") is not { } bytes) continue;
                foreach (var p in _peers.Values)
                {
                    try
                    {
                        await udp.SendAsync(bytes, p.EndPoint, ct);
                        _ctx.Statistics.OnResponse(bytes.Length, false);
                        _ctx.Log.Frame(TrafficDirection.Tx, p.Info.RemoteEndPoint, bytes, () => _runtime.Describe(bytes, false));
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Scripted device on a serial port.</summary>
public sealed class ScriptSerialServer : IProtocolServer
{
    private readonly ScriptSettings _settings;
    private readonly ScriptDataModel _model;
    private readonly ServerContext _ctx;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _writeLock = new();
    private SerialPort? _port;
    private Thread? _thread;
    private Timer? _timer;
    private volatile bool _running;
    private ConnectionInfo? _connection;
    private ScriptRuntime? _runtime;

    public ScriptSerialServer(ScriptSettings settings, ScriptDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _ctx = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    private string Line => $"{_settings.PortName} {_settings.BaudRate} {_settings.DataBits}{_settings.Parity.ToString()[0]}{(_settings.StopBits == StopBits.Two ? 2 : 1)}";

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
                _runtime = new ScriptRuntime(_settings, _model, _ctx);
                var port = new SerialPort(_settings.PortName.Trim(), _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = Math.Max(5, _settings.FrameGapMs),
                    WriteTimeout = 2000,
                };
                port.Open();
                port.DiscardInBuffer();
                _port = port;
                _running = true;
                _connection = new ConnectionInfo(1, Line);
                _ctx.Statistics.OnConnected();
                _thread = new Thread(() => Run(port)) { IsBackground = true, Name = $"Script {_settings.PortName}" };
                _thread.Start();
                if (_settings.PeriodMs > 0 && !string.IsNullOrEmpty(_settings.PeriodicMessage))
                    _timer = new Timer(_ => Periodic(port), null, _settings.PeriodMs, _settings.PeriodMs);
                _ctx.Log.Info(_settings.PortName, $"Scripted device on {Line}, {_runtime.Engine.Rules.Count} rule(s)");
                if (_runtime.Message(_settings.Greeting, _settings.PortName) is { } greeting) Write(port, greeting, false);
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
                    IOException or ArgumentException when !SerialPort.GetPortNames().Contains(_settings.PortName.Trim(), StringComparer.OrdinalIgnoreCase) =>
                        $"{_settings.PortName} doesn't exist. Available ports: {string.Join(", ", SerialPort.GetPortNames().DefaultIfEmpty("none"))}.",
                    _ => ex.Message,
                };
                _ctx.Log.Error("", $"Start failed: {LastError}");
                SetState(ServerState.Faulted);
                throw new InvalidOperationException(LastError, ex);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_port == null) return;
            SetState(ServerState.Stopping);
            _running = false;
            _timer?.Dispose();
            _timer = null;
            var t = _thread;
            if (t != null) await Task.Run(() => t.Join(TimeSpan.FromSeconds(3)));
            try { _port.Close(); } catch (Exception) { }
            _port.Dispose();
            _port = null;
            if (_connection != null)
            {
                _connection = null;
                _ctx.Statistics.OnDisconnected();
            }
            _ctx.Log.Info("", "Serial port closed");
            SetState(ServerState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections() => _connection is { } c ? [c] : [];
    public void DropConnection(long connectionId) => _ctx.Log.Warn(_settings.PortName, "A serial port has no connection to drop; stop the device to close the port.");

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private void SetState(ServerState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Write(SerialPort port, byte[] bytes, bool isError)
    {
        lock (_writeLock) port.Write(bytes, 0, bytes.Length);
        _ctx.Statistics.OnResponse(bytes.Length, isError);
        _ctx.Log.Frame(TrafficDirection.Tx, _settings.PortName, bytes, () => _runtime!.Describe(bytes, false));
    }

    private void Periodic(SerialPort port)
    {
        if (!_running) return;
        try
        {
            if (_runtime?.Message(_settings.PeriodicMessage, _settings.PortName) is { } bytes) Write(port, bytes, false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
    }

    private void Run(SerialPort port)
    {
        var framer = _runtime!.NewFramer();
        var buffer = new byte[4096];
        string remote = _settings.PortName;
        while (_running)
        {
            byte[]? frame = framer.TakeComplete();
            if (frame == null)
            {
                if (framer.Count > ScriptFramer.MaxRequest)
                {
                    _ctx.Statistics.OnMalformed();
                    framer.Clear();
                }
                try
                {
                    int n = port.Read(buffer, 0, buffer.Length);
                    framer.Append(buffer.AsSpan(0, n));
                    continue;
                }
                catch (TimeoutException)
                {
                    if (!framer.UsesSilence) continue;
                    frame = framer.TakeOnSilence();
                    if (frame == null) continue;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (!_running) break;
                    LastError = $"Serial port error: {ex.Message}";
                    _ctx.Log.Error(remote, LastError);
                    SetState(ServerState.Faulted);
                    break;
                }
            }

            _ctx.Statistics.OnRequest(frame.Length);
            _connection?.OnRequest();
            var f = frame;
            _ctx.Log.Frame(TrafficDirection.Rx, remote, f, () => _runtime.Describe(f, true));
            try
            {
                var (bytes, isError) = _runtime.ReplyWithFaultsAsync(frame, remote, CancellationToken.None).GetAwaiter().GetResult();
                if (bytes != null) Write(port, bytes, isError);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                if (!_running) break;
                _ctx.Log.Error(remote, $"Write failed: {ex.Message}");
            }
        }
    }
}
