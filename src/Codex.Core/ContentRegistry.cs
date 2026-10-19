using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;

namespace Codex.Core;

public class ContentRegistry : IContentRegistry
{
    private readonly Dictionary<string, Dictionary<string, (IAbilityDefinition Ability, int Priority, string PackId)>> _abilities = new();
    private readonly Dictionary<string, Dictionary<string, (IActorDefinition Actor, int Priority, string PackId)>> _actors = new();
    private readonly Dictionary<string, Dictionary<string, (ILocationDefinition Location, int Priority, string PackId)>> _locations = new();
    private readonly Dictionary<string, Dictionary<string, (ContentEntry Entry, int Priority, string PackId)>> _entries = new();
    private readonly Dictionary<string, List<string>> _entryTypesBySystem = new();
    private readonly Dictionary<(string System, string Key), Dictionary<string, object>> _flavor = new();
    private readonly Dictionary<string, PackManifest> _packs = new();
    private readonly HashSet<string> _loadedPacks = new();
    private readonly ScriptEvaluator _scriptEvaluator;
    private readonly object _gate = new();

    public ContentRegistry(ScriptEvaluator scriptEvaluator)
    {
        _scriptEvaluator = scriptEvaluator;
    }

    #region Abilities
    public void RegisterAbility(IAbilityDefinition ability, int priority)
    {
        lock (_gate)
        {
            RegisterInternal(_abilities, ability.SystemId, ability.Id, ability, priority, ability.PackId);
            _loadedPacks.Add(ability.PackId);
        }
    }

    public IAbilityDefinition? GetAbility(string fullId) => GetInternal(_abilities, fullId);

    public IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId) => GetBySystemInternal(_abilities, systemId);
    #endregion

    #region Actors
    public void RegisterActor(IActorDefinition actor, int priority)
    {
        lock (_gate)
        {
            RegisterInternal(_actors, actor.SystemId, actor.Id, actor, priority, actor.PackId);
            _loadedPacks.Add(actor.PackId);
        }
    }

    public IActorDefinition? GetActor(string fullId) => GetInternal(_actors, fullId);

    public IEnumerable<IActorDefinition> GetActorsBySystem(string systemId) => GetBySystemInternal(_actors, systemId);
    #endregion

    #region Locations
    public void RegisterLocation(ILocationDefinition location, int priority)
    {
        lock (_gate)
        {
            RegisterInternal(_locations, location.SystemId, location.Id, location, priority, location.PackId);
            _loadedPacks.Add(location.PackId);
        }
    }

    public ILocationDefinition? GetLocation(string fullId) => GetInternal(_locations, fullId);

    public IEnumerable<ILocationDefinition> GetLocationsBySystem(string systemId) => GetBySystemInternal(_locations, systemId);
    #endregion

    #region Entries
    public void RegisterEntry(ContentEntry entry, int priority)
    {
        if (string.IsNullOrWhiteSpace(entry.Type))
        {
            throw new ArgumentException("A content entry needs a type.", nameof(entry));
        }

        lock (_gate)
        {
            RegisterInternal(_entries, entry.SystemId, EntryKey(entry.Type, entry.Id), entry, priority, entry.PackId);
            _loadedPacks.Add(entry.PackId);

            if (!_entryTypesBySystem.TryGetValue(entry.SystemId, out var types))
            {
                types = new List<string>();
                _entryTypesBySystem[entry.SystemId] = types;
            }

            if (!types.Contains(entry.Type, StringComparer.OrdinalIgnoreCase))
            {
                types.Add(entry.Type);
            }
        }
    }

    public void RegisterFlavor(string systemId, string type, string id, string packId, IReadOnlyDictionary<string, object> properties)
    {
        lock (_gate)
        {
            var key = (systemId, EntryKey(type, id));
            if (!_flavor.TryGetValue(key, out var merged)) _flavor[key] = merged = new Dictionary<string, object>();
            foreach (var (name, value) in properties) merged[name] = value;
            merged[ContentEntry.FlavorSourceKey] = packId;
        }
    }

    public IReadOnlyDictionary<string, object> GetFlavor(string systemId, string type, string id)
    {
        lock (_gate)
        {
            return _flavor.TryGetValue((systemId, EntryKey(type, id)), out var hit)
                ? new Dictionary<string, object>(hit)
                : new Dictionary<string, object>();
        }
    }

    public ContentEntry? GetEntry(string systemId, string type, string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(systemId, out var bySystem) && bySystem.TryGetValue(EntryKey(type, id), out var hit)
                ? hit.Entry
                : null;
        }
    }

    public IEnumerable<ContentEntry> GetEntries(string systemId, string type, IReadOnlySet<string>? packIds = null)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(systemId, out var bySystem))
            {
                return Enumerable.Empty<ContentEntry>();
            }

            return bySystem.Values
                .Where(v => string.Equals(v.Entry.Type, type, StringComparison.OrdinalIgnoreCase))
                .Where(v => packIds == null || packIds.Count == 0 || packIds.Contains(v.PackId))
                .Select(v => v.Entry)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IEnumerable<string> GetEntryTypes(string systemId)
    {
        lock (_gate)
        {
            return _entryTypesBySystem.TryGetValue(systemId, out var types) ? types.ToList() : new List<string>();
        }
    }

    public IEnumerable<ContentEntry> Search(string systemId, string query, string? type = null, IReadOnlySet<string>? packIds = null, int limit = 200)
    {
        List<ContentEntry> candidates;
        lock (_gate)
        {
            if (!_entries.TryGetValue(systemId, out var bySystem))
            {
                return Enumerable.Empty<ContentEntry>();
            }

            candidates = bySystem.Values
                .Where(v => type == null || string.Equals(v.Entry.Type, type, StringComparison.OrdinalIgnoreCase))
                .Where(v => packIds == null || packIds.Count == 0 || packIds.Contains(v.PackId))
                .Select(v => v.Entry)
                .ToList();
        }

        return Rank(candidates, query).Take(limit).ToList();
    }

    /// <summary>Ranks entries against a query: name prefix, then name substring, then tag, then
    /// description; ties broken alphabetically. Exposed so homebrew documents can be ranked by
    /// the same rule before being merged with pack content.</summary>
    public static IEnumerable<ContentEntry> Rank(IEnumerable<ContentEntry> entries, string query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        }

        return entries
            .Select(e => (Entry: e, Score: Score(e, trimmed)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry);
    }

    private static int Score(ContentEntry entry, string query)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (entry.Name.Equals(query, comparison)) return 100;
        if (entry.Name.StartsWith(query, comparison)) return 80;
        if (entry.Name.Contains(query, comparison)) return 60;
        if (entry.Tags.Any(t => t.Contains(query, comparison))) return 40;
        if (entry.Properties.Values.Any(v => v is string s && s.Contains(query, comparison))) return 30;
        if (entry.Description?.Contains(query, comparison) == true) return 20;
        return 0;
    }

    private static string EntryKey(string type, string id) => $"{type.ToLowerInvariant()}:{id}";
    #endregion

    #region Packs
    public void RegisterPack(PackManifest manifest)
    {
        lock (_gate)
        {
            _packs[manifest.Id] = manifest;
            _loadedPacks.Add(manifest.Id);
        }
    }

    public IEnumerable<PackManifest> GetPacks(string? systemId = null)
    {
        lock (_gate)
        {
            return _packs.Values
                .Where(p => systemId == null || string.Equals(p.SystemId, systemId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public PackManifest? GetPack(string packId)
    {
        lock (_gate)
        {
            return _packs.GetValueOrDefault(packId);
        }
    }

    public IEnumerable<string> GetLoadedPacks()
    {
        lock (_gate)
        {
            return _loadedPacks.ToList();
        }
    }
    #endregion

    public Task ExecuteAbilityAsync(string fullId, dynamic context)
    {
        var ability = GetAbility(fullId);
        if (ability == null) return Task.CompletedTask;

        if (ability.Effects != null)
        {
            foreach (var effect in ability.Effects)
            {
                if (effect.Params != null && effect.Params.TryGetValue("Script", out var scriptObj) && !string.IsNullOrEmpty(scriptObj?.ToString()) && context is AbilityContext abilityContext)
                {
                    _scriptEvaluator.Execute(scriptObj.ToString()!, abilityContext);
                }
            }
        }

        return Task.CompletedTask;
    }

    #region Helpers
    // Ties are broken deterministically on PackId (ordinal, highest wins) so the outcome doesn't
    // depend on filesystem enumeration order.
    private static void RegisterInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority, string PackId)>> storage,
        string systemId, string itemId, T item, int priority, string packId)
    {
        if (!storage.TryGetValue(systemId, out var systemItems))
        {
            systemItems = new Dictionary<string, (T Item, int Priority, string PackId)>();
            storage[systemId] = systemItems;
        }

        if (!systemItems.TryGetValue(itemId, out var existing) ||
            priority > existing.Priority ||
            (priority == existing.Priority && string.CompareOrdinal(packId, existing.PackId) > 0))
        {
            systemItems[itemId] = (item, priority, packId);
        }
    }

    private T? GetInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority, string PackId)>> storage, string fullId)
    {
        var parts = fullId.Split(':', 2);
        if (parts.Length != 2) return default;

        lock (_gate)
        {
            if (storage.TryGetValue(parts[0], out var systemItems) && systemItems.TryGetValue(parts[1], out var entry))
            {
                return entry.Item;
            }
        }

        return default;
    }

    private IEnumerable<T> GetBySystemInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority, string PackId)>> storage, string systemId)
    {
        lock (_gate)
        {
            return storage.TryGetValue(systemId, out var systemItems)
                ? systemItems.Values.Select(v => v.Item).ToList()
                : Enumerable.Empty<T>();
        }
    }
    #endregion
}
