using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Snmp;

/// <summary>
/// SNMPv1/v2c agent over UDP: Get, GetNext, GetBulk and Set on the system group plus one scalar per tag,
/// with community-based access and optional v2c traps. Message encoding comes from Lextm.SharpSnmpLib (MIT).
/// </summary>
public sealed class SnmpAgent : IProtocolServer
{
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromMinutes(10);
    private static readonly ObjectIdentifier ColdStart = new("1.3.6.1.6.3.1.1.5.1");

    private readonly SnmpSettings _settings;
    private readonly SnmpDataModel _model;
    private readonly ServerContext _ctx;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, ConnectionInfo> _peers = new(StringComparer.Ordinal);
    private readonly UserRegistry _users = new();
    private readonly Stopwatch _uptime = new();
    private long _nextPeerId;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SnmpAgent(SnmpSettings settings, SnmpDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _ctx = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    public IPEndPoint? LocalEndPoint => _udp?.Client.LocalEndPoint as IPEndPoint;

    private ITrafficLog Log => _ctx.Log;
    private uint UptimeTicks => (uint)(_uptime.ElapsedMilliseconds / 10 % uint.MaxValue);

    // ---- Lifecycle ----

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (State == ServerState.Running) return;
            SetState(ServerState.Starting);
            LastError = null;
            try
            {
                var ip = TcpFrameServer.ParseBindAddress(_settings.BindAddress);
                var udp = new UdpClient(ip.AddressFamily);
                if (OperatingSystem.IsWindows())
                {
                    // Don't let an ICMP port-unreachable from a vanished manager kill the receive loop.
                    const int SioUdpConnReset = -1744830452;
                    udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                }
                udp.Client.Bind(new IPEndPoint(ip, _settings.Port));
                _udp = udp;
                _uptime.Restart();
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
                Log.Info("", $"SNMP agent listening on {udp.Client.LocalEndPoint}/udp ({Versions()})");
                SetState(ServerState.Running);
                if (_settings.SendColdStart) SendTrap(ColdStart, []);
            }
            catch (Exception ex)
            {
                _udp?.Dispose();
                _udp = null;
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                    ? $"UDP port {_settings.Port} is already in use (the Windows SNMP service uses 161; pick another port such as 1161)."
                    : ex.Message;
                Log.Error("", $"Start failed: {LastError}");
                SetState(ServerState.Faulted);
                throw new InvalidOperationException(LastError, ex);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_udp == null) return;
            SetState(ServerState.Stopping);
            _cts?.Cancel();
            _udp.Dispose();
            try { await (_loop ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            _udp = null;
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _peers.Clear();
            Log.Info("", "Agent stopped");
            SetState(ServerState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public IReadOnlyList<ConnectionInfo> GetConnections()
    {
        var cutoff = DateTime.Now - PeerExpiry;
        foreach (var (key, peer) in _peers)
            if (peer.LastActivity < cutoff) _peers.TryRemove(key, out _);
        return _peers.Values.OrderBy(p => p.Id).ToList();
    }

    /// <summary>UDP has no connection; this just forgets the manager.</summary>
    public void DropConnection(long connectionId)
    {
        var entry = _peers.FirstOrDefault(p => p.Value.Id == connectionId);
        if (entry.Key != null && _peers.TryRemove(entry.Key, out _)) Log.Info(entry.Key, "Manager forgotten by user");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Versions() => string.Join(", ", new[] { _settings.AllowV1 ? "v1" : null, _settings.AllowV2c ? "v2c" : null }.OfType<string>().DefaultIfEmpty("no versions enabled"));

    // ---- Receive loop ----

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn("", $"Receive: {ex.Message}");
                continue;
            }

            try
            {
                var reply = await HandleAsync(packet.Buffer, packet.RemoteEndPoint, ct);
                if (reply != null)
                {
                    await udp.SendAsync(reply, packet.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Error(packet.RemoteEndPoint.ToString(), $"Request failed: {ex.Message}");
            }
        }
    }

    /// <summary>Processes one datagram; returns the response bytes or null for no reply.</summary>
    internal async Task<byte[]?> HandleAsync(byte[] datagram, IPEndPoint remote, CancellationToken ct)
    {
        string peer = remote.ToString();
        _ctx.Statistics.OnRequest(datagram.Length);

        ISnmpMessage message;
        try
        {
            var messages = MessageFactory.ParseMessages(datagram, _users);
            if (messages.Count == 0) throw new SnmpException("empty datagram");
            message = messages[0];
        }
        catch (Exception ex)
        {
            _ctx.Statistics.OnMalformed();
            Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Error, peer,
                LooksLikeV3(datagram) ? "SNMPv3 is not supported; request ignored" : $"Unparseable SNMP message: {ex.Message}", datagram));
            return null;
        }

        var version = message.Version;
        var type = message.TypeCode();
        var community = message.Parameters.UserName?.ToString() ?? string.Empty;
        var pdu = message.Pdu();
        int requestId = pdu.RequestId.ToInt32();
        var variables = pdu.Variables;

        Touch(peer);
        Log.Frame(TrafficDirection.Rx, peer, datagram, () => $"{TypeName(type)} {VersionName(version)} '{community}' · {DescribeOids(variables)}");

        if (version == VersionCode.V3 || (version == VersionCode.V1 && !_settings.AllowV1) || (version == VersionCode.V2 && !_settings.AllowV2c))
        {
            _ctx.Statistics.OnDropped();
            Log.Warn(peer, $"{VersionName(version)} is not enabled; request ignored");
            return null;
        }
        if (type is not (SnmpType.GetRequestPdu or SnmpType.GetNextRequestPdu or SnmpType.GetBulkRequestPdu or SnmpType.SetRequestPdu))
        {
            Log.Info(peer, $"{TypeName(type)} ignored (an agent only answers requests)");
            return null;
        }
        if (type == SnmpType.GetBulkRequestPdu && version == VersionCode.V1)
        {
            _ctx.Statistics.OnMalformed();
            Log.Warn(peer, "GetBulk is not part of SNMPv1; ignored");
            return null;
        }

        bool canWrite = !string.IsNullOrEmpty(_settings.WriteCommunity) && community == _settings.WriteCommunity;
        bool canRead = canWrite || community == _settings.ReadCommunity;
        if (!canRead)
        {
            _ctx.Statistics.OnDropped();
            Log.Warn(peer, $"Unknown community '{community}'; request ignored");
            return null;
        }

        var faults = _ctx.Faults;
        if (FaultSettings.Roll(faults.DropResponsePercent))
        {
            _ctx.Statistics.OnDropped();
            Log.Warn(peer, "Fault injection: no response");
            return null;
        }
        int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
        if (delay > 0) await Task.Delay(delay, ct);

        (ErrorCode Error, int Index, IList<Variable> Vars) result;
        if (FaultSettings.Roll(faults.ErrorResponsePercent))
        {
            var code = (ErrorCode)Math.Clamp(faults.ErrorCode, 1, 18);
            Log.Warn(peer, $"Fault injection: error {code}");
            result = (code, variables.Count > 0 ? 1 : 0, variables);
        }
        else
        {
            result = type switch
            {
                SnmpType.GetRequestPdu => Get(version, variables),
                SnmpType.GetNextRequestPdu => GetNext(version, variables),
                SnmpType.GetBulkRequestPdu => GetBulk(variables, pdu.ErrorStatus.ToInt32(), pdu.ErrorIndex.ToInt32()),
                _ => Set(version, variables, canWrite, peer),
            };
        }

        var response = new ResponseMessage(requestId, version, new OctetString(community), result.Error, result.Index, result.Vars).ToBytes();
        _ctx.Statistics.OnResponse(response.Length, result.Error != ErrorCode.NoError);
        Log.Frame(TrafficDirection.Tx, peer, response, () =>
            $"Response {(result.Error == ErrorCode.NoError ? "noError" : $"{result.Error} at {result.Index}")} · {DescribeValues(result.Vars)}");
        return response;
    }

    /// <summary>True when the BER header says version 3 (SEQUENCE, length, INTEGER 3).</summary>
    private static bool LooksLikeV3(byte[] d)
    {
        if (d.Length < 5 || d[0] != 0x30) return false;
        int i = 2;
        if ((d[1] & 0x80) != 0) i += d[1] & 0x7F;
        return i + 2 < d.Length && d[i] == 0x02 && d[i + 1] == 0x01 && d[i + 2] == 0x03;
    }

    private void Touch(string peer)
    {
        var p = _peers.GetOrAdd(peer, k => new ConnectionInfo(Interlocked.Increment(ref _nextPeerId), k));
        p.OnRequest();
    }

    // ---- MIB view ----

    private sealed record Entry(uint[] Oid, ObjectIdentifier Id, Func<ISnmpData> Read, Core.Data.NamedTag? Tag, SnmpSyntax Syntax);

    /// <summary>Every object the agent serves, in OID order.</summary>
    private List<Entry> View()
    {
        var list = new List<Entry>
        {
            Sys(1, () => new OctetString(_settings.SysDescr ?? string.Empty)),
            Sys(2, () => TryOid(_settings.SysObjectId)),
            Sys(3, () => new TimeTicks(UptimeTicks)),
            Sys(4, () => new OctetString(_settings.SysContact ?? string.Empty)),
            Sys(5, () => new OctetString(_settings.SysName ?? string.Empty)),
            Sys(6, () => new OctetString(_settings.SysLocation ?? string.Empty)),
            Sys(7, () => new Integer32(_settings.SysServices)),
        };
        foreach (var tag in _model.Store.All())
        {
            var id = new ObjectIdentifier(tag.Name);
            var syntax = _model.SyntaxOf(tag);
            list.Add(new Entry(id.ToNumerical(), id, () => SnmpAddress.Encode(syntax, tag.Value), tag, syntax));
        }
        list.Sort((a, b) => OidComparer.Instance.Compare(a.Oid, b.Oid));
        return list;
    }

    private static Entry Sys(uint n, Func<ISnmpData> read)
    {
        var id = new ObjectIdentifier($"1.3.6.1.2.1.1.{n}.0");
        return new Entry(id.ToNumerical(), id, read, null, SnmpSyntax.String);
    }

    private static ObjectIdentifier TryOid(string text)
    {
        try { return new ObjectIdentifier(SnmpAddress.NormalizeOid(text)); }
        catch (FormatException) { return new ObjectIdentifier("0.0"); }
    }

    private static Entry? Exact(List<Entry> view, uint[] oid)
    {
        int i = view.BinarySearch(new Entry(oid, null!, null!, null, default), EntryComparer.Instance);
        return i >= 0 ? view[i] : null;
    }

    private static Entry? Next(List<Entry> view, uint[] oid)
    {
        int i = view.BinarySearch(new Entry(oid, null!, null!, null, default), EntryComparer.Instance);
        i = i >= 0 ? i + 1 : ~i;
        return i < view.Count ? view[i] : null;
    }

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();
        public int Compare(Entry? x, Entry? y) => OidComparer.Instance.Compare(x?.Oid, y?.Oid);
    }

    // ---- Operations ----

    private (ErrorCode, int, IList<Variable>) Get(VersionCode version, IList<Variable> request)
    {
        var view = View();
        var result = new List<Variable>(request.Count);
        for (int i = 0; i < request.Count; i++)
        {
            var e = Exact(view, request[i].Id.ToNumerical());
            if (e != null) { result.Add(new Variable(e.Id, e.Read())); continue; }
            if (version == VersionCode.V1) return (ErrorCode.NoSuchName, i + 1, request);
            // Same object (every arc but the instance matches) → noSuchInstance, otherwise noSuchObject.
            var wanted = request[i].Id.ToNumerical();
            bool objectExists = wanted.Length > 1 && view.Any(v => v.Oid.Length == wanted.Length && v.Oid.AsSpan(0, v.Oid.Length - 1).SequenceEqual(wanted.AsSpan(0, wanted.Length - 1)));
            result.Add(new Variable(request[i].Id, objectExists ? new NoSuchInstance() : new NoSuchObject()));
        }
        return (ErrorCode.NoError, 0, result);
    }

    private (ErrorCode, int, IList<Variable>) GetNext(VersionCode version, IList<Variable> request)
    {
        var view = View();
        var result = new List<Variable>(request.Count);
        for (int i = 0; i < request.Count; i++)
        {
            var e = Next(view, request[i].Id.ToNumerical());
            if (e != null) { result.Add(new Variable(e.Id, e.Read())); continue; }
            if (version == VersionCode.V1) return (ErrorCode.NoSuchName, i + 1, request);
            result.Add(new Variable(request[i].Id, new EndOfMibView()));
        }
        return (ErrorCode.NoError, 0, result);
    }

    private (ErrorCode, int, IList<Variable>) GetBulk(IList<Variable> request, int nonRepeaters, int maxRepetitions)
    {
        var view = View();
        nonRepeaters = Math.Clamp(nonRepeaters, 0, request.Count);
        maxRepetitions = Math.Max(0, maxRepetitions);
        int limit = Math.Max(1, _settings.MaxBulkVariables);
        var result = new List<Variable>();

        for (int i = 0; i < nonRepeaters; i++)
        {
            var e = Next(view, request[i].Id.ToNumerical());
            result.Add(e != null ? new Variable(e.Id, e.Read()) : new Variable(request[i].Id, new EndOfMibView()));
        }

        var cursors = request.Skip(nonRepeaters).Select(v => v.Id.ToNumerical()).ToArray();
        for (int r = 0; r < maxRepetitions && cursors.Length > 0 && result.Count + cursors.Length <= limit; r++)
        {
            bool any = false;
            for (int c = 0; c < cursors.Length; c++)
            {
                var e = Next(view, cursors[c]);
                if (e != null)
                {
                    result.Add(new Variable(e.Id, e.Read()));
                    cursors[c] = e.Oid;
                    any = true;
                }
                else
                {
                    result.Add(new Variable(new ObjectIdentifier(cursors[c]), new EndOfMibView()));
                }
            }
            if (!any) break;
        }
        return (ErrorCode.NoError, 0, result);
    }

    private (ErrorCode, int, IList<Variable>) Set(VersionCode version, IList<Variable> request, bool canWrite, string peer)
    {
        bool v1 = version == VersionCode.V1;
        if (!canWrite)
        {
            Log.Warn(peer, "Set with the read-only community refused");
            return (v1 ? ErrorCode.NoSuchName : ErrorCode.NoAccess, request.Count > 0 ? 1 : 0, request);
        }

        var view = View();
        var pending = new List<(Core.Data.NamedTag Tag, object Value)>();
        for (int i = 0; i < request.Count; i++)
        {
            var e = Exact(view, request[i].Id.ToNumerical());
            if (e == null) return (v1 ? ErrorCode.NoSuchName : ErrorCode.NoCreation, i + 1, request);
            if (e.Tag == null) return (v1 ? ErrorCode.ReadOnly : ErrorCode.NotWritable, i + 1, request);
            if (request[i].Data.TypeCode != SnmpAddress.TypeOf(e.Syntax)) return (v1 ? ErrorCode.BadValue : ErrorCode.WrongType, i + 1, request);
            var value = SnmpAddress.Decode(e.Syntax, request[i].Data, e.Tag.DataType);
            if (value == null) return (v1 ? ErrorCode.BadValue : ErrorCode.WrongValue, i + 1, request);
            pending.Add((e.Tag, value));
        }

        foreach (var (tag, value) in pending) tag.Write(value);
        if (_settings.TrapOnSet && pending.Count > 0)
        {
            var trapOid = TryOid(_settings.SysObjectId).ToNumerical().Concat(new uint[] { 0, 1 }).ToArray();
            SendTrap(new ObjectIdentifier(trapOid), request.Select(v => new Variable(v.Id, v.Data)).ToList());
        }
        return (ErrorCode.NoError, 0, request);
    }

    // ---- Traps ----

    private void SendTrap(ObjectIdentifier trapOid, IList<Variable> variables)
    {
        foreach (var receiver in ParseReceivers(_settings.TrapReceivers))
        {
            try
            {
                Messenger.SendTrapV2(0, VersionCode.V2, receiver, new OctetString(_settings.TrapCommunity ?? "public"), trapOid, UptimeTicks, variables);
                _ctx.Statistics.OnResponse(0, false);
                Log.Frame(TrafficDirection.Tx, receiver.ToString(), [], () => $"TrapV2 {trapOid}{(variables.Count > 0 ? " · " + DescribeValues(variables) : "")}");
            }
            catch (Exception ex)
            {
                Log.Warn(receiver.ToString(), $"Trap failed: {ex.Message}");
            }
        }
    }

    internal static IReadOnlyList<IPEndPoint> ParseReceivers(string? spec)
    {
        var list = new List<IPEndPoint>();
        foreach (var raw in (spec ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim();
            if (IPEndPoint.TryParse(s, out var ep))
            {
                if (ep.Port == 0) ep.Port = 162;
                list.Add(ep);
                continue;
            }
            int colon = s.LastIndexOf(':');
            string host = colon > 0 ? s[..colon] : s;
            int port = colon > 0 && int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 162;
            try
            {
                var addr = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (addr != null) list.Add(new IPEndPoint(addr, port));
            }
            catch (SocketException) { }
        }
        return list;
    }

    // ---- Formatting ----

    private static string TypeName(SnmpType t) => t switch
    {
        SnmpType.GetRequestPdu => "Get",
        SnmpType.GetNextRequestPdu => "GetNext",
        SnmpType.GetBulkRequestPdu => "GetBulk",
        SnmpType.SetRequestPdu => "Set",
        _ => t.ToString(),
    };

    private static string VersionName(VersionCode v) => v switch { VersionCode.V1 => "v1", VersionCode.V2 => "v2c", VersionCode.V3 => "v3", _ => v.ToString() };

    private static string DescribeOids(IList<Variable> vars) =>
        vars.Count == 0 ? "no variables" : string.Join(", ", vars.Take(6).Select(v => v.Id.ToString())) + (vars.Count > 6 ? $" … +{vars.Count - 6}" : "");

    private static string DescribeValues(IList<Variable> vars) =>
        vars.Count == 0 ? "no variables" : string.Join(", ", vars.Take(6).Select(v => $"{v.Id} = {v.Data}")) + (vars.Count > 6 ? $" … +{vars.Count - 6}" : "");
}
