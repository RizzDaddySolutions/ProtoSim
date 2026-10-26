using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Diagnostics;
using Microsoft.Win32;

namespace ProtoSim.App.ViewModels;

public sealed class TrafficEntryViewModel(TrafficEntry entry)
{
    public TrafficEntry Entry { get; } = entry;
    public string Time => Entry.Timestamp.ToString("HH:mm:ss.fff");
    public TrafficDirection Direction => Entry.Direction;
    public string DirectionText => Entry.Direction switch
    {
        TrafficDirection.Rx => "RX",
        TrafficDirection.Tx => "TX",
        TrafficDirection.Warning => "WARN",
        TrafficDirection.Error => "ERROR",
        _ => "INFO",
    };
    public string Remote => Entry.Remote;
    public string Summary => Entry.Summary;
    public string Hex => _hex ??= Core.Diagnostics.Hex.Format(Entry.Data);
    public int Length => Entry.Data?.Length ?? 0;
    private string? _hex;
}

/// <summary>Displays a <see cref="TrafficLog"/>: batching, filtering, pause and export.</summary>
public sealed class LogViewModel : ObservableObject
{
    private readonly TrafficLog _source;
    private readonly List<TrafficEntry> _buffer = new(512);
    private bool _isPaused;
    private bool _autoScroll = true;
    private string _filterText = string.Empty;
    private bool _showFrames = true;
    private bool _showEvents = true;

    public LogViewModel(TrafficLog source)
    {
        _source = source;
        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = Filter;
        ClearCommand = new RelayCommand(Clear);
        ExportCommand = new RelayCommand(Export, () => Entries.Count > 0);
    }

    public ObservableCollection<TrafficEntryViewModel> Entries { get; } = [];
    public ICollectionView View { get; }
    public int MaxEntries { get; set; } = 5000;

    public ICommand ClearCommand { get; }
    public ICommand ExportCommand { get; }

    /// <summary>Raised after new entries were appended (the view uses it for auto-scroll).</summary>
    public event EventHandler? EntriesAppended;

    public bool IsCapturing
    {
        get => _source.Enabled;
        set { _source.Enabled = value; OnPropertyChanged(); }
    }

    public bool IsPaused { get => _isPaused; set => SetProperty(ref _isPaused, value); }
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }
    public bool ShowFrames { get => _showFrames; set { if (SetProperty(ref _showFrames, value)) View.Refresh(); } }
    public bool ShowEvents { get => _showEvents; set { if (SetProperty(ref _showEvents, value)) View.Refresh(); } }

    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value ?? string.Empty)) View.Refresh(); }
    }

    public void Drain()
    {
        if (_isPaused) return;
        _buffer.Clear();
        _source.Drain(_buffer, 2000);
        if (_buffer.Count == 0) return;

        foreach (var e in _buffer) Entries.Add(new TrafficEntryViewModel(e));
        int excess = Entries.Count - MaxEntries;
        for (int i = 0; i < excess; i++) Entries.RemoveAt(0);
        EntriesAppended?.Invoke(this, EventArgs.Empty);
    }

    private bool Filter(object o)
    {
        var e = (TrafficEntryViewModel)o;
        bool isFrame = e.Direction is TrafficDirection.Rx or TrafficDirection.Tx;
        if (isFrame && !_showFrames) return false;
        if (!isFrame && !_showEvents) return false;
        if (_filterText.Length == 0) return true;
        return e.Summary.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
               || e.Remote.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
               || e.Hex.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void Clear() => Entries.Clear();

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export traffic log",
            Filter = "Text log (*.log)|*.log|CSV (*.csv)|*.csv",
            FileName = $"traffic-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        if (dlg.ShowDialog() != true) return;

        bool csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        if (csv) sb.AppendLine("Time,Direction,Remote,Summary,Bytes");
        foreach (TrafficEntryViewModel e in View)
        {
            if (csv)
                sb.AppendLine(string.Join(",", Csv(e.Entry.Timestamp.ToString("O")), e.DirectionText, Csv(e.Remote), Csv(e.Summary), Csv(e.Hex)));
            else
                sb.AppendLine($"{e.Entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}  {e.DirectionText,-5} {e.Remote,-22} {e.Summary}{(e.Length > 0 ? "\n      " + e.Hex : "")}");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
