namespace Codex.Core.Consistency;

/// <summary>How serious an issue is. The numeric order is the sort order: most severe first.</summary>
public enum IssueSeverity
{
    /// <summary>Something that is certainly broken: a reference to a thing that does not exist, a rule the sheet breaks.</summary>
    Error = 0,

    /// <summary>Probably a mistake, but the DM may have meant it.</summary>
    Warning = 1,

    /// <summary>Worth knowing, never urgent: a field left empty.</summary>
    Info = 2
}

/// <summary>Which kind of change, through the review gate, can fix an issue. <see cref="ReportOnly"/>: none can (the gate cannot
/// change a page's structural links, or a sheet value no edit may set), so the model reports it and the DM fixes it by hand.</summary>
public enum FixVia
{
    ReportOnly,
    Page,
    Place,
    Fact,
    ActorBiography,
    /// <summary>An edit of a character's sheet (<c>existing:sheet:&lt;actorId&gt;</c>): setting or clearing a key the system declares.</summary>
    Sheet,
    /// <summary>An edit of a homebrew entry (<c>existing:homebrew:&lt;type&gt;/&lt;id&gt;</c>): its fields, text or tags.</summary>
    Homebrew
}

/// <summary>
/// One thing the checker found. Produced by code, never by a model: the model is told what is broken and proposes fixes.
/// </summary>
/// <param name="Id">Stable across runs while the problem stays the same: <c>check:kind:entityId:field[:detail]</c>, built from document ids,
/// never from a slug, a title or a position in a list that an edit would move. A fix proposal names the ids it resolves.</param>
/// <param name="Check">The check that found it (<see cref="CheckNames"/>).</param>
/// <param name="Severity">See <see cref="IssueSeverity"/>.</param>
/// <param name="EntityKind">What the issue is about: <c>page</c>, <c>actor</c>, <c>place</c> or <c>fact</c>.</param>
/// <param name="EntityId">The document id of that entity (a place's is its own id inside its region).</param>
/// <param name="Field">The field of the entity, such as <c>body</c> or <c>biography</c>; <c>-</c> when it is about the entity as a whole.</param>
/// <param name="Code">A stable key for the sort of problem, so the app can show it in the reader's language: <c>Consistency.BrokenLink</c>,
/// or a rule engine key such as <c>Actor.Rule.Limit</c>.</param>
/// <param name="Args">What fills the blanks of the sentence for <paramref name="Code"/>, as text, in order.</param>
/// <param name="Message">The sentence in English, for the model, logs and tests.</param>
/// <param name="FixHint">What to do about it, in English. For the model: advice, not an instruction to invent content.</param>
/// <param name="Bundle">What issues are grouped under on a screen or in a list: the entity (<c>page:wikipages/1-A</c>).</param>
/// <param name="FixVia">See <see cref="FixVia"/>.</param>
public sealed record Issue(
    string Id,
    string Check,
    IssueSeverity Severity,
    string EntityKind,
    string EntityId,
    string Field,
    string Code,
    IReadOnlyList<string> Args,
    string Message,
    string FixHint,
    string Bundle,
    FixVia FixVia)
{
    /// <summary>The sort key every list of issues uses: most severe first, then by check, entity, field and id, all ordinal, so two runs
    /// over the same data list the same issues in the same order whatever order the database handed the documents out in.</summary>
    public static readonly IComparer<Issue> Order = Comparer<Issue>.Create(static (a, b) =>
    {
        var c = a.Severity.CompareTo(b.Severity);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Check, b.Check);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.EntityKind, b.EntityKind);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.EntityId, b.EntityId);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Field, b.Field);
        c = string.CompareOrdinal(a.Id, b.Id);
        if (c != 0) return c;
        // Two issues that would share an id are told apart by what they say, never by the order a check happened to produce them in.
        c = string.CompareOrdinal(a.Message, b.Message);
        return c != 0 ? c : string.CompareOrdinal(a.FixHint, b.FixHint);
    });
}

/// <summary>The names of the checks, as an issue's <see cref="Issue.Check"/> and a scope name them.</summary>
public static class CheckNames
{
    public const string BrokenLink = "broken-link";
    public const string UnlinkedMention = "unlinked-mention";
    public const string DanglingMention = "dangling-mention";
    public const string DanglingReference = "dangling-reference";
    public const string EmptyBio = "empty-bio";
    public const string EmptyBody = "empty-body";
    public const string TodoMarker = "todo-marker";
    public const string EmptyInventory = "empty-inventory";
    public const string SecretUnclosed = "secret-unclosed";
    public const string SecretUnknownAudience = "secret-unknown-audience";
    public const string SecretForMissing = "secret-for-missing";
    public const string VisibilityLeak = "visibility-leak";
    public const string SheetRule = "sheet-rule";

    // v2: the structure of the world.
    public const string PlaceTree = "place-tree";
    public const string DuplicateSlug = "duplicate-slug";
    public const string NearDuplicateTitle = "near-duplicate-title";
    public const string FactReference = "fact-reference";
    public const string DanglingPlacement = "dangling-placement";
    public const string OwnerNotMember = "owner-not-member";
    public const string HomebrewReference = "homebrew-reference";
    public const string HomebrewDuplicate = "homebrew-duplicate";
    public const string HazardShape = "hazard-shape";
    public const string TagCase = "tag-case";
    public const string CampaignSystem = "system-unknown";
    public const string SystemDrift = "system-drift";
    public const string PackDrift = "pack-drift";
    public const string PackProblem = "pack-problem";
    public const string LibraryReference = "library-reference";

    // The checks a game system adds (system.yaml issueChecks), and the one issue that says a compiled one could not run.
    public const string SystemRule = "system-rule";
    public const string CheckFailed = "check-failed";

    // Noise: true but rarely worth the DM's time, so off unless asked for (scope "noise" or the check's name).
    public const string OrphanPage = "orphan-page";
    public const string BacklinkMismatch = "backlink-mismatch";
    public const string PersonPageNoActor = "person-page-no-actor";
    public const string RetiredMention = "retired-mention";

    /// <summary>The checks added in v2 (the structure of the world, the campaign and the Library), apart from the noisy ones.</summary>
    public static readonly IReadOnlyList<string> V2 = new[]
    {
        PlaceTree, DuplicateSlug, NearDuplicateTitle, FactReference, DanglingPlacement, OwnerNotMember, HomebrewReference, HomebrewDuplicate, HazardShape,
        TagCase, CampaignSystem, SystemDrift, PackDrift, PackProblem, LibraryReference, SystemRule, CheckFailed
    };

    /// <summary>The checks that are off unless asked for, in the order they run.</summary>
    public static readonly IReadOnlyList<string> Noise = new[] { OrphanPage, BacklinkMismatch, PersonPageNoActor, RetiredMention };

    /// <summary>The scope word that lists every noise check.</summary>
    public const string NoiseScope = "noise";

    public static readonly IReadOnlyList<string> All = new[]
    {
        BrokenLink, UnlinkedMention, DanglingMention, DanglingReference, EmptyBio, EmptyBody, TodoMarker, EmptyInventory,
        SecretUnclosed, SecretUnknownAudience, SecretForMissing, VisibilityLeak, SheetRule,
        PlaceTree, DuplicateSlug, NearDuplicateTitle, FactReference, DanglingPlacement, OwnerNotMember, HomebrewReference, HomebrewDuplicate, HazardShape,
        TagCase, CampaignSystem, SystemDrift, PackDrift, PackProblem, LibraryReference, SystemRule, CheckFailed,
        OrphanPage, BacklinkMismatch, PersonPageNoActor, RetiredMention
    };
}

/// <summary>
/// What a caller asks of the list (the model's <c>list_issues(scope?, severity?)</c>). <see cref="Scope"/> is <c>all</c> (or none), an
/// entity (<c>page:&lt;id&gt;</c>, <c>actor:&lt;id&gt;</c>, <c>place:&lt;id&gt;</c>, <c>fact:&lt;id&gt;</c>), or the name of a check. A scope that
/// is none of these matches nothing: <see cref="IsKnownScope"/> says so, so the caller can tell the model rather than return an empty list
/// that looks like "all clear".
/// </summary>
/// <param name="Scope">See above.</param>
/// <param name="MinSeverity">The least severe level to include: <see cref="IssueSeverity.Warning"/> gives errors and warnings.</param>
public sealed record IssueFilter(string? Scope = null, IssueSeverity? MinSeverity = null)
{
    private static readonly string[] EntityKinds = { "page", "actor", "place", "fact", "homebrew", "campaign" };

    public static bool IsKnownScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        scope = scope.Trim();
        if (scope == CheckNames.NoiseScope) return true;
        if (CheckNames.All.Contains(scope, StringComparer.Ordinal)) return true;
        var colon = scope.IndexOf(':');
        return colon > 0 && colon < scope.Length - 1 && EntityKinds.Contains(scope[..colon], StringComparer.Ordinal);
    }

    public bool Matches(Issue issue)
    {
        if (MinSeverity is { } min && issue.Severity > min) return false;
        if (string.IsNullOrWhiteSpace(Scope) || Scope.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        var scope = Scope.Trim();
        if (scope == CheckNames.NoiseScope) return CheckNames.Noise.Contains(issue.Check, StringComparer.Ordinal);
        if (CheckNames.All.Contains(scope, StringComparer.Ordinal)) return issue.Check == scope;
        var colon = scope.IndexOf(':');
        if (colon <= 0 || !EntityKinds.Contains(scope[..colon], StringComparer.Ordinal)) return false;
        return issue.EntityKind == scope[..colon] && issue.EntityId == scope[(colon + 1)..];
    }

    /// <summary>True when this filter asks for noise: the scope <c>noise</c>, or one noise check by name. Nothing else ever lists a noise issue,
    /// so a caller runs the noise checks only for such a filter.</summary>
    public bool WantsNoise => Scope?.Trim() is { } scope && (scope == CheckNames.NoiseScope || CheckNames.Noise.Contains(scope, StringComparer.Ordinal));

    /// <summary>The issues that match, in <see cref="Issue.Order"/>.</summary>
    public IReadOnlyList<Issue> Apply(IEnumerable<Issue> issues) => issues.Where(Matches).OrderBy(i => i, Issue.Order).ToList();
}

/// <summary>Issues the DM has set aside on purpose (a red link that is meant to stay red): a persisted list of issue ids, applied after
/// the checks ran. Only the DM sets one aside; nothing a model says changes it.</summary>
public static class IssueDismissals
{
    /// <summary>The issues split by whether the DM dismissed them, each in the order given.</summary>
    public static (IReadOnlyList<Issue> Open, IReadOnlyList<Issue> Dismissed) Split(IEnumerable<Issue> issues, IReadOnlySet<string> dismissedIds)
    {
        var open = new List<Issue>();
        var dismissed = new List<Issue>();
        foreach (var issue in issues) (dismissedIds.Contains(issue.Id) ? dismissed : open).Add(issue);
        return (open, dismissed);
    }
}
