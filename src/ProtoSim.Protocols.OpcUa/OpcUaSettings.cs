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
