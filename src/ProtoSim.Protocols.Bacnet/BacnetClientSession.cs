using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO.BACnet;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Bacnet;

public sealed class BacnetClientSettings
{
    [Display(Name = "Device address", GroupName = "Target", Order = 1, Description = "IP and UDP port of the BACnet device, e.g. 192.168.1.40:47808.")]
    public string DeviceAddress { get; set; } = "127.0.0.1:47808";

    [Display(Name = "Device instance", GroupName = "Target", Order = 2, Description = "Instance of the device object; Who-Is shows it.")]
    [Range(0, 4_194_302)]
    public int DeviceInstance { get; set; } = 1001;

    [Display(Name = "Local IP address", GroupName = "Network", Order = 10, Description = "Network interface to send from; empty = automatic.")]
    public string LocalAddress { get; set; } = string.Empty;

    [Display(Name = "Local UDP port", GroupName = "Network", Order = 11,
        Description = "0 = any free port (fine for reads and writes, and for devices that answer Who-Is directly). Use 47808 to also hear broadcast I-Am replies, if no other program on this PC uses it.")]
    [Range(0, 65535)]
    public int LocalPort { get; set; }

    [Display(Name = "Timeout (ms)", GroupName = "Network", Order = 12)]
    [Range(100, 60_000)]
    public int TimeoutMs { get; set; } = 2000;

    [Display(Name = "Retries", GroupName = "Network", Order = 13)]
    [Range(0, 10)]
    public int Retries { get; set; } = 2;
}

public enum BacnetClientOperation
{
    /// <summary>Read the device's object list and every object's name and present value.</summary>
    ReadObjects,
    /// <summary>Read one property of the listed objects.</summary>
    ReadProperty,
    /// <summary>Write the present value (or another property) of one object.</summary>
    WriteProperty,
    /// <summary>Broadcast Who-Is and list the devices that answer.</summary>
    WhoIs,
}

public sealed class BacnetClientRequest
{
    [Display(Name = "Operation", GroupName = "Request", Order = 1)]
    public BacnetClientOperation Operation { get; set; } = BacnetClientOperation.ReadObjects;

    [Display(Name = "Objects", GroupName = "Request", Order = 2,
        Description = "Read/write property: objects such as AI:1, AV:3, BO:0, MSV:2 (several separated by commas). Read objects: optional filter; empty = all.")]
    public string Objects { get; set; } = string.Empty;

    [Display(Name = "Property", GroupName = "Request", Order = 3)]
    public BacnetPropertyIds Property { get; set; } = BacnetPropertyIds.PROP_PRESENT_VALUE;

    [Display(Name = "Value", GroupName = "Request", Order = 4, Description = "Write: the new value; 'null' relinquishes the priority level.")]
    public string Value { get; set; } = string.Empty;

    [Display(Name = "Priority", GroupName = "Request", Order = 5, Description = "Write priority 1-16 for commandable objects (8 = manual operator, 16 = lowest).")]
    [Range(1, 16)]
    public int Priority { get; set; } = 16;

    [Display(Name = "Who-Is wait (ms)", GroupName = "Request", Order = 6)]
    [Range(100, 30_000)]
    public int WhoIsWaitMs { get; set; } = 2000;
}

/// <summary>BACnet/IP client: discovery, object lists, ReadProperty and WriteProperty.</summary>
public sealed class BacnetClientSession(BacnetClientSettings settings, ClientContext context) : IProtocolClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<uint, (string Address, ushort Vendor)> _devices = new();
    private BacnetClient? _client;
    private BacnetAddress? _target;

    private ITrafficLog Log => context.Log;
    private string Remote => settings.DeviceAddress;
    public bool IsConnected => _client != null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { Open(); }
        finally { _gate.Release(); }
    }

    private BacnetClient Open()
    {
        if (_client != null) return _client;
        var transport = new BacnetIpUdpProtocolTransport(settings.LocalPort, false, false, 1472, settings.LocalAddress ?? string.Empty);
        var client = new BacnetClient(transport, settings.TimeoutMs, settings.Retries);
        client.OnIam += (sender, adr, deviceId, maxApdu, segmentation, vendorId) =>
        {
            _devices[deviceId] = (adr.ToString(), vendorId);
            Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, adr.ToString(), $"I-Am device {deviceId} · vendor {vendorId}"));
        };
        try
        {
            client.Start();
        }
        catch (Exception ex)
        {
            client.Dispose();
            Log.Error(Remote, $"Open failed: {ex.Message}");
            throw new InvalidOperationException($"Couldn't open UDP port {settings.LocalPort}: {ex.Message}", ex);
        }
        _client = client;
        _target = new BacnetAddress(BacnetAddressTypes.IP, settings.DeviceAddress.Trim(), 0);
        Log.Info(Remote, "Ready");
        return client;
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _client?.Dispose();
            _client = null;
            _devices.Clear();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (BacnetClientRequest)request;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = Open();
            var target = _target!;
            switch (r.Operation)
            {
                case BacnetClientOperation.WhoIs:
                {
                    _devices.Clear();
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, "broadcast", "Who-Is"));
                    client.WhoIs(-1, -1, null, null);
                    client.WhoIs(-1, -1, target, null); // devices on this PC often don't hear broadcasts
                    await Task.Delay(r.WhoIsWaitMs, cancellationToken);
                    var rows = _devices.OrderBy(d => d.Key).Select(d =>
                        new ClientResultRow($"Device:{d.Key}", $"vendor {d.Value.Vendor}", d.Value.Address) { Typed = d.Value.Address }).ToList();
                    return ClientResult.Ok($"{rows.Count} device(s) answered", rows, sw.Elapsed);
                }
                case BacnetClientOperation.WriteProperty:
                {
                    var ids = ParseObjects(r.Objects);
                    if (ids.Count != 1) throw new FormatException("Write needs exactly one object, e.g. AV:3.");
                    var id = ids[0];
                    var value = EncodeValue(id.Type, r.Property, r.Value);
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"WriteProperty {Key(id)} {Short(r.Property)} = {r.Value} @{r.Priority}"));
                    await client.WritePropertyAsync(target, id, r.Property, [value], (byte)Math.Clamp(r.Priority, 1, 16), null, 0, cancellationToken);
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote, "WriteProperty → OK"));
                    return ClientResult.Ok($"Wrote {Key(id)}", [new ClientResultRow(Key(id), Short(r.Property), r.Value) { Typed = Typed(id.Type, value) }], sw.Elapsed);
                }
                case BacnetClientOperation.ReadProperty:
                {
                    var ids = ParseObjects(r.Objects);
                    if (ids.Count == 0) throw new FormatException("Enter at least one object, e.g. AI:1.");
                    var rows = new List<ClientResultRow>();
                    foreach (var id in ids)
                        rows.Add(await ReadRowAsync(client, target, id, r.Property, cancellationToken));
                    return ClientResult.Ok($"{rows.Count} values", rows, sw.Elapsed);
                }
                default:
                {
                    var deviceId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, (uint)settings.DeviceInstance);
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, Remote, $"ReadProperty device {settings.DeviceInstance} object-list"));
                    var list = await client.ReadPropertyAsync(target, deviceId, BacnetPropertyIds.PROP_OBJECT_LIST, 0, uint.MaxValue, cancellationToken);
                    var objects = list.Select(v => v.Value).OfType<BacnetObjectId>()
                        .Where(o => o.Type != BacnetObjectTypes.OBJECT_DEVICE && BacnetAddressSpec.TryParseKey(Key(o), out _, out _)).ToList();
                    var filter = ParseObjects(r.Objects);
                    if (filter.Count > 0) objects = objects.Where(o => filter.Contains(o)).ToList();
                    Log.Add(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, Remote, $"object-list → {objects.Count} objects"));

                    var rows = new List<ClientResultRow>();
                    foreach (var id in objects.Take(500))
                    {
                        var row = await ReadRowAsync(client, target, id, BacnetPropertyIds.PROP_PRESENT_VALUE, cancellationToken);
                        string name = "";
                        try
                        {
                            var n = await client.ReadPropertyAsync(target, id, BacnetPropertyIds.PROP_OBJECT_NAME, 0, uint.MaxValue, cancellationToken);
                            name = Convert.ToString(n.FirstOrDefault().Value, CultureInfo.InvariantCulture) ?? "";
                        }
                        catch (Exception) { /* name is optional */ }
                        rows.Add(row with { Raw = $"{name} · {row.Raw}" });
                    }
                    return ClientResult.Ok($"{rows.Count} objects from device {settings.DeviceInstance}", rows, sw.Elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(Remote, $"{r.Operation} failed: {ex.Message}");
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClientResultRow> ReadRowAsync(BacnetClient client, BacnetAddress target, BacnetObjectId id, BacnetPropertyIds prop, CancellationToken ct)
    {
        try
        {
            var values = await client.ReadPropertyAsync(target, id, prop, 0, uint.MaxValue, ct);
            if (values.Count == 0) return new ClientResultRow(Key(id), $"{Short(prop)} · empty", "");
            var v = values[0];
            string text = values.Count > 1 ? "[" + string.Join(", ", values.Take(20).Select(x => Format(x.Value))) + "]" : Format(v.Value);
            return new ClientResultRow(Key(id), $"{Short(prop)} · {v.Tag}", text) { Typed = values.Count == 1 ? Typed(id.Type, v) : text };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClientResultRow(Key(id), $"{Short(prop)} · error", ex.Message);
        }
    }

    private static string Key(BacnetObjectId id) => BacnetAddressSpec.KeyOf(id.Type, id.Instance);

    private static string Short(BacnetPropertyIds p) => p.ToString().Replace("PROP_", "").ToLowerInvariant().Replace('_', '-');

    private static string Format(object? v) => v switch
    {
        null => "null",
        float f => f.ToString("G7", CultureInfo.InvariantCulture),
        double d => d.ToString("G15", CultureInfo.InvariantCulture),
        BacnetObjectId o => Key(o),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static object? Typed(BacnetObjectTypes type, BacnetValue v) => (BacnetAddressSpec.KindOf(type), v.Value) switch
    {
        (_, null) => null,
        (BacnetValueKind.Binary, uint u) => u != 0,
        (BacnetValueKind.Binary, bool b) => b,
        (_, float f) => (double)f,
        (_, BacnetObjectId o) => Key(o),
        (_, var other) => other,
    };

    private static List<BacnetObjectId> ParseObjects(string text) =>
        ClientValue.SplitList(text).Select(s =>
        {
            var spec = BacnetAddressSpec.Parse(s);
            return new BacnetObjectId(spec.Type, spec.Instance);
        }).ToList();

    private static BacnetValue EncodeValue(BacnetObjectTypes type, BacnetPropertyIds prop, string text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Equals("null", StringComparison.OrdinalIgnoreCase)) return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
        if (prop == BacnetPropertyIds.PROP_OUT_OF_SERVICE)
            return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, (bool)ValueConverter.Parse(t, TagDataType.Bool));
        if (prop is not (BacnetPropertyIds.PROP_PRESENT_VALUE or BacnetPropertyIds.PROP_RELINQUISH_DEFAULT))
            return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, t);
        return BacnetAddressSpec.KindOf(type) switch
        {
            BacnetValueKind.Analog => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, (float)(double)ValueConverter.Parse(t, TagDataType.Float64)),
            BacnetValueKind.Binary => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, (bool)ValueConverter.Parse(t, TagDataType.Bool) ? 1u : 0u),
            BacnetValueKind.MultiState => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)ValueConverter.Parse(t, TagDataType.UInt32)),
            _ => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, t),
        };
    }
}
