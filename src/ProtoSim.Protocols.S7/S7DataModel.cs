using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.S7;

/// <summary>S7 area codes used on the wire.</summary>
internal enum S7Area : byte
{
    Peripheral = 0x80,
    Inputs = 0x81,
    Outputs = 0x82,
    Flags = 0x83,
    DataBlock = 0x84,
    InstanceDataBlock = 0x85,
    Counters = 0x1C,
    Timers = 0x1D,
}

/// <summary>Memory of a simulated S7 CPU: inputs, outputs, flags and data blocks (all byte-addressed).</summary>
public sealed class S7DataModel : IDeviceDataModel
{
    internal const int MaxAreaBytes = 65536;

    private readonly S7ServerSettings _settings;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<int, ByteArea> _dbs = new();

    public S7DataModel(S7ServerSettings settings)
    {
        _settings = settings;
        Inputs = new ByteArea(_sync, "Inputs (I)", "I", MaxAreaBytes, true, o => $"IW{o}", () => _settings.InputBytes);
        Outputs = new ByteArea(_sync, "Outputs (Q)", "Q", MaxAreaBytes, true, o => $"QW{o}", () => _settings.OutputBytes);
        Flags = new ByteArea(_sync, "Flags (M)", "M", MaxAreaBytes, true, o => $"MW{o}", () => _settings.FlagBytes);

        var areas = new List<IDataArea> { Inputs, Outputs, Flags };
        SortedDictionary<int, int> dbs;
        try { dbs = S7ServerSettings.ParseDataBlocks(settings.DataBlocks); }
        catch (FormatException) { dbs = []; }
        foreach (var number in dbs.Keys) areas.Add(GetOrCreateDb(number));
        Areas = areas;
    }

    public ByteArea Inputs { get; }
    public ByteArea Outputs { get; }
    public ByteArea Flags { get; }
    public IReadOnlyList<IDataArea> Areas { get; }

    internal object Sync => _sync;
    internal S7ServerSettings Settings => _settings;

    /// <summary>A configured data block, or null when it doesn't exist in the current settings.</summary>
    public ByteArea? GetDataBlock(int number)
    {
        SortedDictionary<int, int> dbs;
        try { dbs = S7ServerSettings.ParseDataBlocks(_settings.DataBlocks); }
        catch (FormatException) { return _dbs.GetValueOrDefault(number); }
        return dbs.ContainsKey(number) ? GetOrCreateDb(number) : null;
    }

    private ByteArea GetOrCreateDb(int number) => _dbs.GetOrAdd(number, n => new ByteArea(_sync, $"DB{n}", $"DB{n}", MaxAreaBytes, true,
        o => $"DB{n}.DBW{o}",
        () => { try { return S7ServerSettings.ParseDataBlocks(_settings.DataBlocks).GetValueOrDefault(n); } catch (FormatException) { return 0; } }));

    internal ByteArea? GetArea(S7Area area, int db) => area switch
    {
        S7Area.Inputs => Inputs,
        S7Area.Outputs => Outputs,
        S7Area.Flags => Flags,
        S7Area.DataBlock or S7Area.InstanceDataBlock => GetDataBlock(db),
        _ => null,
    };

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var a = S7Address.Parse(address);
        var area = a.Area == S7Area.DataBlock
            ? GetDataBlock(a.DbNumber) ?? throw new FormatException($"DB{a.DbNumber} is not configured; add it under Data blocks in the server settings.")
            : GetArea(a.Area, 0)!;

        if (a.Kind == 'S' && type != TagDataType.String) throw new FormatException("DBS/string addresses need data type String.");
        if (a.Kind != 'S' && type == TagDataType.String) throw new FormatException("String tags use the DBS address form, e.g. DB1.DBS10.20 (offset 10, max length 20).");
        if (a.Kind == 'X' && type != TagDataType.Bool) throw new FormatException("Bit addresses (e.g. M0.0 or DB1.DBX0.0) need data type Bool.");
        if (a.Kind != 'X' && type == TagDataType.Bool) throw new FormatException("Bool tags use bit addresses, e.g. M0.0 or DB1.DBX0.0.");
        return new ByteAreaBinding(area, a.Offset, a.Kind == 'X' ? a.Bit : -1, type, a.Kind == 'S' ? a.StringLength : 254);
    }
}

/// <summary>A parsed S7 tag address such as DB1.DBD4, MW10, I0.3 or DB2.DBS0.20.</summary>
internal readonly record struct S7Address(S7Area Area, int DbNumber, char Kind, int Offset, int Bit, int StringLength)
{
    public const string Help =
        "Siemens absolute addresses. Data blocks: DB1.DBX0.0 (bit), DB1.DBB0 (byte), DB1.DBW2, DB1.DBD4, DB1.DBS10.20 (STRING, max 20 chars). " +
        "Inputs I/E, outputs Q/A and flags M: I0.0, QB1, MW10, MD20. The data type decides how many bytes are used, so DB1.DBD0 can hold a Float32 or an Int32. " +
        "Values are big-endian like a real CPU.";

    private static readonly Regex DbForm = new(@"^DB(\d+)\.DB([XBWDLS])(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AreaForm = new(@"^([IEQAM])([XBWDL])?(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static S7Address Parse(string text)
    {
        var s = (text ?? string.Empty).Trim().Replace(" ", "");
        var m = DbForm.Match(s);
        if (m.Success)
        {
            int db = ParseInt(m.Groups[1].Value);
            if (db is < 1 or > 65535) throw new FormatException("DB numbers run from 1 to 65535.");
            char kind = char.ToUpperInvariant(m.Groups[2].Value[0]);
            int offset = ParseInt(m.Groups[3].Value);
            return Finish(S7Area.DataBlock, db, kind, offset, m.Groups[4], s);
        }

        m = AreaForm.Match(s);
        if (m.Success)
        {
            var area = char.ToUpperInvariant(m.Groups[1].Value[0]) switch
            {
                'I' or 'E' => S7Area.Inputs,
                'Q' or 'A' => S7Area.Outputs,
                _ => S7Area.Flags,
            };
            char kind = m.Groups[2].Success ? char.ToUpperInvariant(m.Groups[2].Value[0]) : 'X';
            int offset = ParseInt(m.Groups[3].Value);
            return Finish(area, 0, kind, offset, m.Groups[4], s);
        }

        throw new FormatException($"'{text}' is not an S7 address. {Help}");
    }

    private static S7Address Finish(S7Area area, int db, char kind, int offset, Group suffix, string text)
    {
        if (offset >= S7DataModel.MaxAreaBytes) throw new FormatException($"Byte offset {offset} is out of range.");
        switch (kind)
        {
            case 'X':
                if (!suffix.Success) throw new FormatException($"'{text}' needs a bit number, e.g. {text}.0.");
                int bit = ParseInt(suffix.Value);
                if (bit > 7) throw new FormatException("Bit numbers run from 0 to 7.");
                return new S7Address(area, db, 'X', offset, bit, 0);
            case 'S':
                int len = suffix.Success ? ParseInt(suffix.Value) : 254;
                if (len is < 1 or > 254) throw new FormatException("S7 STRING length must be 1-254.");
                return new S7Address(area, db, 'S', offset, -1, len);
            default:
                if (suffix.Success) throw new FormatException($"'{text}': only bit (X) addresses take a .bit suffix.");
                return new S7Address(area, db, kind, offset, -1, 0);
        }
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"'{s}' is not a number.");

    public static string Format(S7Area area, int db, int byteOffset, int bit, int count, byte transportSize)
    {
        string prefix = area switch
        {
            S7Area.Inputs => "I",
            S7Area.Outputs => "Q",
            S7Area.Flags => "M",
            S7Area.DataBlock or S7Area.InstanceDataBlock => $"DB{db}.DB",
            S7Area.Counters => "C",
            S7Area.Timers => "T",
            _ => $"area 0x{(byte)area:X2} ",
        };
        if (transportSize == 1) return $"{prefix}X{byteOffset}.{bit}";
        return $"{prefix}B{byteOffset}" + (count > 1 ? $" ×{count}" : "");
    }
}
