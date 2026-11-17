using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.S7;

public enum S7CpuFamily
{
    S7_300,
    S7_400,
    S7_1200,
    S7_1500,
}

public enum S7CpuMode
{
    Run,
    Stop,
}

public sealed class S7ServerSettings : ITcpServerSettings
{
    [Display(Name = "Bind address", GroupName = "Network", Order = 1, Description = "IP address to listen on. 0.0.0.0 = all IPv4 interfaces, 127.0.0.1 = local only.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "TCP port", GroupName = "Network", Order = 2, Description = "ISO-on-TCP (RFC 1006) port. Real PLCs use 102; most clients can't change it.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 102;

    [Display(Name = "Max connections", GroupName = "Network", Order = 3)]
    [Range(1, 1000)]
    public int MaxConnections { get; set; } = 16;

    [Display(Name = "Idle timeout (s)", GroupName = "Network", Order = 4, Description = "Close connections that send nothing for this long. 0 = never.")]
    [Range(0, 86_400)]
    public int IdleTimeoutSeconds { get; set; }

    [Display(Name = "CPU family", GroupName = "CPU", Order = 10, Description = "Changes the order number and module names reported to clients.")]
    public S7CpuFamily CpuFamily { get; set; } = S7CpuFamily.S7_1500;

    [Display(Name = "Initial mode", GroupName = "CPU", Order = 11, Description = "Clients can switch RUN/STOP with PLC start/stop commands. Data access works in both modes.")]
    public S7CpuMode Mode { get; set; } = S7CpuMode.Run;

    [Display(Name = "Max PDU size", GroupName = "CPU", Order = 12, Description = "Largest PDU offered during setup communication (S7-300: 240, S7-1500: 960).")]
    [Range(240, 960)]
    public int MaxPduSize { get; set; } = 960;

    [Display(Name = "PLC name", GroupName = "CPU", Order = 13)]
    public string PlcName { get; set; } = "ProtoSim";

    [Display(Name = "Serial number", GroupName = "CPU", Order = 14)]
    public string SerialNumber { get; set; } = "S C-PSIM00001";

    [Display(Name = "Data blocks", GroupName = "Memory", Order = 20,
        Description = "Data block numbers and sizes in bytes, e.g. 1:1024, 2:256, 100:8192. Access to other DBs fails with 'object does not exist'. Reopen the project to show new DBs on the Memory tab.")]
    public string DataBlocks { get; set; } = "1:1024, 2:1024, 3:1024";

    [Display(Name = "Inputs (bytes)", GroupName = "Memory", Order = 21)]
    [Range(1, 65536)]
    public int InputBytes { get; set; } = 1024;

    [Display(Name = "Outputs (bytes)", GroupName = "Memory", Order = 22)]
    [Range(1, 65536)]
    public int OutputBytes { get; set; } = 1024;

    [Display(Name = "Flags / Merker (bytes)", GroupName = "Memory", Order = 23)]
    [Range(1, 65536)]
    public int FlagBytes { get; set; } = 4096;

    /// <summary>Parses <see cref="DataBlocks"/> into number → size.</summary>
    public static SortedDictionary<int, int> ParseDataBlocks(string? spec)
    {
        var result = new SortedDictionary<int, int>();
        foreach (var raw in (spec ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim().TrimStart('D', 'B', 'd', 'b');
            int colon = part.IndexOf(':');
            if (colon <= 0
                || !int.TryParse(part[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535
                || !int.TryParse(part[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size is < 1 or > 65536)
                throw new FormatException($"'{raw}' is not a valid data block (use number:size, e.g. 1:1024).");
            result[number] = size;
        }
        return result;
    }
}
