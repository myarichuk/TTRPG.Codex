using System.Globalization;
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
    IReadOnlyDictionary<string, int> Totals);

/// <summary>The engine's whole verdict on one sheet, used by the UI to render and by save to decide.</summary>
public sealed class SheetEvaluation
{
    public Dictionary<string, object> Derived { get; } = new();
    public List<RuleViolation> Violations { get; } = new();

    /// <summary>For each constrained reference field, the entries the sheet may currently pick.</summary>
    public Dictionary<string, IReadOnlyList<ContentEntry>> AllowedOptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For each list rule, the entries that may be added now (not yet picked, conditions met).</summary>
    public Dictionary<string, IReadOnlyList<ContentEntry>> ListOptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For each list rule, the resolved picked entries in sheet order (unresolvable ids are reported as violations).</summary>
    public Dictionary<string, IReadOnlyList<ContentEntry>> ListPicks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int?> ListLimits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AttributeSummary? Attributes { get; set; }

    public bool HasViolations => Violations.Count > 0;
}

/// <summary>
/// Evaluates a system's <see cref="SheetRules"/> against a sheet. Pure and stateless: given the
/// definition, the sheet dictionary and a resolver for the entries it references, it produces
/// derived values, the options each picker may offer, and the list of violations. The UI filters
/// pickers with it; save uses it to enforce.
/// </summary>
public sealed class SheetRuleEngine(SystemDefinition system, IEntryResolver resolver)
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

    /// <summary>Seeds a brand-new sheet: default attribute method and base scores.</summary>
    public void InitializeSheet(Dictionary<string, object> sheet)
    {
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

        foreach (var constraint in Rules.Constraints)
        {
            var field = system.ActorFields.FirstOrDefault(f => f.Key == constraint.Field);
            if (field?.TargetEntityType == null) continue;
            var allowed = resolver.Candidates(field.TargetEntityType).Where(c => constraint.Where.All(w => Holds(w, c, sheet))).ToList();
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
            var picked = sheet.TryGetValue(list.Key, out var raw) && raw is IEnumerable<object> items
                ? items.Select(i => i?.ToString() ?? string.Empty).Where(i => i.Length > 0).ToList()
                : new List<string>();

            var resolved = new List<ContentEntry>();
            foreach (var id in picked)
            {
                var entry = resolver.Resolve(list.TargetEntityType, id);
                if (entry == null)
                {
                    result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: \"{id}\" is not in the Library."));
                    continue;
                }

                resolved.Add(entry);
                foreach (var condition in list.Where.Where(w => !Holds(w, entry, sheet)))
                {
                    result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: {entry.Name} - {condition.Message ?? "not allowed by the rules."}"));
                }
            }

            result.ListPicks[list.Key] = resolved;
            var pickedIds = picked.ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.ListOptions[list.Key] = resolver.Candidates(list.TargetEntityType)
                .Where(c => !pickedIds.Contains(c.Id) && list.Where.All(w => Holds(w, c, sheet)))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var limit = ResolveCount(list.MaxCount, sheet);
            result.ListLimits[list.Key] = limit;
            if (limit.HasValue && picked.Count > limit.Value)
            {
                result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: {picked.Count} picked, but only {limit.Value} allowed at this level."));
            }
        }

        return result;
    }

    // ---- Attributes ----

    private void EvaluateAttributes(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        var rules = Rules!.Attributes;
        if (rules == null || system.Attributes.Count == 0) return;

        var method = (YamlValues.GetString(sheet, MethodKey) ?? rules.Default).ToLowerInvariant();
        var bases = new Dictionary<string, int>();
        var bonuses = new Dictionary<string, int>();
        var totals = new Dictionary<string, int>();
        foreach (var attribute in system.Attributes)
        {
            var baseScore = YamlValues.GetInt(sheet, BasePrefix + attribute.Key) ?? YamlValues.GetInt(sheet, attribute.Key) ?? attribute.Default;
            var bonus = YamlValues.GetInt(sheet, BonusPrefix + attribute.Key) ?? 0;
            bases[attribute.Key] = baseScore;
            if (bonus != 0) bonuses[attribute.Key] = bonus;
            totals[attribute.Key] = baseScore + bonus;
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

        if (rules.Max.HasValue)
        {
            foreach (var (key, total) in totals.Where(t => t.Value > rules.Max.Value))
            {
                result.Violations.Add(new RuleViolation(key, $"{system.Attributes.First(a => a.Key == key).Label}: scores can't exceed {rules.Max.Value}."));
            }
        }

        result.Attributes = new AttributeSummary(method, spent, budget, eligible, bonuses, totals);
    }

    /// <summary>Attribute keys the bonus rule allows, matched by key, abbreviation or label.</summary>
    public List<string> EligibleBonusAttributes(BonusRules bonusRules, IReadOnlyDictionary<string, object> sheet)
    {
        var field = system.ActorFields.FirstOrDefault(f => f.Key == bonusRules.FromField);
        var id = YamlValues.GetString(sheet, bonusRules.FromField);
        if (field?.TargetEntityType == null || string.IsNullOrEmpty(id)) return new List<string>();
        var entry = resolver.Resolve(field.TargetEntityType, id);
        if (entry == null || !entry.Properties.TryGetValue(bonusRules.Property, out var listed)) return new List<string>();

        var names = listed is IEnumerable<object> list ? list.Select(o => o?.ToString() ?? "").ToList() : new List<string> { listed.ToString() ?? "" };
        return system.Attributes
            .Where(a => names.Any(n => n.Equals(a.Key, StringComparison.OrdinalIgnoreCase) || n.Equals(a.Abbreviation, StringComparison.OrdinalIgnoreCase) || n.Equals(a.Label, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Key)
            .ToList();
    }

    /// <summary>Writes base + bonus into each attribute's total key, so the ECS sees final scores.</summary>
    public void ApplyAttributeTotals(Dictionary<string, object> sheet)
    {
        if (Rules?.Attributes == null) return;
        foreach (var attribute in system.Attributes)
        {
            if (!sheet.ContainsKey(BasePrefix + attribute.Key)) continue;
            var baseScore = YamlValues.GetInt(sheet, BasePrefix + attribute.Key) ?? attribute.Default;
            var bonus = YamlValues.GetInt(sheet, BonusPrefix + attribute.Key) ?? 0;
            sheet[attribute.Key] = (long)(baseScore + bonus);
        }
    }

    // ---- Derived ----

    private void EvaluateDerived(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules!.Derived.Count == 0) return;

        var interpreter = new Interpreter();
        var identifiers = new List<Parameter>();
        foreach (var (key, value) in sheet)
        {
            if (!IsIdentifier(key)) continue;
            var number = value switch { long l => (double)l, int i => i, double d => d, string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed, _ => (double?)null };
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
            var fallback = field.DefaultValue switch
            {
                long l => (double)l,
                int i => i,
                double d => d,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0d,
            };
            identifiers.Add(new Parameter(field.Key, fallback));
        }

        interpreter.SetFunction("floor", (Func<double, double>)Math.Floor);
        interpreter.SetFunction("ceil", (Func<double, double>)Math.Ceiling);
        interpreter.SetFunction("max", (Func<double, double, double>)Math.Max);
        interpreter.SetFunction("min", (Func<double, double, double>)Math.Min);
        interpreter.SetFunction("mod", (Func<double, double>)(score => Math.Floor((score - 10) / 2)));

        foreach (var derived in Rules.Derived)
        {
            try
            {
                var value = interpreter.Eval(derived.Formula, identifiers.ToArray());
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                result.Derived[derived.Key] = (long)Math.Floor(number);
            }
            catch (Exception ex)
            {
                result.Violations.Add(new RuleViolation(derived.Key, $"{derived.Label}: formula error ({ex.Message})."));
            }
        }
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
        if (entry == null || !entry.Properties.TryGetValue(lookup.Table, out var table) || table is not IEnumerable<object> rows) return null;
        var key = ToDouble(sheet.GetValueOrDefault(lookup.ByField));
        if (key == null) return null;

        Dictionary<string, object>? best = null;
        double bestKey = double.MinValue;
        foreach (var row in rows.OfType<IDictionary<string, object>>())
        {
            var rowKey = ToDouble(row.TryGetValue(lookup.RowKey, out var rk) ? rk : null);
            if (rowKey == null || rowKey > key || rowKey < bestKey) continue;
            bestKey = rowKey.Value;
            best = new Dictionary<string, object>(row);
        }

        return best == null ? null : ToDouble(best.GetValueOrDefault(lookup.Column));
    }

    private int? ResolveCount(CountRule? rule, IReadOnlyDictionary<string, object> sheet)
    {
        if (rule == null) return null;
        double? value = rule.Value;
        if (rule.Lookup != null)
        {
            value = Lookup(rule.Lookup, sheet);
            if (value == null)
            {
                // No referenced entry yet (no class chosen): the limit is unknown, so don't block.
                // An entry that exists but has no such table (a fighter and "spellcasting") gets 0:
                // the class simply doesn't grant any.
                return ReferencedEntry(rule.Lookup.Field, sheet) == null ? null : rule.Plus;
            }
        }

        return value.HasValue ? (int)value.Value + rule.Plus : null;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        var na = ToDouble(a);
        var nb = ToDouble(b);
        if (na.HasValue && nb.HasValue) return Math.Abs(na.Value - nb.Value) < 0.0001;
        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static double? ToDouble(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        double d => d,
        float f => f,
        bool => null,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };
}
