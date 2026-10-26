using System.Text.Json;
using System.Text.Json.Serialization;
using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Project;

/// <summary>On-disk project format (.psim, JSON).</summary>
public sealed class ProjectDocument
{
    public int Version { get; set; } = 1;
    public List<DeviceDocument> Devices { get; set; } = [];
    public List<ClientDocument> Clients { get; set; } = [];
}

public sealed class DeviceDocument
{
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool AutoStart { get; set; }
    public JsonElement? Settings { get; set; }
    public FaultSettings Faults { get; set; } = new();
    public List<TagDocument> Tags { get; set; } = [];

    /// <summary>Non-zero values per area, keyed by area short name.</summary>
    public Dictionary<string, Dictionary<int, ushort>> Data { get; set; } = [];
}

public sealed class TagDocument
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public TagDataType DataType { get; set; }
    public WordOrder WordOrder { get; set; }
    public string Description { get; set; } = string.Empty;
    public SimulationSettings Simulation { get; set; } = new();

    /// <summary>
    /// Last value of a non-simulated tag. Needed for protocols whose memory isn't
    /// saved as register tables (e.g. OPC UA); harmless duplication for Modbus.
    /// </summary>
    public string? Value { get; set; }

    public static TagDocument From(SimTag t)
    {
        string? value = null;
        if (t.Simulation.Kind == SimulationKind.None && t.Binding is { } b)
        {
            try { value = ValueConverter.Format(b.Read()); } catch (Exception) { }
        }
        return new()
        {
            Name = t.Name,
            Address = t.Address,
            DataType = t.DataType,
            WordOrder = t.WordOrder,
            Description = t.Description,
            Simulation = t.Simulation.Clone(),
            Value = value,
        };
    }

    public SimTag ToTag() => new()
    {
        Name = Name,
        Address = Address,
        DataType = DataType,
        WordOrder = WordOrder,
        Description = Description,
        Simulation = Simulation.Clone(),
    };
}

public sealed class ClientDocument
{
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public JsonElement? Settings { get; set; }
    public JsonElement? Request { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
}

public static class ProjectSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task SaveAsync(ProjectDocument doc, string path)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, doc, Options);
    }

    public static async Task<ProjectDocument> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, Options)
               ?? throw new InvalidDataException("Project file is empty.");
    }

    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, value.GetType(), Options);

    /// <summary>Deserializes into <paramref name="type"/>, or returns null when there is nothing to read.</summary>
    public static object? FromElement(JsonElement? element, Type type) =>
        element is { ValueKind: JsonValueKind.Object } e ? e.Deserialize(type, Options) : null;
}
