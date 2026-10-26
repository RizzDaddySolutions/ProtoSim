using System.Collections.Concurrent;

namespace ProtoSim.Core.Diagnostics;

public enum TrafficDirection
{
    Rx,
    Tx,
    Info,
    Warning,
    Error,
}

public sealed record TrafficEntry(DateTime Timestamp, TrafficDirection Direction, string Remote, string Summary, byte[]? Data = null);

/// <summary>Sink for protocol traffic and diagnostics. Implementations must be thread-safe.</summary>
public interface ITrafficLog
{
    bool Enabled { get; }
    void Add(TrafficEntry entry);
}

/// <summary>Lock-free bounded queue; the UI drains it periodically.</summary>
public sealed class TrafficLog : ITrafficLog
{
    private readonly ConcurrentQueue<TrafficEntry> _queue = new();
    private int _count;

    public bool Enabled { get; set; } = true;

    /// <summary>Oldest entries are discarded once this many are waiting to be drained.</summary>
    public int MaxPending { get; set; } = 20_000;

    public void Add(TrafficEntry entry)
    {
        if (!Enabled) return;
        _queue.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > MaxPending && _queue.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    public int Drain(ICollection<TrafficEntry> target, int max)
    {
        int n = 0;
        while (n < max && _queue.TryDequeue(out var e))
        {
            Interlocked.Decrement(ref _count);
            target.Add(e);
            n++;
        }
        return n;
    }
}

public static class TrafficLogExtensions
{
    public static void Info(this ITrafficLog log, string remote, string message) => Write(log, TrafficDirection.Info, remote, message);
    public static void Warn(this ITrafficLog log, string remote, string message) => Write(log, TrafficDirection.Warning, remote, message);
    public static void Error(this ITrafficLog log, string remote, string message) => Write(log, TrafficDirection.Error, remote, message);

    public static void Frame(this ITrafficLog log, TrafficDirection direction, string remote, ReadOnlySpan<byte> data, Func<string> describe)
    {
        if (!log.Enabled) return;
        log.Add(new TrafficEntry(DateTime.Now, direction, remote, describe(), data.ToArray()));
    }

    private static void Write(ITrafficLog log, TrafficDirection d, string remote, string message)
    {
        if (log.Enabled) log.Add(new TrafficEntry(DateTime.Now, d, remote, message));
    }
}

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        return string.Create(data.Length * 3 - 1, data.ToArray(), static (span, bytes) =>
        {
            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < bytes.Length; i++)
            {
                int o = i * 3;
                span[o] = digits[bytes[i] >> 4];
                span[o + 1] = digits[bytes[i] & 0xF];
                if (o + 2 < span.Length) span[o + 2] = ' ';
            }
        });
    }
}
