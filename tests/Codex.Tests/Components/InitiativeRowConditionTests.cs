using Bunit;
using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>A condition set on the sheet page before the table ran (Bless on the sheet's own list) shows on the creature's row beside the table's
/// statuses, once, says where it came from, and the GM's × takes it off with the table's remove command, by the rules' name.</summary>
public class InitiativeRowConditionTests : ComponentTestBase
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    public InitiativeRowConditionTests()
    {
        var registry = new ContentRegistry();
        Services.AddSingleton<IContentRegistry>(registry);
        Services.AddSingleton(new CampaignLibrary(registry, null!, null!));
    }

    private IRenderedComponent<InitiativeRow> Draw(ActorStateSnapshot state, bool dm = true, List<string>? removed = null, string culture = "en")
    {
        Services.AddSingleton(Localizer.For(culture));
        return Render<InitiativeRow>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "camp-1", SystemId = "srd5e" })
            .Add(x => x.Participant, new ParticipantSnapshot("ash", 12, 0, TurnState.Normal, false))
            .Add(x => x.Name, "Ash")
            .Add(x => x.State, state)
            .Add(x => x.Definition, System.Localize(culture))
            .Add(x => x.IsDm, dm)
            .Add(x => x.OnRemoveCondition, EventCallback.Factory.Create<string>(this, id => removed?.Add(id))));
    }

    private static ActorStateSnapshot State(string[] onSheet, params string[] statuses) =>
        new(new Dictionary<string, int>(), statuses.Select(s => new EffectSnapshot(s, "ash", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn)).ToArray(),
            SheetConditions: onSheet);

    [Fact]
    public void ASheetSetCondition_ShowsOnTheRow_SayingWhereItCameFrom()
    {
        var cut = Draw(State(new[] { "Bless" }));
        var chip = cut.Find("[data-on-sheet='true']");
        Assert.Contains("Bless", chip.TextContent);
        Assert.Equal("Set on the character sheet. Until removed.", chip.GetAttribute("title"));
        Assert.Equal("condition-badge", chip.GetAttribute("data-testid"));
    }

    [Fact]
    public void TheGmsCross_RemovesItByTheRulesName()
    {
        var removed = new List<string>();
        var cut = Draw(State(new[] { "Bless" }), removed: removed, culture: "ru");
        var button = cut.Find("[data-on-sheet='true'] button");
        Assert.Contains("Благословение", button.GetAttribute("aria-label"));
        button.Click();
        Assert.Equal(new[] { "Bless" }, removed);
    }

    [Fact]
    public void APlayer_SeesItWithoutTheCross()
    {
        var cut = Draw(State(new[] { "Bless" }), dm: false);
        Assert.Empty(cut.FindAll("[data-on-sheet='true'] button"));
        Assert.Single(cut.FindAll("[data-on-sheet='true']"));
    }

    [Fact]
    public void ACondition_BothOnTheSheetAndAStatus_ShowsOnce()
    {
        var cut = Draw(State(new[] { "Bless" }, "Bless"));
        Assert.Single(cut.FindAll(Id("condition-badge")));
        Assert.Empty(cut.FindAll("[data-on-sheet='true']"));
    }

    [Fact]
    public void ExhaustionAndTheRowsStateMarkers_OnTheSheetsList_AreNotChips()
    {
        var cut = Draw(State(new[] { "Exhaustion", System.Vitals!.Down!.DeadCondition }));
        Assert.Empty(cut.FindAll("[data-on-sheet='true']"));
    }

    [Theory]
    [InlineData("he", "ברכה", "מדף הדמות. עד להסרה.")]
    [InlineData("ru", "Благословение", "Задано в листе персонажа. Пока не снято.")]
    public void TheChip_IsInTheReadersLanguage(string culture, string name, string title)
    {
        var chip = Draw(State(new[] { "Bless" }), culture: culture).Find("[data-on-sheet='true']");
        Assert.Contains(name, chip.TextContent);
        Assert.Equal(title, chip.GetAttribute("title"));
    }
}
