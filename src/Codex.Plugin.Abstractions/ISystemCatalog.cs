using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>One loaded system as the UI sees it.</summary>
/// <param name="ContainsCompiledCode">A compiled plugin, or a data system whose system.yaml names types in its DLLs (overhaul
/// section 4, rule 7): code that runs with the server's rights, which the Library says.</param>
/// <param name="Version">The version it says it is, as loaded (<see cref="SystemFingerprint.Version"/>).</param>
public record SystemSummary(string Id, string Name, string? Description, string? Compatibility, LicenseInfo? License, bool IsDataDriven, bool ContainsCompiledCode = false, string? Version = null);

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

    /// <summary>What the loaded system is, its version and file hashes (overhaul section 4, rule 8), or null when it is not
    /// loaded. A campaign compares it with the one it last played under.</summary>
    SystemFingerprint? GetFingerprint(string systemId) => GetDefinition(systemId)?.Fingerprint;

    /// <summary>
    /// The UI schemas a loaded system plugin publishes for <paramref name="systemId"/>, or empty
    /// if that system isn't loaded.
    /// </summary>
    IEnumerable<UISchema> GetUISchemas(string systemId);
}
