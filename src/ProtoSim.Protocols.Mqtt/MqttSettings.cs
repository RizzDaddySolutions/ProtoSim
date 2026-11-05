using System.ComponentModel.DataAnnotations;

namespace ProtoSim.Protocols.Mqtt;

public sealed class MqttServerSettings
{
    [Display(Name = "Bind address", GroupName = "Network", Order = 1, Description = "IP address to listen on. 0.0.0.0 listens on all IPv4 interfaces.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "TCP port", GroupName = "Network", Order = 2, Description = "Standard MQTT port is 1883.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 1883;

    [Display(Name = "Allow anonymous", GroupName = "Security", Order = 10)]
    public bool AllowAnonymous { get; set; } = true;

    [Display(Name = "User name", GroupName = "Security", Order = 11)]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password", GroupName = "Security", Order = 12)]
    public string Password { get; set; } = string.Empty;
}
