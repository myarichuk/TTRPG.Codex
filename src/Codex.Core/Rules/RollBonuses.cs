using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// The stable ids of opt-in modifiers (docs/ROLL_BONUSES_DESIGN.md): what a roll's <c>optIns</c> names to tick them. The index is the
/// modifier's place in its source's list, so an id survives edits to anything else.
/// </summary>
public static class OptInIds
{
    /// <summary>A feature's modifier: <c>{source id}#{index}</c>.</summary>
    public static string Feature(string sourceId, int index) => $"{sourceId}#{index}";

    /// <summary>A condition effect's modifier: <c>condition:{name}#{index}</c>.</summary>
    public static string Condition(string name, int index) => $"condition:{name}#{index}";

    /// <summary>A modifier a sheet kind lists itself: <c>kind:{kind}#{index}</c>.</summary>
    public static string Kind(string kind, int index) => $"kind:{kind}#{index}";

    /// <summary>A feature that reaches the sheet through a link (docs/CREW_LINKS_DESIGN.md): <c>link:{link key}:{source id}#{index}</c>, so a
    /// crew's entry and the member's own entry of the same id never collide. Links are not built yet; nothing calls this.</summary>
    public static string Link(string linkKey, string sourceId, int index) => $"link:{linkKey}:{Feature(sourceId, index)}";
}

/// <summary>A modifier that counted in a roll: where it came from, what the roll showed for it, how much, and whether the player ticked it
/// (<see cref="Ticked"/>) or it is always on. <see cref="Id"/> is the opt-in's id; null for an always-on one.</summary>
public sealed record RollBonus(string? Id, string Source, string Label, double Amount, bool Ticked);

/// <summary>What a ticked opt-in spends: the pool's sheet key (a <c>uses_*</c> counter's, or a track's or resource's own) and how much.</summary>
public sealed record RollOptInSpend(string Key, double Amount);

/// <summary>How a roll's label says which bonuses the roller chose: <c>Resist (+1d Ambush)</c>. Always-on bonuses are not listed (the roll's own
/// numbers hold them); the sheet and the table's log both use this, so they say the same.</summary>
public static class RollBonusLabel
{
    /// <summary>The label with the ticked bonuses of <paramref name="bonuses"/> after it; unchanged when none was ticked. In a <paramref name="pool"/> roll an
    /// amount is a number of dice (<c>+1d</c>).</summary>
    /// <param name="dice">How a pool's amount of dice is said (<c>+1</c> -> <c>+1d</c>) in the reader's language; null for the English <c>d</c>.</param>
    public static string Of(string label, IEnumerable<RollBonus> bonuses, bool pool, Func<string, string>? dice = null)
    {
        string Amount(double amount)
        {
            var text = (amount >= 0 ? "+" : string.Empty) + amount.ToString(CultureInfo.InvariantCulture);
            return !pool ? text : dice?.Invoke(text) ?? text + "d";
        }

        var chosen = bonuses.Where(b => b.Ticked).Select(b => $"{Amount(b.Amount)} {b.Label}").ToList();
        return chosen.Count == 0 ? label : $"{label} ({string.Join(", ", chosen)})";
    }
}

/// <summary>A tick box a roll offers. <see cref="Amount"/> is text as the box shows it (<c>+1</c>, <c>+1d4</c>, or the formula when it cannot be
/// worked out yet). <see cref="Spends"/> is what ticking it costs, and <see cref="CanPay"/> whether the pool holds that much (true when it
/// costs nothing). <see cref="Ends"/> is the condition rolling with it ticked removes (the name), or null. <see cref="Target"/> is the roll
/// target the modifier reaches (<c>check</c>, <c>attack.weapon</c>), so a page can say which rolls a tick box fits.</summary>
public sealed record RollOptIn(string Id, string Label, string Amount, string Source, RollOptInSpend? Spends, bool CanPay, string? Ends, string? Target = null);

/// <summary>An opt-in modifier as the model offers it, before the sheet's pools are looked at.</summary>
internal sealed record OptInModifier(string Id, string Label, string Amount, string Source, OptInDefinition Definition, string? Condition, string? Target = null);

/// <summary>The load checks of opt-ins and of dice amounts.</summary>
public static partial class OptInChecks
{
    /// <summary>An amount written as dice (<c>1d4</c>, <c>-2d6</c>, <c>d8</c>), not a formula that happens to start with a d.</summary>
    [GeneratedRegex(@"^\s*[+-]?\s*\d*[dD]\d+(?![\w.])")]
    internal static partial Regex DiceAmount();

    /// <summary>What is wrong with one modifier: <paramref name="inCondition"/> is whether it is a condition's effect (the only place
    /// <c>ends</c> means something).</summary>
    public static IEnumerable<string> Problems(SystemDefinition system, string where, string target, string? amount, OptInDefinition? optIn, bool inCondition)
    {
        if (amount != null && DiceAmount().IsMatch(amount) && RollTargets.PoolTargets(system).Contains(target))
        {
            yield return $"{where}: \"{amount}\" is dice, but \"{target}\" is read by a pool roll, where an amount is a number of dice.";
        }

        if (optIn == null) yield break;
        if (optIn.Ends && !inCondition) yield return $"{where}: \"ends\" only works on a condition's effect: rolling with it ticked removes the condition.";
        if (optIn.Spends is { } spends)
        {
            if (spends.Key.Trim().Length == 0) yield return $"{where}: an opt-in that spends needs the \"key\" of the pool.";
            if (!double.TryParse(spends.Amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var cost) || cost < 1 || cost != Math.Floor(cost))
            {
                yield return $"{where}: an opt-in spends a whole number of uses (1 or more), not \"{spends.Amount}\".";
            }
        }
    }
}

/// <summary>The targets a named roll reads (docs/ROLL_BONUSES_DESIGN.md, "Roll targets").</summary>
public static partial class RollTargets
{
    [GeneratedRegex(@"\bmodifiers\s*\(\s*(?:'(?<t>[^']*)'|""(?<t>[^""]*)"")\s*\)")]
    private static partial Regex Literal();

    private static readonly ConditionalWeakTable<RollDefinition, HashSet<string>> Cache = new();

    /// <summary>The literal <c>modifiers('...')</c> names in the roll's <c>pool</c> and <c>add</c> formulas, plus what those formulas
    /// reach through a keyed list that works its own <c>value</c> out: each of its entries and their <c>@group</c>s (the targets
    /// <see cref="SheetModel"/> stacks onto such an entry). Read from the formula text once per definition.</summary>
    public static IReadOnlySet<string> Of(SystemDefinition system, RollDefinition roll) =>
        Cache.GetValue(roll, r => Read(system, r, null));

    /// <summary>What the roll reads for the <paramref name="inputs"/> given so far: as <see cref="Of(SystemDefinition, RollDefinition)"/>, but a keyed list the
    /// roll indexes by an input (<c>actions[input.action]</c>) reaches only the entry that input names, and none until it is chosen. Use it to say which
    /// bonuses count in the roll as it stands; a per-entry bonus (a crew ability's +1 to one action) is then not listed before its action is chosen.</summary>
    public static IReadOnlySet<string> Of(SystemDefinition system, RollDefinition roll, IReadOnlyDictionary<string, object?> inputs) =>
        Read(system, roll, inputs);

    /// <summary>The targets read by any pool roll of the system.</summary>
    public static IReadOnlySet<string> PoolTargets(SystemDefinition system) =>
        system.Rolls.Where(r => r.Pool != null).SelectMany(r => Of(system, r)).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Read(SystemDefinition system, RollDefinition roll, IReadOnlyDictionary<string, object?>? inputs)
    {
        var formulas = (roll.Pool == null ? Enumerable.Empty<string>() : new[] { roll.Pool }).Concat(roll.Add).ToList();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var formula in formulas)
        {
            foreach (Match match in Literal().Matches(formula)) targets.Add(match.Groups["t"].Value);
        }

        foreach (var list in system.SheetKinds.SelectMany(k => k.KeyedLists).Where(l => l.Derive.ContainsKey("value")))
        {
            if (!formulas.Any(f => Regex.IsMatch(f, $@"\b{Regex.Escape(list.Key)}\b"))) continue;
            var chosen = inputs == null ? null : ChosenKeys(list.Key, formulas, inputs);
            foreach (var key in list.Keys.Where(k => k.Parameter == null))
            {
                if (chosen != null && !chosen.Contains(key.Key)) continue;
                targets.UnionWith(SheetModel.Targets(list, key, key.Key));
            }
        }

        return targets;
    }

    /// <summary>The entries of a keyed list a roll reads once its inputs are known: the formulas reach the list only as <c>list[input.name]</c>, so only the
    /// entry that input names counts (none while it is empty). Null when some formula reads the list whole, so every entry counts.</summary>
    private static HashSet<string>? ChosenKeys(string list, IReadOnlyList<string> formulas, IReadOnlyDictionary<string, object?> inputs)
    {
        var escaped = Regex.Escape(list);
        if (formulas.Any(f => Regex.IsMatch(f, $@"\b{escaped}\b(?!\s*\[\s*input\.\w+\s*\])"))) return null;
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var formula in formulas)
        {
            foreach (Match match in Regex.Matches(formula, $@"\b{escaped}\s*\[\s*input\.(?<name>\w+)\s*\]"))
            {
                if (inputs.TryGetValue(match.Groups["name"].Value, out var value) && value?.ToString() is { Length: > 0 } key) chosen.Add(key);
            }
        }

        return chosen;
    }
}
