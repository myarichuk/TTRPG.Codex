using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Campaign;
using Codex.Web.Components.Pages;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>The Encounters section (ENCOUNTER_BUILDER_DESIGN.md 5.1): the GM's list and editor, and nothing at all for a player who reaches the address.</summary>
public class EncountersSectionTests : ComponentTestBase
{
    private readonly EncounterWorld _w = new();

    // The pages load and search off the render thread (the search is debounced), so every wait has room for a loaded test run.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public EncountersSectionTests()
    {
        Services.AddSingleton(_w.Service);
        Services.AddSingleton<ILogger<EncounterList>>(NullLogger<EncounterList>.Instance);
        Services.AddSingleton<ILogger<EncounterEditor>>(NullLogger<EncounterEditor>.Instance);
        Services.AddSingleton(ViewerClock.ForZone("UTC"));
        // The editor offers "Start at the table" only while a table runs, so it asks the manager. This one has no table running.
        var components = new Codex.Plugin.Abstractions.ComponentRegistry();
        Services.AddSingleton(new Codex.Persistence.Runtime.CampaignRuntimeManager(_w.ActorRepo, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(), components, new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, components, Substitute.For<Codex.Plugin.Abstractions.IContentPackLoader>()), NullLoggerFactory.Instance));
    }

    private IRenderedComponent<CascadingValue<CampaignContext>> Draw(string? route = null, bool dm = true, string culture = "en", params BestiaryEntry[] shortlist)
    {
        Services.AddSingleton(Localizer.For(culture));
        var access = dm ? _w.Gm : _w.Player;
        _w.Campaign.Bestiary = shortlist.ToList();
        var ctx = new CampaignContext
        {
            Campaign = _w.Campaign, Access = access, UserId = access.UserId, Actors = Array.Empty<ActorDocument>(), AllActors = Array.Empty<ActorDocument>(),
            ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
        };
        return Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx).AddChildContent<EncountersSection>(c => c.Add(x => x.Route, route)));
    }

    private async Task<PreparedEncounterDocument> StoredAsync(string name, string? place = "cellar", PreparedStatus status = PreparedStatus.Draft, params PreparedLine[] lines)
    {
        var plan = _w.Plan(name, place, lines);
        plan.Status = status;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        return plan;
    }

    // ---- Players ----

    [Fact]
    public void ThePlayersRailAndMenu_LeaveTheSectionOut()
    {
        Assert.True(CampaignPage.IsDmOnly("encounters"));
        Assert.False(CampaignPage.IsDmOnly("bestiary"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("new")]
    [InlineData("abc")]
    public async Task APlayerWhoReachesTheAddress_SeesNothing_AndNothingIsEvenAskedFor(string? route)
    {
        await StoredAsync("Ambush");
        _w.RegionRepo.ClearReceivedCalls();
        _w.ActorRepo.ClearReceivedCalls();

        var cut = Draw(route, dm: false);

        Assert.NotNull(cut.Find(Id("encounters-dm-only")));
        Assert.Empty(cut.FindAll(Id("encounter-list")));
        Assert.Empty(cut.FindAll(Id("encounter-new")));
        Assert.Empty(cut.FindAll(Id("encounter-editor")));
        Assert.DoesNotContain("Ambush", cut.Markup);
        _ = _w.RegionRepo.DidNotReceive().GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>());
        _ = _w.ActorRepo.DidNotReceive().GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>());
    }

    [Fact]
    public void TheSectionIsInTheWorldGroupAfterTheBestiary_AndOnlyTheGameMasterIsOfferedIt()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "Components", "Pages", "CampaignPage.razor"));

        Assert.Contains("(\"bestiary\", \"Bestiary\", \"paw-print\"),\n            (\"encounters\", \"Encounters\", \"swords\"),", source.Replace("\r\n", "\n"));
        Assert.Contains("<EncountersSection Route=\"@Arg1\" />", source);
        // The rail and the phone menu both filter by it.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"!IsDmOnly\(s\.Key\) \|\| access\.IsDm").Count);
    }

    // ---- The list ----

    [Fact]
    public void WithNoPlans_TheListSaysInOneSentenceWhatAnEncounterIsFor_AndOffersOnlyNewEncounter()
    {
        var cut = Draw();

        cut.WaitForState(() => cut.FindAll(Id("encounters-empty")).Count > 0, Patience);
        Assert.Contains("An encounter is a fight you prepare before the session", cut.Find(Id("encounters-empty")).TextContent);
        Assert.Equal("/campaigns/c1/encounters/new", cut.Find(Id("encounter-new")).GetAttribute("href"));
        Assert.DoesNotContain("AI", cut.Markup.Replace("aria", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("Start at the table", cut.Markup);
    }

    [Fact]
    public async Task EachPlan_IsARow_WithItsPlaceCreaturesAndStatus_AndARanPlanSaysHowManyTimesItRan()
    {
        await StoredAsync("Ambush", "vault", PreparedStatus.Ready, EncounterWorld.Bandits(3), EncounterWorld.Cast("npc-1", "Wendel Tarr"));
        var ran = await StoredAsync("Old fight", "hall");
        _w.Plans.Held[ran.Id].Status = PreparedStatus.Ran;
        _w.Plans.Held[ran.Id].RunEncounterIds = ["enc-1"];
        var often = await StoredAsync("Again and again", "hall");
        _w.Plans.Held[often.Id].Status = PreparedStatus.Ran;
        _w.Plans.Held[often.Id].RunEncounterIds = ["enc-1", "enc-2", "enc-3"];
        var odd = await StoredAsync("Odd one", "hall");
        _w.Plans.Held[odd.Id].Status = PreparedStatus.Ran;

        var cut = Draw();

        cut.WaitForState(() => cut.FindAll(Id("encounter-row")).Count == 4, Patience);
        var rows = cut.FindAll(Id("encounter-row")).ToDictionary(r => r.QuerySelector(Id("encounter-open"))!.TextContent);
        Assert.Equal("Greywater > Vault", rows["Ambush"].QuerySelector(Id("encounter-place"))!.TextContent);
        Assert.Equal("Bandit ×3, Wendel Tarr", rows["Ambush"].QuerySelector(Id("encounter-summary"))!.TextContent);
        Assert.Equal("Ready", rows["Ambush"].QuerySelector(Id("encounter-status"))!.TextContent);
        Assert.Equal("Ran once", rows["Old fight"].QuerySelector(Id("encounter-status"))!.TextContent);
        Assert.Equal("Ran 3 times", rows["Again and again"].QuerySelector(Id("encounter-status"))!.TextContent);
        Assert.Equal("Ran", rows["Odd one"].QuerySelector(Id("encounter-status"))!.TextContent);
        Assert.Equal("No creatures yet", rows["Old fight"].QuerySelector(Id("encounter-summary"))!.TextContent);
        Assert.Equal($"/campaigns/c1/encounters/{PreparedEncounterService.RouteOf(ran.Id)}", rows["Old fight"].QuerySelector(Id("encounter-open"))!.GetAttribute("href"));
    }

    [Fact]
    public async Task TheList_FiltersByPlaceAndStatus_AndSearchesByName_AndSaysHowManyItShows()
    {
        await StoredAsync("Ambush in the cellar", "cellar", PreparedStatus.Ready);
        await StoredAsync("Vault guards", "vault", PreparedStatus.Draft);
        await StoredAsync("Wandering wolves", null, PreparedStatus.Draft);
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("encounter-row")).Count == 3, Patience);

        cut.Find(Id("encounter-status-filter")).Change(nameof(PreparedStatus.Draft));
        Assert.Equal(new[] { "Vault guards", "Wandering wolves" }, Names(cut).Order());
        Assert.Equal("2 encounters", cut.Find(Id("encounter-count")).TextContent);

        cut.Find(Id("encounter-place-filter")).Change("vault");
        Assert.Equal(new[] { "Vault guards" }, Names(cut));
        Assert.Equal("1 encounter", cut.Find(Id("encounter-count")).TextContent);

        cut.Find(Id("encounter-place-filter")).Change("none");
        Assert.Equal(new[] { "Wandering wolves" }, Names(cut));

        cut.Find(Id("encounter-status-filter")).Change(string.Empty);
        cut.Find(Id("encounter-place-filter")).Change(string.Empty);
        cut.Find(Id("encounter-search")).Input("CELLAR");
        Assert.Equal(new[] { "Ambush in the cellar" }, Names(cut));

        cut.Find(Id("encounter-search")).Input("zzz");
        Assert.NotNull(cut.Find(Id("encounters-no-match")));
    }

    private static string[] Names(IRenderedComponent<CascadingValue<CampaignContext>> cut) =>
        cut.FindAll(Id("encounter-open")).Select(a => a.TextContent).ToArray();

    [Fact]
    public async Task ARanPlanWithADate_SaysWhenItLastRan_AndOneWithoutStillSaysHowManyTimes()
    {
        var dated = await StoredAsync("Dated", "hall");
        _w.Plans.Held[dated.Id].Status = PreparedStatus.Ran;
        _w.Plans.Held[dated.Id].RunEncounterIds = ["enc-1", "enc-2"];
        _w.Plans.Held[dated.Id].LastRunAt = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);
        var once = await StoredAsync("Once", "hall");
        _w.Plans.Held[once.Id].Status = PreparedStatus.Ran;
        _w.Plans.Held[once.Id].RunEncounterIds = ["enc-1"];
        _w.Plans.Held[once.Id].LastRunAt = new DateTime(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc);
        var old = await StoredAsync("Old", "hall");
        _w.Plans.Held[old.Id].Status = PreparedStatus.Ran;
        _w.Plans.Held[old.Id].RunEncounterIds = ["enc-1"];

        var cut = Draw();

        cut.WaitForState(() => cut.FindAll(Id("encounter-row")).Count == 3, Patience);
        var rows = cut.FindAll(Id("encounter-row")).ToDictionary(r => r.QuerySelector(Id("encounter-open"))!.TextContent);
        Assert.Equal("Ran 2 times, last on 20 Nov 2026", rows["Dated"].QuerySelector(Id("encounter-status"))!.TextContent);
        Assert.Equal("Ran on 21 Nov 2026", rows["Once"].QuerySelector(Id("encounter-status"))!.TextContent);
        Assert.Equal("Ran once", rows["Old"].QuerySelector(Id("encounter-status"))!.TextContent);
    }

    // ---- The editor ----

    [Fact]
    public async Task TheEditor_WithNoTableRunning_LinksToTheTable_AndOffersNoStartButton()
    {
        var plan = await StoredAsync("Ambush", "cellar", PreparedStatus.Ready, EncounterWorld.Bandits(3));

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 1, Patience);
        Assert.Equal("/campaigns/c1/table", cut.Find(Id("encounter-start-table")).GetAttribute("href"));
        Assert.Equal("Start the table to run this encounter", cut.Find(Id("encounter-start-table")).TextContent.Trim());
        Assert.Empty(cut.FindAll(Id("encounter-start")));
    }

    [Fact]
    public async Task TheEditor_LabelsEachLineWithItsCreature_AndShowsTheHazardsAtThePlaceAsText()
    {
        var plan = await StoredAsync("Ambush", "cellar", PreparedStatus.Ready, EncounterWorld.Bandits(3));

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 1, Patience);
        var line = cut.Find(Id("encounter-line"));
        Assert.Equal("How many Bandit", cut.Find("label[for=encounter-line-0-count]").TextContent);
        Assert.Equal("3", line.QuerySelector(Id("encounter-line-count"))!.GetAttribute("value"));
        Assert.Equal("Starts hidden", cut.Find("label[for=encounter-line-0-hidden]").TextContent);
        Assert.Equal("Remove Bandit", line.QuerySelector(Id("encounter-line-remove"))!.GetAttribute("aria-label"));
        Assert.Equal("Note for Bandit", cut.Find("label[for=encounter-line-0-note]").TextContent);
        Assert.Equal("UL", line.ParentElement!.TagName);

        var hazards = cut.FindAll(Id("encounter-hazard"));
        Assert.Equal(2, hazards.Count);
        Assert.Contains("Spiked pit", hazards[0].TextContent);
        Assert.Contains("Noticed", hazards[0].TextContent);
        Assert.Contains("Hidden", hazards[1].TextContent);
        Assert.Equal("/campaigns/c1/world#place-vault", hazards[1].QuerySelector("a")!.GetAttribute("href"));

        // No table runs here, so there is no start button (the link to the table is its own test). This system declares no difficulty block, so there is no readout either.
        Assert.DoesNotContain("Start at the table", cut.Markup);
        Assert.DoesNotContain("Difficulty", cut.Markup);
    }

    [Fact]
    public async Task APlaceWithNoHazards_SaysSoAndLinksToThePlace()
    {
        var plan = await StoredAsync("Chat", "hall");

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-hazards-none")).Count > 0, Patience);
        var none = cut.Find(Id("encounter-hazards-none"));
        Assert.StartsWith("No hazards are placed here. Place one on the place's page.", none.TextContent);
        Assert.Equal("/campaigns/c1/world#place-hall", none.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task APlanWithAGoneEntryAndAGonePlace_ShowsTheirWarningsAsText_AndKeepsTheLine()
    {
        var gone = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "dragon", Name = "Old dragon" };
        var plan = await StoredAsync("Ambush", "cellar", PreparedStatus.Draft, gone);
        _w.Region.Locations.RemoveAll(l => l.Id == "cellar");

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 1, Patience);
        Assert.Contains("Old dragon", cut.Find(Id("encounter-line")).TextContent);
        Assert.Equal("Not in the Library", cut.Find(Id("encounter-line-gone")).TextContent);
        var warnings = cut.Find(Id("encounter-warnings")).TextContent;
        Assert.Contains("Old dragon is no longer in the Library.", warnings);
        Assert.Contains("The place was deleted.", warnings);
        Assert.Empty(cut.FindAll(Id("encounter-errors")));
    }

    [Fact]
    public async Task TheAddSearch_AnnouncesItsCountInAPoliteRegion_AndNeverListsAHazard()
    {
        _w.Registry.RegisterEntry(new Codex.Plugin.Abstractions.ContentEntry { Id = "trapdoor", Type = "monster", SystemId = "demo", PackId = "base", Name = "Trapdoor", Tags = new List<string> { "hazard" } }, 0);
        var plan = await StoredAsync("Ambush");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForAssertion(() => Assert.Equal("2 results", cut.Find(Id("encounter-add-count")).TextContent), Patience);

        var region = cut.Find(Id("encounter-add-count"));
        Assert.Equal("polite", region.GetAttribute("aria-live"));
        Assert.Equal("status", region.GetAttribute("role"));
        Assert.Equal(new[] { "Bandit", "Wolf" }, cut.FindAll(Id("encounter-add-results") + " .row-title").Select(t => t.TextContent.Trim()).Order());

        cut.Find(Id("encounter-add-search")).Input("ban");
        cut.WaitForAssertion(() => Assert.Equal("1 result", cut.Find(Id("encounter-add-count")).TextContent), Patience);
        cut.Find(Id("encounter-add-search")).Input("trap");
        cut.WaitForAssertion(() => Assert.Equal("0 results", cut.Find(Id("encounter-add-count")).TextContent), Patience);
        Assert.Empty(cut.FindAll(Id("encounter-add-creature")));
    }

    [Fact]
    public async Task AddingACreatureTwice_RaisesItsCount_AndRemovingALine_AnnouncesIt()
    {
        var plan = await StoredAsync("Ambush");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-add-creature")).Count == 2, Patience);

        var addBandit = () => cut.FindAll(Id("encounter-add-creature")).First(b => b.GetAttribute("aria-label") == "Add Bandit");
        addBandit().Click();
        addBandit().Click();

        Assert.Equal("2", cut.Find(Id("encounter-line-count")).GetAttribute("value"));
        Assert.Equal("Added Bandit (2)", cut.Find(Id("encounter-announce")).TextContent);

        cut.Find(Id("encounter-line-remove")).Click();

        Assert.Empty(cut.FindAll(Id("encounter-line")));
        Assert.Equal("Removed Bandit", cut.Find(Id("encounter-announce")).TextContent);
        Assert.NotNull(cut.Find(Id("encounter-lineup-empty")));
    }

    [Fact]
    public async Task TheCastTab_OffersTheGmsCharactersOnly_AndAddsOneAsACastLineWithNoCount()
    {
        var plan = await StoredAsync("Ambush");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-tab-cast")).Count > 0, Patience);

        cut.Find(Id("encounter-tab-cast")).Click();

        cut.WaitForAssertion(() => Assert.Equal("2 results", cut.Find(Id("encounter-add-count")).TextContent), Patience);
        var results = cut.Find(Id("encounter-add-results")).TextContent;
        Assert.DoesNotContain("Mira", results);
        Assert.DoesNotContain("Rolling stone", results);
        Assert.Contains("Cellar rat", results);
        cut.Find(Id("encounter-add-cast")).Click();
        var line = cut.Find(Id("encounter-line"));
        Assert.Contains("Wendel Tarr", line.TextContent);
        Assert.Empty(line.QuerySelectorAll(Id("encounter-line-count")));
    }

    [Fact]
    public async Task AddFromTheBestiaryShortlist_AddsEachEntryOnce_WithItsCountAndNote()
    {
        var plan = await StoredAsync("Ambush");
        var shortlist = new BestiaryEntry { EntryType = "monster", EntryId = "wolf", Name = "Wolf", Count = 4, Note = "from the north" };
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id), shortlist: shortlist);
        cut.WaitForState(() => cut.FindAll(Id("encounter-add-shortlist")).Count > 0, Patience);

        cut.Find(Id("encounter-add-shortlist")).Click();
        cut.Find(Id("encounter-add-shortlist")).Click();

        var line = cut.Find(Id("encounter-line"));
        Assert.Equal("4", line.QuerySelector(Id("encounter-line-count"))!.GetAttribute("value"));
        Assert.Equal("from the north", line.QuerySelector(Id("encounter-line-note"))!.GetAttribute("value"));
        Assert.Equal("0 creatures added from the shortlist", cut.Find(Id("encounter-announce")).TextContent);
    }

    [Fact]
    public void SavingANewEncounter_StoresIt_AndOpensItByTheGuidPartOfItsId()
    {
        var cut = Draw("new");
        cut.WaitForState(() => cut.FindAll(Id("encounter-name")).Count > 0, Patience);
        cut.Find(Id("encounter-name")).Input("Ambush");
        cut.Find(Id("encounter-place-select")).Change("r1|vault");
        cut.FindAll(Id("encounter-add-creature")).First(b => b.GetAttribute("aria-label") == "Add Wolf").Click();

        cut.Find(Id("encounter-editor")).Submit();

        var stored = Assert.Single(_w.Plans.Held.Values);
        Assert.Equal(("Ambush", "r1", "vault"), (stored.Name, stored.RegionId, stored.PlaceId));
        Assert.Equal("wolf", Assert.Single(stored.Lines).EntryId);
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith($"/campaigns/c1/encounters/{PreparedEncounterService.RouteOf(stored.Id)}", nav.Uri);
        Assert.DoesNotContain("preparedencounters", nav.Uri);
    }

    [Fact]
    public void ASaveWithAnError_ShowsItAsText_AndStoresNothing()
    {
        var cut = Draw("new");
        cut.WaitForState(() => cut.FindAll(Id("encounter-name")).Count > 0, Patience);
        cut.Find(Id("encounter-name")).Input("Ambush");
        cut.Find(Id("encounter-tab-cast")).Click();
        cut.WaitForState(() => cut.FindAll(Id("encounter-add-cast")).Count > 0, Patience);
        cut.Find(Id("encounter-add-cast")).Click();
        // Make the line's actor a player character after it was added (the page showed an NPC): the check reads the actor as it is now.
        _w.Actors.First(a => a.Id == "npc-1").Kind = ActorKind.PlayerCharacter;

        cut.Find(Id("encounter-editor")).Submit();

        Assert.Empty(_w.Plans.Held);
        Assert.Contains("Wendel Tarr is a player character or a companion. Only characters the GM runs can be in an encounter.", cut.Find(Id("encounter-errors")).TextContent);
    }

    [Fact]
    public async Task ACountBox_ShowsTheClampedCount_AndTheStoredCountAfterUnreadableText()
    {
        var plan = await StoredAsync("Ambush", "cellar", PreparedStatus.Draft, EncounterWorld.Bandits(3));
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-line-count")).Count == 1, Patience);

        cut.Find(Id("encounter-line-count")).Change("25");
        Assert.Equal("20", cut.Find(Id("encounter-line-count")).GetAttribute("value"));

        cut.Find(Id("encounter-line-count")).Change("0");
        Assert.Equal("1", cut.Find(Id("encounter-line-count")).GetAttribute("value"));

        cut.Find(Id("encounter-line-count")).Change("many");
        Assert.Equal("1", cut.Find(Id("encounter-line-count")).GetAttribute("value"));
    }

    [Fact]
    public async Task TheStartsHiddenBox_NamesItsCreature_ToAScreenReader()
    {
        var plan = await StoredAsync("Ambush", "cellar", PreparedStatus.Draft, EncounterWorld.Bandits(3));
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-line-hidden")).Count == 1, Patience);

        Assert.Equal("Starts hidden: Bandit", cut.Find(Id("encounter-line-hidden")).GetAttribute("aria-label"));
        // Laid out by the app's own direction-neutral rule, not Bootstrap's left-padded .form-check (which breaks in right-to-left).
        Assert.NotNull(cut.Find(Id("encounter-line")).QuerySelector(".line-check"));
        Assert.Null(cut.Find(Id("encounter-line")).QuerySelector(".form-check"));
    }

    [Fact]
    public async Task AMoreThanThirtyResultSearch_SaysItShowsTheFirstThirty()
    {
        for (var i = 0; i < 35; i++)
        {
            _w.Registry.RegisterEntry(new Codex.Plugin.Abstractions.ContentEntry { Id = $"orc-{i}", Type = "monster", SystemId = "demo", PackId = "base", Name = $"Orc {i}" }, 0);
        }

        var plan = await StoredAsync("Ambush");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-add-creature")).Count > 0, Patience);

        Assert.Equal(30, cut.FindAll(Id("encounter-add-creature")).Count);
        Assert.Equal("30 results Showing the first 30 results.", cut.Find(Id("encounter-add-count")).TextContent);
        cut.Find(Id("encounter-add-search")).Input("Orc 1");
        cut.WaitForAssertion(() => Assert.Equal("11 results", cut.Find(Id("encounter-add-count")).TextContent), Patience);
    }

    [Fact]
    public async Task Delete_AsksFirst_SaysItCannotBeUndone_AndKeepsThePlanUntilConfirmed()
    {
        var plan = await StoredAsync("Ambush");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-delete")).Count > 0, Patience);

        cut.Find(Id("encounter-delete")).Click();

        cut.WaitForState(() => cut.FindAll(Id("confirm-accept")).Count > 0, Patience);
        Assert.Contains("cannot be undone", cut.Find(".dialog-body").TextContent);
        Assert.Single(_w.Plans.Held);

        cut.Find(Id("confirm-accept")).Click();

        cut.WaitForAssertion(() => Assert.Empty(_w.Plans.Held), Patience);
    }

    [Fact]
    public void ARanPlansStatus_IsReadOnlyText_NotAChoice()
    {
        var plan = _w.Plan("Old fight");
        plan.Status = PreparedStatus.Ran;
        _w.Plans.Held["preparedencounters/ran"] = plan;
        plan.Id = "preparedencounters/ran";

        var cut = Draw("ran");

        cut.WaitForState(() => cut.FindAll(Id("encounter-name")).Count > 0, Patience);
        Assert.NotNull(cut.Find(Id("encounter-status-ran")));
        Assert.Empty(cut.FindAll(Id("encounter-status-select")));
    }

    [Fact]
    public void AnUnknownPlan_ShowsNotFound_WithALinkBack()
    {
        var cut = Draw(Guid.NewGuid().ToString());

        cut.WaitForState(() => cut.FindAll(Id("encounter-missing")).Count > 0, Patience);
        Assert.Contains("/campaigns/c1/encounters", cut.Find(Id("encounter-missing")).InnerHtml);
    }

    [Theory]
    [InlineData("ru", "Новое столкновение")]
    [InlineData("he", "מפגש קרב חדש")]
    public void TheListSpeaksTheReadersLanguage(string culture, string newButton)
    {
        var cut = Draw(culture: culture);

        cut.WaitForState(() => cut.FindAll(Id("encounter-new")).Count > 0, Patience);
        Assert.Contains(newButton, cut.Find(Id("encounter-new")).TextContent);
    }
}
