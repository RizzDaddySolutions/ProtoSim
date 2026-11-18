using Opc.Ua;
using Opc.Ua.Identity;
using Opc.Ua.Server;
using System.Text;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>OPC Foundation StandardServer wired to ProtoSim's data model, log, statistics and fault injection.</summary>
internal sealed class SimulationServer : StandardServer
{
    private readonly OpcUaServerSettings settings;
    private readonly ServerContext context;

    public SimulationServer(OpcUaDataModel model, OpcUaServerSettings settings, ServerContext context) : base(OpcUaConfiguration.Telemetry)
    {
        this.settings = settings;
        this.context = context;
        // SDK 2.0.2+ requires application node managers to be registered as factories before the server starts.
        AddNodeManager(new NodeManagerFactory(this, model, settings));
    }

    public SimulationNodeManager? NodeManager { get; private set; }

    /// <summary>Creates the simulation node manager when the server starts.</summary>
    private sealed class NodeManagerFactory(SimulationServer owner, OpcUaDataModel model, OpcUaServerSettings settings) : INodeManagerFactory
    {
        public ArrayOf<string> NamespacesUris => new[] { settings.NamespaceUri };

        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            var manager = new SimulationNodeManager(server, configuration, model, settings);
            owner.NodeManager = manager;
            return manager;
        }
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "ProtoSim",
        ProductName = settings.ApplicationName,
        ProductUri = "urn:ProtoSim",
        SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
        BuildNumber = Utils.GetAssemblyBuildNumber(),
        BuildDate = Utils.GetAssemblyTimestamp(),
    };

    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);
        server.IdentityRegistry.Register(new AnonymousAuthenticator(settings.AllowAnonymous));
        server.IdentityRegistry.Register(new UserNameAuthenticator(settings, context));
        server.SessionManager.SessionCreated += (session, _) =>
        {
            context.Statistics.OnConnected();
            context.Log.Info(Describe(session), "Session created");
        };
        server.SessionManager.SessionClosing += (session, _) =>
        {
            context.Statistics.OnDisconnected();
            context.Log.Info(Describe(session), "Session closed");
        };
    }

    private sealed class AnonymousAuthenticator(bool allowAnonymous) : IUserTokenAuthenticator
    {
        public UserTokenType TokenType => UserTokenType.Anonymous;
        public string? IssuedTokenProfileUri => null;

        public ValueTask<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken ct)
        {
            if (allowAnonymous)
                return ValueTask.FromResult(AuthenticationResult.Accept(new UserIdentity()));

            return ValueTask.FromResult(AuthenticationResult.Reject(
                ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Anonymous authentication is not allowed.").StatusCode));
        }
    }

    private sealed class UserNameAuthenticator(OpcUaServerSettings settings, ServerContext serverContext) : IUserTokenAuthenticator
    {
        public UserTokenType TokenType => UserTokenType.UserName;
        public string? IssuedTokenProfileUri => null;

        public ValueTask<AuthenticationResult> AuthenticateAsync(AuthenticationContext authenticationContext, CancellationToken ct)
        {
            if (authenticationContext.TokenHandler is not UserNameIdentityTokenHandler handler)
            {
                return ValueTask.FromResult(AuthenticationResult.Reject(
                    ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "This identity token type is not allowed.").StatusCode));
            }

            byte[]? decryptedPassword = handler.DecryptedPassword;
            string password = decryptedPassword is null || decryptedPassword.Length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(decryptedPassword);

            if (settings.UserName.Length > 0 && handler.UserName == settings.UserName && password == settings.Password)
                return ValueTask.FromResult(AuthenticationResult.Accept(new UserIdentity(handler)));

            serverContext.Log.Warn("", $"Login rejected for user '{handler.UserName}'");
            return ValueTask.FromResult(AuthenticationResult.Reject(
                ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Invalid user name or password.").StatusCode));
        }
    }

    protected override void OnRequestComplete(OperationContext context1)
    {
        base.OnRequestComplete(context1);
        context.Statistics.OnRequest(0);
        context.Statistics.OnResponse(0, false);
    }

    protected override async ValueTask OnRequestValidatedAsync(OperationContext requestContext)
    {
        var faults = context.Faults;
        int delay = Math.Max(0, faults.ResponseDelayMs);
        if (faults.DelayJitterMs > 0)
            delay += Random.Shared.Next(faults.DelayJitterMs + 1);
        if (delay > 0)
            await Task.Delay(delay).ConfigureAwait(false);

        if (FaultSettings.Roll(faults.ErrorResponsePercent))
            throw ServiceResultException.Create(MapFaultCode(faults.ErrorCode), "Injected OPC UA fault response.");

        await base.OnRequestValidatedAsync(requestContext).ConfigureAwait(false);
    }

    /// <summary>Maps the generic fault "error code" (Modbus-style numbers) to OPC UA status codes.</summary>
    private static StatusCode MapFaultCode(int code) => code switch
    {
        1 => StatusCodes.BadServiceUnsupported,
        2 => StatusCodes.BadNodeIdUnknown,
        3 => StatusCodes.BadOutOfRange,
        6 => StatusCodes.BadTooManyOperations,
        _ => StatusCodes.BadInternalError,
    };

    internal static string Describe(Opc.Ua.Server.ISession session)
    {
        return string.IsNullOrEmpty(session.SessionName) ? "session" : session.SessionName;
    }
}
