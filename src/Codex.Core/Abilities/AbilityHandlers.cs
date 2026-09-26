using Codex.Plugin.Abstractions;

namespace Codex.Core.Abilities;

/// <summary>Predicate over one Requires entry. Unknown types fail closed (see AbilityExecutor).</summary>
public interface IRequirementHandler
{
    string Type { get; }
    bool CanSatisfy(TypedComponent requirement, AbilityExecutionContext context, out string? reason);
}

/// <summary>One Costs entry. Check-then-pay: CanPay runs for every cost before any Pay runs.</summary>
public interface ICostHandler
{
    string Type { get; }
    bool CanPay(TypedComponent cost, AbilityExecutionContext context, out string? reason);
    void Pay(TypedComponent cost, AbilityExecutionContext context);
}

/// <summary>One Effects entry. Keyed by TypedComponent.Type; Script remains the fallback.</summary>
public interface IEffectHandler
{
    string Type { get; }
    void Apply(TypedComponent effect, AbilityExecutionContext context, string sourceId);
}
