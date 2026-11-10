using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The bundled srd5e system and the simple-d6 example, as the app's catalog would give them, for book tests that need a system's types.</summary>
internal sealed class BookTestCatalog : ISystemCatalog
{
    private readonly Dictionary<string, SystemDefinition> _systems = new(StringComparer.Ordinal)
    {
        ["srd5e"] = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml")),
        ["simple-d6"] = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "examples", "simple-d6", "system.yaml"))
    };

    public bool IsLoaded => true;
    public IReadOnlySet<string> LoadedSystemIds => _systems.Keys.ToHashSet();
    public IEnumerable<SystemSummary> Systems => [];
    public string GetDisplayName(string systemId) => _systems.TryGetValue(systemId, out var s) ? s.Name : systemId;
    public SystemDefinition? GetDefinition(string systemId) => _systems.GetValueOrDefault(systemId);
    public IEnumerable<UISchema> GetUISchemas(string systemId) => [];
}
