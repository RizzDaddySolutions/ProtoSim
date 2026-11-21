using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.OpcUa;

public sealed class OpcUaPlugin : IProtocolPlugin
{
    public string Id => "opc-ua";
    public string DisplayName => "OPC UA";
    public string Description => "OPC UA server (opc.tcp, None/Sign/SignAndEncrypt, anonymous or user login) with live variables, plus a client for browse, read, write and subscriptions.";

    public Type ServerSettingsType => typeof(OpcUaServerSettings);
    public Type? ClientSettingsType => typeof(OpcUaClientSettings);
    public Type? ClientRequestType => typeof(OpcUaClientRequest);

    public object CreateClientSettings() => new OpcUaClientSettings();

    public object CreateClientSettingsFor(object serverSettings) =>
        new OpcUaClientSettings { EndpointUrl = OpcUaServer.BuildEndpointUrl((OpcUaServerSettings)serverSettings) };

    public object CreateClientRequest() => new OpcUaClientRequest();

    public IProtocolClient CreateClient(object clientSettings, ClientContext context) =>
        new OpcUaClient((OpcUaClientSettings)clientSettings, context);

    public string DescribeClient(object clientSettings) => ((OpcUaClientSettings)clientSettings).EndpointUrl;

    // ---- Network links: node IDs; a plain path such as BOB.MARY.PV means ns=2;s=BOB.MARY.PV (ProtoSim's namespace) ----

    public bool SupportsLinks => true;

    public string SuggestTagAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type, string nameHint)
    {
        var name = new string(nameHint.Trim().Select(c => c is ':' or ';' or '<' or '>' or '&' or '.' or '/' or '\\' || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return IProtocolPlugin.UniqueAddress("Links/" + (name.Length == 0 ? "Value" : name), existing);
    }

    public string LinkSourceAddress(object serverSettings, string tagAddress) => $"ns=2;s={OpcUaDataModel.NormalizePath(tagAddress)}";

    internal static string LinkNodeId(string address)
    {
        var a = address.Trim();
        if (a.Length == 0) throw new FormatException("Enter a node ID.");
        if (a.Contains(',') || a.Contains('|')) throw new FormatException("Node IDs in links cannot contain ',' or '|'.");
        bool nodeId = a.StartsWith("ns=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase) ||
                      a.StartsWith("i=", StringComparison.Ordinal) || a.StartsWith("s=", StringComparison.Ordinal) ||
                      a.StartsWith("g=", StringComparison.Ordinal) || a.StartsWith("b=", StringComparison.Ordinal);
        return nodeId ? a : $"ns=2;s={OpcUaDataModel.NormalizePath(a)}";
    }

    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points)
    {
        var ids = points.Select(p => LinkNodeId(p.Address)).ToList();
        // One Read for every node; rows come back in request order.
        return [new LinkRead(new OpcUaClientRequest { Operation = OpcUaOperation.Read, NodeIds = string.Join(",", ids) }, Enumerable.Range(0, ids.Count).ToList())];
    }

    public object CreateLinkWrite(LinkPoint point, object value) =>
        new OpcUaClientRequest { Operation = OpcUaOperation.Write, NodeIds = LinkNodeId(point.Address), Values = ValueConverter.Format(value) };

    public string AddressHelp =>
        "Path with levels separated by . or /, such as BOB.MARY.SUE.PV or Plant/Tank1/Level. Each level is a folder under Objects (or under the root folder, if one is set), so BOB.MARY.SUE.PV browses as Objects > BOB > MARY > SUE > PV with NodeId ns=2;s=BOB.MARY.SUE.PV. Word order is ignored.";

    public string DefaultTagAddress(int n) => $"Tags/Tag{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<OpcUaServerSettings>().Select(s => s.Port).ToHashSet();
        int port = 4840;
        while (used.Contains(port)) port++;
        return new OpcUaServerSettings { Port = port };
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new OpcUaDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new OpcUaServer((OpcUaServerSettings)serverSettings, (OpcUaDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Tank level", Address = "Plant/Tank1/Level", DataType = TagDataType.Float64, Description = "%",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Tank temperature", Address = "Plant/Tank1/Temperature", DataType = TagDataType.Float32, Description = "°C",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 18, Max = 26, Step = 0.2, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Pump running", Address = "Plant/Pump1/Running", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Pump speed", Address = "Plant/Pump1/Speed", DataType = TagDataType.Int32, Description = "rpm",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Triangle, Min = 900, Max = 1800, PeriodSeconds = 20, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Units produced", Address = "Plant/Line1/Count", DataType = TagDataType.UInt32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Counter, Min = 0, Max = 1_000_000, Step = 1, IntervalMs = 1000 },
        };
        yield return new SimTag { Name = "Level setpoint", Address = "Plant/Setpoints/Level", DataType = TagDataType.Float64, Description = "Writable by clients", Writable = true };
        yield return new SimTag { Name = "Operator message", Address = "Plant/Setpoints/Message", DataType = TagDataType.String, Description = "Writable by clients", Writable = true };
    }

    public string DescribeServer(object serverSettings) => OpcUaServer.BuildEndpointUrl((OpcUaServerSettings)serverSettings);
}
