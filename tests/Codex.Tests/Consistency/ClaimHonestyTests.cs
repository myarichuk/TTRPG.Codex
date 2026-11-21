using System.Runtime.CompilerServices;
using Codex.Core.AI;
using Codex.Core.Consistency;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.Extensions.AI;

namespace Codex.Tests.Consistency;

/// <summary>
/// The review items carried over from the consistency work (docs/WORLD_BUILDER.md, section 9): a <c>resolves</c> claim never outlives what it named by being
/// carried from one version of a proposal to the next; an issue the DM dismissed is never reported as fixed; a dismissal survives the change of a sheet
/// issue's id from an entry's name to its id; the re-check after an apply is bounded; the count note never fails a turn for a cancellation that is not the turn's;
/// and a load that was given up on never leaves an unobserved exception.
/// </summary>
public class ClaimHonestyTests
{
    private const string Mere = "broken-link:page:wikipages/1-A:body:the-mere-of-dead-men";

    private static WikiPageDocument Road(string body = "Past [[The Mere of Dead Men]].") => new() { Id = "wikipages/1-A", Slug = "road", Title = "Road", Body = body, Visibility = WikiVisibility.Party };

    private static WikiPageDocument Empty() => new() { Id = "wikipages/2-A", Slug = "empty", Title = "Empty", Body = "Something.", Visibility = WikiVisibility.Party };

    private static string Edit(string text, string? resolves, string target = "existing:road", string reference = "e") =>
        $$$"""{"proposal": {"entries": [{"ref": "{{{reference}}}", "kind": "edit", "target": "{{{target}}}", "text": "{{{text}}}"{{{(resolves == null ? "" : $", \"resolves\": [\"{resolves}\"]")}}}}]}}""";

    private static WorldChat ChatOf(WorldBuilderTests.Saving saving, WorldBuilderTests.ScriptedModel model) =>
        new(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

    // ---- (a) claims carried across versions of a proposal ----

    [Fact]
    public async Task AClaimCarriedByARevision_ThatIsNoLongerOpen_IsDroppedByAddEntries_NotCarriedBackUnchecked()
    {
        var road = Road();
        var saving = WorldBuilderTests.ServiceWithPages(new[] { road, Empty() });
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Edit("Past the mere.", Mere)),
            // A revision with no "resolves" of its own: it keeps the claim, which its stored input does not list.
            _ => WorldBuilderTests.Calls("c2", "propose_changes", Edit("Past the mere and the fen.", null)),
            _ =>
            {
                road.Body = "Fixed by hand meanwhile."; // the red link is gone while the proposal waits
                return WorldBuilderTests.Calls("c3", "add_entries", """{"additions": {"entries": [{"ref": "m", "kind": "wiki", "name": "The Mere", "text": "Water."}]}}""");
            },
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        Assert.Equal(2, chat.Proposal!.Drafts.Count);
        Assert.Empty(chat.Proposal.Drafts[0].Resolves); // the carried claim did not come back
        var result = WorldBuilderTests.ToolResult(model.Calls[3]);
        Assert.Contains("Dropped from waiting entries, because they are no longer open issues", result);
        Assert.Contains($"e: [\"{Mere}\"]", result);
    }

    [Fact]
    public async Task AClaimCarriedByARevision_ThatIsStillOpen_IsKeptByAddEntries()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road(), Empty() });
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Edit("Past the mere.", Mere)),
            _ => WorldBuilderTests.Calls("c2", "propose_changes", Edit("Past the mere and the fen.", null)),
            _ => WorldBuilderTests.Calls("c3", "add_entries", """{"additions": {"entries": [{"ref": "m", "kind": "wiki", "name": "The Mere", "text": "Water."}]}}"""),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        Assert.Equal(new[] { Mere }, chat.Proposal!.Drafts[0].Resolves);
        Assert.DoesNotContain("Dropped", WorldBuilderTests.ToolResult(model.Calls[3]));
    }

    [Fact]
    public async Task AClaimARevisionWouldCarry_ThatWasFixedMeanwhile_IsDroppedAndNamed_NotCarriedUnchecked()
    {
        var road = Road();
        var saving = WorldBuilderTests.ServiceWithPages(new[] { road });
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Edit("Past the mere.", Mere)),
            _ =>
            {
                road.Body = "Fixed by hand meanwhile.";
                return WorldBuilderTests.Calls("c2", "propose_changes", Edit("Past the mere and the fen.", null));
            },
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        Assert.Empty(chat.Proposal!.Drafts.Single().Resolves);
        var result = WorldBuilderTests.ToolResult(model.Calls[2]);
        Assert.StartsWith("The proposal (1 entries) is ready", result);
        Assert.Contains($"Dropped from the earlier version of a revised edit, because they are no longer open issues", result);
        Assert.Contains($"e: [\"{Mere}\"]", result);
    }

    [Fact]
    public void AtApplyTime_AClaimThatWasNotOpenWhenTheApplyStarted_IsNeverStillOpenOrResolved_AndIsNewIfTheApplyBroughtItBack()
    {
        var after = new[] { Make(Mere) };

        var recheck = ConsistencyChecker.Compare(Array.Empty<string>(), after, new[] { Mere });

        Assert.Empty(recheck.StillOpen);
        Assert.Empty(recheck.Resolved);
        Assert.Empty(recheck.NotFound); // it is an issue after the apply, so "not an issue before or after" would be false
        Assert.Equal(new[] { Mere }, recheck.New.Select(i => i.Id));
    }

    private static Issue Make(string id) => new(id, "broken-link", IssueSeverity.Warning, "page", "wikipages/1-A", "body", "Consistency.BrokenLink", Array.Empty<string>(), "m", "h", "page:wikipages/1-A", FixVia.Page);

    // ---- (b) dismissed is hidden, never fixed ----

    [Fact]
    public void ADismissedIssue_IsDismissed_NeverResolvedNorNotFoundNorAlsoGone()
    {
        const string other = "broken-link:page:wikipages/1-A:body:beta";
        // Claimed and open before; dismissed meanwhile, so not open after. An unclaimed one likewise.
        var recheck = ConsistencyChecker.Compare(new[] { Mere, other }, Array.Empty<Issue>(), new[] { Mere }, dismissed: new[] { Mere, other });

        Assert.Empty(recheck.Resolved);
        Assert.Empty(recheck.ResolvedUnclaimed);
        Assert.Empty(recheck.NotFound);
        Assert.Equal(new[] { other, Mere }, recheck.Dismissed);

        // Claimed while it was already dismissed (so not open when the apply started): still dismissed, not "not an issue".
        var before = ConsistencyChecker.Compare(Array.Empty<string>(), Array.Empty<Issue>(), new[] { Mere }, dismissed: new[] { Mere });
        Assert.Empty(before.NotFound);
        Assert.Equal(new[] { Mere }, before.Dismissed);

        // A dismissed issue that is open again (restored) is open, not dismissed.
        var open = ConsistencyChecker.Compare(new[] { Mere }, new[] { Make(Mere) }, new[] { Mere }, dismissed: new[] { Mere });
        Assert.Equal(new[] { Mere }, open.StillOpen);
        Assert.Empty(open.Dismissed);
    }

    [Fact]
    public async Task AClaimTheGameMasterDismissedBeforeTheApply_IsReportedAsDismissed_NotAsFixedOrAsNoIssue()
    {
        var pages = new List<WikiPageDocument> { Road() };
        var saving = WorldBuilderTests.ServiceWithPages(pages);
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Edit("Still past [[The Mere of Dead Men]].", Mere)),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."),
            _ => new ChatMessage(ChatRole.Assistant, "Noted."));
        var chat = ChatOf(saving, model);
        await chat.SendAsync("Fix it.", _ => { }, CancellationToken.None);
        saving.Dismissed.Add(Mere); // the DM sets it aside before applying

        var recheck = (await chat.ApplyAsync()).Recheck!;

        Assert.False(recheck.Failed);
        Assert.Empty(recheck.Resolved);
        Assert.Empty(recheck.NotFound);
        Assert.Empty(recheck.StillOpen);
        Assert.Equal(new[] { Mere }, recheck.Dismissed);
        await chat.SendAsync("Thanks.", _ => { }, CancellationToken.None);
        var note = model.Calls[^1].Select(m => m.Text).Single(t => t.StartsWith("(A note from the app, not from the game master: the game master applied", StringComparison.Ordinal));
        Assert.Contains($"dismissed by the game master, so hidden but not fixed (never say these are fixed): [\"{Mere}\"]", note);
        Assert.DoesNotContain("resolved:", note);
    }

    [Fact]
    public async Task ARetriedApply_WhoseClaimWasDismissedBetweenTheAttempts_IsDismissed_NotResolved()
    {
        var pages = new List<WikiPageDocument> { Road(), new() { Id = "wikipages/2-A", Slug = "inn", Title = "Inn", Body = "Old.", Visibility = WikiVisibility.Party } };
        var saving = WorldBuilderTests.ServiceWithPages(pages);
        // The road's edit leaves the red link in place; the inn's is refused at the first attempt.
        var proposal = "{\"proposal\": {\"entries\": [" +
            "{\"ref\": \"e\", \"kind\": \"edit\", \"target\": \"existing:road\", \"text\": \"Still past [[The Mere of Dead Men]].\", \"resolves\": [\"" + Mere + "\"]}," +
            "{\"ref\": \"f\", \"kind\": \"edit\", \"target\": \"existing:inn\", \"text\": \"New.\"}]}}";
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", proposal),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);
        await chat.SendAsync("Fix it.", _ => { }, CancellationToken.None);
        pages[1].Body = "Changed meanwhile.";
        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => chat.ApplyAsync());
        Assert.Equal(new[] { Mere }, chat.Document.ApplyBeforeIssueIds);
        pages[1].Body = "Old.";
        saving.Dismissed.Add(Mere); // between the attempts: hidden, not fixed

        var recheck = (await chat.ApplyAsync()).Recheck!;

        Assert.Empty(recheck.Resolved);
        Assert.Empty(recheck.ResolvedUnclaimed);
        Assert.Equal(new[] { Mere }, recheck.Dismissed);
    }

    // ---- (c) a sheet issue about a Library entry is keyed by the entry's id; a dismissal made under the name still holds ----

    private static readonly SystemDefinition Feats = SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        entityTypes:
          - { type: feat, label: Feat }
        sheetRules:
          enforcement: strict
          lists:
            - { key: feats, label: Feats, targetEntityType: feat }
        """);

    private static List<Issue> Twice(string name) => ConsistencyChecker.Run(Fx.Snap(
        actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object> { ["feats"] = new List<object> { "feat-07", "feat-07" } }) },
        system: Feats, library: new Fx.Entries(Fx.Entry("feat", "feat-07", name))), new IConsistencyCheck[] { new SheetRuleCheck() }).Where(i => i.Code == "Actor.Rule.Once").ToList();

    [Fact]
    public void ASheetIssueAboutAnEntry_KeepsItsId_WhenThePackRenamesTheEntry_OrAnotherPackOfTheSameIdWins()
    {
        var alert = Assert.Single(Twice("Alert"));
        var renamed = Assert.Single(Twice("Alert (Revised)"));

        Assert.Equal("sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:feat-07", alert.Id);
        Assert.Equal(alert.Id, renamed.Id);
        // The name is still what the sentence says.
        Assert.Contains("Alert (Revised)", renamed.Message);
        // The id it had before entries were keyed by id, for dismissals made then.
        Assert.Equal("sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:alert", alert.FormerId);
    }

    [Fact]
    public void ADismissalStoredUnderTheOldNameKeyedId_StillDismissesTheIssue_AndANewOneUsesTheNewId()
    {
        var issue = Assert.Single(Twice("Alert"));

        var (open, dismissed) = IssueDismissals.Split(new[] { issue }, new HashSet<string>(StringComparer.Ordinal) { "sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:alert" });
        Assert.Empty(open);
        Assert.Same(issue, Assert.Single(dismissed));
        Assert.True(issue.Answers("sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:alert"));
        Assert.True(issue.Answers(issue.Id));

        // The new id dismisses it as well; an unrelated id does not.
        Assert.Single(IssueDismissals.Split(new[] { issue }, new HashSet<string> { issue.Id }).Dismissed);
        Assert.Single(IssueDismissals.Split(new[] { issue }, new HashSet<string> { "sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:lucky" }).Open);
    }

    [Fact]
    public void AnOldIdThatWouldNameTwoIssues_MatchesNeither_SoADismissalNeverSetsAsideMoreThanItWasMadeFor()
    {
        // Two entries of one name and different ids: the old scheme gave them one id (and "~2"); the new one tells them apart.
        var both = ConsistencyChecker.Run(Fx.Snap(
            actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object> { ["feats"] = new List<object> { "a-1", "a-1", "a-2", "a-2" } }) },
            system: Feats, library: new Fx.Entries(Fx.Entry("feat", "a-1", "Alert"), Fx.Entry("feat", "a-2", "Alert"))), new IConsistencyCheck[] { new SheetRuleCheck() })
            .Where(i => i.Code == "Actor.Rule.Once").ToList();

        Assert.Equal(new[] { "sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:a-1", "sheet-rule:actor:actors/1-A:feats:Actor.Rule.Once:a-2" }, both.Select(i => i.Id));
        Assert.All(both, i => Assert.Null(i.FormerId));
    }

    [Fact]
    public void AnIssueWhoseIdDidNotChange_HasNoFormerId()
    {
        var issue = Assert.Single(ConsistencyChecker.Run(Fx.Snap(
            actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object> { ["feats"] = new List<object> { "ghost" } }) },
            system: Feats, library: new Fx.Entries()), new IConsistencyCheck[] { new SheetRuleCheck() }), i => i.Code == "Actor.Rule.NotInLibrary");
        Assert.Equal("sheet-rule:actor:actors/1-A:feats:Actor.Rule.NotInLibrary:ghost", issue.Id);
        Assert.Null(issue.FormerId);
    }

    // ---- (d) the re-check's budget, the count note's cancellations, abandoned loads ----

    [Fact]
    public async Task AReCheckThatRunsOutOfItsBudget_IsReportedIncomplete_AndTheApplyStands()
    {
        var pages = new List<WikiPageDocument> { Road() };
        var saving = WorldBuilderTests.ServiceWithPages(pages);
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Edit("Past the mere.", Mere)),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."),
            _ => new ChatMessage(ChatRole.Assistant, "Noted."));
        var chat = ChatOf(saving, model);
        await chat.SendAsync("Fix it.", _ => { }, CancellationToken.None);
        saving.Service.RecheckBudget = TimeSpan.Zero; // spent before the check starts

        var result = await chat.ApplyAsync();

        Assert.Equal("Past the mere.", pages.Single().Body);
        Assert.Equal(1, result.Count);
        Assert.True(result.Recheck!.Failed);
        Assert.True(result.Recheck.TimedOut);
        Assert.Empty(result.Recheck.Resolved);
        Assert.Null(chat.Proposal);
        saving.Service.RecheckBudget = TimeSpan.FromSeconds(60);
        await chat.SendAsync("Thanks.", _ => { }, CancellationToken.None);
        var note = model.Calls[^1].Select(m => m.Text).Single(t => t.StartsWith("(A note from the app, not from the game master: the game master applied", StringComparison.Ordinal));
        Assert.Contains("re-check is incomplete", note);
        Assert.Contains("do not say it is", note);
    }

    [Fact]
    public async Task TheReCheck_IsGivenItsOwnBudget_NotTheCountNotes()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        saving.Service.IssueSummaryBudget = TimeSpan.Zero;

        var report = await saving.Service.RecheckReportAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

        Assert.Equal(new[] { Mere }, report!.Open.Select(i => i.Id));
        Assert.Equal(TimeSpan.FromSeconds(60), saving.Service.RecheckBudget);
    }

    [Fact]
    public async Task ACancellationThatIsNeitherTheBudgetNorTheTurn_LeavesTheCountNoteOut_ItNeverFailsTheTurn()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        saving.DismissalsThrow = new TaskCanceledException("the store timed out");

        Assert.Null(await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        // Not remembered as over the budget: the next message has its note.
        saving.DismissalsThrow = null;
        Assert.Contains("1 open issues", await saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
    }

    [Fact]
    public async Task ATurnThatIsStopped_StopsTheCountNote_WithACancellation()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving.Service.IssueSummaryAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, stopped.Token));
    }

    [Fact]
    public async Task AChatTurn_WithAStoreThatCancelsItsOwnRead_StillAnswers_WithoutANote()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        saving.DismissalsThrow = new OperationCanceledException("not the turn's");
        var model = new WorldBuilderTests.ScriptedModel(_ => new ChatMessage(ChatRole.Assistant, "Hello."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Hi.", _ => { }, CancellationToken.None);

        Assert.DoesNotContain(model.Calls[0], m => m.Text.Contains("open issues", StringComparison.Ordinal));
        Assert.Equal("Hello.", chat.Document.Lines[^1].Text);
    }

    [Fact]
    public async Task ALoadThatWasGivenUpOn_AndFailsLater_LeavesNoUnobservedException()
    {
        var marker = "abandoned-" + Guid.NewGuid().ToString("N");
        var seen = false;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == marker)) seen = true;
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await AbandonAndFailAsync(marker);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(seen, "the abandoned load's exception went unobserved");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task WaitObserved_HandsBackTheResult_OrStopsAtOnceWhenCancelled()
    {
        Assert.Equal(7, await Task.FromResult(7).WaitObservedAsync(CancellationToken.None));
        var never = new TaskCompletionSource<int>();
        using var stop = new CancellationTokenSource();
        var waiting = never.Task.WaitObservedAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        // A failure that is not a cancellation is the caller's to see.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromException<int>(new InvalidOperationException("x")).WaitObservedAsync(CancellationToken.None));
    }

    // Out of line, so nothing of it is still referenced when the collector runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AbandonAndFailAsync(string marker)
    {
        var load = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var stop = new CancellationTokenSource())
        {
            var waiting = load.Task.WaitObservedAsync(stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        load.SetException(new InvalidOperationException(marker)); // the read finishes on its own, after it was given up on, and fails
        await Task.Delay(50); // let the continuation that observes it run
    }
}
