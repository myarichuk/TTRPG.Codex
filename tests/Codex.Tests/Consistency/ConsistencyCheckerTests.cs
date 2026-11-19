using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>The whole checker: determinism, the list the model reads, dismissals, and what a change did.</summary>
public class ConsistencyCheckerTests
{
    private static readonly Fx.Entries Library = new(Fx.Entry("class", "fighter", "Fighter", ("startingEquipment", "Chain Mail")));

    /// <summary>A world with something wrong in every check, so a test over it covers all of them.</summary>
    private static (List<PageView> Pages, List<ActorView> Actors, List<PlaceView> Places, List<FactView> Facts) World() => (
        new List<PageView>
        {
            Fx.Page("wikipages/1-A", "The Mere of Dead Men", "A black water."),
            Fx.Page("wikipages/2-A", "Road North", "We cross [[the the Mere of Dead Men]]. Iron Keep waits. [[actor:actors/gone]]\n:::dm\nopen", party: true),
            Fx.Page("wikipages/3-A", "Iron Keep", "", party: false),
            Fx.Page("wikipages/4-A", "Hall", "See [[Iron Keep]]. TODO\n:::for [[actor:actors/gone-too]]\nx\n:::", party: true, actorId: "actors/gone"),
            Fx.Page("wikipages/5-A", "Notes", "Visit the Mere of Dead Men.\n:::partyy\nx\n:::")
        },
        new List<ActorView>
        {
            Fx.Actor("actors/1-A", "Aria", sheet: new Dictionary<string, object> { ["class"] = "ghost" }),
            Fx.Actor("actors/2-A", "Old Tam", ActorRole.NonPlayerCharacter, bio: ""),
            Fx.Actor("actors/3-A", "Bram", sheet: new Dictionary<string, object> { ["class"] = "fighter" })
        },
        new List<PlaceView> { new("p-1", "regions/1", "Docks", "TBD", "wikipages/gone") },
        new List<FactView> { new("facts/1-A", "Seen near [[Lost Light]].", null) });

    private static WorldSnapshot Snapshot(bool reversed = false)
    {
        var (pages, actors, places, facts) = World();
        if (reversed) { pages.Reverse(); actors.Reverse(); places.Reverse(); facts.Reverse(); }
        return Fx.Snap(pages, actors, places, facts, Fx.System(), Library);
    }

    [Fact]
    public void TheWorldUnderTest_TripsEveryCheck()
    {
        var found = ConsistencyChecker.Run(Snapshot()).Select(i => i.Check).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        // The v1 world trips every v1 check; the v2 and noisy checks have worlds of their own (WorldStructureCheckTests).
        Assert.Equal(CheckNames.All.Except(CheckNames.V2).Except(CheckNames.Noise).OrderBy(c => c, StringComparer.Ordinal), found);
    }

    [Fact]
    public void RunningTwiceOverTheSameData_GivesTheSameIssues_InTheSameOrder()
    {
        var snapshot = Snapshot();
        Assert.Equal(ConsistencyChecker.Run(snapshot).Select(Fx.Flat), ConsistencyChecker.Run(snapshot).Select(Fx.Flat));
    }

    [Fact]
    public void EachCheck_OnItsOwn_GivesTheSameIssuesOnRepeatedRuns()
    {
        var snapshot = Snapshot();
        foreach (var check in ConsistencyChecker.Default)
        {
            Assert.Equal(check.Run(snapshot).Select(Fx.Flat), check.Run(snapshot).Select(Fx.Flat));
        }
    }

    [Fact]
    public void TheOrderTheDatabaseHandedTheDocumentsOutIn_ChangesNothing()
    {
        Assert.Equal(ConsistencyChecker.Run(Snapshot()).Select(Fx.Flat), ConsistencyChecker.Run(Snapshot(reversed: true)).Select(Fx.Flat));
        // Not even the order a check's own list of issues comes in: the checks are run in a different order too.
        var shuffled = ConsistencyChecker.Default.Reverse().ToList();
        Assert.Equal(ConsistencyChecker.Run(Snapshot()).Select(Fx.Flat), ConsistencyChecker.Run(Snapshot(), shuffled).Select(Fx.Flat));
    }

    [Fact]
    public void TheList_IsSortedMostSevereFirst_ThenByCheckEntityFieldAndId_AllOrdinal()
    {
        var issues = ConsistencyChecker.Run(Snapshot());
        Assert.Equal(issues.OrderBy(i => i, Issue.Order).Select(i => i.Id), issues.Select(i => i.Id));
        Assert.Equal(IssueSeverity.Error, issues[0].Severity);
        Assert.Equal(IssueSeverity.Info, issues[^1].Severity);
        Assert.Equal(issues.Count, issues.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoIssueMentionsATimeOrARandomValue_SoTheListCanSitInAPrompt()
    {
        var text = string.Join("\n", ConsistencyChecker.Run(Snapshot()).Select(Fx.Flat));
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}T", text);
        Assert.DoesNotMatch(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}", text);
    }

    [Fact]
    public void TwoIssuesThatWouldShareAnId_GetNumbered_SoAnIdAlwaysNamesOne()
    {
        var twin = new FixedCheck(
            Make("a:x", IssueSeverity.Warning), Make("a:x", IssueSeverity.Warning), Make("a:x", IssueSeverity.Warning));
        var ids = ConsistencyChecker.Run(Fx.Snap(), new[] { twin }).Select(i => i.Id).ToList();
        Assert.Equal(new[] { "a:x", "a:x~2", "a:x~3" }, ids);
    }

    // ---- the list the model asks for ----

    [Fact]
    public void ScopeNamesAnEntity_OrACheck_AndSeverityIsAMinimum()
    {
        var all = ConsistencyChecker.Run(Snapshot());

        var page = new IssueFilter("page:wikipages/2-A").Apply(all);
        Assert.NotEmpty(page);
        Assert.All(page, i => Assert.Equal(("page", "wikipages/2-A"), (i.EntityKind, i.EntityId)));

        var links = new IssueFilter(CheckNames.BrokenLink).Apply(all);
        Assert.NotEmpty(links);
        Assert.All(links, i => Assert.Equal(CheckNames.BrokenLink, i.Check));

        var worse = new IssueFilter(null, IssueSeverity.Warning).Apply(all);
        Assert.DoesNotContain(worse, i => i.Severity == IssueSeverity.Info);
        Assert.Contains(worse, i => i.Severity == IssueSeverity.Warning);
        Assert.Equal(all.Count(i => i.Severity <= IssueSeverity.Warning), worse.Count);

        Assert.Equal(all.Select(i => i.Id), new IssueFilter("all").Apply(all).Select(i => i.Id));
    }

    [Fact]
    public void AScopeThatMeansNothing_IsKnownToBeUnknown_SoItIsNotMistakenForAllClear()
    {
        Assert.False(IssueFilter.IsKnownScope("bogus"));
        Assert.False(IssueFilter.IsKnownScope("planet:x"));
        Assert.False(IssueFilter.IsKnownScope("page:"));
        Assert.True(IssueFilter.IsKnownScope(null));
        Assert.True(IssueFilter.IsKnownScope("all"));
        Assert.True(IssueFilter.IsKnownScope("empty-bio"));
        Assert.True(IssueFilter.IsKnownScope("actor:actors/1-A"));
        Assert.Empty(new IssueFilter("bogus").Apply(ConsistencyChecker.Run(Snapshot())));
    }

    // ---- dismissals ----

    [Fact]
    public void DismissedIssues_AreSetAside_NotDeleted_AndOnlyByTheirIds()
    {
        var all = ConsistencyChecker.Run(Snapshot());
        var set = new HashSet<string>(StringComparer.Ordinal) { all[0].Id, "broken-link:page:nothing:body:x" };
        var (open, dismissed) = IssueDismissals.Split(all, set);

        Assert.Equal(new[] { all[0].Id }, dismissed.Select(i => i.Id));
        Assert.Equal(all.Count - 1, open.Count);
        Assert.Equal(all.Skip(1).Select(i => i.Id), open.Select(i => i.Id));
    }

    // ---- fixes: what the model claims, and what the code finds ----

    [Fact]
    public void AProposalThatNamesAnIssueThatDoesNotExist_IsFoundOutBeforeItIsApplied()
    {
        var open = ConsistencyChecker.Run(Snapshot());
        var real = open.First(i => i.Check == CheckNames.EmptyBody).Id;

        Assert.Empty(ConsistencyChecker.UnknownIds(new[] { real }, open));
        Assert.Equal(new[] { "broken-link:page:wikipages/2-A:body:invented", "empty-bio:actor:actors/404:biography" },
            ConsistencyChecker.UnknownIds(new[] { real, "empty-bio:actor:actors/404:biography", "broken-link:page:wikipages/2-A:body:invented", "empty-bio:actor:actors/404:biography" }, open));
    }

    [Fact]
    public void AFixIsCheckedAgainstTheCode_NotTheModelsWord()
    {
        var before = ConsistencyChecker.Run(Snapshot());
        var fixedLink = before.Single(i => i.Id == "broken-link:page:wikipages/2-A:body:the-the-mere-of-dead-men").Id;
        var claimedButNot = before.First(i => i.Check == CheckNames.EmptyBio).Id;

        // The page is edited: the link now reaches the page, but the model also broke a link elsewhere, and did nothing about the bio.
        var (pages, actors, places, facts) = World();
        pages[1] = pages[1] with { Body = "We cross [[The Mere of Dead Men]]. [[Vanished Page]]" };
        var after = ConsistencyChecker.Run(Fx.Snap(pages, actors, places, facts, Fx.System(), Library));

        var recheck = ConsistencyChecker.Compare(before, after, new[] { fixedLink, claimedButNot, "empty-bio:actor:actors/404:biography" });

        Assert.Equal(new[] { fixedLink }, recheck.Resolved);
        Assert.Equal(new[] { claimedButNot }, recheck.StillOpen);
        Assert.Equal(new[] { "empty-bio:actor:actors/404:biography" }, recheck.NotFound);
        Assert.Contains(recheck.New, i => i.Id == "broken-link:page:wikipages/2-A:body:vanished-page");
        Assert.DoesNotContain(fixedLink, recheck.ResolvedUnclaimed);
        // Fixing the link also removed what the same edit took out of the page: reported as a side effect, never as a claim.
        Assert.Contains("dangling-mention:page:wikipages/2-A:body:actor:actors/gone", recheck.ResolvedUnclaimed);
    }

    // ---- helpers ----

    private static Issue Make(string id, IssueSeverity severity) =>
        new(id, "test", severity, "page", "p", "body", "T", Array.Empty<string>(), "m", "h", "page:p", FixVia.Page);

    private sealed class FixedCheck(params Issue[] issues) : IConsistencyCheck
    {
        public string Name => "test";
        public IEnumerable<Issue> Run(WorldSnapshot snapshot) => issues;
    }
}
