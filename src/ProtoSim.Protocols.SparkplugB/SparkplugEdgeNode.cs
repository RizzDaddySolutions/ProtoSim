using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.SparkplugB;

/// <summary>
/// A simulated Sparkplug B edge node. Publishes NBIRTH/DBIRTH, report-by-exception NDATA/DDATA and NDEATH (as the MQTT will),
/// and applies NCMD/DCMD writes and rebirth requests. Optionally hosts its own MQTT broker.
/// </summary>
public sealed class SparkplugEdgeNode : IProtocolServer
{
    private const string RebirthMetric = "Node Control/Rebirth";

    private readonly SparkplugSettings _settings;
    private readonly SparkplugDataModel _model;
    private readonly ServerContext _ctx;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ConcurrentQueue<(string Topic, byte[] Payload)> _incoming = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    // Owned by the run loop.
    private readonly Dictionary<string, ulong> _aliasByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> _keyByAlias = [];
    private readonly HashSet<string> _bornDevices = new(StringComparer.Ordinal);
    private ulong _bdSeq;
    private byte _seq;
    private bool _born;
    private bool _hostOnline;
    private volatile bool _rebirthRequested;
    private volatile bool _dropRequested;

    private global::MQTTnet.Server.MqttServer? _broker;
    private IMqttClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile ConnectionInfo? _connection;
    private long _nextConnectionId;

    public SparkplugEdgeNode(SparkplugSettings settings, SparkplugDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _ctx = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>True once births have been published for the current MQTT session.</summary>
    public bool IsBorn => _born;

    private ITrafficLog Log => _ctx.Log;
    private string BrokerHost => _settings.BrokerMode == SparkplugBrokerMode.Embedded ? "127.0.0.1" : _settings.BrokerHost.Trim();
    private int BrokerPort => _settings.BrokerMode == SparkplugBrokerMode.Embedded ? _settings.Port : _settings.BrokerPort;

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
                _settings.Validate();
                if (_settings.BrokerMode == SparkplugBrokerMode.Embedded)
                {
                    var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_settings.Port).Build();
                    _broker = new MqttServerFactory().CreateMqttServer(options);
                    await _broker.StartAsync();
                    Log.Info("", $"Embedded MQTT broker listening on port {_settings.Port}");
                }

                _model.Store.ValueChanged += OnValueChanged;
                _model.Store.TagsChanged += OnTagsChanged;
                _cts = new CancellationTokenSource();
                _runTask = Task.Run(() => RunAsync(_cts.Token));
                Log.Info("", $"Edge node {_settings.GroupId}/{_settings.EdgeNodeId} connecting to {BrokerHost}:{BrokerPort}");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                _model.Store.ValueChanged -= OnValueChanged;
                _model.Store.TagsChanged -= OnTagsChanged;
                if (_broker != null) { try { await _broker.StopAsync(); } catch (Exception) { } _broker.Dispose(); _broker = null; }
                LastError = ex.Message;
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
            if (_cts == null) return;
            SetState(ServerState.Stopping);
            _model.Store.ValueChanged -= OnValueChanged;
            _model.Store.TagsChanged -= OnTagsChanged;
            _cts.Cancel();
            try { await (_runTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* stopping anyway */ }
            _cts.Dispose();
            _cts = null;
            _runTask = null;

            if (_broker != null)
            {
                try { await _broker.StopAsync(); } catch (Exception ex) { Log.Warn("", $"Broker stop: {ex.Message}"); }
                _broker.Dispose();
                _broker = null;
            }
            Log.Info("", "Edge node stopped");
            SetState(ServerState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections() => _connection is { } c ? [c] : [];

    /// <summary>Drops the MQTT session without a DISCONNECT, so the broker publishes the NDEATH will.</summary>
    public void DropConnection(long connectionId)
    {
        if (_connection?.Id != connectionId) return;
        Log.Warn(_connection.RemoteEndPoint, "Connection dropped by user (broker will publish NDEATH)");
        _dropRequested = true;
        _wake.Release();
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

    private void SetError(string? error)
    {
        if (LastError == error) return;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnValueChanged(NamedTag tag)
    {
        _pending[tag.Name] = 0;
    }

    private void OnTagsChanged()
    {
        _rebirthRequested = true;
        _wake.Release();
    }

    // ---- Run loop: owns the MQTT client and all publishing ----

    private async Task RunAsync(CancellationToken ct)
    {
        string remote = $"{BrokerHost}:{BrokerPort}";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(remote, ct);
                await SessionLoopAsync(remote, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetError($"Broker {remote}: {ex.Message}");
                Log.Error(remote, $"MQTT session ended: {ex.Message}");
            }

            CloseClient(abrupt: true);
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(_settings.ReconnectDelaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        await GracefulShutdownAsync(remote);
    }

    private async Task ConnectAsync(string remote, CancellationToken ct)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            _incoming.Enqueue((e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray()));
            _wake.Release();
            return Task.CompletedTask;
        };

        var death = DeathPayload().Encode();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(BrokerHost, BrokerPort)
            .WithClientId($"protosim-{_settings.GroupId}-{_settings.EdgeNodeId}")
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithWillTopic(_settings.NodeTopic("NDEATH"))
            .WithWillPayload(death)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(_settings.UserName)) builder = builder.WithCredentials(_settings.UserName, _settings.Password);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(builder.Build(), timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _connection = new ConnectionInfo(Interlocked.Increment(ref _nextConnectionId), $"broker {remote}");
        _ctx.Statistics.OnConnected();
        SetError(null);
        Log.Info(remote, $"Connected to MQTT broker (bdSeq {_bdSeq})");

        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic(_settings.NodeTopic("NCMD")).WithAtLeastOnceQoS().Build())
            .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic(_settings.NodeTopic("DCMD") + "/+").WithAtLeastOnceQoS().Build());
        bool waitForHost = !string.IsNullOrWhiteSpace(_settings.PrimaryHostId);
        if (waitForHost)
        {
            subscribe = subscribe
                .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic($"{_settings.Namespace}/STATE/{_settings.PrimaryHostId}").WithAtLeastOnceQoS().Build())
                .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic($"STATE/{_settings.PrimaryHostId}").WithAtLeastOnceQoS().Build());
        }
        await client.SubscribeAsync(subscribe.Build(), ct);

        _born = false;
        _hostOnline = !waitForHost;
        if (waitForHost) Log.Info(remote, $"Waiting for primary host '{_settings.PrimaryHostId}' to be online");
    }

    private async Task SessionLoopAsync(string remote, CancellationToken ct)
    {
        var flushTimer = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            int interval = Math.Max(10, _settings.PublishIntervalMs);
            int wait = (int)Math.Max(0, interval - flushTimer.ElapsedMilliseconds);
            await _wake.WaitAsync(wait, ct);

            if (_client is not { IsConnected: true }) throw new IOException("Connection to the broker was lost.");
            if (_dropRequested)
            {
                _dropRequested = false;
                throw new IOException("Dropped by user.");
            }

            while (_incoming.TryDequeue(out var msg)) await HandleIncomingAsync(remote, msg.Topic, msg.Payload, ct);

            if (_hostOnline && (!_born || _rebirthRequested))
            {
                _rebirthRequested = false;
                await PublishBirthsAsync(remote, ct);
                flushTimer.Restart();
                continue;
            }

            if (_born && flushTimer.ElapsedMilliseconds >= interval)
            {
                flushTimer.Restart();
                if (await FlushChangesAsync(remote, ct)) throw new IOException("Fault injection: connection closed after publish.");
            }
        }
    }

    private async Task GracefulShutdownAsync(string remote)
    {
        if (_client is { IsConnected: true } client)
        {
            try
            {
                // A clean DISCONNECT suppresses the will, so announce the death explicitly first.
                await PublishAsync(remote, _settings.NodeTopic("NDEATH"), DeathPayload(), "NDEATH", CancellationToken.None);
                await client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                Log.Warn(remote, $"Disconnect: {ex.Message}");
            }
        }
        CloseClient(abrupt: false);
    }

    private void CloseClient(bool abrupt)
    {
        var client = _client;
        _client = null;
        _born = false;
        if (_connection != null)
        {
            _connection = null;
            _ctx.Statistics.OnDisconnected();
        }
        if (client == null) return;
        if (abrupt) Log.Warn("", "MQTT session closed without DISCONNECT; the broker publishes the NDEATH will");
        client.Dispose();
        _bdSeq++;
    }

    // ---- Births and data ----

    private SparkplugPayload DeathPayload()
    {
        var p = new SparkplugPayload { Timestamp = SparkplugMetric.ToEpochMs(DateTime.UtcNow) };
        p.Metrics.Add(new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, Value = _bdSeq, Timestamp = p.Timestamp });
        return p;
    }

    private async Task PublishBirthsAsync(string remote, CancellationToken ct)
    {
        var tags = _model.Store.All();
        var byDevice = tags
            .Select(t => (Tag: t, Address: SparkplugAddress.FromKey(t.Name)))
            .GroupBy(x => x.Address.Device ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Devices that lost all their metrics go away.
        foreach (var gone in _bornDevices.Where(d => !byDevice.ContainsKey(d)).ToList())
        {
            await PublishAsync(remote, _settings.DeviceTopic("DDEATH", gone), new SparkplugPayload { Timestamp = Now(), Seq = NextSeq() }, $"DDEATH {gone}", ct);
            _bornDevices.Remove(gone);
        }

        _aliasByKey.Clear();
        _keyByAlias.Clear();
        ulong nextAlias = 1;
        _pending.Clear();
        _seq = 0;

        var now = Now();
        var nbirth = new SparkplugPayload { Timestamp = now, Seq = 0 };
        nbirth.Metrics.Add(new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, Value = _bdSeq, Timestamp = now });
        nbirth.Metrics.Add(new SparkplugMetric { Name = RebirthMetric, DataType = SparkplugDataType.Boolean, Value = false, Timestamp = now });
        foreach (var (tag, address) in byDevice.GetValueOrDefault(string.Empty) ?? [])
            nbirth.Metrics.Add(BirthMetric(tag, address.Metric, ref nextAlias));
        await PublishAsync(remote, _settings.NodeTopic("NBIRTH"), nbirth, "NBIRTH", ct);
        _born = true;

        foreach (var (device, list) in byDevice.Where(kv => kv.Key.Length > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var dbirth = new SparkplugPayload { Timestamp = now, Seq = NextSeq() };
            foreach (var (tag, address) in list) dbirth.Metrics.Add(BirthMetric(tag, address.Metric, ref nextAlias));
            await PublishAsync(remote, _settings.DeviceTopic("DBIRTH", device), dbirth, $"DBIRTH {device}", ct);
            _bornDevices.Add(device);
        }
    }

    private SparkplugMetric BirthMetric(NamedTag tag, string metricName, ref ulong nextAlias)
    {
        ulong? alias = null;
        if (_settings.UseAliases)
        {
            alias = nextAlias++;
            _aliasByKey[tag.Name] = alias.Value;
            _keyByAlias[alias.Value] = tag.Name;
        }
        else
        {
            _aliasByKey[tag.Name] = 0;
        }
        return SparkplugMetric.FromTag(metricName, alias, tag.DataType, tag.Value, tag.TimestampUtc);
    }

    /// <summary>Publishes changed metrics. Returns true when fault injection asks to drop the connection.</summary>
    private async Task<bool> FlushChangesAsync(string remote, CancellationToken ct)
    {
        if (_pending.IsEmpty) return false;
        var keys = _pending.Keys.ToList();
        foreach (var k in keys) _pending.TryRemove(k, out _);

        var groups = new SortedDictionary<string, List<SparkplugMetric>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!_aliasByKey.ContainsKey(key)) continue; // not born yet; the pending rebirth will carry it
            var tag = _model.Store.Find(key);
            if (tag == null) continue;
            var address = SparkplugAddress.FromKey(key);
            ulong? alias = _settings.UseAliases ? _aliasByKey[key] : null;
            var metric = SparkplugMetric.FromTag(_settings.UseAliases ? null : address.Metric, alias, tag.DataType, tag.Value, tag.TimestampUtc);
            string device = address.Device ?? string.Empty;
            if (!groups.TryGetValue(device, out var list)) groups[device] = list = [];
            list.Add(metric);
        }

        var faults = _ctx.Faults;
        bool disconnect = false;
        foreach (var (device, metrics) in groups)
        {
            if (FaultSettings.Roll(faults.DropResponsePercent))
            {
                _ctx.Statistics.OnDropped();
                Log.Warn(remote, $"Fault injection: {(device.Length == 0 ? "NDATA" : "DDATA " + device)} not published");
                continue;
            }
            int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
            if (delay > 0) await Task.Delay(delay, ct);

            var payload = new SparkplugPayload { Timestamp = Now(), Seq = NextSeq() };
            payload.Metrics.AddRange(metrics);
            if (device.Length == 0) await PublishAsync(remote, _settings.NodeTopic("NDATA"), payload, "NDATA", ct);
            else await PublishAsync(remote, _settings.DeviceTopic("DDATA", device), payload, $"DDATA {device}", ct);
            disconnect |= FaultSettings.Roll(faults.DisconnectPercent);
        }
        return disconnect;
    }

    private async Task PublishAsync(string remote, string topic, SparkplugPayload payload, string kind, CancellationToken ct)
    {
        var client = _client ?? throw new IOException("Not connected.");
        var bytes = payload.Encode();
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(bytes)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
        await client.PublishAsync(message, ct);
        _ctx.Statistics.OnResponse(bytes.Length, false);
        _connection?.OnRequest();
        Log.Frame(TrafficDirection.Tx, remote, bytes, () => $"{kind} → {topic} · {Describe(payload)}");
    }

    // ---- Commands and host state ----

    private async Task HandleIncomingAsync(string remote, string topic, byte[] payload, CancellationToken ct)
    {
        _ctx.Statistics.OnRequest(payload.Length);
        string stateTopic = $"{_settings.Namespace}/STATE/{_settings.PrimaryHostId}";
        if (!string.IsNullOrWhiteSpace(_settings.PrimaryHostId) && (topic == stateTopic || topic == $"STATE/{_settings.PrimaryHostId}"))
        {
            var text = Encoding.UTF8.GetString(payload);
            Log.Frame(TrafficDirection.Rx, remote, payload, () => $"STATE {topic} · {text}");
            bool online = ParseHostState(topic == stateTopic, text);
            if (online && !_hostOnline) Log.Info(remote, $"Primary host '{_settings.PrimaryHostId}' is online");
            if (!online && _hostOnline)
            {
                Log.Warn(remote, $"Primary host '{_settings.PrimaryHostId}' went offline");
                if (_born)
                {
                    await PublishAsync(remote, _settings.NodeTopic("NDEATH"), DeathPayload(), "NDEATH", ct);
                    _born = false;
                }
            }
            _hostOnline = online;
            return;
        }

        string nodeCmd = _settings.NodeTopic("NCMD");
        string? device;
        if (topic == nodeCmd) device = null;
        else if (topic.StartsWith(_settings.NodeTopic("DCMD") + "/", StringComparison.Ordinal)) device = topic[(_settings.NodeTopic("DCMD").Length + 1)..];
        else return;

        SparkplugPayload cmd;
        try
        {
            cmd = SparkplugPayload.Decode(payload);
        }
        catch (FormatException ex)
        {
            _ctx.Statistics.OnMalformed();
            Log.Error(remote, $"Malformed {(device == null ? "NCMD" : "DCMD")} payload: {ex.Message}");
            return;
        }
        Log.Frame(TrafficDirection.Rx, remote, payload, () => $"{(device == null ? "NCMD" : "DCMD " + device)} · {Describe(cmd)}");

        foreach (var m in cmd.Metrics)
        {
            if (device == null && m.Name == RebirthMetric)
            {
                if (m.Value is true)
                {
                    Log.Info(remote, "Rebirth requested");
                    _rebirthRequested = true;
                }
                continue;
            }

            string? key = m.Name != null ? (device == null ? m.Name : device + ":" + m.Name)
                : m.Alias is { } alias && _keyByAlias.TryGetValue(alias, out var k) ? k : null;
            var tag = key == null ? null : _model.Store.Find(key);
            if (tag == null || (key != null && device != null && !key.StartsWith(device + ":", StringComparison.Ordinal)))
            {
                Log.Warn(remote, $"Command for unknown metric {m.Name ?? "alias " + m.Alias}");
                continue;
            }
            try
            {
                tag.Write(m.ToClrValue()!);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                Log.Warn(remote, $"Can't write {tag.Name}: {ex.Message}");
            }
        }
    }

    private static bool ParseHostState(bool json, string text)
    {
        if (!json) return text.Trim().Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private ulong NextSeq()
    {
        _seq = unchecked((byte)(_seq + 1));
        return _seq;
    }

    private static ulong Now() => SparkplugMetric.ToEpochMs(DateTime.UtcNow);

    private static string Describe(SparkplugPayload p)
    {
        var sb = new StringBuilder();
        if (p.Seq is { } seq) sb.Append("seq ").Append(seq).Append(" · ");
        sb.Append(p.Metrics.Count).Append(p.Metrics.Count == 1 ? " metric" : " metrics");
        foreach (var m in p.Metrics.Take(8))
        {
            sb.Append(" · ").Append(m.Name ?? "#" + m.Alias).Append('=');
            sb.Append(m.IsNull ? "null" : Convert.ToString(m.ToClrValue(), System.Globalization.CultureInfo.InvariantCulture));
        }
        if (p.Metrics.Count > 8) sb.Append(" …");
        return sb.ToString();
    }
}
