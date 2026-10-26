using System.Reflection;

namespace ProtoSim.Core.Protocols;

/// <summary>Holds the available protocol plugins: built-in ones plus any found in a plugins folder.</summary>
public sealed class PluginCatalog
{
    private readonly List<IProtocolPlugin> _plugins = [];

    public IReadOnlyList<IProtocolPlugin> Plugins => _plugins;

    public void Register(IProtocolPlugin plugin)
    {
        if (_plugins.Any(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))) return;
        _plugins.Add(plugin);
    }

    public IProtocolPlugin? Find(string id) =>
        _plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads every public, non-abstract <see cref="IProtocolPlugin"/> from *.dll files in a folder.</summary>
    /// <returns>Load errors, one message per failed assembly.</returns>
    public IReadOnlyList<string> LoadFromDirectory(string directory)
    {
        var errors = new List<string>();
        if (!Directory.Exists(directory)) return errors;

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(IProtocolPlugin).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                    Register((IProtocolPlugin)Activator.CreateInstance(type)!);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return errors;
    }
}
