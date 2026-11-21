using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.Mqtt;

public sealed class MqttPlugin : IProtocolPlugin
{
    public string Id => "mqtt";
    public string DisplayName => "MQTT";
    public string Description => "MQTT broker with topic-based simulated tags and external publish/subscribe clients.";
    public Type ServerSettingsType => typeof(MqttServerSettings);
    public Type? ClientSettingsType => typeof(MqttClientSettings);
    public Type? ClientRequestType => typeof(MqttClientRequest);
    public object CreateClientSettings() => new MqttClientSettings();
    public object CreateClientSettingsFor(object serverSettings) => new MqttClientSettings { Host = "127.0.0.1", Port = ((MqttServerSettings)serverSettings).Port };
    public object CreateClientRequest() => new MqttClientRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new MqttTopicClient((MqttClientSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (MqttClientSettings)clientSettings;
        return $"{s.Host}:{s.Port}{(s.UseTls ? " TLS" : "")}";
    }
    // ---- Network links: one subscription for every topic; writes publish the value as text ----

    public bool SupportsLinks => true;

    public string SuggestTagAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type, string nameHint) =>
        IProtocolPlugin.UniqueAddress("protosim/links/" + IProtocolPlugin.Slug(nameHint), existing);

    private static string LinkTopic(LinkPoint p)
    {
        var t = p.Address.Trim();
        if (t.Length == 0 || t.Contains('#') || t.Contains('+')) throw new FormatException("Link points need one topic without wildcards.");
        if (t.IndexOfAny([',', ';']) >= 0) throw new FormatException("Topics in links cannot contain ',' or ';'.");
        return t;
    }

    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points)
    {
        var topics = points.Select(LinkTopic).ToList();
        var request = new MqttClientRequest { Operation = MqttClientOperation.Subscribe, Topic = string.Join(",", topics.Distinct(StringComparer.Ordinal)) };
        return [new LinkRead(request, Enumerable.Range(0, topics.Count).ToList()) { RowAddresses = topics }];
    }

    public object CreateLinkWrite(LinkPoint point, object value) =>
        new MqttClientRequest { Operation = MqttClientOperation.Publish, Topic = LinkTopic(point), Payload = ValueConverter.Format(value) };

    public string AddressHelp => "MQTT topic, for example sensors/temperature or plant/pump/running.";
    public string DefaultTagAddress(int n) => $"protosim/tag{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<MqttServerSettings>().Select(s => s.Port).ToHashSet();
        int port = 1883;
        while (used.Contains(port)) port = port == 1883 ? 18830 : port + 1;
        return new MqttServerSettings { Port = port };
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new MqttDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new MqttServer((MqttServerSettings)serverSettings, (MqttDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Temperature", Address = "protosim/plant/temperature", DataType = TagDataType.Float32,
            Description = "Published as UTF-8 text in degrees Celsius.",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 18, Max = 26, Step = 0.2, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Tank level", Address = "protosim/plant/tank/level", DataType = TagDataType.Float32,
            Description = "Published as UTF-8 text percentage.",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Pump running", Address = "protosim/plant/pump/running", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 },
        };
        yield return new SimTag { Name = "Command", Address = "protosim/plant/pump/command", DataType = TagDataType.String, Description = "Writable by MQTT clients.", Writable = true };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (MqttServerSettings)serverSettings;
        return $"mqtt://{(s.BindAddress == "0.0.0.0" ? "localhost" : s.BindAddress)}:{s.Port}";
    }
}
