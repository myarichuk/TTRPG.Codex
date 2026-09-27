using Codex.Plugin.Abstractions;

namespace Codex.Core.Abilities;

/// <summary>Well-known trigger types (5.1d). Abilities opt into events by naming one in a
/// <c>Triggers</c> entry's <see cref="TypedComponent.Type"/>; matching is ordinal.</summary>
public static class TriggerEvents
{
    public const string OnTurnStart = "OnTurnStart";
    public const string OnTurnEnd = "OnTurnEnd";
    public const string OnHit = "OnHit";
}

public sealed record TriggerEvent(string Type, IReadOnlyDictionary<string, object>? Data = null);

public sealed record TriggeredAbility(IAbilityDefinition Ability, AbilityExecutionResult Result);

/// <summary>The runtime event bus (5.1d): matches abilities whose <c>Triggers</c> name the
/// fired event and runs each through the full TRCE pipeline. Pure over the given abilities,
/// so turn/hit commands and tests share this exact path.</summary>
public static class TriggerDispatcher
{
    public static IReadOnlyList<TriggeredAbility> ExecuteForEvent(
        IEnumerable<IAbilityDefinition> abilities,
        TriggerEvent trigger,
        AbilityExecutionContext context,
        AbilityExecutor executor)
    {
        var fired = new List<TriggeredAbility>();
        foreach (var ability in abilities)
        {
            if (ability.Triggers?.Any(t => string.Equals(t.Type, trigger.Type, StringComparison.Ordinal)) != true)
            {
                continue;
            }

            fired.Add(new TriggeredAbility(ability, executor.Execute(ability, context)));
        }

        return fired;
    }

    /// <summary>Resolves an actor blueprint's ability ids into definitions. A dangling id
    /// (pack uninstalled) is skipped, never thrown - the "Missing Content" rule.</summary>
    public static IEnumerable<IAbilityDefinition> ResolveAbilities(
        IActorDefinition? blueprint, IContentRegistry registry)
    {
        if (blueprint == null)
        {
            yield break;
        }

        foreach (var id in blueprint.Abilities)
        {
            var ability = registry.GetAbility(id);
            if (ability != null)
            {
                yield return ability;
            }
        }
    }
}
