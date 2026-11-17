using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One boost a source gives. <see cref="Kind"/> is <see cref="BoostKinds"/>; <see cref="Options"/> the attribute keys a choice may
/// take (all of them for a free boost; the one attribute for a fixed boost); <see cref="Picked"/> the attribute the boost goes to now, null
/// while a choice is open; <see cref="Key"/> the sheet key a choice is stored under (null for a fixed boost).</summary>
public sealed record BoostSlot(string Id, string Kind, IReadOnlyList<string> Options, string? Picked, string? Key);

/// <summary>One source of boosts as the sheet shows it: its slots, its flaws (attribute keys), and the trade, when the source offers one
/// (<see cref="TradeKey"/> is where the fixed boosts given up are stored; <see cref="Given"/> is what is stored there and <see cref="Traded"/> what was applied: they differ while the player has ticked too few).</summary>
public sealed record BoostSourceSummary(string Key, string Label, IReadOnlyList<BoostSlot> Slots, IReadOnlyList<string> Flaws,
    BoostTrade? Trade, string? TradeKey, IReadOnlyList<string> TradeOptions, IReadOnlyList<string> Traded, IReadOnlyList<string> Given);

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

    /// <summary>The prefix of every stored boost choice: <c>boost__{source}__{entryId}.{slot}</c> for a source that reads an entry (so a pick
    /// stops counting when the entry changes), <c>boost__{source}__{slot}</c> for a level milestone.</summary>
    public const string BoostKeyPrefix = "boost__";

    /// <summary>A modifier step is two score points on the half-minus-five scale (one on the <c>value</c> scale, which has its own branch).</summary>
    private const int BoostStep = 2;

    public const string BoostOpenKey = "Actor.Rule.BoostOpen";
    public const string BoostSameKey = "Actor.Rule.BoostSame";
    public const string BoostCantKey = "Actor.Rule.BoostCant";
    public const string BoostTradeKey = "Actor.Rule.BoostTrade";

    private static string BoostChoiceKey(BoostSource source, string? entryId, string slot) =>
        BoostKeyPrefix + source.Key + "__" + (entryId == null ? "" : entryId + ".") + slot;

    /// <summary>Takes out the stored picks of sources whose entry is no longer the one they were made for.</summary>
    private void PruneBoostChoices(BoostRules rules, Dictionary<string, object> sheet)
    {
        foreach (var key in sheet.Keys.Where(k => k.StartsWith(BoostKeyPrefix, StringComparison.Ordinal)).ToList())
        {
            var source = rules.Sources.FirstOrDefault(s => key.StartsWith(BoostKeyPrefix + s.Key + "__", StringComparison.Ordinal));
            if (source == null) { sheet.Remove(key); continue; }
            if (source.Field == null) continue;
            var rest = key[(BoostKeyPrefix.Length + source.Key.Length + 2)..];
            var dot = rest.LastIndexOf('.');
            var current = ReferencedEntry(source.Field, sheet)?.Id;
            if (dot < 0 || current == null || !string.Equals(rest[..dot], current, StringComparison.OrdinalIgnoreCase)) sheet.Remove(key);
        }
    }

    /// <summary>Works the boosts out: the score change each attribute gets (from its default), and what the sheet shows. Problems go to
    /// <paramref name="violations"/> when it is given.</summary>
    private (Dictionary<string, int> Deltas, BoostSummary Summary) EvaluateBoosts(BoostRules rules, IReadOnlyDictionary<string, object> sheet, List<RuleViolation>? violations, List<RuleViolation>? warnings = null)
    {
        var score = system.Attributes.ToDictionary(a => a.Key, a => a.Default);
        var onModifierScale = system.Attributes.Any(a => a.IsModifier);
        var pendingHalves = new HashSet<string>();
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
            var entryId = source.Field != null ? ReferencedEntry(source.Field, sheet)?.Id : null;
            string? tradeKey = null;
            var given = new List<string>();
            var traded = new List<string>();
            var tradeOptions = new List<string>();
            if (source.Trade is { } trade && fixedBoosts.Count >= trade.Fixed)
            {
                tradeKey = BoostChoiceKey(source, entryId, "trade");
                tradeOptions = fixedBoosts.Distinct().ToList();
                given = RawList(sheet, tradeKey).Select(MatchAttribute).OfType<AttributeDefinition>().Select(a => a.Key).Distinct().ToList();
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
                var key = BoostChoiceKey(source, entryId, id);
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

            if (open > 0) warnings?.Add(new RuleViolation("boosts", $"{source.Label}: boosts still to place: {open}.", BoostOpenKey, new object?[] { source.Label, open }));
            foreach (var twice in used.GroupBy(u => u).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                violations?.Add(new RuleViolation("boosts", $"{source.Label}: each boost needs a different attribute, and {Label(twice)} has more than one.", BoostSameKey, new object?[] { source.Label, Label(twice) }));
            }

            if (onModifierScale)
            {
                // The values are modifiers: a boost adds 1, a flaw takes 1, and a boost at PartialFrom or above is a mark that the
                // next such boost turns into +1 (the pending partial boost, kept in pendingHalves, shown as a half step).
                // A flaw only lowers the modifier (the book's flaw rule says nothing of a mark), so a pending partial boost stays, as in score mode.
                foreach (var flaw in flaws) score[flaw] -= 1;
                foreach (var attribute in used)
                {
                    if (score[attribute] < rules.PartialFrom) score[attribute] += 1;
                    else if (!pendingHalves.Remove(attribute)) pendingHalves.Add(attribute);
                    else score[attribute] += 1;
                }

                summaries.Add(new BoostSourceSummary(source.Key, source.Label, shown, flaws, source.Trade, tradeKey, tradeOptions, traded, given));
                continue;
            }

            foreach (var flaw in flaws) score[flaw] -= BoostStep;
            foreach (var attribute in used) score[attribute] += AttributeDefinition.HalfMinusFive(score[attribute]) >= rules.PartialFrom ? 1 : BoostStep;
            summaries.Add(new BoostSourceSummary(source.Key, source.Label, shown, flaws, source.Trade, tradeKey, tradeOptions, traded, given));
        }

        var halves = onModifierScale ? pendingHalves : system.Attributes.Where(a => (score[a.Key] - a.Default) % 2 != 0).Select(a => a.Key).ToHashSet();
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
