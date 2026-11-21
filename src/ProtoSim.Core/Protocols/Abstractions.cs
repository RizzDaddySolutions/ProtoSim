using System.ComponentModel.DataAnnotations;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Protocols;

/// <summary>
/// Entry point for a protocol. Implement this in a class library referencing ProtoSim.Core
/// and drop the DLL in the app's Plugins folder to add a new protocol.
/// Settings and request objects are plain classes; the UI edits their public properties
/// (honouring [Display], [Range] and [Browsable(false)]) and persists them as JSON.
/// </summary>
public interface IProtocolPlugin
{
    /// <summary>Stable identifier stored in project files, e.g. "modbus-tcp".</summary>
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }

    Type ServerSettingsType { get; }

    /// <summary>Help text describing the tag address syntax.</summary>
    string AddressHelp { get; }

    /// <summary>Address suggested for the n-th tag a user adds (n starts at 1).</summary>
    string DefaultTagAddress(int n);

    /// <summary>
    /// An address for a new tag of <paramref name="type"/> that doesn't collide with <paramref name="existing"/> tags
    /// (used when the network wizard creates tags). <paramref name="nameHint"/> is the source tag's name.
    /// </summary>
    string SuggestTagAddress(IReadOnlyList<(string Address, TagDataType Type)> existing, TagDataType type, string nameHint)
    {
        var used = existing.Select(e => e.Address.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            var a = DefaultTagAddress(n);
            if (!used.Contains(a)) return a;
        }
    }

    /// <summary>Unique "name", "name 2", "name 3"... used by <see cref="SuggestTagAddress"/> implementations.</summary>
    static string UniqueAddress(string candidate, IReadOnlyList<(string Address, TagDataType Type)> existing)
    {
        var used = existing.Select(e => e.Address.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(candidate)) return candidate;
        for (int n = 2; ; n++) if (!used.Contains($"{candidate}{n}")) return $"{candidate}{n}";
    }

    /// <summary>Lower-case words joined by '-', for topic and path suggestions: "Tank level (%)" → "tank-level".</summary>
    static string Slug(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "value" : s;
    }

    /// <summary>Creates settings for a new device; <paramref name="existing"/> lets the plugin avoid e.g. port clashes.</summary>
    object CreateServerSettings(IEnumerable<object> existing);

    IDeviceDataModel CreateDataModel(object serverSettings);
    IProtocolServer CreateServer(object serverSettings, IDeviceDataModel dataModel, ServerContext context);

    IEnumerable<SimTag> CreateSampleTags();

    string DescribeServer(object serverSettings);

    // ---- Client (optional) ----

    /// <summary>Settings type of this protocol's client, or null when the protocol has no client.</summary>
    Type? ClientSettingsType => null;

    /// <summary>Request type of this protocol's client.</summary>
    Type? ClientRequestType => null;

    bool SupportsClient => ClientSettingsType != null;

    object CreateClientSettings() => throw new NotSupportedException($"{DisplayName} has no client.");

    /// <summary>Client settings that connect to a simulated device with the given server settings (used by demos).</summary>
    object CreateClientSettingsFor(object serverSettings) => CreateClientSettings();

    object CreateClientRequest() => throw new NotSupportedException($"{DisplayName} has no client.");

    IProtocolClient CreateClient(object clientSettings, ClientContext context) =>
        throw new NotSupportedException($"{DisplayName} has no client.");

    /// <summary>Short text for the client's target, e.g. "192.168.1.10:502".</summary>
    string DescribeClient(object clientSettings) => string.Empty;

    // ---- Network links (optional) ----

    /// <summary>True when the client can read individual points for network links (see <see cref="CreateLinkReads"/>).</summary>
    bool SupportsLinks => false;

    /// <summary>Client settings that reach a device with these server settings running on <paramref name="host"/>.</summary>
    object CreateLinkClientSettings(object serverSettings, string host) => LinkHost.Retarget(CreateClientSettingsFor(serverSettings), host);

    /// <summary>The client-side address of a tag on a simulated device of this protocol (e.g. OPC UA adds the namespace).</summary>
    string LinkSourceAddress(object serverSettings, string tagAddress) => tagAddress.Trim();

    /// <summary>Requests that read the points. Throw <see cref="FormatException"/> for an address the client can't read.</summary>
    IReadOnlyList<LinkRead> CreateLinkReads(IReadOnlyList<LinkPoint> points) =>
        throw new NotSupportedException($"{DisplayName} does not support network links.");

    /// <summary>A request that writes <paramref name="value"/> to the point, or throws <see cref="NotSupportedException"/> when it can't be written.</summary>
    object CreateLinkWrite(LinkPoint point, object value) =>
        throw new NotSupportedException($"{DisplayName} links are read-only.");

    /// <summary>
    /// True when a device of this protocol can be reached from other computers, so it is shared on the network.
    /// False for loopback-only servers (REST) and serial ports.
    /// </summary>
    bool AcceptsRemoteLinks => SupportsLinks;

    /// <summary>True when the protocol can write values back to its source (<see cref="LinkDirection.ReadWrite"/>).</summary>
    bool SupportsLinkWrites => SupportsLinks;

    /// <summary>True when multi-word values can use a configurable word order (e.g. Modbus registers).
    /// The tag grid hides the Order column for protocols that don't.</summary>
    bool SupportsWordOrder => false;

    /// <summary>
    /// Prepares settings for a copy of a device. <paramref name="copy"/> is a deep copy of
    /// <paramref name="source"/>; <paramref name="fresh"/> came from <see cref="CreateServerSettings"/>.
    /// The default takes the port from <paramref name="fresh"/> so the copy doesn't clash.
    /// </summary>
    object PrepareDuplicateSettings(object source, object copy, object fresh)
    {
        var portProp = copy.GetType().GetProperty("Port");
        if (portProp?.PropertyType == typeof(int) && portProp.CanWrite) portProp.SetValue(copy, portProp.GetValue(fresh));
        return copy;
    }
}

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

/// <summary>Services a server receives from the host. Statistics and faults persist across restarts.</summary>
public sealed record ServerContext(ITrafficLog Log, FaultSettings Faults, ServerStatistics Statistics);

/// <summary>A running (or startable) protocol server/slave/outstation.</summary>
public interface IProtocolServer : IAsyncDisposable
{
    ServerState State { get; }
    string? LastError { get; }
    event EventHandler? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();

    IReadOnlyList<ConnectionInfo> GetConnections();
    void DropConnection(long connectionId);
}

public sealed class ConnectionInfo
{
    private long _requests;
    private long _lastActivityTicks;

    public ConnectionInfo(long id, string remoteEndPoint, DateTime? connectedAt = null)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
        ConnectedAt = connectedAt ?? DateTime.Now;
        _lastActivityTicks = ConnectedAt.Ticks;
    }

    /// <summary>For protocol stacks that track their own per-connection counters.</summary>
    public void SetCounters(long requests, DateTime lastActivity)
    {
        Interlocked.Exchange(ref _requests, requests);
        Interlocked.Exchange(ref _lastActivityTicks, lastActivity.Ticks);
    }

    public long Id { get; }
    public string RemoteEndPoint { get; }
    public DateTime ConnectedAt { get; }
    public long Requests => Interlocked.Read(ref _requests);
    public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks));

    public void OnRequest()
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.Now.Ticks);
    }
}

/// <summary>Thread-safe counters updated by servers.</summary>
public sealed class ServerStatistics
{
    private long _requests, _responses, _exceptions, _dropped, _malformed, _bytesIn, _bytesOut, _connections, _active;

    public long Requests => Interlocked.Read(ref _requests);
    public long Responses => Interlocked.Read(ref _responses);
    public long Exceptions => Interlocked.Read(ref _exceptions);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Malformed => Interlocked.Read(ref _malformed);
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long TotalConnections => Interlocked.Read(ref _connections);
    public long ActiveConnections => Interlocked.Read(ref _active);

    public void OnRequest(int bytes) { Interlocked.Increment(ref _requests); Interlocked.Add(ref _bytesIn, bytes); }
    public void OnResponse(int bytes, bool isException)
    {
        Interlocked.Increment(ref _responses);
        Interlocked.Add(ref _bytesOut, bytes);
        if (isException) Interlocked.Increment(ref _exceptions);
    }
    public void OnDropped() => Interlocked.Increment(ref _dropped);
    public void OnMalformed() => Interlocked.Increment(ref _malformed);
    public void OnConnected() { Interlocked.Increment(ref _connections); Interlocked.Increment(ref _active); }
    public void OnDisconnected() => Interlocked.Decrement(ref _active);

    /// <summary>Resets counters except the active connection count.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _responses, 0);
        Interlocked.Exchange(ref _exceptions, 0);
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _malformed, 0);
        Interlocked.Exchange(ref _bytesIn, 0);
        Interlocked.Exchange(ref _bytesOut, 0);
        Interlocked.Exchange(ref _connections, Interlocked.Read(ref _active));
    }
}

/// <summary>Fault injection applied by servers. Editable while the server is running.</summary>
public sealed class FaultSettings
{
    [Display(Name = "Response delay (ms)", GroupName = "Timing", Order = 1, Description = "Fixed delay added before every response.")]
    [Range(0, 60_000)]
    public int ResponseDelayMs { get; set; }

    [Display(Name = "Delay jitter (ms)", GroupName = "Timing", Order = 2, Description = "Random extra delay between 0 and this value.")]
    [Range(0, 60_000)]
    public int DelayJitterMs { get; set; }

    [Display(Name = "Drop responses (%)", GroupName = "Faults", Order = 3, Description = "Probability that a request gets no response (simulates timeouts).")]
    [Range(0, 100)]
    public double DropResponsePercent { get; set; }

    [Display(Name = "Error responses (%)", GroupName = "Faults", Order = 4, Description = "Probability that a request is answered with a protocol error.")]
    [Range(0, 100)]
    public double ErrorResponsePercent { get; set; }

    [Display(Name = "Error code", GroupName = "Faults", Order = 5, Description = "Protocol error code to inject. Modbus: 1 illegal function, 2 illegal address, 3 illegal value, 4 device failure, 6 busy.")]
    [Range(1, 255)]
    public int ErrorCode { get; set; } = 4;

    [Display(Name = "Disconnect after response (%)", GroupName = "Faults", Order = 6, Description = "Probability that the connection is closed after a response.")]
    [Range(0, 100)]
    public double DisconnectPercent { get; set; }

    public static bool Roll(double percent) => percent > 0 && Random.Shared.NextDouble() * 100 < percent;
}
