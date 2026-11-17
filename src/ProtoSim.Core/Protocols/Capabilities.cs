using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace ProtoSim.Core.Protocols;

// Optional capabilities a device data model can implement. The UI shows extra tabs for them
// without knowing which protocol is behind the device.

/// <summary>A client allowed to request access tokens (OAuth 2.0 client credentials).</summary>
public sealed class ClientCredential : ObservableObject
{
    private bool _enabled = true;
    private string _name = string.Empty;
    private string _clientId = string.Empty;
    private string _clientSecret = string.Empty;
    private string _scope = string.Empty;

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value ?? string.Empty); }
    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value?.Trim() ?? string.Empty); }
    public string ClientSecret { get => _clientSecret; set => SetProperty(ref _clientSecret, value ?? string.Empty); }

    /// <summary>Space-separated scopes granted to this client. Empty grants whatever is requested.</summary>
    public string Scope { get => _scope; set => SetProperty(ref _scope, value?.Trim() ?? string.Empty); }

    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(24));
    public static string NewClientId() => "client-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record IssuedToken(string AccessToken, string ClientId, string? Scope, DateTime IssuedUtc, DateTime ExpiresUtc);

/// <summary>A device that authenticates clients with bearer tokens.</summary>
public interface ICredentialProvider
{
    /// <summary>Editable list; persisted with the device settings. Only touch it on the UI thread.</summary>
    ObservableCollection<ClientCredential> Clients { get; }

    /// <summary>True when requests must carry a valid bearer token.</summary>
    bool RequiresAuthentication { get; }

    /// <summary>Full URL of the token endpoint, for display.</summary>
    string TokenUrl { get; }

    /// <summary>Base URL of the API, for display.</summary>
    string BaseUrl { get; }

    int ActiveTokenCount { get; }

    /// <summary>Issues a token as if the client had called the token endpoint.</summary>
    IssuedToken IssueToken(ClientCredential client);

    void RevokeAllTokens();
}

public sealed record DatabaseCollectionInfo(string Name, string Kind, int Count);

/// <summary>A device backed by a JSON document database that persists on disk.</summary>
public interface IJsonDatabaseHost
{
    string DatabasePath { get; }

    /// <summary>Incremented on every change, so the UI can tell when to reload.</summary>
    long Version { get; }

    IReadOnlyList<DatabaseCollectionInfo> GetCollections();

    /// <summary>The whole database as indented JSON.</summary>
    string ExportJson();

    /// <summary>Replaces the whole database. Throws <see cref="FormatException"/> when the JSON is not a valid database.</summary>
    void ImportJson(string json);

    /// <summary>Throws away all data and recreates the database with its sample content.</summary>
    void Recreate();
}
