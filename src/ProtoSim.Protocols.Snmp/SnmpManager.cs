using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Snmp;

public enum SnmpManagerVersion { V1, V2c }

public sealed class SnmpManagerSettings
{
    [Display(Name = "Agent host", GroupName = "Target", Order = 1)]
    public string Host { get; set; } = "127.0.0.1";

    [Display(Name = "UDP port", GroupName = "Target", Order = 2)]
    [Range(1, 65535)]
    public int Port { get; set; } = 161;

    [Display(Name = "Version", GroupName = "Target", Order = 3)]
    public SnmpManagerVersion Version { get; set; } = SnmpManagerVersion.V2c;

    [Display(Name = "Read community", GroupName = "Security", Order = 10)]
    public string ReadCommunity { get; set; } = "public";

    [Display(Name = "Write community", GroupName = "Security", Order = 11, Description = "Used for Set.")]
    public string WriteCommunity { get; set; } = "private";

    [Display(Name = "Timeout (ms)", GroupName = "Target", Order = 4)]
    [Range(100, 60_000)]
    public int TimeoutMs { get; set; } = 2000;
}

public enum SnmpManagerOperation
{
    /// <summary>Get the listed OIDs.</summary>
    Get,
    /// <summary>Walk every object below the listed OIDs.</summary>
    Walk,
    /// <summary>Set one OID.</summary>
    Set,
}

public enum SnmpValueType { Integer, Counter32, Gauge32, Unsigned32, TimeTicks, Counter64, String, IpAddress, TruthValue }

public sealed class SnmpManagerRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1)]
    public SnmpManagerOperation Operation { get; set; } = SnmpManagerOperation.Walk;

    [Display(Name = "OIDs", GroupName = "Request", Order = 2, Description = "Numeric OIDs separated by commas, e.g. 1.3.6.1.2.1.1.3.0. Walk: subtree roots such as 1.3.6.1.4.1.")]
    public string Oids { get; set; } = "1.3.6.1.2.1.1";

    [Display(Name = "Value", GroupName = "Request", Order = 3, Description = "Set: the new value.")]
    public string Value { get; set; } = string.Empty;

    [Display(Name = "Value type", GroupName = "Request", Order = 4, Description = "Set: the SNMP type the agent expects.")]
    public SnmpValueType ValueType { get; set; } = SnmpValueType.Integer;

    [Display(Name = "Max objects", GroupName = "Request", Order = 5, Description = "Walk: stop after this many objects.")]
    [Range(1, 100_000)]
    public int MaxObjects { get; set; } = 1000;
}

/// <summary>SNMP manager (v1/v2c): Get, Walk and Set.</summary>
public sealed class SnmpManager(SnmpManagerSettings settings, ClientContext context) : IProtocolClient
{
    private bool _reachable;

    private ITrafficLog Log => context.Log;
    private string Remote => $"{settings.Host}:{settings.Port}";
    private VersionCode Version => settings.Version == SnmpManagerVersion.V1 ? VersionCode.V1 : VersionCode.V2;

    /// <summary>SNMP is connectionless; "connected" means the agent answered the last request.</summary>
    public bool IsConnected => _reachable;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var r = await ExecuteAsync(new SnmpManagerRequest { Operation = SnmpManagerOperation.Get, Oids = "1.3.6.1.2.1.1.1.0" }, cancellationToken);
        if (!r.Success) throw new InvalidOperationException(r.Message);
    }

    public Task DisconnectAsync()
    {
        _reachable = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IPEndPoint> EndpointAsync(CancellationToken ct)
    {
        if (IPAddress.TryParse(settings.Host.Trim(), out var ip)) return new IPEndPoint(ip, settings.Port);
        var addresses = await Dns.GetHostAddressesAsync(settings.Host.Trim(), ct);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First();
        return new IPEndPoint(v4, settings.Port);
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (SnmpManagerRequest)request;
        var sw = Stopwatch.StartNew();
        try
        {
            var ep = await EndpointAsync(cancellationToken);
            var oids = ClientValue.SplitList(r.Oids).Select(o => new ObjectIdentifier(SnmpAddress.NormalizeOid(o))).ToList();
            if (oids.Count == 0) throw new FormatException("Enter at least one OID.");
            IList<Variable> result;
            switch (r.Operation)
            {
                case SnmpManagerOperation.Set:
                {
                    if (oids.Count != 1) throw new FormatException("Set needs exactly one OID.");
                    var data = Encode(r.ValueType, r.Value);
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"Set {oids[0]} = {data} ({r.ValueType})"));
                    result = await Task.Run(() => Messenger.Set(Version, ep, new OctetString(settings.WriteCommunity), [new Variable(oids[0], data)], settings.TimeoutMs), cancellationToken);
                    break;
                }
                case SnmpManagerOperation.Walk:
                {
                    var all = new List<Variable>();
                    foreach (var root in oids)
                    {
                        Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"Walk {root}"));
                        var list = new List<Variable>();
                        await Task.Run(() => Messenger.Walk(Version, ep, new OctetString(settings.ReadCommunity), root, list, settings.TimeoutMs, WalkMode.WithinSubtree), cancellationToken);
                        all.AddRange(list.Take(Math.Max(1, r.MaxObjects - all.Count)));
                        if (all.Count >= r.MaxObjects) break;
                    }
                    result = all;
                    break;
                }
                default:
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"Get {string.Join(", ", oids)}"));
                    result = await Task.Run(() => Messenger.Get(Version, ep, new OctetString(settings.ReadCommunity), oids.Select(o => new Variable(o)).ToList(), settings.TimeoutMs), cancellationToken);
                    break;
            }
            _reachable = true;
            Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote, $"{r.Operation} → {result.Count} variables"));
            var rows = result.Select(v => new ClientResultRow(v.Id.ToString(), v.Data.TypeCode.ToString(), Text(v.Data)) { Typed = Typed(v.Data) }).ToList();
            return ClientResult.Ok($"{r.Operation}: {rows.Count} objects", rows, sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            _reachable = false;
            Log.Error(Remote, $"No response within {settings.TimeoutMs} ms");
            return ClientResult.Fail($"No response within {settings.TimeoutMs} ms (check the host, port and community).", sw.Elapsed);
        }
        catch (ErrorException ex)
        {
            _reachable = true;
            if (ex.Body is not { } body)
            {
                Log.Error(Remote, $"{r.Operation} failed: {ex.Message}");
                return ClientResult.Fail($"Agent error: {ex.Message}", sw.Elapsed);
            }
            var pdu = body.Pdu();
            var status = pdu.ErrorStatus.ToErrorCode();
            Log.Error(Remote, $"{r.Operation} failed: {status} (index {pdu.ErrorIndex})");
            return ClientResult.Fail($"Agent error: {status} (variable {pdu.ErrorIndex})", sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.Error(Remote, $"{r.Operation} failed: {ex.Message}");
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
    }

    private static string Text(ISnmpData d) => d switch
    {
        NoSuchObject or NoSuchInstance or EndOfMibView => d.TypeCode.ToString(),
        _ => d.ToString(),
    };

    private static object? Typed(ISnmpData d) => d switch
    {
        Integer32 i => i.ToInt32(),
        Counter32 c => c.ToUInt32(),
        Gauge32 g => g.ToUInt32(),
        TimeTicks t => t.ToUInt32(),
        Counter64 c => c.ToUInt64(),
        OctetString s => s.ToString(),
        IP ip => ip.ToString(),
        ObjectIdentifier o => o.ToString(),
        _ => null,
    };

    private static ISnmpData Encode(SnmpValueType type, string text)
    {
        var t = (text ?? string.Empty).Trim();
        return type switch
        {
            SnmpValueType.TruthValue => new Integer32((bool)ValueConverter.Parse(t, TagDataType.Bool) ? 1 : 2),
            SnmpValueType.String => new OctetString(t),
            SnmpValueType.IpAddress => IPAddress.TryParse(t, out var ip) ? new IP(ip.GetAddressBytes()) : throw new FormatException($"'{t}' is not an IPv4 address."),
            SnmpValueType.Counter64 => new Counter64((ulong)ValueConverter.Parse(t, TagDataType.UInt64)),
            SnmpValueType.Integer => new Integer32((int)ValueConverter.Parse(t, TagDataType.Int32)),
            SnmpValueType.Counter32 => new Counter32((uint)ValueConverter.Parse(t, TagDataType.UInt32)),
            SnmpValueType.TimeTicks => new TimeTicks((uint)ValueConverter.Parse(t, TagDataType.UInt32)),
            _ => new Gauge32((uint)ValueConverter.Parse(t, TagDataType.UInt32)),
        };
    }
}
