using ProtoSim.Core.Data;
using System.IO.Ports;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Modbus RTU framing (unit + PDU + CRC) over a TCP connection, as used by serial device servers and gateways.</summary>
public sealed class ModbusRtuOverTcpPlugin : IProtocolPlugin
{
    public string Id => "modbus-rtu-tcp";
    public string DisplayName => "Modbus RTU over TCP";
    public string Description => "Modbus RTU frames (with CRC, no MBAP header) over TCP, like a serial device server: a slave, or a master that polls one. Same function codes and memory as Modbus TCP.";
    public Type ServerSettingsType => typeof(ModbusRtuOverTcpSettings);
    public Type? ClientSettingsType => typeof(ModbusRtuOverTcpClientSettings);
    public Type? ClientRequestType => typeof(ModbusClientRequest);
    public object CreateClientSettings() => new ModbusRtuOverTcpClientSettings();
    public object CreateClientSettingsFor(object serverSettings) =>
        new ModbusRtuOverTcpClientSettings { Host = "127.0.0.1", Port = ((ModbusRtuOverTcpSettings)serverSettings).Port };
    public object CreateClientRequest() => new ModbusClientRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) =>
        new ModbusRtuOverTcpClient((ModbusRtuOverTcpClientSettings)clientSettings, context);
    public string DescribeClient(object clientSettings)
    {
        var s = (ModbusRtuOverTcpClientSettings)clientSettings;
        return $"{s.Host}:{s.Port} · RTU";
    }
    public string AddressHelp => ModbusAddress.Help;
    public string DefaultTagAddress(int n) => $"HR:{100 + n}";
    public bool SupportsWordOrder => true;

    public bool SupportsLinks => true;
    public string LinkSourceAddress(object serverSettings, string tagAddress) => ModbusLinks.SourceAddress(serverSettings, tagAddress);
    public string SuggestTagAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type, string nameHint) =>
        ModbusLinks.SuggestAddress(existing, type);
    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points) => ModbusLinks.CreateReads(points);
    public object CreateLinkWrite(LinkPoint point, object value) => ModbusLinks.CreateWrite(point, value);

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<ModbusRtuOverTcpSettings>().Select(s => s.Port).ToHashSet();
        int port = 502;
        while (used.Contains(port)) port = port == 502 ? 5020 : port + 1;
        return new ModbusRtuOverTcpSettings { Port = port };
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new ModbusDataModel((ModbusDeviceSettings)serverSettings);

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new ModbusRtuOverTcpServer((ModbusRtuOverTcpSettings)serverSettings, (ModbusDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags() => new ModbusTcpPlugin().CreateSampleTags();

    public string DescribeServer(object serverSettings)
    {
        var s = (ModbusRtuOverTcpSettings)serverSettings;
        return $"{s.BindAddress}:{s.Port} · RTU · unit {s.UnitIds}";
    }
}

/// <summary>Modbus RTU slave on a serial port.</summary>
public sealed class ModbusRtuSerialPlugin : IProtocolPlugin
{
    public string Id => "modbus-rtu";
    public string DisplayName => "Modbus RTU (serial)";
    public string Description => "Modbus RTU slave or master on a COM port (RS-232/RS-485 or a virtual COM port pair). Same function codes and memory as Modbus TCP.";
    public Type ServerSettingsType => typeof(ModbusRtuSerialSettings);
    public Type? ClientSettingsType => typeof(ModbusRtuSerialClientSettings);
    public Type? ClientRequestType => typeof(ModbusClientRequest);
    public object CreateClientSettings()
    {
        string[] ports;
        try { ports = SerialPort.GetPortNames(); }
        catch (Exception) { ports = []; }
        return new ModbusRtuSerialClientSettings { PortName = ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "COM1" };
    }
    public object CreateClientRequest() => new ModbusClientRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) =>
        new ModbusRtuSerialClient((ModbusRtuSerialClientSettings)clientSettings, context);
    public string DescribeClient(object clientSettings) => ((ModbusRtuSerialClientSettings)clientSettings).Describe();
    public string AddressHelp => ModbusAddress.Help;
    public string DefaultTagAddress(int n) => $"HR:{100 + n}";
    public bool SupportsWordOrder => true;

    public bool SupportsLinks => true;
    public string LinkSourceAddress(object serverSettings, string tagAddress) => ModbusLinks.SourceAddress(serverSettings, tagAddress);
    public string SuggestTagAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type, string nameHint) =>
        ModbusLinks.SuggestAddress(existing, type);
    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points) => ModbusLinks.CreateReads(points);
    public object CreateLinkWrite(LinkPoint point, object value) => ModbusLinks.CreateWrite(point, value);

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var others = existing.OfType<ModbusRtuSerialSettings>().ToList();
        var usedPorts = others.Select(s => s.PortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] available;
        try { available = SerialPort.GetPortNames(); }
        catch (Exception) { available = []; }
        var port = available.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault(p => !usedPorts.Contains(p));
        if (port != null) return new ModbusRtuSerialSettings { PortName = port };

        // Several slaves can share one bus: reuse the port name with the next unit ID.
        var last = others.LastOrDefault();
        if (last == null) return new ModbusRtuSerialSettings();
        int unit = others.Count + 1;
        return new ModbusRtuSerialSettings { PortName = last.PortName, UnitIds = unit.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    /// <summary>Copies keep the port; they only clash if both run at once.</summary>
    public object PrepareDuplicateSettings(object source, object copy, object fresh) => copy;

    public IDeviceDataModel CreateDataModel(object serverSettings) => new ModbusDataModel((ModbusDeviceSettings)serverSettings);

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new ModbusRtuSerialServer((ModbusRtuSerialSettings)serverSettings, (ModbusDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags() => new ModbusTcpPlugin().CreateSampleTags();

    public string DescribeServer(object serverSettings)
    {
        var s = (ModbusRtuSerialSettings)serverSettings;
        return $"{s.Describe()} · unit {s.UnitIds}";
    }
}
