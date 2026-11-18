using System.ComponentModel.DataAnnotations;

namespace ProtoSim.Protocols.Bacnet;

public sealed class BacnetSettings
{
    [Display(Name = "Local IP address", GroupName = "Network", Order = 1,
        Description = "IP address of the network interface to use. Leave empty to pick it automatically; on a PC with several interfaces (VPN, Hyper-V, WSL...) the start error lists the choices. Use 127.0.0.1 for local-only testing.")]
    public string LocalAddress { get; set; } = string.Empty;

    [Display(Name = "UDP port", GroupName = "Network", Order = 2, Description = "BACnet/IP uses 47808 (0xBAC0). Only one program per port per interface unless it allows sharing.")]
    [Range(1, 65535)]
    public int Port { get; set; } = 47808;

    [Display(Name = "Announce on start", GroupName = "Network", Order = 3, Description = "Broadcast I-Am when the device starts.")]
    public bool AnnounceOnStart { get; set; } = true;

    [Display(Name = "Unicast I-Am replies", GroupName = "Network", Order = 4,
        Description = "Also send I-Am straight to whoever sent Who-Is. Helps clients on the same PC or behind NAT, where broadcasts don't arrive.")]
    public bool UnicastIAm { get; set; } = true;

    [Display(Name = "Device instance", GroupName = "Device", Order = 10, Description = "Device object instance number (0-4194302); must be unique on the BACnet network.")]
    [Range(0, 4_194_302)]
    public int DeviceInstance { get; set; } = 1001;

    [Display(Name = "Device name", GroupName = "Device", Order = 11)]
    public string DeviceName { get; set; } = "ProtoSim BACnet Device";

    [Display(Name = "Vendor name", GroupName = "Device", Order = 12)]
    public string VendorName { get; set; } = "ProtoSim";

    [Display(Name = "Vendor identifier", GroupName = "Device", Order = 13, Description = "ASHRAE-assigned vendor ID reported in I-Am and the device object.")]
    [Range(0, 65535)]
    public int VendorIdentifier { get; set; } = 0;

    [Display(Name = "Model name", GroupName = "Device", Order = 14)]
    public string ModelName { get; set; } = "Virtual BACnet Controller";

    [Display(Name = "Firmware revision", GroupName = "Device", Order = 15)]
    public string FirmwareRevision { get; set; } = "1.0";

    [Display(Name = "Application software version", GroupName = "Device", Order = 16)]
    public string ApplicationSoftwareVersion { get; set; } = "1.0";

    [Display(Name = "Location", GroupName = "Device", Order = 17)]
    public string Location { get; set; } = string.Empty;

    [Display(Name = "Description", GroupName = "Device", Order = 18)]
    public string Description { get; set; } = "Simulated by ProtoSim";

    [Display(Name = "Password", GroupName = "Device", Order = 19,
        Description = "Password for DeviceCommunicationControl and ReinitializeDevice. Leave empty to accept any password.")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "COV increment", GroupName = "Objects", Order = 20, Description = "Analog objects send a COV notification when the value moves by at least this much.")]
    [Range(0, 1_000_000)]
    public double CovIncrement { get; set; } = 0.1;

    [Display(Name = "Multi-state states", GroupName = "Objects", Order = 21, Description = "Number_Of_States for multi-state objects; values run from 1 to this.")]
    [Range(1, 1000)]
    public int MultiStateCount { get; set; } = 8;

    [Display(Name = "Engineering units", GroupName = "Objects", Order = 22, Description = "BACnet units enumeration for analog objects, e.g. 62 = °C, 64 = °F, 98 = percent, 95 = no units.")]
    [Range(0, 65535)]
    public int AnalogUnits { get; set; } = 95;
}
