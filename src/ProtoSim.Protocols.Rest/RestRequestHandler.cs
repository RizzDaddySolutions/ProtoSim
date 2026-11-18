using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.Rest;

/// <summary>
/// Routes requests to the token endpoint, tag endpoints and the JSON database.
/// Pure request → response logic with no networking, so it can be tested directly.
/// </summary>
internal sealed class RestRequestHandler(RestDataModel model)
{
    private const string AllowedMethods = "GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS";
    private RestServerSettings Settings => model.Settings;
    private bool Indent => Settings.IndentJson;

    public HttpResponse Handle(HttpRequest request)
    {
        HttpResponse response;
        try { response = Route(request); }
        catch (Exception ex)
        {
            response = HttpResponse.Error(500, "server_error", ex.Message, Indent);
        }
        if (Settings.EnableCors)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = AllowedMethods;
            response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, Accept";
            response.Headers["Access-Control-Expose-Headers"] = "X-Total-Count, Location";
        }
        return response;
    }

    private HttpResponse Route(HttpRequest request)
    {
        var method = request.Method;
        var path = "/" + request.Path.Trim('/');
        if (path == "/") path = "/";

        if (method == "OPTIONS")
        {
            var preflight = HttpResponse.Empty(204);
            preflight.Headers["Allow"] = AllowedMethods;
            return preflight;
        }

        var tokenPath = RestServerSettings.NormalizePath(Settings.TokenPath);
        if (tokenPath.Length > 0 && string.Equals(path, tokenPath, StringComparison.OrdinalIgnoreCase))
            return method == "POST" ? Token(request) : MethodNotAllowed("POST, OPTIONS");

        var basePath = RestServerSettings.NormalizePath(Settings.BasePath);
        string relative;
        if (basePath.Length == 0) relative = path.Trim('/');
        else if (string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase)) relative = string.Empty;
        else if (path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)) relative = path[(basePath.Length + 1)..].Trim('/');
        else return HttpResponse.Error(404, "not_found", $"'{path}' is outside the API base path '{basePath}'.", Indent);

        if (Settings.RequireAuthentication && Authorize(request) is { } denied) return denied;

        bool read = method is "GET" or "HEAD";
        if (relative.Length == 0)
            return read ? HttpResponse.Json(200, Index(), Indent) : MethodNotAllowed("GET, HEAD, OPTIONS");
        if (relative.Equals("_tags", StringComparison.OrdinalIgnoreCase))
            return read ? HttpResponse.Json(200, new JsonArray(model.AllTags().Select(t => (JsonNode)TagObject(t)).ToArray()), Indent) : MethodNotAllowed("GET, HEAD, OPTIONS");
        if (relative.Equals("_db", StringComparison.OrdinalIgnoreCase))
            return read ? HttpResponse.Json(200, model.Database.Snapshot(), Indent) : MethodNotAllowed("GET, HEAD, OPTIONS");

        if (model.FindTag(relative) is { } tag) return Tag(request, tag);

        var segments = relative.Split('/');
        if (segments.Length > 2)
            return HttpResponse.Error(404, "not_found", $"No tag endpoint or database resource matches '{relative}'.", Indent);
        var resource = segments[0];
        var id = segments.Length == 2 ? segments[1] : null;

        switch (method)
        {
            case "GET" or "HEAD":
                return Read(request, resource, id);
            case "POST" or "PUT" or "PATCH":
                if (!TryParseJson(request, out var body, out var error)) return error!;
                var result = method switch
                {
                    "POST" => model.Database.Create(resource, id, body),
                    "PUT" => model.Database.Replace(resource, id, body),
                    _ => model.Database.Patch(resource, id, body),
                };
                var response = FromDb(result);
                if (result.Status == DbStatus.Created && result.Value is JsonObject created)
                {
                    var newId = JsonDatabase.IdOf(created["id"]);
                    response.Headers["Location"] = $"{Settings.BaseUrl}/{resource}" + (newId != null && method == "POST" ? "/" + Uri.EscapeDataString(newId) : string.Empty);
                }
                return response;
            case "DELETE":
                return FromDb(model.Database.Delete(resource, id));
            default:
                return MethodNotAllowed(AllowedMethods);
        }
    }

    // ---- Authentication ----

    private HttpResponse? Authorize(HttpRequest request)
    {
        var header = request.Header("Authorization");
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized(null, "Send an Authorization: Bearer <token> header. Get a token from " + Settings.TokenUrl + ".");
        var token = header[7..].Trim();
        return model.ValidateToken(token) == null
            ? Unauthorized("invalid_token", "The access token is unknown, expired or revoked, or its client was disabled.")
            : null;
    }

    private HttpResponse Unauthorized(string? error, string message)
    {
        var response = HttpResponse.Error(401, error ?? "unauthorized", message, Indent);
        response.Headers["WWW-Authenticate"] = error == null
            ? "Bearer realm=\"ProtoSim\""
            : $"Bearer realm=\"ProtoSim\", error=\"{error}\", error_description=\"{message}\"";
        return response;
    }

    /// <summary>OAuth 2.0 client credentials grant (RFC 6749 §4.4).</summary>
    private HttpResponse Token(HttpRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentType = request.Header("Content-Type") ?? string.Empty;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonNode.Parse(request.Body) is JsonObject o)
                    foreach (var (k, v) in o)
                        if (v is JsonValue jv) fields[k] = jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : jv.ToJsonString();
            }
            catch (JsonException) { return OAuthError(400, "invalid_request", "The request body is not valid JSON."); }
        }
        else
        {
            foreach (var (k, v) in HttpRequest.ParseForm(request.BodyText)) fields[k] = v;
        }

        bool usedBasic = false;
        fields.TryGetValue("client_id", out var clientId);
        fields.TryGetValue("client_secret", out var clientSecret);
        var auth = request.Header("Authorization");
        if (auth != null && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            usedBasic = true;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                int colon = decoded.IndexOf(':');
                if (colon < 0) return OAuthError(400, "invalid_request", "Malformed Basic credentials.");
                clientId = Uri.UnescapeDataString(decoded[..colon].Replace('+', ' '));
                clientSecret = Uri.UnescapeDataString(decoded[(colon + 1)..].Replace('+', ' '));
            }
            catch (FormatException) { return OAuthError(400, "invalid_request", "Malformed Basic credentials."); }
        }

        if (!fields.TryGetValue("grant_type", out var grantType) || grantType.Length == 0)
            return OAuthError(400, "invalid_request", "Missing grant_type. Use grant_type=client_credentials.");
        if (grantType != "client_credentials")
            return OAuthError(400, "unsupported_grant_type", $"Grant type '{grantType}' is not supported. Use client_credentials.");
        if (string.IsNullOrEmpty(clientId) || clientSecret == null)
            return OAuthError(401, "invalid_client", "Send client_id and client_secret in the body or as HTTP Basic credentials.", usedBasic);

        var client = model.Authenticate(clientId, clientSecret);
        if (client == null) return OAuthError(401, "invalid_client", "Unknown client, wrong secret, or the client is disabled.", usedBasic);

        fields.TryGetValue("scope", out var scope);
        if (RestDataModel.GrantedScope(client, scope) == null)
            return OAuthError(400, "invalid_scope", $"Client '{client.ClientId}' may only request: {client.Scope}.");

        var token = model.IssueToken(client, scope);
        var body = new JsonObject
        {
            ["access_token"] = token.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (long)(token.ExpiresUtc - token.IssuedUtc).TotalSeconds,
        };
        if (token.Scope != null) body["scope"] = token.Scope;
        var response = HttpResponse.Json(200, body, Indent);
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Note = $"token issued to {client.ClientId}";
        return response;
    }

    private HttpResponse OAuthError(int status, string error, string description, bool basic = false)
    {
        var response = HttpResponse.Json(status, new JsonObject { ["error"] = error, ["error_description"] = description }, Indent);
        response.Headers["Cache-Control"] = "no-store";
        if (status == 401 && basic) response.Headers["WWW-Authenticate"] = "Basic realm=\"ProtoSim\"";
        return response;
    }

    // ---- Tags ----

    private HttpResponse Tag(HttpRequest request, RestDataModel.TagSlot tag)
    {
        switch (request.Method)
        {
            case "GET" or "HEAD":
                return HttpResponse.Json(200, Settings.TagResponseFormat == TagResponseFormat.ValueOnly ? ToJson(tag.Value) : TagObject(tag), Indent);
            case "PUT" or "PATCH" or "POST":
                JsonNode? body;
                try { body = request.Body.Length == 0 ? null : JsonNode.Parse(request.Body); }
                catch (JsonException) { body = JsonValue.Create(request.BodyText.Trim()); } // plain text value
                if (body is JsonObject o) body = o.TryGetPropertyValue("value", out var v) ? v : null;
                if (body == null)
                    return HttpResponse.Error(400, "bad_request", "Send the new value as JSON, e.g. 42, true, \"text\" or {\"value\": 42}.", Indent);
                try { tag.Write(FromJson(body, tag.DataType)); }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException)
                {
                    return HttpResponse.Error(400, "bad_request", $"Can't write that value to a {tag.DataType} tag: {ex.Message}", Indent);
                }
                return HttpResponse.Json(200, TagObject(tag), Indent);
            default:
                return MethodNotAllowed("GET, HEAD, PUT, PATCH, POST, OPTIONS");
        }
    }

    private static JsonObject TagObject(RestDataModel.TagSlot tag) => new()
    {
        ["path"] = tag.Path,
        ["value"] = ToJson(tag.Value),
        ["dataType"] = tag.DataType.ToString(),
        ["timestamp"] = tag.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
    };

    internal static JsonNode? ToJson(object value) => value switch
    {
        bool b => JsonValue.Create(b),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : null,
        double d => double.IsFinite(d) ? JsonValue.Create(d) : null,
        short s => JsonValue.Create(s),
        ushort us => JsonValue.Create(us),
        int i => JsonValue.Create(i),
        uint ui => JsonValue.Create(ui),
        long l => JsonValue.Create(l),
        ulong ul => JsonValue.Create(ul),
        string str => JsonValue.Create(str),
        _ => JsonValue.Create(ValueConverter.Format(value)),
    };

    internal static object FromJson(JsonNode node, TagDataType type)
    {
        if (node is not JsonValue v) throw new FormatException("expected a single value, not an object or array.");
        switch (v.GetValueKind())
        {
            case JsonValueKind.True: return ValueConverter.Coerce(true, type);
            case JsonValueKind.False: return ValueConverter.Coerce(false, type);
            case JsonValueKind.String: return ValueConverter.Parse(v.GetValue<string>(), type);
            case JsonValueKind.Number:
                var raw = v.ToJsonString();
                try { return ValueConverter.Parse(raw, type); }
                catch (FormatException) when (type is not TagDataType.Bool) { return ValueConverter.FromDouble(v.GetValue<double>(), type); }
            default:
                throw new FormatException("null is not a value.");
        }
    }

    // ---- Database ----

    private HttpResponse Read(HttpRequest request, string resource, string? id)
    {
        var result = model.Database.Read(resource, id);
        if (result.Status != DbStatus.Ok || id != null || result.Value is not JsonArray items) return FromDb(result);
        var (page, total) = JsonQuery.Apply(items, request.Query);
        var response = HttpResponse.Json(200, page, Indent);
        response.Headers["X-Total-Count"] = total.ToString(CultureInfo.InvariantCulture);
        return response;
    }

    private bool TryParseJson(HttpRequest request, out JsonNode? body, out HttpResponse? error)
    {
        body = null;
        error = null;
        if (request.Body.Length == 0)
        {
            error = HttpResponse.Error(400, "bad_request", "The request needs a JSON body.", Indent);
            return false;
        }
        try
        {
            body = JsonNode.Parse(request.Body, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
            return true;
        }
        catch (JsonException ex)
        {
            var hint = (request.Header("Content-Type") ?? string.Empty).Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                ? " (the body was sent as a form; send JSON with Content-Type: application/json)" : string.Empty;
            error = HttpResponse.Error(400, "bad_request", $"The body is not valid JSON{hint}: {ex.Message}", Indent);
            return false;
        }
    }

    private HttpResponse FromDb(DbResult result) => result.Status switch
    {
        DbStatus.Ok => HttpResponse.Json(200, result.Value, Indent),
        DbStatus.Created => HttpResponse.Json(201, result.Value, Indent),
        DbStatus.NoContent => HttpResponse.Empty(204),
        DbStatus.NotFound => HttpResponse.Error(404, "not_found", result.Message ?? "Not found.", Indent),
        DbStatus.Conflict => HttpResponse.Error(409, "conflict", result.Message ?? "Conflict.", Indent),
        DbStatus.MethodNotAllowed => HttpResponse.Error(405, "method_not_allowed", result.Message ?? "Method not allowed.", Indent),
        _ => HttpResponse.Error(400, "bad_request", result.Message ?? "Bad request.", Indent),
    };

    private HttpResponse MethodNotAllowed(string allow)
    {
        var response = HttpResponse.Error(405, "method_not_allowed", $"Allowed methods: {allow}.", Indent);
        response.Headers["Allow"] = allow;
        return response;
    }

    private JsonObject Index() => new()
    {
        ["name"] = "ProtoSim REST API simulator",
        ["baseUrl"] = Settings.BaseUrl,
        ["tokenUrl"] = Settings.TokenUrl,
        ["authentication"] = Settings.RequireAuthentication ? "bearer" : "none",
        ["tags"] = new JsonArray(model.AllTags().Select(t => (JsonNode)JsonValue.Create($"{Settings.BaseUrl}/{t.Path}")).ToArray()),
        ["resources"] = new JsonArray(model.Database.GetResources()
            .Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["type"] = r.IsCollection ? "collection" : "object", ["count"] = r.Count, ["url"] = $"{Settings.BaseUrl}/{r.Name}" }).ToArray()),
    };
}
