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

/// <summary>What a screen reader and a keyboard get from the encounter pages (ENCOUNTER_BUILDER_DESIGN.md 5.3, 20): names, lists, the one live line, where focus goes when the button
/// that held it goes away, and the table's picker. The browser's own focus is not run here; the page's asks of it (<c>codex.focusById</c>) are read from bUnit's JS interop log.</summary>
public class EncounterAccessibilityTests : ComponentTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly EncounterWorld _w = new();

    public EncounterAccessibilityTests()
    {
        Services.AddSingleton(_w.Service);
        Services.AddSingleton<ILogger<EncounterList>>(NullLogger<EncounterList>.Instance);
        Services.AddSingleton<ILogger<EncounterEditor>>(NullLogger<EncounterEditor>.Instance);
        Services.AddSingleton<ILogger<PreparedEncounterPicker>>(NullLogger<PreparedEncounterPicker>.Instance);
        Services.AddSingleton(ViewerClock.ForZone("UTC"));
        var components = new ComponentRegistry();
        Services.AddSingleton(new Codex.Persistence.Runtime.CampaignRuntimeManager(_w.ActorRepo, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(), components, new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>()), NullLoggerFactory.Instance));
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

    private async Task<PreparedEncounterDocument> StoredAsync(string name, string? place = "hall", PreparedStatus status = PreparedStatus.Ready, params PreparedLine[] lines)
    {
        var plan = _w.Plan(name, place, lines);
        plan.Status = status;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        return plan;
    }

    private static PreparedLine Wolves(int count = 1) => new() { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "wolf", Name = "Wolf", Count = count };

    /// <summary>The ids the page has asked the browser to focus, in order.</summary>
    private List<string> Focused(string function = "codex.focusById") =>
        JSInterop.Invocations.Where(i => i.Identifier == function).Select(i => (string)i.Arguments[0]!).ToList();

    private void ComplexHazards()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "blade", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Swinging blade", Properties = { ["complex"] = true } }, 0);
        _w.RegionRepo.GetAsync("r1", Arg.Any<CampaignAccess>()).Returns(_w.Region);
        _w.StubPlacing();
    }

    // ---- The lineup ----

    [Fact]
    public async Task TheLineup_IsAListNamedByItsHeading_AndEveryControlNamesItsCreature()
    {
        var plan = await StoredAsync("Ambush", "hall", PreparedStatus.Ready, EncounterWorld.Bandits(2), Wolves());
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 2, Patience);

        var list = cut.Find(Id("encounter-lineup"));
        Assert.Equal("UL", list.TagName);
        Assert.Equal("list", list.GetAttribute("role"));
        Assert.Equal("Creatures", cut.Find($"#{list.GetAttribute("aria-labelledby")}").TextContent);

        foreach (var (line, name) in cut.FindAll(Id("encounter-line")).Zip(new[] { "Bandit", "Wolf" }))
        {
            Assert.Equal($"How many {name}", cut.Find($"label[for={line.QuerySelector(Id("encounter-line-count"))!.Id}]").TextContent);
            Assert.Equal($"Note for {name}", cut.Find($"label[for={line.QuerySelector(Id("encounter-line-note"))!.Id}]").TextContent);
            Assert.Equal($"Starts hidden: {name}", line.QuerySelector(Id("encounter-line-hidden"))!.GetAttribute("aria-label"));
            Assert.Equal($"Remove {name}", line.QuerySelector(Id("encounter-line-remove"))!.GetAttribute("aria-label"));
        }
    }

    [Fact]
    public async Task RemovingALine_MovesFocusToTheNextLinesRemoveButton_ThenThePreviousOne_ThenTheHeading()
    {
        var plan = await StoredAsync("Ambush", "hall", PreparedStatus.Ready, EncounterWorld.Bandits(2), Wolves(), EncounterWorld.Cast("npc-1", "Wendel Tarr"));
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-line")).Count == 3, Patience);
        // The ids the page asks for are the ones its buttons carry.
        Assert.Equal(new[] { "encounter-line-0-remove", "encounter-line-1-remove", "encounter-line-2-remove" }, cut.FindAll(Id("encounter-line-remove")).Select(b => b.Id));

        cut.FindAll(Id("encounter-line-remove"))[1].Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "encounter-line-1-remove" }, Focused()), Patience);   // the cast line has taken its place

        cut.FindAll(Id("encounter-line-remove"))[1].Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "encounter-line-1-remove", "encounter-line-0-remove" }, Focused()), Patience);   // it was the last: the one before

        cut.FindAll(Id("encounter-line-remove"))[0].Click();
        cut.WaitForAssertion(() => Assert.Equal("encounter-lineup-heading", Focused().Last()), Patience);
        // The heading can take it.
        Assert.Equal("-1", cut.Find("#encounter-lineup-heading").GetAttribute("tabindex"));
        Assert.Equal("Removed Bandit", cut.Find(Id("encounter-announce")).TextContent);
        Assert.Equal("polite", cut.Find(Id("encounter-announce")).GetAttribute("aria-live"));
    }

    [Fact]
    public async Task TheAddPanel_AnnouncesItsCountAndItsOutcomes_InPoliteRegions_AndItsResultsAreLists()
    {
        var plan = await StoredAsync("Ambush", "hall");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-add-creature")).Count == 2, Patience);

        Assert.Equal("polite", cut.Find(Id("encounter-add-count")).GetAttribute("aria-live"));
        Assert.Equal("status", cut.Find(Id("encounter-add-count")).GetAttribute("role"));
        Assert.Equal("encounter-add-count", cut.Find(Id("encounter-add-search")).GetAttribute("aria-describedby"));
        Assert.Equal("list", cut.Find(Id("encounter-add-results")).GetAttribute("role"));
        Assert.Equal("status", cut.Find(Id("encounter-announce")).GetAttribute("role"));
        // A button that says "Add" is named "Add <creature>": the name holds the words on the button.
        var button = cut.FindAll(Id("encounter-add-creature")).First();
        Assert.Contains(button.TextContent.Trim(), button.GetAttribute("aria-label"));
    }

    // ---- Hazards ----

    [Fact]
    public async Task AHazardsAddButton_IsNamedWithItsVisibleWord_AndTakingBackTheWaitingHazardMovesFocusToThePanelHeading()
    {
        ComplexHazards();
        var plan = await StoredAsync("Ambush", "hall");
        var cut = Draw(PreparedEncounterService.RouteOf(plan.Id));
        cut.WaitForState(() => cut.FindAll(Id("encounter-tab-hazards")).Count > 0, Patience);
        cut.Find(Id("encounter-tab-hazards")).Click();
        cut.WaitForState(() => cut.FindAll(Id("encounter-add-hazard")).Count == 1, Patience);

        var add = cut.Find(Id("encounter-add-hazard"));
        Assert.Equal("Add Swinging blade to the place", add.GetAttribute("aria-label"));
        Assert.Contains(add.TextContent.Trim(), add.GetAttribute("aria-label"));
        add.Click();
        Assert.Equal("list", cut.Find(Id("encounter-hazards")).QuerySelector("ul")!.GetAttribute("role"));
        Assert.Equal("Don't place Swinging blade", cut.Find(Id("encounter-hazard-pending-remove")).GetAttribute("aria-label"));

        cut.Find(Id("encounter-hazard-pending-remove")).Click();

        cut.WaitForAssertion(() => Assert.Equal("encounter-hazards-heading", Focused().Last()), Patience);
        Assert.Equal("-1", cut.Find("#encounter-hazards-heading").GetAttribute("tabindex"));
    }

    // ---- The list ----

    [Fact]
    public async Task TheListOfEncounters_IsANamedList_AndTheBandAndStatusPillsAreWords()
    {
        await StoredAsync("Ambush", "hall", PreparedStatus.Ready, EncounterWorld.Bandits(2));
        var cut = Draw(null);
        cut.WaitForState(() => cut.FindAll(Id("encounter-row")).Count == 1, Patience);

        var list = cut.Find(Id("encounter-list"));
        Assert.Equal("list", list.GetAttribute("role"));
        Assert.Equal("Encounters", list.GetAttribute("aria-label"));
        // Colour is never the only signal: the status is a word in its pill, and the count is a polite status.
        Assert.Equal("Ready", cut.Find(Id("encounter-status")).TextContent);
        Assert.Equal("status", cut.Find(Id("encounter-count")).GetAttribute("role"));
        // The icon on the row is decoration.
        Assert.Equal("true", cut.Find(Id("encounter-row")).QuerySelector(".avatar")!.GetAttribute("aria-hidden"));
    }

    // ---- The table's picker ----

    [Fact]
    public async Task ThePicker_NamesEachButtonWithItsVisibleName_AndIsANamedList()
    {
        await StoredAsync("Alpha", "hall", PreparedStatus.Ready, EncounterWorld.Bandits(2));
        var cut = Render<PreparedEncounterPicker>(p => p.Add(x => x.Campaign, _w.Campaign).Add(x => x.Access, _w.Gm));
        cut.WaitForState(() => cut.FindAll(Id("start-prepared-plan")).Count == 1, Patience);

        var button = cut.Find(Id("start-prepared-plan"));
        Assert.Equal("Start Alpha", button.GetAttribute("aria-label"));
        Assert.Contains(button.TextContent.Trim(), button.GetAttribute("aria-label"));
        Assert.Equal("start-prepared-0", button.Id);
        var list = button.ParentElement!.ParentElement!;
        Assert.Equal("list", list.GetAttribute("role"));
        Assert.Equal("Prepared encounters", list.GetAttribute("aria-label"));
    }

    // ---- Right to left ----

    [Fact]
    public void TheEncounterPages_UseLogicalSidesOnly()
    {
        // The encounter pages draw with the app's logical-property rules (.line-check, .row-actions, .count-input): no left/right in what they own.
        var css = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "app.css"));
        foreach (var rule in new[] { ".line-check", ".count-input", ".inline-input", ".encounter-bar" })
        {
            var at = css.IndexOf(rule + " ", StringComparison.Ordinal);
            Assert.True(at >= 0, rule);
            var body = css[at..css.IndexOf('}', at)];
            Assert.DoesNotMatch(@"(^|[\s;{])(margin|padding|border)?-?(left|right)\s*:|text-align:\s*(left|right)|float:\s*(left|right)", body);
        }

        foreach (var file in new[] { "EncounterEditor.razor", "EncounterList.razor" })
        {
            var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "Components", "Campaign", file));
            Assert.DoesNotMatch(@"\b(ml|mr|pl|pr)-(\d|auto)|text-(left|right)|float-(left|right)", source);
        }
    }
}
