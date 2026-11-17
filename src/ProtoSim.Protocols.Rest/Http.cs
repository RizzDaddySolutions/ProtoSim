using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProtoSim.Protocols.Rest;

/// <summary>A parsed HTTP/1.x request.</summary>
internal sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    /// <summary>Request target as sent, e.g. /api/v1/users?role=admin.</summary>
    public string Target { get; init; } = "/";
    /// <summary>Percent-decoded path without the query string.</summary>
    public string Path { get; init; } = "/";
    public string Version { get; init; } = "HTTP/1.1";
    public List<KeyValuePair<string, string>> Query { get; init; } = [];
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];
    public string RemoteEndPoint { get; init; } = string.Empty;
    /// <summary>Bytes received for the request (head plus body).</summary>
    public int Length { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public bool KeepAlive
    {
        get
        {
            var connection = Header("Connection") ?? string.Empty;
            if (Version == "HTTP/1.0") return connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
            return !connection.Contains("close", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string BodyText => Encoding.UTF8.GetString(Body);

    public static HttpRequest Create(string method, string target, string? body = null, Dictionary<string, string>? headers = null)
    {
        var (path, query) = SplitTarget(target);
        var bytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        return new HttpRequest
        {
            Method = method,
            Target = target,
            Path = path,
            Query = query,
            Headers = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase),
            Body = bytes,
            Length = bytes.Length,
            RemoteEndPoint = "test",
        };
    }

    internal static (string Path, List<KeyValuePair<string, string>> Query) SplitTarget(string target)
    {
        int q = target.IndexOf('?');
        var rawPath = q < 0 ? target : target[..q];
        var query = q < 0 ? [] : ParseForm(target[(q + 1)..]);
        // Absolute-form targets (http://host/path) are allowed by HTTP/1.1.
        if (Uri.TryCreate(rawPath, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https") rawPath = abs.AbsolutePath;
        return (Uri.UnescapeDataString(rawPath), query);
    }

    /// <summary>Parses a query string or application/x-www-form-urlencoded body.</summary>
    internal static List<KeyValuePair<string, string>> ParseForm(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var key = Decode(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? string.Empty : Decode(part[(eq + 1)..]);
            if (key.Length > 0) result.Add(new(key, value));
        }
        return result;

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }
}

internal sealed class HttpResponse
{
    public int Status { get; set; } = 200;
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];
    /// <summary>Short description for the traffic log.</summary>
    public string? Note { get; set; }

    public string BodyText => Encoding.UTF8.GetString(Body);

    public static HttpResponse Json(int status, JsonNode? body, bool indent)
    {
        var response = new HttpResponse { Status = status };
        response.Headers["Content-Type"] = "application/json; charset=utf-8";
        response.Body = Encoding.UTF8.GetBytes(body?.ToJsonString(indent ? IndentedOptions : CompactOptions) ?? "null");
        return response;
    }

    public static HttpResponse Error(int status, string error, string message, bool indent) =>
        Json(status, new JsonObject { ["error"] = error, ["message"] = message, ["status"] = status }, indent);

    public static HttpResponse Empty(int status) => new() { Status = status };

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactOptions = new();

    public byte[] Serialize(bool headOnly, bool keepAlive)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ReasonPhrase(Status)).Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Server: ProtoSim\r\n");
        foreach (var (name, value) in Headers) sb.Append(name).Append(": ").Append(value).Append("\r\n");
        bool hasBody = Status is not (204 or 304) && Status >= 200;
        if (hasBody) sb.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        if (headOnly || !hasBody || Body.Length == 0) return head;
        var all = new byte[head.Length + Body.Length];
        head.CopyTo(all, 0);
        Body.CopyTo(all, head.Length);
        return all;
    }

    public static string ReasonPhrase(int status) => status switch
    {
        100 => "Continue",
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        505 => "HTTP Version Not Supported",
        _ => "Status " + status.ToString(CultureInfo.InvariantCulture),
    };
}

internal sealed class HttpParseException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Reads HTTP/1.x requests from a connection, including keep-alive and chunked bodies.</summary>
internal sealed class HttpRequestReader(Stream stream, string remoteEndPoint)
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const int MaxBodyBytes = 32 * 1024 * 1024;

    private byte[] _buffer = new byte[16 * 1024];
    private int _start, _end;

    /// <summary>Next request, or null when the client closed the connection between requests.</summary>
    public async Task<HttpRequest?> ReadAsync(CancellationToken ct)
    {
        int headEnd;
        while ((headEnd = FindHeaderEnd()) < 0)
        {
            if (_end - _start >= MaxHeaderBytes) throw new HttpParseException(431, "Request headers are too large.");
            if (!await FillAsync(ct))
            {
                if (_end == _start) return null;
                throw new HttpParseException(400, "Connection closed in the middle of the request headers.");
            }
        }

        var head = Encoding.Latin1.GetString(_buffer, _start, headEnd - _start);
        int headLength = headEnd + 4 - _start;
        _start = headEnd + 4;

        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new HttpParseException(400, $"Malformed request line '{lines[0]}'.");
        if (parts[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            throw new HttpParseException(505, $"Unsupported protocol version '{parts[2]}'.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) throw new HttpParseException(400, $"Malformed header line '{lines[i]}'.");
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);

        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(ct);
        }
        else if (headers.TryGetValue("Content-Length", out var cl))
        {
            if (!int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                throw new HttpParseException(400, $"Invalid Content-Length '{cl}'.");
            if (length > MaxBodyBytes) throw new HttpParseException(413, "Request body is too large.");
            body = await ReadExactAsync(length, ct);
        }
        else body = [];

        var (path, query) = HttpRequest.SplitTarget(parts[1]);
        return new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Target = parts[1],
            Path = path,
            Query = query,
            Version = parts[2],
            Headers = headers,
            Body = body,
            RemoteEndPoint = remoteEndPoint,
            Length = headLength + body.Length,
        };
    }

    private int FindHeaderEnd()
    {
        var span = _buffer.AsSpan(_start, _end - _start);
        int i = span.IndexOf("\r\n\r\n"u8);
        return i < 0 ? -1 : _start + i;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0 && _start == _end) _start = _end = 0;
        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else Array.Resize(ref _buffer, _buffer.Length * 2);
        }
        int n = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
        _end += n;
        return n > 0;
    }

    private async Task<byte[]> ReadExactAsync(int length, CancellationToken ct)
    {
        var result = new byte[length];
        int copied = 0;
        while (copied < length)
        {
            if (_start == _end && !await FillAsync(ct))
                throw new HttpParseException(400, "Connection closed before the request body was complete.");
            int n = Math.Min(length - copied, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, copied, n);
            _start += n;
            copied += n;
        }
        return result;
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int i = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8);
            if (i >= 0)
            {
                var line = Encoding.ASCII.GetString(_buffer, _start, i);
                _start += i + 2;
                return line;
            }
            if (_end - _start > 8 * 1024) throw new HttpParseException(400, "Chunk header line is too long.");
            if (!await FillAsync(ct)) throw new HttpParseException(400, "Connection closed in the middle of a chunked body.");
        }
    }

    private async Task<byte[]> ReadChunkedAsync(CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(ct);
            var sizeText = line.Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new HttpParseException(400, $"Invalid chunk size '{sizeText}'.");
            if (size == 0)
            {
                // Skip trailers.
                while ((await ReadLineAsync(ct)).Length > 0) { }
                return body.ToArray();
            }
            if (body.Length + size > MaxBodyBytes) throw new HttpParseException(413, "Request body is too large.");
            body.Write(await ReadExactAsync(size, ct));
            if ((await ReadLineAsync(ct)).Length != 0) throw new HttpParseException(400, "Missing CRLF after chunk data.");
        }
    }
}
