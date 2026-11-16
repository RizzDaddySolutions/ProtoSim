using System.Reflection;
using System.Windows;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(PluginCatalog catalog, IReadOnlyList<string> pluginErrors)
    {
        Protocols = catalog.Plugins;
        Errors = pluginErrors;
        InitializeComponent();
        DataContext = this;
    }

    public IEnumerable<IProtocolPlugin> Protocols { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool HasErrors => Errors.Count > 0;

    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 1);
            return $"{v.Major}.{v.Minor}" + (v.Build > 0 ? $".{v.Build}" : "");
        }
    }
}
