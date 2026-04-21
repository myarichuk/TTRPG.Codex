using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;

namespace Codex.Core;

public class AbilityRegistry : IAbilityRegistry
{
    private readonly Dictionary<string, Dictionary<string, (IAbilityDefinition Ability, int Priority)>> _storage = new();
    private readonly HashSet<string> _loadedPacks = new();
    private readonly ScriptEvaluator _scriptEvaluator;

    public AbilityRegistry(ScriptEvaluator scriptEvaluator)
    {
        _scriptEvaluator = scriptEvaluator;
    }

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

    public Task ExecuteAbilityAsync(string fullId, dynamic context)
    {
        var ability = GetAbility(fullId);
        if (ability == null)
        {
            // Ability not found (e.g., pack uninstalled).
            // In a real system, you'd log this or notify the UI.
            return Task.CompletedTask;
        }

        if (ability.Effects != null)
        {
            foreach (var effect in ability.Effects)
            {
                if (!string.IsNullOrEmpty(effect.Script) && context is AbilityContext abilityContext)
                {
                    _scriptEvaluator.Execute(effect.Script, abilityContext);
                }
                // Handle predefined effect types here later
            }
        }

        return Task.CompletedTask;
    }
}