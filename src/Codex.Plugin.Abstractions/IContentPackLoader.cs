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

    /// <summary>What went wrong in the packs loaded since the last call (an entry that did not parse, a base it inherits
    /// from that is missing), in English, for the DM to see; the list is emptied.</summary>
    IReadOnlyList<string> TakeProblems() => Array.Empty<string>();

    /// <summary>A loader that fills a fresh, empty registry instead of this one's, for a reload: the packs load there while
    /// the app keeps reading the old content, and <see cref="Commit"/> on it puts the new content in place. A loader that
    /// cannot stage returns itself, and its packs load straight into its registry.</summary>
    IContentPackLoader Staging() => this;

    /// <summary>On a loader from <see cref="Staging"/>: what it loaded replaces the content of the registry it was staged from.</summary>
    void Commit()
    {
    }
}
