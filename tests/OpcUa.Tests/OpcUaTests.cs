using System.Net;
using System.Net.Sockets;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.OpcUa;
using ServerState = ProtoSim.Core.Protocols.ServerState;

namespace ProtoSim.OpcUa.Tests;

public class DataModelTests
{
    [Theory]
    [InlineData("Plant/Tank1/Level", "Plant/Tank1/Level")]
    [InlineData(" Plant / Tank1 /Level ", "Plant/Tank1/Level")]
    [InlineData("Plant\\Tank1", "Plant/Tank1")]
    [InlineData("ns=2;s=A/B", "A/B")]
    [InlineData("Motor.Speed", "Motor.Speed")]
    public void NormalizesPaths(string input, string expected) => Assert.Equal(expected, OpcUaDataModel.NormalizePath(input));

    [Theory]
    [InlineData("")]
    [InlineData("A//B")]
    [InlineData("ns=2;i=5")]
    [InlineData("A/B;C")]
    public void RejectsBadPaths(string input) => Assert.Throws<FormatException>(() => OpcUaDataModel.NormalizePath(input));

    [Fact]
    public void SharedPath_IsRefCounted()
    {
        var model = new OpcUaDataModel();
        int added = 0, removed = 0;
        model.VariableAdded += _ => added++;
        model.VariableRemoved += _ => removed++;

        var a = model.Bind("X/Y", TagDataType.Int32, WordOrder.ABCD);
        var b = model.Bind("X/Y", TagDataType.Int32, WordOrder.ABCD);
        Assert.Equal(1, added);
        a.Write(42);
        Assert.Equal(42, b.Read());

        a.Dispose();
        a.Dispose(); // idempotent
        Assert.Equal(0, removed);
        b.Dispose();
        Assert.Equal(1, removed);
        Assert.Empty(model.Variables);
    }

    [Fact]
    public void RejectsTypeAndFolderConflicts()
    {
        var model = new OpcUaDataModel();
        model.Bind("A/B", TagDataType.Float64, WordOrder.ABCD);
        Assert.Throws<FormatException>(() => model.Bind("A/B", TagDataType.Int16, WordOrder.ABCD));
        Assert.Throws<FormatException>(() => model.Bind("A", TagDataType.Int16, WordOrder.ABCD));     // A is a folder
        Assert.Throws<FormatException>(() => model.Bind("A/B/C", TagDataType.Int16, WordOrder.ABCD)); // A/B is a variable
    }

    [Fact]
    public void Strings_AreSupported()
    {
        var model = new OpcUaDataModel();
        var b = model.Bind("Msg", TagDataType.String, WordOrder.ABCD);
        Assert.Equal("", b.Read());
        b.Write("hello, world");
        Assert.Equal("hello, world", b.Read());
    }
}

/// <summary>Starts a real OPC UA server once and talks to it with the ProtoSim client over loopback.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public OpcUaDataModel Model { get; } = new();
    public OpcUaServerSettings Settings { get; } = new() { Port = FreePort(), UserName = "operator", Password = "secret" };
    public ServerStatistics Stats { get; } = new();
    public FaultSettings Faults { get; } = new();
    public OpcUaServer Server { get; private set; } = null!;
    public ITagBinding Level { get; private set; } = null!;
    public ITagBinding Setpoint { get; private set; } = null!;

    public static string PkiRoot { get; } = Path.Combine(Path.GetTempPath(), "ProtoSimTests", "pki");

    public async Task InitializeAsync()
    {
        OpcUaConfiguration.PkiRoot = PkiRoot;
        Level = Model.Bind("Plant/Tank1/Level", TagDataType.Float64, WordOrder.ABCD);
        Setpoint = Model.Bind("Plant/Setpoints/Level", TagDataType.Float64, WordOrder.ABCD);
        Level.Write(12.5);
        Server = new OpcUaServer(Settings, Model, new ServerContext(new TrafficLog(), Faults, Stats));
        await Server.StartAsync();
    }

    public async Task DisposeAsync() => await Server.DisposeAsync();

    public string Url => $"opc.tcp://localhost:{Settings.Port}";

    public OpcUaClient NewClient(Action<OpcUaClientSettings>? configure = null)
    {
        var s = new OpcUaClientSettings { EndpointUrl = Url, OperationTimeoutMs = 10_000 };
        configure?.Invoke(s);
        return new OpcUaClient(s, new ClientContext(new TrafficLog()));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    ValueTask IAsyncLifetime.InitializeAsync()
    {
        return new ValueTask(InitializeAsync());
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return new ValueTask(DisposeAsync());
    }
}

public sealed class LoopbackTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private static OpcUaClientRequest Req(OpcUaOperation op, string nodes = "", string values = "") =>
        new() { Operation = op, NodeIds = nodes, Values = values, SamplingIntervalMs = 100 };

    [Fact]
    public async Task Browse_ShowsTagHierarchy()
    {
        await using var client = fx.NewClient();
        var r = await client.ExecuteAsync(Req(OpcUaOperation.Browse), TestContext.Current.CancellationToken);
        Assert.True(r.Success, r.Message);
        var level = Assert.Single(r.Rows, row => row.Raw == "ns=2;s=Plant/Tank1/Level");
        Assert.Equal(ValueConverter.Format(fx.Level.Read()), level.Value); // other tests change the value
        Assert.Equal("Level", level.Address.Trim());
        Assert.DoesNotContain(r.Rows, row => row.Address.Trim() == "Aliases/"); // standard folders hidden
        Assert.Contains(r.Rows, row => row.Raw == "ns=2;s=Plant/Tank1"); // folder
        Assert.Equal(ServerState.Running, fx.Server.State);
    }

    [Fact]
    public async Task Read_ReflectsLocalChanges()
    {
        await using var client = fx.NewClient();
        fx.Level.Write(77.25);
        var r = await client.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Plant/Tank1/Level"), TestContext.Current.CancellationToken);
        Assert.True(r.Success, r.Message);
        Assert.Equal("77.25", Assert.Single(r.Rows).Value);
        Assert.StartsWith("Good · Double", r.Rows[0].Raw);
    }

    [Fact]
    public async Task Write_UpdatesModel()
    {
        await using var client = fx.NewClient();
        var r = await client.ExecuteAsync(Req(OpcUaOperation.Write, "ns=2;s=Plant/Setpoints/Level", "55.5"), TestContext.Current.CancellationToken);
        Assert.True(r.Success, r.Message);
        Assert.Equal(55.5, fx.Setpoint.Read());
    }

    [Fact]
    public async Task Write_WrongType_Fails()
    {
        await using var client = fx.NewClient();
        var r = await client.ExecuteAsync(Req(OpcUaOperation.Write, "ns=2;s=Plant/Setpoints/Level", "not a number"), TestContext.Current.CancellationToken);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Subscribe_ReceivesChanges()
    {
        await using var client = fx.NewClient();
        var req = Req(OpcUaOperation.Subscribe, "ns=2;s=Plant/Tank1/Level");
        await client.ExecuteAsync(req, TestContext.Current.CancellationToken);

        fx.Level.Write(1.0);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        fx.Level.Write(2.0);

        string? last = null;
        for (int i = 0; i < 30 && last != "2"; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var r = await client.ExecuteAsync(req, TestContext.Current.CancellationToken);
            Assert.True(r.Success, r.Message);
            last = r.Rows[0].Value;
        }
        Assert.Equal("2", last);
    }

    [Fact]
    public async Task NodesAppearAndDisappearWhileRunning()
    {
        await using var client = fx.NewClient();
        var temp = fx.Model.Bind("Dynamic/Temp", TagDataType.Int32, WordOrder.ABCD);
        temp.Write(7);
        var r = await client.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Dynamic/Temp"), TestContext.Current.CancellationToken);
        Assert.Equal("7", Assert.Single(r.Rows).Value);

        temp.Dispose();
        r = await client.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Dynamic/Temp"), TestContext.Current.CancellationToken);
        Assert.StartsWith("BadNodeIdUnknown", Assert.Single(r.Rows).Raw);
    }

    [Fact]
    public async Task UserLogin_AcceptsCorrectPassword_RejectsWrong()
    {
        await using (var good = fx.NewClient(s => { s.UserName = "operator"; s.Password = "secret"; }))
        {
            var r = await good.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Plant/Tank1/Level"), TestContext.Current.CancellationToken);
            Assert.True(r.Success, r.Message);
        }
        await using var bad = fx.NewClient(s => { s.UserName = "operator"; s.Password = "nope"; });
        var fail = await bad.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Plant/Tank1/Level"), TestContext.Current.CancellationToken);
        Assert.False(fail.Success);
    }

    [Fact]
    public async Task SecureConnection_Works()
    {
        await using var client = fx.NewClient(s => s.UseSecurity = true);
        var r = await client.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Plant/Tank1/Level"), TestContext.Current.CancellationToken);
        Assert.True(r.Success, r.Message);
    }

    [Fact]
    public async Task FaultInjection_ErrorResponse()
    {
        await using var client = fx.NewClient();
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        fx.Faults.ErrorResponsePercent = 100;
        try
        {
            var r = await client.ExecuteAsync(Req(OpcUaOperation.Read, "ns=2;s=Plant/Tank1/Level"), TestContext.Current.CancellationToken);
            Assert.False(r.Success);
            Assert.Contains("BadInternalError", r.Message);
        }
        finally
        {
            fx.Faults.ErrorResponsePercent = 0;
        }
    }

    [Fact]
    public async Task ConnectionsAndStatistics_AreTracked()
    {
        await using var client = fx.NewClient();
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Contains(fx.Server.GetConnections(), c => c.RemoteEndPoint.Contains("ProtoSim client"));
        Assert.True(fx.Stats.ActiveConnections >= 1);
        Assert.True(fx.Stats.Requests > 0);
    }
}
