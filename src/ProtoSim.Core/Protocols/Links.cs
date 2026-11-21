using System.Reflection;
using ProtoSim.Core.Data;

namespace ProtoSim.Core.Protocols;

/// <summary>Which way values flow through a network link point.</summary>
public enum LinkDirection
{
    /// <summary>Source values are copied into the target tag.</summary>
    Read,

    /// <summary>As <see cref="Read"/>, and local changes to the target tag are written back to the source.</summary>
    ReadWrite,
}

/// <summary>A value on a remote server, in the client's address syntax (e.g. HR:0, ns=2;s=Tank.Level, plant/temp).</summary>
public sealed record LinkPoint(string Address, TagDataType DataType, WordOrder WordOrder = WordOrder.ABCD);

/// <summary>
/// One client request that reads several link points. By default result row k holds the value of <see cref="Points"/>[k];
/// set <see cref="RowAddresses"/> when rows must be matched by address instead (e.g. MQTT topics arrive in any order).
/// </summary>
public sealed record LinkRead(object Request, IReadOnlyList<int> Points)
{
    /// <summary>Row address for each entry of <see cref="Points"/>, matched case-insensitively.</summary>
    public IReadOnlyList<string>? RowAddresses { get; init; }

    /// <summary>Picks the row for entry k from all result rows (overrides <see cref="RowAddresses"/> and position).</summary>
    public Func<IReadOnlyList<ClientResultRow>, int, ClientResultRow?>? Row { get; init; }

    /// <summary>Turns a row into the value of entry k. Default: <see cref="ClientResultRow.Typed"/>, else the parsed text.</summary>
    public Func<int, ClientResultRow, object?>? Value { get; init; }

    /// <summary>
    /// Plans requests for points that can be read with one request per protocol "group key" (e.g. all OPC UA nodes in one
    /// Read, all SNMP OIDs in one Get). Points with an empty key get a request each.
    /// </summary>
    public static List<LinkRead> Grouped(IReadOnlyList<LinkPoint> points, Func<LinkPoint, string> key, Func<IReadOnlyList<LinkPoint>, object> request)
    {
        var reads = new List<LinkRead>();
        foreach (var g in points.Select((p, i) => (p, i)).GroupBy(x => key(x.p)))
        {
            if (g.Key.Length == 0)
                reads.AddRange(g.Select(x => new LinkRead(request([x.p]), [x.i])));
            else
                reads.Add(new LinkRead(request(g.Select(x => x.p).ToList()), g.Select(x => x.i).ToList()));
        }
        return reads;
    }
}

public static class LinkPoints
{
    /// <summary>True when the plugin can write this point back to its source (e.g. not a DNP3 or BACnet input).</summary>
    public static bool CanWrite(IProtocolPlugin plugin, LinkPoint point)
    {
        if (!plugin.SupportsLinkWrites) return false;
        try
        {
            plugin.CreateLinkWrite(point, ValueConverter.Coerce(null, point.DataType));
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException or InvalidCastException) { return false; }
    }
}

/// <summary>Helpers for pointing client settings made for a local device at another computer.</summary>
public static class LinkHost
{
    private static readonly string[] UrlProperties = ["EndpointUrl", "BaseUrl", "AgentUrl", "TokenUrl", "Url"];

    public static bool IsLocal(string? host) =>
        string.IsNullOrWhiteSpace(host) || host is "127.0.0.1" or "::1" or "localhost" or "0.0.0.0" or "*" or "+";

    /// <summary>
    /// Replaces the host in client settings: a string <c>Host</c> property, URL properties (EndpointUrl, BaseUrl...),
    /// and "ip:port" <c>DeviceAddress</c>. Hosts that already name another computer are left alone.
    /// </summary>
    public static object Retarget(object settings, string host)
    {
        if (LinkHost.IsLocal(host)) return settings;
        foreach (var p in settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(string) || !p.CanRead || !p.CanWrite) continue;
            var value = (string?)p.GetValue(settings) ?? string.Empty;
            if (p.Name == "Host")
            {
                if (IsLocal(value)) p.SetValue(settings, host);
            }
            else if (UrlProperties.Contains(p.Name))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsLocal(uri.Host))
                    p.SetValue(settings, new UriBuilder(uri) { Host = host }.Uri.ToString().TrimEnd('/') + (value.EndsWith('/') ? "/" : ""));
            }
            else if (p.Name == "DeviceAddress")
            {
                int colon = value.LastIndexOf(':');
                var h = colon > 0 ? value[..colon] : value;
                if (IsLocal(h)) p.SetValue(settings, colon > 0 ? host + value[colon..] : host);
            }
        }
        return settings;
    }
}
