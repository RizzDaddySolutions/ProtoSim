using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Modbus TCP server (slave) serving a <see cref="ModbusDataModel"/>.</summary>
public sealed class ModbusTcpServer : IProtocolServer
{
    private const int HeaderLength = 7;

    private readonly ModbusServerSettings _settings;
    private readonly ServerContext _ctx;
    private readonly ModbusRequestHandler _handler;
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool[] _units = UnitFilter.All;
    private long _nextConnectionId;

    public ModbusTcpServer(ModbusServerSettings settings, ModbusDataModel model, ServerContext context)
    {
        _settings = settings;
        _ctx = context;
        _handler = new ModbusRequestHandler(model, settings);
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>Actual endpoint once started (useful when Port = 0).</summary>
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;

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
                _units = UnitFilter.Parse(_settings.UnitIds);
                var ip = ParseBindAddress(_settings.BindAddress);
                var listener = new TcpListener(ip, _settings.Port);
                if (ip.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
                listener.Start(_settings.MaxConnections);
                _listener = listener;
                _cts = new CancellationTokenSource();
                _acceptTask = AcceptLoopAsync(listener, _cts.Token);
                Log.Info("", $"Listening on {listener.LocalEndpoint} (units {_settings.UnitIds})");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                _listener?.Stop();
                _listener = null;
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Port {_settings.Port} is already in use."
                    : ex.Message;
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
            if (_listener == null) return;
            SetState(ServerState.Stopping);
            _cts?.Cancel();
            _listener.Stop();
            foreach (var c in _connections.Values) c.Close();

            var tasks = _connections.Values.Select(c => c.Task).Append(_acceptTask ?? Task.CompletedTask);
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* connections are closing anyway */ }

            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _acceptTask = null;
            Log.Info("", "Server stopped");
            SetState(ServerState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections() => _connections.Values.Select(c => c.Info).ToList();

    public void DropConnection(long connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var c))
        {
            Log.Warn(c.Info.RemoteEndPoint, "Connection dropped by user");
            c.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IPAddress ParseBindAddress(string text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s is "" or "*" or "0.0.0.0") return IPAddress.Any;
        if (s is "::") return IPAddress.IPv6Any;
        if (s.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        return IPAddress.TryParse(s, out var ip) ? ip : throw new FormatException($"'{text}' is not a valid IP address.");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Error("", $"Accept failed: {ex.Message}");
                continue;
            }

            string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            if (_connections.Count >= _settings.MaxConnections)
            {
                Log.Warn(remote, $"Connection refused: limit of {_settings.MaxConnections} reached");
                client.Dispose();
                continue;
            }

            var conn = new Connection(Interlocked.Increment(ref _nextConnectionId), client, remote);
            _connections[conn.Info.Id] = conn;
            _ctx.Statistics.OnConnected();
            conn.Task = Task.Run(() => HandleConnectionAsync(conn, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Connection conn, CancellationToken serverToken)
    {
        string remote = conn.Info.RemoteEndPoint;
        Log.Info(remote, "Client connected");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, conn.Cts.Token);
        var token = linked.Token;
        var frame = new byte[HeaderLength + ModbusLimits.MaxPduLength];

        try
        {
            conn.Client.NoDelay = true;
            var stream = conn.Client.GetStream();

            while (!token.IsCancellationRequested)
            {
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (_settings.IdleTimeoutSeconds > 0) idle.CancelAfter(TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds));
                    try
                    {
                        read = await stream.ReadAtLeastAsync(frame.AsMemory(0, HeaderLength), HeaderLength, false, idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        Log.Warn(remote, $"Idle timeout ({_settings.IdleTimeoutSeconds} s), closing connection");
                        break;
                    }
                }
                if (read < HeaderLength) break; // peer closed

                ushort tid = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0));
                ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
                byte unit = frame[6];

                if (protocolId != 0 || length < 2 || length > ModbusLimits.MaxPduLength + 1)
                {
                    _ctx.Statistics.OnMalformed();
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Error, remote,
                        $"Invalid MBAP header (protocol {protocolId}, length {length}); closing connection",
                        frame[..HeaderLength]));
                    break;
                }

                read = await stream.ReadAtLeastAsync(frame.AsMemory(HeaderLength, length - 1), length - 1, false, token);
                if (read < length - 1) break;

                int total = HeaderLength + length - 1;
                var request = frame.AsSpan(0, total).ToArray();
                _ctx.Statistics.OnRequest(total);
                conn.Info.OnRequest();
                Log.Frame(TrafficDirection.Rx, remote, request, () => ModbusFrameFormatter.Describe(request, true));

                var responsePdu = await ProduceResponseAsync(remote, unit, request.AsMemory(HeaderLength), token);
                if (responsePdu == null) continue;

                var response = new byte[HeaderLength + responsePdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0), tid);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(responsePdu.Length + 1));
                response[6] = unit;
                responsePdu.CopyTo(response, HeaderLength);

                await stream.WriteAsync(response, token);
                _ctx.Statistics.OnResponse(response.Length, (responsePdu[0] & 0x80) != 0);
                Log.Frame(TrafficDirection.Tx, remote, response, () => ModbusFrameFormatter.Describe(response, false));

                if (FaultSettings.Roll(_ctx.Faults.DisconnectPercent))
                {
                    Log.Warn(remote, "Fault injection: closing connection after response");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Error(remote, $"Connection error: {ex.Message}");
        }
        finally
        {
            _connections.TryRemove(conn.Info.Id, out _);
            conn.Close();
            _ctx.Statistics.OnDisconnected();
            Log.Info(remote, "Client disconnected");
        }
    }

    /// <summary>Applies unit filtering and fault injection, then runs the request. Null means "send nothing".</summary>
    private async Task<byte[]?> ProduceResponseAsync(string remote, byte unit, ReadOnlyMemory<byte> pdu, CancellationToken token)
    {
        byte fc = pdu.Span[0];
        bool broadcast = unit == 0 && _settings.TreatUnitZeroAsBroadcast;

        if (!broadcast && !_units[unit])
        {
            switch (_settings.UnmatchedUnitBehavior)
            {
                case UnmatchedUnitBehavior.Ignore:
                    _ctx.Statistics.OnDropped();
                    Log.Info(remote, $"Unit {unit} is not served by this device; no response");
                    return null;
                case UnmatchedUnitBehavior.GatewayPathUnavailable:
                    return ModbusRequestHandler.BuildException(fc, ModbusExceptionCode.GatewayPathUnavailable);
                default:
                    return ModbusRequestHandler.BuildException(fc, ModbusExceptionCode.GatewayTargetFailedToRespond);
            }
        }

        var faults = _ctx.Faults;
        if (FaultSettings.Roll(faults.DropResponsePercent))
        {
            _ctx.Statistics.OnDropped();
            Log.Warn(remote, "Fault injection: response dropped");
            return null;
        }

        int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
        if (delay > 0) await Task.Delay(delay, token);

        byte[] response;
        if (FaultSettings.Roll(faults.ErrorResponsePercent))
        {
            var code = (ModbusExceptionCode)Math.Clamp(faults.ErrorCode, 1, 255);
            Log.Warn(remote, $"Fault injection: exception {(byte)code:X2} {ModbusNames.Describe(code)}");
            response = ModbusRequestHandler.BuildException(fc, code);
        }
        else
        {
            response = _handler.Process(pdu.Span);
        }

        if (broadcast)
        {
            Log.Info(remote, "Broadcast request processed; no response sent");
            return null;
        }
        return response;
    }

    private sealed class Connection(long id, TcpClient client, string remote)
    {
        public ConnectionInfo Info { get; } = new(id, remote);
        public TcpClient Client { get; } = client;
        public CancellationTokenSource Cts { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;

        public void Close()
        {
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
            Client.Dispose();
        }
    }
}

/// <summary>Parses unit ID lists like "*", "1", "1,2,10-20".</summary>
public static class UnitFilter
{
    public static readonly bool[] All = Enumerable.Repeat(true, 256).ToArray();

    public static bool[] Parse(string? spec)
    {
        var s = (spec ?? string.Empty).Trim();
        if (s is "" or "*") return All;

        var result = new bool[256];
        foreach (var raw in s.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            int dash = part.IndexOf('-');
            int from, to;
            if (dash > 0)
            {
                from = ParseId(part[..dash]);
                to = ParseId(part[(dash + 1)..]);
            }
            else
            {
                from = to = ParseId(part);
            }
            if (from > to) (from, to) = (to, from);
            for (int i = from; i <= to; i++) result[i] = true;
        }
        return result;
    }

    private static int ParseId(string s) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= 255
            ? v
            : throw new FormatException($"'{s}' is not a valid unit ID (0-255).");
}
