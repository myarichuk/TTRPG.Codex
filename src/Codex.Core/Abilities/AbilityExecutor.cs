using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Core.Abilities;

/// <summary>TRCE orchestrator (5.1): Requires checks, atomic Costs, typed Effects with Script
/// fallback. Pure over CodexWorld + entities so it runs inside the runtime's single-writer
/// command or directly in a test. Unknown requirement/cost types fail closed; unknown effect
/// types without a Script param fail that effect by name.</summary>
public sealed class AbilityExecutor
{
    public const string RequiresStage = "Requires";
    public const string CostsStage = "Costs";
    public const string EffectsStage = "Effects";

    private readonly ScriptEvaluator _scripts;
    private readonly Dictionary<string, IRequirementHandler> _requirements;
    private readonly Dictionary<string, ICostHandler> _costs;
    private readonly Dictionary<string, IEffectHandler> _effects;
    private readonly ILogger _logger;

    public AbilityExecutor(
        ScriptEvaluator scripts,
        IEnumerable<IRequirementHandler>? requirementHandlers = null,
        IEnumerable<ICostHandler>? costHandlers = null,
        IEnumerable<IEffectHandler>? effectHandlers = null,
        ILogger<AbilityExecutor>? logger = null,
        IDiceRoller? diceRoller = null)
    {
        _scripts = scripts;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _requirements = (requirementHandlers ?? BuiltInHandlers.Requirements(diceRoller)).ToDictionary(h => h.Type, StringComparer.Ordinal);
        _costs = (costHandlers ?? BuiltInHandlers.Costs()).ToDictionary(h => h.Type, StringComparer.Ordinal);
        _effects = (effectHandlers ?? BuiltInHandlers.Effects(diceRoller)).ToDictionary(h => h.Type, StringComparer.Ordinal);
    }

    public AbilityExecutionResult Execute(IAbilityDefinition ability, AbilityExecutionContext context)
    {
        foreach (var req in ability.Requires ?? Enumerable.Empty<TypedComponent>())
        {
            if (!_requirements.TryGetValue(req.Type, out var handler))
            {
                return AbilityExecutionResult.Failed(RequiresStage, $"Unknown requirement '{req.Type}'.");
            }

            if (!handler.CanSatisfy(req, context, out var reason))
            {
                return AbilityExecutionResult.Failed(RequiresStage, reason ?? $"Requirement '{req.Type}' not met.");
            }
        }

        var costs = ability.Costs ?? Enumerable.Empty<TypedComponent>();
        foreach (var cost in costs)
        {
            if (!_costs.TryGetValue(cost.Type, out var handler))
            {
                return AbilityExecutionResult.Failed(CostsStage, $"Unknown cost '{cost.Type}'.");
            }

            if (!handler.CanPay(cost, context, out var reason))
            {
                return AbilityExecutionResult.Failed(CostsStage, reason ?? $"Cost '{cost.Type}' cannot be paid.");
            }
        }

        foreach (var cost in costs)
        {
            _costs[cost.Type].Pay(cost, context);
        }

        var applied = new List<string>();
        foreach (var effect in ability.Effects ?? Enumerable.Empty<TypedComponent>())
        {
            try
            {
                if (_effects.TryGetValue(effect.Type, out var handler))
                {
                    handler.Apply(effect, context, ability.Id);
                    applied.Add(effect.Type);
                }
                else if (BuiltInHandlers.TryGetParam(effect.Params, "Script", out var scriptObj)
                         && !string.IsNullOrWhiteSpace(scriptObj?.ToString()))
                {
                    var scriptResult = _scripts.Execute(
                        scriptObj.ToString()!,
                        new AbilityContext(context.Caster, context.Target, context.World));
                    if (!scriptResult.Success)
                    {
                        return AbilityExecutionResult.Failed(EffectsStage, scriptResult.Error ?? "Script effect failed.", applied);
                    }

                    applied.Add(effect.Type);
                }
                else
                {
                    return AbilityExecutionResult.Failed(EffectsStage, $"Unknown effect '{effect.Type}'.", applied);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Effect '{EffectType}' failed for ability {AbilityId}", effect.Type, ability.Id);
                return AbilityExecutionResult.Failed(EffectsStage, ex.Message, applied);
            }
        }

        return new AbilityExecutionResult(true, AppliedEffects: applied);
    }
}
