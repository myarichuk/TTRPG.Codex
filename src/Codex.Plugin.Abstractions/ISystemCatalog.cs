using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>One loaded system as the UI sees it.</summary>
public record SystemSummary(string Id, string Name, string? Description, string? Compatibility, LicenseInfo? License, bool IsDataDriven);

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

    /// <summary>Every loaded system with its display metadata, ordered by name.</summary>
    IEnumerable<SystemSummary> Systems { get; }

    /// <summary>Display name for a system id; falls back to the id itself for an unknown system.</summary>
    string GetDisplayName(string systemId);

    /// <summary>The data definition behind a system, or null for a compiled plugin without one.</summary>
    SystemDefinition? GetDefinition(string systemId);

    /// <summary>
    /// The UI schemas a loaded system plugin publishes for <paramref name="systemId"/>, or empty
    /// if that system isn't loaded.
    /// </summary>
    IEnumerable<UISchema> GetUISchemas(string systemId);
}
