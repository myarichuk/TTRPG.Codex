using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One modifier of a condition that holds, as the sheet-rules side takes it (docs/SHEET_CONDITIONS_DESIGN.md): <see cref="Feature"/> is a
/// stand-in feature that carries this modifier alone, named after the condition (or after the modifier's own <c>source</c>), so the working, a roll's
/// bonus list and the log credit it the way they credit a feature. <see cref="Index"/> is the modifier's place in the condition's effects, which
/// <see cref="OptInIds.Condition"/> turns into its tick box's id, the id <see cref="SheetModel"/> gives the same modifier.</summary>
internal sealed record ConditionModifier(string Condition, int Index, ActiveFeature Feature, FeatureModifier Modifier);

public sealed partial class SheetRuleEngine
{
    /// <summary>The modifiers of the conditions that hold on <paramref name="sheet"/> (<see cref="SheetConditions.Of"/>) that this side owns: those whose
    /// target <see cref="KindOf(string)"/> knows (armor class, hit points, a derived value, a Number field, a roll target). A target it does not know
    /// (a kind's value, a keyed entry, a group) belongs to <see cref="SheetModel"/> on a system with sheet kinds, and the loader refuses a target both claim,
    /// so a condition is never counted twice. Without <c>sheetRules</c> there are none.</summary>
    internal IReadOnlyList<ConditionModifier> ConditionModifiers(IReadOnlyDictionary<string, object> sheet)
    {
        if (Rules == null || system.ConditionEffects.Count == 0) return Array.Empty<ConditionModifier>();
        var found = new List<ConditionModifier>();
        foreach (var held in SheetConditions.Of(system, sheet).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SheetConditions.EffectOf(system, held) is not { } named) continue;
            var (name, effect) = named;
            IReadOnlyDictionary<string, object>? view = null;
            for (var index = 0; index < effect.Modifiers.Count; index++)
            {
                var m = effect.Modifiers[index];
                if (m.Target.Length == 0 || KindOf(m.Target) == TargetKind.Unknown) continue;
                view ??= SheetConditions.WithValue(name, effect, sheet);
                var source = string.IsNullOrWhiteSpace(m.Source) ? name : m.Source!;
                var modifier = new FeatureModifier(m.Target, m.Amount, m.Type, m.When, m.OptIn);
                var entry = new ContentEntry { Id = OptInIds.Condition(name, index), Type = "condition", Name = source, SystemId = system.Id };
                var feature = new ActiveFeature(source, null, entry, null, view, null, new[] { modifier }, Array.Empty<FeatureAdd>(), null);
                found.Add(new ConditionModifier(name, index, feature, modifier));
            }
        }

        return found;
    }
}
