using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Rest;

/// <summary>
/// Device memory of a REST API simulator: tag endpoints, the JSON database and issued tokens.
/// Created once per device and kept across server restarts.
/// </summary>
public sealed class RestDataModel : IDeviceDataModel, ICredentialProvider, IJsonDatabaseHost
{
    private readonly RestServerSettings _settings;
    private readonly ConcurrentDictionary<string, TagSlot> _tags = new(StringComparer.OrdinalIgnoreCase);
    private volatile ClientCredential[] _clientSnapshot = [];

    public static string DefaultDatabaseDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtoSim", "RestDatabases");

    /// <param name="databaseDirectory">Folder for the database file; null uses <see cref="DefaultDatabaseDirectory"/>.</param>
    public RestDataModel(RestServerSettings settings, string? databaseDirectory = null)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(settings.DatabaseId) || settings.DatabaseId.IndexOfAny([.. Path.GetInvalidFileNameChars(), '.']) >= 0)
            settings.DatabaseId = RestServerSettings.NewDatabaseId();
        DatabasePath = Path.Combine(databaseDirectory ?? DefaultDatabaseDirectory, settings.DatabaseId + ".json");
        Database = new JsonDatabase(DatabasePath);

        settings.Clients ??= [];
        settings.Clients.CollectionChanged += OnClientsChanged;
        OnClientsChanged(null, null);
    }

    public IReadOnlyList<IDataArea> Areas { get; } = [];
    public JsonDatabase Database { get; }
    internal TokenService Tokens { get; } = new();
    internal RestServerSettings Settings => _settings;

    // ---- Tags ----

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var path = NormalizeTagPath(address);
        while (true)
        {
            var slot = _tags.GetOrAdd(path, p => new TagSlot(p, type));
            lock (slot)
            {
                if (slot.Removed) continue; // lost a race with the last handle being disposed
                if (slot.DataType != type)
                    throw new FormatException($"Endpoint '{path}' is already used by a {slot.DataType} tag; use one data type per endpoint.");
                slot.RefCount++;
                return new TagHandle(this, slot);
            }
        }
    }

    internal static string NormalizeTagPath(string address)
    {
        var path = (address ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0)
            throw new FormatException("Enter the endpoint path relative to the base path, e.g. sensors/temperature.");
        if (path.IndexOfAny(['?', '#', ' ']) >= 0 || path.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new FormatException("Endpoint paths can't contain spaces, '?', '#', empty or dot segments.");
        if (path.StartsWith('_'))
            throw new FormatException("Paths starting with '_' are reserved (e.g. _tags, _db).");
        return path;
    }

    internal TagSlot? FindTag(string path) => _tags.TryGetValue(path, out var slot) ? slot : null;

    internal IReadOnlyList<TagSlot> AllTags() => _tags.Values.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();

    private void Release(TagSlot slot)
    {
        lock (slot)
        {
            if (--slot.RefCount > 0) return;
            slot.Removed = true;
            _tags.TryRemove(new KeyValuePair<string, TagSlot>(slot.Path, slot));
        }
    }

    internal sealed class TagSlot(string path, TagDataType type)
    {
        private object _value = type == TagDataType.String ? string.Empty : ValueConverter.FromDouble(0, type);
        private long _timestampTicks = DateTime.UtcNow.Ticks;

        public string Path { get; } = path;
        public TagDataType DataType { get; } = type;
        public int RefCount;
        public bool Removed;

        public object Value => Volatile.Read(ref _value);
        public DateTime TimestampUtc => new(Interlocked.Read(ref _timestampTicks), DateTimeKind.Utc);

        public void Write(object value)
        {
            Volatile.Write(ref _value, ValueConverter.Coerce(value, DataType));
            Interlocked.Exchange(ref _timestampTicks, DateTime.UtcNow.Ticks);
        }
    }

    private sealed class TagHandle(RestDataModel model, TagSlot slot) : ITagBinding
    {
        private int _disposed;
        public TagDataType DataType => slot.DataType;
        public object Read() => slot.Value;
        public void Write(object value) => slot.Write(value);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) model.Release(slot);
        }
    }

    // ---- Clients and tokens ----

    public ObservableCollection<ClientCredential> Clients => _settings.Clients;
    public bool RequiresAuthentication => _settings.RequireAuthentication;
    public string TokenUrl => _settings.TokenUrl;
    public string BaseUrl => _settings.BaseUrl;
    public int ActiveTokenCount => Tokens.ActiveCount;

    private void OnClientsChanged(object? sender, NotifyCollectionChangedEventArgs? e) => _clientSnapshot = [.. _settings.Clients];

    /// <summary>The enabled client with these credentials, or null. Safe to call from any thread.</summary>
    internal ClientCredential? Authenticate(string clientId, string clientSecret)
    {
        foreach (var c in _clientSnapshot)
        {
            if (!c.Enabled || c.ClientId.Length == 0 || !string.Equals(c.ClientId, clientId, StringComparison.Ordinal)) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c.ClientSecret), Encoding.UTF8.GetBytes(clientSecret)))
                return c;
        }
        return null;
    }

    /// <summary>Token record for a valid bearer token whose client still exists and is enabled.</summary>
    internal IssuedToken? ValidateToken(string accessToken)
    {
        var token = Tokens.Validate(accessToken);
        if (token == null) return null;
        return _clientSnapshot.Any(c => c.Enabled && c.ClientId == token.ClientId) ? token : null;
    }

    internal IssuedToken IssueToken(ClientCredential client, string? requestedScope) =>
        Tokens.Issue(client.ClientId, GrantedScope(client, requestedScope), TimeSpan.FromSeconds(Math.Max(10, _settings.TokenLifetimeSeconds)), _settings.Authority);

    public IssuedToken IssueToken(ClientCredential client) => IssueToken(client, null);

    /// <summary>Null when the client asks for a scope it was not granted.</summary>
    internal static string? GrantedScope(ClientCredential client, string? requested)
    {
        var allowed = client.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(requested)) return client.Scope;
        var asked = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (allowed.Length == 0) return string.Join(' ', asked);
        return asked.All(s => allowed.Contains(s, StringComparer.Ordinal)) ? string.Join(' ', asked) : null;
    }

    public void RevokeAllTokens() => Tokens.RevokeAll();

    // ---- Database ----

    public string DatabasePath { get; }
    public long Version => Database.Version;

    public IReadOnlyList<DatabaseCollectionInfo> GetCollections() =>
        Database.GetResources().Select(r => new DatabaseCollectionInfo(r.Name, r.IsCollection ? "Collection" : "Object", r.Count)).ToList();

    public string ExportJson() => Database.ExportJson();
    public void ImportJson(string json) => Database.ImportJson(json);
    public void Recreate() => Database.Recreate();
}
