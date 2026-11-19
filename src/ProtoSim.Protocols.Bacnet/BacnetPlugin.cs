using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.Bacnet;

public sealed class BacnetPlugin : IProtocolPlugin
{
    public string Id => "bacnet-ip";
    public string DisplayName => "BACnet/IP";
    public string Description => "BACnet/IP device with analog, binary, multi-state and character-string objects, priority arrays, COV subscriptions and Who-Is/I-Am discovery.";
    public Type ServerSettingsType => typeof(BacnetSettings);
    public Type? ClientSettingsType => typeof(BacnetClientSettings);
    public Type? ClientRequestType => typeof(BacnetClientRequest);
    public object CreateClientSettings() => new BacnetClientSettings();
    public object CreateClientSettingsFor(object serverSettings)
    {
        var s = (BacnetSettings)serverSettings;
        string ip = string.IsNullOrWhiteSpace(s.LocalAddress) ? "127.0.0.1" : s.LocalAddress.Trim();
        return new BacnetClientSettings { DeviceAddress = $"{ip}:{s.Port}", DeviceInstance = s.DeviceInstance };
    }
    public object CreateClientRequest() => new BacnetClientRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new BacnetClientSession((BacnetClientSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (BacnetClientSettings)clientSettings;
        return $"{s.DeviceAddress} · device {s.DeviceInstance}";
    }
    public string AddressHelp => BacnetAddressSpec.Help;
    public string DefaultTagAddress(int n) => $"AV:{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var others = existing.OfType<BacnetSettings>().ToList();
        var usedPorts = others.Select(s => s.Port).ToHashSet();
        var usedInstances = others.Select(s => s.DeviceInstance).ToHashSet();
        int port = 47808;
        while (usedPorts.Contains(port)) port++;
        int instance = 1001;
        while (usedInstances.Contains(instance)) instance++;
        return new BacnetSettings { Port = port, DeviceInstance = instance, DeviceName = instance == 1001 ? "ProtoSim BACnet Device" : $"ProtoSim BACnet Device {instance}" };
    }

    public object PrepareDuplicateSettings(object source, object copy, object fresh)
    {
        var c = (BacnetSettings)copy;
        var f = (BacnetSettings)fresh;
        c.Port = f.Port;
        c.DeviceInstance = f.DeviceInstance;
        c.DeviceName = f.DeviceName;
        return c;
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new BacnetDataModel();

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new BacnetDevice((BacnetSettings)serverSettings, (BacnetDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Zone temperature", Address = "AI:1/Zone Temperature", DataType = TagDataType.Float32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 19, Max = 24, Step = 0.1, IntervalMs = 1000 },
        };
        yield return new SimTag { Name = "Zone setpoint", Address = "AV:1/Zone Setpoint", DataType = TagDataType.Float32, Description = "Commandable (priority array)" };
        yield return new SimTag
        {
            Name = "Supply fan status", Address = "BI:1/Supply Fan Status", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 20, IntervalMs = 500 },
        };
        yield return new SimTag { Name = "Supply fan command", Address = "BO:1/Supply Fan Command", DataType = TagDataType.Bool, Description = "Commandable" };
        yield return new SimTag
        {
            Name = "Damper position", Address = "AO:1/Damper Position", DataType = TagDataType.Float32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 60, IntervalMs = 500 },
        };
        yield return new SimTag { Name = "Occupancy mode", Address = "MSV:1/Occupancy Mode", DataType = TagDataType.UInt16, Description = "1..8" };
        yield return new SimTag { Name = "Status text", Address = "CSV:1/Status Text", DataType = TagDataType.String };
    }

    public string DescribeServer(object serverSettings)
    {
        var s = (BacnetSettings)serverSettings;
        string ip = string.IsNullOrWhiteSpace(s.LocalAddress) ? "auto" : s.LocalAddress.Trim();
        return $"device {s.DeviceInstance} · {ip}:{s.Port}/udp";
    }
}
