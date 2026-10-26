using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.ModbusTcp;

public sealed class ModbusTcpPlugin : IProtocolPlugin
{
    public string Id => "modbus-tcp";
    public string DisplayName => "Modbus TCP";
    public string Description => "Modbus TCP/IP server (slave) and client (master). Function codes 1-6, 8, 15, 16, 22, 23 and 43/14.";

    public bool SupportsServer => true;
    public bool SupportsClient => true;

    public Type ServerSettingsType => typeof(ModbusServerSettings);
    public Type ClientSettingsType => typeof(ModbusClientSettings);
    public Type ClientRequestType => typeof(ModbusClientRequest);

    public string AddressHelp => ModbusAddress.Help;

    public string DefaultTagAddress(int n) => $"HR:{100 + n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<ModbusServerSettings>().Select(s => s.Port).ToHashSet();
        int port = 502;
        while (used.Contains(port)) port = port == 502 ? 5020 : port + 1;
        return new ModbusServerSettings { Port = port };
    }

    public object CreateClientSettings() => new ModbusClientSettings();

    public object CreateClientSettingsFor(object serverSettings) =>
        new ModbusClientSettings { Host = "127.0.0.1", Port = ((ModbusServerSettings)serverSettings).Port };
    public object CreateClientRequest() => new ModbusClientRequest();

    public IDeviceDataModel CreateDataModel(object serverSettings) => new ModbusDataModel((ModbusServerSettings)serverSettings);

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new ModbusTcpServer((ModbusServerSettings)serverSettings, (ModbusDataModel)dataModel, context);

    public IProtocolClient CreateClient(object clientSettings, ClientContext context) =>
        new ModbusTcpClient((ModbusClientSettings)clientSettings, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Tank level", Address = "HR:0", DataType = TagDataType.Float32, Description = "Level in % (float, 2 registers)",
            Simulation = new SimulationSettings { Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Temperature", Address = "HR:2", DataType = TagDataType.Int16, Description = "Tenths of °C",
            Simulation = new SimulationSettings { Kind = SimulationKind.RandomWalk, Min = 180, Max = 260, Step = 3, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Production count", Address = "HR:10", DataType = TagDataType.UInt32,
            Simulation = new SimulationSettings { Kind = SimulationKind.Counter, Min = 0, Max = 1_000_000, Step = 1, IntervalMs = 1000 },
        };
        yield return new SimTag { Name = "Setpoint", Address = "HR:20", DataType = TagDataType.UInt16, Description = "Writable by clients" };
        yield return new SimTag
        {
            Name = "Flow rate", Address = "IR:0", DataType = TagDataType.UInt16,
            Simulation = new SimulationSettings { Kind = SimulationKind.Random, Min = 400, Max = 600, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Pump running", Address = "DI:0", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 },
        };
        yield return new SimTag { Name = "Start command", Address = "CO:0", DataType = TagDataType.Bool, Description = "Writable by clients" };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (ModbusServerSettings)serverSettings;
        return $"{s.BindAddress}:{s.Port} · unit {s.UnitIds}";
    }

    public string DescribeClient(object clientSettings)
    {
        var s = (ModbusClientSettings)clientSettings;
        return $"{s.Host}:{s.Port}";
    }
}
