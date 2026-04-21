using System;
using System.Collections.Generic;
using System.Linq;
using Codex.Plugin.Abstractions;

namespace Codex.Core;

public class AbilityRegistry : IAbilityRegistry
{
    private readonly Dictionary<string, Dictionary<string, (IAbilityDefinition Ability, int Priority)>> _storage = new();
    private readonly HashSet<string> _loadedPacks = new();

    public void Register(IAbilityDefinition ability)
    {
        Register(ability, 0);
    }

    public void Register(IAbilityDefinition ability, int priority)
    {
        if (!_storage.TryGetValue(ability.SystemId, out var systemAbilities))
        {
            systemAbilities = new Dictionary<string, (IAbilityDefinition Ability, int Priority)>();
            _storage[ability.SystemId] = systemAbilities;
        }

        if (!systemAbilities.TryGetValue(ability.Id, out var existing) || priority >= existing.Priority)
        {
            systemAbilities[ability.Id] = (ability, priority);
            _loadedPacks.Add(ability.PackId);
        }
    }

    public IAbilityDefinition? GetAbility(string fullId)
    {
        var parts = fullId.Split(':', 2);
        if (parts.Length != 2) return null;

        var systemId = parts[0];
        var abilityId = parts[1];

        if (_storage.TryGetValue(systemId, out var systemAbilities) &&
            systemAbilities.TryGetValue(abilityId, out var entry))
        {
            return entry.Ability;
        }

        return null;
    }

    public IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId)
    {
        if (_storage.TryGetValue(systemId, out var systemAbilities))
        {
            return systemAbilities.Values.Select(v => v.Ability);
        }

        return Enumerable.Empty<IAbilityDefinition>();
    }

    public IEnumerable<string> GetLoadedPacks()
    {
        return _loadedPacks;
    }
}