using System.Globalization;
using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A feature's typed contribution to a number: <c>{ target, amount, type, when }</c>.</summary>
public sealed record FeatureModifier(string Target, string Amount, string? Type, string? When);

/// <summary>Entries a feature puts on a list from <see cref="Level"/> on (always, when null). On a ranked list (skills, saving throws; see
/// <see cref="SkillRules.Ranks"/>) it puts the entries at <see cref="Rank"/> (a rank key or number; none means the first rank), or raises
/// them <see cref="Raise"/> ranks (<c>raise: 1</c>; <see cref="Rank"/> and <see cref="Raise"/> exclude each other), or, with a
/// <see cref="Choice"/>, leaves the entries to the player.</summary>
public sealed record FeatureAdd(string To, IReadOnlyList<string> Entries, int? Level, string? Rank = null, int Raise = 0, FeatureChoice? Choice = null);

/// <summary>A choice a feature leaves to the player: <c>{ to, choice, choose, from, minRank, maxRank, rank | raise }</c>.
/// <see cref="Id"/> names it within its entry (<see cref="IdGiven"/>: the data said so; else it is the row's slug and level);
/// <see cref="Choose"/> is how many keys; <see cref="From"/> the keys it offers (null: any), narrowed by <see cref="Filter"/> (a key's own
/// field, such as its attribute, against the values listed); <see cref="MinRank"/> and <see cref="MaxRank"/> the ranks (key or number) a
/// key must already hold to be offered. Where its picks are stored: see <see cref="SheetRuleEngine.ChoiceKey"/>.</summary>
public sealed record FeatureChoice(string Id, bool IdGiven, int Choose, IReadOnlyList<string>? From, IReadOnlyDictionary<string, IReadOnlyList<string>>? Filter,
    string? MinRank, string? MaxRank);

/// <summary>One refill rule of a feature's counter as its data states it: <c>{ on, amount, when }</c>. <see cref="On"/> is a rest's
/// name or a moment (<see cref="FeatureRecovery.Initiative"/>, <see cref="FeatureRecovery.Dawn"/>, <see cref="FeatureRecovery.Manual"/>);
/// <see cref="Amount"/> is <c>all</c>, a number, a formula or dice; <see cref="When"/> is <c>empty</c>, a formula about the
/// character, or null.</summary>
public sealed record FeatureRecover(string On, string Amount, string? When);

/// <summary>What a feature spends from a pool: the pool's <see cref="Key"/> (a feature counter's <c>key</c>, or a pool's own
/// sheet key) and the <see cref="Amount"/> (a number, a formula, or <c>ask</c>: the player chooses each time).</summary>
public sealed record FeatureSpend(string Key, string Amount)
{
    /// <summary>The amount is the player's to choose each time.</summary>
    public bool Ask => string.Equals(Amount, FeatureRecovery.AskAmount, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A feature's counter of uses as its data states it. <see cref="Recovers"/> are the <c>recovers</c> rules;
/// <see cref="RecoversOn"/> is the shorthand for a full refill on each rest named. <see cref="GmRegain"/>: <c>regain: gm</c>.</summary>
public sealed record FeatureUses(string? Key, string Max, IReadOnlyList<string> RecoversOn, IReadOnlyList<FeatureRecover>? Recovers = null, bool GmRegain = false)
{
    /// <summary>Every refill rule: <see cref="RecoversOn"/> as full refills, then <see cref="Recovers"/>.</summary>
    public IReadOnlyList<FeatureRecover> AllRecovers =>
        RecoversOn.Select(on => new FeatureRecover(on, FeatureRecovery.AllAmount, null)).Concat(Recovers ?? Array.Empty<FeatureRecover>()).ToList();
}

/// <summary>A feature the sheet has now: a row of a class, subclass or species entry at or below the sheet's level, or a
/// picked entry that is one. <see cref="View"/> is the sheet as that feature's class sees it (its own class level). <see cref="Copy"/>
/// is which copy of a repeatable pick this is (1 for the first): a choice of the second copy of a feat is stored apart.</summary>
public sealed record ActiveFeature(string Name, string? Description, ContentEntry Source, int? Level, IReadOnlyDictionary<string, object> View, string? LevelKey,
    IReadOnlyList<FeatureModifier> Modifiers, IReadOnlyList<FeatureAdd> Adds, FeatureUses? Uses, FeatureSpend? Spends = null, int Copy = 1)
{
    public bool Grants => Modifiers.Count > 0 || Adds.Count > 0 || Uses != null || Spends != null;
}

/// <summary>One feature's contribution that reached a target, for the sheet's working.</summary>
public sealed record GrantedModifier(string Target, ActiveFeature Feature, string Type, int Amount, bool Counted);

/// <summary>A feature's counter on this sheet: its keys, its worked-out maximum and the rests that refill it
/// (<see cref="RecoversOn"/>: the rests among <see cref="Recovers"/>, never a moment such as initiative). <see cref="Recovers"/>
/// are all its refill rules with amounts worked out. <see cref="GmRegain"/>: only the GM adds a use back by hand.
/// <see cref="LegacyKeys"/> are the keys earlier versions of the data gave the counter, whose value is carried over once.</summary>
public sealed record FeatureCounter(ActiveFeature Feature, string Key, string MaxKey, int Max, IReadOnlyList<string> RecoversOn,
    IReadOnlyList<RecoverRule>? Recovers = null, bool GmRegain = false, IReadOnlyList<string>? LegacyKeys = null)
{
    /// <summary>Whether this viewer may press Regain: everyone who may use the counter, unless <c>regain: gm</c>.</summary>
    public bool CanRegain(bool isDm) => isDm || !GmRegain;
}

/// <summary>A feature that spends from a pool: the <see cref="Cost"/> of one press, and whether the player chooses
/// the amount (<see cref="Ask"/>: <c>Cost.Amount</c> is then the least, 1).</summary>
public sealed record FeatureSpender(ActiveFeature Feature, ActionCost Cost, bool Ask);

/// <summary>A refill rule on the sheet, with its amount worked out: <see cref="Amount"/> is <c>all</c>, a number or dice
/// (<c>1d6+1</c>); <see cref="WhenEmpty"/>: only when nothing is left.</summary>
public sealed record RecoverRule(string On, string Amount, bool WhenEmpty);

/// <summary>What a refill did to one pool: the value it ends at, what was regained, and the dice rolled for it (null: none).</summary>
public sealed record RecoverOutcome(int Value, int Regained, IReadOnlyList<(string Dice, int Total)> Rolls);

/// <summary>
/// The refill rules of a feature's counter. A sheet records them in the counter's <c>_Rest</c> key as
/// <c>Short Rest=1|Long Rest|initiative=1d4!empty</c>: items split by <c>|</c>, <c>on</c> then <c>=amount</c> (a bare name
/// means all), and a trailing <c>!empty</c> for "only when none is left". The table reads them without evaluating the sheet,
/// so formulas are worked out when the sheet is evaluated and only numbers and dice are stored.
/// </summary>
public static class FeatureRecovery
{
    public const string AllAmount = "all";
    public const string AskAmount = "ask";
    public const string EmptyWhen = "empty";
    public const string Initiative = "initiative";
    public const string Dawn = "dawn";
    public const string Manual = "manual";

    /// <summary>The words a refill may name that are not rests: the moment of rolling initiative, a GM-called dawn, and a
    /// refill only a person does by hand.</summary>
    public static readonly IReadOnlyList<string> Moments = new[] { Initiative, Dawn, Manual };

    public static bool IsMoment(string on) => Moments.Contains(on, StringComparer.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex DiceShape = new(@"^\s*\d*d\d+\s*([+-]\s*\d+)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Whether <paramref name="amount"/> is dice (<c>1d6+1</c>, <c>d4</c>) rather than a formula.</summary>
    public static bool IsDice(string amount) => DiceShape.IsMatch(amount);

    /// <summary>The rules as the sheet stores them; empty text for no rules.</summary>
    public static string Encode(IEnumerable<RecoverRule> rules) =>
        string.Join("|", rules.Select(r => r.Amount == AllAmount && !r.WhenEmpty ? r.On : $"{r.On}={r.Amount}{(r.WhenEmpty ? "!" + EmptyWhen : string.Empty)}"));

    /// <summary>The rules a sheet records for the counter <paramref name="key"/> (see <see cref="Encode"/>).</summary>
    public static IReadOnlyList<RecoverRule> Decode(IReadOnlyDictionary<string, object> sheet, string key)
    {
        var rules = new List<RecoverRule>();
        foreach (var item in (YamlValues.GetString(sheet, key + SheetRuleEngine.UsesRestSuffix) ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = item;
            var whenEmpty = text.EndsWith("!" + EmptyWhen, StringComparison.OrdinalIgnoreCase);
            if (whenEmpty) text = text[..^(EmptyWhen.Length + 1)];
            var split = text.IndexOf('=');
            var on = (split < 0 ? text : text[..split]).Trim();
            var amount = split < 0 ? AllAmount : text[(split + 1)..].Trim();
            if (on.Length > 0) rules.Add(new RecoverRule(on, amount.Length == 0 ? AllAmount : amount, whenEmpty));
        }

        return rules;
    }

    /// <summary>The rules of the counter that name <paramref name="moment"/> (a rest, or initiative, dawn).</summary>
    public static IReadOnlyList<RecoverRule> For(IReadOnlyDictionary<string, object> sheet, string key, string moment) =>
        Decode(sheet, key).Where(r => string.Equals(r.On, moment, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Applies the rules in order to a pool holding <paramref name="current"/> of <paramref name="max"/>: <c>all</c>
    /// fills it, a number adds that many, dice are rolled by <paramref name="roll"/>; a rule marked empty applies only when
    /// nothing is left at that point. Never above the maximum.</summary>
    public static RecoverOutcome Apply(IEnumerable<RecoverRule> rules, int current, int max, Func<string, int> roll)
    {
        var value = Math.Clamp(current, 0, Math.Max(max, 0));
        var rolls = new List<(string, int)>();
        foreach (var rule in rules)
        {
            if (rule.WhenEmpty && value > 0) continue;
            var gained = rule.Amount.Equals(AllAmount, StringComparison.OrdinalIgnoreCase) ? max - value
                : int.TryParse(rule.Amount, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number
                : IsDice(rule.Amount) ? RollOnce(rule.Amount, roll, rolls)
                : 0;
            value = Math.Clamp(value + Math.Max(gained, 0), 0, Math.Max(max, 0));
        }

        return new RecoverOutcome(value, value - Math.Clamp(current, 0, Math.Max(max, 0)), rolls);
    }

    private static int RollOnce(string dice, Func<string, int> roll, List<(string, int)> rolls)
    {
        var clean = dice.Replace(" ", string.Empty);
        var total = roll(clean);
        rolls.Add((clean, total));
        return total;
    }
}

public sealed partial class SheetRuleEngine
{
    /// <summary>Sheet keys that remember what features put on the sheet, so it can be taken off again:
    /// <c>granted__darkvision</c> (how much was added to the field) and <c>granted__grantedSpells</c> (the entries added).</summary>
    public const string GrantedPrefix = "granted__";

    /// <summary>A feature counter's keys: <c>uses_{id}</c> (left), <c>uses_{id}_Max</c>, and <c>uses_{id}_Rest</c>, its refill
    /// rules (see <see cref="FeatureRecovery"/>), and <c>uses_{id}_Label</c>, its name, which the table reads without evaluating the sheet.</summary>
    public const string UsesPrefix = "uses_";
    public const string UsesMaxSuffix = "_Max";
    public const string UsesRestSuffix = "_Rest";

    /// <summary><c>uses_{id}_Label</c>: the counter's name (its owning feature's), for the table's log, which reads no features.</summary>
    public const string UsesLabelSuffix = "_Label";

    /// <summary>Whether a sheet key is a feature counter's current value.</summary>
    public static bool IsUsesKey(string key) =>
        key.StartsWith(UsesPrefix, StringComparison.Ordinal) && !key.EndsWith(UsesMaxSuffix, StringComparison.Ordinal)
        && !key.EndsWith(UsesRestSuffix, StringComparison.Ordinal) && !key.EndsWith(UsesLabelSuffix, StringComparison.Ordinal);

    /// <summary>The name a counter goes by in the log: the label the sheet recorded, else its key made readable.</summary>
    public static string UsesLabel(IReadOnlyDictionary<string, object> sheet, string key) =>
        YamlValues.GetString(sheet, key + UsesLabelSuffix) is { Length: > 0 } label ? label : key[UsesPrefix.Length..].Replace('_', ' ');

    /// <summary>The rests that refill the feature counter <paramref name="key"/>, as the sheet records them.</summary>
    public static IReadOnlyList<string> UsesRests(IReadOnlyDictionary<string, object> sheet, string key) =>
        FeatureRecovery.Decode(sheet, key).Select(r => r.On).Where(on => !FeatureRecovery.IsMoment(on)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The features the sheet has now, in the order the system lists their sources.</summary>
    public IReadOnlyList<ActiveFeature> ActiveFeatures(IReadOnlyDictionary<string, object> sheet)
    {
        if (Rules is not { Features.Count: > 0 } rules) return Array.Empty<ActiveFeature>();

        var views = ClassViews(sheet, new List<ClassLevel>());
        var perClass = new HashSet<string>(StringComparer.Ordinal);
        if (rules.Multiclass is { } multiclass)
        {
            perClass.Add(multiclass.ClassField);
            if (multiclass.SubclassField != null) perClass.Add(multiclass.SubclassField);
        }

        var found = new List<ActiveFeature>();
        var copies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // across every list: a feat in two lists is copy 1 and copy 2
        foreach (var source in rules.Features)
        {
            if (source.List != null)
            {
                var list = rules.Lists.FirstOrDefault(l => l.Key == source.List);
                if (list == null) continue;
                foreach (var raw in RawList(sheet, list.Key))
                {
                    if (resolver.Resolve(list.TargetEntityType, ParsePick(raw).Id) is not { } entry) continue;
                    // The second Skilled is copy 2: what it asks the player to choose is stored apart from the first's.
                    var copy = copies[entry.Id] = copies.GetValueOrDefault(entry.Id) + 1;
                    AddFeatures(found, source, entry, views[0], copy);
                }
            }
            else if (source.Field != null)
            {
                foreach (var view in perClass.Contains(source.Field) ? views : views.Take(1))
                {
                    if (ReferencedEntry(source.Field, view) is { } entry) AddFeatures(found, source, entry, view);
                }
            }
        }

        return found;
    }

    private static void AddFeatures(List<ActiveFeature> found, FeatureSource source, ContentEntry entry, IReadOnlyDictionary<string, object> view, int copy = 1)
    {
        if (source.Property == null)
        {
            found.Add(Feature(entry.Name, entry.Description, entry, null, view, source.ByField, entry.Properties, copy));
            return;
        }

        if (!entry.Properties.TryGetValue(source.Property, out var raw) || raw is not IEnumerable<object> rows) return;
        var reached = source.ByField == null ? null : ToDouble(view.GetValueOrDefault(source.ByField));
        foreach (var row in rows.OfType<IDictionary<string, object>>())
        {
            var name = Cell(row, "name")?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var level = ToDouble(Cell(row, source.LevelColumn));
            if (source.ByField != null && level.HasValue && !(reached >= level)) continue;
            found.Add(Feature(name, Cell(row, "description")?.ToString(), entry, level.HasValue ? (int)level.Value : null, view, source.ByField, new Dictionary<string, object>(row), copy));
        }
    }

    private static ActiveFeature Feature(string name, string? description, ContentEntry entry, int? level, IReadOnlyDictionary<string, object> view, string? levelKey, IReadOnlyDictionary<string, object> data, int copy = 1)
    {
        var modifiers = Maps(data.GetValueOrDefault("modifiers"))
            .Select(m => new FeatureModifier(Text(m, "target") ?? string.Empty, Text(m, "amount") ?? "0", Text(m, "type"), Text(m, "when")))
            .Where(m => m.Target.Length > 0)
            .ToList();
        var adds = Maps(data.GetValueOrDefault("adds"))
            .Select(a => new FeatureAdd(Text(a, "to") ?? string.Empty, Texts(Cell(a, "entries")), ToDouble(Cell(a, "level")) is { } at ? (int)at : null,
                Text(a, "rank"), ToDouble(Cell(a, "raise")) is { } raise ? (int)raise : 0, ParseChoice(a, name, level)))
            .Where(a => a.To.Length > 0 && (a.Entries.Count > 0 || a.Choice != null))
            .ToList();
        var uses = data.GetValueOrDefault("uses") switch
        {
            IDictionary<string, object> map when Text(map, "max") is { } max => new FeatureUses(Text(map, "key"), max, Texts(Cell(map, "recoversOn")),
                Maps(Cell(map, "recovers"))
                    .Select(r => new FeatureRecover(Text(r, "on") ?? string.Empty, Text(r, "amount") ?? FeatureRecovery.AllAmount, Text(r, "when")))
                    .Where(r => r.On.Length > 0)
                    .ToList(),
                string.Equals(Text(map, "regain"), "gm", StringComparison.OrdinalIgnoreCase)),
            // "uses: 2" is a counter of two with nothing that refills it but the GM.
            { } plain when ToDouble(plain) is > 0 => new FeatureUses(null, Convert.ToString(plain, CultureInfo.InvariantCulture)!, Array.Empty<string>()),
            _ => null
        };
        var spends = data.GetValueOrDefault("spends") is IDictionary<string, object> spend && Text(spend, "key") is { } spendKey
            ? new FeatureSpend(spendKey, Text(spend, "amount") ?? "1")
            : null;
        return new ActiveFeature(name, description, entry, level, view, levelKey, modifiers, adds, uses, spends, copy);
    }

    /// <summary>The choice an <c>adds</c> item leaves to the player (<c>choice</c> and/or <c>choose</c> in it), or null for a fixed item.
    /// A choice without an id gets <see cref="DefaultChoiceId"/>; one without a count chooses 1; a count below 1 is no choice.</summary>
    private static FeatureChoice? ParseChoice(IDictionary<string, object> add, string featureName, int? level)
    {
        if (!add.ContainsKey("choice") && !add.ContainsKey("choose")) return null;
        var choose = ToDouble(Cell(add, "choose")) is { } count ? (int)count : 1;
        if (choose < 1) return null;
        var given = Text(add, "choice");
        IReadOnlyList<string>? from = null;
        Dictionary<string, IReadOnlyList<string>>? filter = null;
        switch (Cell(add, "from"))
        {
            case null:
                break;
            case IDictionary<string, object> map:
                filter = map.ToDictionary(p => p.Key, p => Texts(p.Value), StringComparer.OrdinalIgnoreCase);
                break;
            case var listed:
                var keys = Texts(listed);
                if (!(keys.Count == 1 && keys[0].Equals(AnyKeys, StringComparison.OrdinalIgnoreCase))) from = keys;
                break;
        }

        return new FeatureChoice(given ?? DefaultChoiceId(featureName, level), given != null, choose, from, filter, Text(add, "minRank"), Text(add, "maxRank"));
    }

    /// <summary>The word for <c>from</c> that offers every key.</summary>
    public const string AnyKeys = "any";

    /// <summary>The id a choice gets when its data names none: the row's name as a slug, then its level (<c>expertise-6</c>), so the two
    /// Expertise rows of a rogue differ. Stored picks hang on it, so a data author should write the id down rather than lean on this.</summary>
    public static string DefaultChoiceId(string featureName, int? level)
    {
        var slug = Slug(featureName).Replace('_', '-');
        if (slug.Length == 0) slug = "choice"; // a name with no Latin letters or digits
        return level.HasValue ? $"{slug}-{level.Value}" : slug;
    }

    private static IEnumerable<IDictionary<string, object>> Maps(object? value) => value switch
    {
        IDictionary<string, object> one => new[] { one },
        string => Enumerable.Empty<IDictionary<string, object>>(),
        IEnumerable<object> many => many.OfType<IDictionary<string, object>>(),
        _ => Enumerable.Empty<IDictionary<string, object>>()
    };

    private static string? Text(IDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value != null && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text.Trim() : null;

    private static IReadOnlyList<string> Texts(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        IEnumerable<object> many => many.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).Where(v => v.Length > 0).ToList(),
        _ => new[] { Convert.ToString(value, CultureInfo.InvariantCulture)! }
    };

    // ---- Where a modifier lands ----

    private enum TargetKind { Unknown, ArmorClass, HitPoints, Derived, Field }

    private TargetKind KindOf(string target)
    {
        if (Rules?.ArmorClass is { } armor && armor.Key == target) return TargetKind.ArmorClass;
        if (Rules?.HitPoints is { } hitPoints && hitPoints.Key == target) return TargetKind.HitPoints;
        if (Rules?.Derived.Any(d => d.Key == target) == true) return TargetKind.Derived;
        return system.ActorFields.Any(f => f.Key == target && f.Type == FieldType.Number) ? TargetKind.Field : TargetKind.Unknown;
    }

    /// <summary>A feature's amount (or its counter's maximum) worked out on the sheet as the feature's class sees it; null when
    /// its <c>when</c> does not hold or a formula cannot be worked out (reported once, as a warning, by <paramref name="warn"/>).</summary>
    private int? Amount(ActiveFeature feature, string formula, string? when, IReadOnlyDictionary<string, double>? derived, Action<string>? warn)
    {
        try
        {
            var values = FormulaContext(feature.View);
            foreach (var (key, value) in derived ?? new Dictionary<string, double>()) values[key] = value;
            if (!string.IsNullOrWhiteSpace(when) && EvaluateFormula(feature.View, values, $"({when}) ? 1 : 0") == 0) return null;
            return (int)EvaluateFormula(feature.View, values, formula);
        }
        catch (Exception)
        {
            warn?.Invoke(feature.Name);
            return null;
        }
    }

    private bool IsBestType(string? type) =>
        ModifierStacking.IsFloor(ModifierStacking.RuleOf(system.ModifierTypes, type ?? Untyped));

    /// <summary>What features add to <paramref name="start"/>: types the system stacks as <c>best</c> set a floor, the rest
    /// stack by their rules and add up.</summary>
    private int WithModifiers(int start, IEnumerable<(ActiveFeature Feature, string? Type, int Amount)> modifiers, string target, List<GrantedModifier>? working)
    {
        // One group per type keeps one member (ModifierStacking). A value rule's kept member competes with the start and replaces
        // it when it wins (a floor for max, a ceiling for min); a bonus rule's kept members are added. Value rules go first.
        var stacked = ModifierStacking.Stack(modifiers.Select(m => (m.Type ?? Untyped, (double)m.Amount, m)), system.ModifierTypes);
        var total = start;
        foreach (var s in stacked.Where(s => ModifierStacking.IsValueRule(ModifierStacking.RuleOf(system.ModifierTypes, s.Type))))
        {
            var amount = (int)s.Amount;
            var wins = s.Counted && (ModifierStacking.IsCeiling(ModifierStacking.RuleOf(system.ModifierTypes, s.Type)) ? amount < total : amount > total);
            if (wins) total = amount;
            working?.Add(new GrantedModifier(target, s.Item.Feature, s.Type, amount, wins));
        }

        foreach (var s in stacked.Where(s => !ModifierStacking.IsValueRule(ModifierStacking.RuleOf(system.ModifierTypes, s.Type))))
        {
            if (s.Counted) total += (int)s.Amount;
            working?.Add(new GrantedModifier(target, s.Item.Feature, s.Type, (int)s.Amount, s.Counted));
        }

        return total;
    }

    // ---- In the evaluation: derived values, counters, and what armor class and hit points pick up ----

    /// <summary>The derived values of <paramref name="sheet"/> as the evaluation will work them out, for formulas that name them.</summary>
    private SheetEvaluation WorkedOutDerived(IReadOnlyDictionary<string, object> sheet)
    {
        var scratch = new SheetEvaluation();
        if (Rules!.Derived.Count > 0) EvaluateDerived(sheet, scratch);
        return scratch;
    }

    private static Dictionary<string, double> DerivedNumbers(SheetEvaluation result) =>
        result.Derived.Where(d => IsIdentifier(d.Key) && YamlValues.ToNumber(d.Value).HasValue).ToDictionary(d => d.Key, d => YamlValues.ToNumber(d.Value)!.Value);

    /// <summary>What a target's modifiers come to; one whose formula fails (<paramref name="warn"/>) or whose condition is off is left out.</summary>
    private List<(ActiveFeature Feature, string? Type, int Amount)> ModifierAmounts(IEnumerable<(ActiveFeature Feature, FeatureModifier Modifier)> modifiers, IReadOnlyDictionary<string, double> derived, Action<string>? warn) =>
        modifiers.Select(x => (x.Feature, x.Modifier.Type, Amount: Amount(x.Feature, x.Modifier.Amount, x.Modifier.When, derived, warn)))
            .Where(x => x.Amount.HasValue)
            .Select(x => (x.Feature, x.Type, x.Amount!.Value))
            .ToList();

    private void EvaluateFeatures(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        var features = ActiveFeatures(sheet);
        if (features.Count == 0) return;
        result.Features.AddRange(features);

        var derived = DerivedNumbers(result);
        var beforeFeatures = new Dictionary<string, double>(derived); // untouched by the derived modifiers below
        var warned = new HashSet<string>(StringComparer.Ordinal);
        void Warn(string name)
        {
            if (warned.Add(name)) result.Warnings.Add(new RuleViolation("features", $"{name}: what this feature grants can't be worked out, so it is left out. The GM can check the entry.", "Actor.Rule.FeatureError", new object?[] { name }));
        }

        foreach (var group in features.SelectMany(f => f.Modifiers.Select(m => (Feature: f, Modifier: m))).GroupBy(x => x.Modifier.Target, StringComparer.Ordinal))
        {
            var kind = KindOf(group.Key);
            if (kind == TargetKind.Unknown)
            {
                foreach (var name in group.Select(x => x.Feature.Name).Distinct()) Warn(name);
                continue;
            }

            // A field's amounts are worked out from the same derived values ApplyGranted used, so what is listed is what was applied.
            var amounts = ModifierAmounts(group, kind == TargetKind.Field ? beforeFeatures : derived, Warn);
            if (amounts.Count == 0) continue;

            switch (kind)
            {
                case TargetKind.Derived:
                    if (YamlValues.ToNumber(result.Derived.GetValueOrDefault(group.Key)) is { } value)
                    {
                        var total = WithModifiers((int)value, amounts, group.Key, result.Granted);
                        result.Derived[group.Key] = (long)total;
                        derived[group.Key] = total;
                    }

                    break;
                case TargetKind.Field:
                    // Already on the sheet (ApplyGranted, from the same amounts); listed here for the working only.
                    WithModifiers(0, amounts, group.Key, result.Granted);
                    break;
                default:
                    // Armor class and hit points take these in their own stages.
                    result.Pending[group.Key] = amounts;
                    break;
            }
        }

        var owners = new List<(ActiveFeature Feature, string Key, int Max)>();
        foreach (var feature in features.Where(f => f.Uses != null))
        {
            if (Amount(feature, feature.Uses!.Max, null, derived, Warn) is not { } max || max <= 0) continue;
            owners.Add((feature, UsesPrefix + Slug(feature.Uses.Key ?? $"{feature.Source.Id}_{feature.Name}"), max));
        }

        // Features naming one key share one counter. The highest maximum is the counter's, whatever the order; naming it
        // from more than one place is said, since the entry's author probably meant one owner.
        foreach (var group in owners.GroupBy(o => o.Key, StringComparer.Ordinal))
        {
            var winner = group.OrderByDescending(o => o.Max).First();
            if (group.Count() > 1)
            {
                var names = string.Join(", ", group.Select(o => o.Feature.Name).Distinct(StringComparer.OrdinalIgnoreCase));
                result.Warnings.Add(new RuleViolation("features", $"{names}: more than one feature sets the maximum of the same pool. The highest, {winner.Max}, is used.", "Actor.Rule.CounterKeyShared", new object?[] { names, winner.Max }));
            }

            var rules = ResolveRecovers(winner.Feature, derived, Warn);
            var legacy = group.Where(o => o.Feature.Uses!.Key != null).Select(o => UsesPrefix + Slug($"{o.Feature.Source.Id}_{o.Feature.Name}")).Where(k => k != winner.Key).Distinct(StringComparer.Ordinal).ToList();
            result.Counters.Add(new FeatureCounter(winner.Feature, winner.Key, winner.Key + UsesMaxSuffix, winner.Max,
                rules.Select(r => r.On).Where(on => !FeatureRecovery.IsMoment(on)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                rules, winner.Feature.Uses!.GmRegain, legacy));
        }

        foreach (var feature in features.Where(f => f.Spends != null))
        {
            var spend = feature.Spends!;
            var amount = spend.Ask ? 1 : Amount(feature, spend.Amount, null, derived, Warn);
            if (amount is not > 0) continue;
            var key = UsesPrefix + Slug(spend.Key);
            ActionCost? cost = null;
            if (result.Counters.FirstOrDefault(c => c.Key == key) is { } counter) cost = ActionCost.OfCounter(counter, amount.Value, null);
            else if (sheet.ContainsKey(spend.Key) && (system.Resources.Any(r => r.Key == spend.Key) || Rules!.SlotPools.Any(p => p.Owns(spend.Key))))
            {
                cost = ActionCost.FromPool(spend.Key, amount.Value, SystemDefinition.PoolMaxKey(system, spend.Key), system.Resources.FirstOrDefault(r => r.Key == spend.Key)?.Label ?? spend.Key, null);
            }

            if (cost == null)
            {
                result.Warnings.Add(new RuleViolation("features", $"{feature.Name}: spends from \"{spend.Key}\", and this sheet has no such pool, so it has no button.", "Actor.Rule.SpendNoPool", new object?[] { feature.Name, spend.Key }));
                continue;
            }

            result.Spenders.Add(new FeatureSpender(feature, cost, spend.Ask));
        }
    }

    /// <summary>A counter's refill rules with their amounts worked out on the sheet (a formula becomes a number, dice stay dice)
    /// and a formula <c>when</c> decided for the build as it is now; a rule that fails or never holds is left out.</summary>
    private List<RecoverRule> ResolveRecovers(ActiveFeature feature, IReadOnlyDictionary<string, double> derived, Action<string> warn)
    {
        var rules = new List<RecoverRule>();
        foreach (var rule in feature.Uses!.AllRecovers)
        {
            var whenEmpty = string.Equals(rule.When, FeatureRecovery.EmptyWhen, StringComparison.OrdinalIgnoreCase);
            if (!whenEmpty && !string.IsNullOrWhiteSpace(rule.When) && Amount(feature, "1", rule.When, derived, warn) == null) continue;
            string? amount;
            if (rule.Amount.Equals(FeatureRecovery.AllAmount, StringComparison.OrdinalIgnoreCase)) amount = FeatureRecovery.AllAmount;
            else if (FeatureRecovery.IsDice(rule.Amount)) amount = rule.Amount.Replace(" ", string.Empty);
            else amount = Amount(feature, rule.Amount, null, derived, warn) is > 0 and var worked ? worked.ToString(CultureInfo.InvariantCulture) : null;
            if (amount != null) rules.Add(new RecoverRule(rule.On, amount, whenEmpty));
        }

        return rules;
    }

    private static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '_') slug.Append('_');
        }

        return slug.ToString().Trim('_');
    }

    // ---- On the sheet: what features add to fields and lists, and their counters ----

    /// <summary>Takes off what features added to the sheet's Number fields the last time, leaving the player's own numbers:
    /// run before anything that sets those fields afresh (a newly chosen species), with <see cref="ApplyGranted"/> after.</summary>
    public void StripGranted(Dictionary<string, object> sheet)
    {
        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.Number))
        {
            if (YamlValues.GetInt(sheet, GrantedPrefix + field.Key) is not { } added) continue;
            if (YamlValues.GetInt(sheet, field.Key) is { } current) sheet[field.Key] = (long)(current - added);
            sheet.Remove(GrantedPrefix + field.Key);
        }
    }

    /// <summary>Puts on the sheet what its features grant outside the evaluation's own stages: amounts on Number fields
    /// (remembered, so the next run starts from the player's number), and entries on lists (remembered too, so they leave
    /// when the feature does, while an entry the player picked themself stays).</summary>
    public void ApplyGranted(Dictionary<string, object> sheet)
    {
        if (Rules == null) return;
        CleanUpGrantedRanks(sheet);
        var features = Rules.Features.Count == 0 ? Array.Empty<ActiveFeature>() : ActiveFeatures(sheet);

        // Derived values (proficiency bonus...) come from the sheet as it is before any field is granted: one pass, no loop. A derived
        // value that itself reads a granted field is therefore worked out without the grant here, and with it in the evaluation.
        Dictionary<string, double>? derived = null;
        foreach (var group in features.SelectMany(f => f.Modifiers.Select(m => (Feature: f, Modifier: m))).GroupBy(x => x.Modifier.Target, StringComparer.Ordinal))
        {
            if (KindOf(group.Key) != TargetKind.Field) continue;
            var field = system.ActorFields.First(f => f.Key == group.Key);
            var start = YamlValues.GetInt(sheet, field.Key) ?? (int)(ToDouble(field.DefaultValue) ?? 0);
            derived ??= DerivedNumbers(WorkedOutDerived(sheet));
            // A formula that fails is left out here; the evaluation that follows runs the same working and warns about it.
            var amounts = ModifierAmounts(group, derived, null);
            var total = WithModifiers(start, amounts, group.Key, null);
            if (total == start) continue;
            sheet[field.Key] = (long)total;
            sheet[GrantedPrefix + field.Key] = (long)(total - start);
        }

        var targets = features.SelectMany(f => f.Adds.Select(a => a.To))
            .Concat(sheet.Keys.Where(k => k.StartsWith(GrantedPrefix, StringComparison.Ordinal) && sheet[k] is not (long or int or double)).Select(k => k[GrantedPrefix.Length..]))
            .Distinct(StringComparer.Ordinal)
            .Where(to => RankTargetOf(to) == null) // a ranked list's grants live in the evaluation only (EvaluateRanks), never on the sheet
            .ToList();
        foreach (var to in targets)
        {
            var list = Rules.Lists.FirstOrDefault(l => l.Key == to);
            var granted = new List<string>();
            foreach (var feature in features)
            {
                foreach (var add in feature.Adds.Where(a => a.To == to))
                {
                    if (!AddApplies(feature, add)) continue;
                    granted.AddRange(add.Entries.Select(e => list == null ? e : EntryId(list.TargetEntityType, e)).OfType<string>());
                }
            }

            granted = granted.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = RawList(sheet, GrantedPrefix + to);
            var held = RawList(sheet, to);
            string IdOf(string raw) => list == null ? raw : ParsePick(raw).Id;
            // Off: what was added before and is granted no more. On: what is granted and not there yet.
            held.RemoveAll(raw => before.Contains(IdOf(raw), StringComparer.OrdinalIgnoreCase) && !granted.Contains(IdOf(raw), StringComparer.OrdinalIgnoreCase));
            var added = before.Where(id => granted.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var id in granted.Where(id => !held.Any(raw => IdOf(raw).Equals(id, StringComparison.OrdinalIgnoreCase))))
            {
                held.Add(id);
                added.Add(id);
            }

            if (held.Count > 0 || sheet.ContainsKey(to)) sheet[to] = held.Cast<object>().ToList();
            if (added.Count > 0) sheet[GrantedPrefix + to] = added.Cast<object>().ToList();
            else sheet.Remove(GrantedPrefix + to);
        }
    }

    /// <summary>Whether an <c>adds</c> item has reached its level: the sheet value the feature's levels are compared with is at or above it.</summary>
    private static bool AddApplies(ActiveFeature feature, FeatureAdd add) =>
        !add.Level.HasValue || (feature.LevelKey == null ? null : ToDouble(feature.View.GetValueOrDefault(feature.LevelKey))) >= add.Level;

    // An entry a feature names by id, or by the name a writer knows it by.
    private string? EntryId(string type, string named) =>
        resolver.Resolve(type, named)?.Id
        ?? resolver.Candidates(type).FirstOrDefault(c => c.Id.Equals(named, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(named, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>Writes the features' counters to the sheet, as the slot pools are written: a counter that just appeared or grew
    /// starts with the new uses available, and one whose feature is gone leaves the sheet.</summary>
    public void ApplyCounters(Dictionary<string, object> sheet, SheetEvaluation evaluation)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var counter in evaluation.Counters)
        {
            keep.Add(counter.Key);
            keep.Add(counter.MaxKey);

            // A counter that gained a key (or took another) keeps what was spent: the value under the old key moves across, once,
            // since the sweep below drops the old keys.
            if (!sheet.ContainsKey(counter.Key) && counter.LegacyKeys?.FirstOrDefault(sheet.ContainsKey) is { } legacy)
            {
                sheet[counter.Key] = sheet[legacy];
                if (sheet.TryGetValue(legacy + UsesMaxSuffix, out var oldMax)) sheet[counter.MaxKey] = oldMax;
            }

            var previousMax = YamlValues.GetInt(sheet, counter.MaxKey);
            var current = YamlValues.GetInt(sheet, counter.Key);
            sheet[counter.MaxKey] = (long)counter.Max;
            if (current == null) current = counter.Max;
            else if (previousMax.HasValue && counter.Max > previousMax.Value) current += counter.Max - previousMax.Value;
            // A lower maximum caps what is left; no change to the build ever refills a counter.
            sheet[counter.Key] = (long)Math.Clamp(current.Value, 0, counter.Max);
            sheet[counter.Key + UsesLabelSuffix] = counter.Feature.Name;
            keep.Add(counter.Key + UsesLabelSuffix);
            if (counter.Recovers is { Count: > 0 } recovers)
            {
                sheet[counter.Key + UsesRestSuffix] = FeatureRecovery.Encode(recovers);
                keep.Add(counter.Key + UsesRestSuffix);
            }
        }

        foreach (var stale in sheet.Keys.Where(k => k.StartsWith(UsesPrefix, StringComparison.Ordinal) && !keep.Contains(k)).ToList()) sheet.Remove(stale);
    }
}
