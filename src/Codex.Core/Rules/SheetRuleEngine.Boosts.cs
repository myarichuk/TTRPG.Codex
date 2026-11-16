using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One boost a source gives. <see cref="Kind"/> is <see cref="BoostKinds"/>; <see cref="Options"/> the attribute keys a choice may
/// take (all of them for a free boost; the one attribute for a fixed boost); <see cref="Picked"/> the attribute the boost goes to now, null
/// while a choice is open; <see cref="Key"/> the sheet key a choice is stored under (null for a fixed boost).</summary>
public sealed record BoostSlot(string Id, string Kind, IReadOnlyList<string> Options, string? Picked, string? Key);

/// <summary>One source of boosts as the sheet shows it: its slots, its flaws (attribute keys), and the trade, when the source offers one
/// (<see cref="TradeKey"/> is where the fixed boosts given up are stored; <see cref="Traded"/> which they are).</summary>
public sealed record BoostSourceSummary(string Key, string Label, IReadOnlyList<BoostSlot> Slots, IReadOnlyList<string> Flaws,
    BoostTrade? Trade, string? TradeKey, IReadOnlyList<string> TradeOptions, IReadOnlyList<string> Traded);

/// <summary>The boosts of a sheet: its sources, and the attributes sitting on half a step (<see cref="Halves"/>).</summary>
public sealed record BoostSummary(IReadOnlyList<BoostSourceSummary> Sources, IReadOnlySet<string> Halves);

/// <summary>The kinds of a <see cref="BoostSlot"/>.</summary>
public static class BoostKinds
{
    public const string Fixed = "fixed";
    public const string Free = "free";
    public const string OneOf = "oneOf";
}

public sealed partial class SheetRuleEngine
{
    // ---- Attribute boosts (docs/ATTRIBUTE_BOOSTS_DESIGN.md) ----
    // Final before anything else reads an attribute: EvaluateAttributes and ApplyAttributeTotals both come here, so ranks, derived values
    // and published values see the score a boost makes. Boosts are not feature grants and not ranks.

    /// <summary>The attribute method that builds scores from boosts.</summary>
    public const string BoostsMethod = "boosts";

    /// <summary>The "entry id" of a source's stored choices: <c>choice__boost-{source}__{slot}</c>.</summary>
    public const string BoostEntryPrefix = "boost-";

    /// <summary>A modifier step is two score points on the half-minus-five scale.</summary>
    private const int BoostStep = 2;

    public const string BoostOpenKey = "Actor.Rule.BoostOpen";
    public const string BoostSameKey = "Actor.Rule.BoostSame";
    public const string BoostCantKey = "Actor.Rule.BoostCant";
    public const string BoostTradeKey = "Actor.Rule.BoostTrade";

    private static string BoostChoiceKey(BoostSource source, string slot) => ChoiceKey(BoostEntryPrefix + source.Key, 1, slot);

    /// <summary>Works the boosts out: the score change each attribute gets (from its default), and what the sheet shows. Problems go to
    /// <paramref name="violations"/> when it is given.</summary>
    private (Dictionary<string, int> Deltas, BoostSummary Summary) EvaluateBoosts(BoostRules rules, IReadOnlyDictionary<string, object> sheet, List<RuleViolation>? violations)
    {
        var score = system.Attributes.ToDictionary(a => a.Key, a => a.Default);
        var summaries = new List<BoostSourceSummary>();
        var level = BoostLevel(sheet);
        string Label(string key) => system.Attributes.First(a => a.Key == key).Label;

        foreach (var source in rules.Sources)
        {
            var fixedBoosts = new List<string>();
            var slots = new List<(string Id, string Kind, List<string> Options)>();
            var flaws = new List<string>();
            if (source.Field != null)
            {
                if (ReferencedEntry(source.Field, sheet) is not { } entry) continue;
                var listed = entry.Properties.GetValueOrDefault(rules.Property);
                var words = listed is string single ? new List<string> { single } : (listed as IEnumerable<object>)?.Select(o => o?.ToString() ?? "").ToList() ?? new List<string>();
                var number = 0;
                foreach (var word in words.Select(w => w.Trim()).Where(w => w.Length > 0))
                {
                    if (word.StartsWith('-')) { if (MatchAttribute(word[1..].Trim()) is { } flaw) flaws.Add(flaw.Key); continue; }
                    if (word.Equals("free", StringComparison.OrdinalIgnoreCase)) { slots.Add(("b" + ++number, BoostKinds.Free, AllAttributeKeys())); continue; }
                    var options = word.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(MatchAttribute).OfType<AttributeDefinition>().Select(a => a.Key).Distinct().ToList();
                    if (options.Count == 1) fixedBoosts.Add(options[0]);
                    else if (options.Count > 1) slots.Add(("b" + ++number, BoostKinds.OneOf, options));
                }
            }
            else if (source.Level is { } from)
            {
                if (level < from) continue;
                for (var i = 1; i <= source.Free; i++) slots.Add(("m" + i, BoostKinds.Free, AllAttributeKeys()));
            }
            else continue;

            // The trade: fixed boosts the player gives up (stored as the attributes), for free ones.
            string? tradeKey = null;
            var traded = new List<string>();
            var tradeOptions = new List<string>();
            if (source.Trade is { } trade && fixedBoosts.Count >= trade.Fixed)
            {
                tradeKey = BoostChoiceKey(source, "trade");
                tradeOptions = fixedBoosts.Distinct().ToList();
                var given = RawList(sheet, tradeKey).Select(MatchAttribute).OfType<AttributeDefinition>().Select(a => a.Key).Distinct().ToList();
                if (given.Count > 0)
                {
                    if (given.Count != trade.Fixed || given.Any(g => !fixedBoosts.Contains(g)))
                    {
                        violations?.Add(new RuleViolation(tradeKey, $"{source.Label}: to trade, give up exactly {trade.Fixed} of its fixed boosts.", BoostTradeKey, new object?[] { source.Label, trade.Fixed, trade.Free }));
                    }
                    else
                    {
                        traded = given;
                        foreach (var g in given) fixedBoosts.Remove(g);
                        for (var i = 1; i <= trade.Free; i++) slots.Add(("t" + i, BoostKinds.Free, AllAttributeKeys()));
                    }
                }
            }

            var shown = new List<BoostSlot>();
            var used = new List<string>(fixedBoosts);
            foreach (var fixedKey in fixedBoosts) shown.Add(new BoostSlot("fixed-" + fixedKey, BoostKinds.Fixed, new[] { fixedKey }, fixedKey, null));
            var open = 0;
            foreach (var (id, kind, options) in slots)
            {
                var key = BoostChoiceKey(source, id);
                var raw = RawList(sheet, key).FirstOrDefault();
                string? picked = null;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var match = MatchAttribute(raw);
                    if (match != null && options.Contains(match.Key)) picked = match.Key;
                    else violations?.Add(new RuleViolation(key, $"{source.Label}: {match?.Label ?? raw} is not one of the choices for this boost.", BoostCantKey, new object?[] { source.Label, match?.Label ?? raw }));
                }

                if (picked == null) open++;
                else used.Add(picked);
                shown.Add(new BoostSlot(id, kind, options, picked, key));
            }

            if (open > 0) violations?.Add(new RuleViolation("boosts", $"{source.Label}: {open} boost{(open == 1 ? "" : "s")} still to place.", BoostOpenKey, new object?[] { source.Label, open }));
            foreach (var twice in used.GroupBy(u => u).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                violations?.Add(new RuleViolation("boosts", $"{source.Label}: each boost needs a different attribute, and {Label(twice)} has more than one.", BoostSameKey, new object?[] { source.Label, Label(twice) }));
            }

            foreach (var flaw in flaws) score[flaw] -= BoostStep;
            foreach (var attribute in used) score[attribute] += AttributeDefinition.HalfMinusFive(score[attribute]) >= rules.PartialFrom ? 1 : BoostStep;
            summaries.Add(new BoostSourceSummary(source.Key, source.Label, shown, flaws, source.Trade, tradeKey, tradeOptions, traded));
        }

        var halves = system.Attributes.Where(a => (score[a.Key] - a.Default) % 2 != 0).Select(a => a.Key).ToHashSet();
        var deltas = system.Attributes.ToDictionary(a => a.Key, a => score[a.Key] - a.Default);
        return (deltas, new BoostSummary(summaries, halves));
    }

    private List<string> AllAttributeKeys() => system.Attributes.Select(a => a.Key).ToList();

    private int BoostLevel(IReadOnlyDictionary<string, object> sheet)
    {
        var levelKey = system.FieldRoles.Level ?? "level";
        var field = system.ActorFields.FirstOrDefault(f => f.Key == levelKey);
        return YamlValues.GetInt(sheet, levelKey) ?? (int)(ToDouble(field?.DefaultValue) ?? 1);
    }
}
