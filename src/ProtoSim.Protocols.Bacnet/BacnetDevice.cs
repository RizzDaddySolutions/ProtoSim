using System.Collections.Concurrent;
using System.Globalization;
using System.IO.BACnet;
using System.Net;
using System.Net.Sockets;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using Tags = System.IO.BACnet.BacnetApplicationTags;
using Props = System.IO.BACnet.BacnetPropertyIds;
using Services = System.IO.BACnet.BacnetConfirmedServices;

namespace ProtoSim.Protocols.Bacnet;

/// <summary>
/// A simulated BACnet/IP device: answers Who-Is/Who-Has, ReadProperty(Multiple), WriteProperty(Multiple) with a
/// 16-level priority array, SubscribeCOV/SubscribeCOVProperty, DeviceCommunicationControl, ReinitializeDevice and TimeSynchronization.
/// Built on the BACnet stack from the "BACnet" NuGet package (MIT).
/// </summary>
public sealed class BacnetDevice : IProtocolServer
{
    private const uint ArrayAll = uint.MaxValue;
    private const uint WildcardDevice = 4_194_303;
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromMinutes(10);

    private readonly BacnetSettings _settings;
    private readonly BacnetDataModel _model;
    private readonly ServerContext _ctx;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, ConnectionInfo> _peers = new(StringComparer.Ordinal);
    private readonly List<CovSubscription> _subscriptions = [];
    private long _nextPeerId;

    private BacnetClient? _client;
    private BacnetIpUdpProtocolTransport? _transport;
    private Thread? _covThread;
    private volatile bool _running;
    private volatile CommunicationState _communication = CommunicationState.Enabled;
    private DateTime _communicationUntilUtc = DateTime.MaxValue;
    private TimeSpan _clockOffset;

    private enum CommunicationState { Enabled, Disabled, DisableInitiation }

    private sealed class CovSubscription
    {
        public required BacnetAddress Address { get; init; }
        public required string Peer { get; init; }
        public required uint ProcessId { get; init; }
        public required BacnetObjectId ObjectId { get; init; }
        public required bool Confirmed { get; init; }
        public required uint Lifetime { get; init; }
        public required DateTime ExpiresUtc { get; init; }
        public Props? Property { get; init; }
        public float? Increment { get; init; }
        public object? LastValue { get; set; }
        public bool? LastOutOfService { get; set; }
    }

    public BacnetDevice(BacnetSettings settings, BacnetDataModel model, ServerContext context)
    {
        _settings = settings;
        _model = model;
        _ctx = context;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>The bound UDP endpoint once started.</summary>
    public IPEndPoint? LocalEndPoint => _transport?.LocalEndPoint;

    private ITrafficLog Log => _ctx.Log;
    private uint Instance => (uint)_settings.DeviceInstance;
    private BacnetObjectId DeviceId => new(BacnetObjectTypes.OBJECT_DEVICE, Instance);

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
                var ip = string.IsNullOrWhiteSpace(_settings.LocalAddress) ? null : _settings.LocalAddress.Trim();
                _transport = new BacnetIpUdpProtocolTransport(_settings.Port, false, false, 1472, ip);
                _transport.MessageRecieved += OnMessage;
                _client = new BacnetClient(_transport, 1000, 3) { VendorId = (ushort)_settings.VendorIdentifier };
                Hook(_client);
                _client.Start();

                _communication = CommunicationState.Enabled;
                _running = true;
                _covThread = new Thread(CovLoop) { IsBackground = true, Name = "BACnet COV" };
                _covThread.Start();

                Log.Info("", $"BACnet/IP device {Instance} listening on {_transport.LocalEndPoint}");
                if (_settings.AnnounceOnStart) SendIAm(null);
                SetState(ServerState.Running);
            }
            catch (Exception ex)
            {
                _running = false;
                DisposeClient();
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                    ? $"UDP port {_settings.Port} is already in use (another BACnet program?)."
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
            if (_client == null) return;
            SetState(ServerState.Stopping);
            _running = false;
            var thread = _covThread;
            if (thread != null) await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5)));
            _covThread = null;
            DisposeClient();
            lock (_subscriptions) _subscriptions.Clear();
            _peers.Clear();
            Log.Info("", "Device stopped");
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

    /// <summary>Forgets a client and cancels its COV subscriptions (UDP has no connection to close).</summary>
    public void DropConnection(long connectionId)
    {
        var entry = _peers.FirstOrDefault(p => p.Value.Id == connectionId);
        if (entry.Key == null) return;
        _peers.TryRemove(entry.Key, out _);
        int removed;
        lock (_subscriptions) removed = _subscriptions.RemoveAll(s => s.Peer == entry.Key);
        Log.Warn(entry.Key, $"Client forgotten by user; {removed} COV subscription(s) cancelled");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private void DisposeClient()
    {
        if (_transport != null) _transport.MessageRecieved -= OnMessage;
        try { _client?.Dispose(); } catch (Exception) { }
        try { _transport?.Dispose(); } catch (Exception) { }
        _client = null;
        _transport = null;
    }

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Hook(BacnetClient c)
    {
        c.OnWhoIs += OnWhoIs;
        c.OnWhoHas += OnWhoHas;
        c.OnReadPropertyRequest += OnReadProperty;
        c.OnReadPropertyMultipleRequest += OnReadPropertyMultiple;
        c.OnWritePropertyRequest += OnWriteProperty;
        c.OnWritePropertyMultipleRequest += OnWritePropertyMultiple;
        c.OnSubscribeCOV += OnSubscribeCov;
        c.OnSubscribeCOVProperty += OnSubscribeCovProperty;
        c.OnDeviceCommunicationControl += OnDeviceCommunicationControl;
        c.OnReinitializedDevice += OnReinitialize;
        c.OnTimeSynchronize += OnTimeSynchronize;
    }

    private void OnMessage(IBacnetTransport sender, byte[] buffer, int offset, int msgLength, BacnetAddress remoteAddress)
    {
        _ctx.Statistics.OnRequest(msgLength);
    }

    // ---- Request plumbing ----

    private string Touch(BacnetAddress adr)
    {
        string key = adr.ToString();
        var peer = _peers.GetOrAdd(key, k => new ConnectionInfo(Interlocked.Increment(ref _nextPeerId), k));
        peer.OnRequest();
        return key;
    }

    private bool CommunicationDisabled
    {
        get
        {
            if (_communication != CommunicationState.Enabled && DateTime.UtcNow >= _communicationUntilUtc)
            {
                _communication = CommunicationState.Enabled;
                Log.Info("", "DeviceCommunicationControl period ended; communication enabled");
            }
            return _communication == CommunicationState.Disabled;
        }
    }

    /// <summary>Logs a confirmed request and applies communication control and fault injection. False = don't answer normally.</summary>
    private bool Begin(BacnetClient c, BacnetAddress adr, byte invokeId, Services service, Func<string> describe, bool exemptFromDcc = false)
    {
        string peer = Touch(adr);
        Log.Frame(TrafficDirection.Rx, peer, [], describe);

        if (!exemptFromDcc && CommunicationDisabled)
        {
            _ctx.Statistics.OnDropped();
            Log.Info(peer, "Communication disabled by DeviceCommunicationControl; request ignored");
            return false;
        }

        var faults = _ctx.Faults;
        if (FaultSettings.Roll(faults.DropResponsePercent))
        {
            _ctx.Statistics.OnDropped();
            Log.Warn(peer, "Fault injection: no response");
            return false;
        }
        int delay = Math.Max(0, faults.ResponseDelayMs) + (faults.DelayJitterMs > 0 ? Random.Shared.Next(0, faults.DelayJitterMs + 1) : 0);
        if (delay > 0) Thread.Sleep(delay);
        if (FaultSettings.Roll(faults.ErrorResponsePercent))
        {
            Log.Warn(peer, $"Fault injection: error device/{faults.ErrorCode}");
            Error(c, adr, service, invokeId, BacnetErrorClasses.ERROR_CLASS_DEVICE, (BacnetErrorCodes)faults.ErrorCode);
            return false;
        }
        return true;
    }

    private void Error(BacnetClient c, BacnetAddress adr, Services service, byte invokeId, BacnetErrorClasses cls, BacnetErrorCodes code)
    {
        c.ErrorResponse(adr, service, invokeId, cls, code);
        _ctx.Statistics.OnResponse(0, true);
        Log.Frame(TrafficDirection.Tx, adr.ToString(), [], () => $"Error {Name(service)}: {Name(cls)} / {Name(code)}");
    }

    private void Ack(BacnetClient c, BacnetAddress adr, Services service, byte invokeId)
    {
        c.SimpleAckResponse(adr, service, invokeId);
        _ctx.Statistics.OnResponse(0, false);
        Log.Frame(TrafficDirection.Tx, adr.ToString(), [], () => $"SimpleAck {Name(service)}");
    }

    private bool PasswordOk(string? password) =>
        string.IsNullOrEmpty(_settings.Password) || string.Equals(password ?? string.Empty, _settings.Password, StringComparison.Ordinal);

    private bool InRange(int low, int high) => low < 0 || high < 0 || (Instance >= (uint)low && Instance <= (uint)high);

    private bool IsThisDevice(BacnetObjectId id) =>
        id.type == BacnetObjectTypes.OBJECT_DEVICE && (id.instance == Instance || id.instance == WildcardDevice);

    // ---- Discovery ----

    private void SendIAm(BacnetAddress? to)
    {
        var c = _client;
        if (c == null || _communication != CommunicationState.Enabled) return;
        c.Iam(Instance, BacnetSegmentations.SEGMENTATION_BOTH);
        if (to != null && _settings.UnicastIAm) c.Iam(Instance, BacnetSegmentations.SEGMENTATION_BOTH, to);
        Log.Frame(TrafficDirection.Tx, to?.ToString() ?? "broadcast", [], () => $"I-Am device {Instance}");
    }

    private void OnWhoIs(BacnetClient sender, BacnetAddress adr, int lowLimit, int highLimit)
    {
        string peer = Touch(adr);
        Log.Frame(TrafficDirection.Rx, peer, [], () => lowLimit < 0 ? "Who-Is (all)" : $"Who-Is {lowLimit}-{highLimit}");
        if (!InRange(lowLimit, highLimit) || CommunicationDisabled) return;
        SendIAm(adr);
    }

    private void OnWhoHas(BacnetClient sender, BacnetAddress adr, int lowLimit, int highLimit, BacnetObjectId? objId, string objName)
    {
        string peer = Touch(adr);
        Log.Frame(TrafficDirection.Rx, peer, [], () => $"Who-Has {(objId is { } id ? Key(id) : $"'{objName}'")}");
        if (!InRange(lowLimit, highLimit) || CommunicationDisabled || _communication != CommunicationState.Enabled) return;

        (BacnetObjectId Id, string Name)? found = null;
        if (objId is { } wanted)
        {
            if (IsThisDevice(wanted)) found = (DeviceId, _settings.DeviceName);
            else if (_model.Find(wanted) is { } o) found = (o.Id, o.Name);
        }
        else if (!string.IsNullOrEmpty(objName))
        {
            if (objName == _settings.DeviceName) found = (DeviceId, _settings.DeviceName);
            else if (_model.FindByName(objName) is { } o) found = (o.Id, o.Name);
        }
        if (found is not { } f) return;
        sender.IHave(DeviceId, f.Id, f.Name);
        Log.Frame(TrafficDirection.Tx, "broadcast", [], () => $"I-Have {Key(f.Id)} '{f.Name}'");
    }

    // ---- ReadProperty / ReadPropertyMultiple ----

    private void OnReadProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetPropertyReference property, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_READ_PROPERTY, () => $"ReadProperty {Key(objectId)} {Name(property)}")) return;

        var prop = (Props)property.propertyIdentifier;
        if (prop is Props.PROP_ALL or Props.PROP_REQUIRED or Props.PROP_OPTIONAL)
        {
            Error(sender, adr, Services.SERVICE_CONFIRMED_READ_PROPERTY, invokeId, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
            return;
        }

        var (values, error) = Read(objectId, property);
        if (error is { } e)
        {
            Error(sender, adr, Services.SERVICE_CONFIRMED_READ_PROPERTY, invokeId, e.error_class, e.error_code);
            return;
        }
        if (IsThisDevice(objectId)) objectId = DeviceId;
        sender.ReadPropertyResponse(adr, invokeId, sender.GetSegmentBuffer(maxSegments), objectId, property, values!);
        _ctx.Statistics.OnResponse(0, false);
        Log.Frame(TrafficDirection.Tx, adr.ToString(), [], () => $"ReadProperty-ACK {Key(objectId)} {Name(property)} = {Format(values!)}");
    }

    private void OnReadPropertyMultiple(BacnetClient sender, BacnetAddress adr, byte invokeId, IList<BacnetReadAccessSpecification> properties, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_READ_PROP_MULTIPLE,
            () => "ReadPropertyMultiple " + string.Join(", ", properties.Select(p => $"{Key(p.objectIdentifier)} [{string.Join(" ", p.propertyReferences.Select(Name))}]")))) return;

        var results = new List<BacnetReadAccessResult>();
        foreach (var spec in properties)
        {
            var objectId = IsThisDevice(spec.objectIdentifier) ? DeviceId : spec.objectIdentifier;
            var values = new List<BacnetPropertyValue>();
            foreach (var reference in spec.propertyReferences)
            {
                var prop = (Props)reference.propertyIdentifier;
                if (prop is Props.PROP_ALL or Props.PROP_REQUIRED or Props.PROP_OPTIONAL)
                {
                    var list = PropertyList(objectId, includeIdentity: true);
                    if (list == null)
                    {
                        values.Add(ErrorValue(reference, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT));
                        continue;
                    }
                    foreach (var p in list)
                    {
                        var r = new BacnetPropertyReference((uint)p, ArrayAll);
                        var (v, _) = Read(objectId, r);
                        if (v != null) values.Add(new BacnetPropertyValue { property = r, value = v });
                    }
                    continue;
                }

                var (vals, error) = Read(objectId, reference);
                values.Add(error is { } e
                    ? ErrorValue(reference, e.error_class, e.error_code)
                    : new BacnetPropertyValue { property = reference, value = vals! });
            }
            results.Add(new BacnetReadAccessResult(objectId, values));
        }

        sender.ReadPropertyMultipleResponse(adr, invokeId, sender.GetSegmentBuffer(maxSegments), results);
        _ctx.Statistics.OnResponse(0, false);
        Log.Frame(TrafficDirection.Tx, adr.ToString(), [], () => $"ReadPropertyMultiple-ACK {results.Sum(r => r.values.Count)} values");
    }

    private static BacnetPropertyValue ErrorValue(BacnetPropertyReference reference, BacnetErrorClasses cls, BacnetErrorCodes code) => new()
    {
        property = reference,
        value = [new BacnetValue(Tags.BACNET_APPLICATION_TAG_ERROR, new BacnetError(cls, code))],
    };

    /// <summary>Reads one property (with optional array index).</summary>
    private (IList<BacnetValue>? Values, BacnetError? Error) Read(BacnetObjectId objectId, BacnetPropertyReference reference)
    {
        var prop = (Props)reference.propertyIdentifier;
        IList<BacnetValue>? values;
        bool isArray;

        if (IsThisDevice(objectId))
        {
            values = DeviceProperty(prop, out isArray);
        }
        else
        {
            var obj = _model.Find(objectId);
            if (obj == null) return (null, new BacnetError(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT));
            values = ObjectProperty(obj, prop, out isArray);
        }
        if (values == null) return (null, new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY));

        uint index = reference.propertyArrayIndex;
        if (index == ArrayAll) return (values, null);
        if (!isArray) return (null, new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_PROPERTY_IS_NOT_AN_ARRAY));
        if (index == 0) return ([new BacnetValue(Tags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)values.Count)], null);
        if (index > values.Count) return (null, new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_ARRAY_INDEX));
        return ([values[(int)index - 1]], null);
    }

    private static readonly Props[] DeviceProperties =
    [
        Props.PROP_OBJECT_IDENTIFIER, Props.PROP_OBJECT_NAME, Props.PROP_OBJECT_TYPE, Props.PROP_SYSTEM_STATUS,
        Props.PROP_VENDOR_NAME, Props.PROP_VENDOR_IDENTIFIER, Props.PROP_MODEL_NAME, Props.PROP_FIRMWARE_REVISION,
        Props.PROP_APPLICATION_SOFTWARE_VERSION, Props.PROP_LOCATION, Props.PROP_DESCRIPTION, Props.PROP_PROTOCOL_VERSION,
        Props.PROP_PROTOCOL_REVISION, Props.PROP_PROTOCOL_SERVICES_SUPPORTED, Props.PROP_PROTOCOL_OBJECT_TYPES_SUPPORTED,
        Props.PROP_OBJECT_LIST, Props.PROP_MAX_APDU_LENGTH_ACCEPTED, Props.PROP_SEGMENTATION_SUPPORTED, Props.PROP_MAX_SEGMENTS_ACCEPTED,
        Props.PROP_APDU_TIMEOUT, Props.PROP_NUMBER_OF_APDU_RETRIES, Props.PROP_DEVICE_ADDRESS_BINDING, Props.PROP_DATABASE_REVISION,
        Props.PROP_LOCAL_DATE, Props.PROP_LOCAL_TIME, Props.PROP_UTC_OFFSET, Props.PROP_DAYLIGHT_SAVINGS_STATUS, Props.PROP_PROPERTY_LIST,
    ];

    private static readonly Props[] IdentityProperties = [Props.PROP_OBJECT_IDENTIFIER, Props.PROP_OBJECT_NAME, Props.PROP_OBJECT_TYPE, Props.PROP_PROPERTY_LIST];

    private static Props[] ObjectProperties(BacnetObjectInfo o)
    {
        var list = new List<Props>
        {
            Props.PROP_OBJECT_IDENTIFIER, Props.PROP_OBJECT_NAME, Props.PROP_OBJECT_TYPE, Props.PROP_PRESENT_VALUE,
            Props.PROP_STATUS_FLAGS, Props.PROP_EVENT_STATE, Props.PROP_RELIABILITY, Props.PROP_OUT_OF_SERVICE, Props.PROP_DESCRIPTION,
        };
        switch (o.Kind)
        {
            case BacnetValueKind.Analog:
                list.AddRange([Props.PROP_UNITS, Props.PROP_COV_INCREMENT]);
                break;
            case BacnetValueKind.Binary:
                if (o.Id.type != BacnetObjectTypes.OBJECT_BINARY_VALUE) list.Add(Props.PROP_POLARITY);
                list.AddRange([Props.PROP_ACTIVE_TEXT, Props.PROP_INACTIVE_TEXT]);
                break;
            case BacnetValueKind.MultiState:
                list.Add(Props.PROP_NUMBER_OF_STATES);
                break;
        }
        if (o.IsCommandable) list.AddRange([Props.PROP_PRIORITY_ARRAY, Props.PROP_RELINQUISH_DEFAULT]);
        list.Add(Props.PROP_PROPERTY_LIST);
        return [.. list];
    }

    /// <summary>Properties of an object, or null when it doesn't exist.</summary>
    private Props[]? PropertyList(BacnetObjectId id, bool includeIdentity)
    {
        Props[]? all = IsThisDevice(id) ? DeviceProperties : _model.Find(id) is { } o ? ObjectProperties(o) : null;
        return all == null ? null : includeIdentity ? all : all.Where(p => !IdentityProperties.Contains(p)).ToArray();
    }

    private static BacnetValue V(Tags tag, object? value) => new(tag, value);
    private static IList<BacnetValue> One(Tags tag, object? value) => [V(tag, value)];
    private static IList<BacnetValue> Text(string? s) => One(Tags.BACNET_APPLICATION_TAG_CHARACTER_STRING, s ?? string.Empty);
    private static IList<BacnetValue> Unsigned(uint v) => One(Tags.BACNET_APPLICATION_TAG_UNSIGNED_INT, v);
    private static IList<BacnetValue> Enumerated(uint v) => One(Tags.BACNET_APPLICATION_TAG_ENUMERATED, v);

    private static IList<BacnetValue> PropertyIdList(IEnumerable<Props> props) =>
        props.Where(p => !IdentityProperties.Contains(p)).Select(p => V(Tags.BACNET_APPLICATION_TAG_ENUMERATED, (uint)p)).ToList();

    private static BacnetBitString Bits(int length, params int[] set)
    {
        var b = new BacnetBitString { bits_used = (byte)length, value = new byte[(length + 7) / 8] };
        foreach (var i in set) b.SetBit((byte)i, true);
        b.bits_used = (byte)length;
        return b;
    }

    private IList<BacnetValue>? DeviceProperty(Props prop, out bool isArray)
    {
        isArray = prop is Props.PROP_OBJECT_LIST or Props.PROP_PROPERTY_LIST;
        var now = DateTime.Now + _clockOffset;
        return prop switch
        {
            Props.PROP_OBJECT_IDENTIFIER => One(Tags.BACNET_APPLICATION_TAG_OBJECT_ID, DeviceId),
            Props.PROP_OBJECT_NAME => Text(_settings.DeviceName),
            Props.PROP_OBJECT_TYPE => Enumerated((uint)BacnetObjectTypes.OBJECT_DEVICE),
            Props.PROP_SYSTEM_STATUS => Enumerated((uint)BacnetDeviceStatus.OPERATIONAL),
            Props.PROP_VENDOR_NAME => Text(_settings.VendorName),
            Props.PROP_VENDOR_IDENTIFIER => Unsigned((uint)_settings.VendorIdentifier),
            Props.PROP_MODEL_NAME => Text(_settings.ModelName),
            Props.PROP_FIRMWARE_REVISION => Text(_settings.FirmwareRevision),
            Props.PROP_APPLICATION_SOFTWARE_VERSION => Text(_settings.ApplicationSoftwareVersion),
            Props.PROP_LOCATION => Text(_settings.Location),
            Props.PROP_DESCRIPTION => Text(_settings.Description),
            Props.PROP_PROTOCOL_VERSION => Unsigned(1),
            Props.PROP_PROTOCOL_REVISION => Unsigned(14),
            Props.PROP_PROTOCOL_SERVICES_SUPPORTED => One(Tags.BACNET_APPLICATION_TAG_BIT_STRING, Bits(40,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_SUBSCRIBE_COV,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_READ_PROPERTY,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_READ_PROP_MULTIPLE,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_WRITE_PROPERTY,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_WRITE_PROP_MULTIPLE,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_DEVICE_COMMUNICATION_CONTROL,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_REINITIALIZE_DEVICE,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_I_AM,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_I_HAVE,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_TIME_SYNCHRONIZATION,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_WHO_HAS,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_WHO_IS,
                (int)BacnetServicesSupported.SERVICE_SUPPORTED_SUBSCRIBE_COV_PROPERTY)),
            Props.PROP_PROTOCOL_OBJECT_TYPES_SUPPORTED => One(Tags.BACNET_APPLICATION_TAG_BIT_STRING, Bits(60,
                (int)BacnetObjectTypes.OBJECT_ANALOG_INPUT, (int)BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, (int)BacnetObjectTypes.OBJECT_ANALOG_VALUE,
                (int)BacnetObjectTypes.OBJECT_BINARY_INPUT, (int)BacnetObjectTypes.OBJECT_BINARY_OUTPUT, (int)BacnetObjectTypes.OBJECT_BINARY_VALUE,
                (int)BacnetObjectTypes.OBJECT_DEVICE, (int)BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT, (int)BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT,
                (int)BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, (int)BacnetObjectTypes.OBJECT_CHARACTERSTRING_VALUE)),
            Props.PROP_OBJECT_LIST => new[] { DeviceId }.Concat(_model.Objects().Select(o => o.Id))
                .Select(id => V(Tags.BACNET_APPLICATION_TAG_OBJECT_ID, id)).ToList(),
            Props.PROP_MAX_APDU_LENGTH_ACCEPTED => Unsigned(1476),
            Props.PROP_SEGMENTATION_SUPPORTED => Enumerated((uint)BacnetSegmentations.SEGMENTATION_BOTH),
            Props.PROP_MAX_SEGMENTS_ACCEPTED => Unsigned(16),
            Props.PROP_APDU_TIMEOUT => Unsigned(3000),
            Props.PROP_NUMBER_OF_APDU_RETRIES => Unsigned(3),
            Props.PROP_DEVICE_ADDRESS_BINDING => [],
            Props.PROP_DATABASE_REVISION => Unsigned(_model.DatabaseRevision),
            Props.PROP_LOCAL_DATE => One(Tags.BACNET_APPLICATION_TAG_DATE, now.Date),
            Props.PROP_LOCAL_TIME => One(Tags.BACNET_APPLICATION_TAG_TIME, now),
            Props.PROP_UTC_OFFSET => One(Tags.BACNET_APPLICATION_TAG_SIGNED_INT, -(int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes),
            Props.PROP_DAYLIGHT_SAVINGS_STATUS => One(Tags.BACNET_APPLICATION_TAG_BOOLEAN, TimeZoneInfo.Local.IsDaylightSavingTime(now)),
            Props.PROP_PROPERTY_LIST => PropertyIdList(DeviceProperties),
            _ => null,
        };
    }

    private IList<BacnetValue>? ObjectProperty(BacnetObjectInfo o, Props prop, out bool isArray)
    {
        isArray = prop is Props.PROP_PRIORITY_ARRAY or Props.PROP_PROPERTY_LIST;
        if (!ObjectProperties(o).Contains(prop)) return null;
        return prop switch
        {
            Props.PROP_OBJECT_IDENTIFIER => One(Tags.BACNET_APPLICATION_TAG_OBJECT_ID, o.Id),
            Props.PROP_OBJECT_NAME => Text(o.Name),
            Props.PROP_OBJECT_TYPE => Enumerated((uint)o.Id.type),
            Props.PROP_PRESENT_VALUE => [Encode(o, o.Tag.Value)],
            Props.PROP_STATUS_FLAGS => One(Tags.BACNET_APPLICATION_TAG_BIT_STRING, StatusFlags(o)),
            Props.PROP_EVENT_STATE => Enumerated((uint)BacnetEventStates.EVENT_STATE_NORMAL),
            Props.PROP_RELIABILITY => Enumerated(0),
            Props.PROP_OUT_OF_SERVICE => One(Tags.BACNET_APPLICATION_TAG_BOOLEAN, o.State.OutOfService),
            Props.PROP_DESCRIPTION => Text(string.Empty),
            Props.PROP_UNITS => Enumerated((uint)_settings.AnalogUnits),
            Props.PROP_COV_INCREMENT => One(Tags.BACNET_APPLICATION_TAG_REAL, (float)_settings.CovIncrement),
            Props.PROP_POLARITY => Enumerated((uint)BacnetPolarity.POLARITY_NORMAL),
            Props.PROP_ACTIVE_TEXT => Text("Active"),
            Props.PROP_INACTIVE_TEXT => Text("Inactive"),
            Props.PROP_NUMBER_OF_STATES => Unsigned((uint)Math.Max(1, _settings.MultiStateCount)),
            Props.PROP_PRIORITY_ARRAY => o.State.Priority.Select(p => p == null ? V(Tags.BACNET_APPLICATION_TAG_NULL, null) : Encode(o, p)).ToList(),
            Props.PROP_RELINQUISH_DEFAULT => [Encode(o, o.State.RelinquishDefault ?? o.Tag.Value)],
            Props.PROP_PROPERTY_LIST => PropertyIdList(ObjectProperties(o)),
            _ => null,
        };
    }

    private static BacnetBitString StatusFlags(BacnetObjectInfo o) =>
        o.State.OutOfService ? Bits(4, 3) : Bits(4);

    private BacnetValue Encode(BacnetObjectInfo o, object value) => o.Kind switch
    {
        BacnetValueKind.Analog => V(Tags.BACNET_APPLICATION_TAG_REAL, Convert.ToSingle(value, CultureInfo.InvariantCulture)),
        BacnetValueKind.Binary => V(Tags.BACNET_APPLICATION_TAG_ENUMERATED, value is true ? 1u : 0u),
        BacnetValueKind.MultiState => V(Tags.BACNET_APPLICATION_TAG_UNSIGNED_INT,
            (uint)Math.Clamp(ValueConverter.ToDouble(value), 1, Math.Max(1, _settings.MultiStateCount))),
        _ => V(Tags.BACNET_APPLICATION_TAG_CHARACTER_STRING, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    // ---- WriteProperty / WritePropertyMultiple ----

    private void OnWriteProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetPropertyValue value, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_WRITE_PROPERTY,
            () => $"WriteProperty {Key(objectId)} {Name(value.property)} = {Format(value.value)}{(value.priority is > 0 and < 16 ? $" @{value.priority}" : "")}")) return;

        if (Write(objectId, value) is { } e) Error(sender, adr, Services.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId, e.error_class, e.error_code);
        else Ack(sender, adr, Services.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId);
    }

    private void OnWritePropertyMultiple(BacnetClient sender, BacnetAddress adr, byte invokeId, ICollection<BacnetWriteAccessSpecification> properties, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_WRITE_PROP_MULTIPLE,
            () => "WritePropertyMultiple " + string.Join(", ", properties.Select(p => $"{Key(p.objectIdentifier)} [{string.Join(" ", p.propertyValues.Select(v => Name(v.property)))}]")))) return;

        foreach (var spec in properties)
        {
            foreach (var value in spec.propertyValues)
            {
                if (Write(spec.objectIdentifier, value) is { } e)
                {
                    Error(sender, adr, Services.SERVICE_CONFIRMED_WRITE_PROP_MULTIPLE, invokeId, e.error_class, e.error_code);
                    return;
                }
            }
        }
        Ack(sender, adr, Services.SERVICE_CONFIRMED_WRITE_PROP_MULTIPLE, invokeId);
    }

    /// <summary>Applies one write; returns the error to report, or null on success.</summary>
    private BacnetError? Write(BacnetObjectId objectId, BacnetPropertyValue value)
    {
        static BacnetError Err(BacnetErrorClasses c, BacnetErrorCodes e) => new(c, e);
        var prop = (Props)value.property.propertyIdentifier;

        if (IsThisDevice(objectId))
            return DeviceProperties.Contains(prop)
                ? Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED)
                : Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);

        var o = _model.Find(objectId);
        if (o == null) return Err(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
        if (!ObjectProperties(o).Contains(prop)) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
        if (value.value == null || value.value.Count == 0) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
        var v = value.value[0];

        // The tag's Writeable box guards the value; Out_Of_Service stays writable so clients can still take the point over.
        if (prop is Props.PROP_PRESENT_VALUE or Props.PROP_RELINQUISH_DEFAULT && !o.Tag.ClientWritable)
            return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);

        switch (prop)
        {
            case Props.PROP_OUT_OF_SERVICE:
                if (v.Value is not bool oos) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
                o.State.OutOfService = oos;
                return null;

            case Props.PROP_RELINQUISH_DEFAULT when o.IsCommandable:
            {
                var (decoded, error) = Decode(o, v);
                if (error != null) return error;
                lock (o.State)
                {
                    o.State.RelinquishDefault = decoded;
                    if (!o.State.IsCommanded) o.Tag.Write(decoded!);
                }
                return null;
            }

            case Props.PROP_PRESENT_VALUE:
            {
                if (o.IsInput && !o.State.OutOfService) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);
                int priority = value.priority == 0 ? 16 : value.priority;
                if (priority is < 1 or > 16) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);

                bool relinquish = v.Tag == Tags.BACNET_APPLICATION_TAG_NULL;
                if (!o.IsCommandable)
                {
                    if (relinquish) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
                    var (direct, err) = Decode(o, v);
                    if (err != null) return err;
                    o.Tag.Write(direct!);
                    return null;
                }
                if (priority == 6) return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED); // reserved for minimum on/off

                object? command = null;
                if (!relinquish)
                {
                    var (decoded, error) = Decode(o, v);
                    if (error != null) return error;
                    command = decoded;
                }
                lock (o.State)
                {
                    if (!o.State.IsCommanded && o.State.RelinquishDefault == null) o.State.RelinquishDefault = o.Tag.Value;
                    o.State.Priority[priority - 1] = command;
                    if (o.State.Effective is { } effective) o.Tag.Write(effective);
                }
                return null;
            }

            default:
                return Err(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);
        }
    }

    /// <summary>Converts a written BACnet value to the tag's CLR type.</summary>
    private (object? Value, BacnetError? Error) Decode(BacnetObjectInfo o, BacnetValue v)
    {
        static (object?, BacnetError?) Bad(BacnetErrorCodes code) => (null, new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, code));
        object? raw = v.Value;
        try
        {
            switch (o.Kind)
            {
                case BacnetValueKind.Analog:
                    if (raw is not (float or double or uint or int or ulong or long)) return Bad(BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
                    return (ValueConverter.Coerce(Convert.ToDouble(raw, CultureInfo.InvariantCulture), o.Tag.DataType), null);

                case BacnetValueKind.Binary:
                    if (raw is bool b) return (b, null);
                    if (raw is not (uint or int)) return Bad(BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
                    long bit = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                    return bit is 0 or 1 ? (bit == 1, null) : Bad(BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);

                case BacnetValueKind.MultiState:
                    if (raw is not (uint or int or ulong or long)) return Bad(BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
                    long state = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                    if (state < 1 || state > Math.Max(1, _settings.MultiStateCount)) return Bad(BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);
                    return (ValueConverter.Coerce(state, o.Tag.DataType), null);

                default:
                    return raw is string s ? (s, null) : Bad(BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            return Bad(BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE);
        }
    }

    // ---- COV ----

    private void OnSubscribeCov(BacnetClient sender, BacnetAddress adr, byte invokeId, uint subscriberProcessIdentifier, BacnetObjectId monitoredObjectIdentifier,
        bool cancellationRequest, bool issueConfirmedNotifications, uint lifetime, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_SUBSCRIBE_COV,
            () => $"SubscribeCOV {Key(monitoredObjectIdentifier)} process {subscriberProcessIdentifier}" +
                  (cancellationRequest ? " cancel" : $" {(issueConfirmedNotifications ? "confirmed" : "unconfirmed")}, {(lifetime == 0 ? "indefinite" : lifetime + " s")}"))) return;
        Subscribe(sender, adr, invokeId, Services.SERVICE_CONFIRMED_SUBSCRIBE_COV, subscriberProcessIdentifier, monitoredObjectIdentifier,
            cancellationRequest, issueConfirmedNotifications, lifetime, null, null);
    }

    private void OnSubscribeCovProperty(BacnetClient sender, BacnetAddress adr, byte invokeId, uint subscriberProcessIdentifier, BacnetObjectId monitoredObjectIdentifier,
        BacnetPropertyReference monitoredProperty, bool cancellationRequest, bool issueConfirmedNotifications, uint lifetime, float covIncrement, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_SUBSCRIBE_COV_PROPERTY,
            () => $"SubscribeCOVProperty {Key(monitoredObjectIdentifier)} {Name(monitoredProperty)} process {subscriberProcessIdentifier}{(cancellationRequest ? " cancel" : "")}")) return;

        var prop = (Props)monitoredProperty.propertyIdentifier;
        if (prop is not (Props.PROP_PRESENT_VALUE or Props.PROP_STATUS_FLAGS))
        {
            Error(sender, adr, Services.SERVICE_CONFIRMED_SUBSCRIBE_COV_PROPERTY, invokeId, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_NOT_COV_PROPERTY);
            return;
        }
        Subscribe(sender, adr, invokeId, Services.SERVICE_CONFIRMED_SUBSCRIBE_COV_PROPERTY, subscriberProcessIdentifier, monitoredObjectIdentifier,
            cancellationRequest, issueConfirmedNotifications, lifetime, prop, covIncrement > 0 ? covIncrement : null);
    }

    private void Subscribe(BacnetClient sender, BacnetAddress adr, byte invokeId, Services service, uint processId, BacnetObjectId objectId,
        bool cancel, bool confirmed, uint lifetime, Props? property, float? increment)
    {
        if (_model.Find(objectId) == null)
        {
            Error(sender, adr, service, invokeId, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
            return;
        }

        string peer = adr.ToString();
        lock (_subscriptions)
        {
            _subscriptions.RemoveAll(s => s.Peer == peer && s.ProcessId == processId && s.ObjectId.Equals(objectId) && s.Property == property);
            if (!cancel)
            {
                _subscriptions.Add(new CovSubscription
                {
                    Address = adr,
                    Peer = peer,
                    ProcessId = processId,
                    ObjectId = objectId,
                    Confirmed = confirmed,
                    Lifetime = lifetime,
                    ExpiresUtc = lifetime == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(lifetime),
                    Property = property,
                    Increment = increment,
                });
            }
        }
        Ack(sender, adr, service, invokeId);
    }

    private void CovLoop()
    {
        while (_running)
        {
            Thread.Sleep(100);
            var client = _client;
            if (client == null || _communication != CommunicationState.Enabled) continue;

            List<CovSubscription> snapshot;
            lock (_subscriptions)
            {
                _subscriptions.RemoveAll(s => s.ExpiresUtc <= DateTime.UtcNow);
                snapshot = [.. _subscriptions];
            }

            foreach (var sub in snapshot)
            {
                if (!_running) return;
                var o = _model.Find(sub.ObjectId);
                if (o == null) continue;
                var value = o.Tag.Value;
                bool oos = o.State.OutOfService;
                if (!Changed(o, sub, value, oos)) continue;

                var values = new List<BacnetPropertyValue>();
                if (sub.Property is null or Props.PROP_PRESENT_VALUE)
                    values.Add(new BacnetPropertyValue { property = new BacnetPropertyReference(Props.PROP_PRESENT_VALUE), value = [Encode(o, value)] });
                if (sub.Property is null or Props.PROP_STATUS_FLAGS)
                    values.Add(new BacnetPropertyValue { property = new BacnetPropertyReference(Props.PROP_STATUS_FLAGS), value = [V(Tags.BACNET_APPLICATION_TAG_BIT_STRING, StatusFlags(o))] });

                uint remaining = sub.Lifetime == 0 ? 0 : (uint)Math.Max(0, (sub.ExpiresUtc - DateTime.UtcNow).TotalSeconds);
                try
                {
                    client.Notify(sub.Address, sub.ProcessId, Instance, sub.ObjectId, remaining, sub.Confirmed, values);
                    sub.LastValue = value;
                    sub.LastOutOfService = oos;
                    _ctx.Statistics.OnResponse(0, false);
                    Log.Frame(TrafficDirection.Tx, sub.Peer, [], () =>
                        $"{(sub.Confirmed ? "Confirmed" : "Unconfirmed")}COVNotification {Key(sub.ObjectId)} = {Format(values[0].value)}");
                }
                catch (Exception ex)
                {
                    Log.Warn(sub.Peer, $"COV notification failed: {ex.Message}");
                }
            }
        }
    }

    private bool Changed(BacnetObjectInfo o, CovSubscription sub, object value, bool oos)
    {
        if (sub.LastValue == null || sub.LastOutOfService != oos) return true;
        if (sub.Property == Props.PROP_STATUS_FLAGS) return false;
        if (o.Kind != BacnetValueKind.Analog) return !Equals(sub.LastValue, value);
        double increment = sub.Increment ?? _settings.CovIncrement;
        double delta = Math.Abs(ValueConverter.ToDouble(value) - ValueConverter.ToDouble(sub.LastValue));
        return increment <= 0 ? delta > 0 : delta >= increment;
    }

    // ---- Device management ----

    private void OnDeviceCommunicationControl(BacnetClient sender, BacnetAddress adr, byte invokeId, uint timeDuration, uint enableDisable, string password, BacnetMaxSegments maxSegments)
    {
        var requested = enableDisable switch { 1 => CommunicationState.Disabled, 2 => CommunicationState.DisableInitiation, _ => CommunicationState.Enabled };
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL,
            () => $"DeviceCommunicationControl {requested}{(timeDuration > 0 ? $" for {timeDuration} min" : "")}", exemptFromDcc: true)) return;

        if (!PasswordOk(password))
        {
            Error(sender, adr, Services.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL, invokeId, BacnetErrorClasses.ERROR_CLASS_SECURITY, BacnetErrorCodes.ERROR_CODE_PASSWORD_FAILURE);
            return;
        }
        Ack(sender, adr, Services.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL, invokeId);
        _communicationUntilUtc = requested == CommunicationState.Enabled || timeDuration == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(timeDuration);
        _communication = requested;
        Log.Warn(adr.ToString(), $"Communication {requested switch { CommunicationState.Disabled => "disabled", CommunicationState.DisableInitiation => "initiation disabled", _ => "enabled" }}" +
            (requested != CommunicationState.Enabled && timeDuration > 0 ? $" for {timeDuration} min" : ""));
    }

    private void OnReinitialize(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetReinitializedStates state, string password, BacnetMaxSegments maxSegments)
    {
        if (!Begin(sender, adr, invokeId, Services.SERVICE_CONFIRMED_REINITIALIZE_DEVICE, () => $"ReinitializeDevice {state}", exemptFromDcc: true)) return;
        if (!PasswordOk(password))
        {
            Error(sender, adr, Services.SERVICE_CONFIRMED_REINITIALIZE_DEVICE, invokeId, BacnetErrorClasses.ERROR_CLASS_SECURITY, BacnetErrorCodes.ERROR_CODE_PASSWORD_FAILURE);
            return;
        }
        Ack(sender, adr, Services.SERVICE_CONFIRMED_REINITIALIZE_DEVICE, invokeId);

        if (state is BacnetReinitializedStates.BACNET_REINIT_COLDSTART or BacnetReinitializedStates.BACNET_REINIT_WARMSTART)
        {
            lock (_subscriptions) _subscriptions.Clear();
            _communication = CommunicationState.Enabled;
            _communicationUntilUtc = DateTime.MaxValue;
            if (state == BacnetReinitializedStates.BACNET_REINIT_COLDSTART) _model.ResetStates();
            Log.Warn(adr.ToString(), state == BacnetReinitializedStates.BACNET_REINIT_COLDSTART
                ? "Cold start: priority arrays, out-of-service flags and COV subscriptions cleared"
                : "Warm start: COV subscriptions cleared");
            SendIAm(null);
        }
    }

    private void OnTimeSynchronize(BacnetClient sender, BacnetAddress adr, DateTime dateTime, bool utc)
    {
        string peer = Touch(adr);
        Log.Frame(TrafficDirection.Rx, peer, [], () => $"{(utc ? "UTC" : "")}TimeSynchronization {dateTime:yyyy-MM-dd HH:mm:ss}");
        if (CommunicationDisabled) return;
        var local = utc ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime() : dateTime;
        _clockOffset = local - DateTime.Now;
    }

    // ---- Formatting ----

    private string Key(BacnetObjectId id) =>
        id.type == BacnetObjectTypes.OBJECT_DEVICE ? $"DEV:{id.instance}" : BacnetAddressSpec.KeyOf(id.type, id.instance);

    private static string Name(BacnetPropertyReference r)
    {
        string name = Name((Props)r.propertyIdentifier);
        return r.propertyArrayIndex == ArrayAll ? name : $"{name}[{r.propertyArrayIndex}]";
    }

    private static string Name(Enum e)
    {
        var s = e.ToString();
        foreach (var prefix in new[] { "PROP_", "SERVICE_CONFIRMED_", "ERROR_CLASS_", "ERROR_CODE_" })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) { s = s[prefix.Length..]; break; }
        return s.ToLowerInvariant().Replace('_', '-');
    }

    private static string Format(IEnumerable<BacnetValue>? values)
    {
        if (values == null) return "";
        var list = values.Take(6).Select(v => v.Tag == Tags.BACNET_APPLICATION_TAG_NULL ? "NULL" : Convert.ToString(v.Value, CultureInfo.InvariantCulture)).ToList();
        return list.Count == 1 ? list[0] ?? "" : "[" + string.Join(", ", list) + "]";
    }
}
