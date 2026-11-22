using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ProtoSim.App.Platform;
using ProtoSim.App.Services;
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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Applies the saved theme: Light, Dark, or following the operating system.</summary>
    public static void ApplyTheme()
    {
        if (Current == null) return;
        Current.RequestedThemeVariant = UiLayout.Current.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public static PluginCatalog CreateCatalog(out IReadOnlyList<string> errors)
    {
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
        errors = catalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Plugins"));
        return catalog;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Views.MessageDialog.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var catalog = CreateCatalog(out var errors);
            var vm = new MainViewModel(catalog, errors);
            var window = new MainWindow { DataContext = vm };
            UiLayout.Current.MainWindow?.ApplyTo(window);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Opened += async (_, _) =>
            {
                vm.RestoreUi();
                // A file path opens a project (e.g. from a file association).
                if (desktop.Args is { Length: > 0 } args && File.Exists(args[0]))
                {
                    try { await vm.LoadAsync(args[0]); }
                    catch (Exception ex) { Views.MessageDialog.Show(ex.Message, "Couldn't open project", MessageBoxButton.OK, MessageBoxImage.Error, window); }
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
