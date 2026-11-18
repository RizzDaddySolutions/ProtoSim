using System.Windows;
using System.Windows.Threading;
using ProtoSim.App.ViewModels;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.ModbusTcp;
using ProtoSim.Protocols.Mqtt;
using ProtoSim.Protocols.MTConnect;
using ProtoSim.Protocols.Bacnet;
using ProtoSim.Protocols.Dnp3;
using ProtoSim.Protocols.OpcUa;
using ProtoSim.Protocols.Rest;
using ProtoSim.Protocols.Script;
using ProtoSim.Protocols.Snmp;
using ProtoSim.Protocols.SparkplugB;

namespace ProtoSim.App;

public partial class App : Application
{
    /// <summary>Raised after the light/dark theme changes (menu or Windows setting).</summary>
    public static event Action? ThemeChanged;

    /// <summary>True when the app currently renders dark, following Windows when the theme is "system".</summary>
    public static bool IsDarkTheme
    {
        get
        {
            var mode = Current?.ThemeMode ?? ThemeMode.System;
            if (mode == ThemeMode.Dark) return true;
            if (mode == ThemeMode.Light) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static void RaiseThemeChanged() =>
        Current?.Dispatcher.BeginInvoke(() => ThemeChanged?.Invoke(), DispatcherPriority.Background);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General) RaiseThemeChanged();
        };

        var catalog = new PluginCatalog();
        catalog.Register(new ModbusTcpPlugin());
        catalog.Register(new ModbusRtuSerialPlugin());
        catalog.Register(new ModbusRtuOverTcpPlugin());
        catalog.Register(new OpcUaPlugin());
        catalog.Register(new MqttPlugin());
        catalog.Register(new Dnp3Plugin());
        catalog.Register(new BacnetPlugin());
        catalog.Register(new SnmpPlugin());
        catalog.Register(new MtcPlugin());
        catalog.Register(new ScriptPlugin());
        catalog.Register(new RestPlugin());
        catalog.Register(new SparkplugPlugin());
        var errors = catalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Plugins"));

        var vm = new MainViewModel(catalog, errors);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        vm.RestoreUi();

        // A file path opens a project (e.g. from a file association).
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            try { await vm.LoadAsync(e.Args[0]); }
            catch (Exception ex) { Views.MessageDialog.Show(ex.Message, "Couldn't open project", MessageBoxButton.OK, MessageBoxImage.Error, window); }
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Views.MessageDialog.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
