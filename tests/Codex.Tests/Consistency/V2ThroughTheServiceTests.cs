using System.Text.Json;
using Codex.Core.Consistency;
using Codex.Persistence;

namespace Codex.Tests.Consistency;

/// <summary>The v2 checks through the service the chat reads: the noisy ones stay out unless asked for, and homebrew reaches the checks.</summary>
public class V2ThroughTheServiceTests
{
    private static WikiPageDocument Page(string id, string title, string body) =>
        new() { Id = id, Slug = Codex.Core.WikiText.Slugify(title), Title = title, Body = body, Visibility = WikiVisibility.Party };

    private static string[] Ids(string listing) => JsonDocument.Parse(listing).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();

    private static Task<string> ListAsync(WorldBuilderTests.Saving saving, string? scope = null) =>
        saving.Service.IssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, scope, null);

    private static WorldBuilderTests.Saving World() => WorldBuilderTests.ServiceWithPages(new[]
    {
        Page("wikipages/1-A", "Road", "Leads to [[Linked]]."), Page("wikipages/2-A", "Linked", "A place."), Page("wikipages/3-A", "Lonely", "Nobody links here.")
    });

    [Fact]
    public async Task TheNoisyChecks_AreNotInTheDefaultList_NorInTheCount_ButAreListedForTheNoiseScope()
    {
        var saving = World();

        Assert.StartsWith("[]", await ListAsync(saving));
        Assert.StartsWith("[]", await ListAsync(saving, "all"));
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        var noise = Ids(await ListAsync(saving, "noise"));
        Assert.Equal(new[] { "orphan-page:page:wikipages/1-A:links", "orphan-page:page:wikipages/3-A:links" }, noise);
        Assert.Equal(noise, Ids(await ListAsync(saving, "orphan-page")));

        // Asking for noise changes nothing about the open issues afterwards.
        Assert.StartsWith("[]", await ListAsync(saving));
        Assert.Empty((await saving.Service.OpenIssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm))!);
    }

    [Fact]
    public async Task TheHomebrewOfTheCampaign_ReachesTheChecks()
    {
        var saving = World();
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/1", Type = "spell", EntryId = "dup", Name = "One", SystemId = "srd5e" });
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/2", Type = "spell", EntryId = "dup", Name = "Two", SystemId = "srd5e" });

        var ids = Ids(await ListAsync(saving, "homebrew-duplicate"));

        Assert.Equal(new[] { "homebrew-duplicate:homebrew:spell/dup:entryId:hb-1", "homebrew-duplicate:homebrew:spell/dup:entryId:hb-2" }, ids);
        Assert.Equal(2, (await saving.Service.OpenIssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm))!.Count);
        Assert.Equal(ids, Ids(await ListAsync(saving, "homebrew:spell/dup")));
    }

    [Fact]
    public async Task TheCountNote_StillCostsOneSnapshot_AndTheV2ChecksAreInIt()
    {
        var saving = World();
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/1", Type = "spell", EntryId = "dup", Name = "One", SystemId = "srd5e" });
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/2", Type = "spell", EntryId = "dup", Name = "Two", SystemId = "srd5e" });
        var before = saving.Snapshots;

        var note = await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

        Assert.Equal(1, saving.Snapshots - before);
        Assert.Contains("2 open issues (2 errors, 0 warnings, 0 info)", note);
    }

    [Fact]
    public async Task TheCountNote_IsLeftOutSilently_WhenTheCheckTakesLongerThanItsBudget_AndNeverHoldsUpTheMessage()
    {
        var saving = World();
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/1", Type = "spell", EntryId = "dup", Name = "One", SystemId = "srd5e" });
        saving.Entries.Add(new HomebrewEntryDocument { Id = "hb/2", Type = "spell", EntryId = "dup", Name = "Two", SystemId = "srd5e" });

        saving.Service.IssueSummaryBudget = TimeSpan.Zero; // already spent when the check starts
        saving.Service.IssueSummaryBackoff = TimeSpan.Zero; // tried again at once (the skip after a timeout has tests of its own)
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        saving.Service.IssueSummaryBudget = TimeSpan.FromSeconds(30);
        Assert.Contains("2 open issues", await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
    }

    [Fact]
    public void TheChecker_LooksAtTheTokenBetweenChecks_AndStopsWithACancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConsistencyChecker.Run(Fx.Snap(), cancel: cancelled.Token));
        Assert.Empty(ConsistencyChecker.Run(Fx.Snap(), cancel: CancellationToken.None));
    }

    /// <summary>A clock a test moves by hand; timers still run on the system's.</summary>
    private sealed class HandClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The reviewer's pathological campaign: a 401-word title of alternating "Bo Ka", and 500 pages of 2000 alternating words, so the
    /// plain-text mention scan walks hundreds of trie steps at every position.</summary>
    private static List<WikiPageDocument> Pathological()
    {
        var title = string.Join(' ', Enumerable.Range(0, 400).Select(i => i % 2 == 0 ? "Bo" : "Ka")) + " End";
        var body = string.Join(' ', Enumerable.Range(0, 2000).Select(i => i % 2 == 0 ? "Bo" : "Ka"));
        var pages = new List<WikiPageDocument> { Page("wikipages/0-A", title, "x") };
        for (var p = 1; p <= 500; p++) pages.Add(Page($"wikipages/{p}-A", "Page " + p, body));
        return pages;
    }

    private static List<WikiPageDocument> Small() => new() { Page("wikipages/1-A", "Road", "Leads to [[Nowhere At All]].") };

    [Fact]
    public async Task ThePathologicalCampaign_StaysWithinTheBudget_ThenIsSkippedAtOnce_UntilTheBackoffEnds_WhileListIssuesStillRunsInFull()
    {
        var pages = Pathological();
        var saving = WorldBuilderTests.ServiceWithPages(pages);
        var clock = new HandClock();
        saving.Service.Clock = clock;
        saving.Service.IssueSummaryBudget = TimeSpan.FromMilliseconds(300);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
        watch.Stop();
        // The budget plus generous slack (the snapshot of 500 pages is built before the checks start); the scan alone takes seconds.
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(300) + TimeSpan.FromSeconds(1.5), $"took {watch.ElapsedMilliseconds} ms");

        // The campaign is now small and quick, and has an issue: the note is still skipped, at once, for the backoff, without a snapshot.
        pages.Clear();
        pages.AddRange(Small());
        saving.Service.IssueSummaryBudget = TimeSpan.FromSeconds(30);
        var snapshots = saving.Snapshots;
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
        Assert.Equal(snapshots, saving.Snapshots);
        clock.Now += saving.Service.IssueSummaryBackoff - TimeSpan.FromSeconds(1);
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        // list_issues and the open issues (the Issues panel, the apply's re-check) are never skipped.
        Assert.Contains("broken-link:page:wikipages/1-A:body:nowhere-at-all", await ListAsync(saving));
        Assert.Single((await saving.Service.OpenIssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm))!);

        // The backoff is over: the note is tried again, and is there.
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Contains("1 open issues", await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
    }

    [Fact]
    public async Task TheSkip_IsPerCampaign_AndAFailureThatIsNotTheBudget_IsNotRemembered()
    {
        var saving = WorldBuilderTests.ServiceWithPages(Small());
        saving.Service.Clock = new HandClock();
        saving.Service.IssueSummaryBudget = TimeSpan.Zero;
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        saving.Service.IssueSummaryBudget = TimeSpan.FromSeconds(30);
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
        var other = new Codex.Persistence.CampaignDocument { Id = "c2", SystemId = "srd5e", Name = "Other" };
        Assert.Contains("1 open issues", await saving.Service.IssueSummaryAsync(other, WorldBuilderTests.Dm));

        // A check that throws leaves no note, but the next message tries again.
        var fresh = WorldBuilderTests.ServiceWithPages(Small());
        fresh.FailChecksAfter = fresh.Snapshots;
        Assert.Null(await fresh.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
        fresh.FailChecksAfter = null;
        Assert.Contains("1 open issues", await fresh.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
    }

    [Fact]
    public async Task TheIssuesPanelsCheck_HasNoBudget_AndFindsWhatThePathologicalCampaignHas()
    {
        // The full run of the campaign the note gave up on: it completes, however long the scan takes.
        var saving = WorldBuilderTests.ServiceWithPages(Pathological());
        saving.Service.IssueSummaryBudget = TimeSpan.FromMilliseconds(1);
        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
        Assert.NotNull(await saving.Service.OpenIssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
    }

    /// <summary>A check that does nothing but wait for its token: only a checker that hands the token to the check can stop it.</summary>
    private sealed class WaitsForTheToken : IConsistencyCheck
    {
        public string Name => "waits";
        public IEnumerable<Issue> Run(WorldSnapshot snapshot) => throw new InvalidOperationException("The checker must call the overload with the token.");

        public IEnumerable<Issue> Run(WorldSnapshot snapshot, CancellationToken cancel)
        {
            if (!cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("The token was never cancelled.");
            cancel.ThrowIfCancellationRequested();
            yield break;
        }
    }

    /// <summary>A check that knows nothing of tokens and yields issues for ever: the checker looks at the token between them.</summary>
    private sealed class Endless : IConsistencyCheck
    {
        public string Name => "endless";

        public IEnumerable<Issue> Run(WorldSnapshot snapshot)
        {
            for (var i = 0; ; i++) yield return new Issue($"endless:x:y:f:{i}", "endless", IssueSeverity.Info, "x", "y", "f", "Consistency.Test", Array.Empty<string>(), "", "", "x:y", FixVia.ReportOnly);
        }
    }

    [Fact]
    public void TheToken_IsObservedInsideACheck_AndBetweenTheIssuesACheckYields_AndACancelledRunReturnsNothing()
    {
        foreach (var check in new IConsistencyCheck[] { new WaitsForTheToken(), new Endless() })
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => ConsistencyChecker.Run(Fx.Snap(), new[] { check }, budget.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{check.Name}: {watch.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void TheMentionScan_LooksAtTheTokenInsideItsLoop()
    {
        var title = string.Join(' ', Enumerable.Range(0, 400).Select(i => i % 2 == 0 ? "Bo" : "Ka")) + " End";
        var body = string.Join(' ', Enumerable.Range(0, 2000).Select(i => i % 2 == 0 ? "Bo" : "Ka"));
        var snapshot = Fx.Snap(pages: new[] { Fx.Page("wikipages/0-A", title, "x") }.Concat(Enumerable.Range(1, 500).Select(p => Fx.Page($"wikipages/{p}-A", "Page " + p, body))));
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The check alone, as one enumeration that yields nothing: only a look at the token inside its loop can stop it early.
        Assert.ThrowsAny<OperationCanceledException>(() => new UnlinkedMentionCheck().Run(snapshot, budget.Token).ToList());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ALoadThatNeverAnswers_DoesNotHoldTheBudgetedCheck_PastItsToken()
    {
        var wiki = NSubstitute.Substitute.For<IWikiRepository>();
        NSubstitute.SubstituteExtensions.Returns(wiki.GetVisibleForCampaignAsync(NSubstitute.Arg.Any<CampaignAccess>()), new TaskCompletionSource<IEnumerable<WikiPageDocument>>().Task);
        var homebrew = NSubstitute.Substitute.For<IHomebrewRepository>();
        var catalog = NSubstitute.Substitute.For<Codex.Plugin.Abstractions.ISystemCatalog>();
        var service = new Codex.Web.Services.ConsistencyService(catalog, new Codex.Web.Services.CampaignLibrary(new Codex.Core.ContentRegistry(), homebrew, catalog), wiki,
            NSubstitute.Substitute.For<IActorRepository>(), NSubstitute.Substitute.For<IRegionRepository>(), NSubstitute.Substitute.For<IFactRepository>(),
            NSubstitute.Substitute.For<IConsistencyDismissalRepository>());
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var check = service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, cancel: budget.Token);

        Assert.Same(check, await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }
}
