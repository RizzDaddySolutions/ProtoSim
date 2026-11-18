using System.IO.Ports;
using System.Net.Sockets;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.ModbusTcp;

namespace ProtoSim.ModbusTcp.Tests;

public sealed class RtuFramingTests
{
    [Fact]
    public void CrcMatchesReferenceFrame()
    {
        // Read 10 holding registers from unit 1, address 0: the classic reference frame ends C5 CD.
        var adu = ModbusRtu.BuildAdu(1, [0x03, 0x00, 0x00, 0x00, 0x0A]);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }, adu);
        Assert.True(ModbusRtu.CheckCrc(adu));
        adu[3] ^= 1;
        Assert.False(ModbusRtu.CheckCrc(adu));
    }

    [Theory]
    [InlineData(new byte[] { 1 }, 0)]
    [InlineData(new byte[] { 1, 3 }, 8)]
    [InlineData(new byte[] { 1, 6 }, 8)]
    [InlineData(new byte[] { 1, 16, 0, 0, 0, 2 }, 0)]
    [InlineData(new byte[] { 1, 16, 0, 0, 0, 2, 4 }, 13)]
    [InlineData(new byte[] { 1, 15, 0, 0, 0, 9, 2 }, 11)]
    [InlineData(new byte[] { 1, 22 }, 10)]
    [InlineData(new byte[] { 1, 23, 0, 0, 0, 1, 0, 0, 0, 1, 2 }, 15)]
    [InlineData(new byte[] { 1, 43 }, 7)]
    [InlineData(new byte[] { 1, 0x41 }, -1)]
    public void PredictsRequestLength(byte[] start, int expected) =>
        Assert.Equal(expected, ModbusRtu.ExpectedRequestLength(start));

    [Fact]
    public void AssemblerJoinsChunksAndSplitsBackToBackFrames()
    {
        var a = ModbusRtu.BuildAdu(1, [0x03, 0x00, 0x10, 0x00, 0x02]);
        var b = ModbusRtu.BuildAdu(2, [0x06, 0x00, 0x01, 0x12, 0x34]);
        var asm = new RtuFrameAssembler();

        asm.Append(a.AsSpan(0, 3));
        Assert.Null(asm.TryTakeComplete());
        asm.Append([.. a.AsSpan(3), .. b]);
        Assert.Equal(a, asm.TryTakeComplete());
        Assert.Equal(b, asm.TryTakeComplete());
        Assert.Null(asm.TryTakeComplete());
        Assert.Equal(0, asm.Count);
    }

    [Fact]
    public void SerialGapFollowsBaudRate()
    {
        Assert.Equal(5, new ModbusRtuSerialSettings { BaudRate = 9600 }.EffectiveGapMs());   // 3.5 × 11 bits ≈ 4.0 ms
        Assert.Equal(2, new ModbusRtuSerialSettings { BaudRate = 115200 }.EffectiveGapMs()); // fixed 1.75 ms, at least 2
        Assert.Equal(20, new ModbusRtuSerialSettings { FrameGapMs = 20 }.EffectiveGapMs());
    }

    [Fact]
    public async Task SerialStartFailsClearlyForMissingPort()
    {
        var settings = new ModbusRtuSerialSettings { PortName = "COM199" };
        var server = new ModbusRtuSerialServer(settings, new ModbusDataModel(settings), new ServerContext(new TrafficLog(), new FaultSettings(), new ServerStatistics()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ServerState.Faulted, server.State);
        Assert.Contains("COM199", server.LastError);
        await server.DisposeAsync();
    }

    [Fact]
    public void SerialPluginDefaultsToStandardFraming()
    {
        var s = (ModbusRtuSerialSettings)new ModbusRtuSerialPlugin().CreateServerSettings([]);
        Assert.Equal(19200, s.BaudRate);
        Assert.Equal(Parity.Even, s.Parity);
        Assert.Equal(8, s.DataBits);
        Assert.Equal("1", s.UnitIds);
        Assert.True(s.TreatUnitZeroAsBroadcast);
    }
}

/// <summary>RTU-over-TCP server against a raw socket client.</summary>
public sealed class RtuOverTcpTests : IAsyncLifetime
{
    private readonly ModbusRtuOverTcpSettings _settings = new() { BindAddress = "127.0.0.1", Port = 0, UnitIds = "1,5", FrameGapMs = 30 };
    private readonly FaultSettings _faults = new();
    private readonly ServerStatistics _stats = new();
    private ModbusDataModel _model = null!;
    private ModbusRtuOverTcpServer _server = null!;
    private TcpClient _client = null!;
    private NetworkStream _stream = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _model = new ModbusDataModel(_settings);
        _server = new ModbusRtuOverTcpServer(_settings, _model, new ServerContext(new TrafficLog(), _faults, _stats));
        await _server.StartAsync(Ct);
        _client = new TcpClient();
        await _client.ConnectAsync("127.0.0.1", _server.LocalEndPoint!.Port, Ct);
        _stream = _client.GetStream();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    /// <summary>Reads one response; null when nothing arrives within the timeout.</summary>
    private async Task<byte[]?> ReceiveAsync(int timeoutMs = 500)
    {
        var buffer = new byte[300];
        int total = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            while (true)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(total), cts.Token);
                if (n == 0) return null;
                total += n;
                // Responses: exception = 5 bytes; reads have a byte count; writes echo 8 bytes.
                if (total >= 3)
                {
                    int expected = (buffer[1] & 0x80) != 0 ? 5 : buffer[1] is 1 or 2 or 3 or 4 or 23 ? 5 + buffer[2] : 8;
                    if (total >= expected) return buffer[..expected];
                }
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private Task SendAsync(byte[] adu) => _stream.WriteAsync(adu, Ct).AsTask();

    [Fact]
    public async Task ReadsAndWritesRegistersWithValidCrc()
    {
        _model.HoldingRegisters.Write(10, 0x1234);
        _model.HoldingRegisters.Write(11, 0xABCD);

        var request = ModbusRtu.BuildAdu(1, [0x03, 0x00, 0x0A, 0x00, 0x02]);
        await SendAsync(request[..4]);
        await Task.Delay(10, Ct);
        await SendAsync(request[4..]);
        var response = await ReceiveAsync();
        Assert.NotNull(response);
        Assert.True(ModbusRtu.CheckCrc(response));
        Assert.Equal(new byte[] { 0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD }, response[..^2]);

        await SendAsync(ModbusRtu.BuildAdu(5, [0x10, 0x00, 0x20, 0x00, 0x02, 0x04, 0x00, 0x07, 0x00, 0x08]));
        response = await ReceiveAsync();
        Assert.Equal(new byte[] { 0x05, 0x10, 0x00, 0x20, 0x00, 0x02 }, response![..^2]);
        Assert.Equal((ushort)8, _model.HoldingRegisters.Read(0x21));
    }

    [Fact]
    public async Task IgnoresBadCrcAndOtherUnitsThenKeepsWorking()
    {
        var bad = ModbusRtu.BuildAdu(1, [0x03, 0x00, 0x00, 0x00, 0x01]);
        bad[^1] ^= 0xFF;
        await SendAsync(bad);
        Assert.Null(await ReceiveAsync(200));

        await SendAsync(ModbusRtu.BuildAdu(9, [0x03, 0x00, 0x00, 0x00, 0x01]));
        Assert.Null(await ReceiveAsync(200));

        await SendAsync(ModbusRtu.BuildAdu(1, [0x03, 0x00, 0x00, 0x00, 0x01]));
        Assert.NotNull(await ReceiveAsync());
        Assert.True(_stats.Malformed >= 1);
        Assert.True(_stats.Dropped >= 1);
    }

    [Fact]
    public async Task UnknownFunctionIsFramedByGapAndRejected()
    {
        await SendAsync(ModbusRtu.BuildAdu(1, [0x41, 0x01, 0x02]));
        var response = await ReceiveAsync();
        Assert.Equal(new byte[] { 0x01, 0xC1, (byte)ModbusExceptionCode.IllegalFunction }, response![..^2]);
    }

    [Fact]
    public async Task BroadcastWritesWithoutReplying()
    {
        await SendAsync(ModbusRtu.BuildAdu(0, [0x06, 0x00, 0x05, 0x00, 0x2A]));
        Assert.Null(await ReceiveAsync(200));
        Assert.Equal((ushort)42, _model.HoldingRegisters.Read(5));
    }

    [Fact]
    public async Task InjectsConfiguredException()
    {
        _faults.ErrorResponsePercent = 100;
        _faults.ErrorCode = 6;
        await SendAsync(ModbusRtu.BuildAdu(1, [0x03, 0x00, 0x00, 0x00, 0x01]));
        var response = await ReceiveAsync();
        Assert.Equal(new byte[] { 0x01, 0x83, 0x06 }, response![..^2]);
        Assert.True(ModbusRtu.CheckCrc(response));
    }
}
