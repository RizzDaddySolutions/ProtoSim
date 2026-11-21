using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.MTConnect;

public sealed class MtcPlugin : IProtocolPlugin
{
    public string Id => "mtconnect";
    public string DisplayName => "MTConnect agent";
    public string Description => "MTConnect agent for a simulated machine tool: probe, current, sample and assets over HTTP, with a component tree, samples, events, conditions and a sequence-numbered buffer.";
    public Type ServerSettingsType => typeof(MtcSettings);
    public Type? ClientSettingsType => typeof(MtcClientSettings);
    public Type? ClientRequestType => typeof(MtcClientRequest);
    public object CreateClientSettings() => new MtcClientSettings();
    public object CreateClientSettingsFor(object serverSettings)
    {
        var s = (MtcSettings)serverSettings;
        return new MtcClientSettings { AgentUrl = $"http://localhost:{s.Port}", Device = s.DeviceName };
    }
    public object CreateClientRequest() => new MtcClientRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new MtcClient((MtcClientSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (MtcClientSettings)clientSettings;
        return string.IsNullOrWhiteSpace(s.Device) ? s.AgentUrl : $"{s.AgentUrl}/{s.Device}";
    }
    // ---- Network links: one "current" request per cycle, matched by data item id; MTConnect is read-only ----

    public bool SupportsLinks => true;
    public bool SupportsLinkWrites => false;

    public string LinkSourceAddress(object serverSettings, string tagAddress) => LinkId(tagAddress);

    /// <summary>A data item id, or the id inside a tag address such as Controller/Path/execution:EXECUTION.</summary>
    private static string LinkId(string address)
    {
        var a = address.Trim();
        if (a.Length == 0) throw new FormatException("Enter a data item id.");
        // "Axes/Linear[X]/Xact:POSITION:SAMPLE" → "Xact": the last path level before the type.
        int colon = a.IndexOf(':');
        var path = colon < 0 ? a : a[..colon];
        int slash = path.LastIndexOf('/');
        var id = (slash < 0 ? path : path[(slash + 1)..]).Trim();
        if (id.Length == 0) throw new FormatException($"'{address}' has no data item id.");
        return id;
    }

    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points)
    {
        var ids = points.Select(p => LinkId(p.Address)).ToList();
        return [new LinkRead(new MtcClientRequest { Operation = MtcClientOperation.Current }, Enumerable.Range(0, ids.Count).ToList())
        {
            RowAddresses = ids,
            Value = (_, row) => row.Value == "UNAVAILABLE" ? null : row.Typed,
        }];
    }

    public string AddressHelp => MtcAddress.Help;
    public string DefaultTagAddress(int n) => $"Controller/Path/item{n}:MESSAGE";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var others = existing.OfType<MtcSettings>().ToList();
        var usedPorts = others.Select(s => s.Port).ToHashSet();
        int port = 5000;
        while (usedPorts.Contains(port)) port++;
        var usedNames = others.Select(s => s.DeviceName).ToHashSet(StringComparer.Ordinal);
        string name = "Mill";
        for (int i = 2; usedNames.Contains(name); i++) name = $"Mill{i}";
        return new MtcSettings { Port = port, DeviceName = name };
    }

    public object PrepareDuplicateSettings(object source, object copy, object fresh)
    {
        var c = (MtcSettings)copy;
        var f = (MtcSettings)fresh;
        c.Port = f.Port;
        c.DeviceName = f.DeviceName;
        c.DeviceUuid = Guid.NewGuid().ToString();
        return c;
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new MtcDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new MtcAgent((MtcSettings)serverSettings, (MtcDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "X actual", Address = "Axes/Linear[X]/Xact:POSITION.ACTUAL", DataType = TagDataType.Float64,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = -150, Max = 150, PeriodSeconds = 20, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Y actual", Address = "Axes/Linear[Y]/Yact:POSITION.ACTUAL", DataType = TagDataType.Float64,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Triangle, Min = -100, Max = 100, PeriodSeconds = 15, IntervalMs = 500 },
        };
        yield return new SimTag
        {
            Name = "Spindle speed", Address = "Axes/Rotary[C]/Sspeed:ROTARY_VELOCITY.ACTUAL", DataType = TagDataType.Float32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 7800, Max = 8200, Step = 25, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Spindle load", Address = "Axes/Rotary[C]/Sload:LOAD", DataType = TagDataType.Float32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Random, Min = 20, Max = 65, IntervalMs = 1000 },
        };
        yield return new SimTag { Name = "Execution", Address = "Controller/Path/execution:EXECUTION", DataType = TagDataType.String, Description = "ACTIVE, READY, STOPPED, INTERRUPTED..." };
        yield return new SimTag { Name = "Controller mode", Address = "Controller/mode:CONTROLLER_MODE", DataType = TagDataType.String, Description = "AUTOMATIC, MANUAL, MANUAL_DATA_INPUT..." };
        yield return new SimTag { Name = "Program", Address = "Controller/Path/program:PROGRAM", DataType = TagDataType.String };
        yield return new SimTag
        {
            Name = "Part count", Address = "Controller/Path/partcount:PART_COUNT", DataType = TagDataType.UInt32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Counter, Min = 0, Max = 1_000_000, Step = 1, IntervalMs = 15_000 },
        };
        yield return new SimTag { Name = "E-stop", Address = "Controller/estop:EMERGENCY_STOP", DataType = TagDataType.Bool, Description = "false = ARMED, true = TRIGGERED" };
        yield return new SimTag { Name = "System condition", Address = "Controller/system:SYSTEM", DataType = TagDataType.String, Description = "NORMAL, WARNING or FAULT" };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (MtcSettings)serverSettings;
        string host = s.BindAddress is "0.0.0.0" or "" or "*" ? "localhost" : s.BindAddress;
        return $"http://{host}:{s.Port}/{s.DeviceName}/current";
    }
}
