using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>
/// One simulated variable. Its address as typed ("BOB.MARY.SUE.PV" or "Plant/Tank1/Level") becomes the string NodeId;
/// every '.' or '/' starts a new folder level, so both appear as a tree under Objects.
/// </summary>
public sealed class OpcUaVariable
{
    private readonly object _gate = new();
    private object _value;

    internal OpcUaVariable(string nodeIdText, TagDataType type)
    {
        NodeIdText = nodeIdText;
        Path = OpcUaDataModel.CanonicalPath(nodeIdText);
        DataType = type;
        _value = ValueConverter.Coerce(null, type);
        var parts = Path.Split('/');
        Name = parts[^1];
        FolderPath = parts.Length > 1 ? string.Join('/', parts[..^1]) : string.Empty;
    }

    /// <summary>The string NodeId identifier, e.g. BOB.MARY.SUE.PV.</summary>
    public string NodeIdText { get; }

    /// <summary>Folder levels joined with '/', e.g. BOB/MARY/SUE/PV. Two addresses with the same levels are the same variable.</summary>
    public string Path { get; }
    public string Name { get; }
    public string FolderPath { get; }
    public TagDataType DataType { get; }
    internal int RefCount;
    private int _readOnly;

    /// <summary>False while any tag bound to this variable has its Writeable box cleared.</summary>
    public bool ClientWritable => Volatile.Read(ref _readOnly) <= 0;

    /// <summary>Returns true when <see cref="ClientWritable"/> flipped.</summary>
    internal bool ChangeReadOnly(int delta)
    {
        int now = Interlocked.Add(ref _readOnly, delta);
        return (now <= 0) != (now - delta <= 0);
    }

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

    /// <summary>Raised when <see cref="OpcUaVariable.ClientWritable"/> changes.</summary>
    public event Action<OpcUaVariable>? WritableChanged;

    internal void ChangeReadOnly(OpcUaVariable variable, int delta)
    {
        if (variable.ChangeReadOnly(delta)) WritableChanged?.Invoke(variable);
    }

    public IReadOnlyList<OpcUaVariable> Variables
    {
        get { lock (_gate) return _variables.Values.ToList(); }
    }

    public OpcUaVariable? Find(string address)
    {
        lock (_gate) return _variables.GetValueOrDefault(CanonicalPath(NormalizePath(address)));
    }

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var nodeIdText = NormalizePath(address);
        var path = CanonicalPath(nodeIdText);
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
                variable = new OpcUaVariable(nodeIdText, type);
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

    /// <summary>
    /// The NodeId identifier for an address: "BOB . MARY.PV" → "BOB.MARY.PV", "Plant\\Tank1/ Level" → "Plant/Tank1/Level".
    /// '.' and '/' separate folder levels and are kept as typed; spaces around levels are trimmed.
    /// </summary>
    public static string NormalizePath(string? address)
    {
        var raw = (address ?? string.Empty).Trim();
        if (raw.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
        {
            int s = raw.IndexOf(";s=", StringComparison.OrdinalIgnoreCase);
            if (s < 0) throw new FormatException("Only string node IDs (ns=…;s=Path) are supported for simulated variables.");
            raw = raw[(s + 3)..];
        }
        var sb = new System.Text.StringBuilder(raw.Length);
        int start = 0;
        for (int i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && raw[i] is not ('/' or '\\' or '.')) continue;
            var seg = raw[start..i].Trim();
            if (seg.Length == 0)
                throw new FormatException("Enter a path like BOB.MARY.SUE.PV or Plant/Tank1/Level (no empty levels).");
            if (seg.Any(c => char.IsControl(c) || c is ':' or ';' or '<' or '>' or '&'))
                throw new FormatException($"'{seg}' contains a character that is not allowed in a node name.");
            sb.Append(seg);
            if (i < raw.Length) sb.Append(raw[i] == '.' ? '.' : '/');
            start = i + 1;
        }
        return sb.ToString();
    }

    /// <summary>The folder levels of a normalized address joined with '/': "BOB.MARY.PV" → "BOB/MARY/PV".</summary>
    public static string CanonicalPath(string nodeIdText) => nodeIdText.Replace('.', '/');
}

internal sealed class OpcUaTagBinding : ITagBinding
{
    private readonly OpcUaDataModel model;
    private readonly OpcUaVariable variable;
    private readonly RemoteWriteFlag _writable;
    private int _disposed;

    public OpcUaTagBinding(OpcUaDataModel model, OpcUaVariable variable)
    {
        this.model = model;
        this.variable = variable;
        _writable = new RemoteWriteFlag(delta => model.ChangeReadOnly(variable, delta));
    }

    public TagDataType DataType => variable.DataType;
    public object Read() => variable.Value;
    public void Write(object value) => model.SetLocal(variable, ValueConverter.Coerce(value, variable.DataType));
    public bool RemoteWritable { get => _writable.Writable; set => _writable.Writable = value; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writable.Release();
        model.Release(variable);
    }
}
