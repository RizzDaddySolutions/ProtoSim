using System.Collections.Concurrent;
using System.IO.BACnet;
using System.Net;
using System.Net.Sockets;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.Bacnet;

namespace ProtoSim.Bacnet.Tests;

public sealed class AddressTests
{
    [Theory]
    [InlineData("AV:3", TagDataType.Float32)]
    [InlineData("ai:1/Zone Temp", TagDataType.Int16)]
    [InlineData("BO:0", TagDataType.Bool)]
    [InlineData("MSV:2", TagDataType.UInt16)]
    [InlineData("CSV:1 / Status", TagDataType.String)]
    public void AcceptsValidAddresses(string address, TagDataType type)
    {
        using var b = new BacnetDataModel().Bind(address, type, WordOrder.ABCD);
        Assert.Equal(type, b.DataType);
    }

    [Theory]
    [InlineData("AV:3", TagDataType.Bool)]
    [InlineData("BI:1", TagDataType.Float32)]
    [InlineData("MSI:1", TagDataType.Float32)]
    [InlineData("CSV:1", TagDataType.Int32)]
    [InlineData("XX:1", TagDataType.Int32)]
    [InlineData("AV:5000000", TagDataType.Float32)]
    [InlineData("AV", TagDataType.Float32)]
    public void RejectsInvalidAddresses(string address, TagDataType type) =>
        Assert.Throws<FormatException>(() => new BacnetDataModel().Bind(address, type, WordOrder.ABCD));
}

/// <summary>The simulated device against a BACnet client from the same stack, over loopback UDP.</summary>
public sealed class LoopbackTests : IAsyncLifetime
{
    private const uint DeviceInstance = 4321;
    private readonly BacnetDataModel _model = new();
    private readonly FaultSettings _faults = new();
    private readonly ServerStatistics _stats = new();
    private readonly List<ITagBinding> _bindings = [];
    private BacnetDevice _device = null!;
    private BacnetClient _client = null!;
    private BacnetAddress _target = null!;

    private ITagBinding _ai = null!, _av = null!, _bv = null!, _msv = null!, _csv = null!;

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private ITagBinding Bind(string address, TagDataType type)
    {
        var b = _model.Bind(address, type, WordOrder.ABCD);
        _bindings.Add(b);
        return b;
    }

    public async ValueTask InitializeAsync()
    {
        _ai = Bind("AI:1/Zone Temperature", TagDataType.Float32);
        _av = Bind("AV:1/Setpoint", TagDataType.Float32);
        _bv = Bind("BV:1", TagDataType.Bool);
        _msv = Bind("MSV:1", TagDataType.UInt16);
        _csv = Bind("CSV:1", TagDataType.String);
        _ai.Write(21.5f);
        _av.Write(20f);

        int port = FreeUdpPort();
        var settings = new BacnetSettings { LocalAddress = "127.0.0.1", Port = port, DeviceInstance = (int)DeviceInstance, AnnounceOnStart = false, CovIncrement = 0.5 };
        _device = new BacnetDevice(settings, _model, new ServerContext(new TrafficLog(), _faults, _stats));
        await _device.StartAsync(TestContext.Current.CancellationToken);

        _client = new BacnetClient(new BacnetIpUdpProtocolTransport(FreeUdpPort(), false, false, 1472, "127.0.0.1"), 500, 2);
        _client.Start();
        _target = new BacnetAddress(BacnetAddressTypes.IP, $"127.0.0.1:{port}");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _device.DisposeAsync();
        foreach (var b in _bindings) b.Dispose();
    }

    private IList<BacnetValue> Read(BacnetObjectId id, BacnetPropertyIds prop, uint index = uint.MaxValue)
    {
        Assert.True(_client.ReadPropertyRequest(_target, id, prop, out var values, 0, index), $"Read {id} {prop} failed");
        return values;
    }

    private bool Write(BacnetObjectId id, BacnetPropertyIds prop, BacnetValue value, byte? priority = null) =>
        _client.WritePropertyRequest(_target, id, prop, [value], 0, priority);

    /// <summary>The client throws on an Error PDU (or returns false on timeout); both count as a refused request.</summary>
    private static string Refused(Func<bool> request)
    {
        try { return request() ? "accepted" : "timeout"; }
        catch (Exception ex) { return ex.Message; }
    }

    private static readonly BacnetObjectId Device = new(BacnetObjectTypes.OBJECT_DEVICE, DeviceInstance);
    private static readonly BacnetObjectId Ai1 = new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
    private static readonly BacnetObjectId Av1 = new(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1);
    private static readonly BacnetObjectId Bv1 = new(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1);
    private static readonly BacnetObjectId Msv1 = new(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, 1);
    private static readonly BacnetObjectId Csv1 = new(BacnetObjectTypes.OBJECT_CHARACTERSTRING_VALUE, 1);

    [Fact]
    public async Task AnswersWhoIsWithIAm()
    {
        var found = new TaskCompletionSource<uint>();
        _client.OnIam += (_, _, deviceId, _, _, _) => found.TrySetResult(deviceId);
        _client.WhoIs(-1, -1, _target);
        Assert.Equal(DeviceInstance, await found.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsDeviceObject()
    {
        Assert.Equal("ProtoSim BACnet Device", Read(Device, BacnetPropertyIds.PROP_OBJECT_NAME)[0].Value);
        var list = Read(Device, BacnetPropertyIds.PROP_OBJECT_LIST);
        Assert.Equal(6, list.Count);
        Assert.Contains(list, v => v.Value is BacnetObjectId id && id.Equals(Ai1));
        Assert.Equal(6u, Read(Device, BacnetPropertyIds.PROP_OBJECT_LIST, 0)[0].Value);
        Assert.Equal(Device, Read(Device, BacnetPropertyIds.PROP_OBJECT_LIST, 1)[0].Value);
        // The wildcard instance 4194303 means "this device".
        Assert.Equal(Device, Read(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 4_194_303), BacnetPropertyIds.PROP_OBJECT_IDENTIFIER)[0].Value);
    }

    [Fact]
    public void ReadsObjectProperties()
    {
        Assert.Equal(21.5f, Read(Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value);
        Assert.Equal("Zone Temperature", Read(Ai1, BacnetPropertyIds.PROP_OBJECT_NAME)[0].Value);
        Assert.Equal("Binary Value 1", Read(Bv1, BacnetPropertyIds.PROP_OBJECT_NAME)[0].Value);
        Assert.Equal(1u, Read(Msv1, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value); // multi-state values start at 1
        Assert.Equal(16, Read(Av1, BacnetPropertyIds.PROP_PRIORITY_ARRAY).Count);
        Assert.Contains("UNKNOWN_PROPERTY", Refused(() => _client.ReadPropertyRequest(_target, Ai1, BacnetPropertyIds.PROP_PRIORITY_ARRAY, out _))); // inputs aren't commandable
        Assert.Contains("UNKNOWN_OBJECT", Refused(() => _client.ReadPropertyRequest(_target, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 99), BacnetPropertyIds.PROP_PRESENT_VALUE, out _)));
    }

    [Fact]
    public void ReadPropertyMultipleExpandsAll()
    {
        Assert.True(_client.ReadPropertyMultipleRequest(_target, Av1, [new BacnetPropertyReference(BacnetPropertyIds.PROP_ALL)], out var results));
        var props = Assert.Single(results).values.Select(v => (BacnetPropertyIds)v.property.propertyIdentifier).ToList();
        Assert.Contains(BacnetPropertyIds.PROP_PRESENT_VALUE, props);
        Assert.Contains(BacnetPropertyIds.PROP_PRIORITY_ARRAY, props);
        Assert.Contains(BacnetPropertyIds.PROP_UNITS, props);
    }

    [Fact]
    public void PriorityArrayCommandsAndRelinquishes()
    {
        Assert.True(Write(Av1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 23.5f), 8));
        Assert.Equal(23.5f, _av.Read());
        Assert.Equal(23.5f, Read(Av1, BacnetPropertyIds.PROP_PRIORITY_ARRAY, 8)[0].Value);
        Assert.Equal(20f, Read(Av1, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT)[0].Value);

        // A higher priority wins; relinquishing it falls back to priority 8, then to the relinquish default.
        Assert.True(Write(Av1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 18f), 3));
        Assert.Equal(18f, _av.Read());
        Assert.True(Write(Av1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null), 3));
        Assert.Equal(23.5f, _av.Read());
        Assert.True(Write(Av1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null), 8));
        Assert.Equal(20f, _av.Read());
    }

    [Fact]
    public void ValidatesWrites()
    {
        // Inputs are read-only until out of service.
        Assert.Contains("WRITE_ACCESS_DENIED", Refused(() => Write(Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 5f))));
        Assert.True(Write(Ai1, BacnetPropertyIds.PROP_OUT_OF_SERVICE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, true)));
        Assert.True(Write(Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 5f)));
        Assert.Equal(5f, _ai.Read());

        Assert.True(Write(Bv1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 1u)));
        Assert.Equal(true, _bv.Read());
        Assert.Contains("VALUE_OUT_OF_RANGE", Refused(() => Write(Bv1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 2u))));

        Assert.True(Write(Msv1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, 3u)));
        Assert.Equal((ushort)3, _msv.Read());
        Assert.Contains("VALUE_OUT_OF_RANGE", Refused(() => Write(Msv1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, 99u))));

        Assert.True(Write(Csv1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, "Occupied")));
        Assert.Equal("Occupied", _csv.Read());
        Assert.Contains("INVALID_DATA_TYPE", Refused(() => Write(Csv1, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 1f))));

        Assert.Contains("WRITE_ACCESS_DENIED", Refused(() => Write(Device, BacnetPropertyIds.PROP_OBJECT_NAME, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, "x"))));
    }

    [Fact]
    public async Task SendsCovNotificationsPastTheIncrement()
    {
        var notes = new BlockingCollection<float>();
        _client.OnCOVNotification += (_, _, _, _, _, monitored, _, _, values, _) =>
        {
            if (!monitored.Equals(Ai1)) return;
            var pv = values.First(v => (BacnetPropertyIds)v.property.propertyIdentifier == BacnetPropertyIds.PROP_PRESENT_VALUE);
            notes.Add((float)pv.value[0].Value);
        };
        Assert.True(_client.SubscribeCOVRequest(_target, Ai1, 7, false, false, 60));

        Assert.True(notes.TryTake(out var initial, 2000, TestContext.Current.CancellationToken));
        Assert.Equal(21.5f, initial);

        _ai.Write(21.6f); // below the 0.5 increment
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Empty(notes);

        _ai.Write(22.5f);
        Assert.True(notes.TryTake(out var changed, 2000, TestContext.Current.CancellationToken));
        Assert.Equal(22.5f, changed);

        Assert.True(_client.SubscribeCOVRequest(_target, Ai1, 7, true, false, 0)); // cancel
        _ai.Write(30f);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Empty(notes);
    }

    [Fact]
    public void DeviceCommunicationControlSilencesTheDevice()
    {
        Assert.True(_client.DeviceCommunicationControlRequest(_target, 0, 1, ""));
        Assert.Equal("timeout", Refused(() => _client.ReadPropertyRequest(_target, Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE, out _)));
        Assert.True(_client.DeviceCommunicationControlRequest(_target, 0, 0, ""));
        Assert.Equal(21.5f, Read(Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE)[0].Value);
    }

    [Fact]
    public void InjectsConfiguredError()
    {
        _faults.ErrorResponsePercent = 100;
        _faults.ErrorCode = 25;
        Assert.Contains("OPERATIONAL_PROBLEM", Refused(() => _client.ReadPropertyRequest(_target, Ai1, BacnetPropertyIds.PROP_PRESENT_VALUE, out _)));
        Assert.True(_stats.Exceptions >= 1);
    }
}
