using System.Collections.Concurrent;
using System.Diagnostics;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>OPC UA client session: browse, read, write and subscribe.</summary>
public sealed class OpcUaClient(OpcUaClientSettings settings, ClientContext context) : IProtocolClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DataValue Value, long Updates)> _subscriptionValues = new();
    private ApplicationConfiguration? _config;
    private Session? _session;
    private Subscription? _subscription;
    private string _subscriptionKey = string.Empty;

    public bool IsConnected => _session?.Connected == true;

    private ITrafficLog Log => context.Log;
    private string Remote => settings.EndpointUrl;

    // The OPC UA stack does blocking work (certificates, sockets) and must not run on a UI thread.
    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.Run(() => ConnectCoreAsync(cancellationToken), cancellationToken);

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureSessionAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public Task DisconnectAsync() => Task.Run(DisconnectCoreAsync);

    private async Task DisconnectCoreAsync()
    {
        await _gate.WaitAsync();
        try { await CloseCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    private async Task CloseCoreAsync()
    {
        var session = _session;
        _session = null;
        _subscription = null;
        _subscriptionKey = string.Empty;
        _subscriptionValues.Clear();
        if (session == null) return;
        try { await session.CloseAsync(CancellationToken.None); }
        catch (Exception) { /* closing anyway */ }
        session.Dispose();
        Log.Info(Remote, "Disconnected");
    }

    private async Task<Session> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { Connected: true } existing) return existing;
        if (_session != null) await CloseCoreAsync();

        _config ??= await CreateConfigurationAsync(ct);
        Log.Info(Remote, $"Connecting ({(settings.UseSecurity ? "most secure endpoint" : "security None")})…");

        var endpointDescription = await CoreClientUtils.SelectEndpointAsync(_config, settings.EndpointUrl, settings.UseSecurity, OpcUaConfiguration.Telemetry, ct)
            ?? throw new InvalidOperationException($"{settings.EndpointUrl} offers no usable endpoint.");
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(_config));
        IUserIdentity identity = string.IsNullOrEmpty(settings.UserName)
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(settings.UserName, System.Text.Encoding.UTF8.GetBytes(settings.Password ?? string.Empty));

        var factory = new DefaultSessionFactory(OpcUaConfiguration.Telemetry);
        var session = (Session)await factory.CreateAsync(_config, endpoint, false, "ProtoSim client",
            (uint)settings.SessionTimeoutMs, identity, null, ct);
        session.OperationTimeout = settings.OperationTimeoutMs;
        _session = session;

        var sec = endpointDescription.SecurityMode == MessageSecurityMode.None
            ? "None"
            : $"{endpointDescription.SecurityMode} {SecurityPolicies.GetDisplayName(endpointDescription.SecurityPolicyUri)}";
        Log.Info(Remote, $"Session created · security {sec} · {(string.IsNullOrEmpty(settings.UserName) ? "anonymous" : $"user {settings.UserName}")}");
        return session;
    }

    private async Task<ApplicationConfiguration> CreateConfigurationAsync(CancellationToken ct)
    {
        var config = await OpcUaConfiguration.CreateAsync("ProtoSim OPC UA Client", ApplicationType.Client,
            settings.AutoAcceptServerCertificate,
            c => c.ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = settings.SessionTimeoutMs },
            ct);
        var app = new ApplicationInstance(config, OpcUaConfiguration.Telemetry)
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = ApplicationType.Client,
        };
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);
        return config;
    }

    public Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExecuteCoreAsync(request, cancellationToken), cancellationToken);

    private async Task<ClientResult> ExecuteCoreAsync(object request, CancellationToken cancellationToken)
    {
        var r = (OpcUaClientRequest)request;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken);
            var rows = r.Operation switch
            {
                OpcUaOperation.Read => await ReadAsync(session, ParseNodeIds(r.NodeIds, required: true), cancellationToken),
                OpcUaOperation.Write => await WriteAsync(session, r, cancellationToken),
                OpcUaOperation.Browse => await BrowseAsync(session, r, cancellationToken),
                OpcUaOperation.Subscribe => await SubscribeAsync(session, r, cancellationToken),
                _ => throw new NotSupportedException(r.Operation.ToString()),
            };
            return new ClientResult(true, $"{r.Operation}: OK ({rows.Count} rows)", rows, sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ServiceResultException ex)
        {
            Log.Error(Remote, $"{r.Operation} failed: {StatusCodes.GetBrowseName(ex.StatusCode)} {ex.Message}");
            if (ex.StatusCode is StatusCodes.BadSessionIdInvalid or StatusCodes.BadSessionClosed or StatusCodes.BadConnectionClosed or StatusCodes.BadNotConnected)
                await CloseCoreAsync();
            return ClientResult.Fail($"{StatusCodes.GetBrowseName(ex.StatusCode)}: {ex.Message}", sw.Elapsed);
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

    private static List<NodeId> ParseNodeIds(string text, bool required)
    {
        var ids = (text ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s =>
            {
                try { return NodeId.Parse(s); }
                catch (Exception) { throw new FormatException($"'{s}' is not a valid node ID (e.g. ns=2;s=Plant/Tank1/Level or i=2258)."); }
            })
            .ToList();
        if (required && ids.Count == 0) throw new FormatException("Enter at least one node ID.");
        return ids;
    }

    private static string FormatValue(DataValue dv)
    {
        var v = dv.Value;
        return v switch
        {
            null => "(null)",
            Array a => "[" + string.Join(", ", a.Cast<object?>().Take(20).Select(x => ValueConverter.Format(x))) + (a.Length > 20 ? ", …" : "") + "]",
            LocalizedText lt => lt.Text ?? string.Empty,
            _ => ValueConverter.Format(v),
        };
    }

    private static string Describe(DataValue dv) =>
        $"{StatusCodes.GetBrowseName(dv.StatusCode.Code)} · {dv.WrappedValue.TypeInfo?.BuiltInType.ToString() ?? "?"} · {dv.SourceTimestamp.ToLocalTime():HH:mm:ss.fff}";

    private async Task<IReadOnlyList<ClientResultRow>> ReadAsync(Session session, List<NodeId> ids, CancellationToken ct)
    {
        var values = await ReadValuesAsync(session, ids, ct);
        return ids.Select((id, i) => new ClientResultRow(id.ToString(), Describe(values[i]), FormatValue(values[i]))).ToList();
    }

    private async Task<DataValueCollection> ReadValuesAsync(Session session, IList<NodeId> ids, CancellationToken ct)
    {
        var toRead = new ReadValueIdCollection(ids.Select(id => new ReadValueId { NodeId = id, AttributeId = Attributes.Value }));
        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"Read {ids.Count} node(s)"));
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, toRead, ct);
        ClientBase.ValidateResponse(response.Results, toRead);
        int good = response.Results.Count(v => StatusCode.IsGood(v.StatusCode));
        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote, $"Read → {good}/{ids.Count} Good"));
        return response.Results;
    }

    private async Task<IReadOnlyList<ClientResultRow>> WriteAsync(Session session, OpcUaClientRequest r, CancellationToken ct)
    {
        var ids = ParseNodeIds(r.NodeIds, required: true);
        var texts = (r.Values ?? string.Empty).Split('|');
        if (texts.Length != 1 && texts.Length != ids.Count)
            throw new FormatException($"Enter one value, or {ids.Count} values separated by '|'.");

        // Read first so each value is converted to the node's actual data type.
        var current = await ReadValuesAsync(session, ids, ct);
        var writes = new WriteValueCollection();
        for (int i = 0; i < ids.Count; i++)
        {
            var text = texts.Length == 1 ? texts[0] : texts[i];
            var builtIn = current[i].WrappedValue.TypeInfo?.BuiltInType ?? BuiltInType.String;
            var tagType = OpcUaConfiguration.TagType(builtIn)
                ?? throw new FormatException($"{ids[i]} has type {builtIn}, which this client cannot write.");
            object value = ValueConverter.Parse(text, tagType);
            value = builtIn switch
            {
                BuiltInType.SByte => Convert.ToSByte(value),
                BuiltInType.Byte => Convert.ToByte(value),
                _ => value,
            };
            writes.Add(new WriteValue { NodeId = ids[i], AttributeId = Attributes.Value, Value = new DataValue(new Variant(value)) });
        }

        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"Write {writes.Count} node(s)"));
        var response = await session.WriteAsync(null, writes, ct);
        ClientBase.ValidateResponse(response.Results, writes);
        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote,
            $"Write → {string.Join(", ", response.Results.Select(s => StatusCodes.GetBrowseName(s.Code)))}"));

        var bad = response.Results.FirstOrDefault(s => StatusCode.IsBad(s));
        var rows = ids.Select((id, i) => new ClientResultRow(id.ToString(), StatusCodes.GetBrowseName(response.Results[i].Code),
            ValueConverter.Format(writes[i].Value.Value))).ToList();
        if (StatusCode.IsBad(bad)) throw new ServiceResultException(bad.Code, $"Write rejected: {StatusCodes.GetBrowseName(bad.Code)}");
        return rows;
    }

    private async Task<IReadOnlyList<ClientResultRow>> BrowseAsync(Session session, OpcUaClientRequest r, CancellationToken ct)
    {
        var start = ParseNodeIds(r.NodeIds, required: false).FirstOrDefault() ?? ObjectIds.ObjectsFolder;
        var found = new List<(ReferenceDescription Ref, NodeId Id, string Path, int Depth)>();
        await BrowseRecursiveAsync(session, start, "", 0, Math.Clamp(r.BrowseDepth, 1, 10), found, ct);

        var variableIds = found.Where(f => f.Ref.NodeClass == NodeClass.Variable).Select(f => f.Id).ToList();
        var values = variableIds.Count > 0 ? await ReadValuesAsync(session, variableIds, ct) : [];
        var valueById = variableIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => values[x.i]);

        // Indented display names read as a tree; the full path is in the NodeId column.
        return found.Select(f =>
        {
            string indent = new string(' ', f.Depth * 2);
            string name = f.Ref.DisplayName.Text;
            if (f.Ref.NodeClass == NodeClass.Variable && valueById.TryGetValue(f.Id, out var dv))
                return new ClientResultRow(indent + name, f.Id.ToString(), FormatValue(dv));
            return new ClientResultRow(indent + name + "/", f.Id.ToString(), $"({f.Ref.NodeClass})");
        }).ToList();
    }

    private async Task BrowseRecursiveAsync(Session session, NodeId node, string path, int depth, int maxDepth,
        List<(ReferenceDescription, NodeId, string, int)> found, CancellationToken ct)
    {
        if (depth >= maxDepth || found.Count > 5000) return;

        var description = new BrowseDescription
        {
            NodeId = node,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
            ResultMask = (uint)BrowseResultMask.All,
        };
        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"Browse {node}"));
        var response = await session.BrowseAsync(null, null, 0, [description], ct);
        var result = response.Results[0];
        var references = new List<ReferenceDescription>(result.References);
        var continuation = result.ContinuationPoint;
        while (continuation is { Length: > 0 })
        {
            var next = await session.BrowseNextAsync(null, false, [continuation], ct);
            references.AddRange(next.Results[0].References);
            continuation = next.Results[0].ContinuationPoint;
        }
        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote, $"Browse → {references.Count} references"));

        foreach (var reference in references)
        {
            var id = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (id == null) continue;
            if (id == ObjectIds.Server) continue; // the standard Server object is huge and not interesting here
            if (node == ObjectIds.ObjectsFolder && id.NamespaceIndex == 0) continue; // skip standard folders (Aliases, Locations…)
            string childPath = path.Length == 0 ? reference.DisplayName.Text : $"{path}/{reference.DisplayName.Text}";
            found.Add((reference, id, childPath, depth));
            if (reference.NodeClass == NodeClass.Object)
                await BrowseRecursiveAsync(session, id, childPath, depth + 1, maxDepth, found, ct);
        }
    }

    private async Task<IReadOnlyList<ClientResultRow>> SubscribeAsync(Session session, OpcUaClientRequest r, CancellationToken ct)
    {
        var ids = ParseNodeIds(r.NodeIds, required: true);
        string key = $"{r.SamplingIntervalMs}|{string.Join(",", ids)}";

        if (_subscription == null || key != _subscriptionKey)
        {
            if (_subscription != null)
            {
                await session.RemoveSubscriptionAsync(_subscription, ct);
                _subscription = null;
            }
            _subscriptionValues.Clear();

            var subscription = new Subscription(session.DefaultSubscription)
            {
                DisplayName = "ProtoSim",
                PublishingEnabled = true,
                PublishingInterval = r.SamplingIntervalMs,
                KeepAliveCount = 10,
                LifetimeCount = 100,
            };
            foreach (var id in ids)
            {
                var item = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = id,
                    AttributeId = Attributes.Value,
                    DisplayName = id.ToString(),
                    SamplingInterval = r.SamplingIntervalMs,
                    QueueSize = 1,
                    DiscardOldest = true,
                };
                string name = id.ToString();
                item.Notification += (monitored, _) =>
                {
                    foreach (var value in monitored.DequeueValues())
                        _subscriptionValues.AddOrUpdate(name, (value, 1), (_, old) => (value, old.Updates + 1));
                };
                subscription.AddItem(item);
            }

            session.AddSubscription(subscription);
            Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"CreateSubscription + {ids.Count} monitored item(s) @ {r.SamplingIntervalMs} ms"));
            await subscription.CreateAsync(ct);
            Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote, $"Subscription {subscription.Id} created"));
            _subscription = subscription;
            _subscriptionKey = key;
        }

        return ids.Select(id =>
        {
            string name = id.ToString();
            return _subscriptionValues.TryGetValue(name, out var v)
                ? new ClientResultRow(name, $"{Describe(v.Value)} · {v.Updates} updates", FormatValue(v.Value))
                : new ClientResultRow(name, "waiting for first notification", "");
        }).ToList();
    }
}
