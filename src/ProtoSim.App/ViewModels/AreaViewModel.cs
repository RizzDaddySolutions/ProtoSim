using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Data;

namespace ProtoSim.App.ViewModels;

public enum RegisterFormat
{
    Unsigned,
    Signed,
    Hex,
    Binary,
    Ascii,
}

public interface IEditableRow
{
    bool IsEditing { get; set; }
}

/// <summary>Raw table view of one data area (e.g. Holding Registers) showing a window of addresses.</summary>
public sealed class AreaViewModel : ObservableObject
{
    public const int MaxRows = 5000;

    private int _startAddress;
    private int _rowCount = 200;
    private RegisterFormat _format = RegisterFormat.Unsigned;
    private string _fillValue = "0";
    private ObservableCollection<RegisterRowViewModel> _rows = [];

    public AreaViewModel(IDataArea area)
    {
        Area = area;
        Rebuild();
        PreviousPageCommand = new RelayCommand(() => StartAddress -= RowCount, () => StartAddress > 0);
        NextPageCommand = new RelayCommand(() => StartAddress += RowCount, () => StartAddress + RowCount < Area.Count);
        FillCommand = new RelayCommand(Fill);
        ClearCommand = new RelayCommand(() => ForEachVisible(_ => 0));
        RandomizeCommand = new RelayCommand(() => ForEachVisible(_ => IsBit ? (ushort)Random.Shared.Next(2) : (ushort)Random.Shared.Next(65536)));
        IncrementCommand = new RelayCommand(() => ForEachVisible(i => (ushort)(IsBit ? (i % 2) : i - StartAddress)));
    }

    public IDataArea Area { get; }
    public string Name => Area.Name;
    public bool IsBit => Area.Kind == DataElementKind.Bit;
    public bool IsWord => !IsBit;
    public static RegisterFormat[] Formats { get; } = Enum.GetValues<RegisterFormat>();

    public ObservableCollection<RegisterRowViewModel> Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand FillCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RandomizeCommand { get; }
    public ICommand IncrementCommand { get; }

    public int StartAddress
    {
        get => _startAddress;
        set
        {
            int v = Math.Clamp(value, 0, Math.Max(0, Area.Count - 1));
            if (SetProperty(ref _startAddress, v)) Rebuild();
        }
    }

    public int RowCount
    {
        get => _rowCount;
        set
        {
            int v = Math.Clamp(value, 1, MaxRows);
            if (SetProperty(ref _rowCount, v)) Rebuild();
        }
    }

    public RegisterFormat Format
    {
        get => _format;
        set { if (SetProperty(ref _format, value)) Refresh(force: true); }
    }

    public string FillValue { get => _fillValue; set => SetProperty(ref _fillValue, value); }

    public string RangeText => $"{Area.FormatAddress(_startAddress)} – {Area.FormatAddress(Math.Min(_startAddress + _rowCount, Area.Count) - 1)} of {Area.Count:N0}";

    private void Rebuild()
    {
        if (_startAddress >= Area.Count) _startAddress = Math.Max(0, Area.Count - 1);
        int end = Math.Min(_startAddress + _rowCount, Area.Count);
        var rows = new ObservableCollection<RegisterRowViewModel>();
        for (int i = _startAddress; i < end; i++) rows.Add(new RegisterRowViewModel(this, i));
        Rows = rows;
        Refresh(force: true);
        OnPropertyChanged(nameof(RangeText));
    }

    /// <summary>Called by the UI timer: pulls current values from the device memory.</summary>
    public void Refresh(bool force = false)
    {
        if (!force && Rows.Count > 0 && Rows[^1].Index >= Area.Count) { Rebuild(); return; } // area shrank
        var now = DateTime.UtcNow;
        foreach (var row in Rows)
            if (row.Index < Area.Count) row.Update(Area.Read(row.Index), now, force);
    }

    private void ForEachVisible(Func<int, ushort> value)
    {
        foreach (var row in Rows) Area.Write(row.Index, value(row.Index));
        Refresh();
    }

    private void Fill()
    {
        try
        {
            var v = Parse(_fillValue);
            ForEachVisible(_ => v);
        }
        catch (FormatException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Fill", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    internal string FormatValue(ushort raw) => IsBit ? (raw != 0 ? "1" : "0") : _format switch
    {
        RegisterFormat.Signed => ((short)raw).ToString(CultureInfo.InvariantCulture),
        RegisterFormat.Hex => $"0x{raw:X4}",
        RegisterFormat.Binary => string.Join(" ", Enumerable.Range(0, 4).Select(n => Convert.ToString((raw >> (12 - n * 4)) & 0xF, 2).PadLeft(4, '0'))),
        RegisterFormat.Ascii => $"{Printable((byte)(raw >> 8))}{Printable((byte)raw)}",
        _ => raw.ToString(CultureInfo.InvariantCulture),
    };

    private static char Printable(byte b) => b is >= 32 and < 127 ? (char)b : '·';

    internal ushort Parse(string text)
    {
        var s = (text ?? string.Empty).Trim();
        if (IsBit) return (bool)ValueConverter.Parse(s, TagDataType.Bool) ? (ushort)1 : (ushort)0;
        try
        {
            switch (_format)
            {
                case RegisterFormat.Hex:
                    var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
                    return ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                case RegisterFormat.Binary:
                    return Convert.ToUInt16(s.Replace(" ", "").Replace("_", ""), 2);
                case RegisterFormat.Ascii:
                    if (s.Length is < 1 or > 2) throw new FormatException("Enter one or two ASCII characters.");
                    return (ushort)((s[0] << 8) | (s.Length > 1 ? s[1] : 0));
            }
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException)
        {
            throw new FormatException($"'{s}' is not a valid {_format} value.");
        }
        // Unsigned / Signed: accept either sign, plus 0x hex.
        var v = ValueConverter.Parse(s, s.StartsWith('-') ? TagDataType.Int16 : TagDataType.UInt16);
        return v is short sh ? unchecked((ushort)sh) : (ushort)v;
    }
}

public sealed class RegisterRowViewModel(AreaViewModel owner, int index) : ObservableObject, IEditableRow
{
    private static readonly TimeSpan HighlightTime = TimeSpan.FromSeconds(1.5);
    private ushort _raw;
    private bool _initialized;
    private DateTime _changedAt;
    private bool _isChanged;
    private string _text = string.Empty;
    private string? _error;

    public int Index { get; } = index;
    public string Address { get; } = owner.Area.FormatAddress(index);
    public bool IsEditing { get; set; }

    public bool IsChanged { get => _isChanged; private set => SetProperty(ref _isChanged, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public string ValueText
    {
        get => _text;
        set
        {
            try
            {
                owner.Area.Write(Index, owner.Parse(value));
                Error = null;
            }
            catch (FormatException ex)
            {
                Error = ex.Message;
            }
            Update(owner.Area.Read(Index), DateTime.UtcNow, force: true);
        }
    }

    public bool BitValue
    {
        get => _raw != 0;
        set
        {
            owner.Area.Write(Index, value ? (ushort)1 : (ushort)0);
            Update(owner.Area.Read(Index), DateTime.UtcNow, force: false);
        }
    }

    internal void Update(ushort raw, DateTime now, bool force)
    {
        if (raw != _raw || !_initialized || force)
        {
            if (_initialized && raw != _raw) _changedAt = now;
            bool bitChanged = raw != _raw;
            _raw = raw;
            _initialized = true;
            if (!IsEditing)
            {
                var text = owner.FormatValue(raw);
                if (text != _text) { _text = text; OnPropertyChanged(nameof(ValueText)); }
            }
            if (bitChanged || force) OnPropertyChanged(nameof(BitValue));
        }
        IsChanged = now - _changedAt < HighlightTime;
    }
}
