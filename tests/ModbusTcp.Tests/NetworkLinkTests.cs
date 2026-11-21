using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Logic;
using ProtoSim.Core.Network;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;
using ProtoSim.Protocols.ModbusTcp;

namespace ProtoSim.ModbusTcp.Tests;

/// <summary>A network link from a simulated Modbus TCP device (the source) into the tags of another device (the target).</summary>
public sealed class NetworkLinkTests : IAsyncLifetime
{
    private readonly IProtocolPlugin _plugin = new ModbusTcpPlugin();
    private readonly ModbusServerSettings _sourceSettings = new() { BindAddress = "127.0.0.1", Port = 0 };
    private readonly ModbusDataModel _target = new(new ModbusServerSettings());
    private readonly Directory _tags = new();
    private ModbusDataModel _source = null!;
    private ModbusTcpServer _server = null!;
    private NetworkLink _link = null!;

    private sealed class Directory : ITagDirectory
    {
        public Dictionary<string, SimTag> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SimTag? Find(string reference) => Tags.GetValueOrDefault(reference);
    }

    public async ValueTask InitializeAsync()
    {
        _source = new ModbusDataModel(_sourceSettings);
        _server = new ModbusTcpServer(_sourceSettings, _source, new ServerContext(new TrafficLog(), new FaultSettings(), new ServerStatistics()));
        await _server.StartAsync(TestContext.Current.CancellationToken);
        var settings = (ModbusClientSettings)_plugin.CreateLinkClientSettings(_sourceSettings, "127.0.0.1");
        settings.Port = _server.LocalEndPoint!.Port;
        settings.ResponseTimeoutMs = 500;
        _link = new NetworkLink(_plugin, "Tank → PLC", settings, _tags);
    }

    public async ValueTask DisposeAsync()
    {
        await _link.StopAsync();
        await _server.DisposeAsync();
    }

    private SimTag TargetTag(string name, string address, TagDataType type)
    {
        var tag = new SimTag { Name = name, Address = address, DataType = type };
        tag.Binding = _target.Bind(address, type, WordOrder.ABCD);
        _tags.Tags["PLC/" + name] = tag;
        return tag;
    }

    private void Point(string source, TagDataType type, string target, LinkDirection direction = LinkDirection.Read) =>
        _link.AddPoint(new LinkPointDocument { Source = source, DataType = type, Target = "PLC/" + target, Direction = direction });

    [Fact]
    public void ContiguousPoints_AreReadWithOneRequest()
    {
        var reads = _plugin.CreateLinkReads([
            new LinkPoint("HR:0", TagDataType.Float32), new LinkPoint("HR:2", TagDataType.Float32), new LinkPoint("HR:10", TagDataType.Float32),
            new LinkPoint("CO:1", TagDataType.Bool), new LinkPoint("CO:2", TagDataType.Bool), new LinkPoint("HR:20.3", TagDataType.Bool)]);
        Assert.Equal(4, reads.Count); // HR:0+HR:2, HR:10, CO:1+CO:2, the bit in HR:20
        var run = reads.Single(r => r.Points.SequenceEqual([0, 1]));
        Assert.Equal(2, ((ModbusClientRequest)run.Request).Quantity);
    }

    [Fact]
    public async Task Cycle_CopiesSourceValuesIntoTargetTags()
    {
        _source.Bind("HR:0", TagDataType.Float32, WordOrder.ABCD).Write(42.5f);
        _source.Bind("HR:2", TagDataType.Int32, WordOrder.ABCD).Write(-7);
        _source.Bind("CO:4", TagDataType.Bool, WordOrder.ABCD).Write(true);
        _source.HoldingRegisters.Write(9, 0b1000);

        var level = TargetTag("Level", "HR:100", TagDataType.Float32);
        var count = TargetTag("Count", "HR:110", TagDataType.Int32);
        var running = TargetTag("Running", "CO:0", TagDataType.Bool);
        var bit = TargetTag("Bit", "CO:1", TagDataType.Bool);
        Point("HR:0", TagDataType.Float32, "Level");
        Point("HR:2", TagDataType.Int32, "Count");
        Point("CO:4", TagDataType.Bool, "Running");
        Point("HR:9.3", TagDataType.Bool, "Bit");

        Assert.True(await _link.CycleAsync(TestContext.Current.CancellationToken), _link.LastMessage);
        Assert.Equal(42.5f, level.Binding!.Read());
        Assert.Equal(-7, count.Binding!.Read());
        Assert.Equal(true, running.Binding!.Read());
        Assert.Equal(true, bit.Binding!.Read());
        Assert.All(_link.Points, p => Assert.False(p.HasError, p.Status));
    }

    [Fact]
    public async Task ReadWrite_SendsLocalChangesBack_AndFollowsTheSource()
    {
        var source = _source.Bind("HR:0", TagDataType.Float32, WordOrder.ABCD);
        source.Write(10f);
        var setpoint = TargetTag("Setpoint", "HR:100", TagDataType.Float32);
        Point("HR:0", TagDataType.Float32, "Setpoint", LinkDirection.ReadWrite);
        var ct = TestContext.Current.CancellationToken;

        await _link.CycleAsync(ct);
        Assert.Equal(10f, setpoint.Binding!.Read());

        setpoint.Binding.Write(55f); // changed here: goes to the source
        Assert.True(await _link.CycleAsync(ct), _link.LastMessage);
        Assert.Equal(55f, source.Read());
        Assert.Equal(1, _link.Writes);

        source.Write(60f); // changed at the source: comes here
        await _link.CycleAsync(ct);
        Assert.Equal(60f, setpoint.Binding.Read());
    }

    [Fact]
    public async Task BadPoint_FailsAlone()
    {
        _source.Bind("HR:0", TagDataType.UInt16, WordOrder.ABCD).Write((ushort)3);
        var good = TargetTag("Good", "HR:100", TagDataType.UInt16);
        TargetTag("Bad", "HR:101", TagDataType.UInt16);
        Point("HR:0", TagDataType.UInt16, "Good");
        Point("XX:9", TagDataType.UInt16, "Bad");
        Point("HR:1", TagDataType.UInt16, "Missing");

        await _link.CycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((ushort)3, good.Binding!.Read());
        Assert.False(_link.Points[0].HasError);
        Assert.True(_link.Points[1].HasError);
        Assert.Contains("No tag", _link.Points[2].Status);
    }

    [Fact]
    public void Retarget_PointsLocalSettingsAtAnotherComputer()
    {
        var s = (ModbusClientSettings)_plugin.CreateLinkClientSettings(new ModbusServerSettings { Port = 5020 }, "192.168.1.20");
        Assert.Equal("192.168.1.20", s.Host);
        Assert.Equal(5020, s.Port);
        Assert.Equal("U3:HR:5", _plugin.LinkSourceAddress(new ModbusServerSettings { UnitIds = "3" }, "HR:5"));
        Assert.Equal("HR:5", _plugin.LinkSourceAddress(new ModbusServerSettings(), "HR:5"));
    }

    [Fact]
    public async Task Discovery_FindsAnInstanceAndServesItsCatalog()
    {
        int port = Random.Shared.Next(48_000, 49_000);
        var catalog = new DiscoveryCatalog
        {
            Name = "Line 2", Project = "Plant", Version = "1.3",
            Devices = [new SharedDevice { Name = "Tank", PluginId = _plugin.Id, Tags = [new SharedTag { Name = "Level", Address = "HR:0", DataType = TagDataType.Float32 }] }],
        };
        await using var service = new DiscoveryService(() => Task.FromResult(catalog), port, "test-instance");
        Assert.True(service.Start(), service.LastError);

        var found = await DiscoveryService.DiscoverAsync(TimeSpan.FromMilliseconds(800), ["127.0.0.1"], TestContext.Current.CancellationToken, port);
        var instance = Assert.Single(found, i => i.Id == "test-instance");
        Assert.Equal("Line 2", instance.Name);
        Assert.Equal(1, instance.DeviceCount);

        var fetched = await DiscoveryService.FetchCatalogAsync(instance.Host, instance.CatalogPort, TestContext.Current.CancellationToken);
        Assert.Equal("Level", Assert.Single(Assert.Single(fetched.Devices).Tags).Name);
    }
}
