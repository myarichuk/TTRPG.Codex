using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface IAbilityRegistry
{
    void Register(IAbilityDefinition ability);
    void Register(IAbilityDefinition ability, int priority);
    IAbilityDefinition? GetAbility(string fullId);
    IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId);
    IEnumerable<string> GetLoadedPacks();
}