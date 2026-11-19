using Bunit;
using Microsoft.AspNetCore.Components;
using Codex.Core.Consistency;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Campaign;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>
/// The Issues panel (docs/WORLD_BUILDER.md, 9): the game master reads what the checks found, filters it, dismisses with a note and restores, and sees what
/// the last apply did. Run against the real <see cref="ConsistencyService"/> over in-memory stores, so the list is the checker's own. A player gets nothing.
/// </summary>
public class IssuesSectionTests : ComponentTestBase
{
    private const string Mere = "broken-link:page:wikipages/1-A:body:the-mere";

    private sealed class MemoryDismissals : IConsistencyDismissalRepository
    {
        public List<IssueDismissal> Held { get; } = new();
        public int Reads { get; private set; }

        public Task<IReadOnlyList<IssueDismissal>> ListAsync(CampaignAccess access)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<IssueDismissal>>(access.IsDm ? Held.ToList() : Array.Empty<IssueDismissal>());
        }

        public Task<bool> DismissAsync(CampaignAccess access, string issueId, string? note)
        {
            if (!access.IsDm || Held.Any(d => d.IssueId == issueId)) return Task.FromResult(false);
            Held.Add(new IssueDismissal { IssueId = issueId, DismissedBy = access.UserId, Note = note });
            return Task.FromResult(true);
        }

        public Task<bool> RestoreAsync(CampaignAccess access, string issueId) => Task.FromResult(access.IsDm && Held.RemoveAll(d => d.IssueId == issueId) > 0);

        public Task DeleteAllForCampaignAsync(string campaignId) => Task.CompletedTask;
    }

    private readonly MemoryDismissals dismissals = new();
    private readonly IWikiRepository wiki = Substitute.For<IWikiRepository>();
    private readonly CampaignDocument campaign = new() { Id = "c1", SystemId = "srd5e", Name = "Salt" };
    private readonly IssueRecheckLog rechecks = new();

    public IssuesSectionTests()
    {
        var pages = new[]
        {
            new WikiPageDocument { Id = "wikipages/1-A", Slug = "road", Title = "Road", Body = "Past [[The Mere]] and [[actor:actors/gone]].", Visibility = WikiVisibility.Party },
            new WikiPageDocument { Id = "wikipages/2-A", Slug = "empty", Title = "Empty", Body = "", Visibility = WikiVisibility.DmOnly },
            new WikiPageDocument { Id = "wikipages/3-A", Slug = "lonely", Title = "Lonely", Body = "Nobody links here.", Visibility = WikiVisibility.DmOnly }
        };
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<CampaignAccess>().IsDm ? pages.AsEnumerable() : Array.Empty<WikiPageDocument>().AsEnumerable());
        var actors = Substitute.For<IActorRepository>();
        actors.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Array.Empty<ActorDocument>().AsEnumerable());
        var regions = Substitute.For<IRegionRepository>();
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Array.Empty<RegionDocument>().AsEnumerable());
        var facts = Substitute.For<IFactRepository>();
        facts.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>(), Arg.Any<IReadOnlySet<string>>()).Returns(Array.Empty<FactDocument>().AsEnumerable());
        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Array.Empty<HomebrewEntryDocument>().AsEnumerable());
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetDefinition("srd5e").Returns(new SystemDefinition { Id = "srd5e", Name = "SRD" });
        var library = new Codex.Web.Services.CampaignLibrary(new Codex.Core.ContentRegistry(), homebrew, catalog);
        Service = new ConsistencyService(catalog, library, wiki, actors, regions, facts, dismissals, homebrew);
        Services.AddSingleton(Service);
        Services.AddSingleton(rechecks);
        Services.AddSingleton(wiki);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<IssuesSection>>(NullLogger<IssuesSection>.Instance);
    }

    private ConsistencyService Service { get; }

    private IRenderedComponent<CascadingValue<CampaignContext>> Draw(bool dm = true, string culture = "en")
    {
        Services.AddSingleton(Localizer.For(culture));
        var access = new CampaignAccess("c1", dm ? "dm" : "p1", dm ? CampaignRole.DM : CampaignRole.Player);
        var ctx = new CampaignContext
        {
            Campaign = campaign, Access = access, UserId = access.UserId, Actors = Array.Empty<ActorDocument>(), AllActors = Array.Empty<ActorDocument>(),
            ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
        };
        return Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx).AddChildContent<IssuesSection>());
    }

    [Fact]
    public void TheGameMaster_SeesTheOpenIssues_BySeverity_MostSevereFirst_InTheirOwnLanguage()
    {
        var cut = Draw();

        cut.WaitForState(() => cut.FindAll(Id("issue")).Count > 0);
        var groups = cut.FindAll("[data-testid^=issues-group-]").Select(g => g.GetAttribute("data-testid")).ToList();
        Assert.Equal(new[] { "issues-group-error", "issues-group-warning", "issues-group-info" }, groups);
        Assert.Equal(new[] { "dangling-mention:page:wikipages/1-A:body:actor:actors/gone", Mere, "empty-body:page:wikipages/2-A:body" },
            cut.FindAll(Id("issue")).Select(i => i.GetAttribute("data-issue-id")).ToArray());
        Assert.Contains("The link [[The Mere]] points to no page.", cut.Markup);
        Assert.Contains("3 open: 1 errors, 1 warnings, 1 info", cut.Find(Id("issues-counts")).TextContent);
        // The noisy checks are off until asked for: the lonely page is not in the list.
        Assert.DoesNotContain("Nothing links to the page", cut.Markup);
        // The page an issue is about opens by its slug.
        Assert.Contains(cut.FindAll(Id("issue-open")), a => a.GetAttribute("href") == "/campaigns/c1/wiki/road");
    }

    [Theory]
    [InlineData("ru", "Ссылка [[The Mere]] не ведёт ни на одну страницу.")]
    [InlineData("he", "הקישור [[The Mere]] לא מוביל לשום דף.")]
    public void TheSentences_AreMadeFromTheCodeAndArgs_InTheReadersLanguage(string culture, string expected)
    {
        var cut = Draw(culture: culture);
        cut.WaitForState(() => cut.FindAll(Id("issue")).Count > 0);
        Assert.Contains(expected, cut.Markup);
        Assert.DoesNotContain("points to no page", cut.Markup);
    }

    [Fact]
    public void ASeverityAndAKindFilter_NarrowTheList()
    {
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 3);

        cut.Find(Id("issues-severity-warning")).Click();
        Assert.Equal(new[] { Mere }, cut.FindAll(Id("issue")).Select(i => i.GetAttribute("data-issue-id")).ToArray());

        cut.Find(Id("issues-severity-all")).Click();
        cut.Find(Id("issues-kind-page")).Click();
        Assert.Equal(3, cut.FindAll(Id("issue")).Count);
        cut.Find(Id("issues-severity-error")).Click();
        cut.Find(Id("issues-severity-info")).Click();
        Assert.Single(cut.FindAll(Id("issue")));
    }

    [Fact]
    public async Task TheNoisyFindings_AreShownOnlyWhenAskedFor()
    {
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 3);

        await cut.Find(Id("issues-noise")).ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        cut.WaitForState(() => cut.FindAll(Id("issue")).Count > 3);
        Assert.Contains("Nothing links to the page", cut.Markup);
    }

    [Fact]
    public void ADismissedIssue_LeavesTheList_KeepsItsNote_AndComesBackWhenRestored()
    {
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 3);

        cut.FindAll(Id("issue-dismiss"))[1].Click(); // the red link
        cut.Find(Id("issue-note")).Input("meant to stay red");
        cut.Find(Id("issue-dismiss-confirm")).Click();

        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 2);
        Assert.DoesNotContain(cut.FindAll(Id("issue")), i => i.GetAttribute("data-issue-id") == Mere);
        var held = Assert.Single(dismissals.Held);
        Assert.Equal((Mere, "meant to stay red", "dm"), (held.IssueId, held.Note, held.DismissedBy));
        Assert.Contains("2 open", cut.Find(Id("issues-counts")).TextContent);
        Assert.Equal("meant to stay red", cut.Find(Id("issue-dismissed-note")).TextContent);
        Assert.Contains("Dismissed (1)", cut.Find(Id("issues-dismissed")).TextContent);

        cut.Find(Id("issue-restore")).Click();

        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 3);
        Assert.Empty(dismissals.Held);
        Assert.Empty(cut.FindAll(Id("issues-dismissed")));
    }

    [Fact]
    public void ADismissalWhoseProblemIsGone_IsListedByItsId_AndCanBeRestored()
    {
        dismissals.Held.Add(new IssueDismissal { IssueId = "broken-link:page:wikipages/9-Z:body:old", DismissedBy = "dm", Note = null });
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 3);

        var row = cut.Find(Id("issue-dismissed"));
        Assert.Contains("broken-link:page:wikipages/9-Z:body:old", row.TextContent);
        Assert.Contains("The problem is gone", row.TextContent);
        Assert.Contains("No note", row.TextContent);
    }

    [Fact]
    public void WhatTheLastApplyDid_IsShown_FromTheChecks_WithAWayToHideIt()
    {
        rechecks.Record("c1", new ApplyRecheck(false, new[] { "a:1" }, new[] { Mere }, Array.Empty<string>(), new[] { "b:2" }, new[] { "c:3" }));
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("issue")).Count == 3);

        var note = cut.Find(Id("issues-recheck"));
        Assert.Contains("Fixed: a:1", note.TextContent);
        Assert.Contains("Still open, the change did not fix them: " + Mere, note.TextContent);
        Assert.Contains("New, introduced by the change: c:3", note.TextContent);
        Assert.Contains("Also fixed, though nobody claimed them: b:2", note.TextContent);

        cut.Find(Id("issues-recheck-hide")).Click();
        Assert.Empty(cut.FindAll(Id("issues-recheck")));
    }

    [Fact]
    public void AReCheckThatCouldNotRun_SaysSo_AndTheApplyStood()
    {
        var cut = Render<ApplyRecheckNote>(p => p.Add(x => x.Recheck, ApplyRecheck.Couldnt).Add(x => x.IssuesHref, "/campaigns/c1/issues"));
        Assert.Contains("could not re-run its checks", cut.Find(Id("issues-recheck-failed")).TextContent);
        Assert.Equal("/campaigns/c1/issues", cut.Find(Id("issues-recheck-see")).GetAttribute("href"));
    }

    // ---- players never see any of it ----

    [Fact]
    public void APlayer_IsToldItIsForTheGameMasterAlone_AndNothingIsReadOrShown()
    {
        rechecks.Record("c1", new ApplyRecheck(false, new[] { "a:1" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
        dismissals.Held.Add(new IssueDismissal { IssueId = Mere, DismissedBy = "dm", Note = "a secret reason" });

        var cut = Draw(dm: false);

        Assert.Contains("only", cut.Find(Id("issues-dm-only")).TextContent);
        Assert.Empty(cut.FindAll(Id("issue")));
        Assert.Empty(cut.FindAll(Id("issues-recheck")));
        Assert.DoesNotContain("Mere", cut.Markup);
        Assert.DoesNotContain("a secret reason", cut.Markup);
        Assert.DoesNotContain("a:1", cut.Markup);
        Assert.Equal(0, dismissals.Reads); // not even asked
        wiki.DidNotReceiveWithAnyArgs().GetVisibleForCampaignAsync(default!);
    }

    [Fact]
    public async Task TheService_RefusesEveryoneButTheGameMaster_ForEveryCall()
    {
        var player = new CampaignAccess("c1", "p1", CampaignRole.Player);
        var observer = new CampaignAccess("c1", "o1", CampaignRole.Observer);
        foreach (var access in new[] { player, observer })
        {
            Assert.Equal("WorldBuilder.Error.OnlyDm", (await Assert.ThrowsAsync<LocalizedException>(() => Service.CheckAsync(campaign, access))).Key);
            Assert.Equal("WorldBuilder.Error.OnlyDm", (await Assert.ThrowsAsync<LocalizedException>(() => Service.ListAsync(campaign, access, new IssueFilter()))).Key);
            Assert.Equal("WorldBuilder.Error.OnlyDm", (await Assert.ThrowsAsync<LocalizedException>(() => Service.DismissAsync(campaign, access, Mere, null))).Key);
            Assert.Equal("WorldBuilder.Error.OnlyDm", (await Assert.ThrowsAsync<LocalizedException>(() => Service.RestoreAsync(access, Mere))).Key);
            Assert.Equal("WorldBuilder.Error.OnlyDm", (await Assert.ThrowsAsync<LocalizedException>(() => Service.DismissalsAsync(access))).Key);
        }

        Assert.Empty(dismissals.Held);
    }

    [Fact]
    public void ThePanel_IsOffered_ToTheGameMasterOnly()
    {
        Assert.True(Codex.Web.Components.Pages.CampaignPage.IsDmOnly("issues"));
        Assert.True(Codex.Web.Components.Pages.CampaignPage.IsDmOnly("build"));
        Assert.False(Codex.Web.Components.Pages.CampaignPage.IsDmOnly("wiki"));
        // The model's list_issues tool is the same: every tool of the chat is the game master's (WorldBuilderService.DmOnly).
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "Components", "Pages", "CampaignPage.razor"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"!IsDmOnly\(s\.Key\) \|\| access\.IsDm").Count);
    }
}
