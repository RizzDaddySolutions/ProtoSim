using System.Collections.Concurrent;
using System.Globalization;
using System.IO.BACnet;
using System.Text.RegularExpressions;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.Bacnet;

internal enum BacnetValueKind
{
    Analog,
    Binary,
    MultiState,
    CharacterString,
}

/// <summary>Per-object state that BACnet clients can change: command priorities, relinquish default, out-of-service.</summary>
internal sealed class BacnetObjectState
{
    public readonly object?[] Priority = new object?[16];
    public object? RelinquishDefault;
    public bool OutOfService;

    public bool IsCommanded => Priority.Any(p => p != null);
    public object? Effective => Priority.FirstOrDefault(p => p != null) ?? RelinquishDefault;

    public void Reset()
    {
        Array.Clear(Priority);
        RelinquishDefault = null;
        OutOfService = false;
    }
}

/// <summary>A BACnet object exposed by the device, backed by a tag value.</summary>
internal sealed record BacnetObjectInfo(string Key, BacnetObjectId Id, BacnetValueKind Kind, string Name, NamedTag Tag, BacnetObjectState State)
{
    public bool IsInput => Id.type is BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT;
    public bool IsCommandable => !IsInput;
}

/// <summary>Objects of a simulated BACnet device. Each tag is one object's Present_Value.</summary>
public sealed class BacnetDataModel : IDeviceDataModel
{
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BacnetObjectState> _states = new(StringComparer.Ordinal);
    private int _databaseRevision;

    public BacnetDataModel()
    {
        Store.TagsChanged += () => Interlocked.Increment(ref _databaseRevision);
    }

    public IReadOnlyList<IDataArea> Areas { get; } = [];

    internal NamedTagStore Store { get; } = new(StringComparer.Ordinal);

    /// <summary>Incremented whenever objects are added or removed (Database_Revision).</summary>
    internal uint DatabaseRevision => (uint)Volatile.Read(ref _databaseRevision);

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var a = BacnetAddressSpec.Parse(address);
        var kind = BacnetAddressSpec.KindOf(a.Type);
        switch (kind)
        {
            case BacnetValueKind.Analog when type is TagDataType.Bool or TagDataType.String:
                throw new FormatException("Analog objects (AI/AO/AV) need a numeric data type, e.g. Float32.");
            case BacnetValueKind.Binary when type != TagDataType.Bool:
                throw new FormatException("Binary objects (BI/BO/BV) need data type Bool.");
            case BacnetValueKind.MultiState when type is TagDataType.Bool or TagDataType.String or TagDataType.Float32 or TagDataType.Float64:
                throw new FormatException("Multi-state objects (MSI/MSO/MSV) need an integer data type, e.g. UInt16; values run from 1 to Number_Of_States.");
            case BacnetValueKind.CharacterString when type != TagDataType.String:
                throw new FormatException("CharacterString Value objects (CSV) need data type String.");
        }

        var binding = Store.Bind(a.Key, type);
        _names[a.Key] = a.Name ?? BacnetAddressSpec.DefaultName(a.Type, a.Instance);
        return binding;
    }

    internal IReadOnlyList<BacnetObjectInfo> Objects() =>
        Store.All().Select(ToInfo).OfType<BacnetObjectInfo>()
            .OrderBy(o => (uint)o.Id.type).ThenBy(o => o.Id.instance).ToList();

    internal BacnetObjectInfo? Find(BacnetObjectId id)
    {
        var tag = Store.Find(BacnetAddressSpec.KeyOf(id.type, id.instance));
        return tag == null ? null : ToInfo(tag);
    }

    internal BacnetObjectInfo? FindByName(string name) =>
        Objects().FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

    internal void ResetStates()
    {
        foreach (var s in _states.Values) s.Reset();
    }

    private BacnetObjectInfo? ToInfo(NamedTag tag)
    {
        if (!BacnetAddressSpec.TryParseKey(tag.Name, out var type, out var instance)) return null;
        return new BacnetObjectInfo(tag.Name, new BacnetObjectId(type, instance), BacnetAddressSpec.KindOf(type),
            _names.GetValueOrDefault(tag.Name) ?? BacnetAddressSpec.DefaultName(type, instance), tag,
            _states.GetOrAdd(tag.Name, _ => new BacnetObjectState()));
    }
}

/// <summary>Parses tag addresses such as "AV:3" or "AI:1/Zone Temperature".</summary>
internal readonly record struct BacnetAddressSpec(BacnetObjectTypes Type, uint Instance, string? Name)
{
    public const string Help =
        "Object type and instance, optionally followed by /object name: AI:1, AO:2, AV:3/Zone Setpoint (analog, numeric types), " +
        "BI:0, BO:0, BV:0 (binary, Bool), MSI:1, MSO:1, MSV:1 (multi-state, integer 1..N), CSV:1 (CharacterString Value, String). " +
        "Outputs and values are commandable with a 16-level priority array; inputs are read-only unless Out_Of_Service is set.";

    private static readonly Regex Form = new(@"^\s*([A-Za-z]+)\s*:\s*(\d+)\s*(?:/\s*(.+?)\s*)?$", RegexOptions.CultureInvariant);

    private static readonly (string Abbrev, BacnetObjectTypes Type)[] Types =
    [
        ("AI", BacnetObjectTypes.OBJECT_ANALOG_INPUT),
        ("AO", BacnetObjectTypes.OBJECT_ANALOG_OUTPUT),
        ("AV", BacnetObjectTypes.OBJECT_ANALOG_VALUE),
        ("BI", BacnetObjectTypes.OBJECT_BINARY_INPUT),
        ("BO", BacnetObjectTypes.OBJECT_BINARY_OUTPUT),
        ("BV", BacnetObjectTypes.OBJECT_BINARY_VALUE),
        ("MSI", BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT),
        ("MSO", BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT),
        ("MSV", BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE),
        ("CSV", BacnetObjectTypes.OBJECT_CHARACTERSTRING_VALUE),
    ];

    public string Key => KeyOf(Type, Instance);

    public static string KeyOf(BacnetObjectTypes type, uint instance) => $"{Abbrev(type)}:{instance}";

    public static string Abbrev(BacnetObjectTypes type) => Types.FirstOrDefault(t => t.Type == type).Abbrev ?? ((uint)type).ToString(CultureInfo.InvariantCulture);

    public static BacnetAddressSpec Parse(string text)
    {
        var m = Form.Match(text ?? string.Empty);
        if (!m.Success) throw new FormatException($"'{text}' is not a BACnet object address. {Help}");
        var abbrev = m.Groups[1].Value.ToUpperInvariant();
        var entry = Types.FirstOrDefault(t => t.Abbrev == abbrev);
        if (entry.Abbrev == null) throw new FormatException($"Unknown object type '{m.Groups[1].Value}'. Use AI, AO, AV, BI, BO, BV, MSI, MSO, MSV or CSV.");
        if (!uint.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance > 4_194_302)
            throw new FormatException("Object instances run from 0 to 4194302.");
        string? name = m.Groups[3].Success ? m.Groups[3].Value : null;
        return new BacnetAddressSpec(entry.Type, instance, name);
    }

    public static bool TryParseKey(string key, out BacnetObjectTypes type, out uint instance)
    {
        type = default;
        instance = 0;
        int colon = key.IndexOf(':');
        if (colon <= 0) return false;
        var entry = Types.FirstOrDefault(t => t.Abbrev == key[..colon]);
        if (entry.Abbrev == null || !uint.TryParse(key[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out instance)) return false;
        type = entry.Type;
        return true;
    }

    public static BacnetValueKind KindOf(BacnetObjectTypes type) => type switch
    {
        BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE => BacnetValueKind.Analog,
        BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE => BacnetValueKind.Binary,
        BacnetObjectTypes.OBJECT_CHARACTERSTRING_VALUE => BacnetValueKind.CharacterString,
        _ => BacnetValueKind.MultiState,
    };

    public static string DefaultName(BacnetObjectTypes type, uint instance) => type switch
    {
        BacnetObjectTypes.OBJECT_ANALOG_INPUT => "Analog Input ",
        BacnetObjectTypes.OBJECT_ANALOG_OUTPUT => "Analog Output ",
        BacnetObjectTypes.OBJECT_ANALOG_VALUE => "Analog Value ",
        BacnetObjectTypes.OBJECT_BINARY_INPUT => "Binary Input ",
        BacnetObjectTypes.OBJECT_BINARY_OUTPUT => "Binary Output ",
        BacnetObjectTypes.OBJECT_BINARY_VALUE => "Binary Value ",
        BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT => "Multi-state Input ",
        BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT => "Multi-state Output ",
        BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE => "Multi-state Value ",
        _ => "CharacterString Value ",
    } + instance.ToString(CultureInfo.InvariantCulture);
}
