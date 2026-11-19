using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Mqtt;

public sealed class MqttClientSettings
{
    [Display(Name = "Broker host", GroupName = "Connection", Order = 1)]
    public string Host { get; set; } = "127.0.0.1";

    [Display(Name = "TCP port", GroupName = "Connection", Order = 2, Description = "1883 plain, 8883 with TLS.")]
    [Range(1, 65535)]
    public int Port { get; set; } = 1883;

    [Display(Name = "Use TLS", GroupName = "Connection", Order = 3)]
    public bool UseTls { get; set; }

    [Display(Name = "Accept any server certificate", GroupName = "Connection", Order = 4, Description = "For test brokers with self-signed certificates.")]
    public bool AcceptAnyCertificate { get; set; } = true;

    [Display(Name = "Client ID", GroupName = "Connection", Order = 5, Description = "Empty = a random ID.")]
    public string ClientId { get; set; } = string.Empty;

    [Display(Name = "User name", GroupName = "Connection", Order = 6, Description = "Leave empty for anonymous.")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password", GroupName = "Connection", Order = 7)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep alive (s)", GroupName = "Connection", Order = 8)]
    [Range(1, 3600)]
    public int KeepAliveSeconds { get; set; } = 30;

    [Display(Name = "Connect timeout (ms)", GroupName = "Connection", Order = 9)]
    [Range(100, 60_000)]
    public int ConnectTimeoutMs { get; set; } = 5000;
}

public enum MqttClientOperation
{
    /// <summary>Subscribe to the topic filter (once) and show the latest message of every matching topic.</summary>
    Subscribe,
    /// <summary>Publish the payload to the topic.</summary>
    Publish,
}

public sealed class MqttClientRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1, Description = "Subscribe keeps the latest message per topic; poll it to watch values change.")]
    public MqttClientOperation Operation { get; set; } = MqttClientOperation.Subscribe;

    [Display(Name = "Topic", GroupName = "Request", Order = 2, Description = "Subscribe: a filter, e.g. plant/# or sensors/+/temperature (several separated by commas). Publish: one topic.")]
    public string Topic { get; set; } = "#";

    [Display(Name = "Payload", GroupName = "Request", Order = 3, Description = "Publish: the message text.")]
    public string Payload { get; set; } = string.Empty;

    [Display(Name = "QoS", GroupName = "Request", Order = 4)]
    public MqttQualityOfServiceLevel Qos { get; set; } = MqttQualityOfServiceLevel.AtMostOnce;

    [Display(Name = "Retain", GroupName = "Request", Order = 5, Description = "Publish: ask the broker to keep the message for new subscribers.")]
    public bool Retain { get; set; }
}

/// <summary>MQTT client: subscribes to topic filters and publishes messages.</summary>
public sealed class MqttTopicClient(MqttClientSettings settings, ClientContext context) : IProtocolClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Payload, int Length, bool Retained, long Count, DateTime At)> _latest = new(StringComparer.Ordinal);
    private IMqttClient? _client;
    private string _subscribed = string.Empty;

    private ITrafficLog Log => context.Log;
    private string Remote => $"{settings.Host}:{settings.Port}";
    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureConnectedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<IMqttClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true } c) return c;
        await CloseAsync();

        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            var m = e.ApplicationMessage;
            var bytes = m.Payload.ToArray();
            var text = Encoding.UTF8.GetString(bytes);
            _latest.AddOrUpdate(m.Topic, (text, bytes.Length, m.Retain, 1, DateTime.Now),
                (_, old) => (text, bytes.Length, m.Retain, old.Count + 1, DateTime.Now));
            Log.Frame(TrafficDirection.Rx, Remote, bytes, () => $"PUBLISH {m.Topic}{(m.Retain ? " (retained)" : "")} Â· {Shorten(text)}");
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected) Log.Warn(Remote, $"Disconnected: {e.Reason}");
            return Task.CompletedTask;
        };

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId(string.IsNullOrWhiteSpace(settings.ClientId) ? $"protosim-{Guid.NewGuid():N}"[..23] : settings.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
            .WithCleanSession(true);
        if (!string.IsNullOrEmpty(settings.UserName)) builder = builder.WithCredentials(settings.UserName, settings.Password);
        if (settings.UseTls)
            builder = builder.WithTlsOptions(o =>
            {
                o.UseTls();
                if (settings.AcceptAnyCertificate) o.WithCertificateValidationHandler(_ => true);
            });

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
        Log.Info(Remote, "Connected");
        _client = client;
        _subscribed = string.Empty;
        return client;
    }

    private async Task CloseAsync()
    {
        var client = _client;
        _client = null;
        _subscribed = string.Empty;
        if (client == null) return;
        try
        {
            if (client.IsConnected) await client.DisconnectAsync();
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
            await CloseAsync();
            _latest.Clear();
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
        var r = (MqttClientRequest)request;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedAsync(cancellationToken);
            if (r.Operation == MqttClientOperation.Publish)
            {
                var topic = (r.Topic ?? string.Empty).Trim();
                if (topic.Length == 0 || topic.Contains('#') || topic.Contains('+'))
                    throw new FormatException("Publish needs one topic without wildcards.");
                var bytes = Encoding.UTF8.GetBytes(r.Payload ?? string.Empty);
                var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(bytes)
                    .WithQualityOfServiceLevel(r.Qos).WithRetainFlag(r.Retain).Build();
                var result = await client.PublishAsync(message, cancellationToken);
                Log.Frame(TrafficDirection.Tx, Remote, bytes, () => $"PUBLISH {topic} QoS {(int)r.Qos}{(r.Retain ? " retain" : "")} Â· {Shorten(r.Payload ?? "")}");
                if (!result.IsSuccess) return ClientResult.Fail($"Publish rejected: {result.ReasonCode} {result.ReasonString}", sw.Elapsed);
                return ClientResult.Ok($"Published to {topic}", [new ClientResultRow(topic, $"{bytes.Length} bytes Â· {result.ReasonCode}", r.Payload ?? "") { Typed = ClientValue.Parse(r.Payload) }], sw.Elapsed);
            }

            var filters = ClientValue.SplitList(r.Topic);
            if (filters.Length == 0) throw new FormatException("Enter a topic filter, e.g. plant/#.");
            string key = $"{(int)r.Qos}|{string.Join(",", filters)}";
            if (key != _subscribed)
            {
                if (_subscribed.Length > 0)
                {
                    var old = new MqttClientUnsubscribeOptionsBuilder();
                    foreach (var f in _subscribed.Split('|')[1].Split(',')) old = old.WithTopicFilter(f);
                    await client.UnsubscribeAsync(old.Build(), cancellationToken);
                    _latest.Clear();
                }
                var sub = new MqttClientSubscribeOptionsBuilder();
                foreach (var f in filters) sub = sub.WithTopicFilter(f, r.Qos);
                await client.SubscribeAsync(sub.Build(), cancellationToken);
                Log.Info(Remote, $"SUBSCRIBE {string.Join(", ", filters)} QoS {(int)r.Qos}");
                _subscribed = key;
            }

            var rows = _latest.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ClientResultRow(kv.Key,
                    $"{kv.Value.Length} bytes Â· {kv.Value.Count} msgs{(kv.Value.Retained ? " Â· retained" : "")} Â· {kv.Value.At:HH:mm:ss.fff}",
                    kv.Value.Payload) { Typed = ClientValue.Parse(kv.Value.Payload) })
                .ToList();
            return ClientResult.Ok(rows.Count == 0 ? $"Subscribed to {string.Join(", ", filters)}; waiting for messages" : $"{rows.Count} topics", rows, sw.Elapsed);
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

    private static string Shorten(string s) => s.Length > 120 ? s[..120] + "â€¦" : s;
}
