using System.ComponentModel.DataAnnotations;

namespace ProtoSim.Protocols.Dnp3;

public sealed class Dnp3ServerSettings
{
    [Display(Name = "Bind address", GroupName = "Endpoint", Order = 1)]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "TCP port", GroupName = "Endpoint", Order = 2)]
    [Range(1, 65535)]
    public int Port { get; set; } = 20000;

    [Display(Name = "Outstation address", GroupName = "DNP3", Order = 10)]
    [Range(1, 65519)]
    public int OutstationAddress { get; set; } = 10;

    [Display(Name = "Master address", GroupName = "DNP3", Order = 11)]
    [Range(1, 65519)]
    public int MasterAddress { get; set; } = 1;

    [Display(Name = "Allow unsolicited responses", GroupName = "DNP3", Order = 12)]
    public bool AllowUnsolicited { get; set; }

    [Display(Name = "Allow writes", GroupName = "Behavior", Order = 20)]
    public bool AllowWrites { get; set; } = true;

    [Display(Name = "Maximum points", GroupName = "Behavior", Order = 21)]
    [Range(1, 65535)]
    public int MaximumPoints { get; set; } = 1024;
}
