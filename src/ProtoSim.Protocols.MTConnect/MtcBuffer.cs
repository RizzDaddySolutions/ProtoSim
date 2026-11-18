namespace ProtoSim.Protocols.MTConnect;

internal sealed record Observation(long Sequence, string DataItemId, string Value, DateTime TimestampUtc);

/// <summary>The agent's circular buffer of observations, numbered by a sequence that only grows.</summary>
internal sealed class MtcBuffer
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Observation> _latest = new(StringComparer.Ordinal);
    private Observation?[] _ring = new Observation?[16];
    private long _next = 1;
    private int _count;

    public long InstanceId { get; private set; }
    public int Capacity => _ring.Length;

    public void Reset(int capacity)
    {
        lock (_lock)
        {
            _ring = new Observation?[Math.Max(16, capacity)];
            _latest.Clear();
            _next = 1;
            _count = 0;
            InstanceId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    /// <summary>Adds an observation. Unless <paramref name="recordDuplicates"/>, an unchanged value is skipped.</summary>
    public bool Add(string dataItemId, string value, DateTime timestampUtc, bool recordDuplicates)
    {
        lock (_lock)
        {
            if (!recordDuplicates && _latest.TryGetValue(dataItemId, out var last) && last.Value == value) return false;
            var obs = new Observation(_next, dataItemId, value, timestampUtc);
            _ring[_next % _ring.Length] = obs;
            _next++;
            if (_count < _ring.Length) _count++;
            _latest[dataItemId] = obs;
            return true;
        }
    }

    /// <summary>(first, last, next) sequence numbers. With an empty buffer first = next and last = next - 1.</summary>
    public (long First, long Last, long Next) Sequences()
    {
        lock (_lock) return (_next - _count, _next - 1, _next);
    }

    public IReadOnlyList<Observation> Latest(Func<string, bool> include)
    {
        lock (_lock) return _latest.Values.Where(o => include(o.DataItemId)).OrderBy(o => o.Sequence).ToList();
    }

    /// <summary>The newest observation of each data item at or before <paramref name="at"/> that is still in the buffer.</summary>
    public IReadOnlyList<Observation> At(long at, Func<string, bool> include)
    {
        lock (_lock)
        {
            var byItem = new Dictionary<string, Observation>(StringComparer.Ordinal);
            for (long seq = _next - _count; seq <= Math.Min(at, _next - 1); seq++)
            {
                var o = _ring[seq % _ring.Length]!;
                if (include(o.DataItemId)) byItem[o.DataItemId] = o;
            }
            return byItem.Values.OrderBy(o => o.Sequence).ToList();
        }
    }

    /// <summary>Up to <paramref name="count"/> matching observations starting at <paramref name="from"/>; <paramref name="nextSequence"/> is where the next sample should start.</summary>
    public IReadOnlyList<Observation> Range(long from, int count, Func<string, bool> include, out long nextSequence)
    {
        lock (_lock)
        {
            var result = new List<Observation>(Math.Min(count, 1024));
            long seq = Math.Max(from, _next - _count);
            for (; seq < _next && result.Count < count; seq++)
            {
                var o = _ring[seq % _ring.Length]!;
                if (include(o.DataItemId)) result.Add(o);
            }
            nextSequence = seq;
            return result;
        }
    }
}
