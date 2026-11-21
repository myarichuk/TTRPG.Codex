using System.Text.Json;
using Codex.Core.AI;
using Codex.Core.Consistency;
using Codex.Core.WorldBuilder;
using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.Extensions.AI;

namespace Codex.Tests.Consistency;

/// <summary>Phase 2 of the consistency checker: what a proposal's <c>resolves</c> may name, and what the <c>list_issues</c> tool hands the model.</summary>
public class ResolvesTests
{
    private const string BrokenLink = "broken-link:page:wikipages/1-A:body:the-mere-of-dead-men";
    private const string TodoPlace = "todo-marker:place:p-1:description";
    private const string DanglingPlace = "dangling-reference:page:wikipages/2-A:placeId";

    // Two pages (one with a red link, one pointing at a place that is gone) and a place with a TODO: a warning, an error that only
    // the DM can fix, and an info.
    private static readonly IReadOnlyList<Issue> Open = ConsistencyChecker.Run(Fx.Snap(
        pages: new[]
        {
            Fx.Page("wikipages/1-A", "Road", "Past [[The Mere of Dead Men]]."),
            Fx.Page("wikipages/2-A", "Hidden", "x", placeId: "p-gone")
        },
        places: new[] { new PlaceView("p-1", "regions/1", "Docks", "Wet. TODO", null) }));

    private static readonly ProposalRules Rules = new(
        new Dictionary<string, string>(),
        new Dictionary<string, string> { ["road"] = "Road", ["hidden"] = "Hidden" },
        new[] { "p-1" },
        _ => Array.Empty<FieldSpec>(),
        ExistingPageIds: new Dictionary<string, string> { ["road"] = "wikipages/1-A", ["hidden"] = "wikipages/2-A" });

    private static string Edit(string target, string? resolves, string reference = "e1") =>
        $$"""{"ref": "{{reference}}", "kind": "edit", "target": "{{target}}", "text": "New text."{{(resolves == null ? "" : $", \"resolves\": {resolves}")}}}""";

    private static ProposalCheck Parse(string entries, IReadOnlyList<Issue>? open = null) =>
        WorldBundleParser.ParseProposal($$"""{"entries": [{{entries}}]}""", Rules, open ?? Open);

    [Fact]
    public void TheFixtureHasTheIssuesTheTestsCite()
    {
        Assert.Contains(Open, i => i.Id == BrokenLink && i.FixVia == FixVia.Page);
        Assert.Contains(Open, i => i.Id == TodoPlace && i.FixVia == FixVia.Place);
        Assert.Contains(Open, i => i.Id == DanglingPlace && i.FixVia == FixVia.ReportOnly);
    }

    [Fact]
    public void AProposalWithNoResolves_IsReadAsBefore()
    {
        var check = Parse(Edit("existing:road", null), open: Array.Empty<Issue>());
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Empty(check.Bundle.Drafts.Single().Resolves);
        // And an empty list is the same as none.
        Assert.True(Parse(Edit("existing:road", "[]"), open: Array.Empty<Issue>()).Ok);
    }

    [Fact]
    public void AnOpenIssueOfTheThingTheEditChanges_IsAccepted_AndKept()
    {
        var check = Parse(Edit("existing:road", $"[\"{BrokenLink}\", \"{BrokenLink}\"]") + "," + Edit("existing:place:p-1", $"[\"{TodoPlace}\"]", "e2"));
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal(new[] { BrokenLink }, check.Bundle.Drafts[0].Resolves);
        Assert.Equal(new[] { TodoPlace }, check.Bundle.Drafts[1].Resolves);
    }

    [Fact]
    public void AnIdThatIsNotAnOpenIssue_RefusesTheWholeProposal_NamingIt()
    {
        var check = Parse(Edit("existing:road", $"[\"{BrokenLink}\"]") + "," + Edit("existing:place:p-1", "[\"todo-marker:place:p-1:made-up\"]", "e2"));

        Assert.False(check.Ok);
        var error = Assert.Single(check.Errors);
        Assert.Contains("todo-marker:place:p-1:made-up", error);
        Assert.Contains("list_issues", error);
        Assert.DoesNotContain(BrokenLink, error); // the good id is not blamed
    }

    [Fact]
    public void WithNoIssuesKnown_EveryIdIsUnknown()
    {
        var check = WorldBundleParser.ParseProposal($$"""{"entries": [{{Edit("existing:road", $"[\"{BrokenLink}\"]")}}]}""", Rules);
        Assert.False(check.Ok);
        Assert.Contains(BrokenLink, Assert.Single(check.Errors));
    }

    [Fact]
    public void AnIssueAboutSomethingElse_IsRefused()
    {
        var check = Parse(Edit("existing:place:p-1", $"[\"{BrokenLink}\"]"));
        Assert.False(check.Ok);
        var error = Assert.Single(check.Errors);
        Assert.Contains(BrokenLink, error);
        Assert.Contains("page wikipages/1-A", error);
        Assert.Contains("changes place p-1", error);

        // Another page of the same kind is a mismatch too: the id, not only the kind, has to agree.
        Assert.False(Parse(Edit("existing:hidden", $"[\"{BrokenLink}\"]")).Ok);
    }

    [Fact]
    public void AnEntryThatIsNotAnEdit_CannotResolveAnything()
    {
        var check = Parse($$"""{"ref": "n", "kind": "wiki", "name": "The Mere", "text": "Water.", "resolves": ["{{BrokenLink}}"]}""");
        Assert.False(check.Ok);
        Assert.Contains("is not an edit", Assert.Single(check.Errors));
    }

    [Fact]
    public void AnIssueOnlyTheGameMasterCanFix_IsRefusedWithAClearReason()
    {
        var check = Parse(Edit("existing:hidden", $"[\"{DanglingPlace}\"]"));
        Assert.False(check.Ok);
        var error = Assert.Single(check.Errors);
        Assert.Contains(DanglingPlace, error);
        Assert.Contains("no proposal can fix", error);
    }

    [Fact]
    public void ASingleIdAsAString_IsReadLikeAList()
    {
        var check = Parse(Edit("existing:road", $"\"{BrokenLink}\""));
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal(new[] { BrokenLink }, check.Bundle.Drafts.Single().Resolves);
    }

    [Fact]
    public void MoreThanFiftyIds_AreRefused_NotQuietlyCut()
    {
        var ids = string.Join(", ", Enumerable.Range(0, 51).Select(i => $"\"made-up:{i}\""));
        var check = Parse(Edit("existing:road", $"[{ids}]"));

        Assert.False(check.Ok);
        Assert.Contains("names 51 issues, and one entry may name at most 50", string.Join(" ", check.Errors));
        // Fifty is not too many; here they are not issues, which is the other refusal, and the cut is not what said so.
        Assert.DoesNotContain("at most 50", string.Join(" ", Parse(Edit("existing:road", $"[{string.Join(", ", Enumerable.Range(0, 50).Select(i => $"\"made-up:{i}\""))}]")).Errors));
    }

    [Fact]
    public void AMemberThatIsNotText_RefusesTheProposal_NotQuietlyLeftOut()
    {
        var check = Parse(Edit("existing:road", $"[\"{BrokenLink}\", 3]"));
        Assert.False(check.Ok);
        Assert.Contains("one of its members is not text", string.Join(" ", check.Errors));

        var notAList = Parse(Edit("existing:road", "{\"a\": 1}"));
        Assert.False(notAList.Ok);
        Assert.Contains("is a list of issue ids as text", string.Join(" ", notAList.Errors));
    }

    [Fact]
    public void AnEntryThatWritesItsOwnResolves_EvenAnEmptyList_IsToldApartFromOneThatDoesNot()
    {
        Assert.True(Parse(Edit("existing:road", "[]"), open: Array.Empty<Issue>()).Bundle.Drafts.Single().ResolvesGiven);
        Assert.False(Parse(Edit("existing:road", null), open: Array.Empty<Issue>()).Bundle.Drafts.Single().ResolvesGiven);
        Assert.False(Parse(Edit("existing:road", "null"), open: Array.Empty<Issue>()).Bundle.Drafts.Single().ResolvesGiven);
    }

    [Fact]
    public void TheDraftsResolves_SurviveBeingStoredWithTheChat()
    {
        var draft = Parse(Edit("existing:road", $"[\"{BrokenLink}\"]")).Bundle.Drafts.Single();
        var back = JsonSerializer.Deserialize<WorldDraft>(JsonSerializer.Serialize(draft))!;
        Assert.Equal(new[] { BrokenLink }, back.Resolves);
    }
}

/// <summary>The tool the model reads issues through, and a proposal naming them through the whole chat.</summary>
public class ListIssuesToolTests
{
    private const string BrokenLink = "broken-link:page:wikipages/1-A:body:the-mere-of-dead-men";

    private static WikiPageDocument Road() => new() { Id = "wikipages/1-A", Slug = "road", Title = "Road", Body = "Past [[The Mere of Dead Men]].", Visibility = WikiVisibility.Party };

    private static WikiPageDocument Empty() => new() { Id = "wikipages/2-A", Slug = "empty", Title = "Empty", Body = "", Visibility = WikiVisibility.DmOnly };

    private static Task<string> ListAsync(WorldBuilderTests.Saving saving, string? scope = null, string? severity = null) =>
        saving.Service.IssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, scope, severity);

    [Fact]
    public async Task ListIssues_IsTheSameOnEveryCall_InTheCheckersOrder_AsSnakeCaseJson()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Empty(), Road() });

        var first = await ListAsync(saving);
        var second = await ListAsync(saving);

        Assert.Equal(first, second);
        var rows = JsonDocument.Parse(first).RootElement.EnumerateArray().ToList();
        Assert.Equal(new[] { BrokenLink, "empty-body:page:wikipages/2-A:body" }, rows.Select(r => r.GetProperty("id").GetString()));
        var row = rows[0];
        Assert.Equal(new[] { "id", "check", "severity", "entity_kind", "entity_id", "field", "code", "args", "message", "fix_hint", "bundle", "fix_via" },
            row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("warning", "page", "wikipages/1-A", "page"), (row.GetProperty("severity").GetString(), row.GetProperty("entity_kind").GetString(),
            row.GetProperty("entity_id").GetString(), row.GetProperty("fix_via").GetString()));
        Assert.Equal("info", rows[1].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task ListIssues_HonoursTheScope_AndTheLeastSeverity()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Empty(), Road() });

        string[] Ids(string text) => JsonDocument.Parse(text).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(new[] { BrokenLink }, Ids(await ListAsync(saving, severity: "warning")));
        Assert.Equal(new[] { BrokenLink }, Ids(await ListAsync(saving, "broken-link")));
        Assert.Equal(new[] { "empty-body:page:wikipages/2-A:body" }, Ids(await ListAsync(saving, "page:wikipages/2-A")));
        Assert.Equal(2, Ids(await ListAsync(saving, "all", "INFO")).Length);
        Assert.StartsWith("[]", await ListAsync(saving, "place:nope"));
        Assert.StartsWith("[]", await ListAsync(saving, severity: "error"));
    }

    [Theory]
    [InlineData("brokenlink", null, "is not a scope")]
    [InlineData("pagez:1", null, "is not a scope")]
    [InlineData(null, "serious", "is not a severity")]
    public async Task ListIssues_RefusesAnUnknownScopeOrSeverity_InWords_NotAsAnEmptyList(string? scope, string? severity, string expected)
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });

        var answer = await ListAsync(saving, scope, severity);

        Assert.StartsWith("Nothing was listed", answer);
        Assert.Contains(expected, answer);
    }

    [Fact]
    public async Task ListIssues_CutsAtFifty_AndSaysHowManyMore()
    {
        var pages = Enumerable.Range(1, 57).Select(n => new WikiPageDocument
        {
            Id = $"wikipages/{n:000}-A", Slug = $"p{n:000}", Title = $"Page {n:000}", Body = $"See [[Missing {n:000}]].", Visibility = WikiVisibility.DmOnly
        }).ToList();
        var saving = WorldBuilderTests.ServiceWithPages(pages);

        var answer = await ListAsync(saving);

        var lines = answer.Split('\n');
        Assert.Equal(50, JsonDocument.Parse(string.Join('\n', lines[..^1])).RootElement.GetArrayLength());
        Assert.Equal("(7 more not shown, of 57; narrow with scope or severity to see them)", lines[^1]);
        Assert.Equal(answer, await ListAsync(saving));
    }

    [Fact]
    public async Task ListIssues_IsForTheDmOnly()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() =>
            saving.Service.IssuesAsync(WorldBuilderTests.Campaign, new CampaignAccess("c1", "p", CampaignRole.Player), null, null));
    }

    [Fact]
    public async Task TheChat_CallsListIssues_ReadsItFenced_AndAProposalCitingAMadeUpIdIsRefusedWhole()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        const string made = """{"proposal": {"entries": [{"ref": "e", "kind": "edit", "target": "existing:road", "text": "Past the mere.", "resolves": ["broken-link:page:wikipages/1-A:body:nothing"]}]}}""";
        var good = made.Replace("broken-link:page:wikipages/1-A:body:nothing", BrokenLink);
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "list_issues", "{}"),
            _ => WorldBuilderTests.Calls("c2", "propose_changes", made),
            _ => WorldBuilderTests.Calls("c3", "propose_changes", good),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);
        var steps = new List<WorldChatStep>();

        await chat.SendAsync("Fix the red link.", steps.Add, CancellationToken.None);

        var listed = model.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().First().Result!.ToString()!;
        Assert.Matches(@"^\[campaign data [0-9a-f]{12}\]\n\[", listed);
        Assert.Contains(BrokenLink, listed);
        var refused = WorldBuilderTests.ToolResult(model.Calls[2]);
        Assert.StartsWith("Nothing was proposed", refused);
        Assert.Contains("broken-link:page:wikipages/1-A:body:nothing", refused);
        Assert.Contains("list_issues", refused);
        Assert.Equal(new[] { BrokenLink }, chat.Proposal!.Drafts.Single().Resolves);
    }

    [Fact]
    public async Task ARevisionOfAnEdit_KeepsTheIssuesItResolves()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        string Proposal(string text, string? resolves) =>
            $$$"""{"proposal": {"entries": [{"ref": "e", "kind": "edit", "target": "existing:road", "text": "{{{text}}}"{{{(resolves == null ? "" : $", \"resolves\": [\"{resolves}\"]")}}}}]}}""";
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Proposal("Past the mere.", BrokenLink)),
            _ => WorldBuilderTests.Calls("c2", "propose_changes", Proposal("Past the mere and the fen.", null)),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        var draft = chat.Proposal!.Drafts.Single();
        Assert.Equal("Past the mere and the fen.", draft.Body);
        Assert.Equal(new[] { BrokenLink }, draft.Resolves);
    }

    [Fact]
    public async Task AddingEntries_ChecksTheWaitingOnesToo_AndKeepsTheirResolves()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road(), Empty() });
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", $$$"""{"proposal": {"entries": [{"ref": "e", "kind": "edit", "target": "existing:road", "text": "Past the mere.", "resolves": ["{{{BrokenLink}}}"]}]}}"""),
            _ => WorldBuilderTests.Calls("c2", "add_entries", """{"additions": {"entries": [{"ref": "m", "kind": "wiki", "name": "The Mere", "text": "Water."}]}}"""),
            _ => WorldBuilderTests.Calls("c3", "add_entries", """{"additions": {"entries": [{"ref": "x", "kind": "edit", "target": "existing:empty", "text": "Something.", "resolves": ["nope"]}]}}"""),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        Assert.Equal(2, chat.Proposal!.Drafts.Count);
        Assert.Equal(new[] { BrokenLink }, chat.Proposal.Drafts[0].Resolves);
        // The third call named an id that is not an issue: refused whole, and the proposal that was waiting stands.
        var refused = WorldBuilderTests.ToolResult(model.Calls[3]);
        Assert.StartsWith("Nothing was", refused);
        Assert.Contains("nope", refused);
    }

    private static WorldChat ChatOf(WorldBuilderTests.Saving saving, WorldBuilderTests.ScriptedModel model) =>
        new(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

    [Fact]
    public async Task AClaimOnAWaitingEntryThatIsNoLongerOpen_IsDropped_AndTheModelIsTold_NotRefusedForIt()
    {
        var road = Road();
        var saving = WorldBuilderTests.ServiceWithPages(new[] { road, Empty() });
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", $$$"""{"proposal": {"entries": [{"ref": "e", "kind": "edit", "target": "existing:road", "text": "Past the mere.", "resolves": ["{{{BrokenLink}}}"]}]}}"""),
            _ =>
            {
                road.Body = "Past the mere, already fixed by hand."; // the red link is gone while the proposal waits
                return WorldBuilderTests.Calls("c2", "add_entries", """{"additions": {"entries": [{"ref": "m", "kind": "wiki", "name": "The Mere", "text": "Water."}]}}""");
            },
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        var result = WorldBuilderTests.ToolResult(model.Calls[2]);
        Assert.DoesNotContain("Nothing was", result);
        Assert.Contains("Dropped from waiting entries, because they are no longer open issues", result);
        Assert.Contains($"e: [\"{BrokenLink}\"]", result);
        Assert.Equal(2, chat.Proposal!.Drafts.Count);
        Assert.Empty(chat.Proposal.Drafts[0].Resolves); // not carried back over
    }

    [Fact]
    public async Task AClaimThatIsStillOpen_IsKeptWhenEntriesAreAdded_AndAMadeUpIdOnANewEntryStillRefuses()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road(), Empty() });
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", $$$"""{"proposal": {"entries": [{"ref": "e", "kind": "edit", "target": "existing:road", "text": "Past the mere.", "resolves": ["{{{BrokenLink}}}"]}]}}"""),
            _ => WorldBuilderTests.Calls("c2", "add_entries", """{"additions": {"entries": [{"ref": "x", "kind": "edit", "target": "existing:empty", "text": "Something.", "resolves": ["nope"]}]}}"""),
            _ => WorldBuilderTests.Calls("c3", "add_entries", """{"additions": {"entries": [{"ref": "m", "kind": "wiki", "name": "The Mere", "text": "Water."}]}}"""),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        Assert.Contains("nope", WorldBuilderTests.ToolResult(model.Calls[2]));
        Assert.Equal(new[] { BrokenLink }, chat.Proposal!.Drafts[0].Resolves);
        Assert.DoesNotContain("Dropped", WorldBuilderTests.ToolResult(model.Calls[3]));
    }

    [Fact]
    public async Task ARevisionThatListsItsOwnResolves_ReplacesTheEarlierClaims_AndAnEmptyListWithdrawsThem()
    {
        var saving = WorldBuilderTests.ServiceWithPages(new[] { Road() });
        string Proposal(string text, string? resolves) =>
            $$$"""{"proposal": {"entries": [{"ref": "e", "kind": "edit", "target": "existing:road", "text": "{{{text}}}"{{{(resolves == null ? "" : $", \"resolves\": {resolves}")}}}}]}}""";
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Proposal("Past the mere.", $"[\"{BrokenLink}\"]")),
            _ => WorldBuilderTests.Calls("c2", "propose_changes", Proposal("Past the mere and the fen.", "[]")),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."));
        var chat = ChatOf(saving, model);

        await chat.SendAsync("Fix the red link.", _ => { }, CancellationToken.None);

        Assert.Empty(chat.Proposal!.Drafts.Single().Resolves);
        Assert.Equal("Past the mere and the fen.", chat.Proposal.Drafts.Single().Body);
    }
}
