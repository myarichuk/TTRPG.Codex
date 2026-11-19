using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>What is wrong with a system's <c>issueChecks</c> (layers 2 and 3 of the consistency checker): each one a sentence naming the check. Any
/// of them refuses the system at load. A rule must name a sheet key the system declares, a message may only fill the blanks the checker knows, and a
/// key the section does not read is an error, not a thing ignored, so a typo (<c>sevrity</c>) cannot make a check quietly do nothing.</summary>
public static class IssueCheckProblems
{
    public static readonly IReadOnlyList<string> Rules = new[] { "required", "empty", "range", "reference", "count" };
    public static readonly IReadOnlyList<string> Severities = new[] { "error", "warning", "info" };
    public static readonly IReadOnlyList<string> Roles = new[] { "pc", "npc", "monster" };

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-_]*$", RegexOptions.Compiled);
    private static readonly Regex Blank = new(@"\{(\w*)\}", RegexOptions.Compiled);
    private static readonly string[] Blanks = { "name", "field", "value", "min", "max" };

    /// <summary>The sheet keys a rule may be about: attributes, actor fields, the inventory and the lists.</summary>
    public static IReadOnlyDictionary<string, string?> Keys(SystemDefinition system)
    {
        var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in system.Attributes) keys[a.Key] = "number";
        foreach (var f in system.ActorFields) keys[f.Key] = f.Type == FieldType.Number ? "number" : f.Type == FieldType.EntityReference ? "reference" : f.Type == FieldType.TagList ? "list" : "text";
        if (system.SheetRules?.Inventory is { } inventory) keys[inventory.Key] = "list";
        foreach (var l in system.SheetRules?.Lists ?? new List<ListRule>()) keys[l.Key] = "list";
        return keys;
    }

    /// <summary>The keys of the file under <c>issueChecks</c> that the model does not read, as errors; <paramref name="unknownKeys"/> are the dotted paths
    /// <see cref="UnknownYamlKeys"/> found.</summary>
    public static IEnumerable<string> UnknownKeys(IEnumerable<string> unknownKeys) =>
        unknownKeys.Where(k => k.StartsWith("issueChecks", StringComparison.Ordinal)).Select(k => $"{k} is not a key an issue check reads (see the issueChecks section of the reference).");

    public static IEnumerable<string> Problems(SystemDefinition system)
    {
        var keys = Keys(system);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < system.IssueChecks.Count; i++)
        {
            var check = system.IssueChecks[i];
            var name = string.IsNullOrWhiteSpace(check.Id) ? $"issueChecks[{i}]" : $"issue check '{check.Id}'";
            if (string.IsNullOrWhiteSpace(check.Id)) yield return $"{name}: id is required.";
            else if (!IdPattern.IsMatch(check.Id)) yield return $"{name}: id must be lower-case letters, digits, '-' or '_', and start with a letter or digit.";
            else if (!seen.Add(check.Id)) yield return $"{name} is declared more than once.";

            var compiled = !string.IsNullOrWhiteSpace(check.Type);
            if (compiled == !string.IsNullOrWhiteSpace(check.Rule))
            {
                yield return $"{name}: give either a rule ({string.Join(", ", Rules)}) or a type (compiled code), not both and not neither.";
                continue;
            }

            if (compiled)
            {
                foreach (var set in new (string Key, bool Present)[] { ("field", check.Field != null), ("message", check.Message != null), ("when", check.When != null), ("min", check.Min != null), ("max", check.Max != null), ("targetEntityType", check.TargetEntityType != null), ("fixHint", check.FixHint != null), ("code", check.Code != null) }.Where(s => s.Present))
                {
                    yield return $"{name}: a compiled check says its own message; '{set.Key}' belongs to a rule.";
                }

                continue;
            }

            if (!Rules.Contains(check.Rule!)) yield return $"{name}: rule '{check.Rule}' is not one of {string.Join(", ", Rules)}.";
            if (!Severities.Contains(check.Severity)) yield return $"{name}: severity '{check.Severity}' is not one of {string.Join(", ", Severities)}.";
            if (string.IsNullOrWhiteSpace(check.Message)) yield return $"{name}: message is required.";
            else
            {
                foreach (var blank in Blank.Matches(check.Message).Select(m => m.Groups[1].Value).Where(b => !Blanks.Contains(b)).Distinct())
                {
                    yield return $"{name}: the message has {{{blank}}}; the blanks are {string.Join(", ", Blanks.Select(b => "{" + b + "}"))}.";
                }
            }

            foreach (var role in check.Roles.Where(r => !Roles.Contains(r))) yield return $"{name}: role '{role}' is not one of {string.Join(", ", Roles)}.";
            if (check.Code is { } code && !Regex.IsMatch(code, @"^[A-Z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$")) yield return $"{name}: code '{code}' must be a dotted key such as Consistency.System.Wizard.";

            string? kind = null;
            if (string.IsNullOrWhiteSpace(check.Field)) yield return $"{name}: field is required for a rule.";
            else if (!keys.TryGetValue(check.Field, out kind)) yield return $"{name}: field '{check.Field}' is not a sheet key (attributes, actor fields, the inventory and the lists of this system are).";

            if (check.When is { } when)
            {
                if (!keys.ContainsKey(when.Field ?? string.Empty)) yield return $"{name}: when.field '{when.Field}' is not a sheet key.";
                var operators = new[] { when.Is != null, when.In is { Count: > 0 }, when.NotEmpty == true, when.Empty == true }.Count(x => x);
                if (operators != 1) yield return $"{name}: when needs exactly one of is, in, notEmpty and empty (true).";
            }

            switch (check.Rule)
            {
                case "range":
                    if (kind != null && kind != "number") yield return $"{name}: a range is over a number, and '{check.Field}' is not one.";
                    if (check.Min == null && check.Max == null) yield return $"{name}: a range needs min, max or both.";
                    break;
                case "count":
                    if (kind != null && kind != "list") yield return $"{name}: a count is over a list (the inventory, one of the lists, a tag list), and '{check.Field}' is not one.";
                    if (check.Min == null && check.Max == null) yield return $"{name}: a count needs min, max or both.";
                    break;
                case "reference":
                    if (kind != null && kind is not ("reference" or "list") && string.IsNullOrWhiteSpace(check.TargetEntityType))
                    {
                        yield return $"{name}: '{check.Field}' is not a reference to an entry; say targetEntityType.";
                    }

                    if (!string.IsNullOrWhiteSpace(check.TargetEntityType) && system.FindType(check.TargetEntityType) == null) yield return $"{name}: targetEntityType '{check.TargetEntityType}' is not a content type of this system.";
                    break;
            }

            if (check.Rule is not ("range" or "count") && (check.Min != null || check.Max != null)) yield return $"{name}: min and max are for a range or a count.";
            if (check.Min != null && check.Max != null && check.Min > check.Max) yield return $"{name}: min is above max.";
        }
    }
}
