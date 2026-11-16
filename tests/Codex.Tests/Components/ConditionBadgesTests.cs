using Bunit;
using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>P4b (c) of docs/CONDITION_IMPLIES_DESIGN.md: the initiative row shows a level badge for every valued condition that holds (not only Exhaustion),
/// a condition another brings shows with what brings it and no remove button, and a printed creature's card shows its numbers with the conditions applied.</summary>
public class ConditionBadgesTests : ComponentTestBase
{
    private static SystemDefinition Srd(string culture = "en")
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        system.ConditionEffects["Frightened"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = "4" },
            Modifiers = { new ModifierDefinition { Target = "armorClass", Amount = "-value", Type = "status" } }
        };
        system.ConditionEffects["Dread"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = "4" },
            Implies = { new ConditionImplication { Condition = "Frightened", Value = "value" } }
        };
        system.SheetRules!.Printed = new List<PrintedNumberDefinition> { new() { From = "armorClass", Target = "armorClass" } };
        return culture == "en" ? system : system.Localize(culture);
    }

    public ConditionBadgesTests()
    {
        var registry = new ContentRegistry();
        Services.AddSingleton<IContentRegistry>(registry);
        Services.AddSingleton(new CampaignLibrary(registry, null!, null!));
    }

    private static ActorStateSnapshot State(IReadOnlyDictionary<string, int>? pools = null, params string[] statuses) =>
        new(pools ?? new Dictionary<string, int>(), statuses.Select(s => new EffectSnapshot(s, "ash", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn)).ToArray());

    private IRenderedComponent<InitiativeRow> Row(ActorStateSnapshot state, string culture = "en", bool dm = true)
    {
        Services.AddSingleton(Localizer.For(culture));
        return Render<InitiativeRow>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "camp-1", SystemId = "srd5e" })
            .Add(x => x.Participant, new ParticipantSnapshot("ash", 12, 0, TurnState.Normal, false))
            .Add(x => x.Name, "Ash")
            .Add(x => x.Actor, new ActorDocument { Id = "ash", Name = "Ash", Sheet = new Dictionary<string, object>() })
            .Add(x => x.State, state)
            .Add(x => x.Definition, Srd(culture))
            .Add(x => x.IsDm, dm));
    }

    /// <summary>A chip's words: the last line of its text, after an icon's fallback letter.</summary>
    private static string Label(AngleSharp.Dom.IElement chip) => chip.TextContent.Trim().Split('\n').Last().Trim();

    [Fact]
    public void EveryValuedConditionThatHolds_ShowsItsLevel_NotOnlyExhaustion()
    {
        var cut = Row(State(new Dictionary<string, int> { ["Exhaustion"] = 3, ["condition_frightened"] = 2 }));

        var frightened = cut.Find(Id("valued-badge"));
        Assert.Equal("Frightened 2", frightened.TextContent.Trim());
        Assert.Equal("2", frightened.GetAttribute("data-level"));
        Assert.Equal("Frightened, level 2", frightened.GetAttribute("title"));
        Assert.Equal("Exhaustion 3", cut.Find(Id("exhaustion-badge")).TextContent.Trim());
    }

    [Fact]
    public void TheBadge_ShowsTheLevelAsHeld_WhenAnotherConditionBringsMore()
    {
        // Frightened stored at 1 and held at 3 by Dread 3 reads 3, and is not also an implied chip.
        var cut = Row(State(new Dictionary<string, int> { ["condition_dread"] = 3, ["condition_frightened"] = 1 }));

        Assert.Equal(new[] { "Dread 3", "Frightened 3, from Dread" }, cut.FindAll(Id("valued-badge")).Select(Label).Order().ToArray());
        Assert.Empty(cut.FindAll("[data-implied='true']"));
    }

    [Fact]
    public void AValuedConditionOnlyAnotherHolds_ShowsOnceAsAnImpliedChip_WithItsLevel()
    {
        var cut = Row(State(new Dictionary<string, int> { ["condition_dread"] = 2 }));

        Assert.Equal("Dread 2", Label(Assert.Single(cut.FindAll(Id("valued-badge")))));
        Assert.Equal("Frightened 2, from Dread", Assert.Single(cut.FindAll("[data-implied='true']")).GetAttribute("aria-label"));
    }

    [Fact]
    public void AValuedConditionAtZero_ShowsNoBadge()
    {
        var cut = Row(State(new Dictionary<string, int> { ["Exhaustion"] = 0, ["condition_frightened"] = 0 }));

        Assert.Empty(cut.FindAll(Id("valued-badge")));
        Assert.Empty(cut.FindAll(Id("exhaustion-badge")));
    }

    [Fact]
    public void AConditionAnotherBrings_ShowsWhatBringsIt_AndHasNoRemoveButton()
    {
        var cut = Row(State(null, "Stunned"));

        var implied = cut.FindAll("[data-implied='true']");
        Assert.Equal("Incapacitated", Assert.Single(implied).GetAttribute("data-effect-id"));
        Assert.Equal("Incapacitated, from Stunned", implied[0].GetAttribute("aria-label"));
        Assert.Contains("Incapacitated, from Stunned", implied[0].TextContent);
        Assert.Empty(cut.FindAll("[data-implied='true'] button"));
        // The status itself keeps its remove button for the GM.
        Assert.Single(cut.FindAll("[data-effect-id='Stunned'] button"));
    }

    [Fact]
    public void TheImpliedChip_IsInTheReadersLanguage()
    {
        var cut = Row(State(null, "Stunned"), "ru");

        var chip = cut.FindAll("[data-implied='true']")[0];
        Assert.Contains("из-за", chip.GetAttribute("aria-label"));
        Assert.Contains("Недееспособный", chip.GetAttribute("aria-label"));
    }

    [Fact]
    public void TheImpliedChip_InHebrew_SaysItsSource()
    {
        var cut = Row(State(null, "Stunned"), "he");

        Assert.Contains("בגלל", cut.FindAll("[data-implied='true']")[0].GetAttribute("aria-label"));
    }

    // ---- The table card ----

    private IRenderedComponent<TableCard> Card(IReadOnlyList<PrintedNumber>? printed, Dictionary<string, object> sheet, ActorStateSnapshot state, SheetRuleEngine? engine = null)
    {
        var system = Srd();
        return Render<TableCard>(p => p
            .Add(x => x.Campaign, new CampaignDocument { Id = "camp-1", SystemId = "srd5e" })
            .Add(x => x.Actor, new ActorDocument { Id = "ash", Name = "Ash", Sheet = sheet })
            .Add(x => x.State, state)
            .Add(x => x.Definition, system)
            .Add(x => x.Printed, printed)
            .Add(x => x.Engine, engine)
            .Add(x => x.Full, true));
    }

    [Fact]
    public void ACreaturesArmorClass_DropsWhileItIsShaken_AndSaysWhatItPrints()
    {
        var system = Srd();
        var engine = new SheetRuleEngine(system, NullEntryResolver.Instance);
        var sheet = new Dictionary<string, object> { [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod, ["armorClass"] = 18L };
        var state = State(new Dictionary<string, int> { ["condition_frightened"] = 2 });
        var live = state.LiveView(system, sheet);

        var cut = Card(engine.Printed(live), sheet, state, engine);

        var ac = cut.FindAll("[data-testid='table-card-stats'] div").Single(d => d.QuerySelector("dt")!.TextContent.Contains("AC")).QuerySelector("dd")!;
        Assert.StartsWith("16", ac.TextContent.Trim());
        Assert.Contains("printed 18; Frightened \u22122", ac.TextContent);
        Assert.Equal("printed 18; Frightened −2", ac.GetAttribute("title"));
    }

    [Fact]
    public void ACharactersManualArmorClass_TakesTheConditionModifiers_Too()
    {
        var system = Srd();
        var engine = new SheetRuleEngine(system, NullEntryResolver.Instance);
        var sheet = new Dictionary<string, object> { ["armorClass"] = 14L };

        var cut = Card(null, sheet, State(new Dictionary<string, int> { ["condition_frightened"] = 2 }), engine);

        var ac = cut.FindAll("[data-testid='table-card-stats'] div").Single(d => d.QuerySelector("dt")!.TextContent.Contains("AC")).QuerySelector("dd")!;
        Assert.StartsWith("12", ac.TextContent.Trim());
        Assert.Contains("printed 14; Frightened \u22122", ac.TextContent);
        Assert.Equal(14L, sheet["armorClass"]);
    }

    [Fact]
    public void TheCard_ShowsTheLevelOfEveryValuedConditionThatHolds()
    {
        var cut = Card(null, new Dictionary<string, object>(), State(new Dictionary<string, int> { ["Exhaustion"] = 3, ["condition_frightened"] = 2 }));

        var chips = cut.FindAll("[data-level]");
        Assert.Equal(new[] { "Exhaustion 3", "Frightened 2" }, chips.Select(Label).Order().ToArray());
        Assert.Equal("Frightened, level 2", chips.Single(c => c.GetAttribute("data-condition") == "Frightened").GetAttribute("title"));
    }

    [Fact]
    public void TheCard_ShowsAnImpliedConditionWithItsSource_AndNoRemoveButton()
    {
        var cut = Card(null, new Dictionary<string, object>(), State(null, "Stunned"));

        var chip = cut.Find("[data-implied='true']");
        Assert.Contains("Incapacitated, from Stunned", chip.TextContent);
        Assert.Empty(cut.FindAll("[data-implied='true'] button"));
    }
}
