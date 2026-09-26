using DefaultEcs;

namespace Codex.Core.Abilities;

/// <summary>Per-execution input for the TRCE pipeline (5.1). Entities are resolved by the
/// caller (a runtime command inside the single-writer loop, or a test against a bare world).</summary>
public sealed record AbilityExecutionContext(
    CodexWorld World,
    Entity Caster,
    Entity? Target = null,
    IReadOnlyDictionary<string, object>? Params = null,
    // 6.3: area effects fan out across these in addition to Target. Single-target callers
    // keep setting Target only; area callers set Targets (Target may still name the primary).
    IReadOnlyList<Entity>? Targets = null)
{
    /// <summary>Target plus Targets, de-duplicated. Empty when neither is set.</summary>
    public IReadOnlyList<Entity> AllTargets()
    {
        if (Target == null)
        {
            return Targets ?? Array.Empty<Entity>();
        }

        if (Targets == null || Targets.Count == 0)
        {
            return new[] { Target.Value };
        }

        var seen = new HashSet<Entity> { Target.Value };
        var all = new List<Entity> { Target.Value };
        foreach (var target in Targets)
        {
            if (seen.Add(target))
            {
                all.Add(target);
            }
        }

        return all;
    }
}

/// <summary>Typed outcome of one ability execution. Replaces fire-and-forget Task usage.</summary>
public sealed record AbilityExecutionResult(
    bool Success,
    string? FailureReason = null,
    string? FailedStage = null,
    IReadOnlyList<string>? AppliedEffects = null)
{
    public static readonly AbilityExecutionResult Ok = new(true, AppliedEffects: Array.Empty<string>());

    public static AbilityExecutionResult Failed(string stage, string reason, IReadOnlyList<string>? applied = null)
        => new(false, reason, stage, applied ?? Array.Empty<string>());
}
