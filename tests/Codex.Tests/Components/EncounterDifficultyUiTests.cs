using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Campaign;
using Codex.Web.Components.Pages;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>The difficulty readout on the page (ENCOUNTER_BUILDER_DESIGN.md 16): the editor's section, which follows the lineup as it changes and can rate for another party, and the band word
/// beside a plan on the list and in the table's picker. Two characters of level 2 in the <c>difficulty-sum</c> fixture have 30/60/90 to spend; a goblin is worth 10 (invented numbers).</summary>
public class EncounterDifficultyUiTests : ComponentTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly EncounterWorld _w = new(system: EncounterWorld.Fixture("difficulty-sum"));

    public EncounterDifficultyUiTests()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "goblin", Type = "monster", SystemId = "demo", PackId = "base", Name = "Goblin", Properties = { ["worth"] = 10L } }, 0);
        _w.Actors.Clear();
        foreach (var (id, name) in new[] { ("pc-1", "Mira"), ("pc-2", "Tor") })
        {
            _w.Actors.Add(new ActorDocument { Id = id, CampaignId = "c1", Name = name, Kind = ActorKind.PlayerCharacter, Sheet = { ["kind"] = "hero", ["level"] = 2L } });
        }

        Services.AddSingleton(_w.Service);
        Services.AddSingleton<ILogger<EncounterList>>(NullLogger<EncounterList>.Instance);
        Services.AddSingleton<ILogger<EncounterEditor>>(NullLogger<EncounterEditor>.Instance);
        Services.AddSingleton<ILogger<PreparedEncounterPicker>>(NullLogger<PreparedEncounterPicker>.Instance);
        Services.AddSingleton(ViewerClock.ForZone("UTC"));
        var components = new ComponentRegistry();
        Services.AddSingleton(new Codex.Persistence.Runtime.CampaignRuntimeManager(_w.ActorRepo, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(), components, new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>()), NullLoggerFactory.Instance));
    }

    private static PreparedLine Goblins(int count) => new() { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "goblin", Name = "Goblin", Count = count };

    private async Task<PreparedEncounterDocument> StoredAsync(string name, PreparedStatus status = PreparedStatus.Ready, params PreparedLine[] lines)
    {
        var plan = _w.Plan(name, "cellar", lines);
        plan.Status = status;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        return plan;
    }

    private IRenderedComponent<CascadingValue<CampaignContext>> Draw(string? route, string culture = "en")
    {
        Services.AddSingleton(Localizer.For(culture));
        var ctx = new CampaignContext
        {
            Campaign = _w.Campaign, Access = _w.Gm, UserId = _w.Gm.UserId, Actors = Array.Empty<ActorDocument>(), AllActors = Array.Empty<ActorDocument>(),
            ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
        };
        return Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx).AddChildContent<EncountersSection>(c => c.Add(x => x.Route, route)));
    }

    private string Summary(IRenderedComponent<CascadingValue<CampaignContext>> cut) => cut.Find(Id("encounter-difficulty-summary")).TextContent.Trim();

    [Fact]
    public async Task TheEditor_SaysInWords_TheBandTheTotalAndTheParty_AndListsEveryBandsBudget()
    {
        var plan = await StoredAsync("Ambush", PreparedStatus.Ready, Goblins(2));

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty-summary")).Count > 0, Patience);
        Assert.Equal("Low: 20 pts for 2 characters of level 2", Summary(cut));
        Assert.Equal("Bands for this party: Low up to 30 · Moderate up to 60 · High up to 90", cut.Find(Id("encounter-difficulty-bands")).TextContent.Trim());
        Assert.Empty(cut.FindAll(Id("encounter-difficulty-notcounted")));
        Assert.Contains("Difficulty", cut.Find(Id("encounter-difficulty")).TextContent);
    }

    [Fact]
    public async Task TheReadout_FollowsTheLineup_WithoutSaving_AndTheLiveRegionSaysOneLineAfterTheChangesStop()
    {
        var plan = await StoredAsync("Ambush", PreparedStatus.Ready, Goblins(2));
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 1, Patience);
        Assert.Equal("Low: 20 pts for 2 characters of level 2", cut.Find(Id("encounter-difficulty-live")).TextContent.Trim());

        cut.Find(Id("encounter-line-count")).Change("5");

        cut.WaitForAssertion(() => Assert.Equal("Moderate: 50 pts for 2 characters of level 2", Summary(cut)), Patience);
        // The page changed at once; the live region still has the old line, and only says the new one once the changes have stopped.
        Assert.Equal("Low: 20 pts for 2 characters of level 2", cut.Find(Id("encounter-difficulty-live")).TextContent.Trim());
        cut.WaitForAssertion(() => Assert.Equal("Moderate: 50 pts for 2 characters of level 2", cut.Find(Id("encounter-difficulty-live")).TextContent.Trim()), Patience);
        Assert.Equal("polite", cut.Find(Id("encounter-difficulty-live")).GetAttribute("aria-live"));
        Assert.Equal("status", cut.Find(Id("encounter-difficulty-live")).GetAttribute("role"));
        // Nothing was saved: the stored plan still has two.
        Assert.Equal(2, _w.Plans.Held[plan.Id].Lines[0].Count);

        cut.Find(Id("encounter-line-remove")).Click();

        cut.WaitForAssertion(() => Assert.Equal("Nothing counted yet", Summary(cut)), Patience);
    }

    [Fact]
    public async Task ALineThatCannotBeValued_IsNamedWithItsReason_AndTheBandIsStillGiven()
    {
        var plan = await StoredAsync("Ambush", PreparedStatus.Ready, Goblins(1), EncounterWorld.Bandits(2));

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty-notcounted")).Count > 0, Patience);
        Assert.Equal("Low: 10 pts for 2 characters of level 2", Summary(cut));
        var skipped = cut.Find(Id("encounter-difficulty-notcounted"));
        Assert.Contains("1 line not counted", skipped.TextContent);
        Assert.Contains("Bandit has no Worth", skipped.TextContent);
    }

    [Fact]
    public async Task RateForADifferentParty_IsADisclosure_ThatChangesTheReadoutOnly_AndCanGoBack()
    {
        var plan = await StoredAsync("Ambush", PreparedStatus.Ready, Goblins(5));
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty-toggle")).Count > 0, Patience);
        Assert.Equal("Moderate: 50 pts for 2 characters of level 2", Summary(cut));
        var toggle = cut.Find(Id("encounter-difficulty-toggle"));
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(Id("encounter-difficulty-different")));

        toggle.Click();

        Assert.Equal("true", cut.Find(Id("encounter-difficulty-toggle")).GetAttribute("aria-expanded"));
        // Opened, the boxes say the party the readout is for now.
        Assert.Equal("2", cut.Find(Id("encounter-difficulty-size")).GetAttribute("value"));
        Assert.Equal("2", cut.Find(Id("encounter-difficulty-level")).GetAttribute("value"));
        Assert.Equal("Characters", cut.Find("label[for=encounter-difficulty-size]").TextContent);
        Assert.Equal("Level", cut.Find("label[for=encounter-difficulty-level]").TextContent);
        Assert.Equal("1", cut.Find(Id("encounter-difficulty-level")).GetAttribute("min"));
        Assert.Equal("3", cut.Find(Id("encounter-difficulty-level")).GetAttribute("max"));

        cut.Find(Id("encounter-difficulty-size")).Change("4");

        // Four characters of level 2: 60/120/180.
        cut.WaitForAssertion(() => Assert.Equal("Low: 50 pts for 4 characters of level 2", Summary(cut)), Patience);
        Assert.Equal("Rating for a different party: 4 characters of level 2.", cut.Find(Id("encounter-difficulty-source")).TextContent.Trim());
        Assert.Equal("Bands for this party: Low up to 60 · Moderate up to 120 · High up to 180", cut.Find(Id("encounter-difficulty-bands")).TextContent.Trim());

        cut.Find(Id("encounter-difficulty-reset")).Click();

        cut.WaitForAssertion(() => Assert.Equal("Moderate: 50 pts for 2 characters of level 2", Summary(cut)), Patience);
        Assert.Empty(cut.FindAll(Id("encounter-difficulty-source")));
    }

    [Fact]
    public async Task ANumberTypedPastTheLimit_IsClamped_AndTheBoxShowsTheNumberTheReadoutUses()
    {
        var plan = await StoredAsync("Ambush", PreparedStatus.Ready, Goblins(1));
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty-toggle")).Count > 0, Patience);
        cut.Find(Id("encounter-difficulty-toggle")).Click();

        cut.Find(Id("encounter-difficulty-size")).Change("99");

        cut.WaitForAssertion(() => Assert.Contains("for 20 characters of level 2", Summary(cut)), Patience);
        cut.WaitForAssertion(() => Assert.Equal("20", cut.Find(Id("encounter-difficulty-size")).GetAttribute("value")), Patience);
        // Twenty characters of level 2: 20 x 15 and so on.
        Assert.Equal("Bands for this party: Low up to 300 · Moderate up to 600 · High up to 900", cut.Find(Id("encounter-difficulty-bands")).TextContent.Trim());
    }

    [Fact]
    public async Task WithNoCharactersInTheCampaign_TheFormIsOpen_AndFilled_AndTheReadoutSaysWhoItRatesFor()
    {
        _w.Actors.Clear();
        var plan = await StoredAsync("Ambush", PreparedStatus.Ready, Goblins(5));

        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty-different")).Count > 0, Patience);
        Assert.Equal("No characters in this campaign yet; rating for 4 characters of level 1.", cut.Find(Id("encounter-difficulty-source")).TextContent.Trim());
        Assert.Equal("4", cut.Find(Id("encounter-difficulty-size")).GetAttribute("value"));
        Assert.Equal("1", cut.Find(Id("encounter-difficulty-level")).GetAttribute("value"));
        // 4 x 10/20/30 = 40/80/120: 50 is the second band.
        Assert.Equal("Moderate: 50 pts for 4 characters of level 1", Summary(cut));
    }

    [Fact]
    public async Task ASystemWithNoBlock_ShowsNoDifficultyAtAll_NotEvenABadge()
    {
        var plain = new EncounterWorld();
        Services.AddSingleton(plain.Service);
        var plan = plain.Plan("Ambush", "cellar", EncounterWorld.Bandits(2));
        Assert.True((await plain.Service.SaveAsync(plain.Campaign, plain.Gm, plan)).Saved);
        Services.AddSingleton(Localizer.For("en"));
        var ctx = new CampaignContext
        {
            Campaign = plain.Campaign, Access = plain.Gm, UserId = "gm", Actors = Array.Empty<ActorDocument>(), AllActors = Array.Empty<ActorDocument>(),
            ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
        };

        var cut = Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx).AddChildContent<EncountersSection>(c => c.Add(x => x.Route, PreparedEncounterService.RouteOf(plan.Id))));

        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 1, Patience);
        Assert.Empty(cut.FindAll(Id("encounter-difficulty")));
        Assert.DoesNotContain("Difficulty", cut.Markup);
        Assert.DoesNotContain("not counted", cut.Markup);
    }

    [Fact]
    public async Task TheList_ShowsTheBandWordBesideEachPlan_AndNothingForAPlanWithNothingCounted()
    {
        await StoredAsync("Small", PreparedStatus.Ready, Goblins(2));
        await StoredAsync("Big", PreparedStatus.Draft, Goblins(8));
        await StoredAsync("Empty", PreparedStatus.Draft);

        var cut = Draw(null);

        cut.WaitForState(() => cut.FindAll(Id("encounter-row")).Count == 3, Patience);
        var rows = cut.FindAll(Id("encounter-row")).ToDictionary(r => r.QuerySelector(Id("encounter-open"))!.TextContent);
        Assert.Equal("Difficulty: Low", rows["Small"].QuerySelector(Id("encounter-difficulty"))!.TextContent.Trim());
        // 80 of 30/60/90 is the high band; the word has no colour to carry it.
        Assert.Equal("Difficulty: High", rows["Big"].QuerySelector(Id("encounter-difficulty"))!.TextContent.Trim());
        Assert.Null(rows["Empty"].QuerySelector(Id("encounter-difficulty")));
    }

    [Fact]
    public async Task TheList_SaysAboveTheLastBand_WhenTheTotalPassesIt()
    {
        await StoredAsync("Huge", PreparedStatus.Ready, Goblins(20));

        var cut = Draw(null);

        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty")).Count == 1, Patience);
        Assert.Equal("Difficulty: Above High", cut.Find(Id("encounter-difficulty")).TextContent.Trim());
    }

    [Fact]
    public async Task TheListInRussian_UsesTheTranslatedBandWords()
    {
        await StoredAsync("Small", PreparedStatus.Ready, Goblins(2));

        var cut = Draw(null, "ru");

        cut.WaitForState(() => cut.FindAll(Id("encounter-difficulty")).Count == 1, Patience);
        Assert.Equal("Сложность: Лёгкое", cut.Find(Id("encounter-difficulty")).TextContent.Trim());
    }

    [Fact]
    public async Task ThePicker_ShowsTheBandWordBesideAPlan()
    {
        await StoredAsync("Small", PreparedStatus.Ready, Goblins(5));
        Services.AddSingleton(Localizer.For("en"));

        var cut = Render<PreparedEncounterPicker>(p => p.Add(x => x.Campaign, _w.Campaign).Add(x => x.Access, _w.Gm));

        cut.WaitForState(() => cut.FindAll(Id("prepared-difficulty")).Count == 1, Patience);
        Assert.Equal("Difficulty: Moderate", cut.Find(Id("prepared-difficulty")).TextContent.Trim());
    }
}
