using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.Snmp;

namespace ProtoSim.Snmp.Tests;

public sealed class AddressTests
{
    [Theory]
    [InlineData("1.3.6.1.4.1.99999.1.1.0", TagDataType.Int32)]
    [InlineData(".1.3.6.1.4.1.99999.1.2.0:counter32", TagDataType.UInt32)]
    [InlineData("1.3.6.1.4.1.99999.1.3.0:ipaddress", TagDataType.String)]
    [InlineData("1.3.6.1.4.1.99999.1.4.0", TagDataType.Float32)]
    public void AcceptsValidAddresses(string address, TagDataType type)
    {
        using var b = new SnmpDataModel().Bind(address, type, WordOrder.ABCD);
        Assert.Equal(type, b.DataType);
    }

    [Theory]
    [InlineData("1.3.6.1.2.1.1.5.0", TagDataType.String)] // system group is built in
    [InlineData("1.3.6.x.1", TagDataType.Int32)]
    [InlineData("5.1.2", TagDataType.Int32)]
    [InlineData("1.3.6.1.4.1.99999.1.1.0:counter32", TagDataType.Float32)]
    [InlineData("1.3.6.1.4.1.99999.1.1.0:bogus", TagDataType.Int32)]
    [InlineData("1.3.6.1.4.1.99999.1.1.0:truthvalue", TagDataType.Int32)]
    public void RejectsInvalidAddresses(string address, TagDataType type) =>
        Assert.Throws<FormatException>(() => new SnmpDataModel().Bind(address, type, WordOrder.ABCD));
}

/// <summary>Request handling, driven with messages built by the same SNMP library a manager would use.</summary>
public sealed class AgentTests : IAsyncLifetime
{
    private static readonly IPEndPoint Manager = new(IPAddress.Loopback, 50000);
    private readonly SnmpSettings _settings = new() { BindAddress = "127.0.0.1", Port = 0, SendColdStart = false, SysName = "sim-1" };
    private readonly SnmpDataModel _model = new();
    private readonly FaultSettings _faults = new();
    private readonly ServerStatistics _stats = new();
    private readonly List<ITagBinding> _bindings = [];
    private SnmpAgent _agent = null!;
    private ITagBinding _load = null!, _packets = null!, _temp = null!, _enabled = null!, _label = null!;

    private const string Base = "1.3.6.1.4.1.99999.1";

    private ITagBinding Bind(string address, TagDataType type)
    {
        var b = _model.Bind(address, type, WordOrder.ABCD);
        _bindings.Add(b);
        return b;
    }

    public async ValueTask InitializeAsync()
    {
        _load = Bind($"{Base}.2.0", TagDataType.Int32);
        _packets = Bind($"{Base}.3.0:counter32", TagDataType.UInt32);
        _temp = Bind($"{Base}.4.0", TagDataType.Float32);
        _enabled = Bind($"{Base}.5.0", TagDataType.Bool);
        _label = Bind($"{Base}.10.0", TagDataType.String); // .10 sorts after .5
        _load.Write(450);
        _packets.Write(1234u);
        _temp.Write(21.5f);
        _label.Write("rack A");
        _agent = new SnmpAgent(_settings, _model, new ServerContext(new TrafficLog(), _faults, _stats));
        await _agent.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
        foreach (var b in _bindings) b.Dispose();
    }

    private static List<Variable> Vars(params string[] oids) => oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();

    private async Task<ISnmpPdu?> SendAsync(ISnmpMessage request)
    {
        var bytes = await _agent.HandleAsync(request.ToBytes(), Manager, TestContext.Current.CancellationToken);
        return bytes == null ? null : MessageFactory.ParseMessages(bytes, new UserRegistry())[0].Pdu();
    }

    [Fact]
    public async Task GetReturnsSystemGroupAndTags()
    {
        var pdu = await SendAsync(new GetRequestMessage(1, VersionCode.V2, new OctetString("public"),
            Vars("1.3.6.1.2.1.1.5.0", $"{Base}.2.0", $"{Base}.3.0", $"{Base}.4.0", $"{Base}.5.0")));
        Assert.NotNull(pdu);
        Assert.Equal(0, pdu.ErrorStatus.ToInt32());
        var v = pdu.Variables;
        Assert.Equal("sim-1", v[0].Data.ToString());
        Assert.Equal(new Integer32(450), v[1].Data);
        Assert.Equal(new Counter32(1234), v[2].Data);
        Assert.Equal("21.5", v[3].Data.ToString());
        Assert.Equal(new Integer32(2), v[4].Data); // TruthValue false
    }

    [Fact]
    public async Task GetReportsMissingObjectsPerVersion()
    {
        var v2 = await SendAsync(new GetRequestMessage(2, VersionCode.V2, new OctetString("public"), Vars($"{Base}.2.1", "1.3.6.1.4.1.12345.1.0")));
        Assert.Equal(SnmpType.NoSuchInstance, v2!.Variables[0].Data.TypeCode);
        Assert.Equal(SnmpType.NoSuchObject, v2.Variables[1].Data.TypeCode);

        var v1 = await SendAsync(new GetRequestMessage(3, VersionCode.V1, new OctetString("public"), Vars($"{Base}.2.0", "1.3.6.1.4.1.12345.1.0")));
        Assert.Equal((int)ErrorCode.NoSuchName, v1!.ErrorStatus.ToInt32());
        Assert.Equal(2, v1.ErrorIndex.ToInt32());
    }

    [Fact]
    public async Task GetNextWalksInNumericOrder()
    {
        var oids = new List<string>();
        var cursor = "1.3.6.1.2.1.1";
        while (true)
        {
            var pdu = await SendAsync(new GetNextRequestMessage(4, VersionCode.V2, new OctetString("public"), Vars(cursor)));
            var v = pdu!.Variables[0];
            if (v.Data.TypeCode == SnmpType.EndOfMibView) break;
            oids.Add(v.Id.ToString());
            cursor = v.Id.ToString();
        }
        Assert.Equal(new[]
        {
            "1.3.6.1.2.1.1.1.0", "1.3.6.1.2.1.1.2.0", "1.3.6.1.2.1.1.3.0", "1.3.6.1.2.1.1.4.0", "1.3.6.1.2.1.1.5.0", "1.3.6.1.2.1.1.6.0", "1.3.6.1.2.1.1.7.0",
            $"{Base}.2.0", $"{Base}.3.0", $"{Base}.4.0", $"{Base}.5.0", $"{Base}.10.0",
        }, oids);
    }

    [Fact]
    public async Task GetBulkHonoursNonRepeatersAndRepetitions()
    {
        var pdu = await SendAsync(new GetBulkRequestMessage(5, VersionCode.V2, new OctetString("public"), 1, 3, Vars("1.3.6.1.2.1.1.4.0", Base)));
        var ids = pdu!.Variables.Select(v => v.Id.ToString()).ToList();
        Assert.Equal(new[] { "1.3.6.1.2.1.1.5.0", $"{Base}.2.0", $"{Base}.3.0", $"{Base}.4.0" }, ids);
    }

    [Fact]
    public async Task SetNeedsWriteCommunityAndMatchingType()
    {
        var readOnly = await SendAsync(new SetRequestMessage(6, VersionCode.V2, new OctetString("public"),
            [new Variable(new ObjectIdentifier($"{Base}.2.0"), new Integer32(1))]));
        Assert.Equal((int)ErrorCode.NoAccess, readOnly!.ErrorStatus.ToInt32());

        var wrongType = await SendAsync(new SetRequestMessage(7, VersionCode.V2, new OctetString("private"),
            [new Variable(new ObjectIdentifier($"{Base}.2.0"), new OctetString("x"))]));
        Assert.Equal((int)ErrorCode.WrongType, wrongType!.ErrorStatus.ToInt32());

        var system = await SendAsync(new SetRequestMessage(8, VersionCode.V2, new OctetString("private"),
            [new Variable(new ObjectIdentifier("1.3.6.1.2.1.1.5.0"), new OctetString("x"))]));
        Assert.Equal((int)ErrorCode.NotWritable, system!.ErrorStatus.ToInt32());

        // A failing varbind leaves the others unchanged (Set is atomic).
        var partial = await SendAsync(new SetRequestMessage(9, VersionCode.V2, new OctetString("private"),
        [
            new Variable(new ObjectIdentifier($"{Base}.2.0"), new Integer32(999)),
            new Variable(new ObjectIdentifier($"{Base}.5.0"), new Integer32(7)),
        ]));
        Assert.Equal((int)ErrorCode.WrongValue, partial!.ErrorStatus.ToInt32());
        Assert.Equal(2, partial.ErrorIndex.ToInt32());
        Assert.Equal(450, _load.Read());

        var ok = await SendAsync(new SetRequestMessage(10, VersionCode.V2, new OctetString("private"),
        [
            new Variable(new ObjectIdentifier($"{Base}.2.0"), new Integer32(999)),
            new Variable(new ObjectIdentifier($"{Base}.5.0"), new Integer32(1)),
            new Variable(new ObjectIdentifier($"{Base}.10.0"), new OctetString("rack B")),
        ]));
        Assert.Equal(0, ok!.ErrorStatus.ToInt32());
        Assert.Equal(999, _load.Read());
        Assert.Equal(true, _enabled.Read());
        Assert.Equal("rack B", _label.Read());
    }

    [Fact]
    public async Task IgnoresUnknownCommunityAndDisabledVersions()
    {
        Assert.Null(await SendAsync(new GetRequestMessage(11, VersionCode.V2, new OctetString("wrong"), Vars($"{Base}.2.0"))));
        _settings.AllowV1 = false;
        Assert.Null(await SendAsync(new GetRequestMessage(12, VersionCode.V1, new OctetString("public"), Vars($"{Base}.2.0"))));
        Assert.True(_stats.Dropped >= 2);
    }

    [Fact]
    public async Task InjectsConfiguredError()
    {
        _faults.ErrorResponsePercent = 100;
        _faults.ErrorCode = 5;
        var pdu = await SendAsync(new GetRequestMessage(13, VersionCode.V2, new OctetString("public"), Vars($"{Base}.2.0")));
        Assert.Equal((int)ErrorCode.GenError, pdu!.ErrorStatus.ToInt32());
    }

    [Fact]
    public void AnswersOverUdp()
    {
        var result = Messenger.Get(VersionCode.V2, _agent.LocalEndPoint!, new OctetString("public"), Vars("1.3.6.1.2.1.1.1.0", $"{Base}.3.0"), 2000);
        Assert.Equal("ProtoSim simulated SNMP agent", result[0].Data.ToString());
        Assert.Equal(new Counter32(1234), result[1].Data);
    }

    [Fact]
    public async Task SendsColdStartTrapToReceivers()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        var settings = new SnmpSettings { BindAddress = "127.0.0.1", Port = 0, TrapReceivers = $"127.0.0.1:{port}" };
        await using var agent = new SnmpAgent(settings, new SnmpDataModel(), new ServerContext(new TrafficLog(), new FaultSettings(), new ServerStatistics()));
        await agent.StartAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(3000);
        var packet = await receiver.ReceiveAsync(timeout.Token);
        var trap = (TrapV2Message)MessageFactory.ParseMessages(packet.Buffer, new UserRegistry())[0];
        Assert.Equal("1.3.6.1.6.3.1.1.5.1", trap.Enterprise.ToString());
    }
}
