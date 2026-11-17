using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.MTConnect;

namespace ProtoSim.MTConnect.Tests;

public sealed class AddressTests
{
    [Theory]
    [InlineData("Axes/Linear[X]/Xact:POSITION", TagDataType.Float64)]
    [InlineData("Controller/Path/execution:EXECUTION", TagDataType.String)]
    [InlineData("Controller/system:SYSTEM", TagDataType.String)]
    [InlineData("partcount:PART_COUNT", TagDataType.UInt32)]
    [InlineData("Axes/Rotary[C]/S1:ROTARY_VELOCITY.ACTUAL", TagDataType.Float32)]
    [InlineData("Systems/flag:CUSTOM_FLAG:EVENT", TagDataType.Bool)]
    public void AcceptsValidAddresses(string address, TagDataType type)
    {
        using var b = new MtcDataModel().Bind(address, type, WordOrder.ABCD);
        Assert.Equal(type, b.DataType);
    }

    [Theory]
    [InlineData("Axes/Xact", TagDataType.Float64)]                   // no type
    [InlineData("axes/Xact:POSITION", TagDataType.Float64)]          // component types are PascalCase
    [InlineData("Axes/Linear[X]/Xact:POSITION", TagDataType.String)] // samples are numeric
    [InlineData("avail:AVAILABILITY", TagDataType.String)]           // reserved id
    [InlineData("Axes/Xact:POSITION:BOGUS", TagDataType.Float64)]
    public void RejectsInvalidAddresses(string address, TagDataType type) =>
        Assert.Throws<FormatException>(() => new MtcDataModel().Bind(address, type, WordOrder.ABCD));

    [Fact]
    public void RejectsDuplicateIdsWithDifferentShape()
    {
        var model = new MtcDataModel();
        using var a = model.Bind("Axes/Linear[X]/pos:POSITION", TagDataType.Float64, WordOrder.ABCD);
        Assert.Throws<FormatException>(() => model.Bind("Axes/Linear[Y]/pos:POSITION", TagDataType.Float64, WordOrder.ABCD));
    }
}

/// <summary>The agent over real HTTP on loopback.</summary>
public sealed class AgentTests : IAsyncLifetime
{
    private static readonly XNamespace Streams = "urn:mtconnect.org:MTConnectStreams:2.0";
    private static readonly XNamespace Devices = "urn:mtconnect.org:MTConnectDevices:2.0";
    private static readonly XNamespace Errors = "urn:mtconnect.org:MTConnectError:2.0";

    private readonly MtcSettings _settings = new() { BindAddress = "127.0.0.1", Port = 0, DeviceName = "Mill", BufferSize = 64 };
    private readonly MtcDataModel _model = new();
    private readonly FaultSettings _faults = new();
    private readonly List<ITagBinding> _bindings = [];
    private MtcAgent _agent = null!;
    private HttpClient _http = null!;
    private ITagBinding _x = null!, _exec = null!, _count = null!, _system = null!, _estop = null!;

    private ITagBinding Bind(string address, TagDataType type)
    {
        var b = _model.Bind(address, type, WordOrder.ABCD);
        _bindings.Add(b);
        return b;
    }

    public async ValueTask InitializeAsync()
    {
        _x = Bind("Axes/Linear[X]/Xact:POSITION.ACTUAL", TagDataType.Float64);
        _exec = Bind("Controller/Path/execution:EXECUTION", TagDataType.String);
        _count = Bind("Controller/Path/partcount:PART_COUNT", TagDataType.UInt32);
        _system = Bind("Controller/system:SYSTEM", TagDataType.String);
        _estop = Bind("Controller/estop:EMERGENCY_STOP", TagDataType.Bool);
        _x.Write(12.5);
        _exec.Write("READY");
        _system.Write("NORMAL");

        _agent = new MtcAgent(_settings, _model, new ServerContext(new TrafficLog(), _faults, new ServerStatistics()));
        await _agent.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_agent.LocalEndPoint!.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _agent.DisposeAsync();
        foreach (var b in _bindings) b.Dispose();
    }

    private async Task<(HttpStatusCode Status, XElement Root)> GetAsync(string url)
    {
        var response = await _http.GetAsync(url, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, XElement.Parse(text));
    }

    private static XElement Header(XElement root, XNamespace ns) => root.Element(ns + "Header")!;
    private static long Attr(XElement e, string name) => long.Parse((string)e.Attribute(name)!);

    [Fact]
    public async Task ProbeDescribesComponentsAndDataItems()
    {
        var (status, root) = await GetAsync("probe");
        Assert.Equal(HttpStatusCode.OK, status);
        var device = root.Descendants(Devices + "Device").Single();
        Assert.Equal("Mill", (string)device.Attribute("name")!);

        var linear = root.Descendants(Devices + "Linear").Single();
        Assert.Equal("X", (string)linear.Attribute("name")!);
        var xact = linear.Descendants(Devices + "DataItem").Single();
        Assert.Equal("POSITION", (string)xact.Attribute("type")!);
        Assert.Equal("ACTUAL", (string)xact.Attribute("subType")!);
        Assert.Equal("SAMPLE", (string)xact.Attribute("category")!);
        Assert.Equal("MILLIMETER", (string)xact.Attribute("units")!);

        var items = root.Descendants(Devices + "DataItem").Select(d => (string)d.Attribute("id")!).ToList();
        Assert.Equal(6, items.Count);
        Assert.Contains("avail", items);

        // The device name in the URL selects the same document; other names don't exist.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("Mill/probe")).Status);
        var (missing, error) = await GetAsync("Lathe/probe");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("NO_DEVICE", (string)error.Descendants(Errors + "Error").Single().Attribute("errorCode")!);
    }

    [Fact]
    public async Task CurrentReportsLatestValuesByCategory()
    {
        _count.Write(7u);
        var (_, root) = await GetAsync("current");
        Assert.Equal("AVAILABLE", root.Descendants(Streams + "Availability").Single().Value);
        Assert.Equal("12.5", root.Descendants(Streams + "Position").Single().Value);
        Assert.Equal("READY", root.Descendants(Streams + "Execution").Single().Value);
        Assert.Equal("7", root.Descendants(Streams + "PartCount").Single().Value);
        Assert.Equal("ARMED", root.Descendants(Streams + "EmergencyStop").Single().Value);
        var normal = root.Descendants(Streams + "Normal").Single();
        Assert.Equal("SYSTEM", (string)normal.Attribute("type")!);

        var stream = root.Descendants(Streams + "ComponentStream").Single(c => (string?)c.Attribute("name") == "X");
        Assert.Equal("Linear", (string)stream.Attribute("component")!);
        Assert.NotNull(stream.Element(Streams + "Samples"));

        _system.Write("FAULT");
        (_, root) = await GetAsync("current");
        Assert.Single(root.Descendants(Streams + "Fault"));
    }

    [Fact]
    public async Task PathFiltersWithXPath()
    {
        var (_, root) = await GetAsync("current?path=//Axes");
        Assert.Equal(new[] { "Xact" }, root.Descendants().Where(e => e.Attribute("dataItemId") != null).Select(e => (string)e.Attribute("dataItemId")!));

        (_, root) = await GetAsync("current?path=" + Uri.EscapeDataString("//DataItem[@category='EVENT']"));
        var ids = root.Descendants().Where(e => e.Attribute("dataItemId") != null).Select(e => (string)e.Attribute("dataItemId")!).ToHashSet();
        Assert.Equal(new HashSet<string> { "avail", "execution", "partcount", "estop" }, ids);

        var (status, error) = await GetAsync("current?path=" + Uri.EscapeDataString("//[bad"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_XPATH", (string)error.Descendants(Errors + "Error").Single().Attribute("errorCode")!);
    }

    [Fact]
    public async Task SampleReturnsChangesInSequenceOrder()
    {
        var (_, cur) = await GetAsync("current");
        long next = Attr(Header(cur, Streams), "nextSequence");

        _x.Write(13.0);
        _x.Write(13.0); // unchanged: no new observation
        _x.Write(14.0);
        _exec.Write("ACTIVE");

        var (_, root) = await GetAsync($"sample?from={next}&count=10");
        var obs = root.Descendants().Where(e => e.Attribute("sequence") != null).ToList();
        Assert.Equal(new[] { "13", "14", "ACTIVE" }, obs.Select(o => o.Value));
        Assert.Equal(new[] { next, next + 1, next + 2 }, obs.Select(o => Attr(o, "sequence")));
        Assert.Equal(next + 3, Attr(Header(root, Streams), "nextSequence"));

        // count limits the result and nextSequence continues from there.
        (_, root) = await GetAsync($"sample?from={next}&count=1");
        Assert.Single(root.Descendants(), e => e.Attribute("sequence") != null);
        Assert.Equal(next + 1, Attr(Header(root, Streams), "nextSequence"));

        // at= gives the state at an earlier sequence.
        (_, root) = await GetAsync($"current?at={next}");
        Assert.Equal("13", root.Descendants(Streams + "Position").Single().Value);
    }

    [Fact]
    public async Task BufferWrapsAndReportsOutOfRange()
    {
        for (int i = 0; i < 100; i++) _x.Write((double)i);
        var (_, cur) = await GetAsync("current");
        var header = Header(cur, Streams);
        long first = Attr(header, "firstSequence");
        Assert.Equal(64, Attr(header, "lastSequence") - first + 1);

        var (status, error) = await GetAsync($"sample?from={first - 1}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("OUT_OF_RANGE", (string)error.Descendants(Errors + "Error").Single().Attribute("errorCode")!);

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("sample?count=0")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("current?interval=1000")).Status);
    }

    [Fact]
    public async Task AssetsAndErrors()
    {
        var (status, root) = await GetAsync("assets");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("MTConnectAssets", root.Name.LocalName);

        var post = await _http.PostAsync("current", new StringContent("x"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

        _faults.ErrorResponsePercent = 100;
        var (faulted, error) = await GetAsync("current");
        Assert.Equal(HttpStatusCode.InternalServerError, faulted);
        Assert.Equal("INTERNAL_ERROR", (string)error.Descendants(Errors + "Error").Single().Attribute("errorCode")!);
    }

    [Fact]
    public async Task NewTagsAppearInProbeWhileRunning()
    {
        using var load = _model.Bind("Axes/Rotary[C]/Sload:LOAD", TagDataType.Float32, WordOrder.ABCD);
        load.Write(42f);
        var (_, probe) = await GetAsync("probe");
        Assert.Contains(probe.Descendants(Devices + "DataItem"), d => (string?)d.Attribute("id") == "Sload");
        var (_, cur) = await GetAsync("current");
        Assert.Equal("42", cur.Descendants(Streams + "Load").Single().Value);
    }
}
