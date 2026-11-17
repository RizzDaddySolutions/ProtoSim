using System.Globalization;
using System.Net;
using Lextm.SharpSnmpLib;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.Snmp;

/// <summary>SNMP syntax a tag is exposed with.</summary>
internal enum SnmpSyntax
{
    Integer,
    Counter32,
    Gauge32,
    Unsigned32,
    TimeTicks,
    Counter64,
    String,
    IpAddress,
    TruthValue,
}

/// <summary>Objects of a simulated SNMP agent: each tag is one scalar OID.</summary>
public sealed class SnmpDataModel : IDeviceDataModel
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SnmpSyntax> _syntax = new(StringComparer.Ordinal);

    public IReadOnlyList<IDataArea> Areas { get; } = [];

    internal NamedTagStore Store { get; } = new(StringComparer.Ordinal);

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var a = SnmpAddress.Parse(address, type);
        var binding = Store.Bind(a.Oid, type);
        _syntax[a.Oid] = a.Syntax;
        return binding;
    }

    internal SnmpSyntax SyntaxOf(NamedTag tag) => _syntax.GetValueOrDefault(tag.Name, SnmpAddress.DefaultSyntax(tag.DataType));
}

/// <summary>A tag address: a numeric OID with an optional :syntax suffix.</summary>
internal readonly record struct SnmpAddress(string Oid, SnmpSyntax Syntax)
{
    public const string SystemPrefix = "1.3.6.1.2.1.1.";

    public const string Help =
        "Numeric OID of a scalar, usually ending in .0, e.g. 1.3.6.1.4.1.99999.1.1.0. Add :syntax to choose the SNMP type: " +
        ":integer, :counter32, :gauge32, :unsigned32, :timeticks, :counter64, :string, :ipaddress, :truthvalue. " +
        "Defaults: integers → INTEGER (UInt32 → Gauge32, UInt64 → Counter64), Bool → TruthValue, floats and strings → OCTET STRING. " +
        "The system group (1.3.6.1.2.1.1) is built in.";

    public static SnmpAddress Parse(string text, TagDataType type)
    {
        var s = (text ?? string.Empty).Trim();
        SnmpSyntax? syntax = null;
        int colon = s.IndexOf(':');
        if (colon >= 0)
        {
            var name = s[(colon + 1)..].Trim().ToLowerInvariant();
            syntax = name switch
            {
                "integer" or "integer32" or "int" => SnmpSyntax.Integer,
                "counter32" or "counter" => SnmpSyntax.Counter32,
                "gauge32" or "gauge" => SnmpSyntax.Gauge32,
                "unsigned32" => SnmpSyntax.Unsigned32,
                "timeticks" => SnmpSyntax.TimeTicks,
                "counter64" => SnmpSyntax.Counter64,
                "string" or "octetstring" => SnmpSyntax.String,
                "ipaddress" or "ip" => SnmpSyntax.IpAddress,
                "truthvalue" or "bool" => SnmpSyntax.TruthValue,
                _ => throw new FormatException($"Unknown SNMP syntax '{name}'. {Help}"),
            };
            s = s[..colon].Trim();
        }

        var oid = NormalizeOid(s);
        if ((oid + ".").StartsWith(SystemPrefix, StringComparison.Ordinal) || oid == SystemPrefix.TrimEnd('.'))
            throw new FormatException("The system group (1.3.6.1.2.1.1) is built in; set sysDescr, sysName etc. in the server settings.");

        var sx = syntax ?? DefaultSyntax(type);
        bool ok = sx switch
        {
            SnmpSyntax.TruthValue => type == TagDataType.Bool,
            SnmpSyntax.IpAddress => type == TagDataType.String,
            SnmpSyntax.String => true,
            _ => type is not (TagDataType.String or TagDataType.Float32 or TagDataType.Float64),
        };
        if (!ok) throw new FormatException($"{type} tags can't use the {sx} syntax.");
        return new SnmpAddress(oid, sx);
    }

    public static SnmpSyntax DefaultSyntax(TagDataType type) => type switch
    {
        TagDataType.Bool => SnmpSyntax.TruthValue,
        TagDataType.UInt32 => SnmpSyntax.Gauge32,
        TagDataType.UInt64 or TagDataType.Int64 => SnmpSyntax.Counter64,
        TagDataType.Float32 or TagDataType.Float64 or TagDataType.String => SnmpSyntax.String,
        _ => SnmpSyntax.Integer,
    };

    public static string NormalizeOid(string text)
    {
        var s = (text ?? string.Empty).Trim().TrimStart('.');
        var parts = s.Split('.');
        if (parts.Length < 2) throw new FormatException($"'{text}' is not a numeric OID such as 1.3.6.1.4.1.99999.1.1.0. {Help}");
        foreach (var p in parts)
            if (!uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new FormatException($"'{text}' is not a numeric OID; every part must be a whole number.");
        if (parts[0] is not ("0" or "1" or "2")) throw new FormatException("OIDs start with 0, 1 or 2.");
        return string.Join('.', parts.Select(p => uint.Parse(p, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Encodes a tag value with its syntax.</summary>
    public static ISnmpData Encode(SnmpSyntax syntax, object value)
    {
        double d() => ValueConverter.ToDouble(value);
        uint u32() => (uint)Math.Clamp(d(), 0, uint.MaxValue);
        return syntax switch
        {
            SnmpSyntax.Integer => new Integer32((int)Math.Clamp(d(), int.MinValue, int.MaxValue)),
            SnmpSyntax.Counter32 => new Counter32(u32()),
            SnmpSyntax.Gauge32 => new Gauge32(u32()),
            SnmpSyntax.Unsigned32 => new Gauge32(u32()),
            SnmpSyntax.TimeTicks => new TimeTicks(u32()),
            SnmpSyntax.Counter64 => new Counter64(value is ulong ul ? ul : (ulong)Math.Max(0, Convert.ToInt64(value, CultureInfo.InvariantCulture))),
            SnmpSyntax.TruthValue => new Integer32(value is true ? 1 : 2),
            SnmpSyntax.IpAddress => IPAddress.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? new IP(ip.GetAddressBytes())
                : new IP(new byte[4]),
            _ => new OctetString(ValueConverter.Format(value)),
        };
    }

    /// <summary>Converts a Set value; null when its type doesn't match the syntax.</summary>
    public static object? Decode(SnmpSyntax syntax, ISnmpData data, TagDataType tagType)
    {
        try
        {
            object? raw = (syntax, data) switch
            {
                (SnmpSyntax.Integer, Integer32 i) => i.ToInt32(),
                (SnmpSyntax.Counter32, Counter32 c) => c.ToUInt32(),
                (SnmpSyntax.Gauge32 or SnmpSyntax.Unsigned32, Gauge32 g) => g.ToUInt32(),
                (SnmpSyntax.TimeTicks, TimeTicks t) => t.ToUInt32(),
                (SnmpSyntax.Counter64, Counter64 c) => c.ToUInt64(),
                (SnmpSyntax.TruthValue, Integer32 i) => i.ToInt32() switch { 1 => true, 2 => false, _ => null },
                (SnmpSyntax.IpAddress, IP ip) => ip.ToString(),
                (SnmpSyntax.String, OctetString s) => s.ToString(),
                _ => null,
            };
            return raw == null ? null : ValueConverter.Coerce(raw, tagType);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    public static SnmpType TypeOf(SnmpSyntax syntax) => syntax switch
    {
        SnmpSyntax.Integer or SnmpSyntax.TruthValue => SnmpType.Integer32,
        SnmpSyntax.Counter32 => SnmpType.Counter32,
        SnmpSyntax.Gauge32 or SnmpSyntax.Unsigned32 => SnmpType.Gauge32,
        SnmpSyntax.TimeTicks => SnmpType.TimeTicks,
        SnmpSyntax.Counter64 => SnmpType.Counter64,
        SnmpSyntax.IpAddress => SnmpType.IPAddress,
        _ => SnmpType.OctetString,
    };
}

/// <summary>Orders OIDs numerically, arc by arc.</summary>
internal sealed class OidComparer : IComparer<uint[]>
{
    public static readonly OidComparer Instance = new();

    public int Compare(uint[]? x, uint[]? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            int c = x[i].CompareTo(y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}
