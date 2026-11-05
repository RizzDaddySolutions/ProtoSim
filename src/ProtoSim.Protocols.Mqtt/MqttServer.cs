using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Mqtt;

public sealed class MqttServer : IProtocolServer
{
    private readonly MqttServerSettings _settings;
    private readonly MqttDataModel _model;
    private readonly ServerContext _context;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _topics = new(StringComparer.Ordinal);
    private global::MQTTnet.Server.MqttServer? _server;

    public MqttServer(MqttServerSettings settings, MqttDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _context = context;
        _model.ValueChanged += OnValueChanged;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

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
                var options = new MqttServerOptionsBuilder()
                    .WithDefaultEndpoint()
                    .WithDefaultEndpointPort(_settings.Port)
                    .Build();
                _server = new MqttServerFactory().CreateMqttServer(options);
                _server.ValidatingConnectionAsync += ValidateConnectionAsync;
                _server.InterceptingPublishAsync += InterceptPublishAsync;
                await _server.StartAsync();
                _context.Log.Info(string.Empty, $"MQTT broker listening on {_settings.BindAddress}:{_settings.Port}");
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _context.Log.Error(string.Empty, $"MQTT start failed: {LastError}");
                SetState(ServerState.Faulted);
                throw new InvalidOperationException(LastError, ex);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_server == null) return;
            SetState(ServerState.Stopping);
            await _server.StopAsync();
            _server.Dispose();
            _server = null;
            SetState(ServerState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections() => [];
    public void DropConnection(long connectionId) { }

    private Task ValidateConnectionAsync(ValidatingConnectionEventArgs args)
    {
        if (!_settings.AllowAnonymous && (args.UserName != _settings.UserName || args.Password != _settings.Password))
            args.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
        return Task.CompletedTask;
    }

    private Task InterceptPublishAsync(InterceptingPublishEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payloadBuilder = new StringBuilder();
        foreach (var segment in args.ApplicationMessage.Payload)
            payloadBuilder.Append(Encoding.UTF8.GetString(segment.Span));
        var payload = payloadBuilder.ToString();
        if (_model.TryWrite(topic, payload))
        {
            _context.Statistics.OnRequest(payload.Length);
            _context.Statistics.OnResponse(payload.Length, false);
            _context.Log.Frame(TrafficDirection.Rx, topic, Encoding.UTF8.GetBytes(payload), () => payload);
        }
        return Task.CompletedTask;
    }

    private void OnValueChanged(string topic, object value)
    {
        _topics[topic] = 0;
        if (_server == null) return;
        var payload = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        var message = new InjectedMqttApplicationMessage(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build())
        {
            SenderClientId = "ProtoSim"
        };
        _ = _server.InjectApplicationMessage(message);
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _model.ValueChanged -= OnValueChanged;
        await StopAsync();
        _lifecycle.Dispose();
    }
}
