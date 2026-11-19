using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.ModbusTcp;

namespace ProtoSim.ModbusTcp.Tests;

/// <summary>The product Modbus TCP client against the simulated server, including tags whose Writeable box is cleared.</summary>
public sealed class ClientAndWriteableTests : IAsyncLifetime
{
    private readonly ModbusServerSettings _serverSettings = new() { BindAddress = "127.0.0.1", Port = 0 };
    private readonly TrafficLog _log = new();
    private ModbusDataModel _model = null!;
    private ModbusTcpServer _server = null!;
    private ModbusTcpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _model = new ModbusDataModel(_serverSettings);
        _server = new ModbusTcpServer(_serverSettings, _model, new ServerContext(_log, new FaultSettings(), new ServerStatistics()));
        await _server.StartAsync(TestContext.Current.CancellationToken);
        _client = new ModbusTcpClient(new ModbusClientSettings { Port = _server.LocalEndPoint!.Port, ResponseTimeoutMs = 500 }, new ClientContext(new TrafficLog()));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Execute_DecodesTypedRows()
    {
        var tag = _model.Bind("HR:10", TagDataType.Float32, WordOrder.ABCD);
        tag.Write(12.5f);
        var r = await _client.ExecuteAsync(new ModbusClientRequest { Function = ModbusClientFunction.ReadHoldingRegisters, Address = 10, Quantity = 2, DataType = TagDataType.Float32 },
            TestContext.Current.CancellationToken);
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, r.Rows.Count); // Quantity counts Float32 values: 4 registers
        Assert.Equal("HR:10 (40011)", r.Rows[0].Address);
        Assert.Equal(12.5f, r.Rows[0].Typed);
    }

    [Fact]
    public async Task WriteToReadOnlyTag_IsRejected_OthersStillWork()
    {
        var locked = _model.Bind("HR:20", TagDataType.UInt32, WordOrder.ABCD); // registers 20 and 21
        locked.RemoteWritable = false;
        locked.Write(7u);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => _client.WriteSingleRegisterAsync(1, 21, 99, TestContext.Current.CancellationToken));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.Code);
        ex = await Assert.ThrowsAsync<ModbusException>(() => _client.WriteMultipleRegistersAsync(1, 18, [1, 2, 3], TestContext.Current.CancellationToken));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.Code);
        Assert.Equal(7u, locked.Read());

        await _client.WriteMultipleRegistersAsync(1, 22, [5, 6], TestContext.Current.CancellationToken); // next to it: fine
        Assert.Equal((ushort)5, _model.HoldingRegisters.Read(22));

        locked.RemoteWritable = true;
        await _client.WriteSingleRegisterAsync(1, 21, 99, TestContext.Current.CancellationToken);
        Assert.Equal((ushort)99, _model.HoldingRegisters.Read(21));
    }

    [Fact]
    public async Task ReadOnlyCoil_RejectsWrite_UntilBindingDisposed()
    {
        var coil = _model.Bind("CO:3", TagDataType.Bool, WordOrder.ABCD);
        coil.RemoteWritable = false;
        await Assert.ThrowsAsync<ModbusException>(() => _client.WriteSingleCoilAsync(1, 3, true, TestContext.Current.CancellationToken));
        coil.Dispose(); // removing the tag lifts the protection
        await _client.WriteSingleCoilAsync(1, 3, true, TestContext.Current.CancellationToken);
        Assert.Equal((ushort)1, _model.Coils.Read(3));
    }

    [Fact]
    public async Task LocalWritesAreNeverBlocked()
    {
        var tag = _model.Bind("HR:30", TagDataType.UInt16, WordOrder.ABCD);
        tag.RemoteWritable = false;
        tag.Write((ushort)42);
        var regs = await _client.ReadHoldingRegistersAsync(1, 30, 1, TestContext.Current.CancellationToken);
        Assert.Equal((ushort)42, regs[0]);
    }
}
