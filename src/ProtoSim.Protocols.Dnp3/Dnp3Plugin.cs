using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.Dnp3;

public sealed class Dnp3Plugin : IProtocolPlugin
{
    public string Id => "dnp3";
    public string DisplayName => "DNP3";
    public string Description => "DNP3 TCP outstation with binary inputs, analog inputs, counters, commands, and simulated values.";
    public Type ServerSettingsType => typeof(Dnp3ServerSettings);
    public string AddressHelp => "BI:<index>, AI:<index>, or C:<index>";
    public string DefaultTagAddress(int n) => $"AI:{n - 1}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<Dnp3ServerSettings>().Select(s => s.Port).ToHashSet();
        int port = 20000;
        while (used.Contains(port)) port++;
        return new Dnp3ServerSettings { Port = port };
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new Dnp3DataModel((Dnp3ServerSettings)serverSettings);

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new Dnp3Server((Dnp3ServerSettings)serverSettings, (Dnp3DataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag { Name = "Tank level", Address = "AI:0", DataType = TagDataType.Float32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 250 } };
        yield return new SimTag { Name = "Temperature", Address = "AI:1", DataType = TagDataType.Float32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 18, Max = 26, Step = 0.2, IntervalMs = 1000 } };
        yield return new SimTag { Name = "Production count", Address = "C:0", DataType = TagDataType.UInt32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Counter, Min = 0, Max = 1_000_000, Step = 1, IntervalMs = 1000 } };
        yield return new SimTag { Name = "Pump running", Address = "BI:0", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 } };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (Dnp3ServerSettings)serverSettings;
        return $"dnp3://{(s.BindAddress == "0.0.0.0" ? "localhost" : s.BindAddress)}:{s.Port} · outstation {s.OutstationAddress}";
    }
}
