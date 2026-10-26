using System.ComponentModel.DataAnnotations;

namespace ProtoSim.Protocols.OpcUa;

public sealed class OpcUaServerSettings
{
    [Display(Name = "Host name", GroupName = "Endpoint", Order = 1, Description = "Host name used in the endpoint URL. The server listens on all interfaces; use this machine's name if clients connect from other computers.")]
    public string HostName { get; set; } = "localhost";

    [Display(Name = "TCP port", GroupName = "Endpoint", Order = 2, Description = "Standard OPC UA port is 4840.")]
    [Range(1, 65535)]
    public int Port { get; set; } = 4840;

    [Display(Name = "Endpoint path", GroupName = "Endpoint", Order = 3, Description = "Optional path after the port, e.g. /ProtoSim. Leave empty for opc.tcp://host:port.")]
    public string EndpointPath { get; set; } = string.Empty;

    [Display(Name = "Application name", GroupName = "Application", Order = 10, Description = "Also used for the certificate subject and the application URI.")]
    public string ApplicationName { get; set; } = "ProtoSim OPC UA Server";

    [Display(Name = "Namespace URI", GroupName = "Application", Order = 11, Description = "Namespace of the simulated variables (normally namespace index 2).")]
    public string NamespaceUri { get; set; } = "urn:protosim:simulation";

    [Display(Name = "Root folder", GroupName = "Application", Order = 12, Description = "Folder under Objects that holds all simulated variables.")]
    public string RootFolder { get; set; } = "ProtoSim";

    [Display(Name = "Allow unsecured (None)", GroupName = "Security", Order = 20, Description = "Offer an endpoint without signing or encryption. Easiest for testing.")]
    public bool AllowSecurityNone { get; set; } = true;

    [Display(Name = "Basic256Sha256 Sign", GroupName = "Security", Order = 21)]
    public bool AllowSign { get; set; } = true;

    [Display(Name = "Basic256Sha256 Sign & Encrypt", GroupName = "Security", Order = 22)]
    public bool AllowSignAndEncrypt { get; set; } = true;

    [Display(Name = "Auto-accept client certificates", GroupName = "Security", Order = 23, Description = "Trust any client certificate. Turn off to require certificates in the trusted store (pki folder).")]
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;

    [Display(Name = "Allow anonymous", GroupName = "Users", Order = 30)]
    public bool AllowAnonymous { get; set; } = true;

    [Display(Name = "User name", GroupName = "Users", Order = 31, Description = "If set, clients may log in with this user name and password.")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password", GroupName = "Users", Order = 32)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Allow writes", GroupName = "Behavior", Order = 40, Description = "When off, variables are read-only for clients.")]
    public bool AllowWrites { get; set; } = true;

    [Display(Name = "Max sessions", GroupName = "Behavior", Order = 41)]
    [Range(1, 1000)]
    public int MaxSessions { get; set; } = 50;

    [Display(Name = "Min sampling interval (ms)", GroupName = "Behavior", Order = 42, Description = "Fastest sampling the server allows for monitored items.")]
    [Range(10, 60_000)]
    public int MinSamplingIntervalMs { get; set; } = 100;
}

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
