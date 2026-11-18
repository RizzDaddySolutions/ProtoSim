using System.ComponentModel.DataAnnotations;

namespace ProtoSim.Protocols.SparkplugB;

public enum SparkplugBrokerMode
{
    /// <summary>ProtoSim runs its own MQTT broker and the edge node connects to it.</summary>
    Embedded,
    /// <summary>The edge node connects to another broker (Mosquitto, HiveMQ, Ignition's MQTT Distributor...).</summary>
    External,
}

public sealed class SparkplugSettings
{
    [Display(Name = "Broker", GroupName = "Broker", Order = 1, Description = "Embedded: ProtoSim hosts an MQTT broker on the port below. External: connect to the broker host and port below.")]
    public SparkplugBrokerMode BrokerMode { get; set; } = SparkplugBrokerMode.Embedded;

    [Display(Name = "Embedded broker port", GroupName = "Broker", Order = 2, Description = "TCP port of the embedded broker (all interfaces). Standard MQTT is 1883.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 1883;

    [Display(Name = "External broker host", GroupName = "Broker", Order = 3)]
    public string BrokerHost { get; set; } = "localhost";

    [Display(Name = "External broker port", GroupName = "Broker", Order = 4)]
    [Range(1, 65535)]
    public int BrokerPort { get; set; } = 1883;

    [Display(Name = "User name", GroupName = "Broker", Order = 5, Description = "Credentials for the external broker. Leave empty for anonymous.")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password", GroupName = "Broker", Order = 6)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Reconnect delay (s)", GroupName = "Broker", Order = 7)]
    [Range(1, 3600)]
    public int ReconnectDelaySeconds { get; set; } = 5;

    [Display(Name = "Group ID", GroupName = "Sparkplug", Order = 10)]
    public string GroupId { get; set; } = "ProtoSim";

    [Display(Name = "Edge node ID", GroupName = "Sparkplug", Order = 11)]
    public string EdgeNodeId { get; set; } = "EdgeNode1";

    [Display(Name = "Primary host ID", GroupName = "Sparkplug", Order = 12,
        Description = "When set, the edge node waits for this host application's STATE message to be online before publishing births, and sends NDEATH when it goes offline. Leave empty to publish right away.")]
    public string PrimaryHostId { get; set; } = string.Empty;

    [Display(Name = "Use aliases", GroupName = "Sparkplug", Order = 13, Description = "Births carry name and alias; data messages carry only the alias.")]
    public bool UseAliases { get; set; } = true;

    [Display(Name = "Publish interval (ms)", GroupName = "Sparkplug", Order = 14,
        Description = "Report by exception: changed metrics are collected for this long, then sent in one NDATA/DDATA per device.")]
    [Range(10, 60_000)]
    public int PublishIntervalMs { get; set; } = 100;

    internal string Namespace => "spBv1.0";
    internal string NodeTopic(string type) => $"{Namespace}/{GroupId}/{type}/{EdgeNodeId}";
    internal string DeviceTopic(string type, string device) => $"{Namespace}/{GroupId}/{type}/{EdgeNodeId}/{device}";

    internal void Validate()
    {
        static void Check(string value, string what)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['/', '+', '#']) >= 0)
                throw new InvalidOperationException($"{what} must be non-empty and can't contain '/', '+' or '#'.");
        }
        Check(GroupId, "Group ID");
        Check(EdgeNodeId, "Edge node ID");
        if (!string.IsNullOrEmpty(PrimaryHostId)) Check(PrimaryHostId, "Primary host ID");
        if (BrokerMode == SparkplugBrokerMode.External && string.IsNullOrWhiteSpace(BrokerHost))
            throw new InvalidOperationException("Enter the external broker host.");
    }
}
