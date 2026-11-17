using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.MTConnect;

internal enum MtcCategory
{
    Sample,
    Event,
    Condition,
}

/// <summary>One component level in a data item's path, e.g. Axes or Linear[X].</summary>
internal readonly record struct MtcComponent(string Type, string? Name)
{
    public string Key => Name ?? Type;
    public string DisplayName => Name ?? Type;
}

/// <summary>A data item declared by a tag address.</summary>
internal sealed record MtcDataItem(string Id, IReadOnlyList<MtcComponent> Path, string Type, string? SubType, MtcCategory Category, string? Units)
{
    public string Name => Id;
}

/// <summary>Data items of a simulated MTConnect device. Each tag is one data item; values become observations.</summary>
public sealed class MtcDataModel : IDeviceDataModel
{
    private readonly ConcurrentDictionary<string, MtcDataItem> _items = new(StringComparer.Ordinal);

    public IReadOnlyList<IDataArea> Areas { get; } = [];

    /// <summary>Keyed by data item id.</summary>
    internal NamedTagStore Store { get; } = new(StringComparer.Ordinal);

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var item = MtcAddress.Parse(address, type);
        if (_items.TryGetValue(item.Id, out var existing) && Store.Find(item.Id) != null && !SameShape(existing, item))
            throw new FormatException($"Data item id '{item.Id}' is already used by {MtcAddress.Format(existing)}; ids must be unique.");
        var binding = Store.Bind(item.Id, type);
        _items[item.Id] = item;
        return binding;
    }

    private static bool SameShape(MtcDataItem a, MtcDataItem b) =>
        a.Type == b.Type && a.SubType == b.SubType && a.Category == b.Category && a.Path.SequenceEqual(b.Path);

    /// <summary>Data items whose tags exist, in a stable order.</summary>
    internal IReadOnlyList<(MtcDataItem Item, NamedTag Tag)> Items() =>
        Store.All().Select(t => _items.TryGetValue(t.Name, out var i) ? (i, t) : default)
            .Where(x => x.i != null)
            .OrderBy(x => string.Join("/", x.i.Path.Select(p => p.Key)), StringComparer.Ordinal)
            .ThenBy(x => x.i.Id, StringComparer.Ordinal)
            .Select(x => (x.i, x.t))
            .ToList();

    internal MtcDataItem? Find(string id) => Store.Find(id) != null && _items.TryGetValue(id, out var i) ? i : null;
}

internal static class MtcAddress
{
    public const string AvailabilityId = "avail";

    public const string Help =
        "Component path, data item id and type: Axes/Linear[X]/Xact:POSITION, Controller/Path/execution:EXECUTION, Controller/system:SYSTEM:CONDITION. " +
        "Components are MTConnect types (Axes, Controller, Path, Linear, Rotary, Systems...), with an optional [name]. " +
        "Add .SUBTYPE to the type (POSITION.ACTUAL) and :SAMPLE, :EVENT or :CONDITION to override the category. " +
        "Leave out the path for a device-level data item (e.g. partcount:PART_COUNT).";

    private static readonly Regex Segment = new(@"^([A-Z][A-Za-z]*)(?:\[([^\[\]/]+)\])?$", RegexOptions.CultureInvariant);
    private static readonly Regex Id = new(@"^[A-Za-z_][A-Za-z0-9_\-.]*$", RegexOptions.CultureInvariant);
    private static readonly Regex TypeForm = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Known types: category and default units.</summary>
    private static readonly Dictionary<string, (MtcCategory Category, string? Units)> Known = new(StringComparer.Ordinal)
    {
        ["POSITION"] = (MtcCategory.Sample, "MILLIMETER"),
        ["TEMPERATURE"] = (MtcCategory.Sample, "CELSIUS"),
        ["ROTARY_VELOCITY"] = (MtcCategory.Sample, "REVOLUTION/MINUTE"),
        ["SPINDLE_SPEED"] = (MtcCategory.Sample, "REVOLUTION/MINUTE"),
        ["LOAD"] = (MtcCategory.Sample, "PERCENT"),
        ["PATH_FEEDRATE"] = (MtcCategory.Sample, "MILLIMETER/SECOND"),
        ["AXIS_FEEDRATE"] = (MtcCategory.Sample, "MILLIMETER/SECOND"),
        ["VELOCITY"] = (MtcCategory.Sample, "MILLIMETER/SECOND"),
        ["ACCELERATION"] = (MtcCategory.Sample, "MILLIMETER/SECOND^2"),
        ["ANGLE"] = (MtcCategory.Sample, "DEGREE"),
        ["ANGULAR_VELOCITY"] = (MtcCategory.Sample, "DEGREE/SECOND"),
        ["PRESSURE"] = (MtcCategory.Sample, "PASCAL"),
        ["VOLTAGE_AC"] = (MtcCategory.Sample, "VOLT"),
        ["VOLTAGE_DC"] = (MtcCategory.Sample, "VOLT"),
        ["AMPERAGE_AC"] = (MtcCategory.Sample, "AMPERE"),
        ["AMPERAGE_DC"] = (MtcCategory.Sample, "AMPERE"),
        ["WATTAGE"] = (MtcCategory.Sample, "WATT"),
        ["ELECTRICAL_ENERGY"] = (MtcCategory.Sample, "WATT_SECOND"),
        ["FREQUENCY"] = (MtcCategory.Sample, "HERTZ"),
        ["FLOW"] = (MtcCategory.Sample, "LITER/SECOND"),
        ["TORQUE"] = (MtcCategory.Sample, "NEWTON_METER"),
        ["HUMIDITY_RELATIVE"] = (MtcCategory.Sample, "PERCENT"),
        ["FILL_LEVEL"] = (MtcCategory.Sample, "PERCENT"),
        ["CONCENTRATION"] = (MtcCategory.Sample, "PERCENT"),
        ["ACCUMULATED_TIME"] = (MtcCategory.Sample, "SECOND"),

        ["AVAILABILITY"] = (MtcCategory.Event, null),
        ["EXECUTION"] = (MtcCategory.Event, null),
        ["CONTROLLER_MODE"] = (MtcCategory.Event, null),
        ["EMERGENCY_STOP"] = (MtcCategory.Event, null),
        ["PROGRAM"] = (MtcCategory.Event, null),
        ["PROGRAM_COMMENT"] = (MtcCategory.Event, null),
        ["LINE"] = (MtcCategory.Event, null),
        ["LINE_NUMBER"] = (MtcCategory.Event, null),
        ["BLOCK"] = (MtcCategory.Event, null),
        ["PART_COUNT"] = (MtcCategory.Event, null),
        ["PART_ID"] = (MtcCategory.Event, null),
        ["TOOL_NUMBER"] = (MtcCategory.Event, null),
        ["TOOL_ASSET_ID"] = (MtcCategory.Event, null),
        ["MESSAGE"] = (MtcCategory.Event, null),
        ["OPERATOR_ID"] = (MtcCategory.Event, null),
        ["PATH_FEEDRATE_OVERRIDE"] = (MtcCategory.Event, null),
        ["ROTARY_VELOCITY_OVERRIDE"] = (MtcCategory.Event, null),
        ["SPINDLE_INTERLOCK"] = (MtcCategory.Event, null),
        ["DOOR_STATE"] = (MtcCategory.Event, null),
        ["CHUCK_STATE"] = (MtcCategory.Event, null),
        ["AXIS_STATE"] = (MtcCategory.Event, null),
        ["FUNCTIONAL_MODE"] = (MtcCategory.Event, null),
        ["PALLET_ID"] = (MtcCategory.Event, null),
        ["ACTIVE_AXES"] = (MtcCategory.Event, null),
        ["POWER_STATE"] = (MtcCategory.Event, null),
        ["PROGRAM_EDIT"] = (MtcCategory.Event, null),
        ["ROTARY_MODE"] = (MtcCategory.Event, null),

        ["SYSTEM"] = (MtcCategory.Condition, null),
        ["LOGIC_PROGRAM"] = (MtcCategory.Condition, null),
        ["MOTION_PROGRAM"] = (MtcCategory.Condition, null),
        ["COMMUNICATIONS"] = (MtcCategory.Condition, null),
        ["HARDWARE"] = (MtcCategory.Condition, null),
        ["ACTUATOR"] = (MtcCategory.Condition, null),
        ["DATA_RANGE"] = (MtcCategory.Condition, null),
    };

    public static MtcDataItem Parse(string text, TagDataType type)
    {
        var s = (text ?? string.Empty).Trim();
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Trim().Length == 0))
            throw new FormatException($"'{text}' is not an MTConnect data item address. {Help}");

        var path = parts[0].Trim().Split('/', StringSplitOptions.TrimEntries);
        var id = path[^1];
        if (!Id.IsMatch(id)) throw new FormatException($"'{id}' is not a valid data item id (letters, digits, _ - .; starts with a letter).");
        if (id == AvailabilityId) throw new FormatException("'avail' is the agent's built-in availability data item.");

        var components = new List<MtcComponent>();
        foreach (var seg in path[..^1])
        {
            var m = Segment.Match(seg);
            if (!m.Success) throw new FormatException($"'{seg}' is not a component; use an MTConnect type such as Axes, Controller or Linear[X].");
            components.Add(new MtcComponent(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Trim() : null));
        }

        var typeText = parts[1].Trim().ToUpperInvariant();
        string? subType = null;
        int dot = typeText.IndexOf('.');
        if (dot > 0)
        {
            subType = typeText[(dot + 1)..];
            typeText = typeText[..dot];
        }
        if (!TypeForm.IsMatch(typeText) || (subType != null && !TypeForm.IsMatch(subType)))
            throw new FormatException($"'{parts[1]}' is not an MTConnect type such as POSITION, EXECUTION or SYSTEM.");

        bool numeric = type is not (TagDataType.String or TagDataType.Bool);
        MtcCategory category;
        string? units = null;
        if (parts.Length == 3)
        {
            category = parts[2].Trim().ToUpperInvariant() switch
            {
                "SAMPLE" => MtcCategory.Sample,
                "EVENT" => MtcCategory.Event,
                "CONDITION" => MtcCategory.Condition,
                _ => throw new FormatException($"'{parts[2]}' is not a category; use SAMPLE, EVENT or CONDITION."),
            };
            if (Known.TryGetValue(typeText, out var k) && k.Category == category) units = k.Units;
        }
        else if (Known.TryGetValue(typeText, out var known))
        {
            (category, units) = known;
        }
        else
        {
            category = numeric ? MtcCategory.Sample : MtcCategory.Event;
        }

        if (category == MtcCategory.Sample && !numeric)
            throw new FormatException($"{typeText} is a sample and needs a numeric data type.");
        return new MtcDataItem(id, components, typeText, subType, category, units);
    }

    public static string Format(MtcDataItem i) =>
        string.Join("/", i.Path.Select(p => p.Name == null ? p.Type : $"{p.Type}[{p.Name}]").Append(i.Id)) + ":" + i.Type + (i.SubType != null ? "." + i.SubType : "");

    /// <summary>POSITION → Position, ROTARY_VELOCITY → RotaryVelocity.</summary>
    public static string ElementName(string type) =>
        string.Concat(type.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w[..1] + w[1..].ToLowerInvariant()));

    /// <summary>The text of an observation for a tag value.</summary>
    public static string ValueText(MtcDataItem item, object value)
    {
        if (item.Category == MtcCategory.Condition) return ConditionLevel(value);
        if (value is bool b)
        {
            return item.Type switch
            {
                "AVAILABILITY" => b ? "AVAILABLE" : "UNAVAILABLE",
                "EMERGENCY_STOP" => b ? "TRIGGERED" : "ARMED",
                "DOOR_STATE" => b ? "OPEN" : "CLOSED",
                "CHUCK_STATE" => b ? "OPEN" : "CLOSED",
                "SPINDLE_INTERLOCK" or "POWER_STATE" => b ? "ON" : "OFF",
                _ => b ? "ON" : "OFF",
            };
        }
        return value switch
        {
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
            string s => s.Length == 0 ? "UNAVAILABLE" : s,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "UNAVAILABLE",
        };
    }

    /// <summary>Condition level: NORMAL/WARNING/FAULT/UNAVAILABLE from text, Bool (true = FAULT) or 0/1/2.</summary>
    public static string ConditionLevel(object value) => value switch
    {
        bool b => b ? "FAULT" : "NORMAL",
        string s => s.Trim().ToUpperInvariant() switch
        {
            "WARNING" or "WARN" => "WARNING",
            "FAULT" or "ERROR" or "ALARM" => "FAULT",
            "UNAVAILABLE" or "" => "UNAVAILABLE",
            _ => "NORMAL",
        },
        _ => ValueConverter.ToDouble(value) switch { >= 2 => "FAULT", >= 1 => "WARNING", _ => "NORMAL" },
    };
}
