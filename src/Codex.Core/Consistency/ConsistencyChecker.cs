namespace Codex.Core.Consistency;

/// <summary>
/// Runs the checks over a snapshot. Pure code: the same snapshot gives the same issues in the same order, run after run, and no model is
/// asked anything. The model is handed the result and proposes fixes; whether something is broken is decided here.
/// </summary>
public static class ConsistencyChecker
{
    /// <summary>The checks run by default, in a fixed order. A system's own checks join here later (docs: the layered design).</summary>
    public static IReadOnlyList<IConsistencyCheck> Default { get; } = new IConsistencyCheck[]
    {
        new BrokenLinkCheck(),
        new UnlinkedMentionCheck(),
        new VisibilityLeakCheck(),
        new DanglingMentionCheck(),
        new DanglingReferenceCheck(),
        new EmptyBioCheck(),
        new EmptyBodyCheck(),
        new TodoMarkerCheck(),
        new EmptyInventoryCheck(),
        new SecretUnclosedCheck(),
        new SecretUnknownAudienceCheck(),
        new SecretForMissingCheck(),
        new SheetRuleCheck(),
        // v2: the structure of the world, the campaign and the Library it points at.
        new PlaceTreeCheck(),
        new DuplicateTitleCheck(),
        new FactReferenceCheck(),
        new PlacementCheck(),
        new OwnerMembershipCheck(),
        new HomebrewChecks(),
        new HazardShapeCheck(),
        new TagCaseCheck(),
        new CampaignHealthCheck(),
        new LibraryReferenceCheck(),
        // The checks the campaign's game system adds (system.yaml issueChecks): data rules and compiled checks, each guarded.
        new SystemRuleCheck()
    };

    /// <summary>The noisy checks: true but rarely worth the DM's time. Never part of <see cref="Default"/>; a caller that asks for them
    /// (<see cref="IssueFilter.WantsNoise"/>) runs <see cref="Default"/> and these together.</summary>
    public static IReadOnlyList<IConsistencyCheck> Noisy { get; } = new IConsistencyCheck[]
    {
        new OrphanPageCheck(),
        new BacklinkMismatchCheck(),
        new PersonPageNoActorCheck(),
        new RetiredMentionCheck()
    };

    /// <summary>The checks a filter lists from: <see cref="Default"/>, and the noisy ones only when the filter asks for them.</summary>
    public static IReadOnlyList<IConsistencyCheck> For(IssueFilter filter) => filter.WantsNoise ? Default.Concat(Noisy).ToList() : Default;

    /// <summary>Every issue the checks find, in <see cref="Issue.Order"/>, each id once. Two issues that would share an id (the same rule
    /// broken twice with the same words) get <c>~2</c>, <c>~3</c> in that order, so an id always names one issue (the order of two such issues is by what they say, `Issue.Order`; a check that can say which item an issue is about puts that in the id itself, so this stays the exception). The checks stay pure and know
    /// nothing of time; <paramref name="cancel"/> is looked at between them, between the issues each yields, and inside the long ones'
    /// loops (<see cref="IConsistencyCheck.Run(WorldSnapshot, CancellationToken)"/>), so a caller with a time budget can give up on a run (an
    /// <see cref="OperationCanceledException"/>, never a partial list).</summary>
    public static IReadOnlyList<Issue> Run(WorldSnapshot snapshot, IEnumerable<IConsistencyCheck>? checks = null, CancellationToken cancel = default)
    {
        var found = new List<Issue>();
        foreach (var check in checks ?? Default)
        {
            cancel.ThrowIfCancellationRequested();
            // Looked at again with every issue a check yields, and handed to the check for the loops of the long ones: a run over its budget stops
            // inside the check that is running, and throws; a cancelled run returns nothing, never a part of the list.
            foreach (var issue in check.Run(snapshot, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                found.Add(issue);
            }
        }

        var all = found.OrderBy(i => i, Issue.Order).ToList();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var suffixed = false;
        for (var i = 0; i < all.Count; i++)
        {
            var id = all[i].Id;
            if (!seen.TryGetValue(id, out var count))
            {
                seen[id] = 1;
                continue;
            }

            seen[id] = ++count;
            all[i] = all[i] with { Id = $"{id}~{count}", FormerId = null };
            suffixed = true;
        }

        // A former id (the one an issue had under an earlier id scheme, for matching old dismissals) is kept only where it names this issue alone: shared by
        // two issues, or equal to another issue's id, a dismissal stored under it would set aside more than the one problem it was made for.
        if (all.Any(i => i.FormerId != null))
        {
            var current = all.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var shared = all.Where(i => i.FormerId != null).GroupBy(i => i.FormerId!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].FormerId is { } former && (shared.Contains(former) || current.Contains(former))) all[i] = all[i] with { FormerId = null };
            }
        }

        // A suffix can move an issue within its group; sort once more so the order is the documented one. With no suffix nothing moved: the list
        // is in that order already (the sort is stable, so sorting it again would give the same list).
        return suffixed ? all.OrderBy(i => i, Issue.Order).ToList() : all;
    }

    /// <summary>The issues <paramref name="filter"/> asks for.</summary>
    public static IReadOnlyList<Issue> Run(WorldSnapshot snapshot, IssueFilter filter, IEnumerable<IConsistencyCheck>? checks = null, CancellationToken cancel = default) =>
        filter.Apply(Run(snapshot, checks, cancel));

    /// <summary>
    /// The ids a proposal says it resolves that are not issues right now, sorted and each once. A proposal naming any is refused whole:
    /// an id the model made up, or one that was fixed or dismissed since, is not something it can claim to resolve. Empty: all of them are
    /// open issues.
    /// </summary>
    public static IReadOnlyList<string> UnknownIds(IEnumerable<string> claimed, IEnumerable<Issue> open)
    {
        var known = open.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return claimed.Where(id => !known.Contains(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What a change did, from the issues before it and after it, as the code sees them: the ids that are gone, the ids that are still
    /// there, and the ones that are new. A model's claim to have fixed something is checked against this, never taken as the answer.
    /// <paramref name="claimed"/> are the ids a proposal said it resolves.
    /// </summary>
    public static Recheck Compare(IEnumerable<Issue> before, IEnumerable<Issue> after, IEnumerable<string> claimed) =>
        Compare(before.Select(i => i.Id), after, claimed);

    /// <summary>
    /// <see cref="Compare(IEnumerable{Issue}, IEnumerable{Issue}, IEnumerable{string})"/> with the ids that were open before as ids: an apply that is
    /// retried after part of it was saved passes the ids from before the first attempt as well, so a claim of an entry already saved is still seen as an
    /// issue that was there (and is now gone), not as one that was never an issue.
    /// <para>The claims are judged against the ids open when the apply started, never taken as given: a claim that was not open then (fixed, or
    /// never an issue) cannot have been fixed by the apply, so it is never <see cref="Recheck.Resolved"/> or <see cref="Recheck.StillOpen"/>; if the apply
    /// brought it back it is <see cref="Recheck.New"/>, otherwise <see cref="Recheck.NotFound"/>. <paramref name="dismissed"/> are the ids the DM has set
    /// aside (before or after the apply): one of those that is not open after the apply was hidden, not fixed, and is
    /// <see cref="Recheck.Dismissed"/>, never resolved or "not an issue".</para>
    /// </summary>
    public static Recheck Compare(IEnumerable<string> beforeIssueIds, IEnumerable<Issue> after, IEnumerable<string> claimed, IEnumerable<string>? dismissed = null)
    {
        var beforeIds = beforeIssueIds.ToHashSet(StringComparer.Ordinal);
        var afterById = after.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var hidden = (dismissed ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var claims = claimed.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        bool Open(string id) => afterById.ContainsKey(id);
        bool Hidden(string id) => !Open(id) && hidden.Contains(id);

        return new Recheck(
            Resolved: claims.Where(id => beforeIds.Contains(id) && !Open(id) && !Hidden(id)).ToList(),
            StillOpen: claims.Where(id => beforeIds.Contains(id) && Open(id)).ToList(),
            NotFound: claims.Where(id => !beforeIds.Contains(id) && !Open(id) && !Hidden(id)).ToList(),
            ResolvedUnclaimed: beforeIds.Where(id => !Open(id) && !Hidden(id) && !claims.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            New: afterById.Values.Where(i => !beforeIds.Contains(i.Id)).OrderBy(i => i, Issue.Order).ToList(),
            Dismissed: claims.Concat(beforeIds).Where(Hidden).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }
}

/// <summary>The outcome of <see cref="ConsistencyChecker.Compare"/>.</summary>
/// <param name="Resolved">Claimed, was an issue when the apply started, is gone now (and not merely dismissed).</param>
/// <param name="StillOpen">Claimed, was an issue when the apply started, and still is: the change did not fix it.</param>
/// <param name="NotFound">Claimed, but not an issue when the apply started and not one after: a claim gone stale (fixed meanwhile) or one that never
/// named an issue. The apply cannot have fixed it.</param>
/// <param name="ResolvedUnclaimed">Gone now, though nothing claimed it: the change fixed it as a side effect.</param>
/// <param name="New">Not an issue when the apply started, one now: the change broke something (or brought back what a stale claim named).</param>
/// <param name="Dismissed">Claimed or open before, not open now because the DM set it aside: hidden, not fixed. Never counted as resolved.</param>
public sealed record Recheck(IReadOnlyList<string> Resolved, IReadOnlyList<string> StillOpen, IReadOnlyList<string> NotFound,
    IReadOnlyList<string> ResolvedUnclaimed, IReadOnlyList<Issue> New, IReadOnlyList<string>? Dismissed = null)
{
    /// <summary>See the parameter; never null.</summary>
    public IReadOnlyList<string> Dismissed { get; init; } = Dismissed ?? Array.Empty<string>();
}
