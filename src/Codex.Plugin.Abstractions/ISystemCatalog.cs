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
}
