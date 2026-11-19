using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.Snmp;

public sealed class SnmpPlugin : IProtocolPlugin
{
    public string Id => "snmp";
    public string DisplayName => "SNMP agent";
    public string Description => "SNMPv1/v2c agent with the system group plus one scalar OID per tag: Get, GetNext, GetBulk, Set, community strings and v2c traps.";
    public Type ServerSettingsType => typeof(SnmpSettings);
    public Type? ClientSettingsType => typeof(SnmpManagerSettings);
    public Type? ClientRequestType => typeof(SnmpManagerRequest);
    public object CreateClientSettings() => new SnmpManagerSettings();
    public object CreateClientSettingsFor(object serverSettings)
    {
        var s = (SnmpSettings)serverSettings;
        return new SnmpManagerSettings
        {
            Host = "127.0.0.1", Port = s.Port, ReadCommunity = s.ReadCommunity, WriteCommunity = s.WriteCommunity,
            Version = s.AllowV2c ? SnmpManagerVersion.V2c : SnmpManagerVersion.V1,
        };
    }
    public object CreateClientRequest() => new SnmpManagerRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new SnmpManager((SnmpManagerSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (SnmpManagerSettings)clientSettings;
        return $"{s.Host}:{s.Port} · {s.Version}";
    }
    public string AddressHelp => SnmpAddress.Help;
    public string DefaultTagAddress(int n) => $"1.3.6.1.4.1.99999.1.{n}.0";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<SnmpSettings>().Select(s => s.Port).ToHashSet();
        int port = 161;
        while (used.Contains(port)) port = port == 161 ? 1161 : port + 1;
        return new SnmpSettings { Port = port };
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new SnmpDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new SnmpAgent((SnmpSettings)serverSettings, (SnmpDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Battery charge", Address = "1.3.6.1.4.1.99999.1.1.0:gauge32", DataType = TagDataType.UInt32, Description = "Percent",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 60, Max = 100, PeriodSeconds = 120, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Load", Address = "1.3.6.1.4.1.99999.1.2.0", DataType = TagDataType.Int32, Description = "Watts",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 200, Max = 900, Step = 15, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Packets in", Address = "1.3.6.1.4.1.99999.1.3.0:counter32", DataType = TagDataType.UInt32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Counter, Min = 0, Max = 4_000_000_000, Step = 37, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Temperature", Address = "1.3.6.1.4.1.99999.1.4.0", DataType = TagDataType.Float32, Description = "Sent as text (SNMP has no float type)",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 20, Max = 35, Step = 0.3, IntervalMs = 1000 },
        };
        yield return new SimTag { Name = "Output enabled", Address = "1.3.6.1.4.1.99999.1.5.0", DataType = TagDataType.Bool, Description = "TruthValue, writable with Set" };
        yield return new SimTag { Name = "Label", Address = "1.3.6.1.4.1.99999.1.6.0", DataType = TagDataType.String, Description = "Writable with Set" };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (SnmpSettings)serverSettings;
        return $"{s.BindAddress}:{s.Port}/udp · '{s.ReadCommunity}'";
    }
}
