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
    private readonly Dictionary<(string System, string Key), Dictionary<string, (Dictionary<string, object> Fields, string PackId)>> _translations = new();
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

    public void RegisterTranslation(string systemId, string type, string id, string locale, string packId, IReadOnlyDictionary<string, object> fields)
    {
        var language = LanguageOf(locale);
        lock (_gate)
        {
            var key = (systemId, EntryKey(type, id));
            if (!_translations.TryGetValue(key, out var byLocale)) _translations[key] = byLocale = new();
            byLocale[language] = (new Dictionary<string, object>(fields), packId);
        }
    }

    public ContentEntry Localize(ContentEntry entry, string? culture)
    {
        var language = LanguageOf(culture);
        if (language.Length == 0) return entry;

        Dictionary<string, object> fields;
        string translationPack;
        lock (_gate)
        {
            if (!_translations.TryGetValue((entry.SystemId, EntryKey(entry.Type, entry.Id)), out var byLocale)
                || !byLocale.TryGetValue(language, out var hit))
                return entry;
            fields = hit.Fields;
            translationPack = hit.PackId;
        }

        var copy = entry.Clone();
        copy.Properties[ContentEntry.TranslationSourceKey] = translationPack;
        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case "name" when value is string n && n.Length > 0:
                    copy.Name = n;
                    break;
                case "description" when value is string d:
                    copy.Description = d;
                    break;
                default:
                    copy.Properties[name] = value;
                    break;
            }
        }

        // The importer writes a stat block's rows both into the description and as row tables. The translated description carries
        // the translated rows, so the English rows the original description already held would show a second, English copy.
        if (fields.ContainsKey("description") && entry.Description != null)
        {
            foreach (var (name, value) in copy.Properties.ToList())
            {
                if (!fields.ContainsKey(name) && IsRows(value, out var rows) && rows.All(r => r.TryGetValue("name", out var rowName) && rowName is string n && entry.Description.Contains(n)))
                    copy.Properties.Remove(name);
            }
        }

        return copy;
    }

    private static bool IsRows(object value, out List<IDictionary<string, object>> rows)
    {
        rows = new List<IDictionary<string, object>>();
        if (value is not IEnumerable<object> list || value is string) return false;
        foreach (var item in list)
        {
            if (item is not IDictionary<string, object> row) return false;
            rows.Add(row);
        }

        return rows.Count > 0;
    }

    public bool IsLocalized(string? culture) => LanguageOf(culture).Length > 0;

    /// <summary>"ru-RU" -> "ru"; empty for English (the entries' own language) and for no culture.</summary>
    private static string LanguageOf(string? culture)
    {
        var language = (culture ?? "").Trim().Split('-', '_')[0].ToLowerInvariant();
        return language switch { "en" => "", "iw" => "he", _ => language };
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

    public IEnumerable<ContentEntry> Search(string systemId, string query, string? type = null, IReadOnlySet<string>? packIds = null, int limit = 200, string? culture = null)
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

        if (LanguageOf(culture).Length == 0)
        {
            return Rank(candidates, query).Take(limit).ToList();
        }

        // A translated UI still has to find "Fireball" by its English name, so each entry scores as the better of both.
        var trimmed = (query ?? string.Empty).Trim();
        return candidates
            .Select(e => (Original: e, Shown: Localize(e, culture)))
            .Select(x => (x.Shown, Score: trimmed.Length == 0 ? 1 : Math.Max(Score(x.Shown, trimmed), Score(x.Original, trimmed))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Shown.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Shown)
            .Take(limit)
            .ToList();
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
        query = Fold(query);
        var name = Fold(entry.Name);
        if (name.Equals(query, comparison)) return 100;
        if (name.StartsWith(query, comparison)) return 80;
        if (name.Contains(query, comparison)) return 60;
        if (entry.Tags.Any(t => Fold(t).Contains(query, comparison))) return 40;
        if (entry.Properties.Values.Any(v => v is string s && Fold(s).Contains(query, comparison))) return 30;
        if (entry.Description != null && Fold(entry.Description).Contains(query, comparison)) return 20;
        return 0;
    }

    /// <summary>Makes searching forgiving in Russian and Hebrew: ё matches е, Hebrew vowel points and cantillation marks
    /// are ignored, and final letter forms match their ordinary forms. Text without such characters is returned as is.</summary>
    internal static string Fold(string text)
    {
        var needs = false;
        foreach (var c in text)
        {
            if (c == 'ё' || c == 'Ё' || (c >= '\u0591' && c <= '\u05C7') || c is 'ך' or 'ם' or 'ן' or 'ף' or 'ץ') { needs = true; break; }
        }
        if (!needs) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c >= '\u0591' && c <= '\u05C7') continue;
            sb.Append(c switch { 'ё' => 'е', 'Ё' => 'Е', 'ך' => 'כ', 'ם' => 'מ', 'ן' => 'נ', 'ף' => 'פ', 'ץ' => 'צ', _ => c });
        }
        return sb.ToString();
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
