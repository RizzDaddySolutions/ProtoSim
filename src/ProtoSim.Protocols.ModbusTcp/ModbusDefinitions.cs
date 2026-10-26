namespace ProtoSim.Protocols.ModbusTcp;

public enum ModbusFunctionCode : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    Diagnostics = 0x08,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10,
    MaskWriteRegister = 0x16,
    ReadWriteMultipleRegisters = 0x17,
    EncapsulatedInterface = 0x2B,
}

public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
    Acknowledge = 0x05,
    ServerDeviceBusy = 0x06,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetFailedToRespond = 0x0B,
}

public enum ModbusAreaKind
{
    Coils,
    DiscreteInputs,
    InputRegisters,
    HoldingRegisters,
}

/// <summary>Raised by the request handler (server side) or by the client when a device answers with an exception.</summary>
public sealed class ModbusException(ModbusExceptionCode code, string? message = null)
    : Exception(message ?? $"Modbus exception {(byte)code:X2}: {ModbusNames.Describe(code)}")
{
    public ModbusExceptionCode Code { get; } = code;
}

public static class ModbusLimits
{
    public const int AddressSpace = 65536;
    public const int MaxReadBits = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteBits = 1968;
    public const int MaxWriteRegisters = 123;
    public const int MaxReadWriteWriteRegisters = 121;
    public const int MaxPduLength = 253;
}

public static class ModbusNames
{
    public static string Describe(byte functionCode) => (ModbusFunctionCode)functionCode switch
    {
        ModbusFunctionCode.ReadCoils => "Read Coils",
        ModbusFunctionCode.ReadDiscreteInputs => "Read Discrete Inputs",
        ModbusFunctionCode.ReadHoldingRegisters => "Read Holding Registers",
        ModbusFunctionCode.ReadInputRegisters => "Read Input Registers",
        ModbusFunctionCode.WriteSingleCoil => "Write Single Coil",
        ModbusFunctionCode.WriteSingleRegister => "Write Single Register",
        ModbusFunctionCode.Diagnostics => "Diagnostics",
        ModbusFunctionCode.WriteMultipleCoils => "Write Multiple Coils",
        ModbusFunctionCode.WriteMultipleRegisters => "Write Multiple Registers",
        ModbusFunctionCode.MaskWriteRegister => "Mask Write Register",
        ModbusFunctionCode.ReadWriteMultipleRegisters => "Read/Write Multiple Registers",
        ModbusFunctionCode.EncapsulatedInterface => "Read Device Identification",
        _ => $"Function 0x{functionCode:X2}",
    };

    public static string Describe(ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => "Illegal function",
        ModbusExceptionCode.IllegalDataAddress => "Illegal data address",
        ModbusExceptionCode.IllegalDataValue => "Illegal data value",
        ModbusExceptionCode.ServerDeviceFailure => "Server device failure",
        ModbusExceptionCode.Acknowledge => "Acknowledge",
        ModbusExceptionCode.ServerDeviceBusy => "Server device busy",
        ModbusExceptionCode.MemoryParityError => "Memory parity error",
        ModbusExceptionCode.GatewayPathUnavailable => "Gateway path unavailable",
        ModbusExceptionCode.GatewayTargetFailedToRespond => "Gateway target failed to respond",
        _ => "Unknown exception",
    };

    public static string Prefix(ModbusAreaKind kind) => kind switch
    {
        ModbusAreaKind.Coils => "CO",
        ModbusAreaKind.DiscreteInputs => "DI",
        ModbusAreaKind.InputRegisters => "IR",
        _ => "HR",
    };

    /// <summary>Modicon-style 1-based address: 00001, 10001, 30001, 40001 (6 digits above 9999).</summary>
    public static string Modicon(ModbusAreaKind kind, int index)
    {
        int prefix = kind switch
        {
            ModbusAreaKind.Coils => 0,
            ModbusAreaKind.DiscreteInputs => 1,
            ModbusAreaKind.InputRegisters => 3,
            _ => 4,
        };
        return index < 9999
            ? (prefix * 10000 + index + 1).ToString("D5")
            : (prefix * 100000 + index + 1).ToString("D6");
    }
}
