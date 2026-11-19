using System.Reflection;
using System.Windows;
using ProtoSim.Core.Protocols;

namespace ProtoSim.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(PluginCatalog catalog, IReadOnlyList<string> pluginErrors)
    {
        Protocols = catalog.Plugins.OrderBy(plugin => plugin.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Errors = pluginErrors;
        InitializeComponent();
        DataContext = this;
    }

    public IReadOnlyList<IProtocolPlugin> Protocols { get; }
    public IReadOnlyList<string> Errors { get; }
    public int ProtocolCount => Protocols.Count;
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
