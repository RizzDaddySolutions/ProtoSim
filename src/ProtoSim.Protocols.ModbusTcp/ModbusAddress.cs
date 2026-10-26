using System.Globalization;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>
/// A parsed tag address. Accepted forms:
/// <list type="bullet">
/// <item>Prefixed, 0-based: HR:100, IR5, CO:0, DI:7 (optional ".bit" on registers: HR:10.3)</item>
/// <item>Modicon, 1-based: 00001, 10001, 30001, 40001 or 6-digit 400001</item>
/// </list>
/// </summary>
public readonly record struct ModbusAddress(ModbusAreaKind Area, int Index, int Bit = -1)
{
    public const string Help =
        "HR:0, IR:0, CO:0, DI:0 (0-based offsets) · 40001 / 400001 (Modicon, 1-based) · HR:10.3 (bit 3 of a register)";

    private static readonly (string Prefix, ModbusAreaKind Kind)[] Prefixes =
    [
        ("HR", ModbusAreaKind.HoldingRegisters),
        ("IR", ModbusAreaKind.InputRegisters),
        ("DI", ModbusAreaKind.DiscreteInputs),
        ("CO", ModbusAreaKind.Coils),
        ("C", ModbusAreaKind.Coils),
    ];

    public bool IsBitArea => Area is ModbusAreaKind.Coils or ModbusAreaKind.DiscreteInputs;

    public static ModbusAddress Parse(string text) =>
        TryParse(text, out var a, out var error) ? a : throw new FormatException(error);

    public static bool TryParse(string? text, out ModbusAddress address, out string? error)
    {
        address = default;
        error = null;
        var s = (text ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", "");
        if (s.Length == 0) { error = "Address is empty."; return false; }

        int bit = -1;
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            if (!int.TryParse(s.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out bit) || bit > 15)
            {
                error = "Bit number must be 0-15.";
                return false;
            }
            s = s[..dot];
        }

        ModbusAreaKind kind;
        int index;
        var prefix = Prefixes.FirstOrDefault(p => s.StartsWith(p.Prefix, StringComparison.Ordinal) && s.Length > p.Prefix.Length);
        if (prefix.Prefix != null)
        {
            kind = prefix.Kind;
            var rest = s[prefix.Prefix.Length..].TrimStart(':');
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index >= ModbusLimits.AddressSpace)
            {
                error = $"Offset '{rest}' must be 0-65535.";
                return false;
            }
        }
        else if (s.Length is 5 or 6 && s.All(char.IsAsciiDigit))
        {
            kind = s[0] switch
            {
                '0' => ModbusAreaKind.Coils,
                '1' => ModbusAreaKind.DiscreteInputs,
                '3' => ModbusAreaKind.InputRegisters,
                '4' => ModbusAreaKind.HoldingRegisters,
                _ => (ModbusAreaKind)(-1),
            };
            if ((int)kind < 0) { error = $"'{s[0]}' is not a Modicon area prefix (use 0, 1, 3 or 4)."; return false; }
            index = int.Parse(s.AsSpan(1), CultureInfo.InvariantCulture) - 1;
            if (index < 0 || index >= ModbusLimits.AddressSpace) { error = "Modicon address is out of range."; return false; }
        }
        else
        {
            error = $"Unrecognized address '{text}'. Use {Help}.";
            return false;
        }

        if (bit >= 0 && kind is ModbusAreaKind.Coils or ModbusAreaKind.DiscreteInputs)
        {
            error = "Bit numbers are only valid on registers.";
            return false;
        }

        address = new ModbusAddress(kind, index, bit);
        return true;
    }

    public override string ToString() =>
        $"{ModbusNames.Prefix(Area)}:{Index}" + (Bit >= 0 ? $".{Bit}" : string.Empty);
}
