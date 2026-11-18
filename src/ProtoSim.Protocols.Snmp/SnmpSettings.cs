using System.ComponentModel.DataAnnotations;

namespace ProtoSim.Protocols.Snmp;

public sealed class SnmpSettings
{
    [Display(Name = "Bind address", GroupName = "Network", Order = 1, Description = "IP address to listen on. 0.0.0.0 = all IPv4 interfaces, 127.0.0.1 = local only.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "UDP port", GroupName = "Network", Order = 2,
        Description = "SNMP uses 161. If the Windows SNMP service is installed it may already hold 161; use another port such as 1161 and point the manager at it.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 161;

    [Display(Name = "Read community", GroupName = "Security", Order = 10, Description = "Community string for Get, GetNext and GetBulk.")]
    public string ReadCommunity { get; set; } = "public";

    [Display(Name = "Write community", GroupName = "Security", Order = 11, Description = "Community string that also allows Set. Leave empty to make the agent read-only.")]
    public string WriteCommunity { get; set; } = "private";

    [Display(Name = "Allow SNMPv1", GroupName = "Security", Order = 12)]
    public bool AllowV1 { get; set; } = true;

    [Display(Name = "Allow SNMPv2c", GroupName = "Security", Order = 13)]
    public bool AllowV2c { get; set; } = true;

    [Display(Name = "sysDescr", GroupName = "System group", Order = 20)]
    public string SysDescr { get; set; } = "ProtoSim simulated SNMP agent";

    [Display(Name = "sysObjectID", GroupName = "System group", Order = 21, Description = "Identifies the device type; usually under the vendor's enterprise number.")]
    public string SysObjectId { get; set; } = "1.3.6.1.4.1.99999.1";

    [Display(Name = "sysContact", GroupName = "System group", Order = 22)]
    public string SysContact { get; set; } = string.Empty;

    [Display(Name = "sysName", GroupName = "System group", Order = 23)]
    public string SysName { get; set; } = "protosim";

    [Display(Name = "sysLocation", GroupName = "System group", Order = 24)]
    public string SysLocation { get; set; } = string.Empty;

    [Display(Name = "sysServices", GroupName = "System group", Order = 25, Description = "Bit mask of OSI layers served (72 = applications + transport).")]
    [Range(0, 127)]
    public int SysServices { get; set; } = 72;

    [Display(Name = "Trap receivers", GroupName = "Traps", Order = 30,
        Description = "Where to send SNMPv2c traps, e.g. 192.168.1.10 or 192.168.1.10:162, comma-separated. Empty = no traps.")]
    public string TrapReceivers { get; set; } = string.Empty;

    [Display(Name = "Trap community", GroupName = "Traps", Order = 31)]
    public string TrapCommunity { get; set; } = "public";

    [Display(Name = "Send coldStart on start", GroupName = "Traps", Order = 32)]
    public bool SendColdStart { get; set; } = true;

    [Display(Name = "Trap on Set", GroupName = "Traps", Order = 33,
        Description = "Send a trap (snmpTrapOID = the enterprise OID below .0.1) carrying the new values whenever a manager changes objects with Set.")]
    public bool TrapOnSet { get; set; }

    [Display(Name = "Max GetBulk varbinds", GroupName = "Limits", Order = 40, Description = "Upper limit on the variables in one GetBulk response.")]
    [Range(1, 10_000)]
    public int MaxBulkVariables { get; set; } = 500;
}
