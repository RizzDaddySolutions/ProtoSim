using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.Rest;

namespace ProtoSim.Rest.Tests;

public sealed class RestFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "ProtoSimRestTests", Guid.NewGuid().ToString("N"));

    public (RestServerSettings Settings, RestDataModel Model) Create(bool auth = true)
    {
        var settings = (RestServerSettings)new RestPlugin().CreateServerSettings([]);
        settings.RequireAuthentication = auth;
        settings.Port = 0;
        settings.IndentJson = false;
        return (settings, new RestDataModel(settings, Directory));
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}

public sealed class HandlerTests(RestFixture fx) : IClassFixture<RestFixture>
{
    private static JsonNode? Json(HttpResponse r) => JsonNode.Parse(r.Body);

    private static (RestRequestHandler Handler, RestDataModel Model, string Token) Setup(RestFixture fx, bool auth = true)
    {
        var (_, model) = fx.Create(auth);
        var token = model.IssueToken(model.Clients[0]).AccessToken;
        return (new RestRequestHandler(model), model, token);
    }

    private static HttpRequest Req(string method, string target, string? body = null, string? token = null, string contentType = "application/json")
    {
        var headers = new Dictionary<string, string>();
        if (token != null) headers["Authorization"] = "Bearer " + token;
        if (body != null) headers["Content-Type"] = contentType;
        return HttpRequest.Create(method, target, body, headers);
    }

    [Fact]
    public void Token_endpoint_issues_bearer_tokens_for_valid_clients_only()
    {
        var (handler, model, _) = Setup(fx);
        var client = model.Clients[0];

        var ok = handler.Handle(Req("POST", "/oauth/token",
            $"grant_type=client_credentials&client_id={client.ClientId}&client_secret={Uri.EscapeDataString(client.ClientSecret)}", contentType: "application/x-www-form-urlencoded"));
        Assert.Equal(200, ok.Status);
        var body = Json(ok)!;
        Assert.Equal("Bearer", (string?)body["token_type"]);
        Assert.Equal(3600, (long)body["expires_in"]!);
        var token = (string)body["access_token"]!;
        Assert.Equal(3, token.Split('.').Length); // JWT
        Assert.NotNull(model.ValidateToken(token));

        var bad = handler.Handle(Req("POST", "/oauth/token", $"grant_type=client_credentials&client_id={client.ClientId}&client_secret=wrong", contentType: "application/x-www-form-urlencoded"));
        Assert.Equal(401, bad.Status);
        Assert.Equal("invalid_client", (string?)Json(bad)!["error"]);

        var grant = handler.Handle(Req("POST", "/oauth/token", "grant_type=password", contentType: "application/x-www-form-urlencoded"));
        Assert.Equal("unsupported_grant_type", (string?)Json(grant)!["error"]);
    }

    [Fact]
    public void Token_endpoint_accepts_basic_auth_and_json()
    {
        var (handler, model, _) = Setup(fx);
        var c = model.Clients[0];
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ClientId}:{c.ClientSecret}"));
        var r = HttpRequest.Create("POST", "/oauth/token", "grant_type=client_credentials",
            new() { ["Authorization"] = "Basic " + basic, ["Content-Type"] = "application/x-www-form-urlencoded" });
        Assert.Equal(200, handler.Handle(r).Status);

        var json = handler.Handle(Req("POST", "/oauth/token", $"{{\"grant_type\":\"client_credentials\",\"client_id\":\"{c.ClientId}\",\"client_secret\":\"{c.ClientSecret}\"}}"));
        Assert.Equal(200, json.Status);
    }

    [Fact]
    public void Disabled_client_tokens_stop_working()
    {
        var (handler, model, token) = Setup(fx);
        Assert.Equal(200, handler.Handle(Req("GET", "/api/v1/users", token: token)).Status);
        model.Clients[0].Enabled = false;
        Assert.Equal(401, handler.Handle(Req("GET", "/api/v1/users", token: token)).Status);
    }

    [Fact]
    public void Scopes_are_enforced()
    {
        var (handler, model, _) = Setup(fx);
        var c = model.Clients[0];
        c.Scope = "read write";
        var ok = handler.Handle(Req("POST", "/oauth/token", $"grant_type=client_credentials&client_id={c.ClientId}&client_secret={c.ClientSecret}&scope=read", contentType: "application/x-www-form-urlencoded"));
        Assert.Equal("read", (string?)Json(ok)!["scope"]);
        var bad = handler.Handle(Req("POST", "/oauth/token", $"grant_type=client_credentials&client_id={c.ClientId}&client_secret={c.ClientSecret}&scope=admin", contentType: "application/x-www-form-urlencoded"));
        Assert.Equal("invalid_scope", (string?)Json(bad)!["error"]);
    }

    [Fact]
    public void Requests_without_valid_token_are_rejected()
    {
        var (handler, model, token) = Setup(fx);
        var none = handler.Handle(Req("GET", "/api/v1/users"));
        Assert.Equal(401, none.Status);
        Assert.StartsWith("Bearer", none.Headers["WWW-Authenticate"]);
        Assert.Equal(401, handler.Handle(Req("GET", "/api/v1/users", token: "nope")).Status);
        model.RevokeAllTokens();
        Assert.Equal(401, handler.Handle(Req("GET", "/api/v1/users", token: token)).Status);
    }

    [Fact]
    public void Authentication_can_be_turned_off()
    {
        var (handler, _, _) = Setup(fx, auth: false);
        Assert.Equal(200, handler.Handle(Req("GET", "/api/v1/users")).Status);
    }

    [Fact]
    public void Tags_are_served_and_writable()
    {
        var (handler, model, token) = Setup(fx);
        var temp = model.Bind("sensors/temperature", TagDataType.Float32, WordOrder.ABCD);
        temp.Write(21.5);

        var get = handler.Handle(Req("GET", "/api/v1/sensors/temperature", token: token));
        Assert.Equal(200, get.Status);
        Assert.Equal(21.5, (double)Json(get)!["value"]!);
        Assert.Equal("Float32", (string?)Json(get)!["dataType"]);

        Assert.Equal(200, handler.Handle(Req("PUT", "/api/v1/sensors/temperature", "{\"value\": 30.25}", token)).Status);
        Assert.Equal(30.25f, temp.Read());
        Assert.Equal(200, handler.Handle(Req("PUT", "/API/V1/Sensors/Temperature", "12", token)).Status);
        Assert.Equal(12f, temp.Read());

        var flag = model.Bind("pump/running", TagDataType.Bool, WordOrder.ABCD);
        Assert.Equal(200, handler.Handle(Req("PUT", "/api/v1/pump/running", "true", token)).Status);
        Assert.Equal(true, flag.Read());
        Assert.Equal(400, handler.Handle(Req("PUT", "/api/v1/pump/running", "\"maybe\"", token)).Status);

        var all = Json(handler.Handle(Req("GET", "/api/v1/_tags", token: token)))!.AsArray();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Value_only_format_returns_bare_value()
    {
        var (handler, model, token) = Setup(fx);
        model.Settings.TagResponseFormat = TagResponseFormat.ValueOnly;
        model.Bind("count", TagDataType.Int32, WordOrder.ABCD).Write(7);
        Assert.Equal("7", handler.Handle(Req("GET", "/api/v1/count", token: token)).BodyText);
    }

    [Fact]
    public void Tag_binding_is_released_when_last_tag_goes()
    {
        var (_, model, _) = Setup(fx);
        var a = model.Bind("x", TagDataType.Int32, WordOrder.ABCD);
        var b = model.Bind("x", TagDataType.Int32, WordOrder.ABCD);
        Assert.Throws<FormatException>(() => model.Bind("x", TagDataType.Bool, WordOrder.ABCD));
        a.Dispose();
        Assert.NotNull(model.FindTag("x"));
        b.Dispose();
        Assert.Null(model.FindTag("x"));
        Assert.Throws<FormatException>(() => model.Bind("_tags", TagDataType.Int32, WordOrder.ABCD));
    }

    [Fact]
    public void Crud_operations_act_on_the_database()
    {
        var (handler, model, t) = Setup(fx);

        var list = handler.Handle(Req("GET", "/api/v1/users", token: t));
        Assert.Equal(3, Json(list)!.AsArray().Count);
        Assert.Equal("3", list.Headers["X-Total-Count"]);

        var created = handler.Handle(Req("POST", "/api/v1/users", "{\"name\":\"Linus\",\"role\":\"viewer\"}", t));
        Assert.Equal(201, created.Status);
        Assert.Equal(4, (int)Json(created)!["id"]!);
        Assert.EndsWith("/api/v1/users/4", created.Headers["Location"]);

        Assert.Equal("Linus", (string?)Json(handler.Handle(Req("GET", "/api/v1/users/4", token: t)))!["name"]);

        var put = handler.Handle(Req("PUT", "/api/v1/users/4", "{\"name\":\"Linus T\"}", t));
        Assert.Equal(200, put.Status);
        Assert.Null(Json(put)!["role"]); // PUT replaces
        Assert.Equal(4, (int)Json(put)!["id"]!);

        var patch = handler.Handle(Req("PATCH", "/api/v1/users/4", "{\"role\":\"admin\",\"name\":null}", t));
        Assert.Equal("admin", (string?)Json(patch)!["role"]);
        Assert.Null(Json(patch)!["name"]);

        Assert.Equal(204, handler.Handle(Req("DELETE", "/api/v1/users/4", token: t)).Status);
        Assert.Equal(404, handler.Handle(Req("GET", "/api/v1/users/4", token: t)).Status);
        Assert.Equal(404, handler.Handle(Req("DELETE", "/api/v1/users/4", token: t)).Status);

        Assert.Equal(409, handler.Handle(Req("POST", "/api/v1/users", "{\"id\":1}", t)).Status);
        Assert.Equal(400, handler.Handle(Req("POST", "/api/v1/users", "not json", t)).Status);
        Assert.Equal(405, handler.Handle(Req("DELETE", "/api/v1/users", token: t)).Status);

        // New collections are created on first POST.
        Assert.Equal(201, handler.Handle(Req("POST", "/api/v1/alarms", "{\"text\":\"High level\"}", t)).Status);
        Assert.Contains(model.GetCollections(), c => c.Name == "alarms" && c.Count == 1);

        // Singular resources.
        Assert.Equal("UTC", (string?)Json(handler.Handle(Req("GET", "/api/v1/settings", token: t)))!["timezone"]);
        Assert.Equal(200, handler.Handle(Req("PATCH", "/api/v1/settings", "{\"timezone\":\"CET\"}", t)).Status);
        Assert.Equal("CET", (string?)Json(handler.Handle(Req("GET", "/api/v1/settings", token: t)))!["timezone"]);
    }

    [Fact]
    public void Collections_support_filter_sort_and_paging()
    {
        var (handler, _, t) = Setup(fx);
        var admins = Json(handler.Handle(Req("GET", "/api/v1/users?role=admin", token: t)))!.AsArray();
        Assert.Single(admins);

        var sorted = Json(handler.Handle(Req("GET", "/api/v1/products?_sort=-price", token: t)))!.AsArray();
        Assert.Equal("PMP-100", (string?)sorted[0]!["sku"]);

        var cheap = Json(handler.Handle(Req("GET", "/api/v1/products?price_lte=500", token: t)))!.AsArray();
        Assert.Equal(2, cheap.Count);

        var page = handler.Handle(Req("GET", "/api/v1/users?_page=2&_limit=2", token: t));
        Assert.Single(Json(page)!.AsArray());
        Assert.Equal("3", page.Headers["X-Total-Count"]);

        var search = Json(handler.Handle(Req("GET", "/api/v1/users?q=grace", token: t)))!.AsArray();
        Assert.Single(search);
    }

    [Fact]
    public void Paths_outside_base_path_and_unknown_routes_are_404()
    {
        var (handler, _, t) = Setup(fx);
        Assert.Equal(404, handler.Handle(Req("GET", "/other/users", token: t)).Status);
        Assert.Equal(404, handler.Handle(Req("GET", "/api/v1/nothing", token: t)).Status);
        Assert.Equal(404, handler.Handle(Req("GET", "/api/v1/users/1/orders/2", token: t)).Status);
        Assert.Equal(200, handler.Handle(Req("GET", "/api/v1", token: t)).Status);
        Assert.Equal(204, handler.Handle(Req("OPTIONS", "/api/v1/users")).Status);
    }

    [Fact]
    public void Database_persists_and_round_trips_through_export_import()
    {
        var (settings, model) = fx.Create();
        model.Database.Create("notes", null, JsonNode.Parse("{\"text\":\"hello\"}"));
        Assert.True(File.Exists(model.DatabasePath));

        // A second model on the same settings (e.g. reopening the project) sees the same data.
        var reopened = new RestDataModel(settings, fx.Directory);
        Assert.Contains(reopened.GetCollections(), c => c.Name == "notes");

        // Export from one machine, import on another.
        var exported = model.ExportJson();
        var (_, other) = fx.Create();
        Assert.DoesNotContain(other.GetCollections(), c => c.Name == "notes");
        other.ImportJson(exported);
        Assert.Contains(other.GetCollections(), c => c.Name == "notes");

        Assert.Throws<FormatException>(() => other.ImportJson("[1,2]"));
        Assert.Throws<FormatException>(() => other.ImportJson("{\"a\": [1]}"));
        Assert.Throws<FormatException>(() => other.ImportJson("{ broken"));

        other.Recreate();
        Assert.DoesNotContain(other.GetCollections(), c => c.Name == "notes");
        Assert.Contains(other.GetCollections(), c => c.Name == "users");
    }

    [Fact]
    public void Corrupt_database_file_is_set_aside()
    {
        var (settings, model) = fx.Create();
        File.WriteAllText(model.DatabasePath, "{ not json");
        var reopened = new RestDataModel(settings, fx.Directory);
        Assert.NotNull(reopened.Database.LoadWarning);
        Assert.Contains(reopened.GetCollections(), c => c.Name == "users");
    }
}

public sealed class LoopbackTests(RestFixture fx) : IClassFixture<RestFixture>
{
    [Fact]
    public async Task Full_flow_over_http_on_loopback()
    {
        var (settings, model) = fx.Create();
        model.Bind("sensors/temperature", TagDataType.Float32, WordOrder.ABCD).Write(19.5f);
        var log = new TrafficLog();
        var ctx = new ServerContext(log, new FaultSettings(), new ServerStatistics());
        await using var server = new RestServer(settings, model, ctx);
        await server.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Running, server.State);
        Assert.True(IPAddress.IsLoopback(server.LocalEndPoint!.Address));
        int port = server.LocalEndPoint.Port;

        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        var ct = TestContext.Current.CancellationToken;
        var client = model.Clients[0];

        var unauthorized = await http.GetAsync("/api/v1/users", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var tokenResponse = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
        }), ct);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = (string)JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(ct))!["access_token"]!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var temp = await http.GetAsync("/api/v1/sensors/temperature", ct);
        Assert.Equal("application/json; charset=utf-8", temp.Content.Headers.ContentType!.ToString());
        Assert.Equal(19.5, (double)JsonNode.Parse(await temp.Content.ReadAsStringAsync(ct))!["value"]!);

        var post = await http.PostAsync("/api/v1/users", new StringContent("{\"name\":\"Over HTTP\"}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var id = (int)JsonNode.Parse(await post.Content.ReadAsStringAsync(ct))!["id"]!;

        // Large body exercises Expect: 100-continue / chunked handling paths of real clients.
        var big = new string('x', 200_000);
        var put = await http.PutAsync($"/api/v1/users/{id}", new StringContent($"{{\"bio\":\"{big}\"}}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var delete = await http.DeleteAsync($"/api/v1/users/{id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/v1/users"), ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        Assert.NotEmpty(server.GetConnections());
        Assert.True(ctx.Statistics.Requests >= 7);

        await server.StopAsync();
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact]
    public async Task Chunked_request_bodies_are_read()
    {
        var (settings, model) = fx.Create(auth: false);
        await using var server = new RestServer(settings, model, new ServerContext(new TrafficLog(), new FaultSettings(), new ServerStatistics()));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint!.Port, ct);
        var stream = tcp.GetStream();
        var request = "POST /api/v1/notes HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                      "8\r\n{\"text\":\r\n9\r\n\"chunked\"\r\n1\r\n}\r\n0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var reader = new StreamReader(stream);
        var response = await reader.ReadToEndAsync(ct);
        Assert.StartsWith("HTTP/1.1 201 Created", response);
        Assert.Contains("\"chunked\"", response);
    }

    [Fact]
    public async Task Injected_errors_use_http_status()
    {
        var (settings, model) = fx.Create(auth: false);
        var faults = new FaultSettings { ErrorResponsePercent = 100, ErrorCode = 6 };
        await using var server = new RestServer(settings, model, new ServerContext(new TrafficLog(), faults, new ServerStatistics()));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        using var http = new HttpClient();
        var r = await http.GetAsync($"http://127.0.0.1:{server.LocalEndPoint!.Port}/api/v1/users", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }
}
