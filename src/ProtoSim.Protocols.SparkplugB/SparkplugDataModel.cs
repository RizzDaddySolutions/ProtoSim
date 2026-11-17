using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.SparkplugB;

/// <summary>Metric values of a simulated Sparkplug B edge node and its devices.</summary>
public sealed class SparkplugDataModel : IDeviceDataModel
{
    public IReadOnlyList<IDataArea> Areas { get; } = [];

    /// <summary>Keyed by "metric" (node) or "device:metric". Sparkplug names are case-sensitive.</summary>
    internal NamedTagStore Store { get; } = new(StringComparer.Ordinal);

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var a = SparkplugAddress.Parse(address);
        return Store.Bind(a.Key, type);
    }
}

/// <summary>A tag address: "Metric/Path" for an edge node metric, "Device:Metric/Path" for a device metric.</summary>
internal readonly record struct SparkplugAddress(string? Device, string Metric)
{
    public const string Help =
        "Metric name for an edge node metric, e.g. Inputs/Temperature. Prefix a device ID and a colon for a device metric, e.g. Pump1:Outputs/Speed. " +
        "Device IDs can't contain '/', '+' or '#'. bdSeq and Node Control/... are reserved.";

    public string Key => Device == null ? Metric : Device + ":" + Metric;

    public static SparkplugAddress Parse(string text)
    {
        var s = (text ?? string.Empty).Trim();
        string? device = null;
        int colon = s.IndexOf(':');
        if (colon >= 0)
        {
            device = s[..colon].Trim();
            s = s[(colon + 1)..].Trim();
            if (device.Length == 0 || device.IndexOfAny(['/', '+', '#']) >= 0)
                throw new FormatException("Device IDs must be non-empty and can't contain '/', '+' or '#'.");
        }
        if (s.Length == 0) throw new FormatException("Enter a metric name, e.g. Inputs/Temperature or Pump1:Speed.");
        if (s.Any(char.IsControl)) throw new FormatException("Metric names can't contain control characters.");
        if (device == null && (s == "bdSeq" || s.StartsWith("Node Control/", StringComparison.Ordinal)))
            throw new FormatException($"'{s}' is reserved by Sparkplug.");
        return new SparkplugAddress(device, s);
    }

    public static SparkplugAddress FromKey(string key)
    {
        int colon = key.IndexOf(':');
        return colon < 0 ? new SparkplugAddress(null, key) : new SparkplugAddress(key[..colon], key[(colon + 1)..]);
    }
}
