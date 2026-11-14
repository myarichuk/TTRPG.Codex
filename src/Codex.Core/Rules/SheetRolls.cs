using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Core.Rules;

/// <summary>One input as the roll used it, and who supplied it: <c>roller</c>, <c>gm</c>, <c>weapon</c>, <c>parent</c> or
/// <c>default</c> (nobody did, so the definition's default).</summary>
public sealed record RollInput(object? Value, string From);

/// <summary>Someone joining a group roll: their sheet, and where their dice come from.</summary>
public sealed record RollParticipant(string ActorId, IReadOnlyDictionary<string, object> Sheet, IDiceSource Dice);

/// <summary>A roll to make: which definition, the inputs supplied, and for a group roll who joins it.</summary>
public sealed class SheetRollRequest
{
    public string Definition { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Inputs { get; init; } = new Dictionary<string, object?>();

    /// <summary>Who supplied each input, where the caller knows better than the definition (the GM answered a roller's
    /// input). Unnamed inputs are recorded as the definition says.</summary>
    public IReadOnlyDictionary<string, string> SuppliedBy { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<RollParticipant> Participants { get; init; } = Array.Empty<RollParticipant>();

    /// <summary>The ids of the opt-in bonuses the roller ticked for this roll (<see cref="SheetRolls.OptIns"/> lists what is offered). An id the
    /// roll does not offer is ignored. Only the roll named here counts them: not its follow-ups, and not a group's participants.</summary>
    public IReadOnlyCollection<string> OptIns { get; init; } = Array.Empty<string>();
}

/// <summary>Something a roll asks the table to do (overhaul section 3, "Events → intents"): <c>adjust</c> a track and so on.
/// <see cref="Actor"/> names whose sheet; null is the sheet's own. <see cref="SheetIntents.Apply"/> does what they ask of one
/// sheet and leaves the rest for the table.</summary>
public sealed record SheetIntent(string Kind, string? Actor, IReadOnlyDictionary<string, object?> Fields);

/// <summary>What a roll came to.</summary>
public sealed class SheetRollResult
{
    public string Definition { get; init; } = string.Empty;
    public string? Label { get; init; }

    /// <summary>The named outcome (<c>critical</c>, <c>partial</c>); null when the definition names none. A follow-up whose
    /// outcome replaces its parent's (a confirmed threat) has already replaced it here.</summary>
    public string? Outcome { get; set; }

    /// <summary>How the dice display marks the result: <c>critical</c>, <c>fumble</c> or null. Read from the outcome, never
    /// from the dice.</summary>
    public string? Mark { get; set; }

    public double Total { get; init; }

    /// <summary>The first die's face as it came up, before anything was added.</summary>
    public int? Natural { get; init; }

    /// <summary>Every face rolled, in the order rolled, explosions included.</summary>
    public IReadOnlyList<int> Dice { get; init; } = Array.Empty<int>();

    /// <summary>The dice that count (a die's value includes what it exploded into or imploded by), in the order rolled.</summary>
    public IReadOnlyList<int> Kept { get; init; } = Array.Empty<int>();

    public IReadOnlyList<int> Dropped { get; init; } = Array.Empty<int>();

    /// <summary>What was added to the kept dice: the expression's number and the definition's <c>add</c>.</summary>
    public double Modifier { get; init; }

    /// <summary>The pool had no dice, so the definition's <c>zeroDice</c> roll was made instead.</summary>
    public bool ZeroDice { get; init; }

    public IReadOnlyDictionary<string, RollInput> Inputs { get; init; } = new Dictionary<string, RollInput>();
    public IReadOnlyDictionary<string, object?> Carries { get; init; } = new Dictionary<string, object?>();

    /// <summary>The modifiers that counted in this roll, each with its source, and whether the roller ticked it or it is always on.</summary>
    public IReadOnlyList<RollBonus> Bonuses { get; init; } = Array.Empty<RollBonus>();

    /// <summary>Rolls this one led to (a confirmation), in order.</summary>
    public List<SheetRollResult> FollowUps { get; } = new();

    /// <summary>For a group roll, each participant's own roll.</summary>
    public List<(string ActorId, SheetRollResult Roll)> Participants { get; } = new();

    public List<SheetIntent> Intents { get; } = new();

    /// <summary>This roll's total, then each follow-up's, depth first.</summary>
    public IEnumerable<double> Totals() => FollowUps.SelectMany(f => f.Totals()).Prepend(Total);

    /// <summary>The intents of this roll and every roll it led to, in order.</summary>
    public IEnumerable<SheetIntent> AllIntents() => Intents.Concat(FollowUps.SelectMany(f => f.AllIntents()));
}

/// <summary>A roll that cannot be made: an unknown definition, an input nobody supplied, a formula that fails. The message is
/// for whoever made the roll, or the GM when it is the system's formula.</summary>
public sealed class RollException(string message) : Exception(message);

/// <summary>
/// Makes the system's named rolls (overhaul section 3, "Rolls") against a sheet. Dice come from an <see cref="IDiceSource"/>
/// one face at a time, so a fixture or a test names exactly the faces it wants.
/// <para>The names a roll's formulas read, beyond the sheet's: every input by its key (<c>target</c>) and as
/// <c>input.key</c>; <c>natural</c>, <c>kept</c> (the kept dice added up), <c>dice</c> (every face, for patterns such as
/// <c>count(dice, d => d == 6)</c>) and <c>zeroDice</c>; then <c>total</c> for the outcomes; then <c>outcome</c> and, for
/// a group roll, <c>results</c> (each participant's <c>actor</c>, <c>outcome</c> and <c>total</c>) for what follows.</para>
/// </summary>
public static partial class SheetRolls
{
    /// <summary>How deep follow-up rolls may go: a roll that rolls itself must stop.</summary>
    public const int MaxDepth = 8;

    /// <summary>How many dice a pool may have.</summary>
    public const int MaxPool = 100;

    public static SheetRollResult Roll(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet, SheetRollRequest request, IDiceSource dice)
    {
        var supplied = request.Inputs.ToDictionary(i => i.Key, i => new RollInput(i.Value, request.SuppliedBy.GetValueOrDefault(i.Key) ?? ""), StringComparer.Ordinal);
        return new Roller(system, resolver, request.Participants, request.OptIns.ToHashSet(StringComparer.Ordinal)).Make(Definition(system, request.Definition), sheet, supplied, parent: null, dice, depth: 0);
    }

    /// <summary>The tick boxes the named roll <paramref name="rollKey"/> offers on <paramref name="sheet"/> for the <paramref name="inputs"/> given so far:
    /// every opt-in modifier whose target the roll reads (<see cref="RollTargets.Of"/>) and whose <c>when</c> holds. Inputs not given take
    /// the definition's default, and a <c>when</c> that reads one nobody gave does not hold. A group roll offers none.</summary>
    /// <exception cref="RollException">The system has no such roll.</exception>
    public static IReadOnlyList<RollOptIn> OptIns(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet, string rollKey,
        IReadOnlyDictionary<string, object?> inputs)
    {
        var definition = Definition(system, rollKey);
        if (definition.Participants != null) return Array.Empty<RollOptIn>();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var declared in definition.Inputs.Where(i => i.Default != null)) values[declared.Key] = SheetModel.Plain(YamlValues.Normalize(declared.Default));
        foreach (var (key, value) in inputs) values[key] = SheetModel.Plain(YamlValues.Normalize(value));
        var model = new SheetModel(system, resolver, sheet, values);
        return model.OptIns(RollTargets.Of(system, definition)).Select(o => Offer(sheet, o)).ToList();
    }

    internal static RollOptIn Offer(IReadOnlyDictionary<string, object> sheet, OptInModifier optIn)
    {
        RollOptInSpend? spends = null;
        var canPay = true;
        if (optIn.Definition.Spends is { } spend)
        {
            var amount = double.TryParse(spend.Amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var cost) ? cost : 1;
            var key = PoolKey(sheet, spend.Key);
            spends = new RollOptInSpend(key ?? spend.Key, amount);
            canPay = key != null && (YamlValues.ToNumber(sheet.GetValueOrDefault(key)) ?? 0) >= amount;
        }

        return new RollOptIn(optIn.Id, optIn.Label, optIn.Amount, optIn.Source, spends, canPay, optIn.Definition.Ends ? optIn.Condition : null);
    }

    /// <summary>The sheet key of the pool a spend names: a feature counter's <c>uses_*</c> key, else a track or resource keyed as named; null
    /// when the sheet holds neither.</summary>
    private static string? PoolKey(IReadOnlyDictionary<string, object> sheet, string key)
    {
        var counter = SheetRuleEngine.UsesPrefix + SheetRuleEngine.Slug(key);
        return sheet.ContainsKey(counter) ? counter : sheet.ContainsKey(key) ? key : null;
    }

    private static readonly string[] InputSources = { "roller", "gm", "weapon", "parent" };

    /// <summary>What is wrong with the system's roll definitions, as the loader reports it: shapes, and names of rolls that are
    /// not there. Formulas are checked when they run (a roll that fails says which formula and why).</summary>
    public static IReadOnlyList<string> Problems(SystemDefinition system)
    {
        var problems = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roll in system.Rolls)
        {
            var name = string.IsNullOrWhiteSpace(roll.Key) ? "A roll" : $"The roll \"{roll.Key}\"";
            if (string.IsNullOrWhiteSpace(roll.Key)) problems.Add("A roll has no key.");
            else if (!keys.Add(roll.Key)) problems.Add($"{name} is defined twice.");

            var shapes = new[] { roll.Dice != null, roll.Pool != null, roll.Participants != null }.Count(b => b);
            if (shapes != 1) problems.Add($"{name} needs exactly one of dice, pool or participants.");
            if (roll.Dice != null)
            {
                try
                {
                    var parsed = DiceExpressionParser.Parse(roll.Dice);
                    if (parsed.Dice.Count != 1 || parsed.Dice[0].Sign < 0 || parsed.Dice[0].Explodes || parsed.Dice[0].SuccessAt != null)
                    {
                        problems.Add($"{name}: dice is one group of dice (1d20, 3d6, 4d6kh3); explode and keep are the definition's own.");
                    }
                }
                catch (FormatException ex)
                {
                    problems.Add($"{name}: {ex.Message}");
                }
            }

            if (roll.Pool != null && (roll.Die == null || !DieSides().IsMatch(roll.Die))) problems.Add($"{name}: a pool needs a die (d6).");
            if (roll.Participants is { } group && !system.Rolls.Any(r => r.Key == group.Roll)) problems.Add($"{name}: participants roll \"{group.Roll}\", which is not defined.");
            foreach (var then in roll.Then.Where(t => t.Roll != null && !system.Rolls.Any(r => r.Key == t.Roll)))
            {
                problems.Add($"{name} leads to \"{then.Roll}\", which is not defined.");
            }

            foreach (var input in roll.Inputs.Where(i => !InputSources.Contains(i.From)))
            {
                problems.Add($"{name}: input \"{input.Key}\" comes from \"{input.From}\"; it can come from {string.Join(", ", InputSources)}.");
            }
        }

        return problems;
    }

    private static RollDefinition Definition(SystemDefinition system, string key) =>
        system.Rolls.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal))
        ?? throw new RollException($"The system has no roll called \"{key}\".");

    [GeneratedRegex(@"^\s*d(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DieSides();

    private sealed class Roller(SystemDefinition system, IEntryResolver resolver, IReadOnlyList<RollParticipant> participants, IReadOnlySet<string> ticked)
    {
        public SheetRollResult Make(RollDefinition definition, IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, RollInput> supplied,
            IReadOnlyDictionary<string, RollInput>? parent, IDiceSource dice, int depth)
        {
            if (depth > MaxDepth) throw new RollException($"{Name(definition)}: rolls lead to rolls more than {MaxDepth} deep.");

            // The model reads its inputs through this dictionary, so an input worked out from the sheet (the weapon's threat
            // range) is in place before any formula runs.
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            // Only the roll the request names counts the ticks: a follow-up or a group's participant is a roll of its own.
            var model = new SheetModel(system, resolver, sheet, values, depth == 0 ? ticked : null);
            var inputs = Inputs(definition, model, supplied, parent);
            foreach (var (key, input) in inputs) values[key] = input.Value;

            var names = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (key, input) in inputs)
            {
                if (Bindable(input.Value) is { } value) names[key] = value;
            }

            return definition.Participants != null
                ? Group(definition, model, inputs, names, depth)
                : Single(definition, sheet, model, inputs, names, dice, depth);
        }

        private SheetRollResult Single(RollDefinition definition, IReadOnlyDictionary<string, object> sheet, SheetModel model,
            Dictionary<string, RollInput> inputs, Dictionary<string, object> names, IDiceSource dice, int depth)
        {
            var thrown = Throw(definition, model, names, dice);
            names["natural"] = (double)(thrown.Natural ?? 0);
            names["kept"] = (double)thrown.Kept.Sum();
            names["dice"] = new FormulaList(thrown.Faces.Select(f => (FormulaItem)new FormulaNumber(f)).ToList());
            names["zeroDice"] = thrown.ZeroDice;

            var added = thrown.Modifier + definition.Add.Sum(formula => Number(model, definition, "add", formula, names));
            var total = thrown.Kept.Sum() + added;
            names["total"] = total;

            var outcome = Outcome(definition, model, names);
            var targets = RollTargets.Of(system, definition);
            var result = new SheetRollResult
            {
                Bonuses = model.Bonuses(targets),
                Definition = definition.Key,
                Label = definition.Label,
                Outcome = outcome?.Name,
                Mark = Mark(outcome),
                Total = total,
                Natural = thrown.Natural,
                Dice = thrown.Faces,
                Kept = thrown.Kept,
                Dropped = thrown.Dropped,
                Modifier = added,
                ZeroDice = thrown.ZeroDice,
                Inputs = inputs,
                Carries = definition.Carries.ToDictionary(c => c, c => inputs.TryGetValue(c, out var input) ? input.Value : null, StringComparer.Ordinal)
            };

            FollowUps(definition, sheet, model, inputs, names, result, dice, depth);
            if (depth == 0) result.Intents.InsertRange(0, TickedIntents(definition, sheet, model, targets));
            return result;
        }

        /// <summary>What the opt-ins ticked for this roll cost: an <c>adjust</c> of the pool for a <c>spends</c> (the roll is refused when the pool is
        /// short), and a <c>condition</c> <c>remove</c> for an <c>ends</c>. The same intents any handler gives, so the table and the sheet page
        /// apply them as they do the rest.</summary>
        private IEnumerable<SheetIntent> TickedIntents(RollDefinition definition, IReadOnlyDictionary<string, object> sheet, SheetModel model, IReadOnlySet<string> targets)
        {
            if (ticked.Count == 0) yield break;
            foreach (var optIn in model.OptIns(targets).Where(o => ticked.Contains(o.Id)))
            {
                if (Offer(sheet, optIn) is { Spends: { } spend } offer)
                {
                    if (!offer.CanPay) throw new RollException($"{Name(definition)}: {optIn.Label} spends {spend.Amount} from \"{spend.Key}\", which has not enough left.");
                    yield return new SheetIntent("adjust", null, new Dictionary<string, object?> { ["track"] = spend.Key, ["by"] = -spend.Amount });
                }

                if (optIn.Definition.Ends && optIn.Condition != null)
                {
                    yield return new SheetIntent("condition", null, new Dictionary<string, object?> { ["remove"] = optIn.Condition });
                }
            }
        }

        private SheetRollResult Group(RollDefinition definition, SheetModel model, Dictionary<string, RollInput> inputs, Dictionary<string, object> names, int depth)
        {
            var each = Definition(system, definition.Participants!.Roll);
            if (participants.Count == 0) throw new RollException($"{Name(definition)}: nobody is taking part.");

            var rolls = participants.Select(p => (p.ActorId, Roll: Make(each, p.Sheet, inputs, parent: null, p.Dice, depth + 1))).ToList();
            var rank = (SheetRollResult r) => each.Outcomes.FindIndex(o => o.Name == r.Outcome) is var i and >= 0 ? i : int.MaxValue;
            var worst = string.Equals(definition.Result, "worst", StringComparison.OrdinalIgnoreCase);
            var counted = (worst ? rolls.OrderByDescending(r => rank(r.Roll)) : rolls.OrderBy(r => rank(r.Roll))).First().Roll;

            var result = new SheetRollResult
            {
                Definition = definition.Key,
                Label = definition.Label,
                Outcome = counted.Outcome,
                Mark = counted.Mark,
                Total = counted.Total,
                Inputs = inputs,
                Carries = counted.Carries
            };
            result.Participants.AddRange(rolls);

            names["outcome"] = counted.Outcome ?? "";
            names["total"] = counted.Total;
            names["results"] = new FormulaList(rolls.Select(r => new FormulaItem(name => name switch
            {
                "actor" => r.ActorId,
                "outcome" => r.Roll.Outcome,
                "total" => r.Roll.Total,
                "natural" => r.Roll.Natural is { } n ? (double)n : null,
                _ => null
            })).ToList());

            // A group's follow-ups are intents only: there is no one die source a further roll would be the group's.
            foreach (var then in definition.Then.Where(t => Holds(model, definition, "then", t.When, names)))
            {
                if (then.Roll != null) throw new RollException($"{Name(definition)}: a group roll cannot lead to another roll.");
                result.Intents.AddRange(then.Intents.Select(i => Intent(definition, model, inputs, names, i)));
            }

            return result;
        }

        private void FollowUps(RollDefinition definition, IReadOnlyDictionary<string, object> sheet, SheetModel model, Dictionary<string, RollInput> inputs,
            Dictionary<string, object> names, SheetRollResult result, IDiceSource dice, int depth)
        {
            names["outcome"] = result.Outcome ?? "";
            foreach (var then in definition.Then.Where(t => Holds(model, definition, "then", t.When, names)))
            {
                if (then.Roll is { } next)
                {
                    var nextDefinition = Definition(system, next);
                    // A follow-up sees the parent's inputs (the weapon still in hand); those it declares from: parent are the parent's.
                    var passed = inputs.ToDictionary(i => i.Key, i => i.Value with { From = "parent" }, StringComparer.Ordinal);
                    var followUp = Make(nextDefinition, sheet, passed, inputs, dice, depth + 1);
                    result.FollowUps.Add(followUp);
                    if (nextDefinition.Outcomes.FirstOrDefault(o => o.Name == followUp.Outcome) is { ReplacesParent: true })
                    {
                        result.Outcome = followUp.Outcome;
                        result.Mark = followUp.Mark;
                    }
                }

                result.Intents.AddRange(then.Intents.Select(i => Intent(definition, model, inputs, names, i)));
            }
        }

        // ---- Inputs ----

        private Dictionary<string, RollInput> Inputs(RollDefinition definition, SheetModel model, IReadOnlyDictionary<string, RollInput> supplied,
            IReadOnlyDictionary<string, RollInput>? parent)
        {
            var inputs = new Dictionary<string, RollInput>(StringComparer.Ordinal);
            foreach (var (key, input) in supplied)
            {
                var declared = definition.Inputs.FirstOrDefault(i => i.Key == key);
                inputs[key] = new RollInput(SheetModel.Plain(YamlValues.Normalize(input.Value)), input.From is { Length: > 0 } from ? from : declared?.From ?? "roller");
            }

            foreach (var declared in definition.Inputs)
            {
                if (inputs.TryGetValue(declared.Key, out var given) && given.Value != null)
                {
                    if (declared.Options is { Count: > 0 } options && !options.Contains(given.Value.ToString() ?? "", StringComparer.OrdinalIgnoreCase))
                    {
                        throw new RollException($"{Name(definition)}: \"{given.Value}\" is not one of {string.Join(", ", options)} for {declared.Label ?? declared.Key}.");
                    }

                    continue;
                }

                if (declared.From == "parent" && parent != null && parent.TryGetValue(declared.Key, out var inherited))
                {
                    inputs[declared.Key] = inherited with { From = "parent" };
                    continue;
                }

                if (declared.From == "weapon" && Weapon(model, inputs) is { } weapon && SheetModel.Plain(weapon.Get(declared.Property ?? declared.Key)) is { } property)
                {
                    inputs[declared.Key] = new RollInput(property, "weapon");
                    continue;
                }

                if (declared.Default != null)
                {
                    inputs[declared.Key] = new RollInput(SheetModel.Plain(YamlValues.Normalize(declared.Default)), "default");
                    continue;
                }

                throw new RollException($"{Name(definition)} needs {declared.Label ?? declared.Key}.");
            }

            return inputs;
        }

        /// <summary>The item the <c>weapon</c> input names: a pick on the sheet, or failing that the library's entry.</summary>
        private FormulaItem? Weapon(SheetModel model, Dictionary<string, RollInput> inputs)
        {
            if (inputs.GetValueOrDefault("weapon")?.Value?.ToString() is not { Length: > 0 } id) return null;
            if (model.Item(id) is { } item) return item;
            foreach (var type in model.Kind?.Lists.Select(l => l.Type).OfType<string>().Distinct() ?? Enumerable.Empty<string>())
            {
                if (resolver.Resolve(type, id) is { } entry) return new FormulaItem(name => name == "id" ? entry.Id : name == "name" ? entry.Name : entry.Properties.GetValueOrDefault(name));
            }

            return null;
        }

        // ---- Dice ----

        private sealed record Thrown(List<int> Faces, List<int> Kept, List<int> Dropped, int? Natural, int Modifier, bool ZeroDice);

        private static Thrown Throw(RollDefinition definition, SheetModel model, Dictionary<string, object> names, IDiceSource dice)
        {
            int count, sides, modifier = 0;
            var keep = definition.Keep;
            var zeroDice = false;
            if (definition.Dice != null)
            {
                ParsedDiceExpression parsed;
                try
                {
                    parsed = DiceExpressionParser.Parse(definition.Dice);
                }
                catch (FormatException ex)
                {
                    throw new RollException($"{Name(definition)}: {ex.Message}");
                }

                if (parsed.Dice.Count != 1 || parsed.Dice[0].Sign < 0 || parsed.Dice[0].Explodes || parsed.Dice[0].SuccessAt != null)
                {
                    throw new RollException($"{Name(definition)}: dice is one group of dice (1d20, 3d6, 4d6kh3); explode and keep are the definition's own.");
                }

                var term = parsed.Dice[0];
                (count, sides, modifier) = (term.Count, term.Sides, parsed.Modifier);
                if (term.KeepMode is { } mode) keep = mode == 'h' ? new KeepDefinition { Highest = term.KeepCount } : new KeepDefinition { Lowest = term.KeepCount };
            }
            else if (definition.Pool != null)
            {
                sides = definition.Die is { } die && DieSides().Match(die) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                    : throw new RollException($"{Name(definition)}: a pool needs a die (d6).");
                count = (int)Math.Floor(Number(model, definition, "pool", definition.Pool, names));
                if (count > MaxPool) throw new RollException($"{Name(definition)}: {count} dice is more than {MaxPool}.");
                if (count < 1)
                {
                    var zero = definition.ZeroDice ?? throw new RollException($"{Name(definition)}: the pool has no dice.");
                    (count, zeroDice) = (Math.Clamp(zero.Roll, 1, MaxPool), true);
                    keep = string.Equals(zero.Keep, "highest", StringComparison.OrdinalIgnoreCase) ? new KeepDefinition { Highest = 1 } : new KeepDefinition { Lowest = 1 };
                }
            }
            else
            {
                throw new RollException($"{Name(definition)}: neither dice nor a pool.");
            }

            if (sides < 2) throw new RollException($"{Name(definition)}: a die has at least two faces.");

            var faces = new List<int>();
            var values = new List<int>();
            int? natural = null;
            for (var i = 0; i < count; i++)
            {
                var face = dice.Roll(sides);
                faces.Add(face);
                natural ??= face;
                var value = face + Chain(definition.Explode, face, sides, dice, faces) - Chain(definition.Implode, face, sides, dice, faces);
                values.Add(value);
            }

            var (kept, dropped) = Keep(values, keep);
            return new Thrown(faces, kept, dropped, natural, modifier, zeroDice);
        }

        /// <summary>What a die showing <paramref name="face"/> adds by exploding (or takes away by imploding): another die
        /// while the last one shows <see cref="RerollDefinition.On"/>, at most <see cref="RerollDefinition.Times"/> in a row.
        /// An imploding die's own dice never explode, and the reverse.</summary>
        private static int Chain(RerollDefinition? reroll, int face, int sides, IDiceSource dice, List<int> faces)
        {
            if (reroll == null || face != reroll.On) return 0;
            var times = Math.Min(reroll.Times ?? StandardDiceRoller.MaxExplosions, StandardDiceRoller.MaxExplosions);
            var sum = 0;
            for (var (last, n) = (face, 0); last == reroll.On && n < times; n++)
            {
                last = dice.Roll(sides);
                faces.Add(last);
                sum += last;
            }

            return sum;
        }

        private static (List<int> Kept, List<int> Dropped) Keep(List<int> values, KeepDefinition? keep)
        {
            var take = keep?.Highest ?? keep?.Lowest;
            if (take == null) return (values.ToList(), new List<int>());
            var order = Enumerable.Range(0, values.Count);
            var chosen = (keep!.Highest != null ? order.OrderByDescending(i => values[i]) : order.OrderBy(i => values[i])).Take(take.Value).ToHashSet();
            return (Enumerable.Range(0, values.Count).Where(chosen.Contains).Select(i => values[i]).ToList(),
                Enumerable.Range(0, values.Count).Where(i => !chosen.Contains(i)).Select(i => values[i]).ToList());
        }

        // ---- Outcomes and intents ----

        private static RollOutcomeDefinition? Outcome(RollDefinition definition, SheetModel model, Dictionary<string, object> names) =>
            definition.Outcomes.FirstOrDefault(o => Holds(model, definition, $"the outcome \"{o.Name}\"", o.When, names));

        private static string? Mark(RollOutcomeDefinition? outcome) =>
            outcome?.Mark ?? (outcome?.Name.ToLowerInvariant() is "critical" or "fumble" ? outcome.Name.ToLowerInvariant() : null);

        private static SheetIntent Intent(RollDefinition definition, SheetModel model, Dictionary<string, RollInput> inputs, Dictionary<string, object> names,
            Dictionary<string, object> intent)
        {
            if (intent.Count != 1) throw new RollException($"{Name(definition)}: an intent is one kind (adjust, set...) and its fields.");
            var (kind, raw) = intent.First();
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            string? actor = null;
            var map = YamlValues.Normalize(raw) as IDictionary<string, object>
                ?? throw new RollException($"{Name(definition)}: the intent \"{kind}\" needs its fields ({{ track, by }}).");
            foreach (var (key, value) in map)
            {
                switch (key)
                {
                    // An actor named by an input (leader) is whoever that input names; otherwise it is a sheet id.
                    case "actor":
                        actor = value?.ToString() is { } who && inputs.TryGetValue(who, out var named) ? named.Value?.ToString() : value?.ToString();
                        break;
                    // How much, worked out now: "6 - kept" is the roll's to say, not the table's.
                    case "by" or "to" when value is string formula:
                        fields[key] = Number(model, definition, kind, formula, names);
                        break;
                    default:
                        fields[key] = SheetModel.Plain(value);
                        break;
                }
            }

            return new SheetIntent(kind, actor, fields);
        }

        // ---- Formulas ----

        private static bool Holds(SheetModel model, RollDefinition definition, string what, string? when, Dictionary<string, object> names) =>
            when == null || Evaluate(model, definition, what, when, names) is true;

        private static double Number(SheetModel model, RollDefinition definition, string what, string formula, Dictionary<string, object> names)
        {
            var value = Evaluate(model, definition, what, formula, names);
            try
            {
                return SheetFormulas.Number(value);
            }
            catch (FormulaException ex)
            {
                throw new RollException($"{Name(definition)}: {what} ({formula}) can't be worked out: {ex.Message}.");
            }
        }

        private static object? Evaluate(SheetModel model, RollDefinition definition, string what, string formula, Dictionary<string, object> names)
        {
            try
            {
                return model.Run(formula, names);
            }
            catch (FormulaException ex)
            {
                throw new RollException($"{Name(definition)}: {what} ({formula}) can't be worked out: {ex.Message}.");
            }
        }

        /// <summary>An input as a formula reads it: a number, a string, a flag. Anything else (a map) is not bound by name.</summary>
        private static object? Bindable(object? value) => value switch
        {
            double or string or bool => value,
            _ => null
        };
    }

    private static string Name(RollDefinition definition) => definition.Label ?? definition.Key;
}
