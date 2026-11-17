using System.Text;
using System.Text.RegularExpressions;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Script;

/// <summary>Tags of a scripted device: plain variable names used in rule templates.</summary>
public sealed class ScriptDataModel : IDeviceDataModel
{
    private static readonly Regex Name = new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant);

    public const string Help =
        "A variable name used in the rules, e.g. Temperature or Pump.Speed (letters, digits, _ and .). " +
        "Replies insert it with {Temperature} or {Temperature:F2}; rules write it with {Temperature=$1}.";

    public IReadOnlyList<IDataArea> Areas { get; } = [];

    internal NamedTagStore Store { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var name = (address ?? string.Empty).Trim();
        if (!Name.IsMatch(name)) throw new FormatException($"'{address}' is not a variable name. {Help}");
        if (name.Equals("now", StringComparison.OrdinalIgnoreCase) || name.Equals("code", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"'{name}' is a built-in placeholder; pick another name.");
        return Store.Bind(name, type);
    }
}

/// <summary>Splits a byte stream into requests according to the framing setting.</summary>
internal sealed class ScriptFramer(ScriptFraming framing, byte[] customTerminator)
{
    public const int MaxRequest = 64 * 1024;
    private readonly List<byte> _buffer = [];

    public int Count => _buffer.Count;
    public bool UsesSilence => framing == ScriptFraming.Silence || (framing == ScriptFraming.Custom && customTerminator.Length == 0);

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _buffer.Add(b);
    }

    /// <summary>The next complete request (terminator removed), or null.</summary>
    public byte[]? TakeComplete()
    {
        while (true)
        {
            int end, skip;
            switch (framing)
            {
                case ScriptFraming.AnyNewline:
                    end = _buffer.FindIndex(b => b is (byte)'\r' or (byte)'\n');
                    skip = 1;
                    break;
                case ScriptFraming.Cr:
                    end = _buffer.IndexOf((byte)'\r');
                    skip = 1;
                    break;
                case ScriptFraming.Lf:
                    end = _buffer.IndexOf((byte)'\n');
                    skip = 1;
                    break;
                case ScriptFraming.CrLf:
                    end = IndexOf([(byte)'\r', (byte)'\n']);
                    skip = 2;
                    break;
                case ScriptFraming.Custom when customTerminator.Length > 0:
                    end = IndexOf(customTerminator);
                    skip = customTerminator.Length;
                    break;
                default:
                    return null;
            }
            if (end < 0) return null;
            var frame = _buffer.GetRange(0, end).ToArray();
            _buffer.RemoveRange(0, end + skip);
            if (framing == ScriptFraming.AnyNewline && frame.Length == 0) continue; // the LF of a CRLF, or a blank line
            return frame;
        }
    }

    /// <summary>After a pause: everything buffered is one request.</summary>
    public byte[]? TakeOnSilence()
    {
        if (_buffer.Count == 0) return null;
        var frame = _buffer.ToArray();
        _buffer.Clear();
        if (framing == ScriptFraming.AnyNewline) frame = frame.Where(b => b is not ((byte)'\r' or (byte)'\n')).ToArray();
        return frame.Length == 0 ? null : frame;
    }

    public void Clear() => _buffer.Clear();

    private int IndexOf(byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= _buffer.Count; i++)
        {
            int j = 0;
            while (j < pattern.Length && _buffer[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}

/// <summary>Turns requests into reply bytes; shared by the TCP, UDP and serial transports.</summary>
internal sealed class ScriptRuntime
{
    public static readonly Encoding Text = Encoding.Latin1;

    private readonly ScriptSettings _settings;
    private readonly ScriptEngine _engine;
    private readonly ServerContext _ctx;
    private readonly byte[] _terminator;

    public ScriptRuntime(ScriptSettings settings, ScriptDataModel model, ServerContext context)
    {
        _settings = settings;
        _ctx = context;
        _engine = ScriptEngine.Create(settings, model.Store);
        _terminator = Text.GetBytes(ScriptEngine.Unescape(settings.ReplyTerminator ?? string.Empty));
    }

    public ScriptEngine Engine => _engine;

    public ScriptFramer NewFramer() =>
        new(_settings.Framing, Text.GetBytes(ScriptEngine.Unescape(_settings.CustomTerminator ?? string.Empty)));

    /// <summary>The bytes to send for a request (echo and/or reply), or null for none.</summary>
    public (byte[]? Bytes, bool IsError) Reply(byte[] request, string remote)
    {
        var text = Text.GetString(request);
        var result = _engine.Execute(text);
        foreach (var note in result.Notes) _ctx.Log.Warn(remote, $"\"{ScriptEngine.Visible(text)}\": {note}");

        var output = new List<byte>();
        if (_settings.Echo) output.AddRange(request.Concat(_terminator));
        if (result.Reply != null) output.AddRange(Text.GetBytes(result.Reply).Concat(_terminator));
        return (output.Count == 0 ? null : output.ToArray(), result.IsError);
    }

    public byte[] Fault(int code) => Text.GetBytes(_engine.RenderError(code)).Concat(_terminator).ToArray();

    /// <summary>A rendered template with the reply terminator, or null when the template is empty.</summary>
    public byte[]? Message(string? template, string remote)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var notes = new List<string>();
        var text = _engine.Render(template, notes);
        foreach (var note in notes) _ctx.Log.Warn(remote, note);
        return Text.GetBytes(text).Concat(_terminator).ToArray();
    }

    public string Describe(byte[] frame, bool isRequest)
    {
        var text = Text.GetString(frame);
        if (!isRequest) return $"\"{Preview(text)}\"";
        var trimmed = text.Trim();
        var rule = _engine.Rules.FirstOrDefault(r => r.Regex?.IsMatch(trimmed) ?? string.Equals(r.Literal, trimmed,
            _settings.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        return $"\"{Preview(text)}\"" + (rule != null ? $" · rule line {rule.Line}" : " · no rule");
    }

    private static string Preview(string text)
    {
        var v = ScriptEngine.Visible(text);
        return v.Length <= 200 ? v : v[..200] + "…";
    }

    /// <summary>Fault-injection delay (fixed + jitter).</summary>
    public int DelayMs()
    {
        var f = _ctx.Faults;
        return Math.Max(0, f.ResponseDelayMs) + (f.DelayJitterMs > 0 ? Random.Shared.Next(0, f.DelayJitterMs + 1) : 0);
    }

    /// <summary>For transports without TcpFrameServer: applies drop/delay/error faults, then replies.</summary>
    public async Task<(byte[]? Bytes, bool IsError)> ReplyWithFaultsAsync(byte[] request, string remote, CancellationToken ct)
    {
        var faults = _ctx.Faults;
        if (FaultSettings.Roll(faults.DropResponsePercent))
        {
            _ctx.Statistics.OnDropped();
            _ctx.Log.Warn(remote, "Fault injection: no reply");
            return (null, false);
        }
        int delay = DelayMs();
        if (delay > 0) await Task.Delay(delay, ct);
        if (FaultSettings.Roll(faults.ErrorResponsePercent))
        {
            _ctx.Log.Warn(remote, $"Fault injection: error reply (code {faults.ErrorCode})");
            return (Fault(faults.ErrorCode), true);
        }
        return Reply(request, remote);
    }
}
