using System.ComponentModel.DataAnnotations;

namespace ProtoSim.OpcUa.Tests;

public sealed class OpcUaClientSettings
{
    [Display(Name = "Endpoint URL", GroupName = "Connection", Order = 1)]
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840";

    [Display(Name = "Use security", GroupName = "Connection", Order = 2, Description = "Pick the most secure endpoint the server offers. Off = SecurityMode None.")]
    public bool UseSecurity { get; set; }

    [Display(Name = "Auto-accept server certificate", GroupName = "Connection", Order = 3)]
    public bool AutoAcceptServerCertificate { get; set; } = true;

    [Display(Name = "User name", GroupName = "Connection", Order = 4, Description = "Leave empty for anonymous login.")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password", GroupName = "Connection", Order = 5)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Session timeout (ms)", GroupName = "Timing", Order = 10)]
    [Range(1000, 3_600_000)]
    public int SessionTimeoutMs { get; set; } = 60_000;

    [Display(Name = "Operation timeout (ms)", GroupName = "Timing", Order = 11)]
    [Range(100, 600_000)]
    public int OperationTimeoutMs { get; set; } = 5_000;
}

public enum OpcUaOperation
{
    Read,
    Write,
    Browse,
    Subscribe,
}

public sealed class OpcUaClientRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1, Description = "Subscribe creates monitored items; poll it to see the latest notifications.")]
    public OpcUaOperation Operation { get; set; } = OpcUaOperation.Browse;

    [Display(Name = "Node IDs", GroupName = "Request", Order = 2, Description = "One or more node IDs separated by commas, e.g. ns=2;s=Plant/Tank1/Level. Browse starts at the Objects folder when empty.")]
    public string NodeIds { get; set; } = string.Empty;

    [Display(Name = "Values", GroupName = "Request", Order = 3, Description = "Write: one value for all nodes, or one per node separated by '|'. Converted to each node's data type.")]
    public string Values { get; set; } = string.Empty;

    [Display(Name = "Browse depth", GroupName = "Request", Order = 4)]
    [Range(1, 10)]
    public int BrowseDepth { get; set; } = 4;

    [Display(Name = "Sampling interval (ms)", GroupName = "Request", Order = 5, Description = "Subscribe: sampling and publishing interval.")]
    [Range(10, 600_000)]
    public int SamplingIntervalMs { get; set; } = 500;
}
