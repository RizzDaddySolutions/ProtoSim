using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ProtoSim.Core.Diagnostics;

namespace ProtoSim.Core.Protocols;

/// <summary>Network settings shared by request/response TCP servers.</summary>
public interface ITcpServerSettings
{
    string BindAddress { get; }
    int Port { get; }
    int MaxConnections { get; }
    int IdleTimeoutSeconds { get; }
}

/// <summary>Thrown by <see cref="TcpSession.ReadFrameAsync"/> when the peer sends something unparseable; the connection closes.</summary>
public sealed class ProtocolFrameException(string message, byte[]? response = null) : Exception(message)
{
    /// <summary>Optional reply sent before closing.</summary>
    public byte[]? Response { get; } = response;
}

/// <summary>Result of processing one request frame.</summary>
/// <param name="Response">Bytes to send, or null to send nothing.</param>
/// <param name="IsError">Counted as an exception/error response in the statistics.</param>
/// <param name="Close">Close the connection after sending.</param>
public readonly record struct FrameResult(byte[]? Response, bool IsError = false, bool Close = false);

/// <summary>Per-connection protocol state for a <see cref="TcpFrameServer"/>.</summary>
public abstract class TcpSession
{
    /// <summary>Reads one complete request frame, or null when the peer closed the connection cleanly.</summary>
    public abstract ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken);

    public abstract FrameResult Process(byte[] request);

    /// <summary>A protocol error reply for fault injection, or null if this request can't carry one (it's then processed normally).</summary>
    public abstract byte[]? BuildFault(byte[] request, int errorCode);

    /// <summary>False for handshake frames that fault injection should leave alone.</summary>
    public virtual bool CanInjectFault(byte[] request) => true;

    public abstract string Describe(byte[] frame, bool isRequest);

    /// <summary>Bytes sent once when the client connects (a banner or prompt), or null.</summary>
    public virtual byte[]? Greeting => null;

    /// <summary>
    /// Sends bytes outside the request/response cycle (unsolicited output). Set by the server once the
    /// connection is up; safe to call from any thread.
    /// </summary>
    public Func<byte[], CancellationToken, Task>? Push { get; internal set; }

    /// <summary>Called once when the connection has closed.</summary>
    public virtual void OnClosed() { }

    /// <summary>Reads exactly <paramref name="count"/> bytes. Null if the stream ended before the first byte; throws if it ends mid-frame.</summary>
    protected static async ValueTask<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken ct, byte[]? prefix = null)
    {
        int offset = prefix?.Length ?? 0;
        var buffer = new byte[offset + count];
        prefix?.CopyTo(buffer, 0);
        int read = await stream.ReadAtLeastAsync(buffer.AsMemory(offset), count, false, ct);
        if (read == 0 && offset == 0) return null;
        if (read < count) throw new EndOfStreamException();
        return buffer;
    }
}

/// <summary>
/// Base class for request/response protocols over TCP (S7, EtherNet/IP, MC, FINS...).
/// Handles listening, connection tracking, idle timeouts, statistics, traffic logging and fault injection.
/// </summary>
public abstract class TcpFrameServer : IProtocolServer
{
    private readonly ITcpServerSettings _settings;
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private long _nextConnectionId;

    protected TcpFrameServer(ITcpServerSettings settings, ServerContext context)
    {
        _settings = settings;
        Context = context;
    }

    protected ServerContext Context { get; }
    protected ITrafficLog Log => Context.Log;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>Actual endpoint once started (useful when Port = 0).</summary>
    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>Protocol name used in log messages.</summary>
    protected abstract string ProtocolName { get; }

    protected abstract TcpSession CreateSession(string remoteEndPoint);

    /// <summary>Called after the TCP listener started, e.g. to start a UDP endpoint too.</summary>
    protected virtual Task OnStartedAsync(IPAddress bindAddress, int port, CancellationToken serverToken) => Task.CompletedTask;

    /// <summary>Called while stopping, after the listener closed.</summary>
    protected virtual Task OnStoppingAsync() => Task.CompletedTask;

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
                var ip = ParseBindAddress(_settings.BindAddress);
                var listener = new TcpListener(ip, _settings.Port);
                if (ip.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
                listener.Start(Math.Max(1, _settings.MaxConnections));
                _listener = listener;
                _cts = new CancellationTokenSource();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                await OnStartedAsync(ip, port, _cts.Token);
                _acceptTask = AcceptLoopAsync(listener, _cts.Token);
                Log.Info("", $"{ProtocolName} listening on {listener.LocalEndpoint}");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener = null;
                try { await OnStoppingAsync(); } catch (Exception) { }
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
            if (_listener == null) return;
            SetState(ServerState.Stopping);
            _cts?.Cancel();
            _listener.Stop();
            try { await OnStoppingAsync(); } catch (Exception ex) { Log.Warn("", $"Stop: {ex.Message}"); }
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
        GC.SuppressFinalize(this);
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static IPAddress ParseBindAddress(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s is "" or "*" or "0.0.0.0") return IPAddress.Any;
        if (s is "::") return IPAddress.IPv6Any;
        if (s.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        return IPAddress.TryParse(s, out var ip) ? ip : throw new FormatException($"'{text}' is not a valid IP address.");
    }

    /// <summary>Delay configured by fault injection (fixed + jitter).</summary>
    protected int FaultDelayMs()
    {
        var f = Context.Faults;
        return Math.Max(0, f.ResponseDelayMs) + (f.DelayJitterMs > 0 ? Random.Shared.Next(0, f.DelayJitterMs + 1) : 0);
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
            if (_connections.Count >= Math.Max(1, _settings.MaxConnections))
            {
                Log.Warn(remote, $"Connection refused: limit of {_settings.MaxConnections} reached");
                client.Dispose();
                continue;
            }

            var conn = new Connection(Interlocked.Increment(ref _nextConnectionId), client, remote);
            _connections[conn.Info.Id] = conn;
            Context.Statistics.OnConnected();
            conn.Task = Task.Run(() => HandleConnectionAsync(conn, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Connection conn, CancellationToken serverToken)
    {
        string remote = conn.Info.RemoteEndPoint;
        Log.Info(remote, "Client connected");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, conn.Cts.Token);
        var token = linked.Token;
        using var writeLock = new SemaphoreSlim(1, 1);
        TcpSession? session = null;

        try
        {
            conn.Client.NoDelay = true;
            var stream = conn.Client.GetStream();
            session = CreateSession(remote);
            var s = session;

            async Task WriteAsync(byte[] bytes, bool isError, CancellationToken ct)
            {
                await writeLock.WaitAsync(ct);
                try { await stream.WriteAsync(bytes, ct); }
                finally { writeLock.Release(); }
                Context.Statistics.OnResponse(bytes.Length, isError);
                Log.Frame(TrafficDirection.Tx, remote, bytes, () => s.Describe(bytes, false));
            }

            session.Push = (bytes, ct) => WriteAsync(bytes, false, ct);
            if (session.Greeting is { Length: > 0 } greeting) await WriteAsync(greeting, false, token);

            while (!token.IsCancellationRequested)
            {
                byte[]? request;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (_settings.IdleTimeoutSeconds > 0) idle.CancelAfter(TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds));
                    try
                    {
                        request = await session.ReadFrameAsync(stream, idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        Log.Warn(remote, $"Idle timeout ({_settings.IdleTimeoutSeconds} s), closing connection");
                        break;
                    }
                    catch (ProtocolFrameException ex)
                    {
                        Context.Statistics.OnMalformed();
                        Log.Error(remote, $"Malformed frame: {ex.Message}; closing connection");
                        if (ex.Response != null)
                        {
                            await stream.WriteAsync(ex.Response, token);
                            Context.Statistics.OnResponse(ex.Response.Length, true);
                        }
                        break;
                    }
                }
                if (request == null) break; // peer closed

                Context.Statistics.OnRequest(request.Length);
                conn.Info.OnRequest();
                Log.Frame(TrafficDirection.Rx, remote, request, () => session.Describe(request, true));

                var faults = Context.Faults;
                bool faultable = session.CanInjectFault(request);
                if (faultable && FaultSettings.Roll(faults.DropResponsePercent))
                {
                    Context.Statistics.OnDropped();
                    Log.Warn(remote, "Fault injection: response dropped");
                    continue;
                }

                int delay = faultable ? FaultDelayMs() : 0;
                if (delay > 0) await Task.Delay(delay, token);

                FrameResult result;
                byte[]? fault = faultable && FaultSettings.Roll(faults.ErrorResponsePercent) ? session.BuildFault(request, faults.ErrorCode) : null;
                if (fault != null)
                {
                    Log.Warn(remote, $"Fault injection: error response (code {faults.ErrorCode})");
                    result = new FrameResult(fault, true);
                }
                else
                {
                    try
                    {
                        result = session.Process(request);
                    }
                    catch (ProtocolFrameException ex)
                    {
                        Context.Statistics.OnMalformed();
                        Log.Error(remote, $"Malformed frame: {ex.Message}; closing connection");
                        result = new FrameResult(ex.Response, true, true);
                    }
                }

                if (result.Response is { Length: > 0 } response) await WriteAsync(response, result.IsError, token);

                if (result.Close) break;
                if (faultable && FaultSettings.Roll(faults.DisconnectPercent))
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
            if (session != null)
            {
                session.Push = null;
                try { session.OnClosed(); } catch (Exception ex) { Log.Warn(remote, $"Session cleanup: {ex.Message}"); }
            }
            _connections.TryRemove(conn.Info.Id, out _);
            conn.Close();
            Context.Statistics.OnDisconnected();
            Log.Info(remote, "Client disconnected");
        }
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
