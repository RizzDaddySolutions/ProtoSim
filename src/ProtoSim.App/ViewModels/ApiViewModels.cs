using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using ProtoSim.App.Platform;
using ProtoSim.App.Mvvm;
using ProtoSim.App.Views;
using ProtoSim.Core;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App.ViewModels;

/// <summary>API clients tab: client IDs/secrets and bearer tokens, for devices that implement <see cref="ICredentialProvider"/>.</summary>
public sealed class ApiClientsViewModel : ObservableObject
{
    private readonly ICredentialProvider _provider;
    private ClientCredential? _selectedClient;
    private string _tokenText = string.Empty;
    private string _tokenInfo = string.Empty;
    private int _activeTokens;
    private string _examples = string.Empty;
    private bool _requiresAuthentication;

    public ApiClientsViewModel(ICredentialProvider provider)
    {
        _provider = provider;
        _selectedClient = provider.Clients.FirstOrDefault();
        AddClientCommand = new RelayCommand(AddClient);
        RemoveClientCommand = new RelayCommand(RemoveClient, () => SelectedClient != null);
        NewSecretCommand = new RelayCommand(NewSecret, () => SelectedClient != null);
        IssueTokenCommand = new RelayCommand(IssueToken, () => SelectedClient is { Enabled: true, ClientId.Length: > 0 });
        CopyTokenCommand = new RelayCommand(() => CopyText(TokenText), () => TokenText.Length > 0);
        CopyExamplesCommand = new RelayCommand(() => CopyText(Examples));
        RevokeTokensCommand = new RelayCommand(RevokeTokens, () => ActiveTokens > 0);
        Refresh();
    }

    public ObservableCollection<ClientCredential> Clients => _provider.Clients;

    public ClientCredential? SelectedClient
    {
        get => _selectedClient;
        set { if (SetProperty(ref _selectedClient, value)) Refresh(); }
    }

    public string TokenText { get => _tokenText; private set => SetProperty(ref _tokenText, value); }
    public string TokenInfo { get => _tokenInfo; private set => SetProperty(ref _tokenInfo, value); }
    public int ActiveTokens { get => _activeTokens; private set => SetProperty(ref _activeTokens, value); }
    public string Examples { get => _examples; private set => SetProperty(ref _examples, value); }
    public bool RequiresAuthentication { get => _requiresAuthentication; private set => SetProperty(ref _requiresAuthentication, value); }
    public string TokenUrl => _provider.TokenUrl;

    public ICommand AddClientCommand { get; }
    public ICommand RemoveClientCommand { get; }
    public ICommand NewSecretCommand { get; }
    public ICommand IssueTokenCommand { get; }
    public ICommand CopyTokenCommand { get; }
    public ICommand CopyExamplesCommand { get; }
    public ICommand RevokeTokensCommand { get; }

    public void Refresh()
    {
        ActiveTokens = _provider.ActiveTokenCount;
        RequiresAuthentication = _provider.RequiresAuthentication;
        OnPropertyChanged(nameof(TokenUrl));
        Examples = BuildExamples();
    }

    private string BuildExamples()
    {
        var c = SelectedClient;
        string id = c?.ClientId is { Length: > 0 } i ? i : "<client_id>";
        string secret = c?.ClientSecret is { Length: > 0 } s ? s : "<client_secret>";
        string token = TokenText.Length > 0 ? TokenText : "<access_token>";
        string auth = _provider.RequiresAuthentication ? $" -H \"Authorization: Bearer {token}\"" : string.Empty;
        string baseUrl = _provider.BaseUrl;
        var lines = new List<string>();
        if (_provider.RequiresAuthentication)
        {
            lines.Add("# 1. Get a token (client credentials grant)");
            lines.Add($"curl -X POST {_provider.TokenUrl} -d \"grant_type=client_credentials&client_id={id}&client_secret={secret}\"");
            lines.Add(string.Empty);
            lines.Add("# 2. Call the API with the access_token from the response");
        }
        lines.Add($"curl {baseUrl}/sensors/temperature{auth}");
        lines.Add($"curl \"{baseUrl}/users?role=admin&_sort=name\"{auth}");
        lines.Add($"curl -X POST {baseUrl}/users{auth} -H \"Content-Type: application/json\" -d \"{{\\\"name\\\":\\\"New user\\\"}}\"");
        lines.Add($"curl -X PATCH {baseUrl}/users/1{auth} -H \"Content-Type: application/json\" -d \"{{\\\"active\\\":false}}\"");
        lines.Add($"curl -X DELETE {baseUrl}/users/3{auth}");
        lines.Add(string.Empty);
        lines.Add("# In Windows PowerShell type curl.exe instead of curl.");
        return string.Join(Environment.NewLine, lines);
    }

    private void AddClient()
    {
        var used = Clients.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = Clients.Count + 1;
        while (used.Contains($"Client {n}")) n++;
        var client = new ClientCredential { Name = $"Client {n}", ClientId = ClientCredential.NewClientId(), ClientSecret = ClientCredential.NewSecret() };
        Clients.Add(client);
        SelectedClient = client;
    }

    private void RemoveClient()
    {
        if (SelectedClient is not { } c) return;
        if (!MessageDialog.Confirm($"Remove client '{c.Name}' ({c.ClientId})? Its tokens stop working immediately.", "Remove client")) return;
        int index = Clients.IndexOf(c);
        Clients.Remove(c);
        SelectedClient = Clients.Count == 0 ? null : Clients[Math.Min(index, Clients.Count - 1)];
    }

    private void NewSecret()
    {
        if (SelectedClient is not { } c) return;
        if (!MessageDialog.Confirm($"Generate a new secret for '{c.Name}'? Apps using the old secret can no longer get tokens.", "New client secret")) return;
        c.ClientSecret = ClientCredential.NewSecret();
        Refresh();
    }

    private void IssueToken()
    {
        if (SelectedClient is not { } c) return;
        var token = _provider.IssueToken(c);
        TokenText = token.AccessToken;
        TokenInfo = $"Issued to {token.ClientId}, expires {token.ExpiresUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)}" +
                    (token.Scope is { Length: > 0 } scope ? $" · scope: {scope}" : string.Empty);
        Refresh();
    }

    private void RevokeTokens()
    {
        if (!MessageDialog.Confirm($"Revoke all {ActiveTokens} active tokens? Clients will have to request new ones.", "Revoke tokens")) return;
        _provider.RevokeAllTokens();
        TokenText = string.Empty;
        TokenInfo = string.Empty;
        Refresh();
    }

    internal static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Modal.SetClipboardText(text); }
        catch (Exception ex) { MessageDialog.Show($"Couldn't copy to the clipboard: {ex.Message}", "Copy", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

/// <summary>Database tab, for devices that implement <see cref="IJsonDatabaseHost"/>.</summary>
public sealed class DatabaseViewModel : ObservableObject
{
    private readonly IJsonDatabaseHost _host;
    private readonly Func<string> _deviceName;
    private string _jsonText = string.Empty;
    private bool _isDirty;
    private bool _loading;
    private string? _error;
    private long _loadedVersion = -1;

    public DatabaseViewModel(IJsonDatabaseHost host, Func<string> deviceName)
    {
        _host = host;
        _deviceName = deviceName;
        ApplyCommand = new RelayCommand(Apply, () => IsDirty);
        DiscardCommand = new RelayCommand(Reload, () => IsDirty);
        RecreateCommand = new RelayCommand(Recreate);
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopyPathCommand = new RelayCommand(() => ApiClientsViewModel.CopyText(DatabasePath));
        Reload();
    }

    public string DatabasePath => _host.DatabasePath;
    public ObservableCollection<DatabaseCollectionInfo> Collections { get; } = [];

    public string JsonText
    {
        get => _jsonText;
        set
        {
            if (!SetProperty(ref _jsonText, value) || _loading) return;
            IsDirty = true;
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (SetProperty(ref _isDirty, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public ICommand ApplyCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand RecreateCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }

    /// <summary>Called by the UI timer: picks up changes made through the API unless the user is editing.</summary>
    public void Refresh()
    {
        if (_host.Version != _loadedVersion && !IsDirty) Reload();
    }

    private void Reload()
    {
        _loadedVersion = _host.Version;
        _loading = true;
        try { JsonText = _host.ExportJson(); }
        finally { _loading = false; }
        IsDirty = false;
        Error = null;
        Collections.Clear();
        foreach (var c in _host.GetCollections()) Collections.Add(c);
    }

    private void Apply()
    {
        try
        {
            _host.ImportJson(JsonText);
            IsDirty = false;
            Reload();
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
        }
    }

    private void Recreate()
    {
        if (!MessageDialog.Confirm("Recreate the database with the sample data? Everything in it now is deleted. Export it first if you want to keep a copy.", "Recreate database")) return;
        _host.Recreate();
        Reload();
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Title = "Import database", Filter = "JSON (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            if (!MessageDialog.Confirm($"Replace the whole database with the contents of {Path.GetFileName(dlg.FileName)}?", "Import database")) return;
            _host.ImportJson(json);
            Reload();
            MessageDialog.Show($"Imported {Collections.Count} resources from {Path.GetFileName(dlg.FileName)}.", "Import database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(ex.Message, "Import database", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export()
    {
        var name = string.Concat(_deviceName().Split(Path.GetInvalidFileNameChars()));
        var dlg = new SaveFileDialog { Title = "Export database", Filter = "JSON (*.json)|*.json", FileName = $"{name}-database.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _host.ExportJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(ex.Message, "Export database", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder()
    {
        if (File.Exists(DatabasePath))
            Shell.Reveal(DatabasePath);
        else if (Path.GetDirectoryName(DatabasePath) is { } dir && Directory.Exists(dir))
            Shell.Open(dir);
    }
}
