using System.Collections.ObjectModel;
using System.Windows.Input;
using ProtoSim.App.Mvvm;
using ProtoSim.Core;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App.ViewModels;

/// <summary>A client (master) session used to poll or test a device, simulated or real.</summary>
public sealed class ClientViewModel : ObservableObject
{
    private IProtocolClient? _client;
    private CancellationTokenSource? _pollCts;
    private string _name;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isPolling;
    private int _pollIntervalMs = 1000;
    private long _successCount;
    private long _failureCount;
    private string _lastMessage = "Not connected";
    private bool _lastSuccess = true;
    private string _lastElapsed = string.Empty;

    public ClientViewModel(IProtocolPlugin plugin, string name, object settings, object request)
    {
        Plugin = plugin;
        _name = name;
        Settings = settings;
        Request = request;
        Log = new LogViewModel(TrafficLog);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !_isBusy);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected || _isPolling);
        ExecuteCommand = new AsyncRelayCommand(ExecuteOnceAsync, () => !_isBusy && !_isPolling);
        StartPollingCommand = new RelayCommand(StartPolling, () => !_isPolling);
        StopPollingCommand = new RelayCommand(StopPolling, () => _isPolling);
        ResetCountersCommand = new RelayCommand(() => { SuccessCount = 0; FailureCount = 0; });
    }

    public IProtocolPlugin Plugin { get; }
    public object Settings { get; }
    public object Request { get; }
    public TrafficLog TrafficLog { get; } = new();
    public LogViewModel Log { get; }
    public ObservableCollection<ClientResultRow> Results { get; } = [];

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand StartPollingCommand { get; }
    public ICommand StopPollingCommand { get; }
    public ICommand ResetCountersCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ProtocolName => Plugin.DisplayName;
    public string Endpoint => Plugin.DescribeClient(Settings);

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(CanEditSettings));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        private set
        {
            if (!SetProperty(ref _isPolling, value)) return;
            OnPropertyChanged(nameof(CanEditSettings));
            OnPropertyChanged(nameof(CanEditRequest));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEditSettings => !_isConnected && !_isPolling;
    public bool CanEditRequest => true;

    public int PollIntervalMs { get => _pollIntervalMs; set => SetProperty(ref _pollIntervalMs, Math.Clamp(value, 10, 3_600_000)); }
    public long SuccessCount { get => _successCount; private set => SetProperty(ref _successCount, value); }
    public long FailureCount { get => _failureCount; private set => SetProperty(ref _failureCount, value); }
    public string LastMessage { get => _lastMessage; private set => SetProperty(ref _lastMessage, value); }
    public bool LastSuccess { get => _lastSuccess; private set => SetProperty(ref _lastSuccess, value); }
    public string LastElapsed { get => _lastElapsed; private set => SetProperty(ref _lastElapsed, value); }

    private IProtocolClient EnsureClient() => _client ??= Plugin.CreateClient(Settings, new ClientContext(TrafficLog));

    private async Task ConnectAsync()
    {
        _isBusy = true;
        try
        {
            await EnsureClient().ConnectAsync();
            LastMessage = $"Connected to {Endpoint}";
            LastSuccess = true;
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            LastSuccess = false;
        }
        finally
        {
            _isBusy = false;
            IsConnected = _client?.IsConnected == true;
        }
    }

    public async Task DisconnectAsync()
    {
        StopPolling();
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null; // recreated on next use so edited settings apply
        }
        IsConnected = false;
        LastMessage = "Disconnected";
        LastSuccess = true;
    }

    private Task ExecuteOnceAsync() => ExecuteAsync(CancellationToken.None);

    private async Task ExecuteAsync(CancellationToken ct)
    {
        _isBusy = true;
        try
        {
            var result = await EnsureClient().ExecuteAsync(Request, ct);
            Apply(result);
        }
        finally
        {
            _isBusy = false;
            IsConnected = _client?.IsConnected == true;
        }
    }

    private void Apply(ClientResult result)
    {
        LastSuccess = result.Success;
        LastMessage = result.Message;
        LastElapsed = $"{result.Elapsed.TotalMilliseconds:F1} ms";
        if (result.Success) SuccessCount++; else FailureCount++;
        if (!result.Success) return;

        // Update in place where possible so the grid doesn't flicker while polling.
        for (int i = 0; i < result.Rows.Count; i++)
        {
            if (i < Results.Count) { if (Results[i] != result.Rows[i]) Results[i] = result.Rows[i]; }
            else Results.Add(result.Rows[i]);
        }
        while (Results.Count > result.Rows.Count) Results.RemoveAt(Results.Count - 1);
    }

    private async void StartPolling()
    {
        if (_isPolling) return;
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        IsPolling = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                await ExecuteAsync(ct);
                var wait = TimeSpan.FromMilliseconds(_pollIntervalMs) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                else await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            LastSuccess = false;
        }
        finally
        {
            IsPolling = false;
        }
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    public void Refresh()
    {
        Log.Drain();
        OnPropertyChanged(nameof(Endpoint));
        if (!_isBusy) IsConnected = _client?.IsConnected == true;
    }
}
