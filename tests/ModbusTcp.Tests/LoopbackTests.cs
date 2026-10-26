using System.Net.Sockets;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.ModbusTcp;

namespace ProtoSim.ModbusTcp.Tests;

/// <summary>End-to-end tests: the simulator's server and client talking over a real loopback socket.</summary>
public sealed class LoopbackTests : IAsyncLifetime
{
    private readonly ModbusServerSettings _serverSettings = new() { BindAddress = "127.0.0.1", Port = 0, UnitIds = "1,5-6" };
    private readonly FaultSettings _faults = new();
    private readonly ServerStatistics _stats = new();
    private readonly TrafficLog _log = new();
    private ModbusDataModel _model = null!;
    private ModbusTcpServer _server = null!;
    private ModbusTcpClient _client = null!;

    public async Task InitializeAsync()
    {
        _model = new ModbusDataModel(_serverSettings);
        _server = new ModbusTcpServer(_serverSettings, _model, new ServerContext(_log, _faults, _stats));
        await _server.StartAsync();
        _client = new ModbusTcpClient(
            new ModbusClientSettings { Host = "127.0.0.1", Port = _server.LocalEndPoint!.Port, ResponseTimeoutMs = 500 },
            new ClientContext(new TrafficLog()));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task WriteThenReadRegisters()
    {
        await _client.WriteMultipleRegistersAsync(1, 100, [10, 20, 30]);
        Assert.Equal(new ushort[] { 10, 20, 30 }, await _client.ReadHoldingRegistersAsync(1, 100, 3));
        Assert.Equal((ushort)20, _model.HoldingRegisters.Read(101));
        Assert.True(_stats.Requests >= 2);
    }

    [Fact]
    public async Task CoilsAndDiscreteInputs()
    {
        await _client.WriteMultipleCoilsAsync(1, 0, [true, false, true]);
        Assert.Equal(new[] { true, false, true }, await _client.ReadCoilsAsync(1, 0, 3));

        _model.DiscreteInputs.Write(3, 1);
        Assert.Equal(new[] { false, true }, await _client.ReadDiscreteInputsAsync(1, 2, 2));
    }

    [Fact]
    public async Task ExceptionIsReportedToClient()
    {
        var ex = await Assert.ThrowsAsync<ModbusException>(() => _client.ReadHoldingRegistersAsync(1, 0, 200));
        Assert.Equal(ModbusExceptionCode.IllegalDataValue, ex.Code);
        Assert.Equal(1, _stats.Exceptions);
    }

    [Fact]
    public async Task UnservedUnit_GetsGatewayException_OrNothing()
    {
        var ex = await Assert.ThrowsAsync<ModbusException>(() => _client.ReadHoldingRegistersAsync(9, 0, 1));
        Assert.Equal(ModbusExceptionCode.GatewayTargetFailedToRespond, ex.Code);

        await _client.ReadHoldingRegistersAsync(6, 0, 1); // in the 5-6 range

        _serverSettings.UnmatchedUnitBehavior = UnmatchedUnitBehavior.Ignore;
        await Assert.ThrowsAsync<TimeoutException>(() => _client.ReadHoldingRegistersAsync(9, 0, 1));
    }

    [Fact]
    public async Task FaultInjection_DropCausesTimeout_ThenRecovers()
    {
        _faults.DropResponsePercent = 100;
        await Assert.ThrowsAsync<TimeoutException>(() => _client.ReadHoldingRegistersAsync(1, 0, 1));
        _faults.DropResponsePercent = 0;
        Assert.Single(await _client.ReadHoldingRegistersAsync(1, 0, 1)); // client reconnects automatically
    }

    [Fact]
    public async Task FaultInjection_ErrorResponse()
    {
        _faults.ErrorResponsePercent = 100;
        _faults.ErrorCode = 6;
        var ex = await Assert.ThrowsAsync<ModbusException>(() => _client.ReadHoldingRegistersAsync(1, 0, 1));
        Assert.Equal(ModbusExceptionCode.ServerDeviceBusy, ex.Code);
    }

    [Fact]
    public async Task DeviceIdentification()
    {
        var objects = await _client.ReadDeviceIdentificationAsync(1);
        Assert.Equal(7, objects.Count);
        Assert.Equal(_serverSettings.VendorName, objects[0].Value);
        Assert.Equal(_serverSettings.UserApplicationName, objects[6].Value);
    }

    [Fact]
    public async Task GenericExecute_DecodesFloats()
    {
        _model.Bind("HR:50", TagDataType.Float32, WordOrder.CDAB).Write(12.5f);
        var result = await _client.ExecuteAsync(new ModbusClientRequest
        {
            Function = ModbusClientFunction.ReadHoldingRegisters,
            Address = 50,
            Quantity = 2,
            DataType = TagDataType.Float32,
            WordOrder = WordOrder.CDAB,
        });
        Assert.True(result.Success, result.Message);
        Assert.Equal("12.5", Assert.Single(result.Rows).Value);
    }

    [Fact]
    public async Task GenericExecute_WritesTypedValues()
    {
        var result = await _client.ExecuteAsync(new ModbusClientRequest
        {
            Function = ModbusClientFunction.WriteMultipleRegisters,
            Address = 0,
            Values = "-1, 70000",
            DataType = TagDataType.Int32,
        });
        Assert.True(result.Success, result.Message);
        Assert.Equal(-1, _model.Bind("HR:0", TagDataType.Int32, WordOrder.ABCD).Read());
        Assert.Equal(70000, _model.Bind("HR:2", TagDataType.Int32, WordOrder.ABCD).Read());
    }

    [Fact]
    public async Task InvalidHeader_ClosesConnection()
    {
        using var raw = new TcpClient();
        await raw.ConnectAsync("127.0.0.1", _server.LocalEndPoint!.Port);
        var stream = raw.GetStream();
        await stream.WriteAsync(new byte[] { 0, 1, 0, 9, 0, 6, 1, 3, 0, 0, 0, 1 }); // protocol id 9
        var buffer = new byte[16];
        using var cts = new CancellationTokenSource(2000);
        // Server closes the socket: either a clean FIN (0 bytes) or a reset (unread bytes were pending).
        int n;
        try { n = await stream.ReadAsync(buffer, cts.Token); }
        catch (IOException) { n = 0; }
        Assert.Equal(0, n);
        Assert.Equal(1, _stats.Malformed);
    }

    [Fact]
    public async Task StopAndRestart()
    {
        await _server.StopAsync();
        Assert.Equal(ServerState.Stopped, _server.State);
        await _server.StartAsync();
        Assert.Equal(ServerState.Running, _server.State);
    }
}
