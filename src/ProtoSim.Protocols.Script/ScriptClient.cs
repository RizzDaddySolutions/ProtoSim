using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Script;

public sealed class ScriptClientSettings
{
    [Display(Name = "Transport", GroupName = "Transport", Order = 1)]
    public ScriptTransport Transport { get; set; } = ScriptTransport.Tcp;

    [Display(Name = "Host", GroupName = "Network", Order = 2, Description = "TCP and UDP: the instrument or gateway.")]
    public string Host { get; set; } = "127.0.0.1";

    [Display(Name = "Port", GroupName = "Network", Order = 3)]
    [Range(1, 65535)]
    public int Port { get; set; } = 10001;

    [Display(Name = "Serial port", GroupName = "Serial", Order = 10)]
    public string PortName { get; set; } = SerialPorts.DefaultName;

    [Display(Name = "Baud rate", GroupName = "Serial", Order = 11)]
    [Range(300, 921_600)]
    public int BaudRate { get; set; } = 9600;

    [Display(Name = "Data bits", GroupName = "Serial", Order = 12)]
    [Range(5, 8)]
    public int DataBits { get; set; } = 8;

    [Display(Name = "Parity", GroupName = "Serial", Order = 13)]
    public Parity Parity { get; set; } = Parity.None;

    [Display(Name = "Stop bits", GroupName = "Serial", Order = 14)]
    public StopBits StopBits { get; set; } = StopBits.One;

    [Display(Name = "Command terminator", GroupName = "Framing", Order = 20, Description = @"Appended to every command, e.g. \r\n. Empty for none.")]
    public string CommandTerminator { get; set; } = @"\r\n";

    [Display(Name = "Reply ends at", GroupName = "Framing", Order = 21, Description = "What ends a reply.")]
    public ScriptFraming ReplyFraming { get; set; } = ScriptFraming.AnyNewline;

    [Display(Name = "Custom terminator", GroupName = "Framing", Order = 22, Description = @"For Custom framing, e.g. \x03.")]
    public string CustomTerminator { get; set; } = string.Empty;

    [Display(Name = "Frame gap (ms)", GroupName = "Framing", Order = 23, Description = "For Silence framing: a pause this long ends the reply.")]
    [Range(1, 10_000)]
    public int FrameGapMs { get; set; } = 50;

    [Display(Name = "Reply timeout (ms)", GroupName = "Framing", Order = 24)]
    [Range(10, 60_000)]
    public int TimeoutMs { get; set; } = 1000;
}

public enum ScriptClientOperation
{
    /// <summary>Send each command and wait for its reply.</summary>
    Send,
    /// <summary>Show messages the device sent on its own (periodic output, streaming scales...).</summary>
    Listen,
}

public sealed class ScriptClientRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1)]
    public ScriptClientOperation Operation { get; set; } = ScriptClientOperation.Send;

    [Display(Name = "Commands", GroupName = "Request", Order = 2, Description = @"One command per line, e.g. *IDN? and MEAS:TEMP?. Escapes \r \n \t \xHH work.")]
    [DataType(DataType.MultilineText)]
    public string Commands { get; set; } = "*IDN?\nMEAS:TEMP?";

    [Display(Name = "Value pattern", GroupName = "Request", Order = 3,
        Description = @"Optional regular expression; its first group (or the whole match) becomes the row's value, e.g. ([-0-9.]+) to pick a number out of '+21.53 C'.")]
    public string ValuePattern { get; set; } = string.Empty;
}

/// <summary>Line-oriented client for ASCII instruments and gateways over TCP, UDP or a serial port.</summary>
public sealed class ScriptClient(ScriptClientSettings settings, ClientContext context) : IProtocolClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<byte> _rx = new();
    private readonly ConcurrentQueue<(DateTime At, string Text)> _unsolicited = new();
    private readonly SemaphoreSlim _rxSignal = new(0);
    private readonly List<(DateTime At, string Text)> _history = [];
    private TcpClient? _tcp;
    private UdpClient? _udp;
    private SerialPort? _serial;
    private CancellationTokenSource? _readCts;
    private bool _awaitingReply;

    private ITrafficLog Log => context.Log;
    private string Remote => settings.Transport == ScriptTransport.Serial ? settings.PortName : $"{settings.Host}:{settings.Port}";
    private static Encoding Latin1 => Encoding.Latin1;

    public bool IsConnected => settings.Transport switch
    {
        ScriptTransport.Tcp => _tcp?.Connected == true,
        ScriptTransport.Udp => _udp != null,
        _ => _serial?.IsOpen == true,
    };

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await OpenAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        Close();
        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;
        switch (settings.Transport)
        {
            case ScriptTransport.Tcp:
            {
                var tcp = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Math.Max(1000, settings.TimeoutMs * 3));
                try { await tcp.ConnectAsync(settings.Host, settings.Port, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    tcp.Dispose();
                    throw new TimeoutException($"Connecting to {Remote} timed out.");
                }
                _tcp = tcp;
                var stream = tcp.GetStream();
                _ = Task.Run(async () =>
                {
                    var buf = new byte[4096];
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            int n = await stream.ReadAsync(buf, token);
                            if (n == 0) { Log.Warn(Remote, "Connection closed by device"); break; }
                            Received(buf.AsSpan(0, n));
                        }
                    }
                    catch (Exception) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { Log.Warn(Remote, $"Receive stopped: {ex.Message}"); }
                }, token);
                break;
            }
            case ScriptTransport.Udp:
            {
                var udp = new UdpClient();
                udp.Connect(settings.Host, settings.Port);
                _udp = udp;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var res = await udp.ReceiveAsync(token);
                            Received(res.Buffer);
                        }
                    }
                    catch (Exception) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { Log.Warn(Remote, $"Receive stopped: {ex.Message}"); }
                }, token);
                break;
            }
            default:
            {
                var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits);
                port.DataReceived += (_, _) =>
                {
                    try
                    {
                        var p = _serial;
                        if (p == null || !p.IsOpen) return;
                        var buf = new byte[p.BytesToRead];
                        int n = p.Read(buf, 0, buf.Length);
                        Received(buf.AsSpan(0, n));
                    }
                    catch (Exception) { /* port closing */ }
                };
                port.Open();
                _serial = port;
                break;
            }
        }
        Log.Info(Remote, $"Connected ({settings.Transport})");
    }

    private void Received(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _rx.Enqueue(b);
        _rxSignal.Release();
        if (!_awaitingReply) CollectUnsolicited(force: settings.ReplyFraming == ScriptFraming.Silence);
    }

    /// <summary>Moves complete messages that arrived without a command into the unsolicited list.</summary>
    private void CollectUnsolicited(bool force)
    {
        while (TakeFrame(force) is { } frame)
        {
            Log.Frame(TrafficDirection.Rx, Remote, Latin1.GetBytes(frame), () => ScriptEngine.Visible(frame));
            _unsolicited.Enqueue((DateTime.Now, frame));
            while (_unsolicited.Count > 200) _unsolicited.TryDequeue(out _);
        }
    }

    /// <summary>A complete reply from the receive buffer per the framing setting, or null.</summary>
    private string? TakeFrame(bool force)
    {
        lock (_rxLock) return TakeFrameLocked(force);
    }

    private readonly object _rxLock = new();

    private string? TakeFrameLocked(bool force)
    {
        var text = Latin1.GetString(_rx.ToArray());
        if (text.Length == 0) return null;
        int end = -1, skip = 0;
        switch (settings.ReplyFraming)
        {
            case ScriptFraming.Cr: end = text.IndexOf('\r'); skip = 1; break;
            case ScriptFraming.Lf: end = text.IndexOf('\n'); skip = 1; break;
            case ScriptFraming.CrLf: end = text.IndexOf("\r\n", StringComparison.Ordinal); skip = 2; break;
            case ScriptFraming.Custom:
            {
                var t = ScriptEngine.Unescape(settings.CustomTerminator ?? string.Empty);
                if (t.Length > 0) { end = text.IndexOf(t, StringComparison.Ordinal); skip = t.Length; }
                break;
            }
            case ScriptFraming.AnyNewline:
                end = text.IndexOfAny(['\r', '\n']);
                skip = 1;
                if (end >= 0 && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') skip = 2;
                break;
        }
        if (end < 0)
        {
            if (!force) return null;
            end = text.Length;
            skip = 0;
        }
        for (int i = 0; i < end + skip; i++) _rx.TryDequeue(out _);
        var frame = text[..end];
        if (frame.Length == 0 && settings.ReplyFraming == ScriptFraming.AnyNewline) return TakeFrameLocked(force); // blank line between CR and LF
        return frame;
    }

    private async Task<string?> WaitReplyAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + settings.TimeoutMs;
        bool silence = settings.ReplyFraming == ScriptFraming.Silence;
        long lastRx = Environment.TickCount64;
        int seen = _rx.Count;
        while (true)
        {
            if (!silence && TakeFrame(false) is { } frame) return frame;
            if (silence && _rx.Count > 0 && Environment.TickCount64 - lastRx >= settings.FrameGapMs) return TakeFrame(true);
            long now = Environment.TickCount64;
            if (now >= deadline) return _rx.Count > 0 ? TakeFrame(true) : null;
            int wait = (int)Math.Min(deadline - now, silence ? settings.FrameGapMs : 50);
            await _rxSignal.WaitAsync(Math.Max(1, wait), ct);
            if (_rx.Count != seen) { seen = _rx.Count; lastRx = Environment.TickCount64; }
        }
    }

    private async Task SendAsync(string command, CancellationToken ct)
    {
        var text = ScriptEngine.Unescape(command) + ScriptEngine.Unescape(settings.CommandTerminator ?? string.Empty);
        var bytes = Latin1.GetBytes(text);
        switch (settings.Transport)
        {
            case ScriptTransport.Tcp: await _tcp!.GetStream().WriteAsync(bytes, ct); break;
            case ScriptTransport.Udp: await _udp!.SendAsync(bytes, ct); break;
            default: _serial!.Write(bytes, 0, bytes.Length); break;
        }
        Log.Frame(TrafficDirection.Tx, Remote, bytes, () => ScriptEngine.Visible(text));
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (ScriptClientRequest)request;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenAsync(cancellationToken);
            Regex? pattern = string.IsNullOrWhiteSpace(r.ValuePattern) ? null : new Regex(r.ValuePattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

            if (r.Operation == ScriptClientOperation.Listen)
            {
                CollectUnsolicited(force: settings.ReplyFraming == ScriptFraming.Silence);
                while (_unsolicited.TryDequeue(out var m)) _history.Add(m);
                if (_history.Count > 50) _history.RemoveRange(0, _history.Count - 50);
                var rows = _history.AsEnumerable().Reverse().Select((m, i) => Row(i == 0 ? "latest" : $"latest-{i}", $"{m.At:HH:mm:ss.fff}", m.Text, pattern)).ToList();
                return ClientResult.Ok(rows.Count == 0 ? "No messages yet" : $"{rows.Count} messages", rows, sw.Elapsed);
            }

            var commands = (r.Commands ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (commands.Length == 0) throw new FormatException("Enter at least one command.");
            var results = new List<ClientResultRow>();
            int timeouts = 0;
            _awaitingReply = true;
            try
            {
                foreach (var cmd in commands)
                {
                    CollectUnsolicited(force: false); // anything left over isn't this command's reply
                    var started = Stopwatch.StartNew();
                    await SendAsync(cmd, cancellationToken);
                    var reply = await WaitReplyAsync(cancellationToken);
                    if (reply == null)
                    {
                        timeouts++;
                        Log.Warn(Remote, $"No reply to {cmd} within {settings.TimeoutMs} ms");
                        results.Add(new ClientResultRow(cmd, "no reply", ""));
                        continue;
                    }
                    Log.Frame(TrafficDirection.Rx, Remote, Latin1.GetBytes(reply), () => ScriptEngine.Visible(reply));
                    results.Add(Row(cmd, $"{reply.Length} chars · {started.Elapsed.TotalMilliseconds:F0} ms", reply, pattern));
                }
            }
            finally
            {
                _awaitingReply = false;
            }
            return timeouts == commands.Length
                ? ClientResult.Fail($"No reply within {settings.TimeoutMs} ms", sw.Elapsed)
                : ClientResult.Ok($"{commands.Length - timeouts}/{commands.Length} replies", results, sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is IOException or SocketException or InvalidOperationException) Close();
            Log.Error(Remote, ex.Message);
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ClientResultRow Row(string address, string raw, string reply, Regex? pattern)
    {
        string value = reply;
        if (pattern != null)
        {
            var m = pattern.Match(reply);
            value = !m.Success ? "" : m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
        }
        return new ClientResultRow(address, raw + (pattern != null ? $" · {ScriptEngine.Visible(reply)}" : ""), ScriptEngine.Visible(value)) { Typed = ClientValue.Parse(value) };
    }

    private void Close()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;
        _tcp?.Dispose();
        _udp?.Dispose();
        try { _serial?.Close(); } catch (IOException) { }
        _serial?.Dispose();
        _tcp = null;
        _udp = null;
        _serial = null;
        _rx.Clear();
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsConnected) Log.Info(Remote, "Disconnected");
            Close();
            _unsolicited.Clear();
            _history.Clear();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
        _rxSignal.Dispose();
    }
}
