using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Rest;

public enum RestClientAuth
{
    None,
    Basic,
    /// <summary>A fixed bearer token.</summary>
    BearerToken,
    /// <summary>OAuth 2.0 client credentials: a token is fetched from the token URL and renewed before it expires.</summary>
    ClientCredentials,
}

public sealed class RestClientSettings
{
    [Display(Name = "Base URL", GroupName = "Connection", Order = 1, Description = "Request paths are relative to this, e.g. http://192.168.1.20:8080/api.")]
    public string BaseUrl { get; set; } = "http://localhost:8080/api";

    [Display(Name = "Timeout (ms)", GroupName = "Connection", Order = 2)]
    [Range(100, 600_000)]
    public int TimeoutMs { get; set; } = 5000;

    [Display(Name = "Accept any TLS certificate", GroupName = "Connection", Order = 3, Description = "For test servers with self-signed certificates.")]
    public bool AcceptAnyCertificate { get; set; }

    [Display(Name = "Extra headers", GroupName = "Connection", Order = 4, Description = "One per line or separated by ';', e.g. X-Api-Key: abc123.")]
    public string Headers { get; set; } = string.Empty;

    [Display(Name = "Authentication", GroupName = "Authentication", Order = 10)]
    public RestClientAuth Authentication { get; set; } = RestClientAuth.None;

    [Display(Name = "User name / client ID", GroupName = "Authentication", Order = 11, Description = "Basic: user name. Client credentials: client ID.")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "Password / client secret", GroupName = "Authentication", Order = 12)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Bearer token", GroupName = "Authentication", Order = 13)]
    public string Token { get; set; } = string.Empty;

    [Display(Name = "Token URL", GroupName = "Authentication", Order = 14, Description = "Client credentials: the OAuth 2.0 token endpoint.")]
    public string TokenUrl { get; set; } = "http://localhost:8080/oauth/token";

    [Display(Name = "Scope", GroupName = "Authentication", Order = 15)]
    public string Scope { get; set; } = string.Empty;
}

public enum RestClientMethod { GET, POST, PUT, PATCH, DELETE, HEAD }

public sealed class RestClientRequest
{
    [Display(Name = "Method", GroupName = "Request", Order = 1)]
    public RestClientMethod Method { get; set; } = RestClientMethod.GET;

    [Display(Name = "Path", GroupName = "Request", Order = 2, Description = "Relative to the base URL, e.g. sensors/temperature or _tags. A full URL also works.")]
    public string Path { get; set; } = "_tags";

    [Display(Name = "Body", GroupName = "Request", Order = 3, Description = "POST/PUT/PATCH body.")]
    public string Body { get; set; } = string.Empty;

    [Display(Name = "Content type", GroupName = "Request", Order = 4)]
    public string ContentType { get; set; } = "application/json";

    [Display(Name = "Values to extract", GroupName = "Response", Order = 10,
        Description = "JSON paths separated by commas, e.g. temperature, data.items[0].value. Empty = every value in the response.")]
    public string Extract { get; set; } = string.Empty;
}

/// <summary>HTTP client for REST APIs: any method, JSON responses flattened into one row per value.</summary>
public sealed class RestApiClient : IProtocolClient
{
    private readonly RestClientSettings _settings;
    private readonly ClientContext _context;
    private readonly HttpClient _http;
    private string? _accessToken;
    private DateTime _tokenExpires;
    private bool _connected;

    public RestApiClient(RestClientSettings settings, ClientContext context)
    {
        _settings = settings;
        _context = context;
        var handler = new HttpClientHandler();
        if (settings.AcceptAnyCertificate) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs) };
    }

    private ITrafficLog Log => _context.Log;
    private string Remote => _settings.BaseUrl;

    /// <summary>HTTP has no connection to hold; "connected" means the last request reached the server.</summary>
    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_settings.Authentication == RestClientAuth.ClientCredentials) await EnsureTokenAsync(cancellationToken);
        _connected = true;
        Log.Info(Remote, "Ready");
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        _accessToken = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private Uri Resolve(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return abs;
        var b = _settings.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(b), (path ?? string.Empty).TrimStart('/'));
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpires) return;
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (!string.IsNullOrWhiteSpace(_settings.Scope)) form["scope"] = _settings.Scope;
        using var req = new HttpRequestMessage(HttpMethod.Post, _settings.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(_settings.UserName)}:{Uri.EscapeDataString(_settings.Password)}")));
        Log.Info(_settings.TokenUrl, "Requesting access token (client credentials)");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Token request failed: {(int)resp.StatusCode} {text}");
        var json = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Token response is not a JSON object.");
        _accessToken = json["access_token"]?.GetValue<string>() ?? throw new InvalidDataException("Token response has no access_token.");
        int seconds = json["expires_in"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : 3600;
        _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(5, seconds - 10));
        Log.Info(_settings.TokenUrl, $"Token received, valid {seconds} s");
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (RestClientRequest)request;
        var sw = Stopwatch.StartNew();
        try
        {
            var uri = Resolve(r.Path);
            using var req = new HttpRequestMessage(new HttpMethod(r.Method.ToString()), uri);
            if (r.Method is RestClientMethod.POST or RestClientMethod.PUT or RestClientMethod.PATCH)
                req.Content = new StringContent(r.Body ?? string.Empty, Encoding.UTF8, string.IsNullOrWhiteSpace(r.ContentType) ? "application/json" : r.ContentType);
            foreach (var line in (_settings.Headers ?? string.Empty).Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) req.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
            switch (_settings.Authentication)
            {
                case RestClientAuth.Basic:
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.UserName}:{_settings.Password}")));
                    break;
                case RestClientAuth.BearerToken:
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
                    break;
                case RestClientAuth.ClientCredentials:
                    await EnsureTokenAsync(cancellationToken);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                    break;
            }

            var sent = r.Body ?? string.Empty;
            Log.Frame(TrafficDirection.Tx, Remote, req.Content == null ? [] : Encoding.UTF8.GetBytes(sent), () => $"{r.Method} {uri.PathAndQuery}");
            using var resp = await _http.SendAsync(req, cancellationToken);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            var text = Encoding.UTF8.GetString(bytes);
            _connected = true;
            Log.Frame(TrafficDirection.Rx, Remote, bytes, () => $"{(int)resp.StatusCode} {resp.ReasonPhrase} · {resp.Content.Headers.ContentType?.MediaType ?? "no body"} · {bytes.Length} bytes");

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && _settings.Authentication == RestClientAuth.ClientCredentials)
                _accessToken = null; // fetch a new token next time

            var rows = Rows(text, r.Extract);
            string message = $"{(int)resp.StatusCode} {resp.ReasonPhrase} · {rows.Count} values";
            return resp.IsSuccessStatusCode ? ClientResult.Ok(message, rows, sw.Elapsed) : ClientResult.Fail(message + $": {Shorten(text)}", sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _connected = false;
            Log.Error(Remote, $"No response within {_settings.TimeoutMs} ms");
            return ClientResult.Fail($"No response within {_settings.TimeoutMs} ms.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException) _connected = false;
            Log.Error(Remote, $"{r.Method} failed: {ex.Message}");
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
    }

    private static string Shorten(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>One row per requested JSON path, or per leaf value when none are requested.</summary>
    internal static List<ClientResultRow> Rows(string body, string extract)
    {
        JsonNode? root;
        try { root = body.Length == 0 ? null : JsonNode.Parse(body); }
        catch (JsonException) { return [new ClientResultRow("body", $"{body.Length} chars", Shorten(body)) { Typed = ClientValue.Parse(body) }]; }

        var paths = ClientValue.SplitList(extract);
        if (paths.Length > 0)
            return paths.Select(p => Select(root, p) is { } node ? Row(p, node) : new ClientResultRow(p, "missing", "")).ToList();

        var rows = new List<ClientResultRow>();
        Flatten(root, "", rows);
        if (rows.Count == 0) rows.Add(new ClientResultRow("body", "empty", ""));
        return rows;
    }

    private static void Flatten(JsonNode? node, string path, List<ClientResultRow> rows)
    {
        if (rows.Count >= 2000) return;
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o) Flatten(v, path.Length == 0 ? k : $"{path}.{k}", rows);
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) Flatten(a[i], $"{path}[{i}]", rows);
                break;
            default:
                rows.Add(node == null ? new ClientResultRow(path.Length == 0 ? "$" : path, "null", "(null)") : Row(path.Length == 0 ? "$" : path, node));
                break;
        }
    }

    private static ClientResultRow Row(string path, JsonNode node)
    {
        if (node is JsonValue v)
        {
            var kind = v.GetValueKind();
            object? typed = kind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                // Cast each branch: a long/double conditional would widen integers to double.
                JsonValueKind.Number => v.TryGetValue<JsonElement>(out var e)
                    ? (e.TryGetInt64(out var l) ? (object)l : e.GetDouble())
                    : v.TryGetValue<long>(out var l2) ? (object)l2 : v.GetValue<double>(),
                JsonValueKind.String => v.GetValue<string>(),
                _ => null,
            };
            string text = typed switch
            {
                null => "(null)",
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => typed.ToString() ?? "",
            };
            return new ClientResultRow(path, kind.ToString(), text) { Typed = typed };
        }
        var json = node.ToJsonString();
        return new ClientResultRow(path, node is JsonArray arr ? $"array[{arr.Count}]" : "object", Shorten(json)) { Typed = json };
    }

    /// <summary>Follows a path like data.items[0].value (a leading $ or $. is allowed).</summary>
    internal static JsonNode? Select(JsonNode? root, string path)
    {
        var p = path.Trim();
        if (p.StartsWith('$')) p = p[1..];
        var node = root;
        int i = 0;
        while (i < p.Length && node != null)
        {
            if (p[i] == '.') { i++; continue; }
            if (p[i] == '[')
            {
                int end = p.IndexOf(']', i);
                if (end < 0) return null;
                var inner = p[(i + 1)..end].Trim('\'', '"');
                node = int.TryParse(inner, out var idx) ? (node is JsonArray a && idx >= 0 && idx < a.Count ? a[idx] : null) : (node as JsonObject)?[inner];
                i = end + 1;
                continue;
            }
            int stop = p.IndexOfAny(['.', '['], i);
            var name = stop < 0 ? p[i..] : p[i..stop];
            node = (node as JsonObject)?[name];
            i = stop < 0 ? p.Length : stop;
        }
        return node;
    }
}
