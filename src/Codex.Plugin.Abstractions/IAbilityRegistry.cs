using System.Collections.Generic;
using System.Threading.Tasks;

namespace Codex.Plugin.Abstractions;

public interface IAbilityRegistry
{
    void Register(IAbilityDefinition ability);
    void Register(IAbilityDefinition ability, int priority);
    IAbilityDefinition? GetAbility(string fullId);
    IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId);
    IEnumerable<string> GetLoadedPacks();

    // Dynamic context will be handled by implementations to avoid leaky abstractions
    Task ExecuteAbilityAsync(string fullId, dynamic context);
}