using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.SparkplugB;

namespace ProtoSim.SparkplugB.Tests;

public sealed class PayloadTests
{
    [Fact]
    public void RoundTripsAllValueKinds()
    {
        var p = new SparkplugPayload { Timestamp = 1_700_000_000_000, Seq = 7 };
        p.Metrics.Add(SparkplugMetric.FromTag("a", 1, TagDataType.Int16, (short)-5, DateTime.UtcNow));
        p.Metrics.Add(SparkplugMetric.FromTag("b", 2, TagDataType.Int64, -9_000_000_000L, DateTime.UtcNow));
        p.Metrics.Add(SparkplugMetric.FromTag("c", null, TagDataType.Float32, 1.5f, DateTime.UtcNow));
        p.Metrics.Add(SparkplugMetric.FromTag("d", null, TagDataType.Float64, -2.25, DateTime.UtcNow));
        p.Metrics.Add(SparkplugMetric.FromTag("e", null, TagDataType.Bool, true, DateTime.UtcNow));
        p.Metrics.Add(SparkplugMetric.FromTag("f", null, TagDataType.String, "héllo", DateTime.UtcNow));
        p.Metrics.Add(new SparkplugMetric { Name = "g", DataType = SparkplugDataType.Int32, IsNull = true });

        var d = SparkplugPayload.Decode(p.Encode());

        Assert.Equal(7UL, d.Seq);
        Assert.Equal(1_700_000_000_000UL, d.Timestamp);
        Assert.Equal(7, d.Metrics.Count);
        Assert.Equal((short)-5, d.Metrics[0].ToClrValue());
        Assert.Equal(1UL, d.Metrics[0].Alias);
        Assert.Equal(-9_000_000_000L, d.Metrics[1].ToClrValue());
        Assert.Equal(1.5f, d.Metrics[2].ToClrValue());
        Assert.Equal(-2.25, d.Metrics[3].ToClrValue());
        Assert.Equal(true, d.Metrics[4].ToClrValue());
        Assert.Equal("héllo", d.Metrics[5].ToClrValue());
        Assert.True(d.Metrics[6].IsNull);
        Assert.Equal(SparkplugDataType.Int32, d.Metrics[6].DataType);
    }

    [Fact]
    public void RejectsTruncatedPayload()
    {
        var p = new SparkplugPayload { Seq = 1 };
        p.Metrics.Add(SparkplugMetric.FromTag("name", null, TagDataType.String, "value", DateTime.UtcNow));
        var bytes = p.Encode();
        Assert.Throws<FormatException>(() => SparkplugPayload.Decode(bytes.AsSpan(0, bytes.Length - 3)));
    }

    [Theory]
    [InlineData("Inputs/Temperature", null, "Inputs/Temperature")]
    [InlineData("Pump1:Speed", "Pump1", "Speed")]
    [InlineData(" Tank 1 : Level/PV ", "Tank 1", "Level/PV")]
    public void ParsesAddresses(string text, string? device, string metric)
    {
        var model = new SparkplugDataModel();
        using var b = model.Bind(text, TagDataType.Int32, WordOrder.ABCD);
        var tag = Assert.Single(model.Store.All());
        Assert.Equal(device == null ? metric : device + ":" + metric, tag.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Dev/1:Speed")]
    [InlineData("bdSeq")]
    [InlineData("Node Control/Rebirth")]
    [InlineData("Pump1:")]
    public void RejectsBadAddresses(string text) =>
        Assert.Throws<FormatException>(() => new SparkplugDataModel().Bind(text, TagDataType.Int32, WordOrder.ABCD));
}

public sealed class EdgeNodeTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Host : IAsyncDisposable
    {
        public readonly BlockingCollection<(string Topic, SparkplugPayload Payload)> Messages = new();
        public IMqttClient Client { get; } = new MqttClientFactory().CreateMqttClient();

        public static async Task<Host> ConnectAsync(int port)
        {
            var h = new Host();
            h.Client.ApplicationMessageReceivedAsync += e =>
            {
                var bytes = e.ApplicationMessage.Payload.ToArray();
                if (e.ApplicationMessage.Topic.StartsWith("spBv1.0/ProtoSim/", StringComparison.Ordinal))
                    h.Messages.Add((e.ApplicationMessage.Topic, SparkplugPayload.Decode(bytes)));
                return Task.CompletedTask;
            };
            await h.Client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId("test-host").Build());
            await h.Client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic("spBv1.0/ProtoSim/#").Build()).Build());
            return h;
        }

        public (string Topic, SparkplugPayload Payload) Next(string type, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Messages.TryTake(out var m, 100) && m.Topic.Split('/')[2] == type) return m;
            }
            throw new TimeoutException($"No {type} received.");
        }

        public Task SendAsync(string topic, SparkplugPayload payload) =>
            Client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload.Encode())
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());

        public async ValueTask DisposeAsync()
        {
            if (Client.IsConnected) await Client.DisconnectAsync();
            Client.Dispose();
        }
    }

    private static (SparkplugEdgeNode Node, SparkplugDataModel Model, SparkplugSettings Settings) Create(int port, Action<SparkplugSettings>? configure = null)
    {
        var settings = new SparkplugSettings { Port = port, PublishIntervalMs = 50, ReconnectDelaySeconds = 1 };
        configure?.Invoke(settings);
        var model = new SparkplugDataModel();
        var node = new SparkplugEdgeNode(settings, model, new ServerContext(new TrafficLog(), new FaultSettings(), new ServerStatistics()));
        return (node, model, settings);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task PublishesBirthsDataAndAppliesCommands()
    {
        int port = FreePort();
        var (node, model, _) = Create(port);
        using var level = model.Bind("Tank1:Level", TagDataType.Float32, WordOrder.ABCD);
        using var setpoint = model.Bind("Tank1:Setpoint", TagDataType.Int32, WordOrder.ABCD);
        using var uptime = model.Bind("Uptime", TagDataType.UInt32, WordOrder.ABCD);
        level.Write(42.5f);

        await node.StartAsync(TestContext.Current.CancellationToken);
        await using var host = await Host.ConnectAsync(port);
        await WaitUntil(() => node.IsBorn);

        // The host connected after the first births; ask for a rebirth like a real primary host would.
        var rebirth = new SparkplugPayload { Timestamp = 1 };
        rebirth.Metrics.Add(new SparkplugMetric { Name = "Node Control/Rebirth", DataType = SparkplugDataType.Boolean, Value = true });
        await host.SendAsync("spBv1.0/ProtoSim/NCMD/EdgeNode1", rebirth);

        var nbirth = host.Next("NBIRTH").Payload;
        Assert.Equal(0UL, nbirth.Seq);
        Assert.Contains(nbirth.Metrics, m => m.Name == "bdSeq");
        Assert.Contains(nbirth.Metrics, m => m.Name == "Node Control/Rebirth");
        Assert.Contains(nbirth.Metrics, m => m.Name == "Uptime" && m.DataType == SparkplugDataType.UInt32);

        var (dtopic, dbirth) = host.Next("DBIRTH");
        Assert.Equal("spBv1.0/ProtoSim/DBIRTH/EdgeNode1/Tank1", dtopic);
        Assert.Equal(1UL, dbirth.Seq);
        var levelMetric = Assert.Single(dbirth.Metrics, m => m.Name == "Level");
        Assert.Equal(42.5f, levelMetric.ToClrValue());
        var spAlias = Assert.Single(dbirth.Metrics, m => m.Name == "Setpoint").Alias!.Value;

        // Report by exception, alias only.
        level.Write(10f);
        var ddata = host.Next("DDATA").Payload;
        var changed = Assert.Single(ddata.Metrics);
        Assert.Null(changed.Name);
        Assert.Equal(levelMetric.Alias, changed.Alias);
        Assert.Equal(10f, changed.ToClrValue());

        // DCMD by alias writes the tag, which is then reported back.
        var cmd = new SparkplugPayload { Timestamp = 1 };
        cmd.Metrics.Add(new SparkplugMetric { Alias = spAlias, DataType = SparkplugDataType.Int32, Value = unchecked((uint)-7) });
        await host.SendAsync("spBv1.0/ProtoSim/DCMD/EdgeNode1/Tank1", cmd);
        await WaitUntil(() => (int)setpoint.Read() == -7);
        var echo = host.Next("DDATA").Payload;
        Assert.Equal(-7, Assert.Single(echo.Metrics, m => m.Alias == spAlias).ToClrValue());

        // Stopping announces the death with the session's bdSeq.
        await node.StopAsync();
        var ndeath = host.Next("NDEATH").Payload;
        Assert.Equal(nbirth.Metrics.Single(m => m.Name == "bdSeq").Value, ndeath.Metrics.Single(m => m.Name == "bdSeq").Value);
        await node.DisposeAsync();
    }

    [Fact]
    public async Task AddingATagTriggersRebirthAndRemovingADeviceSendsDdeath()
    {
        int port = FreePort();
        var (node, model, _) = Create(port, s => s.UseAliases = false);
        await node.StartAsync(TestContext.Current.CancellationToken);
        await using var host = await Host.ConnectAsync(port);
        await WaitUntil(() => node.IsBorn);

        var pump = model.Bind("Pump1:Running", TagDataType.Bool, WordOrder.ABCD);
        var dbirth = host.Next("DBIRTH").Payload;
        var running = Assert.Single(dbirth.Metrics);
        Assert.Equal("Running", running.Name);
        Assert.Null(running.Alias);

        pump.Write(true);
        var ddata = host.Next("DDATA").Payload;
        Assert.Equal("Running", Assert.Single(ddata.Metrics).Name);

        pump.Dispose();
        var (topic, _) = host.Next("DDEATH");
        Assert.Equal("spBv1.0/ProtoSim/DDEATH/EdgeNode1/Pump1", topic);
        await node.DisposeAsync();
    }

    [Fact]
    public async Task WaitsForPrimaryHostBeforeBirth()
    {
        int port = FreePort();
        var (node, model, _) = Create(port, s => s.PrimaryHostId = "SCADA");
        using var t = model.Bind("Value", TagDataType.Int16, WordOrder.ABCD);
        await node.StartAsync(TestContext.Current.CancellationToken);
        await using var host = await Host.ConnectAsync(port);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(node.IsBorn);

        await host.Client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("spBv1.0/STATE/SCADA")
            .WithPayload(Encoding.UTF8.GetBytes("{\"online\":true,\"timestamp\":1}")).Build(), TestContext.Current.CancellationToken);
        host.Next("NBIRTH");
        Assert.True(node.IsBorn);

        await host.Client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("spBv1.0/STATE/SCADA")
            .WithPayload(Encoding.UTF8.GetBytes("{\"online\":false,\"timestamp\":2}")).Build(), TestContext.Current.CancellationToken);
        host.Next("NDEATH");
        await WaitUntil(() => !node.IsBorn);
        await node.DisposeAsync();
    }

    [Fact]
    public async Task ExternalBrokerUnreachableKeepsRetrying()
    {
        var (node, _, _) = Create(0, s => { s.BrokerMode = SparkplugBrokerMode.External; s.BrokerHost = "127.0.0.1"; s.BrokerPort = FreePort(); });
        await node.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Running, node.State);
        await WaitUntil(() => node.LastError != null);
        await node.StopAsync();
        Assert.Equal(ServerState.Stopped, node.State);
        await node.DisposeAsync();
    }
}
