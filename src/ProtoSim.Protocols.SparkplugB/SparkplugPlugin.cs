using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.SparkplugB;

public sealed class SparkplugPlugin : IProtocolPlugin
{
    public string Id => "sparkplug-b";
    public string DisplayName => "Sparkplug B";
    public string Description => "Sparkplug B edge node with device metrics, births/deaths, report by exception, NCMD/DCMD writes and an optional embedded MQTT broker.";
    public Type ServerSettingsType => typeof(SparkplugSettings);
    public Type? ClientSettingsType => typeof(SparkplugHostSettings);
    public Type? ClientRequestType => typeof(SparkplugHostRequest);
    public object CreateClientSettings() => new SparkplugHostSettings();
    public object CreateClientSettingsFor(object serverSettings)
    {
        var s = (SparkplugSettings)serverSettings;
        return s.BrokerMode == SparkplugBrokerMode.Embedded
            ? new SparkplugHostSettings { Host = "127.0.0.1", Port = s.Port, GroupId = s.GroupId }
            : new SparkplugHostSettings { Host = s.BrokerHost, Port = s.BrokerPort, GroupId = s.GroupId, UserName = s.UserName, Password = s.Password };
    }
    public object CreateClientRequest() => new SparkplugHostRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new SparkplugHostClient((SparkplugHostSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (SparkplugHostSettings)clientSettings;
        return $"{s.Host}:{s.Port} · spBv1.0/{s.GroupId}";
    }
    // ---- Network links: "group/node[/device]:metric"; reads monitor births and data, writes send NCMD/DCMD ----

    public bool SupportsLinks => true;

    public string LinkSourceAddress(object serverSettings, string tagAddress)
    {
        var s = (SparkplugSettings)serverSettings;
        var a = SparkplugAddress.Parse(tagAddress);
        return a.Device == null ? $"{s.GroupId}/{s.EdgeNodeId}:{a.Metric}" : $"{s.GroupId}/{s.EdgeNodeId}/{a.Device}:{a.Metric}";
    }

    private static (string Group, string Node, string? Device, string Metric) LinkMetric(LinkPoint p)
    {
        var a = p.Address.Trim();
        int colon = a.IndexOf(':');
        var owner = colon > 0 ? a[..colon].Split('/') : [];
        if (colon <= 0 || colon == a.Length - 1 || owner.Length is < 2 or > 3 || owner.Any(o => o.Length == 0))
            throw new FormatException($"'{p.Address}' is not a Sparkplug metric such as ProtoSim/EdgeNode1:Inputs/Temperature or ProtoSim/EdgeNode1/Pump1:Speed.");
        return (owner[0], owner[1], owner.Length == 3 ? owner[2] : null, a[(colon + 1)..]);
    }

    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points)
    {
        var metrics = points.Select(LinkMetric).ToList();
        var keys = metrics.Select(m => m.Device == null ? $"{m.Group}/{m.Node}:{m.Metric}" : $"{m.Group}/{m.Node}/{m.Device}:{m.Metric}").ToList();
        var nodes = string.Join(",", metrics.Select(m => $"{m.Group}/{m.Node}").Distinct(StringComparer.Ordinal));
        return [new LinkRead(new SparkplugHostRequest { Operation = SparkplugHostOperation.Monitor, RebirthIfMissing = nodes }, Enumerable.Range(0, keys.Count).ToList()) { RowAddresses = keys }];
    }

    public object CreateLinkWrite(LinkPoint point, object value)
    {
        var m = LinkMetric(point);
        return new SparkplugHostRequest
        {
            Operation = SparkplugHostOperation.Command, GroupId = m.Group, EdgeNodeId = m.Node, DeviceId = m.Device ?? string.Empty,
            Metric = m.Metric, DataType = point.DataType, Value = ValueConverter.Format(ValueConverter.Coerce(value, point.DataType)),
        };
    }

    public string AddressHelp => SparkplugAddress.Help;
    public string DefaultTagAddress(int n) => $"Device1:Metric{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var others = existing.OfType<SparkplugSettings>().ToList();
        var usedPorts = others.Select(s => s.Port).ToHashSet();
        int port = 1883;
        while (usedPorts.Contains(port)) port = port == 1883 ? 18830 : port + 1;

        var usedNodes = others.Select(s => s.EdgeNodeId).ToHashSet(StringComparer.Ordinal);
        int n = 1;
        while (usedNodes.Contains($"EdgeNode{n}")) n++;
        return new SparkplugSettings { Port = port, EdgeNodeId = $"EdgeNode{n}" };
    }

    public object PrepareDuplicateSettings(object source, object copy, object fresh)
    {
        var c = (SparkplugSettings)copy;
        var f = (SparkplugSettings)fresh;
        c.Port = f.Port;
        c.EdgeNodeId = f.EdgeNodeId;
        return c;
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new SparkplugDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new SparkplugEdgeNode((SparkplugSettings)serverSettings, (SparkplugDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Node uptime", Address = "Properties/Uptime", DataType = TagDataType.UInt32, Description = "Edge node metric (seconds)",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Counter, Min = 0, Max = 4_000_000_000, Step = 1, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Tank level", Address = "Tank1:Level", DataType = TagDataType.Float32, Description = "Device metric (%)",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Temperature", Address = "Tank1:Temperature", DataType = TagDataType.Float64,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 18, Max = 26, Step = 0.2, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Pump running", Address = "Pump1:Running", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 },
        };
        yield return new SimTag { Name = "Pump speed setpoint", Address = "Pump1:Speed Setpoint", DataType = TagDataType.Int32, Description = "Writable with DCMD", Writable = true };
        yield return new SimTag { Name = "Batch ID", Address = "Pump1:Batch ID", DataType = TagDataType.String, Description = "Writable with DCMD", Writable = true };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (SparkplugSettings)serverSettings;
        string broker = s.BrokerMode == SparkplugBrokerMode.Embedded ? $"embedded :{s.Port}" : $"{s.BrokerHost}:{s.BrokerPort}";
        return $"spBv1.0/{s.GroupId}/{s.EdgeNodeId} · {broker}";
    }
}
