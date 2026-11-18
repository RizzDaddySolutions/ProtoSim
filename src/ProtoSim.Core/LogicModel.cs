namespace ProtoSim.Core.Logic;

/// <summary>A function block program: blocks wired together, executed every scan. Saved with the project.</summary>
public sealed class LogicProgram
{
    /// <summary>Stable identity; the engine keeps block state across edits of the same program id.</summary>
    public string Id { get; set; } = LogicBlock.NewId();
    public string Name { get; set; } = "Program";
    public bool Enabled { get; set; } = true;

    /// <summary>Scan interval in milliseconds.</summary>
    public int ScanMs { get; set; } = 100;

    public string Description { get; set; } = string.Empty;
    public List<LogicBlock> Blocks { get; set; } = [];
    public List<LogicWire> Wires { get; set; } = [];

    /// <summary>A deep copy, so the scan thread never sees edits half-made.</summary>
    public LogicProgram Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        ScanMs = ScanMs,
        Description = Description,
        Blocks = Blocks.Select(b => new LogicBlock
        {
            Id = b.Id, Type = b.Type, X = b.X, Y = b.Y, Label = b.Label,
            Params = new Dictionary<string, string>(b.Params, StringComparer.Ordinal),
        }).ToList(),
        Wires = Wires.Select(w => new LogicWire { FromBlock = w.FromBlock, FromPort = w.FromPort, ToBlock = w.ToBlock, ToPort = w.ToPort }).ToList(),
    };
}

/// <summary>One block instance on the canvas.</summary>
public sealed class LogicBlock
{
    public string Id { get; set; } = NewId();
    public string Type { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Optional label shown in the block header instead of the type name.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Parameter values as invariant-culture text, keyed by parameter name.</summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.Ordinal);

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public string Param(string name, string fallback = "") => Params.TryGetValue(name, out var v) ? v : fallback;
}

/// <summary>A connection from one block's output to another block's input.</summary>
public sealed class LogicWire
{
    public string FromBlock { get; set; } = string.Empty;
    public string FromPort { get; set; } = string.Empty;
    public string ToBlock { get; set; } = string.Empty;
    public string ToPort { get; set; } = string.Empty;
}

/// <summary>Finds tags anywhere in the project by "Device/Tag" reference. Must be safe to call from the scan thread.</summary>
public interface ITagDirectory
{
    Simulation.SimTag? Find(string reference);
}
