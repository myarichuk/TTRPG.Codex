using System.Globalization;
using Codex.Core.Rules;
using Codex.Core.WorldBuilder;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Consistency;

/// <summary>
/// A character with nothing in its inventory whose class, background or the like brings starting gear. Info: a gold-only start
/// is legal, and the free text of a starting kit is not parsed, only noticed. Characters of the table only (not bestiary creatures, not
/// retired ones), and only in a system whose sheet has an inventory.
/// </summary>
public sealed class EmptyInventoryCheck : IConsistencyCheck
{
    public string Name => CheckNames.EmptyInventory;

    /// <summary>The entry properties that hold a starting kit, as the bundled systems name them.</summary>
    private static readonly string[] KitProperties = { "startingEquipment", "equipment" };

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (s.System?.SheetRules?.Inventory is not { } inventory) yield break;
        var references = s.System.ActorFields.Where(f => f.Type == FieldType.EntityReference && !string.IsNullOrEmpty(f.TargetEntityType)).ToList();

        foreach (var a in s.Actors.Where(a => a.Role != ActorRole.Monster && !a.Retired))
        {
            if (SheetRuleEngine.RawList(a.Sheet, inventory.Key).Count > 0) continue;
            var sources = new List<string>();
            foreach (var field in references)
            {
                if (YamlValues.GetString(a.Sheet, field.Key) is not { Length: > 0 } id) continue;
                if (s.Library.Resolve(field.TargetEntityType!, id) is not { } entry) continue;
                if (KitProperties.Any(k => entry.Properties.TryGetValue(k, out var kit) && !string.IsNullOrWhiteSpace(Convert.ToString(kit, CultureInfo.InvariantCulture)))) sources.Add(entry.Name);
            }

            if (sources.Count == 0) continue;
            sources.Sort(StringComparer.Ordinal);
            yield return Make.Issue(Name, IssueSeverity.Info, "actor", a.Id, inventory.Key, null, "Consistency.EmptyInventory",
                new[] { a.Name, string.Join(", ", sources) },
                $"\"{Make.Quote(a.Name)}\" carries nothing, though {Make.Quote(string.Join(", ", sources))} bring starting equipment.",
                "Ask the DM whether the character starts with gold instead, or what they carry. Only if the DM wants the starting kit, propose a sheet edit that sets the inventory " +
                "to items the class's or background's own starting-equipment text names and the Library has (get_entry reads it); never invent or auto-fill gear, and leave it empty if the DM says so.",
                FixVia.Sheet);
        }
    }
}

/// <summary>
/// What the system's own sheet rules say about a saved character, as issues: the rules engine already knows the constraints, limits and
/// multiclass rules of every system, so this reuses its verdicts instead of restating them. Its violations are errors and its warnings
/// are warnings. Characters of the table only; a retired one is skipped; nothing is reported where the campaign's enforcement is
/// <c>off</c>. How ability scores were bought is not checked here (the point-buy keys are left out).
/// </summary>
public sealed class SheetRuleCheck : IConsistencyCheck
{
    public string Name => CheckNames.SheetRule;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (s.System?.SheetRules == null || s.Enforcement == "off") yield break;
        var engine = new SheetRuleEngine(s.System, s.Library);
        // The sheets a character's links point to, from the same snapshot, as the sheet page runs the rules with them.
        var linked = new SnapshotLinkedSheets(s.Actors);

        foreach (var a in s.Actors.Where(a => a.Role != ActorRole.Monster && !a.Retired))
        {
            var (evaluation, failure) = Evaluate(engine, a, linked);
            if (evaluation == null)
            {
                yield return Make.Issue(Name, IssueSeverity.Info, "actor", a.Id, "sheet", "evaluation-failed", "Consistency.SheetEvaluationFailed",
                    new[] { a.Name, failure! },
                    $"The sheet rules could not be evaluated for \"{Make.Quote(a.Name)}\" ({failure}).",
                    "Nothing to propose; tell the DM the sheet could not be read.", FixVia.ReportOnly);
                continue;
            }

            foreach (var issue in Issues(s.System, a, evaluation.Violations, IssueSeverity.Error).Concat(Issues(s.System, a, evaluation.Warnings, IssueSeverity.Warning))) yield return issue;
        }
    }

    // One bad sheet must not hide every other character's issues; it becomes an issue of its own. Not in the iterator: it cannot yield from a catch.
    private static (SheetEvaluation? Evaluation, string? Failure) Evaluate(SheetRuleEngine engine, ActorView actor, ILinkedSheets linked)
    {
        try
        {
            // Recompute writes the values it derives onto the sheet it is given: a copy, so the snapshot stays as it was taken.
            return (engine.Recompute(new Dictionary<string, object>(actor.Sheet), linked), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, e.GetType().Name);
        }
    }

    private IEnumerable<Issue> Issues(SystemDefinition? system, ActorView actor, IEnumerable<RuleViolation> violations, IssueSeverity severity)
    {
        foreach (var v in violations)
        {
            if (v.Key != null && v.Key.StartsWith("Actor.Rule.PointBuy", StringComparison.Ordinal)) continue;
            var args = (v.Args ?? Array.Empty<object?>()).Select(ArgText).ToList();
            var detail = (v.Key ?? "rule") + (args.Count > 0 ? ":" + Slug(string.Join(' ', args)) : v.Key == null ? ":" + Slug(v.Message) : string.Empty);
            // Fixable through a sheet edit only when the rule is about a key an edit may set; a derived value is the sheet's own to compute.
            var fixable = SheetProposal.IsEditable(system, v.FieldKey);
            yield return Make.Issue(Name, severity, "actor", actor.Id, v.FieldKey, detail, v.Key ?? "Consistency.SheetRule", args,
                v.Message,
                fixable
                    ? "Ask the DM which value they want, then propose a sheet edit of this key (existing:sheet:<actor id>); do not guess a value or invent an entry. Anything that stays broken is reported again."
                    : "This is a value the sheet works out itself, which no proposal can set. Tell the DM which rule the sheet breaks; do not guess.",
                fixable ? FixVia.Sheet : FixVia.ReportOnly);
        }
    }

    private static string ArgText(object? arg) => arg switch
    {
        null => string.Empty,
        ContentEntry e => e.Name,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => arg.ToString() ?? string.Empty
    };

    // A short readable stand-in for a sentence in an id: the slug of its words, cut so an id stays one line.
    private static string Slug(string text)
    {
        var slug = WikiText.Slugify(text);
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}

/// <summary>The sheets a character's links point to, read from the snapshot's own actors (plain data; the checker never reaches a store). Same
/// rule as the sheet page's: a retired actor's sheet is never handed out, so the link is then "deleted", and an id the campaign does not have is "not found".</summary>
internal sealed class SnapshotLinkedSheets : ILinkedSheets
{
    private readonly Dictionary<string, ActorView> actors;

    public SnapshotLinkedSheets(IEnumerable<ActorView> all) => actors = all.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object>? Sheet(string actorId) => actors.GetValueOrDefault(actorId) is { Retired: false } a ? a.Sheet : null;

    public LinkedActorStatus Status(string actorId) =>
        !actors.TryGetValue(actorId, out var a) ? LinkedActorStatus.NotFound : a.Retired ? LinkedActorStatus.Deleted : LinkedActorStatus.Available;
}
