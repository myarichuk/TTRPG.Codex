using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Which conditions hold on a sheet (docs/SHEET_CONDITIONS_DESIGN.md): the ones the sheet lists under
/// <see cref="SheetModel.ConditionsKey"/>, and every valued condition whose value on the sheet is above 0. The one reader both engines
/// share, so a condition holds the same way for a sheet kind and for sheet rules.
/// </summary>
public static partial class SheetConditions
{
    /// <summary>The key a valued condition's value is kept under when <c>value.key</c> is not written: <c>condition_frightened</c>.</summary>
    public static string DefaultKey(string condition) => "condition_" + Slug(condition);

    /// <summary>The name in lower case with each run of anything but letters and digits made one <c>_</c> (<c>"Half Cover"</c> is <c>half_cover</c>).</summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '_') slug.Append('_');
        }

        return slug.ToString().TrimEnd('_');
    }

    /// <summary>The key a valued condition keeps its value under: <c>value.key</c>, else <see cref="DefaultKey"/>.</summary>
    public static string KeyOf(string condition, ConditionValueDefinition value) =>
        string.IsNullOrWhiteSpace(value.Key) ? DefaultKey(condition) : value.Key;

    /// <summary>Every valued condition of the system (a <c>conditionEffects</c> entry with a <c>value</c>), with the key its value lives under.</summary>
    public static IEnumerable<(string Condition, string Key, ConditionValueDefinition Value)> Valued(SystemDefinition system)
    {
        foreach (var (name, effect) in system.ConditionEffects)
        {
            if (effect.Value is { } value) yield return (name, KeyOf(name, value), value);
        }
    }

    /// <summary>The <c>conditionEffects</c> entry of <paramref name="condition"/> (names compare without regard to case), with its name as the system writes it.</summary>
    public static (string Name, ConditionEffectDefinition Effect)? EffectOf(SystemDefinition system, string condition)
    {
        foreach (var (name, effect) in system.ConditionEffects)
        {
            if (string.Equals(name, condition, StringComparison.OrdinalIgnoreCase)) return (name, effect);
        }

        return null;
    }

    /// <summary>What <c>value</c> is in the effects of a condition that holds: the number under its key on the sheet, or 1 for a condition with no
    /// <c>value</c>. A valued condition holds only while its number is above 0 (<see cref="Of"/>), so for one that holds it is that number.</summary>
    public static double ValueOf(string condition, ConditionEffectDefinition effect, IReadOnlyDictionary<string, object> sheet) =>
        effect.Value is { } value && YamlValues.ToNumber(sheet.GetValueOrDefault(KeyOf(condition, value))) is { } number && number > 0 ? number : 1;

    /// <summary>A copy of <paramref name="sheet"/> with <c>value</c> added: what the formulas of the condition's effects are worked out on.</summary>
    public static IReadOnlyDictionary<string, object> WithValue(string condition, ConditionEffectDefinition effect, IReadOnlyDictionary<string, object> sheet) =>
        WithValue(ValueOf(condition, effect, sheet), sheet);

    /// <summary>A copy of <paramref name="sheet"/> with <c>value</c> set to <paramref name="value"/>: the number a condition holds at, stored or brought by another.</summary>
    public static IReadOnlyDictionary<string, object> WithValue(double value, IReadOnlyDictionary<string, object> sheet)
    {
        var copy = new Dictionary<string, object>(sheet.Count + 1, StringComparer.Ordinal);
        foreach (var (key, number) in sheet) copy[key] = number;
        copy[ValueName] = value;
        return copy;
    }

    /// <summary>The name a condition's formulas read its value by.</summary>
    public const string ValueName = "value";

    /// <summary>The targets <see cref="SheetModel"/> reads on a system with sheet kinds, so the ones a condition's modifier would reach through it: a
    /// kind's value, track or keyed entry (<c>skills.balance</c>, its <c>@group</c>s), and each name a named roll's <c>modifiers('...')</c> reads.
    /// The loader sets them against what <c>sheetRules</c> claims (<see cref="SheetRuleEngine"/>): a target in both would count a condition twice.</summary>
    public static IReadOnlySet<string> KindTargets(SystemDefinition system, Func<SheetKindDefinition, bool>? kinds = null)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in system.SheetKinds)
        {
            if (kinds != null && !kinds(kind)) continue;
            foreach (var value in kind.Values) targets.Add(value.Key);
            foreach (var track in kind.Tracks) targets.Add(track.Key);
            foreach (var list in kind.KeyedLists)
            {
                foreach (var key in list.Keys) targets.UnionWith(SheetModel.Targets(list, key, key.Key));
            }
        }

        foreach (var roll in system.Rolls) targets.UnionWith(RollTargets.Of(system, roll));
        return targets;
    }

    /// <summary>The resource key of <see cref="UnreachedKindModifiers"/>'s message, and its English text (the resource string, word for word: a test holds them together).</summary>
    public const string UnreachedKey = "Wizard.Modifiers.UnreachedLine";
    public const string UnreachedEnglish = "{0} '{1}': modifiers[{2}] aims at '{3}', which only the sheet rules read; a sheet kind's feature never reaches it, so it counts for nothing.";

    /// <summary>
    /// On a hybrid system (sheetRules and sheet kinds): for an entry of a type some sheet kind reads as features and sheetRules does not, each modifier whose
    /// target the sheet-rules side owns (<see cref="SheetRuleEngine.KindOf(SystemDefinition, string)"/> knows it) and the kind does not (<see cref="KindTargets"/>).
    /// Such a modifier counts for nothing: the kind's feature never reaches the sheet-rules numbers. One message each (docs/CONDITION_IMPLIES_DESIGN.md).
    /// </summary>
    public static IEnumerable<string> UnreachedKindModifiers(SystemDefinition system, ContentEntry entry, Func<string, object?[], string>? say = null)
    {
        if (system.SheetRules is not { } rules || system.SheetKinds.Count == 0 || string.IsNullOrWhiteSpace(entry.Type)) yield break;
        var kindTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in system.SheetKinds)
        {
            foreach (var source in kind.Features)
            {
                var owner = source.Link != null
                    ? system.SheetKinds.FirstOrDefault(k => string.Equals(k.Kind, kind.Links.FirstOrDefault(l => l.Key == source.Link)?.Kind, StringComparison.OrdinalIgnoreCase))
                    : kind;
                if (source.List != null && owner?.Lists.FirstOrDefault(l => l.Key == source.List)?.Type is { } type) kindTypes.Add(type);
            }
        }

        if (!kindTypes.Contains(entry.Type)) yield break;
        // What sheetRules collects, read from the sources CollectFeatures reads: a list's entry type, and the entry type a field references.
        var ruleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in rules.Features)
        {
            if (source.List != null && rules.Lists.FirstOrDefault(l => l.Key == source.List)?.TargetEntityType is { } listType) ruleTypes.Add(listType);
            if (source.Field != null && system.ActorFields.FirstOrDefault(f => f.Key == source.Field)?.TargetEntityType is { } fieldType) ruleTypes.Add(fieldType);
        }

        if (ruleTypes.Contains(entry.Type) || !entry.Properties.TryGetValue("modifiers", out var raw) || YamlValues.Normalize(raw) is not IEnumerable<object> modifiers) yield break;
        // Only the kinds the rules apply to: a companion's own `ac` is no reason to think a character-kind feature's `ac` reaches something (the same filter as the both-claim rule).
        var kindTargets = KindTargets(system, kind => SheetRuleEngine.AppliesTo(system, kind));
        var index = 0;
        foreach (var modifier in modifiers)
        {
            var at = index++;
            if (YamlValues.Normalize(modifier) is not IDictionary<string, object> map || (map.TryGetValue("target", out var targetValue) ? targetValue?.ToString() : null) is not { Length: > 0 } target) continue;
            if (kindTargets.Contains(target) || SheetRuleEngine.KindOf(system, target) == SheetRuleEngine.TargetKind.Unknown) continue;
            var kindName = char.ToUpperInvariant(entry.Type[0]) + entry.Type[1..];
            object?[] args = { kindName, entry.Name, at, target };
            yield return say != null ? say(UnreachedKey, args) : string.Format(System.Globalization.CultureInfo.InvariantCulture, UnreachedEnglish, args);
        }
    }

    /// <summary>
    /// The sheet as the table has it now (docs/SHEET_CONDITIONS_DESIGN.md, "Storage"): the one view every host that works out numbers reads, so a
    /// condition that holds at the table changes them. A new dictionary, for evaluating and for a handler to run on, <b>never for saving</b>: its
    /// <c>conditions</c> carry the table's statuses and its numbers the table's pools, and neither belongs in the stored sheet (a valued condition's
    /// own key excepted, which the table mirrors to the sheet on purpose: <c>ActorSheetSync</c>). It is:
    /// <list type="bullet">
    /// <item>the stored <paramref name="sheet"/>;</item>
    /// <item>the <paramref name="pools"/> over the numbers the table keeps: a key the sheet holds as a number (hit points, resources, slots, a feature's
    /// <c>uses_*</c>), a track of <paramref name="trackKeys"/> (a sheet kind's numeric tracks, which the sheet may not hold yet), and each valued
    /// condition's key;</item>
    /// <item><c>conditions</c>: the sheet's own, the table's <paramref name="statuses"/> (an actor's active effects, by name), and every valued
    /// condition whose key is above 0 (<see cref="Of"/>). A valued condition at 0 does not hold, listed or not.</item>
    /// </list>
    /// Plain data in and out, because the Web hosts hold no runtime: a table card has its state snapshot's pools and effects. Nothing passed in is changed.
    /// </summary>
    public static Dictionary<string, object> LiveView(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, int>? pools,
        IEnumerable<string>? statuses, IEnumerable<string>? trackKeys = null)
    {
        var live = new Dictionary<string, object>(sheet, StringComparer.Ordinal);
        if (pools != null)
        {
            var owned = new HashSet<string>(trackKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var (_, key, _) in Valued(system)) owned.Add(key);
            foreach (var (key, value) in pools)
            {
                if (owned.Contains(key) || YamlValues.ToNumber(sheet.GetValueOrDefault(key)) != null) live[key] = (long)value;
            }
        }

        var listed = SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey).Concat(statuses ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
        live[SheetModel.ConditionsKey] = listed;
        live[SheetModel.ConditionsKey] = Listed(system, live).Cast<object>().ToList();
        return live;
    }

    /// <summary>What holds on <paramref name="sheet"/> on its own: the conditions it lists, and each valued condition whose key on the sheet is above 0.
    /// A valued condition holds only then, listed or not: one the sheet lists with its value at 0 or unset is left out. What a held condition
    /// implies is not here (<see cref="Holding"/>): this is the record a diff reads.</summary>
    public static List<string> Listed(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var conditions = SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey);
        foreach (var (name, key, _) in Valued(system))
        {
            var holds = YamlValues.ToNumber(sheet.GetValueOrDefault(key)) is > 0;
            var listed = conditions.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (!holds && listed >= 0) conditions.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            else if (holds && listed < 0) conditions.Add(name);
        }

        return conditions;
    }
}
