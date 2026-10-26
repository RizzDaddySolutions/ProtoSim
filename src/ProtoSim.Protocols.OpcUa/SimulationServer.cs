using Opc.Ua;
using Opc.Ua.Server;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>OPC Foundation StandardServer wired to ProtoSim's data model, log, statistics and fault injection.</summary>
internal sealed class SimulationServer(OpcUaDataModel model, OpcUaServerSettings settings, ServerContext context) : StandardServer
{
    public SimulationNodeManager? NodeManager { get; private set; }

    private static readonly HashSet<RequestType> DataRequests =
    [
        RequestType.Read, RequestType.Write, RequestType.Browse, RequestType.BrowseNext,
        RequestType.TranslateBrowsePathsToNodeIds, RequestType.Call, RequestType.HistoryRead,
        RequestType.CreateMonitoredItems, RequestType.ModifyMonitoredItems, RequestType.CreateSubscription,
    ];

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new SimulationNodeManager(server, configuration, model, settings);
        return new MasterNodeManager(server, configuration, null, NodeManager);
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
        server.SessionManager.ImpersonateUser += OnImpersonateUser;
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

    private void OnImpersonateUser(object? sender, ImpersonateEventArgs args)
    {
        switch (args.NewIdentity)
        {
            case AnonymousIdentityToken when settings.AllowAnonymous:
                return;
            case UserNameIdentityToken token:
                string password = token.DecryptedPassword is { } p ? System.Text.Encoding.UTF8.GetString(p) : string.Empty;
                if (settings.UserName.Length > 0 && token.UserName == settings.UserName && password == settings.Password)
                {
                    args.Identity = new UserIdentity(token);
                    return;
                }
                context.Log.Warn("", $"Login rejected for user '{token.UserName}'");
                throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Invalid user name or password.");
            default:
                throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "This identity token type is not allowed.");
        }
    }

    protected override OperationContext ValidateRequest(SecureChannelContext secureChannelContext, RequestHeader requestHeader, RequestType requestType)
    {
        var op = base.ValidateRequest(secureChannelContext, requestHeader, requestType);
        context.Statistics.OnRequest(0);

        if (requestType is RequestType.Publish or RequestType.Republish) return op; // too chatty to log
        string who = op.Session is { } s ? Describe(s) : "(no session)";
        context.Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, who, requestType.ToString()));

        if (!DataRequests.Contains(requestType)) return op;

        var faults = context.Faults;
        if (FaultSettings.Roll(faults.DropResponsePercent))
        {
            context.Statistics.OnDropped();
            context.Log.Warn(who, $"Fault injection: {requestType} times out");
            throw ServiceResultException.Create(StatusCodes.BadTimeout, "Simulated timeout (fault injection).");
        }

        int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
        if (delay > 0) Thread.Sleep(delay);

        if (FaultSettings.Roll(faults.ErrorResponsePercent))
        {
            var code = MapFaultCode(faults.ErrorCode);
            context.Statistics.OnResponse(0, true);
            context.Log.Warn(who, $"Fault injection: {requestType} → {StatusCodes.GetBrowseName(code)}");
            throw ServiceResultException.Create(code, "Simulated error (fault injection).");
        }

        if (FaultSettings.Roll(faults.DisconnectPercent) && op.Session is { } session)
        {
            context.Log.Warn(who, "Fault injection: closing session");
            _ = Task.Run(() => CurrentInstance.SessionManager.CloseSession(session.Id));
        }
        return op;
    }

    protected override void OnRequestComplete(OperationContext context1)
    {
        base.OnRequestComplete(context1);
        context.Statistics.OnResponse(0, false);
    }

    /// <summary>Maps the generic fault "error code" (Modbus-style numbers) to OPC UA status codes.</summary>
    private static uint MapFaultCode(int code) => code switch
    {
        1 => StatusCodes.BadServiceUnsupported,
        2 => StatusCodes.BadNodeIdUnknown,
        3 => StatusCodes.BadOutOfRange,
        6 => StatusCodes.BadTooManyOperations,
        _ => StatusCodes.BadInternalError,
    };

    internal static string Describe(Opc.Ua.Server.ISession session)
    {
        var d = session.SessionDiagnostics;
        var app = d?.ClientDescription?.ApplicationName?.Text;
        return string.IsNullOrEmpty(app) ? d?.SessionName ?? "session" : $"{d?.SessionName} ({app})";
    }
}
