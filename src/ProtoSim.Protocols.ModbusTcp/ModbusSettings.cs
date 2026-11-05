using System.ComponentModel.DataAnnotations;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.ModbusTcp;

public enum UnmatchedUnitBehavior
{
    /// <summary>Send nothing (client times out), like a gateway with no device behind it.</summary>
    Ignore,
    /// <summary>Reply with exception 0x0B.</summary>
    GatewayTargetFailedToRespond,
    /// <summary>Reply with exception 0x0A.</summary>
    GatewayPathUnavailable,
}

public sealed class ModbusServerSettings
{
    [Display(Name = "Bind address", GroupName = "Network", Order = 1, Description = "IP address to listen on. 0.0.0.0 = all IPv4 interfaces, :: = all interfaces (dual stack), 127.0.0.1 = local only.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "TCP port", GroupName = "Network", Order = 2, Description = "Standard Modbus TCP port is 502.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 502;

    [Display(Name = "Max connections", GroupName = "Network", Order = 3, Description = "Further connection attempts are refused.")]
    [Range(1, 1000)]
    public int MaxConnections { get; set; } = 16;

    [Display(Name = "Idle timeout (s)", GroupName = "Network", Order = 4, Description = "Close connections that send nothing for this long. 0 = never.")]
    [Range(0, 86_400)]
    public int IdleTimeoutSeconds { get; set; }

    [Display(Name = "Unit IDs", GroupName = "Unit", Order = 10, Description = "Unit identifiers this device answers: * for all, or a list such as 1,2,10-20.")]
    public string UnitIds { get; set; } = "*";

    [Display(Name = "Other unit IDs", GroupName = "Unit", Order = 11, Description = "What to do with requests for unit IDs not listed above.")]
    public UnmatchedUnitBehavior UnmatchedUnitBehavior { get; set; } = UnmatchedUnitBehavior.GatewayTargetFailedToRespond;

    [Display(Name = "Unit 0 is broadcast", GroupName = "Unit", Order = 12, Description = "Process requests to unit 0 without replying (serial-line broadcast semantics).")]
    public bool TreatUnitZeroAsBroadcast { get; set; }

    [Display(Name = "Allow writes", GroupName = "Behavior", Order = 20, Description = "When off, write functions return Illegal Function (read-only device).")]
    public bool AllowWrites { get; set; } = true;

    [Display(Name = "Coils", GroupName = "Memory size", Order = 30, Description = "Number of valid coil addresses. Requests beyond return Illegal Data Address.")]
    [Range(1, 65536)]
    public int CoilCount { get; set; } = 65536;

    [Display(Name = "Discrete inputs", GroupName = "Memory size", Order = 31)]
    [Range(1, 65536)]
    public int DiscreteInputCount { get; set; } = 65536;

    [Display(Name = "Input registers", GroupName = "Memory size", Order = 32)]
    [Range(1, 65536)]
    public int InputRegisterCount { get; set; } = 65536;

    [Display(Name = "Holding registers", GroupName = "Memory size", Order = 33)]
    [Range(1, 65536)]
    public int HoldingRegisterCount { get; set; } = 65536;

    [Display(Name = "Vendor name", GroupName = "Device identification (FC 43/14)", Order = 40)]
    public string VendorName { get; set; } = "ProtoSim";

    [Display(Name = "Product code", GroupName = "Device identification (FC 43/14)", Order = 41)]
    public string ProductCode { get; set; } = "ISIM-MB";

    [Display(Name = "Revision", GroupName = "Device identification (FC 43/14)", Order = 42)]
    public string Revision { get; set; } = "1.0";

    [Display(Name = "Vendor URL", GroupName = "Device identification (FC 43/14)", Order = 43)]
    public string VendorUrl { get; set; } = "https://example.com";

    [Display(Name = "Product name", GroupName = "Device identification (FC 43/14)", Order = 44)]
    public string ProductName { get; set; } = "Modbus TCP Simulator";

    [Display(Name = "Model name", GroupName = "Device identification (FC 43/14)", Order = 45)]
    public string ModelName { get; set; } = "Virtual PLC";

    [Display(Name = "User application name", GroupName = "Device identification (FC 43/14)", Order = 46)]
    public string UserApplicationName { get; set; } = "Simulation";
}
