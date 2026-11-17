using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ProtoSim.Protocols.MTConnect;

/// <summary>
/// Converts the agent's XML documents to JSON in the shape of the MTConnect reference agent's JSON version 2:
/// attributes become properties, element text becomes "value", single-occurrence containers (Header, Devices,
/// DataItems, Samples...) become objects and repeating elements are grouped into arrays by element name.
/// </summary>
internal static class MtcJson
{
    public const string ContentType = "application/json; charset=utf-8";

    /// <summary>Elements that occur at most once under their parent and are written as an object, not an array.</summary>
    private static readonly HashSet<string> Singletons = new(StringComparer.Ordinal)
    {
        "Header", "Description", "Devices", "Streams", "Assets", "Errors", "DataItems", "Components",
        "Samples", "Events", "Condition", "Configuration", "CuttingToolLifeCycle", "Measurements", "CutterStatus",
    };

    /// <summary>Attributes that are numbers in the JSON output.</summary>
    private static readonly HashSet<string> NumericAttributes = new(StringComparer.Ordinal)
    {
        "instanceId", "bufferSize", "nextSequence", "firstSequence", "lastSequence", "sequence", "assetBufferSize", "assetCount",
    };

    public static byte[] FromXml(byte[] xml, bool indent = true)
    {
        var root = XElement.Parse(System.Text.Encoding.UTF8.GetString(xml));
        var body = Convert(root, numericValue: false);
        body.Insert(0, "jsonVersion", 2);
        if (root.Element(root.Name.Namespace + "Header")?.Attribute("version") is { } v)
            body.Insert(1, "schemaVersion", string.Join('.', v.Value.Split('.').Take(2)));
        var doc = new JsonObject { [root.Name.LocalName] = body };
        return JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions { WriteIndented = indent });
    }

    private static JsonObject Convert(XElement e, bool numericValue)
    {
        var o = new JsonObject();
        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration))
            o[a.Name.LocalName] = NumericAttributes.Contains(a.Name.LocalName) && long.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? JsonValue.Create(n)
                : JsonValue.Create(a.Value);

        // Observations under Samples carry numeric values; everything else stays text.
        bool childrenNumeric = e.Name.LocalName == "Samples";
        foreach (var group in e.Elements().GroupBy(c => c.Name.LocalName))
        {
            if (Singletons.Contains(group.Key) && group.Count() == 1)
            {
                o[group.Key] = Convert(group.First(), childrenNumeric);
            }
            else
            {
                var array = new JsonArray();
                foreach (var c in group) array.Add(Convert(c, childrenNumeric));
                o[group.Key] = array;
            }
        }

        if (!e.HasElements && !string.IsNullOrEmpty(e.Value))
        {
            o["value"] = numericValue && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? JsonValue.Create(d)
                : JsonValue.Create(e.Value);
        }
        return o;
    }
}
