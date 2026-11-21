using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ProtoSim.Core.Data;
using ProtoSim.Core.Project;

namespace ProtoSim.Core.Network;

/// <summary>What one ProtoSim instance shares with the network: its devices and their tags.</summary>
public sealed class DiscoveryCatalog
{
    public string InstanceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<SharedDevice> Devices { get; set; } = [];
}

public sealed class SharedDevice
{
    public string Name { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public bool Running { get; set; }

    /// <summary>Where the device listens, as the device shows it (e.g. 0.0.0.0:502).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The device's server settings, from which a client on another computer builds its connection settings.</summary>
    public JsonElement? ServerSettings { get; set; }

    public bool SupportsWrites { get; set; }
    public List<SharedTag> Tags { get; set; } = [];
}

public sealed class SharedTag
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The tag's address as a client reads it (see IProtocolPlugin.LinkSourceAddress).</summary>
    public string Address { get; set; } = string.Empty;
    public TagDataType DataType { get; set; }
    public WordOrder WordOrder { get; set; }
    public bool Writable { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>A ProtoSim instance that answered a discovery query.</summary>
public sealed record RemoteInstance(string Id, string Name, string Project, string Version, string Host, int CatalogPort, int DeviceCount)
{
    public string Display => $"{Name} ({Host}) · {(Project.Length > 0 ? Project + " · " : "")}{DeviceCount} device{(DeviceCount == 1 ? "" : "s")}";
}

/// <summary>
/// LAN discovery of ProtoSim instances. Each instance answers UDP queries on <see cref="Port"/> with a short
/// "here I am", and serves its full catalog (devices and tags as JSON) to anyone who connects to its TCP catalog port.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    public const int Port = 47_900;
    private const string QueryType = "protosim.discover";
    private const string ReplyType = "protosim.here";
    private const int MaxCatalogBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = ProjectSerializer.Options;

    private readonly Func<Task<DiscoveryCatalog>> _catalog;
    private readonly string _id;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private TcpListener? _tcp;

    /// <param name="catalog">Builds the current catalog; called for every catalog request.</param>
    /// <param name="port">UDP port to answer on (tests use their own).</param>
    /// <param name="instanceId">Identity in replies; defaults to <see cref="InstanceId"/>.</param>
    public DiscoveryService(Func<Task<DiscoveryCatalog>> catalog, int port = Port, string? instanceId = null)
    {
        _catalog = catalog;
        _port = port;
        _id = instanceId ?? InstanceId;
    }

    /// <summary>Identifies this process, so it can ignore its own replies.</summary>
    public static string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public bool IsRunning => _cts != null;
    public string? LastError { get; private set; }
    public int CatalogPort { get; private set; }

    /// <summary>Starts answering discovery queries. Returns false (see <see cref="LastError"/>) when the ports can't be opened.</summary>
    public bool Start()
    {
        if (_cts != null) return true;
        try
        {
            _tcp = new TcpListener(IPAddress.Any, 0);
            _tcp.Start();
            CatalogPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;

            var udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false, EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            _udp = udp;
        }
        catch (SocketException ex)
        {
            LastError = $"Network sharing could not open its ports: {ex.Message}";
            _tcp?.Stop();
            _tcp = null;
            _udp?.Dispose();
            _udp = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        LastError = null;
        _ = Task.Run(() => AnswerQueriesAsync(_udp, _cts.Token));
        _ = Task.Run(() => ServeCatalogAsync(_tcp, _cts.Token));
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
        _tcp?.Stop();
        _tcp = null;
        CatalogPort = 0;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private async Task AnswerQueriesAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable from an earlier reply

            try
            {
                using var doc = JsonDocument.Parse(received.Buffer);
                if (!doc.RootElement.TryGetProperty("type", out var t) || t.GetString() != QueryType) continue;
                if (doc.RootElement.TryGetProperty("from", out var from) && from.GetString() == _id) continue;

                var catalog = await _catalog();
                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = ReplyType,
                    v = 1,
                    id = _id,
                    name = catalog.Name,
                    project = catalog.Project,
                    version = catalog.Version,
                    catalogPort = CatalogPort,
                    devices = catalog.Devices.Count,
                });
                await udp.SendAsync(reply, received.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* malformed query or transient error: ignore */ }
        }
    }

    private async Task ServeCatalogAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var catalog = await _catalog();
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, Json);
                        var stream = client.GetStream();
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(10_000);
                        await stream.WriteAsync(bytes, timeout.Token);
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception) { /* client went away */ }
                }
            }, ct);
        }
    }

    // ---- Client side ----

    /// <summary>
    /// Asks the network who is there: broadcasts on every IPv4 interface (and to <paramref name="hosts"/> directly,
    /// for computers on other subnets) and collects replies for <paramref name="wait"/>.
    /// </summary>
    public static async Task<IReadOnlyList<RemoteInstance>> DiscoverAsync(TimeSpan wait, IEnumerable<string>? hosts = null, CancellationToken ct = default, int port = Port)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var query = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = QueryType, v = 1, from = InstanceId }));

        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, port), new(IPAddress.Loopback, port) };
        targets.AddRange(BroadcastAddresses().Select(a => new IPEndPoint(a, port)));
        foreach (var h in hosts ?? [])
        {
            foreach (var a in await ResolveAsync(h, ct)) targets.Add(new IPEndPoint(a, port));
        }
        foreach (var t in targets.Distinct())
        {
            try { await udp.SendAsync(query, t, ct); }
            catch (SocketException) { /* interface down or unreachable */ }
        }

        var found = new Dictionary<string, RemoteInstance>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(wait);
        while (true)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(window.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }
            try
            {
                using var doc = JsonDocument.Parse(r.Buffer);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != ReplyType) continue;
                var id = root.GetProperty("id").GetString() ?? string.Empty;
                if (id == InstanceId || id.Length == 0) continue;
                var host = r.RemoteEndPoint.Address.IsIPv4MappedToIPv6 ? r.RemoteEndPoint.Address.MapToIPv4() : r.RemoteEndPoint.Address;
                var instance = new RemoteInstance(id,
                    root.GetProperty("name").GetString() ?? "ProtoSim",
                    root.TryGetProperty("project", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("version", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                    host.ToString(),
                    root.GetProperty("catalogPort").GetInt32(),
                    root.TryGetProperty("devices", out var d) ? d.GetInt32() : 0);
                // The same instance can answer on several interfaces; prefer a non-loopback address.
                if (!found.TryGetValue(id, out var existing) || IPAddress.IsLoopback(IPAddress.Parse(existing.Host)))
                    found[id] = instance;
            }
            catch (Exception) { /* not a ProtoSim reply */ }
        }
        return found.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Host).ToList();
    }

    /// <summary>Downloads an instance's catalog of devices and tags.</summary>
    public static async Task<DiscoveryCatalog> FetchCatalogAsync(string host, int catalogPort, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(10_000);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, catalogPort, timeout.Token);
        using var buffer = new MemoryStream();
        var stream = tcp.GetStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxCatalogBytes) throw new InvalidDataException("The catalog is too large.");
        }
        buffer.Position = 0;
        return JsonSerializer.Deserialize<DiscoveryCatalog>(buffer, Json) ?? throw new InvalidDataException("The catalog is empty.");
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        host = host.Trim();
        if (host.Length == 0) return [];
        if (IPAddress.TryParse(host, out var ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? [ip] : [];
        try { return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).Take(4).ToArray(); }
        catch (SocketException) { return []; }
    }

    /// <summary>Directed broadcast address of every IPv4 interface that is up (255.255.255.255 only leaves one of them on Windows).</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                var addr = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                if (mask.Length != 4 || mask.All(b => b == 0)) continue;
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(addr[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }
}
