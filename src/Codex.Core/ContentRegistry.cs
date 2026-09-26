using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Codex.Core.Abilities;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;

namespace Codex.Core;

public class ContentRegistry : IContentRegistry
{
    private readonly Dictionary<string, Dictionary<string, (IAbilityDefinition Ability, int Priority, string PackId)>> _abilities = new();
    private readonly Dictionary<string, Dictionary<string, (IActorDefinition Actor, int Priority, string PackId)>> _actors = new();
    private readonly Dictionary<string, Dictionary<string, (ILocationDefinition Location, int Priority, string PackId)>> _locations = new();
    private readonly Dictionary<string, Dictionary<string, (IRulesEntryDefinition Entry, int Priority, string PackId)>> _rules = new();
    private readonly HashSet<string> _loadedPacks = new();
    private readonly ScriptEvaluator _scriptEvaluator;

    public ContentRegistry(ScriptEvaluator scriptEvaluator)
    {
        _scriptEvaluator = scriptEvaluator;
    }

    #region Abilities
    public void RegisterAbility(IAbilityDefinition ability, int priority)
    {
        RegisterInternal(_abilities, ability.SystemId, ability.Id, ability, priority, ability.PackId);
        _loadedPacks.Add(ability.PackId);
    }

    public IAbilityDefinition? GetAbility(string fullId) => GetInternal(_abilities, fullId);

    public IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId) => GetBySystemInternal(_abilities, systemId);
    #endregion

    #region Actors
    public void RegisterActor(IActorDefinition actor, int priority)
    {
        RegisterInternal(_actors, actor.SystemId, actor.Id, actor, priority, actor.PackId);
        _loadedPacks.Add(actor.PackId);
    }

    public IActorDefinition? GetActor(string fullId) => GetInternal(_actors, fullId);

    public IEnumerable<IActorDefinition> GetActorsBySystem(string systemId) => GetBySystemInternal(_actors, systemId);
    #endregion

    #region Locations
    public void RegisterLocation(ILocationDefinition location, int priority)
    {
        RegisterInternal(_locations, location.SystemId, location.Id, location, priority, location.PackId);
        _loadedPacks.Add(location.PackId);
    }

    public ILocationDefinition? GetLocation(string fullId) => GetInternal(_locations, fullId);

    public IEnumerable<ILocationDefinition> GetLocationsBySystem(string systemId) => GetBySystemInternal(_locations, systemId);
    #endregion

    #region Rules entries
    public void RegisterRulesEntry(IRulesEntryDefinition entry, int priority)
    {
        RegisterInternal(_rules, entry.SystemId, entry.Id, entry, priority, entry.PackId);
        _loadedPacks.Add(entry.PackId);
    }

    public IRulesEntryDefinition? GetRulesEntry(string fullId) => GetInternal(_rules, fullId);

    public IEnumerable<IRulesEntryDefinition> GetRulesEntries(string systemId, string? kind = null)
    {
        var entries = GetBySystemInternal(_rules, systemId);
        return kind == null
            ? entries
            : entries.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
    }
    #endregion

    public IEnumerable<string> GetLoadedPacks() => _loadedPacks;

    public AbilityExecutionResult ExecuteAbility(IAbilityDefinition ability, AbilityExecutionContext context)
    {
        var executor = new AbilityExecutor(_scriptEvaluator);
        return executor.Execute(ability, context);
    }

    public Task<AbilityExecutionResult> ExecuteAbilityAsync(string fullId, AbilityExecutionContext context)
    {
        var ability = GetAbility(fullId);
        if (ability == null)
        {
            return Task.FromResult(AbilityExecutionResult.Failed(AbilityExecutor.EffectsStage, $"Unknown ability '{fullId}'."));
        }

        return Task.FromResult(ExecuteAbility(ability, context));
    }

    public Task ExecuteAbilityAsync(string fullId, dynamic context)
    {
        var ability = GetAbility(fullId);
        if (ability == null) return Task.CompletedTask;

        if (context is AbilityContext abilityContext)
        {
            ExecuteAbility(ability, new AbilityExecutionContext(abilityContext.World, abilityContext.Caster, abilityContext.Target, abilityContext.Params));
        }
        else if (context is AbilityExecutionContext executionContext)
        {
            ExecuteAbility(ability, executionContext);
        }

        return Task.CompletedTask;
    }

    #region Helpers
    // B9 remediation: ties used to be broken by ">= existing.Priority", which meant whichever
    // pack Directory.GetFiles() happened to enumerate last won - nondeterministic across
    // filesystems and OSes. Ties are now broken deterministically on PackId (ordinal, highest
    // wins) so the outcome doesn't depend on load order at all.
    private void RegisterInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority, string PackId)>> storage,
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

        var systemId = parts[0];
        var itemId = parts[1];

        if (storage.TryGetValue(systemId, out var systemItems) &&
            systemItems.TryGetValue(itemId, out var entry))
        {
            return entry.Item;
        }

        return default;
    }

    private IEnumerable<T> GetBySystemInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority, string PackId)>> storage, string systemId)
    {
        if (storage.TryGetValue(systemId, out var systemItems))
        {
            return systemItems.Values.Select(v => v.Item);
        }

        return Enumerable.Empty<T>();
    }
    #endregion
}