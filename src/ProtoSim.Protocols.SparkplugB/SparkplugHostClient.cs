using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.SparkplugB;

public sealed class SparkplugHostSettings
{
    [Display(Name = "Broker host", GroupName = "Broker", Order = 1)]
    public string Host { get; set; } = "127.0.0.1";

    [Display(Name = "TCP port", GroupName = "Broker", Order = 2)]
    [Range(1, 65535)]
    public int Port { get; set; } = 1883;

    [Display(Name = "User name", GroupName = "Broker", Order = 3, Description = "Leave empty for anonymous.")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password", GroupName = "Broker", Order = 4)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Group ID", GroupName = "Sparkplug", Order = 10, Description = "Group to watch, or + for every group.")]
    public string GroupId { get; set; } = "+";

    [Display(Name = "Host application ID", GroupName = "Sparkplug", Order = 11,
        Description = "When set, the client publishes STATE online/offline as this primary host, so edge nodes that wait for it start publishing.")]
    public string HostId { get; set; } = string.Empty;

    [Display(Name = "Connect timeout (ms)", GroupName = "Broker", Order = 5)]
    [Range(100, 60_000)]
    public int ConnectTimeoutMs { get; set; } = 5000;
}

public enum SparkplugHostOperation
{
    /// <summary>Show the latest value of every metric seen in births and data messages.</summary>
    Monitor,
    /// <summary>Write a metric with NCMD (edge node metric) or DCMD (device metric).</summary>
    Command,
    /// <summary>Ask an edge node to publish its births again (NCMD Node Control/Rebirth).</summary>
    Rebirth,
}

public sealed class SparkplugHostRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1, Description = "Monitor keeps the latest value of every metric; poll it to watch them change.")]
    public SparkplugHostOperation Operation { get; set; } = SparkplugHostOperation.Monitor;

    [Display(Name = "Group ID", GroupName = "Target", Order = 2, Description = "Command and Rebirth: the edge node's group.")]
    public string GroupId { get; set; } = "ProtoSim";

    [Display(Name = "Edge node ID", GroupName = "Target", Order = 3)]
    public string EdgeNodeId { get; set; } = "EdgeNode1";

    [Display(Name = "Device ID", GroupName = "Target", Order = 4, Description = "Empty for an edge node metric (NCMD), otherwise DCMD to this device.")]
    public string DeviceId { get; set; } = string.Empty;

    [Display(Name = "Metric", GroupName = "Target", Order = 5)]
    public string Metric { get; set; } = string.Empty;

    [Display(Name = "Value", GroupName = "Target", Order = 6)]
    public string Value { get; set; } = string.Empty;

    [Display(Name = "Data type", GroupName = "Target", Order = 7)]
    public TagDataType DataType { get; set; } = TagDataType.Int32;
}

/// <summary>Sparkplug B host application: decodes births, data and deaths, and sends commands.</summary>
public sealed class SparkplugHostClient(SparkplugHostSettings settings, ClientContext context) : IProtocolClient
{
    private const string Ns = "spBv1.0";

    private sealed record MetricState(string Type, object? Value, DateTime At, long Updates);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, MetricState> _metrics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _online = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Node, ulong Alias), string> _aliases = new();
    private IMqttClient? _client;

    private ITrafficLog Log => context.Log;
    private string Remote => $"{settings.Host}:{settings.Port}";
    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureConnectedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private string StateTopic => $"{Ns}/STATE/{settings.HostId}";

    private static byte[] StatePayload(bool online) =>
        Encoding.UTF8.GetBytes($"{{\"online\":{(online ? "true" : "false")},\"timestamp\":{SparkplugMetric.ToEpochMs(DateTime.UtcNow)}}}");

    private async Task<IMqttClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true } c) return c;
        await CloseAsync(false);

        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            Handle(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray());
            return Task.CompletedTask;
        };
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId($"protosim-host-{Guid.NewGuid():N}"[..23])
            .WithCleanSession(true);
        if (!string.IsNullOrEmpty(settings.UserName)) builder = builder.WithCredentials(settings.UserName, settings.Password);
        bool primary = !string.IsNullOrWhiteSpace(settings.HostId);
        if (primary)
            builder = builder.WithWillTopic(StateTopic).WithWillPayload(StatePayload(false)).WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(builder.Build(), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            Log.Error(Remote, "Connect timed out");
            throw new TimeoutException($"Connecting to {Remote} timed out after {settings.ConnectTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            client.Dispose();
            Log.Error(Remote, $"Connect failed: {ex.Message}");
            throw;
        }

        string group = string.IsNullOrWhiteSpace(settings.GroupId) ? "+" : settings.GroupId.Trim();
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter($"{Ns}/{group}/#", MqttQualityOfServiceLevel.AtMostOnce).Build(), ct);
        Log.Info(Remote, $"Connected Â· watching {Ns}/{group}/#");
        if (primary)
        {
            await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(StateTopic).WithPayload(StatePayload(true))
                .WithRetainFlag(true).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
            Log.Info(Remote, $"STATE online as primary host '{settings.HostId}'");
        }
        _client = client;
        return client;
    }

    private void Handle(string topic, byte[] payload)
    {
        // spBv1.0/{group}/{type}/{node}[/{device}]
        var parts = topic.Split('/');
        if (parts.Length < 4 || parts[0] != Ns || parts[1] == "STATE") return;
        string group = parts[1], type = parts[2], node = parts[3];
        string? device = parts.Length > 4 ? parts[4] : null;
        string nodeKey = $"{group}/{node}";
        string owner = device == null ? nodeKey : $"{nodeKey}/{device}";

        SparkplugPayload p;
        try { p = SparkplugPayload.Decode(payload); }
        catch (Exception ex)
        {
            Log.Error(Remote, $"Malformed {type} on {topic}: {ex.Message}");
            return;
        }
        Log.Frame(TrafficDirection.Rx, Remote, payload, () => $"{type} {owner} Â· {p.Metrics.Count} metrics");

        switch (type)
        {
            case "NBIRTH":
                foreach (var k in _metrics.Keys.Where(k => k.StartsWith(nodeKey + "/", StringComparison.Ordinal) || k.StartsWith(nodeKey + ":", StringComparison.Ordinal)).ToList())
                    _metrics.TryRemove(k, out _);
                foreach (var a in _aliases.Keys.Where(a => a.Node == nodeKey).ToList()) _aliases.TryRemove(a, out _);
                _online[nodeKey] = true;
                break;
            case "DBIRTH":
                _online[owner] = true;
                break;
            case "NDEATH":
                foreach (var k in _online.Keys.Where(k => k == nodeKey || k.StartsWith(nodeKey + "/", StringComparison.Ordinal)).ToList()) _online[k] = false;
                return;
            case "DDEATH":
                _online[owner] = false;
                return;
            case "NDATA" or "DDATA":
                break;
            default:
                return; // commands from other hosts
        }

        bool birth = type is "NBIRTH" or "DBIRTH";
        foreach (var m in p.Metrics)
        {
            string? name = m.Name;
            if (birth && name != null && m.Alias is { } alias) _aliases[(nodeKey, alias)] = name;
            if (name == null && m.Alias is { } a2) _aliases.TryGetValue((nodeKey, a2), out name);
            if (name == null || name == "bdSeq") continue;
            string key = $"{owner}:{name}";
            var at = m.Timestamp is { } ts ? DateTimeOffset.FromUnixTimeMilliseconds((long)ts).LocalDateTime : DateTime.Now;
            var value = m.IsNull ? null : m.ToClrValue();
            _metrics.AddOrUpdate(key, new MetricState(m.DataType.ToString(), value, at, 1), (_, old) => new MetricState(m.DataType.ToString(), value, at, old.Updates + 1));
        }
    }

    private async Task CloseAsync(bool graceful)
    {
        var client = _client;
        _client = null;
        if (client == null) return;
        try
        {
            if (client.IsConnected)
            {
                if (graceful && !string.IsNullOrWhiteSpace(settings.HostId))
                    await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(StateTopic).WithPayload(StatePayload(false))
                        .WithRetainFlag(true).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
                await client.DisconnectAsync();
            }
        }
        catch (Exception) { /* closing anyway */ }
        client.Dispose();
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsConnected) Log.Info(Remote, "Disconnected");
            await CloseAsync(true);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (SparkplugHostRequest)request;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedAsync(cancellationToken);
            switch (r.Operation)
            {
                case SparkplugHostOperation.Rebirth:
                {
                    var payload = new SparkplugPayload { Timestamp = SparkplugMetric.ToEpochMs(DateTime.UtcNow) };
                    payload.Metrics.Add(new SparkplugMetric { Name = "Node Control/Rebirth", DataType = SparkplugDataType.Boolean, Value = true, Timestamp = payload.Timestamp });
                    var topic = $"{Ns}/{Id(r.GroupId, "Group ID")}/NCMD/{Id(r.EdgeNodeId, "Edge node ID")}";
                    await PublishAsync(client, topic, payload, "NCMD Rebirth", cancellationToken);
                    return ClientResult.Ok($"Rebirth requested from {r.GroupId}/{r.EdgeNodeId}", [], sw.Elapsed);
                }
                case SparkplugHostOperation.Command:
                {
                    if (string.IsNullOrWhiteSpace(r.Metric)) throw new FormatException("Enter the metric name.");
                    var value = ValueConverter.Parse(r.Value, r.DataType);
                    var payload = new SparkplugPayload { Timestamp = SparkplugMetric.ToEpochMs(DateTime.UtcNow) };
                    payload.Metrics.Add(SparkplugMetric.FromTag(r.Metric.Trim(), null, r.DataType, value, DateTime.UtcNow));
                    string group = Id(r.GroupId, "Group ID"), node = Id(r.EdgeNodeId, "Edge node ID");
                    bool isDevice = !string.IsNullOrWhiteSpace(r.DeviceId);
                    var topic = isDevice ? $"{Ns}/{group}/DCMD/{node}/{Id(r.DeviceId, "Device ID")}" : $"{Ns}/{group}/NCMD/{node}";
                    await PublishAsync(client, topic, payload, isDevice ? "DCMD" : "NCMD", cancellationToken);
                    string address = $"{group}/{node}{(isDevice ? "/" + r.DeviceId.Trim() : "")}:{r.Metric.Trim()}";
                    return ClientResult.Ok($"{(isDevice ? "DCMD" : "NCMD")} sent", [new ClientResultRow(address, r.DataType.ToString(), ValueConverter.Format(value)) { Typed = value }], sw.Elapsed);
                }
                default:
                {
                    var rows = _metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                    {
                        int colon = kv.Key.IndexOf(':');
                        string owner = colon < 0 ? kv.Key : kv.Key[..colon];
                        string state = _online.TryGetValue(owner, out var on) ? (on ? "online" : "OFFLINE") : "online";
                        string text = kv.Value.Value == null ? "(null)" : ValueConverter.Format(kv.Value.Value);
                        return new ClientResultRow(kv.Key, $"{kv.Value.Type} Â· {state} Â· {kv.Value.At:HH:mm:ss.fff} Â· {kv.Value.Updates} updates", text) { Typed = kv.Value.Value };
                    }).ToList();
                    int nodes = _online.Keys.Count(k => k.Count(ch => ch == '/') == 1);
                    return ClientResult.Ok(rows.Count == 0 ? "Waiting for births (send Rebirth to ask an edge node for them)" : $"{rows.Count} metrics from {nodes} edge node(s)", rows, sw.Elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private static string Id(string value, string what)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0 || v.IndexOfAny(['/', '+', '#']) >= 0) throw new FormatException($"{what} must be non-empty and can't contain '/', '+' or '#'.");
        return v;
    }

    private async Task PublishAsync(IMqttClient client, string topic, SparkplugPayload payload, string kind, CancellationToken ct)
    {
        var bytes = payload.Encode();
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(bytes)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build(), ct);
        Log.Frame(TrafficDirection.Tx, Remote, bytes, () => $"{kind} â†’ {topic} Â· " +
            string.Join(", ", payload.Metrics.Select(m => $"{m.Name}={Convert.ToString(m.Value, CultureInfo.InvariantCulture)}")));
    }
}
