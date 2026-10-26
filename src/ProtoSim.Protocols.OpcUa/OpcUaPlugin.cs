using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.OpcUa;

public sealed class OpcUaPlugin : IProtocolPlugin
{
    public string Id => "opc-ua";
    public string DisplayName => "OPC UA";
    public string Description => "OPC UA server (opc.tcp, None/Sign/SignAndEncrypt, anonymous or user login) with live variables, plus a client for browse, read, write and subscriptions.";

    public bool SupportsServer => true;
    public bool SupportsClient => true;

    public Type ServerSettingsType => typeof(OpcUaServerSettings);
    public Type ClientSettingsType => typeof(OpcUaClientSettings);
    public Type ClientRequestType => typeof(OpcUaClientRequest);

    public string AddressHelp =>
        "Folder path such as Plant/Tank1/Level. It appears under Objects/<root folder> with NodeId ns=2;s=Plant/Tank1/Level. Folders are created automatically. Word order is ignored.";

    public string DefaultTagAddress(int n) => $"Tags/Tag{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<OpcUaServerSettings>().Select(s => s.Port).ToHashSet();
        int port = 4840;
        while (used.Contains(port)) port++;
        return new OpcUaServerSettings { Port = port };
    }

    public object CreateClientSettings() => new OpcUaClientSettings();

    public object CreateClientSettingsFor(object serverSettings) =>
        new OpcUaClientSettings { EndpointUrl = OpcUaServer.BuildEndpointUrl((OpcUaServerSettings)serverSettings) };
    public object CreateClientRequest() => new OpcUaClientRequest();

    public IDeviceDataModel CreateDataModel(object serverSettings) => new OpcUaDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new OpcUaServer((OpcUaServerSettings)serverSettings, (OpcUaDataModel)dataModel, context);

    public IProtocolClient CreateClient(object clientSettings, ClientContext context) =>
        new OpcUaClient((OpcUaClientSettings)clientSettings, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Tank level", Address = "Plant/Tank1/Level", DataType = TagDataType.Float64, Description = "%",
            Simulation = new SimulationSettings { Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Tank temperature", Address = "Plant/Tank1/Temperature", DataType = TagDataType.Float32, Description = "°C",
            Simulation = new SimulationSettings { Kind = SimulationKind.RandomWalk, Min = 18, Max = 26, Step = 0.2, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Pump running", Address = "Plant/Pump1/Running", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Pump speed", Address = "Plant/Pump1/Speed", DataType = TagDataType.Int32, Description = "rpm",
            Simulation = new SimulationSettings { Kind = SimulationKind.Triangle, Min = 900, Max = 1800, PeriodSeconds = 20, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Units produced", Address = "Plant/Line1/Count", DataType = TagDataType.UInt32,
            Simulation = new SimulationSettings { Kind = SimulationKind.Counter, Min = 0, Max = 1_000_000, Step = 1, IntervalMs = 1000 },
        };
        yield return new SimTag { Name = "Level setpoint", Address = "Plant/Setpoints/Level", DataType = TagDataType.Float64, Description = "Writable by clients" };
        yield return new SimTag { Name = "Operator message", Address = "Plant/Setpoints/Message", DataType = TagDataType.String, Description = "Writable by clients" };
    }

    public string DescribeServer(object serverSettings) => OpcUaServer.BuildEndpointUrl((OpcUaServerSettings)serverSettings);

    public string DescribeClient(object clientSettings) => ((OpcUaClientSettings)clientSettings).EndpointUrl;
}
