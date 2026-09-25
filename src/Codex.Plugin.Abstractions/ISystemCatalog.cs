using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// The set of system plugin ids currently loaded into the process. Persistence uses this to
/// reject a <c>CampaignDocument.SystemId</c> that names a system nothing implements, rather than
/// silently accepting an id that will never resolve an <see cref="ICodexSystemPlugin"/>.
/// </summary>
public interface ISystemCatalog
{
    /// <summary>True once plugin discovery has run at least once, even if it found nothing.</summary>
    bool IsLoaded { get; }

    IReadOnlySet<string> LoadedSystemIds { get; }

    /// <summary>
    /// The UI schemas a loaded system plugin publishes for <paramref name="systemId"/>, or empty
    /// if that system isn't loaded. This is what the web renderer (2.2) builds its forms from -
    /// the same <see cref="UISchema"/> the Authoring app renders, just a different client.
    /// </summary>
    IEnumerable<UISchema> GetUISchemas(string systemId);
}
