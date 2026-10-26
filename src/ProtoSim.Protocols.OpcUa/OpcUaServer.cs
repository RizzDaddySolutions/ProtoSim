using Opc.Ua;
using Opc.Ua.Configuration;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ServerState = ProtoSim.Core.Protocols.ServerState;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>Runs one simulated OPC UA server (one endpoint) for a ProtoSim device.</summary>
public sealed class OpcUaServer(OpcUaServerSettings settings, OpcUaDataModel model, ServerContext context) : IProtocolServer
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<NodeId, ConnectionInfo> _sessions = [];
    private long _nextConnectionId;
    private ApplicationInstance? _application;
    private SimulationServer? _server;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    public string EndpointUrl => BuildEndpointUrl(settings);

    public static string BuildEndpointUrl(OpcUaServerSettings s)
    {
        var path = (s.EndpointPath ?? string.Empty).Trim();
        if (path.Length > 0 && !path.StartsWith('/')) path = "/" + path;
        return $"opc.tcp://{(string.IsNullOrWhiteSpace(s.HostName) ? "localhost" : s.HostName.Trim())}:{s.Port}{path}";
    }

    // The OPC UA stack does blocking work (certificates, sockets) and must not run on a UI thread.
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.Run(() => StartCoreAsync(cancellationToken), cancellationToken);

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (State == ServerState.Running) return;
            SetState(ServerState.Starting);
            LastError = null;
            try
            {
                if (!settings.AllowSecurityNone && !settings.AllowSign && !settings.AllowSignAndEncrypt)
                    throw new InvalidOperationException("Enable at least one security mode.");
                if (!settings.AllowAnonymous && settings.UserName.Length == 0)
                    throw new InvalidOperationException("Allow anonymous login or set a user name.");

                var config = await OpcUaConfiguration.CreateAsync(settings.ApplicationName, ApplicationType.Server,
                    settings.AutoAcceptUntrustedCertificates, Configure, cancellationToken);

                var application = new ApplicationInstance(config, OpcUaConfiguration.Telemetry)
                {
                    ApplicationName = settings.ApplicationName,
                    ApplicationType = ApplicationType.Server,
                };
                context.Log.Info("", "Checking application certificate (created on first start)…");
                if (!await application.CheckApplicationInstanceCertificatesAsync(false, null, cancellationToken))
                    throw new InvalidOperationException("The application certificate is not valid.");

                var server = new SimulationServer(model, settings, context);
                await application.StartAsync(server);
                _application = application;
                _server = server;

                context.Log.Info("", $"Listening on {EndpointUrl} ({DescribeSecurity()})");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                await CleanupAsync();
                LastError = ex is ServiceResultException sre && sre.InnerException is System.Net.Sockets.SocketException
                    ? $"Port {settings.Port} is already in use."
                    : ex.Message;
                context.Log.Error("", $"Start failed: {LastError}");
                SetState(ServerState.Faulted);
                throw new InvalidOperationException(LastError, ex);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void Configure(ApplicationConfiguration config)
    {
        var server = new ServerConfiguration
        {
            BaseAddresses = [EndpointUrl],
            MaxSessionCount = settings.MaxSessions,
            MinRequestThreadCount = 5,
            MaxRequestThreadCount = 100,
            MaxQueuedRequestCount = 2000,
            MinSessionTimeout = 10_000,
            MaxSessionTimeout = 3_600_000,
            MinSubscriptionLifetime = 10_000,
            MinPublishingInterval = Math.Max(10, settings.MinSamplingIntervalMs),
            DiagnosticsEnabled = true,
        };

        server.SecurityPolicies.Clear();
        if (settings.AllowSecurityNone)
            server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None });
        if (settings.AllowSign)
            server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
        if (settings.AllowSignAndEncrypt)
            server.SecurityPolicies.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });

        server.UserTokenPolicies.Clear();
        if (settings.AllowAnonymous) server.UserTokenPolicies.Add(new UserTokenPolicy(UserTokenType.Anonymous));
        if (settings.UserName.Length > 0)
            server.UserTokenPolicies.Add(new UserTokenPolicy(UserTokenType.UserName) { SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });

        config.ServerConfiguration = server;
    }

    private string DescribeSecurity()
    {
        var modes = new List<string>();
        if (settings.AllowSecurityNone) modes.Add("None");
        if (settings.AllowSign) modes.Add("Sign");
        if (settings.AllowSignAndEncrypt) modes.Add("SignAndEncrypt");
        var users = new List<string>();
        if (settings.AllowAnonymous) users.Add("anonymous");
        if (settings.UserName.Length > 0) users.Add($"user '{settings.UserName}'");
        return $"security {string.Join("/", modes)}; login {string.Join(", ", users)}";
    }

    public Task StopAsync() => Task.Run(StopCoreAsync);

    private async Task StopCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_application == null) return;
            SetState(ServerState.Stopping);
            await CleanupAsync();
            context.Log.Info("", "Server stopped");
            SetState(ServerState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task CleanupAsync()
    {
        var app = _application;
        var server = _server;
        _application = null;
        _server = null;
        server?.NodeManager?.Dispose();
        try { if (app != null) await app.StopAsync(); }
        catch (Exception ex) { context.Log.Warn("", $"Stop: {ex.Message}"); }
        server?.Dispose();
        lock (_sessions) _sessions.Clear();
    }

    public IReadOnlyList<ConnectionInfo> GetConnections()
    {
        var server = _server;
        if (server == null || State != ServerState.Running) return [];
        var sessions = server.CurrentInstance.SessionManager.GetSessions();
        lock (_sessions)
        {
            var live = new HashSet<NodeId>();
            var result = new List<ConnectionInfo>();
            foreach (var s in sessions)
            {
                live.Add(s.Id);
                var d = s.SessionDiagnostics;
                if (!_sessions.TryGetValue(s.Id, out var info))
                {
                    info = new ConnectionInfo(Interlocked.Increment(ref _nextConnectionId), SimulationServer.Describe(s),
                        d?.ClientConnectionTime.ToLocalTime() ?? DateTime.Now);
                    _sessions[s.Id] = info;
                }
                if (d != null) info.SetCounters(d.TotalRequestCount?.TotalCount ?? 0, d.ClientLastContactTime.ToLocalTime());
                result.Add(info);
            }
            foreach (var gone in _sessions.Keys.Where(k => !live.Contains(k)).ToList()) _sessions.Remove(gone);
            return result;
        }
    }

    public void DropConnection(long connectionId)
    {
        var server = _server;
        if (server == null) return;
        NodeId? id;
        lock (_sessions) id = _sessions.FirstOrDefault(kv => kv.Value.Id == connectionId).Key;
        if (id == null) return;
        context.Log.Warn("", "Session closed by user");
        server.CurrentInstance.SessionManager.CloseSession(id);
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
}
