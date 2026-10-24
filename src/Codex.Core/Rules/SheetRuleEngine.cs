using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;
using DynamicExpresso;

namespace Codex.Core.Rules;

/// <summary>Where the engine finds the entries a sheet references (packs plus a campaign's homebrew).</summary>
public interface IEntryResolver
{
    ContentEntry? Resolve(string type, string id);
    IEnumerable<ContentEntry> Candidates(string type);
}

/// <summary>A resolver that knows no entries - enough for <see cref="SheetRuleEngine.InitializeSheet"/>.</summary>
public sealed class NullEntryResolver : IEntryResolver
{
    public static readonly NullEntryResolver Instance = new();
    public ContentEntry? Resolve(string type, string id) => null;
    public IEnumerable<ContentEntry> Candidates(string type) => Enumerable.Empty<ContentEntry>();
}

public sealed record RuleViolation(string FieldKey, string Message);

/// <summary>What the attribute rules make of the sheet's current scores.</summary>
public sealed record AttributeSummary(
    string Method,
    int PointsSpent,
    int? PointsBudget,
    IReadOnlyList<string> EligibleBonusAttributes,
    IReadOnlyDictionary<string, int> Bonuses,
    IReadOnlyDictionary<string, int> Increases,
    IReadOnlyDictionary<string, int> Totals);

/// <summary>One entry picked in a list. <see cref="Index"/> is its position in the sheet's raw list
/// (the same entry can appear twice when the list allows repeats); <see cref="Choices"/> is what the
/// player chose for it, such as where an Ability Score Improvement's points go.</summary>
public sealed record SheetPick(int Index, string Raw, ContentEntry Entry, IReadOnlyDictionary<string, int> Choices);

/// <summary>A picked entry that grants score increases, and what the player did with them.</summary>
public sealed record IncreaseGrant(SheetPick Pick, int Points, IReadOnlyList<string> Options, int? Cap);

/// <summary>One of the sheet's classes and how many of the character's levels are in it.</summary>
public sealed record ClassLevel(string Id, ContentEntry? Entry, int Level, bool IsFirst, string? SubclassId = null, ContentEntry? Subclass = null);

/// <summary>A class other than the first, as stored: <c>"class-id:levels:subclass-id"</c>.</summary>
public sealed record OtherClass(string Id, int Level, string? Subclass = null);

/// <summary>One skill or saving throw as the sheet shows it.</summary>
public sealed record SkillScore(string Key, string Label, string Attribute, bool Proficient, bool Expert, int Bonus);

/// <summary>A passive score such as passive Perception.</summary>
public sealed record PassiveScore(string Key, string Label, int Value);

/// <summary>A spellcasting class's ability, save DC and spell attack bonus.</summary>
public sealed record SpellcastingScore(string ClassName, string Attribute, int SaveDc, int AttackBonus);

/// <summary>A slot pool's maximum slots per slot level for this sheet.</summary>
public sealed record SlotPool(SlotPoolRule Rule, IReadOnlyDictionary<int, int> Max);

/// <summary>The engine's whole verdict on one sheet, used by the UI to render and by save to decide.</summary>
public sealed class SheetEvaluation
{
    public Dictionary<string, object> Derived { get; } = new();
    public List<RuleViolation> Violations { get; } = new();

    /// <summary>For each constrained reference field, the entries the sheet may currently pick.</summary>
    public Dictionary<string, IReadOnlyList<ContentEntry>> AllowedOptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For each list rule, the entries that may be added now (conditions met, not yet picked unless repeatable).</summary>
    public Dictionary<string, IReadOnlyList<ContentEntry>> ListOptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For each list rule, the resolved picks in sheet order (unresolvable ids are reported as violations).</summary>
    public Dictionary<string, IReadOnlyList<SheetPick>> ListPicks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int?> ListLimits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AttributeSummary? Attributes { get; set; }

    /// <summary>Picks that grant score increases (empty when the system declares none).</summary>
    public List<IncreaseGrant> Increases { get; } = new();

    /// <summary>The sheet's classes, first class first (empty without multiclass rules).</summary>
    public List<ClassLevel> Classes { get; } = new();

    /// <summary>Classes that can be added now: not taken yet and prerequisites met.</summary>
    public IReadOnlyList<ContentEntry> MulticlassOptions { get; set; } = Array.Empty<ContentEntry>();

    public List<SlotPool> SlotPools { get; } = new();

    /// <summary>Maximum hit points as the rules work them out (null without hit point rules or a class).</summary>
    public HitPointSummary? HitPoints { get; set; }

    /// <summary>Armor class as the rules work it out (null without armor class rules).</summary>
    public ArmorClassSummary? ArmorClass { get; set; }

    /// <summary>What the sheet carries, in sheet order (empty without inventory rules).</summary>
    public List<InventoryItem> Inventory { get; } = new();

    /// <summary>Things worth a look that never stop a save (armor the class isn't trained in).</summary>
    public List<RuleViolation> Warnings { get; } = new();

    public List<SkillScore> SavingThrows { get; } = new();
    public List<SkillScore> Skills { get; } = new();
    public List<PassiveScore> Passives { get; } = new();
    public List<SpellcastingScore> Spellcasting { get; } = new();

    public bool HasViolations => Violations.Count > 0;
}

/// <summary>
/// Evaluates a system's <see cref="SheetRules"/> against a sheet. Pure and stateless: given the
/// definition, the sheet dictionary and a resolver for the entries it references, it produces
/// derived values, the options each picker may offer, and the list of violations. The UI filters
/// pickers with it; save uses it to enforce.
/// </summary>
public sealed partial class SheetRuleEngine(SystemDefinition system, IEntryResolver resolver)
{
    public const string MethodKey = "attrMethod";
    public const string BasePrefix = "base_";
    public const string BonusPrefix = "bonus_";

    private SheetRules? Rules => system.SheetRules;

    public static string EffectiveEnforcement(SystemDefinition? system, string? campaignOverride)
    {
        var value = string.IsNullOrWhiteSpace(campaignOverride) || campaignOverride == "inherit"
            ? system?.SheetRules?.Enforcement ?? "off"
            : campaignOverride;
        return value.ToLowerInvariant() is "strict" or "warn" ? value.ToLowerInvariant() : "off";
    }

    /// <summary>Seeds a brand-new sheet: default attribute method and base scores, and the modes that
    /// let hit points and armor class follow the rules.</summary>
    public void InitializeSheet(Dictionary<string, object> sheet)
    {
        if (Rules?.HitPoints is { } hitPoints) sheet[hitPoints.ModeKey] = hitPoints.Default;
        if (Rules?.ArmorClass is { } armorClass) sheet[armorClass.ModeKey] = "auto";

        var attributes = Rules?.Attributes;
        if (attributes == null) return;
        var method = attributes.Default;
        sheet[MethodKey] = method;
        foreach (var attribute in system.Attributes)
        {
            var baseScore = method == "point-buy" && attributes.PointBuy != null ? attributes.PointBuy.Min : attribute.Default;
            sheet[BasePrefix + attribute.Key] = (long)baseScore;
            sheet[attribute.Key] = (long)baseScore;
        }
    }

    public SheetEvaluation Evaluate(IReadOnlyDictionary<string, object> sheet)
    {
        var result = new SheetEvaluation();
        if (Rules == null) return result;

        EvaluateAttributes(sheet, result);
        EvaluateDerived(sheet, result);
        var views = ClassViews(sheet, result.Classes);
        EvaluateMulticlass(sheet, result);

        foreach (var constraint in Rules.Constraints)
        {
            var field = system.ActorFields.FirstOrDefault(f => f.Key == constraint.Field);
            if (field?.TargetEntityType == null) continue;
            var allowed = resolver.Candidates(field.TargetEntityType).Where(c => constraint.Where.All(w => Holds(w, c, sheet))).ToList();
            if (IsSubclassField(constraint.Field) && result.Classes.FirstOrDefault(c => c.IsFirst) is { } firstClass && firstClass.Level < Rules.Multiclass!.SubclassLevel) allowed.Clear();
            result.AllowedOptions[constraint.Field] = allowed;

            var chosen = YamlValues.GetString(sheet, constraint.Field);
            if (string.IsNullOrEmpty(chosen)) continue;
            var entry = resolver.Resolve(field.TargetEntityType, chosen);
            if (entry == null)
            {
                result.Violations.Add(new RuleViolation(constraint.Field, $"{field.Label}: \"{chosen}\" is not in the Library."));
                continue;
            }

            foreach (var condition in constraint.Where.Where(w => !Holds(w, entry, sheet)))
            {
                result.Violations.Add(new RuleViolation(constraint.Field, $"{field.Label}: {condition.Message ?? "this choice is not allowed by the rules."}"));
            }
        }

        foreach (var list in Rules.Lists)
        {
            var picked = RawList(sheet, list.Key);
            var resolved = new List<SheetPick>();
            for (var index = 0; index < picked.Count; index++)
            {
                var (id, choices) = ParsePick(picked[index]);
                var entry = resolver.Resolve(list.TargetEntityType, id);
                if (entry == null)
                {
                    result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: \"{id}\" is not in the Library."));
                    continue;
                }

                resolved.Add(new SheetPick(index, picked[index], entry, choices));
                if (views.Any(v => list.Where.All(w => Holds(w, entry, v)))) continue;

                // No class allows it: report what the closest class objects to - fewest failures,
                // then the one that got furthest down the conditions (a priest spell is "too high"
                // for the priest, not "not on your list" for the mage).
                var failures = views
                    .Select(v => list.Where.Where(w => !Holds(w, entry, v)).ToList())
                    .OrderBy(f => f.Count)
                    .ThenByDescending(f => f.Count == 0 ? int.MaxValue : list.Where.IndexOf(f[0]))
                    .First();
                foreach (var condition in failures)
                {
                    result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: {entry.Name} - {condition.Message ?? "not allowed by the rules."}"));
                }
            }

            result.ListPicks[list.Key] = resolved;
            var pickedIds = resolved.Select(p => p.Entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.ListOptions[list.Key] = resolver.Candidates(list.TargetEntityType)
                .Where(c => (!pickedIds.Contains(c.Id) || IsRepeatable(list, c)) && views.Any(v => list.Where.All(w => Holds(w, c, v))))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var duplicate in resolved.GroupBy(p => p.Entry.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && !IsRepeatable(list, g.First().Entry)))
            {
                result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: {duplicate.First().Entry.Name} can only be taken once."));
            }

            var limit = ResolveCount(list.MaxCount, views);
            result.ListLimits[list.Key] = limit;
            if (limit.HasValue && picked.Count > limit.Value)
            {
                result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: {picked.Count} picked, but only {limit.Value} allowed at this level."));
            }
        }

        EvaluateSlotPools(views, result);
        EvaluateInventory(sheet, result);
        EvaluateHitPoints(sheet, result);
        EvaluateArmorClass(sheet, result);
        EvaluateSkills(sheet, result);
        EvaluateSpellcasting(sheet, result);
        EvaluateProficiencies(sheet, result);
        return result;
    }

    private bool IsRepeatable(ListRule list, ContentEntry entry) =>
        list.RepeatableProperty != null && entry.Properties.TryGetValue(list.RepeatableProperty, out var value) && value is true or "true" or "True";

    // ---- Picks ----

    /// <summary>The raw strings stored in a list field.</summary>
    public static List<string> RawList(IReadOnlyDictionary<string, object> sheet, string key) =>
        sheet.TryGetValue(key, out var raw) && raw is not string and IEnumerable<object> items
            ? items.Select(i => i?.ToString() ?? string.Empty).Where(i => i.Length > 0).ToList()
            : new List<string>();

    /// <summary>Splits <c>"feat-id|str:1,dex:1"</c> into the entry id and the choices made for it.</summary>
    public static (string Id, Dictionary<string, int> Choices) ParsePick(string raw)
    {
        var choices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bar = raw.IndexOf('|');
        if (bar < 0) return (raw, choices);
        foreach (var part in raw[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(part[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)) continue;
            var key = part[..colon];
            choices[key] = choices.GetValueOrDefault(key) + amount;
        }

        return (raw[..bar], choices);
    }

    public static string FormatPick(string id, IReadOnlyDictionary<string, int> choices)
    {
        var parts = choices.Where(c => c.Value != 0).Select(c => $"{c.Key}:{c.Value.ToString(CultureInfo.InvariantCulture)}").ToList();
        return parts.Count == 0 ? id : id + "|" + string.Join(",", parts);
    }

    // ---- Attributes ----

    private void EvaluateAttributes(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        var rules = Rules!.Attributes;
        if (rules == null || system.Attributes.Count == 0) return;

        var method = (YamlValues.GetString(sheet, MethodKey) ?? rules.Default).ToLowerInvariant();
        var bases = new Dictionary<string, int>();
        var bonuses = new Dictionary<string, int>();
        foreach (var attribute in system.Attributes)
        {
            var baseScore = YamlValues.GetInt(sheet, BasePrefix + attribute.Key) ?? YamlValues.GetInt(sheet, attribute.Key) ?? attribute.Default;
            var bonus = YamlValues.GetInt(sheet, BonusPrefix + attribute.Key) ?? 0;
            bases[attribute.Key] = baseScore;
            if (bonus != 0) bonuses[attribute.Key] = bonus;
        }

        var spent = 0;
        int? budget = null;
        if (method == "point-buy" && rules.PointBuy is { } pointBuy)
        {
            budget = pointBuy.Budget;
            foreach (var attribute in system.Attributes)
            {
                var score = bases[attribute.Key];
                if (score < pointBuy.Min || score > pointBuy.Max)
                {
                    result.Violations.Add(new RuleViolation(attribute.Key, $"{attribute.Label}: point buy scores run from {pointBuy.Min} to {pointBuy.Max}."));
                    continue;
                }

                spent += pointBuy.Costs.TryGetValue(score, out var cost) ? cost : score - pointBuy.Min;
            }

            if (spent > pointBuy.Budget)
            {
                result.Violations.Add(new RuleViolation(MethodKey, $"Point buy: {spent} of {pointBuy.Budget} points spent."));
            }
        }
        else if (method == "standard-array" && rules.StandardArray.Count > 0)
        {
            var expected = rules.StandardArray.OrderBy(v => v).ToList();
            var actual = bases.Values.OrderBy(v => v).ToList();
            if (!expected.SequenceEqual(actual))
            {
                result.Violations.Add(new RuleViolation(MethodKey, $"Standard array: assign exactly {string.Join(", ", rules.StandardArray)}, one score per attribute."));
            }
        }
        else if (!rules.Methods.Contains(method, StringComparer.OrdinalIgnoreCase) && method != "manual")
        {
            result.Violations.Add(new RuleViolation(MethodKey, $"Unknown attribute method \"{method}\"."));
        }

        var eligible = new List<string>();
        if (rules.Bonuses is { } bonusRules)
        {
            eligible = EligibleBonusAttributes(bonusRules, sheet);
            var allocation = bonuses.Values.Where(v => v > 0).OrderByDescending(v => v).ToList();
            if (bonuses.Count > 0)
            {
                foreach (var key in bonuses.Keys.Where(k => !eligible.Contains(k)))
                {
                    var label = system.Attributes.First(a => a.Key == key).Label;
                    result.Violations.Add(new RuleViolation(BonusPrefix + key, $"{bonusRules.Label}: {label} is not one of the eligible attributes."));
                }

                var allowed = bonusRules.Allocations.Any(a => a.OrderByDescending(v => v).SequenceEqual(allocation));
                if (!allowed)
                {
                    var options = string.Join(" or ", bonusRules.Allocations.Select(a => string.Join("/", a.Select(v => "+" + v))));
                    result.Violations.Add(new RuleViolation(MethodKey, $"{bonusRules.Label}: spread them as {options}, each on a different attribute."));
                }
            }
        }

        // Increases from picks (Ability Score Improvement and the like), each with its own cap. A grant
        // is applied to the score as it stands when the player took it, in sheet order, so a Boon's
        // higher cap can't carry an earlier Ability Score Improvement past its own.
        var increases = new Dictionary<string, int>();
        var running = system.Attributes.ToDictionary(a => a.Key, a => bases[a.Key] + bonuses.GetValueOrDefault(a.Key));
        foreach (var attribute in system.Attributes)
        {
            if (rules.Max.HasValue && running[attribute.Key] > rules.Max.Value)
            {
                result.Violations.Add(new RuleViolation(attribute.Key, $"{attribute.Label}: scores can't exceed {rules.Max.Value}."));
            }
        }

        if (rules.Increases is { } increaseRules)
        {
            var overCap = new HashSet<string>();
            foreach (var grant in EvaluateIncreases(increaseRules, sheet, result))
            {
                foreach (var (key, amount) in grant.Pick.Choices)
                {
                    var attribute = MatchAttribute(key);
                    if (attribute == null) continue;
                    increases[attribute.Key] = increases.GetValueOrDefault(attribute.Key) + amount;
                    running[attribute.Key] += amount;
                    var cap = grant.Cap ?? rules.Max;
                    if (cap.HasValue && running[attribute.Key] > cap.Value && overCap.Add(attribute.Key))
                    {
                        result.Violations.Add(new RuleViolation(attribute.Key, $"{attribute.Label}: scores can't exceed {cap.Value} with {grant.Pick.Entry.Name}."));
                    }
                }
            }
        }

        var totals = system.Attributes.ToDictionary(
            a => a.Key,
            a => bases[a.Key] + bonuses.GetValueOrDefault(a.Key) + increases.GetValueOrDefault(a.Key));

        result.Attributes = new AttributeSummary(method, spent, budget, eligible, bonuses, increases, totals);
    }

    private IEnumerable<IncreaseGrant> EvaluateIncreases(IncreaseRules rules, IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        var list = Rules!.Lists.FirstOrDefault(l => l.Key.Equals(rules.FromList, StringComparison.OrdinalIgnoreCase));
        if (list == null) yield break;

        var raw = RawList(sheet, list.Key);
        for (var index = 0; index < raw.Count; index++)
        {
            var (id, choices) = ParsePick(raw[index]);
            var entry = resolver.Resolve(list.TargetEntityType, id);
            if (entry == null) continue;
            var pick = new SheetPick(index, raw[index], entry, choices);

            var points = (int)(ToDouble(entry.Properties.GetValueOrDefault(rules.Property)) ?? 0);
            if (points <= 0)
            {
                if (choices.Count > 0) result.Violations.Add(new RuleViolation(list.Key, $"{rules.Label}: {entry.Name} doesn't raise any score."));
                continue;
            }

            var options = AttributesNamedIn(entry.Properties.GetValueOrDefault(rules.OptionsProperty));
            if (options.Count == 0) options = system.Attributes.Select(a => a.Key).ToList();
            var capValue = ToDouble(entry.Properties.GetValueOrDefault(rules.CapProperty));
            var grant = new IncreaseGrant(pick, points, options, capValue.HasValue ? (int)capValue.Value : null);
            result.Increases.Add(grant);

            var spent = choices.Values.Sum();
            if (spent < points)
            {
                result.Violations.Add(new RuleViolation(list.Key, $"{rules.Label}: choose where {entry.Name}'s {points - spent} remaining point{(points - spent == 1 ? "" : "s")} go{(points - spent == 1 ? "es" : "")}."));
            }
            else if (spent > points)
            {
                result.Violations.Add(new RuleViolation(list.Key, $"{rules.Label}: {entry.Name} grants {points} point{(points == 1 ? "" : "s")}, not {spent}."));
            }

            foreach (var key in choices.Keys)
            {
                var attribute = MatchAttribute(key);
                if (attribute == null || !options.Contains(attribute.Key))
                {
                    result.Violations.Add(new RuleViolation(list.Key, $"{rules.Label}: {entry.Name} can't raise {attribute?.Label ?? key}."));
                }
            }

            yield return grant;
        }
    }

    private AttributeDefinition? MatchAttribute(string name) =>
        system.Attributes.FirstOrDefault(a => name.Equals(a.Key, StringComparison.OrdinalIgnoreCase) || name.Equals(a.Abbreviation, StringComparison.OrdinalIgnoreCase) || name.Equals(a.Label, StringComparison.OrdinalIgnoreCase)
            || name.Equals(a.CanonicalAbbreviation, StringComparison.OrdinalIgnoreCase) || name.Equals(a.CanonicalLabel, StringComparison.OrdinalIgnoreCase));

    /// <summary>The attribute keys a property value names, by key, abbreviation or label.</summary>
    private List<string> AttributesNamedIn(object? listed)
    {
        if (listed == null) return new List<string>();
        var names = listed is not string and IEnumerable<object> list ? list.Select(o => o?.ToString() ?? "").ToList() : new List<string> { listed.ToString() ?? "" };
        return system.Attributes
            .Where(a => names.Any(n => n.Equals(a.Key, StringComparison.OrdinalIgnoreCase) || n.Equals(a.Abbreviation, StringComparison.OrdinalIgnoreCase) || n.Equals(a.Label, StringComparison.OrdinalIgnoreCase)
                                    || n.Equals(a.CanonicalAbbreviation, StringComparison.OrdinalIgnoreCase) || n.Equals(a.CanonicalLabel, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Key)
            .ToList();
    }

    /// <summary>Attribute keys the bonus rule allows, matched by key, abbreviation or label.</summary>
    public List<string> EligibleBonusAttributes(BonusRules bonusRules, IReadOnlyDictionary<string, object> sheet)
    {
        var entry = ReferencedEntry(bonusRules.FromField, sheet);
        return entry == null ? new List<string>() : AttributesNamedIn(entry.Properties.GetValueOrDefault(bonusRules.Property));
    }

    /// <summary>Writes base + bonus + increases into each attribute's total key, so the ECS sees final scores.</summary>
    public void ApplyAttributeTotals(Dictionary<string, object> sheet)
    {
        if (Rules?.Attributes is not { } rules) return;
        var increases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (rules.Increases is { } increaseRules)
        {
            foreach (var raw in RawList(sheet, increaseRules.FromList))
            {
                foreach (var (key, amount) in ParsePick(raw).Choices)
                {
                    if (MatchAttribute(key) is { } attribute) increases[attribute.Key] = increases.GetValueOrDefault(attribute.Key) + amount;
                }
            }
        }

        foreach (var attribute in system.Attributes)
        {
            if (!sheet.ContainsKey(BasePrefix + attribute.Key)) continue;
            var baseScore = YamlValues.GetInt(sheet, BasePrefix + attribute.Key) ?? attribute.Default;
            var bonus = YamlValues.GetInt(sheet, BonusPrefix + attribute.Key) ?? 0;
            sheet[attribute.Key] = (long)(baseScore + bonus + increases.GetValueOrDefault(attribute.Key));
        }
    }

    // ---- Multiclass ----

    /// <summary>The other classes stored under the multiclass key: id, levels and optional subclass.</summary>
    public static List<OtherClass> ParseClassLevels(IReadOnlyDictionary<string, object> sheet, string key)
    {
        var result = new List<OtherClass>();
        foreach (var raw in RawList(sheet, key))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var level = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
            result.Add(new OtherClass(parts[0], level, parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null));
        }

        return result;
    }

    public static string FormatClassLevel(OtherClass other) =>
        $"{other.Id}:{other.Level.ToString(CultureInfo.InvariantCulture)}" + (string.IsNullOrEmpty(other.Subclass) ? "" : ":" + other.Subclass);

    /// <summary>
    /// One view of the sheet per class: the same values, with the class field set to that class and
    /// the class-level key set to its levels. Lists and slots are evaluated against every view, so a
    /// fighter 2 / wizard 3 sees the wizard spell list at wizard level 3. Without multiclass rules
    /// there is a single view: the sheet itself.
    /// </summary>
    private List<IReadOnlyDictionary<string, object>> ClassViews(IReadOnlyDictionary<string, object> sheet, List<ClassLevel> classes)
    {
        if (Rules!.Multiclass is not { } rules) return new List<IReadOnlyDictionary<string, object>> { sheet };

        var total = TotalLevel(sheet, rules);
        var others = ParseClassLevels(sheet, rules.Key);
        var classType = ClassType(rules);
        var subclassType = rules.SubclassField == null ? null : system.ActorFields.FirstOrDefault(f => f.Key == rules.SubclassField)?.TargetEntityType;
        var firstId = YamlValues.GetString(sheet, rules.ClassField);
        var firstLevel = total - others.Sum(o => o.Level);
        var firstSubclass = rules.SubclassField == null ? null : YamlValues.GetString(sheet, rules.SubclassField);

        ContentEntry? Subclass(string? id) => string.IsNullOrEmpty(id) || subclassType == null ? null : resolver.Resolve(subclassType, id);

        var first = new Dictionary<string, object>(sheet) { [rules.ClassLevelKey] = (long)firstLevel };
        var views = new List<IReadOnlyDictionary<string, object>> { first };
        if (!string.IsNullOrEmpty(firstId))
        {
            classes.Add(new ClassLevel(firstId, classType == null ? null : resolver.Resolve(classType, firstId), firstLevel, true, firstSubclass, Subclass(firstSubclass)));
        }

        foreach (var other in others)
        {
            // Another class never inherits the first class's subclass.
            var view = new Dictionary<string, object>(sheet) { [rules.ClassField] = other.Id, [rules.ClassLevelKey] = (long)other.Level };
            if (rules.SubclassField != null)
            {
                if (string.IsNullOrEmpty(other.Subclass)) view.Remove(rules.SubclassField);
                else view[rules.SubclassField] = other.Subclass;
            }

            views.Add(view);
            classes.Add(new ClassLevel(other.Id, classType == null ? null : resolver.Resolve(classType, other.Id), other.Level, false, other.Subclass, Subclass(other.Subclass)));
        }

        return views;
    }

    private bool IsSubclassField(string key) => Rules?.Multiclass?.SubclassField is { } field && field.Equals(key, StringComparison.OrdinalIgnoreCase);

    private int TotalLevel(IReadOnlyDictionary<string, object> sheet, MulticlassRules rules)
    {
        var field = system.ActorFields.FirstOrDefault(f => f.Key == rules.LevelField);
        return YamlValues.GetInt(sheet, rules.LevelField) ?? (int)(ToDouble(field?.DefaultValue) ?? 1);
    }

    private string? ClassType(MulticlassRules rules) => system.ActorFields.FirstOrDefault(f => f.Key == rules.ClassField)?.TargetEntityType;

    private void EvaluateMulticlass(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules!.Multiclass is not { } rules) return;
        var classType = ClassType(rules);
        if (classType == null) return;

        var scores = result.Attributes?.Totals
            ?? system.Attributes.ToDictionary(a => a.Key, a => YamlValues.GetInt(sheet, a.Key) ?? a.Default);
        var others = result.Classes.Where(c => !c.IsFirst).ToList();
        var taken = result.Classes.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        result.MulticlassOptions = resolver.Candidates(classType)
            .Where(c => !taken.Contains(c.Id) && MeetsPrerequisites(c, rules, scores, out _))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EvaluateSubclasses(rules, result);
        if (others.Count == 0) return;

        var first = result.Classes.FirstOrDefault(c => c.IsFirst);
        if (first == null)
        {
            result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: choose a first class before adding more."));
        }
        else if (first.Level < 1)
        {
            result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: the other classes take {others.Sum(o => o.Level)} levels, leaving none for {first.Entry?.Name ?? first.Id}. Raise the character level."));
        }

        foreach (var duplicate in result.Classes.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: {duplicate.First().Entry?.Name ?? duplicate.Key} is listed more than once."));
        }

        foreach (var cls in result.Classes)
        {
            if (cls.Entry == null)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: \"{cls.Id}\" is not in the Library."));
                continue;
            }

            if (!cls.IsFirst && cls.Level < 1)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: {cls.Entry.Name} needs at least 1 level."));
            }

            if ((cls.IsFirst && !rules.RequireForFirstClass) || MeetsPrerequisites(cls.Entry, rules, scores, out var needed)) continue;
            result.Violations.Add(new RuleViolation(rules.Key, cls.IsFirst
                ? $"{rules.Label}: to multiclass out of {cls.Entry.Name} you need {needed}."
                : $"{rules.Label}: {cls.Entry.Name} needs {needed}."));
        }
    }

    /// <summary>A subclass needs its class's level, belongs to that class, and each class has one.</summary>
    private void EvaluateSubclasses(MulticlassRules rules, SheetEvaluation result)
    {
        if (rules.SubclassField == null) return;
        var label = system.ActorFields.FirstOrDefault(f => f.Key == rules.SubclassField)?.Label ?? "Subclass";
        var constraint = Rules!.Constraints.FirstOrDefault(c => c.Field == rules.SubclassField);
        foreach (var cls in result.Classes)
        {
            if (string.IsNullOrEmpty(cls.SubclassId)) continue;
            var className = cls.Entry?.Name ?? cls.Id;
            // The first class's own subclass is already checked against the field's constraint.
            var key = cls.IsFirst ? rules.SubclassField : rules.Key;
            if (cls.Subclass == null)
            {
                if (!cls.IsFirst) result.Violations.Add(new RuleViolation(key, $"{label}: \"{cls.SubclassId}\" is not in the Library."));
            }
            else if (!cls.IsFirst)
            {
                var asClass = new Dictionary<string, object> { [rules.ClassField] = cls.Id };
                foreach (var condition in constraint?.Where.Where(w => !Holds(w, cls.Subclass, asClass)) ?? Enumerable.Empty<RuleCondition>())
                {
                    result.Violations.Add(new RuleViolation(key, $"{label}: {className}'s {cls.Subclass.Name} - {condition.Message ?? "this choice is not allowed by the rules."}"));
                }
            }

            if (cls.Level < rules.SubclassLevel)
            {
                result.Violations.Add(new RuleViolation(key, $"{label}: {className} gets a subclass at level {rules.SubclassLevel}, and you have {cls.Level}."));
            }
        }
    }

    private static readonly Regex Requirement = new(@"^\s*(?<attr>[A-Za-z][A-Za-z ]*?)\s+(?<min>\d+)\s*\+?\s*$", RegexOptions.Compiled);

    /// <summary>True when the class lists no prerequisites or the scores meet any one of its options;
    /// <paramref name="needed"/> describes the options for a message.</summary>
    public bool MeetsPrerequisites(ContentEntry cls, MulticlassRules rules, IReadOnlyDictionary<string, int> scores, out string needed)
    {
        var listed = cls.Properties.GetValueOrDefault(rules.PrerequisitesProperty);
        var options = listed is not string and IEnumerable<object> list ? list.Select(o => o?.ToString() ?? "").Where(o => o.Length > 0).ToList()
            : listed is string single && single.Length > 0 ? new List<string> { single } : new List<string>();
        needed = string.Join(" or ", options);
        if (options.Count == 0) return true;

        foreach (var option in options)
        {
            var met = true;
            foreach (var part in Regex.Split(option, @",|\band\b", RegexOptions.IgnoreCase).Where(p => p.Trim().Length > 0))
            {
                var match = Requirement.Match(part);
                if (!match.Success || MatchAttribute(match.Groups["attr"].Value.Trim()) is not { } attribute) continue; // unreadable: don't block
                if (scores.GetValueOrDefault(attribute.Key) < int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture)) met = false;
            }

            if (met) return true;
        }

        return false;
    }

    // ---- Slot pools ----

    private void EvaluateSlotPools(List<IReadOnlyDictionary<string, object>> views, SheetEvaluation result)
    {
        foreach (var rule in Rules!.SlotPools)
        {
            var contributions = new List<(string? CasterType, int Level, Dictionary<int, int> Slots)>();
            foreach (var view in views)
            {
                var entry = ReferencedEntry(rule.Field, view);
                if (entry == null) continue;
                var casterType = entry.Properties.GetValueOrDefault(rule.CasterTypeProperty)?.ToString();
                if (rule.IncludeCasterTypes.Count > 0 && !rule.IncludeCasterTypes.Contains(casterType ?? "", StringComparer.OrdinalIgnoreCase)) continue;
                if (rule.ExcludeCasterTypes.Contains(casterType ?? "", StringComparer.OrdinalIgnoreCase)) continue;

                var level = ToDouble(view.GetValueOrDefault(rule.ByField));
                if (level is not > 0) continue;
                var row = LookupRow(entry, rule.Table, level.Value, "level");
                if (row == null) continue;
                var slotLevel = rule.SlotLevelColumn == null ? null : ToDouble(Cell(row, rule.SlotLevelColumn));
                var slots = ParseSlots(Cell(row, rule.Column), slotLevel.HasValue ? (int)slotLevel.Value : null);
                if (slots.Count > 0) contributions.Add((casterType, (int)level.Value, slots));
            }

            if (contributions.Count == 0) continue;

            Dictionary<int, int> max;
            if (contributions.Count > 1 && rule.Combine is { Table.Count: > 0 } combine)
            {
                var casterLevel = Round(contributions.Sum(c => c.Level * combine.Weights.GetValueOrDefault(c.CasterType ?? "", 0)), combine.Rounding);
                max = casterLevel > 0 ? ParseSlots(combine.Table[Math.Min(casterLevel, combine.Table.Count) - 1], null) : new Dictionary<int, int>();
            }
            else
            {
                max = new Dictionary<int, int>();
                foreach (var (slotLevel, count) in contributions.SelectMany(c => c.Slots)) max[slotLevel] = max.GetValueOrDefault(slotLevel) + count;
            }

            if (max.Count > 0) result.SlotPools.Add(new SlotPool(rule, max));
        }
    }

    private static int Round(double value, string rounding) => rounding.ToLowerInvariant() switch
    {
        "up" => (int)Math.Ceiling(value - 1e-9),
        "nearest" => (int)Math.Round(value, MidpointRounding.AwayFromZero),
        _ => (int)Math.Floor(value + 1e-9)
    };

    /// <summary><c>"4/3/2"</c> → {1: 4, 2: 3, 3: 2}; a plain count with a slot level → {level: count}.</summary>
    public static Dictionary<int, int> ParseSlots(object? value, int? slotLevel)
    {
        var slots = new Dictionary<int, int>();
        var text = value switch { null => null, long l => l.ToString(CultureInfo.InvariantCulture), int i => i.ToString(CultureInfo.InvariantCulture), double d => d.ToString(CultureInfo.InvariantCulture), _ => value.ToString() };
        if (string.IsNullOrWhiteSpace(text)) return slots;

        if (slotLevel.HasValue)
        {
            if (slotLevel > 0 && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0) slots[slotLevel.Value] = count;
            return slots;
        }

        var parts = text.Split('/', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0) slots[i + 1] = count;
        }

        return slots;
    }

    /// <summary>
    /// Brings the sheet's slot resources in line with the evaluation: sets each <c>_Max</c>, fills a
    /// new slot level, trims remaining slots above a lowered maximum and drops slot levels the
    /// character no longer has.
    /// </summary>
    public void ApplySlotPools(Dictionary<string, object> sheet, SheetEvaluation evaluation)
    {
        if (Rules == null) return;
        foreach (var rule in Rules.SlotPools)
        {
            var pool = evaluation.SlotPools.FirstOrDefault(p => p.Rule == rule);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (level, max) in pool?.Max ?? new Dictionary<int, int>())
            {
                var key = rule.KeyFor(level);
                var maxKey = rule.MaxKeyFor(level);
                keep.Add(key);
                keep.Add(maxKey);
                var previousMax = YamlValues.GetInt(sheet, maxKey);
                var current = YamlValues.GetInt(sheet, key);
                sheet[maxKey] = (long)max;
                // A slot level that just appeared (or grew) starts with the new slots available.
                if (current == null) current = max;
                else if (previousMax.HasValue && max > previousMax.Value) current += max - previousMax.Value;
                sheet[key] = (long)Math.Clamp(current.Value, 0, max);
            }

            foreach (var stale in sheet.Keys.Where(k => rule.Owns(k) && !keep.Contains(k)).ToList()) sheet.Remove(stale);
        }
    }

    /// <summary>Rest names any slot pool recovers on, in declaration order.</summary>
    public IReadOnlyList<string> RestNames() =>
        Rules?.SlotPools.SelectMany(p => p.RecoversOn).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

    // ---- Derived ----

    private void EvaluateDerived(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules!.Derived.Count == 0) return;

        var (interpreter, identifiers) = FormulaContext(sheet);
        foreach (var derived in Rules.Derived)
        {
            try
            {
                result.Derived[derived.Key] = EvaluateFormula(interpreter, identifiers, derived.Formula);
            }
            catch (Exception ex)
            {
                result.Violations.Add(new RuleViolation(derived.Key, $"{derived.Label}: formula error ({ex.Message})."));
            }
        }
    }

    private static long EvaluateFormula(Interpreter interpreter, Parameter[] identifiers, string formula) =>
        (long)Math.Floor(Convert.ToDouble(interpreter.Eval(formula, identifiers), CultureInfo.InvariantCulture));

    /// <summary>A formula interpreter and the sheet's numbers as identifiers (attribute scores, numeric fields).</summary>
    private (Interpreter Interpreter, Parameter[] Identifiers) FormulaContext(IReadOnlyDictionary<string, object> sheet)
    {
        var interpreter = new Interpreter();
        var identifiers = new List<Parameter>();
        foreach (var (key, value) in sheet)
        {
            if (!IsIdentifier(key)) continue;
            var number = YamlValues.ToNumber(value);
            if (number.HasValue) identifiers.Add(new Parameter(key, number.Value));
        }

        foreach (var attribute in system.Attributes)
        {
            if (sheet.ContainsKey(attribute.Key)) continue;
            identifiers.Add(new Parameter(attribute.Key, (double)attribute.Default));
        }

        // A numeric field the sheet hasn't set yet counts as its declared default (level 1), or 0.
        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.Number && IsIdentifier(f.Key)))
        {
            if (identifiers.Any(p => p.Name == field.Key)) continue;
            identifiers.Add(new Parameter(field.Key, ToDouble(field.DefaultValue) ?? 0d));
        }

        interpreter.SetFunction("floor", (Func<double, double>)Math.Floor);
        interpreter.SetFunction("ceil", (Func<double, double>)Math.Ceiling);
        interpreter.SetFunction("max", (Func<double, double, double>)Math.Max);
        interpreter.SetFunction("min", (Func<double, double, double>)Math.Min);
        interpreter.SetFunction("mod", (Func<double, double>)(score => Math.Floor((score - 10) / 2)));
        return (interpreter, identifiers.ToArray());
    }

    private static bool IsIdentifier(string key) =>
        key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_') && key.All(c => char.IsLetterOrDigit(c) || c == '_');

    // ---- Conditions ----

    public bool Holds(RuleCondition condition, ContentEntry candidate, IReadOnlyDictionary<string, object> sheet)
    {
        var value = CandidateValue(candidate, condition.Property);

        if (condition.Equals != null)
        {
            return ValuesEqual(value, condition.Equals);
        }

        if (condition.In != null)
        {
            var text = value?.ToString();
            return text != null && condition.In.Any(option => option.Equals(text, StringComparison.OrdinalIgnoreCase));
        }

        if (condition.Gte.HasValue || condition.Lte.HasValue)
        {
            var number = ToDouble(value);
            if (number == null) return false;
            return (!condition.Gte.HasValue || number >= condition.Gte) && (!condition.Lte.HasValue || number <= condition.Lte);
        }

        if (condition.EqualsField != null)
        {
            var sheetValue = sheet.TryGetValue(condition.EqualsField, out var sv) ? sv : null;
            return sheetValue != null && ValuesEqual(value, sheetValue);
        }

        if (condition.EqualsPropertyOfField is { } fieldProperty)
        {
            var referenced = ReferencedEntry(fieldProperty.Field, sheet);
            if (referenced == null) return true; // nothing chosen yet: no constraint to apply
            return referenced.Properties.TryGetValue(fieldProperty.Property, out var expected) && ValuesEqual(value, expected);
        }

        if (condition.ContainsNameOfField != null)
        {
            var referenced = ReferencedEntry(condition.ContainsNameOfField, sheet);
            if (referenced == null) return false;
            var names = value is IEnumerable<object> list ? list.Select(o => o?.ToString() ?? "") : new[] { value?.ToString() ?? "" };
            return names.Any(n => n.Equals(referenced.Name, StringComparison.OrdinalIgnoreCase) || n.Equals(referenced.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (condition.LteField != null || condition.GteField != null)
        {
            var number = ToDouble(value);
            if (number == null) return true; // the candidate doesn't say: don't block
            if (condition.LteField != null)
            {
                var bound = ToDouble(sheet.GetValueOrDefault(condition.LteField));
                if (bound == null || number > bound) return false;
            }

            if (condition.GteField != null)
            {
                var bound = ToDouble(sheet.GetValueOrDefault(condition.GteField));
                if (bound == null || number < bound) return false;
            }

            return true;
        }

        if (condition.LteLookup is { } lookup)
        {
            var number = ToDouble(value);
            if (number == null) return true;
            var bound = Lookup(lookup, sheet);
            return bound.HasValue && number <= bound.Value;
        }

        return true;
    }

    private static object? CandidateValue(ContentEntry candidate, string property) => property.ToLowerInvariant() switch
    {
        "id" => candidate.Id,
        "name" => candidate.Name,
        "type" => candidate.Type,
        "tags" => candidate.Tags.Cast<object>().ToList(),
        "packid" => candidate.PackId,
        _ => candidate.Properties.GetValueOrDefault(property)
    };

    private ContentEntry? ReferencedEntry(string fieldKey, IReadOnlyDictionary<string, object> sheet)
    {
        var field = system.ActorFields.FirstOrDefault(f => f.Key == fieldKey);
        var id = YamlValues.GetString(sheet, fieldKey);
        return field?.TargetEntityType == null || string.IsNullOrEmpty(id) ? null : resolver.Resolve(field.TargetEntityType, id);
    }

    /// <summary>The number a <see cref="LookupRule"/> resolves to for this sheet, or null when the
    /// referenced entry, table, row or column is missing.</summary>
    public double? Lookup(LookupRule lookup, IReadOnlyDictionary<string, object> sheet)
    {
        var entry = ReferencedEntry(lookup.Field, sheet);
        var key = ToDouble(sheet.GetValueOrDefault(lookup.ByField));
        if (entry == null || key == null) return null;
        var row = LookupRow(entry, lookup.Table, key.Value, lookup.RowKey);
        return row == null ? null : ToDouble(Cell(row, lookup.Column));
    }

    private static object? Cell(IDictionary<string, object> row, string key) => row.TryGetValue(key, out var value) ? value : null;

    /// <summary>The row of an entry's table whose key equals <paramref name="key"/>, or the highest row below it.</summary>
    private static IDictionary<string, object>? LookupRow(ContentEntry entry, string table, double key, string rowKey)
    {
        if (!entry.Properties.TryGetValue(table, out var raw) || raw is not IEnumerable<object> rows) return null;

        IDictionary<string, object>? best = null;
        var bestKey = double.MinValue;
        foreach (var row in rows.OfType<IDictionary<string, object>>())
        {
            var value = ToDouble(row.TryGetValue(rowKey, out var rk) ? rk : null);
            if (value == null || value > key || value < bestKey) continue;
            bestKey = value.Value;
            best = row;
        }

        return best;
    }

    /// <summary>A list's limit across every class view: table lookups are added up per class (a
    /// wizard 3 / cleric 2 prepares both classes' spells), a constant counts once.</summary>
    private int? ResolveCount(CountRule? rule, IReadOnlyList<IReadOnlyDictionary<string, object>> views)
    {
        if (rule == null) return null;
        var property = 0d;
        var hasProperty = false;
        if (rule.FromField is { } source && ReferencedEntry(source.Field, views[0]) is { } entry)
        {
            property = ToDouble(entry.Properties.GetValueOrDefault(source.Property)) ?? 0;
            hasProperty = true;
        }

        if (rule.Lookup == null)
        {
            if (rule.Value.HasValue) return rule.Value.Value + rule.Plus + (int)property;
            return hasProperty ? rule.Plus + (int)property : null;
        }

        double total = 0;
        var known = false;
        foreach (var view in views)
        {
            foreach (var lookup in rule.Also.Prepend(rule.Lookup))
            {
                if (Lookup(lookup, view) is { } value)
                {
                    total += value;
                    known = true;
                }
                // An entry that exists but has no such table (a fighter and "spellcasting") gets 0: the
                // class simply doesn't grant any. No referenced entry yet (no class chosen): unknown.
                else if (ReferencedEntry(lookup.Field, view) != null)
                {
                    known = true;
                }
            }
        }

        return known || hasProperty ? (int)(total + property) + rule.Plus : null;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        var na = ToDouble(a);
        var nb = ToDouble(b);
        if (na.HasValue && nb.HasValue) return Math.Abs(na.Value - nb.Value) < 0.0001;
        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static double? ToDouble(object? value) => YamlValues.ToNumber(value);
}
