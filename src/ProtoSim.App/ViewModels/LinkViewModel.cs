using System.ComponentModel;
using System.Windows.Input;
using ProtoSim.App.Platform;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Network;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App.ViewModels;

/// <summary>A network link in the navigation list and its page: status, points and traffic.</summary>
public sealed class LinkViewModel : ObservableObject
{
    private readonly ProjectTagDirectory _tags;
    private LinkPointState? _selectedPoint;
    private IReadOnlyList<string> _tagReferences = [];

    public LinkViewModel(NetworkLink link, ProjectTagDirectory tags)
    {
        Link = link;
        _tags = tags;
        _tagReferences = tags.References;
        Log = new LogViewModel(link.TrafficLog);
        link.PropertyChanged += OnLinkChanged;

        StartCommand = new RelayCommand(Start, () => !Link.IsRunning);
        StopCommand = new AsyncRelayCommand(Link.StopAsync, () => Link.IsRunning);
        AddPointCommand = new RelayCommand(AddPoint);
        RemovePointCommand = new RelayCommand(RemovePoint, () => SelectedPoint != null);
        ReadWriteAllCommand = new RelayCommand(() => SetAll(LinkDirection.ReadWrite), () => CanWriteBack && Link.Points.Count > 0);
        ReadAllCommand = new RelayCommand(() => SetAll(LinkDirection.Read), () => Link.Points.Count > 0);
    }

    public NetworkLink Link { get; }
    public LogViewModel Log { get; }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AddPointCommand { get; }
    public ICommand RemovePointCommand { get; }
    public ICommand ReadWriteAllCommand { get; }
    public ICommand ReadAllCommand { get; }

    public string Name { get => Link.Name; set { Link.Name = value; OnPropertyChanged(); } }
    public string ProtocolName => Link.Plugin.DisplayName;
    public string Endpoint => Link.Endpoint;
    public string SourceDescription => Link.SourceDescription;
    public object Settings => Link.Settings;
    public bool CanEditSettings => !Link.IsRunning;
    public bool CanWriteBack => Link.Plugin.SupportsLinkWrites;
    public IReadOnlyList<LinkDirection> Directions => CanWriteBack ? Enum.GetValues<LinkDirection>() : [LinkDirection.Read];

    /// <summary>For the status dot, in the same colours as devices and clients.</summary>
    public ServerState State => Link.State switch
    {
        LinkState.Running => ServerState.Running,
        LinkState.Faulted => ServerState.Faulted,
        _ => ServerState.Stopped,
    };

    public string Summary => $"{Link.Points.Count} point{(Link.Points.Count == 1 ? "" : "s")} · {(Link.IsRunning ? "running" : "stopped")}";

    public IReadOnlyList<string> TagReferences { get => _tagReferences; private set => SetProperty(ref _tagReferences, value); }

    public LinkPointState? SelectedPoint
    {
        get => _selectedPoint;
        set { if (SetProperty(ref _selectedPoint, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private void OnLinkChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NetworkLink.State):
                OnPropertyChanged(nameof(State));
                break;
            case nameof(NetworkLink.IsRunning):
                OnPropertyChanged(nameof(CanEditSettings));
                OnPropertyChanged(nameof(Summary));
                CommandManager.InvalidateRequerySuggested();
                break;
            case nameof(NetworkLink.Name):
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(NetworkLink.SourceDescription):
                OnPropertyChanged(nameof(SourceDescription));
                break;
        }
    }

    public void Start()
    {
        if (Link.IsRunning) return;
        Link.Start();
    }

    private void AddPoint()
    {
        var p = Link.AddPoint(new LinkPointDocument { DataType = Core.Data.TagDataType.Float32 });
        SelectedPoint = p;
        OnPropertyChanged(nameof(Summary));
    }

    private void RemovePoint()
    {
        if (SelectedPoint is not { } p) return;
        int i = Link.Points.IndexOf(p);
        Link.Points.Remove(p);
        SelectedPoint = Link.Points.Count == 0 ? null : Link.Points[Math.Min(i, Link.Points.Count - 1)];
        OnPropertyChanged(nameof(Summary));
    }

    private void SetAll(LinkDirection direction)
    {
        foreach (var p in Link.Points)
            p.Direction = direction == LinkDirection.ReadWrite && LinkPoints.CanWrite(Link.Plugin, p.ToPoint()) ? LinkDirection.ReadWrite : LinkDirection.Read;
    }

    public void Refresh()
    {
        Log.Drain();
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(Summary));
        if (!ReferenceEquals(_tagReferences, _tags.References)) TagReferences = _tags.References;
    }
}
