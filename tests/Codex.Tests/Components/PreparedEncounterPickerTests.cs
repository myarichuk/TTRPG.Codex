using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Components;

/// <summary>The table's list of encounters ready to start (ENCOUNTER_BUILDER_DESIGN.md 5.2): Ready and Ran plans, never a Draft, each a button with its own name.</summary>
public class PreparedEncounterPickerTests : ComponentTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly EncounterWorld _w = new();

    private IRenderedComponent<PreparedEncounterPicker> Draw(string? placeId = null)
    {
        Services.AddSingleton(_w.Service);
        Services.AddSingleton(Localizer.For("en"));
        Services.AddSingleton<ILogger<PreparedEncounterPicker>>(NullLogger<PreparedEncounterPicker>.Instance);
        return Render<PreparedEncounterPicker>(p => p
            .Add(x => x.Campaign, _w.Campaign)
            .Add(x => x.Access, _w.Gm)
            .Add(x => x.PlaceId, placeId));
    }

    private async Task StoredAsync(string name, string place, PreparedStatus status)
    {
        var plan = _w.Plan(name, place, EncounterWorld.Bandits(2));
        plan.Status = status;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
    }

    [Fact]
    public async Task ItListsReadyPlansAsButtons_LeavesDraftsOut_AndPutsThePlaceInUseFirst()
    {
        await StoredAsync("Alpha", "hall", PreparedStatus.Ready);
        await StoredAsync("Beta", "cellar", PreparedStatus.Ready);
        await StoredAsync("Gamma", "cellar", PreparedStatus.Draft);

        var cut = Draw("cellar");

        cut.WaitForState(() => cut.FindAll(Id("start-prepared-plan")).Count == 2, Patience);
        var buttons = cut.FindAll(Id("start-prepared-plan"));
        Assert.Equal(new[] { "Beta", "Alpha" }, buttons.Select(b => b.TextContent.Trim()));
        Assert.Equal("Start Beta", buttons[0].GetAttribute("aria-label"));
        Assert.Equal("UL", buttons[0].ParentElement!.ParentElement!.TagName);
        Assert.DoesNotContain("Gamma", cut.Markup);
    }

    [Fact]
    public async Task TheConfirmCountsTheComplexHazardsThatJoin_AndOnlyThem()
    {
        _w.Registry.RegisterEntry(new Codex.Plugin.Abstractions.ContentEntry { Id = "press", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Crushing press", Properties = { ["complex"] = true } }, 0);
        _w.Region.Locations.Single(l => l.Id == "vault").Feature!.Hazard!.Entry = "hazard/press";
        _w.Actors.RemoveAll(a => a.Id == "stone-1");
        await StoredAsync("Alpha", "vault", PreparedStatus.Ready);
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("start-prepared-plan")).Count == 1, Patience);

        cut.Find(Id("start-prepared-plan")).Click();

        cut.WaitForState(() => cut.FindAll(Id("confirm-accept")).Count > 0, Patience);
        Assert.Equal("Start Alpha? 2 creatures and 1 hazard join the fight and roll initiative.", cut.Find(".dialog-body p").TextContent);
    }

    [Fact]
    public async Task WithNoHazardThatJoins_TheConfirmIsTheOldSentence()
    {
        await StoredAsync("Alpha", "cellar", PreparedStatus.Ready);
        var cut = Draw();
        cut.WaitForState(() => cut.FindAll(Id("start-prepared-plan")).Count == 1, Patience);

        cut.Find(Id("start-prepared-plan")).Click();

        cut.WaitForState(() => cut.FindAll(Id("confirm-accept")).Count > 0, Patience);
        Assert.Equal("Start Alpha? 2 creatures join the fight and roll initiative.", cut.Find(".dialog-body p").TextContent);
    }

    [Theory]
    [InlineData("en", 3, 1, "Start Ambush? 3 creatures and 1 hazard join the fight and roll initiative.")]
    [InlineData("en", 1, 4, "Start Ambush? 1 creature and 4 hazards join the fight and roll initiative.")]
    [InlineData("en", 0, 1, "Start Ambush? One hazard joins the fight and rolls initiative.")]
    [InlineData("en", 0, 2, "Start Ambush? 2 hazards join the fight and roll initiative.")]
    [InlineData("en", 2, 0, "Start Ambush? 2 creatures join the fight and roll initiative.")]
    [InlineData("ru", 2, 1, "Начать «Ambush»? В бой вступают 2 существа и 1 опасность. Все бросают инициативу.")]
    [InlineData("ru", 5, 3, "Начать «Ambush»? В бой вступают 5 существ и 3 опасности. Все бросают инициативу.")]
    [InlineData("ru", 0, 5, "Начать «Ambush»? В бой вступают 5 опасностей и бросают инициативу.")]
    [InlineData("he", 3, 1, "להתחיל את Ambush? 3 יצורים וסכנה אחת מצטרפים לקרב ומטילים יוזמה.")]
    public void TheConfirmSentence_IsRightInEachLanguage(string culture, int creatures, int hazards, string expected) =>
        Assert.Equal(expected, PreparedConfirm.Text(Localizer.For(culture), "Ambush", creatures, hazards));

    [Fact]
    public void WithNoPlansReady_ItSaysSo_AndLinksToTheEncounters()
    {
        var cut = Draw();

        cut.WaitForAssertion(() => Assert.Contains("No encounters are ready to start.", cut.Markup), Patience);
        Assert.Equal("/campaigns/c1/encounters", cut.Find("a").GetAttribute("href"));
        Assert.Empty(cut.FindAll(Id("start-prepared-plan")));
    }
}
