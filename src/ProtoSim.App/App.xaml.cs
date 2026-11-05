using System.Windows;
using System.Windows.Threading;
using ProtoSim.App.ViewModels;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.ModbusTcp;
using ProtoSim.Protocols.Mqtt;
using ProtoSim.Protocols.OpcUa;

namespace ProtoSim.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var catalog = new PluginCatalog();
        catalog.Register(new ModbusTcpPlugin());
        catalog.Register(new OpcUaPlugin());
        catalog.Register(new MqttPlugin());
        var errors = catalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Plugins"));

        var vm = new MainViewModel(catalog, errors);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        // A file path opens a project (e.g. from a file association).
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            try { await vm.LoadAsync(e.Args[0]); }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "Open project", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow!, e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
