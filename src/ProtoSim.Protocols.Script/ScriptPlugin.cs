using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.Script;

public sealed class ScriptPlugin : IProtocolPlugin
{
    public string Id => "scripted";
    public string DisplayName => "Scripted device";
    public string Description => "Text request/reply device over TCP, UDP or a serial port, defined by rules with tag values in the replies: instruments, scales, barcode readers, legacy ASCII protocols.";
    public Type ServerSettingsType => typeof(ScriptSettings);
    public string AddressHelp => ScriptDataModel.Help;
    public string DefaultTagAddress(int n) => $"Value{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<ScriptSettings>().Select(s => s.Port).ToHashSet();
        int port = 10001;
        while (used.Contains(port)) port++;
        return new ScriptSettings { Port = port };
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new ScriptDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context)
    {
        var s = (ScriptSettings)serverSettings;
        var m = (ScriptDataModel)dataModel;
        return s.Transport switch
        {
            ScriptTransport.Udp => new ScriptUdpServer(s, m, context),
            ScriptTransport.Serial => new ScriptSerialServer(s, m, context),
            _ => new ScriptTcpServer(s, m, context),
        };
    }

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Temperature", Address = "Temperature", DataType = TagDataType.Float32, Description = "MEAS:TEMP?",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 20, Max = 30, Step = 0.05, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Pressure", Address = "Pressure", DataType = TagDataType.Float32, Description = "MEAS:PRES?",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 990, Max = 1030, PeriodSeconds = 60, IntervalMs = 500 },
        };
        yield return new SimTag { Name = "Setpoint", Address = "Setpoint", DataType = TagDataType.Float64, Description = "SOUR:VOLT? / SOUR:VOLT <v>" };
        yield return new SimTag { Name = "Output", Address = "Output", DataType = TagDataType.Bool, Description = "OUTP? / OUTP ON|OFF" };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (ScriptSettings)serverSettings;
        return s.Transport switch
        {
            ScriptTransport.Serial => $"{s.PortName} {s.BaudRate} · scripted",
            ScriptTransport.Udp => $"{s.BindAddress}:{s.Port}/udp · scripted",
            _ => $"{s.BindAddress}:{s.Port}/tcp · scripted",
        };
    }
}
