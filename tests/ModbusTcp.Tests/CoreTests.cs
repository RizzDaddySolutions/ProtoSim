using ProtoSim.Core.Data;
using ProtoSim.Core.Simulation;
using ProtoSim.Protocols.ModbusTcp;

namespace ProtoSim.ModbusTcp.Tests;

public class RegisterCodecTests
{
    [Theory]
    [InlineData(WordOrder.ABCD, 0x3F80, 0x0000)]
    [InlineData(WordOrder.CDAB, 0x0000, 0x3F80)]
    [InlineData(WordOrder.BADC, 0x803F, 0x0000)]
    [InlineData(WordOrder.DCBA, 0x0000, 0x803F)]
    public void Float32_UsesExpectedWordOrder(WordOrder order, int w0, int w1)
    {
        var words = RegisterCodec.Encode(1.0f, TagDataType.Float32, order);
        Assert.Equal(new[] { (ushort)w0, (ushort)w1 }, words);
        Assert.Equal(1.0f, RegisterCodec.Decode(words, TagDataType.Float32, order));
    }

    [Theory]
    [InlineData(TagDataType.Int16, "-1234")]
    [InlineData(TagDataType.UInt32, "4000000000")]
    [InlineData(TagDataType.Int32, "-2000000000")]
    [InlineData(TagDataType.Int64, "-9000000000000")]
    [InlineData(TagDataType.UInt64, "18000000000000000000")]
    [InlineData(TagDataType.Float64, "3.14159265358979")]
    public void RoundTrips_AllOrders(TagDataType type, string value)
    {
        foreach (var order in Enum.GetValues<WordOrder>())
        {
            var original = ValueConverter.Parse(value, type);
            var words = RegisterCodec.Encode(original, type, order);
            Assert.Equal(RegisterCodec.WordCount(type), words.Length);
            Assert.Equal(original, RegisterCodec.Decode(words, type, order));
        }
    }

    [Fact]
    public void Parse_HexIsReinterpretedAsRawBits()
    {
        Assert.Equal((short)-1, ValueConverter.Parse("0xFFFF", TagDataType.Int16));
        Assert.Throws<FormatException>(() => ValueConverter.Parse("70000", TagDataType.UInt16));
    }

    [Fact]
    public void FromDouble_ClampsIntegers()
    {
        Assert.Equal((ushort)65535, ValueConverter.FromDouble(1e9, TagDataType.UInt16));
        Assert.Equal((short)-32768, ValueConverter.FromDouble(-1e9, TagDataType.Int16));
        Assert.Equal((ushort)0, ValueConverter.FromDouble(-5, TagDataType.UInt16));
    }
}

public class ModbusAddressTests
{
    [Theory]
    [InlineData("HR:100", ModbusAreaKind.HoldingRegisters, 100, -1)]
    [InlineData("hr 5", ModbusAreaKind.HoldingRegisters, 5, -1)]
    [InlineData("IR0", ModbusAreaKind.InputRegisters, 0, -1)]
    [InlineData("CO:7", ModbusAreaKind.Coils, 7, -1)]
    [InlineData("DI:65535", ModbusAreaKind.DiscreteInputs, 65535, -1)]
    [InlineData("40001", ModbusAreaKind.HoldingRegisters, 0, -1)]
    [InlineData("400101", ModbusAreaKind.HoldingRegisters, 100, -1)]
    [InlineData("30010", ModbusAreaKind.InputRegisters, 9, -1)]
    [InlineData("00001", ModbusAreaKind.Coils, 0, -1)]
    [InlineData("10002", ModbusAreaKind.DiscreteInputs, 1, -1)]
    [InlineData("HR:10.15", ModbusAreaKind.HoldingRegisters, 10, 15)]
    public void Parses(string text, ModbusAreaKind area, int index, int bit)
    {
        var a = ModbusAddress.Parse(text);
        Assert.Equal(area, a.Area);
        Assert.Equal(index, a.Index);
        Assert.Equal(bit, a.Bit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("XX:1")]
    [InlineData("HR:70000")]
    [InlineData("40000")]   // Modicon is 1-based
    [InlineData("20001")]   // no area 2
    [InlineData("CO:1.2")]  // bits only on registers
    [InlineData("HR:1.16")]
    [InlineData("4001")]    // too short to be Modicon
    public void Rejects(string text)
    {
        Assert.False(ModbusAddress.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Modicon_SwitchesToSixDigitsAbove9999()
    {
        Assert.Equal("40001", ModbusNames.Modicon(ModbusAreaKind.HoldingRegisters, 0));
        Assert.Equal("49999", ModbusNames.Modicon(ModbusAreaKind.HoldingRegisters, 9998));
        Assert.Equal("410000", ModbusNames.Modicon(ModbusAreaKind.HoldingRegisters, 9999));
        Assert.Equal("00001", ModbusNames.Modicon(ModbusAreaKind.Coils, 0));
    }
}

public class TagBindingTests
{
    private readonly ModbusDataModel _model = new(new ModbusServerSettings());

    [Fact]
    public void Float32_WritesTwoRegisters()
    {
        var b = _model.Bind("HR:0", TagDataType.Float32, WordOrder.ABCD);
        b.Write("1.0");
        Assert.Equal((ushort)0x3F80, _model.HoldingRegisters.Read(0));
        Assert.Equal((ushort)0, _model.HoldingRegisters.Read(1));
        Assert.Equal(1.0f, b.Read());
    }

    [Fact]
    public void RegisterBit_SetsAndClearsOnlyThatBit()
    {
        _model.HoldingRegisters.Write(5, 0x00F0);
        var b = _model.Bind("HR:5.0", TagDataType.Bool, WordOrder.ABCD);
        b.Write(true);
        Assert.Equal((ushort)0x00F1, _model.HoldingRegisters.Read(5));
        _model.Bind("HR:5.4", TagDataType.Bool, WordOrder.ABCD).Write(false);
        Assert.Equal((ushort)0x00E1, _model.HoldingRegisters.Read(5));
    }

    [Fact]
    public void Coils_RequireBool()
    {
        Assert.Throws<FormatException>(() => _model.Bind("CO:0", TagDataType.UInt16, WordOrder.ABCD));
        var b = _model.Bind("CO:0", TagDataType.Bool, WordOrder.ABCD);
        b.Write("on");
        Assert.Equal((ushort)1, _model.Coils.Read(0));
    }
}

public class SimulationTests
{
    [Fact]
    public void Counter_IncrementsAndWraps()
    {
        var model = new ModbusDataModel(new ModbusServerSettings());
        var tag = new SimTag
        {
            Address = "HR:0",
            DataType = TagDataType.UInt16,
            Simulation = new SimulationSettings { Kind = SimulationKind.Counter, Min = 0, Max = 2, Step = 1, IntervalMs = 100 },
        };
        tag.Binding = model.Bind(tag.Address, tag.DataType, tag.WordOrder);

        SimulationEngine.Step(tag, 0.0);
        SimulationEngine.Step(tag, 0.05); // within interval: ignored
        Assert.Equal((ushort)1, model.HoldingRegisters.Read(0));
        SimulationEngine.Step(tag, 0.2);
        SimulationEngine.Step(tag, 0.4);
        Assert.Equal((ushort)0, model.HoldingRegisters.Read(0)); // 1 -> 2 -> wraps to 0
    }

    [Fact]
    public void Sine_StaysInRange()
    {
        var s = new SimulationSettings { Kind = SimulationKind.Sine, Min = 10, Max = 20, PeriodSeconds = 4 };
        for (double t = 0; t < 8; t += 0.1)
        {
            var v = s.Evaluate(t, 0, Random.Shared);
            Assert.InRange(v, 10, 20);
        }
        Assert.Equal(15, s.Evaluate(0, 0, Random.Shared), 6);
        Assert.Equal(20, s.Evaluate(1, 0, Random.Shared), 6);
    }
}
