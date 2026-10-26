namespace ProtoSim.Core.Data;

/// <summary>Logical data type of a tag.</summary>
public enum TagDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    /// <summary>Text. Supported by node-based protocols (e.g. OPC UA), not by register-based ones.</summary>
    String,
}

/// <summary>
/// Byte/word order for values that span several 16-bit registers.
/// Letters name the bytes of the big-endian value (A = most significant).
/// </summary>
public enum WordOrder
{
    /// <summary>Big-endian (Modbus standard).</summary>
    ABCD,
    /// <summary>Word swap.</summary>
    CDAB,
    /// <summary>Byte swap within each word.</summary>
    BADC,
    /// <summary>Little-endian.</summary>
    DCBA,
}

public enum DataElementKind
{
    Bit,
    Word,
}
