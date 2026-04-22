using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;

namespace Codex.Core;

public class ContentRegistry : IContentRegistry
{
    private readonly Dictionary<string, Dictionary<string, (IAbilityDefinition Ability, int Priority)>> _abilities = new();
    private readonly Dictionary<string, Dictionary<string, (IActorDefinition Actor, int Priority)>> _actors = new();
    private readonly Dictionary<string, Dictionary<string, (ILocationDefinition Location, int Priority)>> _locations = new();
    private readonly HashSet<string> _loadedPacks = new();
    private readonly ScriptEvaluator _scriptEvaluator;

    public ContentRegistry(ScriptEvaluator scriptEvaluator)
    {
        _scriptEvaluator = scriptEvaluator;
    }

    #region Abilities
    public void RegisterAbility(IAbilityDefinition ability, int priority)
    {
        RegisterInternal(_abilities, ability.SystemId, ability.Id, ability, priority);
        _loadedPacks.Add(ability.PackId);
    }

    public IAbilityDefinition? GetAbility(string fullId) => GetInternal(_abilities, fullId);

    public IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId) => GetBySystemInternal(_abilities, systemId);
    #endregion

    #region Actors
    public void RegisterActor(IActorDefinition actor, int priority)
    {
        RegisterInternal(_actors, actor.SystemId, actor.Id, actor, priority);
        _loadedPacks.Add(actor.PackId);
    }

    public IActorDefinition? GetActor(string fullId) => GetInternal(_actors, fullId);

    public IEnumerable<IActorDefinition> GetActorsBySystem(string systemId) => GetBySystemInternal(_actors, systemId);
    #endregion

    #region Locations
    public void RegisterLocation(ILocationDefinition location, int priority)
    {
        RegisterInternal(_locations, location.SystemId, location.Id, location, priority);
        _loadedPacks.Add(location.PackId);
    }

    public ILocationDefinition? GetLocation(string fullId) => GetInternal(_locations, fullId);

    public IEnumerable<ILocationDefinition> GetLocationsBySystem(string systemId) => GetBySystemInternal(_locations, systemId);
    #endregion

    public IEnumerable<string> GetLoadedPacks() => _loadedPacks;

    public Task ExecuteAbilityAsync(string fullId, dynamic context)
    {
        var ability = GetAbility(fullId);
        if (ability == null) return Task.CompletedTask;

        if (ability.Effects != null)
        {
            foreach (var effect in ability.Effects)
            {
                if (effect.Type == "Script" && 
                    effect.Params?.TryGetValue("Script", out var scriptObj) == true && 
                    scriptObj is string script && 
                    context is AbilityContext abilityContext)
                {
                    _scriptEvaluator.Execute(script, abilityContext);
                }
            }
        }

        return Task.CompletedTask;
    }

    #region Helpers
    private void RegisterInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority)>> storage,
        string systemId, string itemId, T item, int priority)
    {
        if (!storage.TryGetValue(systemId, out var systemItems))
        {
            systemItems = new Dictionary<string, (T Item, int Priority)>();
            storage[systemId] = systemItems;
        }

        if (!systemItems.TryGetValue(itemId, out var existing) || priority >= existing.Priority)
        {
            systemItems[itemId] = (item, priority);
        }
    }

    private T? GetInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority)>> storage, string fullId)
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

    private IEnumerable<T> GetBySystemInternal<T>(Dictionary<string, Dictionary<string, (T Item, int Priority)>> storage, string systemId)
    {
        if (storage.TryGetValue(systemId, out var systemItems))
        {
            return systemItems.Values.Select(v => v.Item);
        }

        return Enumerable.Empty<T>();
    }
    #endregion
}