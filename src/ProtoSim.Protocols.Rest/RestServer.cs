using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Rest;

/// <summary>
/// HTTP/1.1 server for the REST API simulator. Listens on the loopback interface only
/// (127.0.0.1 and ::1), so it can never be reached from another computer.
/// </summary>
public sealed class RestServer : IProtocolServer
{
    private const int MaxConnections = 200;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private readonly RestServerSettings _settings;
    private readonly ServerContext _ctx;
    private readonly RestRequestHandler _handler;
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptTasks = [];

    private CancellationTokenSource? _cts;
    private long _nextConnectionId;

    public RestServer(RestServerSettings settings, RestDataModel model, ServerContext context)
    {
        _settings = settings;
        _ctx = context;
        _handler = new RestRequestHandler(model);
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>Actual IPv4 endpoint once started (useful when Port = 0).</summary>
    public IPEndPoint? LocalEndPoint => _listeners.Count > 0 ? _listeners[0].LocalEndpoint as IPEndPoint : null;

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
                var v4 = new TcpListener(IPAddress.Loopback, _settings.Port);
                v4.Start(MaxConnections);
                _listeners.Add(v4);
                int port = ((IPEndPoint)v4.LocalEndpoint).Port;
                if (Socket.OSSupportsIPv6)
                {
                    // "localhost" may resolve to ::1 first; serve it too. Not fatal if IPv6 is unavailable.
                    try
                    {
                        var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
                        v6.Start(MaxConnections);
                        _listeners.Add(v6);
                    }
                    catch (SocketException ex)
                    {
                        Log.Warn("", $"Not listening on [::1]:{port}: {ex.Message}");
                    }
                }

                _cts = new CancellationTokenSource();
                foreach (var l in _listeners) _acceptTasks.Add(AcceptLoopAsync(l, _cts.Token));
                Log.Info("", $"REST API listening on {string.Join(", ", _listeners.Select(l => l.LocalEndpoint))} (loopback only)");
                Log.Info("", $"Base URL {_settings.BaseUrl} · token endpoint {_settings.TokenUrl} · authentication {(_settings.RequireAuthentication ? "required" : "off")}");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                foreach (var l in _listeners) l.Stop();
                _listeners.Clear();
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
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
            if (_listeners.Count == 0) return;
            SetState(ServerState.Stopping);
            _cts?.Cancel();
            foreach (var l in _listeners) l.Stop();
            foreach (var c in _connections.Values) c.Close();

            var tasks = _connections.Values.Select(c => c.Task).Concat(_acceptTasks);
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* connections are closing anyway */ }

            _listeners.Clear();
            _acceptTasks.Clear();
            _cts?.Dispose();
            _cts = null;
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
            if (_connections.Count >= MaxConnections)
            {
                Log.Warn(remote, $"Connection refused: limit of {MaxConnections} reached");
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

        try
        {
            conn.Client.NoDelay = true;
            var stream = conn.Client.GetStream();
            var reader = new HttpRequestReader(stream, remote);

            while (!token.IsCancellationRequested)
            {
                HttpRequest? request;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(IdleTimeout);
                    try
                    {
                        request = await reader.ReadAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        Log.Info(remote, $"Idle for {IdleTimeout.TotalSeconds:0} s, closing connection");
                        break;
                    }
                    catch (HttpParseException ex)
                    {
                        _ctx.Statistics.OnMalformed();
                        Log.Error(remote, $"Malformed request: {ex.Message}");
                        var bad = HttpResponse.Error(ex.Status, "bad_request", ex.Message, _settings.IndentJson).Serialize(false, false);
                        await stream.WriteAsync(bad, token);
                        _ctx.Statistics.OnResponse(bad.Length, true);
                        break;
                    }
                }
                if (request == null) break; // peer closed

                _ctx.Statistics.OnRequest(request.Length);
                conn.Info.OnRequest();
                Log.Frame(TrafficDirection.Rx, remote, request.Body, () => DescribeRequest(request));

                var faults = _ctx.Faults;
                if (FaultSettings.Roll(faults.DropResponsePercent))
                {
                    _ctx.Statistics.OnDropped();
                    Log.Warn(remote, "Fault injection: no response; closing connection");
                    break;
                }

                int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
                if (delay > 0) await Task.Delay(delay, token);

                HttpResponse response;
                if (request.Method != "OPTIONS" && FaultSettings.Roll(faults.ErrorResponsePercent))
                {
                    int status = InjectedStatus(faults.ErrorCode);
                    response = HttpResponse.Error(status, "injected_fault", $"Fault injection (error code {faults.ErrorCode}).", _settings.IndentJson);
                    Log.Warn(remote, $"Fault injection: {status} {HttpResponse.ReasonPhrase(status)}");
                }
                else
                {
                    response = _handler.Handle(request);
                }

                bool disconnect = FaultSettings.Roll(faults.DisconnectPercent);
                bool keepAlive = request.KeepAlive && !disconnect;
                var bytes = response.Serialize(request.Method == "HEAD", keepAlive);
                await stream.WriteAsync(bytes, token);
                _ctx.Statistics.OnResponse(bytes.Length, response.Status >= 400);
                Log.Frame(TrafficDirection.Tx, remote, request.Method == "HEAD" ? [] : response.Body, () => DescribeResponse(response));

                if (disconnect) Log.Warn(remote, "Fault injection: closing connection after response");
                if (!keepAlive) break;
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

    /// <summary>Maps the generic fault error code onto an HTTP status.</summary>
    internal static int InjectedStatus(int errorCode) => errorCode switch
    {
        1 => 405,
        2 => 404,
        3 => 400,
        6 => 503,
        >= 400 and <= 599 => errorCode,
        _ => 500,
    };

    private static string DescribeRequest(HttpRequest r)
    {
        var sb = new StringBuilder().Append(r.Method).Append(' ').Append(r.Target);
        if (r.Header("Authorization") is { } auth)
            sb.Append(auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? " · bearer token" : " · " + auth.Split(' ')[0] + " auth");
        if (r.Body.Length > 0) sb.Append(" · ").Append(Preview(r.BodyText));
        return sb.ToString();
    }

    private static string DescribeResponse(HttpResponse r)
    {
        var sb = new StringBuilder().Append(r.Status).Append(' ').Append(HttpResponse.ReasonPhrase(r.Status));
        if (r.Note != null) sb.Append(" · ").Append(r.Note);
        if (r.Body.Length > 0) sb.Append(" · ").Append(Preview(r.BodyText));
        return sb.ToString();
    }

    private static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        return flat.Length <= 300 ? flat : flat[..300] + "…";
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
