using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>What a rule's conditions ask of an entry a model is about to write: the properties the entry is given whatever the model
/// says, and sentences for what can only be asked of it ("level: at most 3").</summary>
public sealed record EntryRequirements(IReadOnlyDictionary<string, object> Properties, IReadOnlyList<string> Hints)
{
    // Candidate values the engine reads off the entry itself, not off a property: nothing to set.
    private static readonly string[] NotProperties = { "id", "name", "type", "tags", "packid" };

    /// <summary>Turns <paramref name="where"/> (a list's or a field's conditions) into <see cref="EntryRequirements"/> for this sheet,
    /// or null when some condition cannot be written for (a value another entry names, an entry's id or name), so the slot is left
    /// alone. Equals, in (its first value), gte, lte, equalsField, containsNameOfField, lteField and gteField become properties;
    /// lteLookup, and the lower bound beside it, become a hint. This is a head start: the engine has the last word.</summary>
    public static EntryRequirements? For(IEnumerable<RuleCondition> where, SystemDefinition system, IEntryResolver entries, SheetRuleEngine engine, IReadOnlyDictionary<string, object> sheet)
    {
        sheet = engine.FirstClassView(sheet);
        var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var lower = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var upper = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var looked = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var condition in where)
        {
            if (condition.EqualsPropertyOfField != null) return null;
            if (NotProperties.Contains(condition.Property, StringComparer.OrdinalIgnoreCase) && Compares(condition)) return null;

            if (condition.Equals != null)
            {
                properties[condition.Property] = condition.Equals;
            }
            else if (condition.In is { Count: > 0 } options)
            {
                properties[condition.Property] = options[0];
            }
            else if (condition.Gte.HasValue || condition.Lte.HasValue)
            {
                if (condition.Gte.HasValue) Raise(lower, condition.Property, condition.Gte.Value);
                if (condition.Lte.HasValue) Lower(upper, condition.Property, condition.Lte.Value);
            }
            else if (condition.EqualsField != null)
            {
                if (!sheet.TryGetValue(condition.EqualsField, out var value) || value == null) return null;
                properties[condition.Property] = value;
            }
            else if (condition.ContainsNameOfField != null)
            {
                var field = system.ActorFields.FirstOrDefault(f => f.Key == condition.ContainsNameOfField);
                var id = sheet.TryGetValue(condition.ContainsNameOfField, out var named) ? named?.ToString() : null;
                var entry = field?.TargetEntityType == null || string.IsNullOrEmpty(id) ? null : entries.Resolve(field.TargetEntityType, id);
                if (entry == null) return null;
                properties[condition.Property] = new List<object> { entry.Name };
            }
            else if (condition.Contains != null)
            {
                properties[condition.Property] = new List<object> { condition.Contains };
            }
            else if (condition.ContainsChoice != null || condition.EqualsChoice != null)
            {
                return null; // what another choice picked: nothing to write ahead
            }
            else if (condition.LteField != null || condition.GteField != null)
            {
                if (condition.LteField != null)
                {
                    if (YamlValues.ToNumber(sheet.GetValueOrDefault(condition.LteField)) is not { } bound) return null;
                    Lower(upper, condition.Property, bound);
                }

                if (condition.GteField != null)
                {
                    if (YamlValues.ToNumber(sheet.GetValueOrDefault(condition.GteField)) is not { } bound) return null;
                    Raise(lower, condition.Property, bound);
                }
            }
            else if (condition.LteLookup is { } lookup)
            {
                if (engine.Lookup(lookup, sheet) is not { } bound) return null;
                Lower(looked, condition.Property, bound);
            }
        }

        var hints = new List<string>();
        foreach (var property in lower.Keys.Concat(upper.Keys).Concat(looked.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (properties.ContainsKey(property)) continue;
            var low = lower.TryGetValue(property, out var l) ? l : (double?)null;
            var high = upper.TryGetValue(property, out var u) ? u : (double?)null;
            if (looked.TryGetValue(property, out var lookedUp)) high = high.HasValue ? Math.Min(high.Value, lookedUp) : lookedUp;
            if (low.HasValue && high.HasValue && Math.Ceiling(low.Value) > Math.Floor(high.Value)) return null;

            // A bound that comes from a table is the most the writer may use, not a value to copy: asked in words.
            if (looked.ContainsKey(property))
            {
                hints.Add($"{property}: {Range(low, high)}");
            }
            else
            {
                properties[property] = (long)(low.HasValue ? Math.Ceiling(low.Value) : Math.Floor(high!.Value));
            }
        }

        return new EntryRequirements(properties, hints);
    }

    private static bool Compares(RuleCondition c) => c.Equals != null || c.In != null || c.Gte.HasValue || c.Lte.HasValue || c.EqualsField != null || c.ContainsNameOfField != null || c.Contains != null || c.ContainsChoice != null || c.EqualsChoice != null || c.LteField != null || c.GteField != null || c.LteLookup != null;

    private static void Raise(Dictionary<string, double> bounds, string property, double value) =>
        bounds[property] = bounds.TryGetValue(property, out var current) ? Math.Max(current, value) : value;

    private static void Lower(Dictionary<string, double> bounds, string property, double value) =>
        bounds[property] = bounds.TryGetValue(property, out var current) ? Math.Min(current, value) : value;

    private static string Range(double? low, double? high) => (low, high) switch
    {
        ({ } a, { } b) => $"at least {Math.Ceiling(a)} and at most {Math.Floor(b)}",
        (_, { } b) => $"at most {Math.Floor(b)}",
        ({ } a, _) => $"at least {Math.Ceiling(a)}",
        _ => string.Empty
    };
}
