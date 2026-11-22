namespace ProtoSim.Core.Protocols;

/// <summary>Serial port naming per operating system.</summary>
public static class SerialPorts
{
    /// <summary>A sensible default port name: COM1, /dev/ttyUSB0 or /dev/cu.usbserial.</summary>
    public static string DefaultName =>
        OperatingSystem.IsWindows() ? "COM1" : OperatingSystem.IsMacOS() ? "/dev/cu.usbserial" : "/dev/ttyUSB0";

    public const string Help = "Serial port name: COM3 on Windows, /dev/ttyUSB0 on Linux, /dev/cu.usbserial-XXXX on macOS.";
}
