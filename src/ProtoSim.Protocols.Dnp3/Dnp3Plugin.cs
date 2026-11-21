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
    public Type? ClientSettingsType => typeof(Dnp3MasterSettings);
    public Type? ClientRequestType => typeof(Dnp3MasterRequest);
    public object CreateClientSettings() => new Dnp3MasterSettings();
    public object CreateClientSettingsFor(object serverSettings)
    {
        var s = (Dnp3ServerSettings)serverSettings;
        return new Dnp3MasterSettings { Host = "127.0.0.1", Port = s.Port, MasterAddress = s.MasterAddress, OutstationAddress = s.OutstationAddress };
    }
    public object CreateClientRequest() => new Dnp3MasterRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new Dnp3Master((Dnp3MasterSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (Dnp3MasterSettings)clientSettings;
        return $"{s.Host}:{s.Port} · outstation {s.OutstationAddress}";
    }
    // ---- Network links: one integrity poll per cycle; BO:/AO: points are written with direct-operate commands ----

    public bool SupportsLinks => true;

    private static (string Type, int Index) LinkPointOf(LinkPoint p)
    {
        var parts = p.Address.Trim().ToUpperInvariant().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index > 65535)
            throw new FormatException($"'{p.Address}' is not a DNP3 point such as AI:0, BI:3, C:1, BO:0 or AO:2.");
        if (parts[0] is not ("BI" or "DBI" or "BO" or "C" or "FC" or "AI" or "AO"))
            throw new FormatException($"'{parts[0]}' is not a DNP3 point type (BI, DBI, BO, C, FC, AI, AO).");
        return (parts[0], index);
    }

    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points)
    {
        var keys = points.Select(p => { var (t, i) = LinkPointOf(p); return $"{t}:{i}"; }).ToList();
        return [new LinkRead(new Dnp3MasterRequest { Operation = Dnp3MasterOperation.IntegrityPoll }, Enumerable.Range(0, keys.Count).ToList()) { RowAddresses = keys }];
    }

    public object CreateLinkWrite(LinkPoint point, object value)
    {
        var (type, index) = LinkPointOf(point);
        return type switch
        {
            "BO" => new Dnp3MasterRequest
            {
                Operation = Dnp3MasterOperation.BinaryCommand, Start = index, SelectBeforeOperate = false,
                BinaryCommand = (bool)ValueConverter.Coerce(value, TagDataType.Bool) ? Dnp3BinaryCommand.LatchOn : Dnp3BinaryCommand.LatchOff,
            },
            "AO" => new Dnp3MasterRequest { Operation = Dnp3MasterOperation.AnalogCommand, Start = index, SelectBeforeOperate = false, AnalogValue = ValueConverter.ToDouble(value) },
            _ => throw new NotSupportedException($"{type} points are inputs; only BO and AO points can be written."),
        };
    }

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
