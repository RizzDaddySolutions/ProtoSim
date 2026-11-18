using System.Collections.Concurrent;

namespace ProtoSim.Core.Data;

/// <summary>
/// Name-addressed tag values for symbolic protocols (EtherNet/IP, Sparkplug B, OPC DA...).
/// Several tags may bind the same name (same data type); the value lives as long as one binding does.
/// </summary>
public sealed class NamedTagStore
{
    private readonly ConcurrentDictionary<string, NamedTag> _tags;

    public NamedTagStore(StringComparer? comparer = null)
    {
        _tags = new ConcurrentDictionary<string, NamedTag>(comparer ?? StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised after every write, from whichever thread wrote. Keep handlers short.</summary>
    public event Action<NamedTag>? ValueChanged;

    /// <summary>Raised when a name appears or disappears.</summary>
    public event Action? TagsChanged;

    public ITagBinding Bind(string name, TagDataType type)
    {
        while (true)
        {
            bool added = false;
            var tag = _tags.GetOrAdd(name, n => { added = true; return new NamedTag(this, n, type); });
            lock (tag)
            {
                if (tag.Removed) continue; // lost a race with the last handle being disposed
                if (tag.DataType != type)
                    throw new FormatException($"'{tag.Name}' is already used by a {tag.DataType} tag; use one data type per name.");
                tag.RefCount++;
            }
            if (added) TagsChanged?.Invoke();
            return new Handle(tag);
        }
    }

    public NamedTag? Find(string name) => _tags.TryGetValue(name, out var t) ? t : null;

    public IReadOnlyList<NamedTag> All() => _tags.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public int Count => _tags.Count;

    internal void OnWritten(NamedTag tag) => ValueChanged?.Invoke(tag);

    private void Release(NamedTag tag)
    {
        lock (tag)
        {
            if (--tag.RefCount > 0) return;
            tag.Removed = true;
            _tags.TryRemove(new KeyValuePair<string, NamedTag>(tag.Name, tag));
        }
        TagsChanged?.Invoke();
    }

    private sealed class Handle(NamedTag tag) : ITagBinding
    {
        private int _disposed;
        public TagDataType DataType => tag.DataType;
        public object Read() => tag.Value;
        public void Write(object value) => tag.Write(value);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) tag.Store.Release(tag);
        }
    }
}

public sealed class NamedTag
{
    private object _value;
    private long _timestampTicks = DateTime.UtcNow.Ticks;
    private long _version;

    internal NamedTag(NamedTagStore store, string name, TagDataType type)
    {
        Store = store;
        Name = name;
        DataType = type;
        _value = type == TagDataType.String ? string.Empty : ValueConverter.FromDouble(0, type);
    }

    internal NamedTagStore Store { get; }
    internal int RefCount;
    internal bool Removed;

    public string Name { get; }
    public TagDataType DataType { get; }
    public object Value => Volatile.Read(ref _value);
    public DateTime TimestampUtc => new(Interlocked.Read(ref _timestampTicks), DateTimeKind.Utc);

    /// <summary>Incremented on every write; lets subscribers detect changes cheaply.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Stable small integer identifying the tag while it exists (used as alias / instance id).</summary>
    public int Id { get; } = Interlocked.Increment(ref s_nextId);
    private static int s_nextId;

    public void Write(object value)
    {
        Volatile.Write(ref _value, ValueConverter.Coerce(value, DataType));
        Interlocked.Exchange(ref _timestampTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _version);
        Store.OnWritten(this);
    }
}
