using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Protocols.Rest;

public sealed class RestPlugin : IProtocolPlugin
{
    public string Id => "rest-api";
    public string DisplayName => "REST API";
    public string Description => "Local-only HTTP JSON API with OAuth 2.0 bearer tokens, simulated GET endpoints and a JSON database for POST/PUT/PATCH/DELETE.";
    public Type ServerSettingsType => typeof(RestServerSettings);
    public Type? ClientSettingsType => typeof(RestClientSettings);
    public Type? ClientRequestType => typeof(RestClientRequest);
    public object CreateClientSettings() => new RestClientSettings();
    public object CreateClientSettingsFor(object serverSettings)
    {
        var s = (RestServerSettings)serverSettings;
        var c = new RestClientSettings { BaseUrl = s.BaseUrl, TokenUrl = s.TokenUrl };
        if (s.RequireAuthentication && s.Clients.FirstOrDefault(x => x.Enabled) is { } cred)
        {
            c.Authentication = RestClientAuth.ClientCredentials;
            c.UserName = cred.ClientId;
            c.Password = cred.ClientSecret;
        }
        return c;
    }
    public object CreateClientRequest() => new RestClientRequest();
    public IProtocolClient CreateClient(object clientSettings, ClientContext context) => new RestApiClient((RestClientSettings)clientSettings, context);
    public string DescribeClient(object clientSettings) => ((RestClientSettings)clientSettings).BaseUrl;
    // ---- Network links: GET each path; "path#json.path" picks a value out of any JSON response ----

    public bool SupportsLinks => true;

    public string SuggestTagAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type, string nameHint) =>
        IProtocolPlugin.UniqueAddress("links/" + IProtocolPlugin.Slug(nameHint), existing);

    /// <summary>The server listens on loopback only, so links work on this computer only.</summary>
    public bool AcceptsRemoteLinks => false;

    private static (string Path, string Extract) SplitLink(LinkPoint p)
    {
        var a = p.Address.Trim();
        if (a.Length == 0) throw new FormatException("Enter a path.");
        int hash = a.IndexOf('#');
        return hash < 0 ? (a.Trim('/'), string.Empty) : (a[..hash].Trim().Trim('/'), a[(hash + 1)..].Trim());
    }

    public IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points) =>
        points.Select((p, i) =>
        {
            var (path, extract) = SplitLink(p);
            return new LinkRead(new RestClientRequest { Method = RestClientMethod.GET, Path = path, Extract = extract }, [i])
            {
                // A ProtoSim tag answers {"path","value",...} or just the value; other APIs need "#json.path".
                Row = (rows, _) => extract.Length > 0 ? rows.FirstOrDefault()
                    : rows.FirstOrDefault(r => r.Address == "value") ?? rows.FirstOrDefault(r => r.Address == "$") ?? (rows.Count == 1 ? rows[0] : null),
            };
        }).ToList();

    public object CreateLinkWrite(LinkPoint point, object value)
    {
        var (path, extract) = SplitLink(point);
        if (extract.Length > 0) throw new NotSupportedException("Points with '#' are read-only.");
        return new RestClientRequest { Method = RestClientMethod.PUT, Path = path, Body = System.Text.Json.JsonSerializer.Serialize(value), ContentType = "application/json" };
    }

    public string AddressHelp => "Endpoint path relative to the base path, e.g. sensors/temperature serves GET {base URL}/sensors/temperature. PUT/PATCH/POST to the same path writes the value. Other paths go to the JSON database (Database tab).";
    public string DefaultTagAddress(int n) => $"tags/tag{n}";

    public object CreateServerSettings(IEnumerable<object> existing)
    {
        var used = existing.OfType<RestServerSettings>().Select(s => s.Port).ToHashSet();
        int port = 5080;
        while (used.Contains(port)) port++;
        return new RestServerSettings
        {
            Port = port,
            Clients =
            [
                new ClientCredential { Name = "Default client", ClientId = "protosim-client", ClientSecret = ClientCredential.NewSecret() },
            ],
        };
    }

    public object PrepareDuplicateSettings(object source, object copy, object fresh)
    {
        var src = (RestServerSettings)source;
        var dst = (RestServerSettings)copy;
        dst.Port = ((RestServerSettings)fresh).Port;
        dst.DatabaseId = RestServerSettings.NewDatabaseId();

        // The copy starts with the same data, in a database file of its own.
        var from = Path.Combine(RestDataModel.DefaultDatabaseDirectory, src.DatabaseId + ".json");
        if (File.Exists(from))
            File.Copy(from, Path.Combine(RestDataModel.DefaultDatabaseDirectory, dst.DatabaseId + ".json"), true);
        return dst;
    }

    public IDeviceDataModel CreateDataModel(object serverSettings) => new RestDataModel((RestServerSettings)serverSettings);

    public IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context) =>
        new RestServer((RestServerSettings)serverSettings, (RestDataModel)dataModel, context);

    public IEnumerable<SimTag> CreateSampleTags()
    {
        yield return new SimTag
        {
            Name = "Temperature", Address = "sensors/temperature", DataType = TagDataType.Float32,
            Description = "Degrees Celsius.",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.RandomWalk, Min = 18, Max = 26, Step = 0.2, IntervalMs = 1000 },
        };
        yield return new SimTag
        {
            Name = "Tank level", Address = "sensors/tank/level", DataType = TagDataType.Float32,
            Description = "Percent full.",
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine, Min = 0, Max = 100, PeriodSeconds = 30, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Pump running", Address = "equipment/pump/running", DataType = TagDataType.Bool,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square, Min = 0, Max = 1, PeriodSeconds = 10, IntervalMs = 250 },
        };
        yield return new SimTag
        {
            Name = "Batch counter", Address = "production/count", DataType = TagDataType.Int32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Counter, Min = 0, Max = 100000, Step = 1, IntervalMs = 2000 },
        };
        yield return new SimTag { Name = "Status", Address = "status", DataType = TagDataType.String, Description = "Writable with PUT.", Writable = true };
    }

    public string DescribeServer(object serverSettings) => ((RestServerSettings)serverSettings).BaseUrl;
}
