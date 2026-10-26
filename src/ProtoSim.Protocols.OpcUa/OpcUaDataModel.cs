using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>One simulated variable. Its path ("Plant/Tank1/Level") becomes the string NodeId.</summary>
public sealed class OpcUaVariable
{
    private readonly object _gate = new();
    private object _value;

    internal OpcUaVariable(string path, TagDataType type)
    {
        Path = path;
        DataType = type;
        _value = ValueConverter.Coerce(null, type);
        var parts = path.Split('/');
        Name = parts[^1];
        FolderPath = parts.Length > 1 ? string.Join('/', parts[..^1]) : string.Empty;
    }

    public string Path { get; }
    public string Name { get; }
    public string FolderPath { get; }
    public TagDataType DataType { get; }
    internal int RefCount;

    public object Value
    {
        get { lock (_gate) return _value; }
    }

    /// <summary>Sets the value; returns false if it was unchanged.</summary>
    internal bool SetValue(object value)
    {
        lock (_gate)
        {
            if (Equals(_value, value)) return false;
            _value = value;
            return true;
        }
    }
}

/// <summary>
/// Device memory for an OPC UA server: a set of variables keyed by path.
/// Exists independently of the running server so values survive restarts.
/// </summary>
public sealed class OpcUaDataModel : IDeviceDataModel
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OpcUaVariable> _variables = new(StringComparer.Ordinal);

    public IReadOnlyList<IDataArea> Areas => [];

    /// <summary>Raised (outside locks) when the first tag binds a new path.</summary>
    public event Action<OpcUaVariable>? VariableAdded;

    /// <summary>Raised when the last tag bound to a path is removed.</summary>
    public event Action<OpcUaVariable>? VariableRemoved;

    /// <summary>Raised when a value changes locally (simulation or UI). Remote writes do not raise it.</summary>
    public event Action<OpcUaVariable>? ValueChanged;

    public IReadOnlyList<OpcUaVariable> Variables
    {
        get { lock (_gate) return _variables.Values.ToList(); }
    }

    public OpcUaVariable? Find(string path)
    {
        lock (_gate) return _variables.GetValueOrDefault(path);
    }

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var path = NormalizePath(address);
        OpcUaVariable variable;
        bool added = false;
        lock (_gate)
        {
            if (_variables.TryGetValue(path, out var existing))
            {
                if (existing.DataType != type)
                    throw new FormatException($"'{path}' is already used by another tag as {existing.DataType}.");
                variable = existing;
            }
            else
            {
                if (_variables.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal)))
                    throw new FormatException($"'{path}' is already a folder of other variables.");
                var parentClash = _variables.Keys.FirstOrDefault(k => path.StartsWith(k + "/", StringComparison.Ordinal));
                if (parentClash != null)
                    throw new FormatException($"'{parentClash}' is a variable, so it cannot also be a folder.");
                variable = new OpcUaVariable(path, type);
                _variables[path] = variable;
                added = true;
            }
            variable.RefCount++;
        }
        if (added) VariableAdded?.Invoke(variable);
        return new OpcUaTagBinding(this, variable);
    }

    internal void Release(OpcUaVariable variable)
    {
        bool removed = false;
        lock (_gate)
        {
            if (--variable.RefCount <= 0 && _variables.Remove(variable.Path)) removed = true;
        }
        if (removed) VariableRemoved?.Invoke(variable);
    }

    internal void SetLocal(OpcUaVariable variable, object value)
    {
        if (variable.SetValue(value)) ValueChanged?.Invoke(variable);
    }

    /// <summary>Called by the server when a client writes; does not echo back to the server.</summary>
    internal static void SetRemote(OpcUaVariable variable, object value) => variable.SetValue(value);

    /// <summary>"Plant.Tank1/ Level" → "Plant.Tank1/Level". Segments are separated by '/'.</summary>
    public static string NormalizePath(string? address)
    {
        var raw = (address ?? string.Empty).Trim();
        if (raw.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
        {
            int s = raw.IndexOf(";s=", StringComparison.OrdinalIgnoreCase);
            if (s < 0) throw new FormatException("Only string node IDs (ns=…;s=Path) are supported for simulated variables.");
            raw = raw[(s + 3)..];
        }
        var segments = raw.Split(['/', '\\'], StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(s => s.Length == 0))
            throw new FormatException("Enter a path like Plant/Tank1/Level (no empty segments).");
        foreach (var seg in segments)
        {
            if (seg.Any(c => char.IsControl(c) || c is ':' or ';' or '<' or '>' or '&'))
                throw new FormatException($"'{seg}' contains a character that is not allowed in a node name.");
        }
        return string.Join('/', segments);
    }
}

internal sealed class OpcUaTagBinding(OpcUaDataModel model, OpcUaVariable variable) : ITagBinding
{
    private int _disposed;

    public TagDataType DataType => variable.DataType;
    public object Read() => variable.Value;
    public void Write(object value) => model.SetLocal(variable, ValueConverter.Coerce(value, variable.DataType));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) model.Release(variable);
    }
}
