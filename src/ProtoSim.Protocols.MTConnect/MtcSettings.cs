using System.ComponentModel.DataAnnotations;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.MTConnect;

public enum MtcFormat
{
    Xml,
    Json,
}

public sealed class MtcSettings : ITcpServerSettings
{
    [Display(Name = "Bind address", GroupName = "Network", Order = 1, Description = "IP address to listen on. 0.0.0.0 = all IPv4 interfaces, 127.0.0.1 = local only.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "HTTP port", GroupName = "Network", Order = 2, Description = "MTConnect agents commonly use 5000.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 5000;

    [Display(Name = "Max connections", GroupName = "Network", Order = 3)]
    [Range(1, 1000)]
    public int MaxConnections { get; set; } = 32;

    [Display(Name = "Idle timeout (s)", GroupName = "Network", Order = 4, Description = "Close keep-alive connections that send nothing for this long. 0 = never.")]
    [Range(0, 86_400)]
    public int IdleTimeoutSeconds { get; set; } = 120;

    [Display(Name = "Device name", GroupName = "Device", Order = 10, Description = "Used in URLs such as /Mill/current and in DeviceStream name.")]
    public string DeviceName { get; set; } = "Mill";

    [Display(Name = "Device UUID", GroupName = "Device", Order = 11)]
    public string DeviceUuid { get; set; } = Guid.NewGuid().ToString();

    [Display(Name = "Manufacturer", GroupName = "Device", Order = 12)]
    public string Manufacturer { get; set; } = "ProtoSim";

    [Display(Name = "Model", GroupName = "Device", Order = 13)]
    public string Model { get; set; } = "Virtual 3-axis mill";

    [Display(Name = "Serial number", GroupName = "Device", Order = 14)]
    public string SerialNumber { get; set; } = "PSIM-0001";

    [Display(Name = "Description", GroupName = "Device", Order = 15)]
    public string Description { get; set; } = "Simulated by ProtoSim";

    [Display(Name = "Schema version", GroupName = "Agent", Order = 20, Description = "MTConnect version reported in headers and namespaces, e.g. 2.0, 1.8 or 1.3.")]
    public string SchemaVersion { get; set; } = "2.0";

    [Display(Name = "Buffer size", GroupName = "Agent", Order = 21, Description = "Number of observations kept for sample requests.")]
    [Range(16, 1_000_000)]
    public int BufferSize { get; set; } = 10_000;

    [Display(Name = "Max sample count", GroupName = "Agent", Order = 22, Description = "Largest count accepted by a sample request.")]
    [Range(1, 1_000_000)]
    public int MaxSampleCount { get; set; } = 10_000;

    [Display(Name = "Record unchanged values", GroupName = "Agent", Order = 23,
        Description = "Off: like a real agent, a new observation is only added when a value changes. On: every simulation update is recorded.")]
    public bool RecordDuplicates { get; set; }

    [Display(Name = "Default format", GroupName = "Agent", Order = 24,
        Description = "Format when the request doesn't choose one. Clients choose with Accept: application/json or ?format=json / ?format=xml. JSON follows the reference agent's JSON version 2 layout.")]
    public MtcFormat DefaultFormat { get; set; } = MtcFormat.Xml;
}
