using System.Threading.Tasks;

namespace Codex.Plugin.Abstractions;

public interface IContentPackLoader
{
    /// <summary>
    /// Loads a pack directory or <c>.zip</c>/<c>.cdx</c> archive into the registry.
    /// <paramref name="system"/> is the definition of the pack's system when one is known - it tells
    /// the loader which folders are which content types and which types double as actor blueprints.
    /// Without it, every unknown folder still loads as generic entries typed by folder name.
    /// </summary>
    Task LoadPackAsync(string packPath, SystemDefinition? system = null);

    Task<PackManifest> ReadManifestAsync(string packPath);
}
