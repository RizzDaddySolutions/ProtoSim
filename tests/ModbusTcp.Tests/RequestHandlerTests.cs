using ProtoSim.Protocols.ModbusTcp;

namespace ProtoSim.ModbusTcp.Tests;

public class RequestHandlerTests
{
    private readonly ModbusServerSettings _settings = new() { HoldingRegisterCount = 100, CoilCount = 100 };
    private readonly ModbusDataModel _model;
    private readonly ModbusRequestHandler _handler;

    public RequestHandlerTests()
    {
        _model = new ModbusDataModel(_settings);
        _handler = new ModbusRequestHandler(_model, _settings);
    }

    private byte[] Run(params byte[] pdu) => _handler.Process(pdu);

    private static void AssertException(byte[] resp, byte fc, ModbusExceptionCode code) =>
        Assert.Equal(new[] { (byte)(fc | 0x80), (byte)code }, resp);

    [Fact]
    public void ReadHoldingRegisters_ReturnsBigEndianWords()
    {
        _model.HoldingRegisters.Write(1, 0x1234);
        _model.HoldingRegisters.Write(2, 0xABCD);
        Assert.Equal(new byte[] { 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD }, Run(0x03, 0x00, 0x01, 0x00, 0x02));
    }

    [Fact]
    public void ReadCoils_PacksLsbFirst()
    {
        // Coils 0..9: 1,0,1,1,0,0,0,0, 1,1
        foreach (var i in new[] { 0, 2, 3, 8, 9 }) _model.Coils.Write(i, 1);
        Assert.Equal(new byte[] { 0x01, 0x02, 0b0000_1101, 0b0000_0011 }, Run(0x01, 0x00, 0x00, 0x00, 0x0A));
    }

    [Fact]
    public void Read_PastConfiguredSize_IsIllegalAddress() =>
        AssertException(Run(0x03, 0x00, 0x62, 0x00, 0x03), 0x03, ModbusExceptionCode.IllegalDataAddress); // 98 + 3 > 100

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void Read_BadQuantity_IsIllegalValue(int qty) =>
        AssertException(Run(0x03, 0x00, 0x00, 0x00, (byte)qty), 0x03, ModbusExceptionCode.IllegalDataValue);

    [Fact]
    public void UnknownFunction_IsIllegalFunction() =>
        AssertException(Run(0x41, 0x00), 0x41, ModbusExceptionCode.IllegalFunction);

    [Fact]
    public void WriteSingleCoil_EchoesAndRejectsBadValue()
    {
        Assert.Equal(new byte[] { 0x05, 0x00, 0x07, 0xFF, 0x00 }, Run(0x05, 0x00, 0x07, 0xFF, 0x00));
        Assert.Equal((ushort)1, _model.Coils.Read(7));
        AssertException(Run(0x05, 0x00, 0x07, 0x12, 0x34), 0x05, ModbusExceptionCode.IllegalDataValue);
    }

    [Fact]
    public void WriteMultipleRegisters_Writes()
    {
        var resp = Run(0x10, 0x00, 0x0A, 0x00, 0x02, 0x04, 0x00, 0x01, 0x00, 0x02);
        Assert.Equal(new byte[] { 0x10, 0x00, 0x0A, 0x00, 0x02 }, resp);
        Assert.Equal((ushort)1, _model.HoldingRegisters.Read(10));
        Assert.Equal((ushort)2, _model.HoldingRegisters.Read(11));
    }

    [Fact]
    public void WriteMultipleRegisters_ByteCountMismatch_IsIllegalValue() =>
        AssertException(Run(0x10, 0x00, 0x0A, 0x00, 0x02, 0x03, 0x00, 0x01, 0x00), 0x10, ModbusExceptionCode.IllegalDataValue);

    [Fact]
    public void WriteMultipleCoils_Writes()
    {
        Run(0x0F, 0x00, 0x00, 0x00, 0x0A, 0x02, 0b0000_0101, 0b0000_0010);
        var expected = new ushort[] { 1, 0, 1, 0, 0, 0, 0, 0, 0, 1 };
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], _model.Coils.Read(i));
    }

    [Fact]
    public void MaskWrite_FollowsSpecFormula()
    {
        // Example from the Modbus spec: current 0x12, AND 0xF2, OR 0x25 -> 0x17
        _model.HoldingRegisters.Write(4, 0x12);
        Run(0x16, 0x00, 0x04, 0x00, 0xF2, 0x00, 0x25);
        Assert.Equal((ushort)0x17, _model.HoldingRegisters.Read(4));
    }

    [Fact]
    public void ReadWriteMultiple_WritesBeforeReading()
    {
        var resp = Run(0x17, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01, 0x00, 0x01, 0x02, 0x00, 0x2A);
        Assert.Equal(new byte[] { 0x17, 0x04, 0x00, 0x00, 0x00, 0x2A }, resp);
    }

    [Fact]
    public void AllowWritesOff_RejectsWrites()
    {
        _settings.AllowWrites = false;
        AssertException(Run(0x06, 0x00, 0x00, 0x00, 0x01), 0x06, ModbusExceptionCode.IllegalFunction);
        Assert.Equal(0x03, Run(0x03, 0x00, 0x00, 0x00, 0x01)[0]);
    }

    [Fact]
    public void Diagnostics_EchoesQueryData() =>
        Assert.Equal(new byte[] { 0x08, 0x00, 0x00, 0xBE, 0xEF }, Run(0x08, 0x00, 0x00, 0xBE, 0xEF));

    [Fact]
    public void DeviceIdentification_Basic()
    {
        var resp = Run(0x2B, 0x0E, 0x01, 0x00);
        Assert.Equal(new byte[] { 0x2B, 0x0E, 0x01, 0x82, 0x00, 0x00, 0x03 }, resp[..7]);
        Assert.Equal(0, resp[7]);                      // first object id
        Assert.Equal(_settings.VendorName.Length, resp[8]);
    }

    [Fact]
    public void DeviceIdentification_SingleObjectOutOfRange_IsIllegalAddress() =>
        AssertException(Run(0x2B, 0x0E, 0x04, 0x07), 0x2B, ModbusExceptionCode.IllegalDataAddress);
}
