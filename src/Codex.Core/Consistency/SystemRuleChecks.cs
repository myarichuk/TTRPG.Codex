using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Consistency;

/// <summary>
/// The checks a game system adds (<c>issueChecks</c> in system.yaml): rules over the sheet's own keys (layer 2) and compiled checks (layer 3). Every
/// rule is data read here, with no formula and no model; a compiled check is called inside a guard, and one that cannot be had, throws or returns what
/// cannot be read is one <c>info</c> issue of its own (<see cref="CheckNames.CheckFailed"/>), never a failure of the run. A retired character is never
/// checked, and nothing is reported where the campaign's rule enforcement is <c>off</c> (the DM has said the system's rules do not apply here).
/// </summary>
public sealed class SystemRuleCheck : IConsistencyCheck
{
    public string Name => CheckNames.SystemRule;

    /// <summary>The most issues one compiled check may return; the rest are dropped.</summary>
    public const int MaxCompiledIssues = 200;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (s.System is not { IssueChecks.Count: > 0 } system || s.Enforcement == "off") yield break;
        var keys = IssueCheckProblems.Keys(system);
        foreach (var check in system.IssueChecks.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
        {
            if (!string.IsNullOrWhiteSpace(check.Type))
            {
                foreach (var issue in Compiled(s, system, check)) yield return issue;
            }
            else
            {
                foreach (var issue in Rule(s, system, check, keys)) yield return issue;
            }
        }
    }

    // ---- layer 2: rules ----

    private IEnumerable<Issue> Rule(WorldSnapshot s, SystemDefinition system, IssueCheckDefinition check, IReadOnlyDictionary<string, string?> keys)
    {
        if (string.IsNullOrWhiteSpace(check.Field) || string.IsNullOrWhiteSpace(check.Rule)) yield break;
        var field = Canonical(keys, check.Field);
        var label = LabelOf(system, field);
        var roles = check.Roles.Count == 0 ? new[] { "pc", "npc" } : check.Roles.ToArray();
        var severity = check.Severity switch { "error" => IssueSeverity.Error, "info" => IssueSeverity.Info, _ => IssueSeverity.Warning };
        var fixable = SheetProposal.IsEditable(system, field);

        foreach (var actor in s.Actors.Where(a => !a.Retired && roles.Contains(RoleWord(a.Role))))
        {
            if (check.When is { } when && !Holds(when, actor.Sheet, keys)) continue;
            var found = Judge(check, field, actor, s, system);
            if (found == null) continue;
            var (value, shown) = found.Value;
            var message = Fill(check.Message ?? string.Empty, actor.Name, label, shown, check);
            var args = new[] { actor.Name, label, shown };
            yield return Make.Issue(Name, severity, "actor", actor.Id, field, check.Id, check.Code ?? "Consistency.System." + Pascal(check.Id), args, message,
                check.FixHint ?? DefaultHint(check, label),
                fixable ? FixVia.Sheet : FixVia.ReportOnly);
        }
    }

    // The value shown in the message, or null when the sheet is fine.
    private static (object? Value, string Shown)? Judge(IssueCheckDefinition check, string field, ActorView actor, WorldSnapshot s, SystemDefinition system)
    {
        actor.Sheet.TryGetValue(field, out var value);
        switch (check.Rule)
        {
            case "required":
                return IsEmpty(value) ? (value, "—") : null;
            case "empty":
                return IsEmpty(value) ? null : (value, Display(value));
            case "range":
                if (IsEmpty(value) || ToNumber(value) is not { } number) return null;
                return check.Min is { } lo && number < lo || check.Max is { } hi && number > hi ? (value, Num(number)) : null;
            case "count":
                var count = SheetRuleEngine.RawList(actor.Sheet, field).Count;
                return check.Min is { } min && count < min || check.Max is { } max && count > max ? (count, Num(count)) : null;
            case "reference":
                var types = TypesOf(check, field, system);
                if (types.Count == 0) return null;
                var missing = (value is string text ? new List<string> { text } : SheetRuleEngine.RawList(actor.Sheet, field).Select(p => SheetRuleEngine.ParsePick(p).Id).ToList())
                    .Where(id => id.Length > 0 && !types.Any(t => s.Library.Resolve(t, id) != null)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                return missing.Count == 0 ? null : (missing[0], string.Join(", ", missing));
            default:
                return null;
        }
    }

    private static IReadOnlyList<string> TypesOf(IssueCheckDefinition check, string field, SystemDefinition system)
    {
        if (!string.IsNullOrWhiteSpace(check.TargetEntityType)) return new[] { check.TargetEntityType };
        if (system.ActorFields.FirstOrDefault(f => string.Equals(f.Key, field, StringComparison.OrdinalIgnoreCase)) is { Type: FieldType.EntityReference, TargetEntityType: { Length: > 0 } t }) return new[] { t };
        if (system.SheetRules?.Inventory is { } inventory && string.Equals(inventory.Key, field, StringComparison.OrdinalIgnoreCase)) return inventory.Types;
        if (system.SheetRules?.Lists.FirstOrDefault(l => string.Equals(l.Key, field, StringComparison.OrdinalIgnoreCase)) is { } list) return new[] { list.TargetEntityType };
        return Array.Empty<string>();
    }

    private static bool Holds(IssueCondition when, IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, string?> keys)
    {
        sheet.TryGetValue(Canonical(keys, when.Field), out var value);
        if (when.NotEmpty == true) return !IsEmpty(value);
        if (when.Empty == true) return IsEmpty(value);
        var text = Display(value);
        if (when.Is != null) return string.Equals(text, when.Is, StringComparison.OrdinalIgnoreCase);
        return when.In?.Any(o => string.Equals(text, o, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string Canonical(IReadOnlyDictionary<string, string?> keys, string key) => keys.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;

    private static string LabelOf(SystemDefinition system, string key) =>
        system.Attributes.FirstOrDefault(a => a.Key == key)?.Label ?? system.ActorFields.FirstOrDefault(f => f.Key == key)?.Label
        ?? (system.SheetRules?.Inventory?.Key == key ? system.SheetRules.Inventory.Label : null) ?? system.SheetRules?.Lists.FirstOrDefault(l => l.Key == key)?.Label ?? key;

    private static string RoleWord(ActorRole role) => role switch { ActorRole.PlayerCharacter => "pc", ActorRole.NonPlayerCharacter => "npc", _ => "monster" };

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        System.Collections.IEnumerable list => !list.Cast<object?>().Any(i => !string.IsNullOrWhiteSpace(i?.ToString())),
        _ => false
    };

    private static double? ToNumber(object? value) => value switch
    {
        null => null,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        IConvertible c when value is not bool => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => null
    };

    private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Display(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        System.Collections.IEnumerable list => string.Join(", ", list.Cast<object?>().Select(i => i?.ToString() ?? string.Empty)),
        IConvertible c => Convert.ToString(c, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string Fill(string message, string name, string label, string value, IssueCheckDefinition check) =>
        Regex.Replace(message, @"\{(name|field|value|min|max)\}", m => m.Groups[1].Value switch
        {
            "name" => Make.Quote(name),
            "field" => label,
            "value" => Make.Quote(value),
            "min" => check.Min is { } lo ? Num(lo) : string.Empty,
            _ => check.Max is { } hi ? Num(hi) : string.Empty
        });

    private static string DefaultHint(IssueCheckDefinition check, string label) => check.Rule switch
    {
        "required" => $"Ask the DM what {label} should be, then propose a sheet edit of it with exactly what they say. Do not invent a value.",
        "empty" => $"Ask the DM whether {label} should be cleared; if so, propose a sheet edit that clears it. Do not decide for them.",
        "range" => $"Ask the DM what {label} should be within the limit, then propose a sheet edit. Do not guess a value.",
        "reference" => $"Ask the DM which entry {label} should name (search_library lists them), then propose a sheet edit. Do not invent an id.",
        _ => $"Ask the DM what {label} should hold, then propose a sheet edit. Do not invent entries."
    };

    private static string Pascal(string id) => string.Concat(id.Split('-', '_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    // ---- layer 3: compiled checks ----

    private IEnumerable<Issue> Compiled(WorldSnapshot s, SystemDefinition system, IssueCheckDefinition check)
    {
        var (issues, failure) = Call(s, system, check);
        if (failure != null)
        {
            var owner = s.Campaign.Id.Length > 0 ? s.Campaign.Id : system.Id;
            yield return Make.Issue(CheckNames.CheckFailed, IssueSeverity.Info, "campaign", owner, "issueChecks", check.Id, "Consistency.CheckFailed", new[] { check.Id, failure },
                $"The system's check \"{Make.Quote(check.Id)}\" could not run ({failure}), so what it looks for was not checked.",
                "Tell the DM; this is a fault in the system's own check, not in the campaign. Nothing can be proposed about it.", FixVia.ReportOnly);
            yield break;
        }

        foreach (var issue in issues!) yield return issue;
    }

    // Not in the iterator: it cannot yield from a catch.
    private (List<Issue>? Issues, string? Failure) Call(WorldSnapshot s, SystemDefinition system, IssueCheckDefinition check)
    {
        if (!system.Compiled.TryGetValue(check.Type!, out var supplier)) return (null, "its type was not looked for");
        if (supplier.Problem != null) return (null, supplier.Problem);
        if (supplier.Instance is not ICodexIssueCheck instance) return (null, "its type is not an ICodexIssueCheck");

        try
        {
            var returned = instance.Check(WorldOf(s, system)) ?? Array.Empty<CodexIssue>();
            var issues = new List<Issue>();
            foreach (var row in returned.Take(MaxCompiledIssues))
            {
                if (Read(row, check) is not { } issue) return (null, "it returned an issue the host cannot read (an unknown kind, severity or an empty id)");
                issues.Add(issue);
            }

            return (issues, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, e.GetType().Name);
        }
    }

    private Issue? Read(CodexIssue row, IssueCheckDefinition check)
    {
        if (row == null || row.Kind is not ("actor" or "page" or "place") || string.IsNullOrWhiteSpace(row.EntityId) || string.IsNullOrWhiteSpace(row.Message)) return null;
        var severity = row.Severity switch { "error" => IssueSeverity.Error, "warning" => IssueSeverity.Warning, "info" => IssueSeverity.Info, _ => (IssueSeverity?)null };
        if (severity == null) return null;
        var detail = string.IsNullOrWhiteSpace(row.Detail) ? check.Id : check.Id + "." + WikiText.Slugify(row.Detail);
        return Make.Issue(Name, severity.Value, row.Kind, row.EntityId.Trim(), string.IsNullOrWhiteSpace(row.Field) ? "-" : row.Field.Trim(), detail,
            string.IsNullOrWhiteSpace(row.Code) ? "Consistency.System." + Pascal(check.Id) : row.Code.Trim(), (row.Args ?? Array.Empty<string>()).Select(a => a ?? string.Empty).ToList(),
            row.Message.Trim(), string.IsNullOrWhiteSpace(row.FixHint) ? "Tell the DM; the system's own check found this." : row.FixHint.Trim(), FixVia.ReportOnly);
    }

    private static CodexIssueWorld WorldOf(WorldSnapshot s, SystemDefinition system) => new(system.Id,
        s.Actors.Select(a => new CodexIssueActor(a.Id, a.Name, RoleWord(a.Role), a.Retired, a.Sheet.ToDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal))).ToList(),
        s.Pages.Select(p => new CodexIssuePage(p.Id, p.Title, p.Kind, p.PartyVisible, (p.Tags ?? Array.Empty<string>()).ToList())).ToList(),
        s.Places.Select(p => new CodexIssuePlace(p.Id, p.RegionId, p.Name, p.ParentId)).ToList());

    // A copy a check may change without changing the snapshot: numbers, text and flags as they are, lists as new lists.
    private static object? Plain(object? value) => value switch
    {
        null => null,
        string or bool => value,
        System.Collections.IDictionary => null,
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
