using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Rest;

public enum TagResponseFormat
{
    /// <summary>{"path": ..., "value": ..., "dataType": ..., "timestamp": ...}</summary>
    Object,
    /// <summary>Just the JSON value, e.g. 21.5</summary>
    ValueOnly,
}

public sealed class RestServerSettings
{
    [Display(Name = "Host name", GroupName = "Base URL", Order = 1,
        Description = "Host shown in the base URL. The server only listens on this computer's loopback interface, so use localhost or a name mapped to 127.0.0.1 in the hosts file.")]
    public string HostName { get; set; } = "localhost";

    [Display(Name = "TCP port", GroupName = "Base URL", Order = 2)]
    [Range(1, 65535)]
    public int Port { get; set; } = 5080;

    [Display(Name = "Base path", GroupName = "Base URL", Order = 3,
        Description = "Path prefix for every API route, e.g. /api/v1. Use / for none.")]
    public string BasePath { get; set; } = "/api/v1";

    [Display(Name = "Require bearer token", GroupName = "Security", Order = 10,
        Description = "When on, every API request needs an Authorization: Bearer header with a token from the token endpoint. Manage client IDs and secrets on the API clients tab.")]
    public bool RequireAuthentication { get; set; } = true;

    [Display(Name = "Token endpoint path", GroupName = "Security", Order = 11,
        Description = "Path (from the server root, not the base path) of the OAuth 2.0 client credentials token endpoint.")]
    public string TokenPath { get; set; } = "/oauth/token";

    [Display(Name = "Token lifetime (s)", GroupName = "Security", Order = 12)]
    [Range(10, 31_536_000)]
    public int TokenLifetimeSeconds { get; set; } = 3600;

    [Display(Name = "Tag response format", GroupName = "Responses", Order = 20,
        Description = "Object returns path, value, data type and timestamp; ValueOnly returns just the value.")]
    public TagResponseFormat TagResponseFormat { get; set; } = TagResponseFormat.Object;

    [Display(Name = "Indent JSON", GroupName = "Responses", Order = 21)]
    public bool IndentJson { get; set; } = true;

    [Display(Name = "Allow CORS", GroupName = "Responses", Order = 22,
        Description = "Adds Access-Control-Allow-* headers so browser apps on other origins can call the API.")]
    public bool EnableCors { get; set; } = true;

    /// <summary>Edited on the API clients tab.</summary>
    [Browsable(false)]
    public ObservableCollection<ClientCredential> Clients { get; set; } = [];

    /// <summary>Names the database file under %LocalAppData%\ProtoSim\RestDatabases.</summary>
    [Browsable(false)]
    public string DatabaseId { get; set; } = NewDatabaseId();

    public static string NewDatabaseId() => Guid.NewGuid().ToString("N");

    /// <summary>"/api/v1" style: leading slash, no trailing slash; "" for the root.</summary>
    internal static string NormalizePath(string? path)
    {
        var p = (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        return p.Length == 0 ? string.Empty : "/" + p;
    }

    internal string Authority => $"http://{(string.IsNullOrWhiteSpace(HostName) ? "localhost" : HostName.Trim())}:{Port}";
    internal string BaseUrl => Authority + NormalizePath(BasePath);
    internal string TokenUrl => Authority + NormalizePath(TokenPath);
}
