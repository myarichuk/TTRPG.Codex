using Bunit;
using Codex.Core.Rules;
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

/// <summary>The conditions with a value at the table and on the sheet page (docs/SHEET_CONDITIONS_DESIGN.md, "Sheet and table"): one stepper per valued
/// condition, the picker's groups and the add path that starts a valued condition at 1 and never stores it as a status. srd5e has Exhaustion (key and
/// maximum from <c>vitals.exhaustion</c>); Frightened is added here as the general case.</summary>
public class ConditionSteppersTests : ComponentTestBase
{
    private static SystemDefinition Srd5e(string? frightenedMax = "4")
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        if (frightenedMax != null)
        {
            system.ConditionEffects["Frightened"] = new ConditionEffectDefinition { Value = new ConditionValueDefinition { Max = frightenedMax } };
        }

        return system;
    }

    private IRenderedComponent<ConditionSteppers> Draw(SystemDefinition? system, Dictionary<string, int> values, List<(string, int)>? steps = null,
        Dictionary<string, object>? sheet = null, string culture = "en")
    {
        Services.AddSingleton(Localizer.For(culture));
        return Render<ConditionSteppers>(p => p.Add(x => x.Definition, system?.Localize(culture)).Add(x => x.Name, "Ash").Add(x => x.Values, values)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.OnAdjust, EventCallback.Factory.Create<(string Condition, int Delta)>(this, step => steps?.Add(step))));
    }

    [Fact]
    public void EachValuedCondition_HasOneStepper_WithItsValueInAStatus()
    {
        var cut = Draw(Srd5e(), new() { ["Exhaustion"] = 2, ["condition_frightened"] = 1 });
        Assert.Equal(2, cut.FindAll("[data-testid^='condition-stepper-']").Count);
        Assert.Equal("2", cut.Find(Id("condition-value-exhaustion")).TextContent);
        Assert.Equal("status", cut.Find(Id("condition-value-exhaustion")).GetAttribute("role"));
        Assert.Equal("1", cut.Find(Id("condition-value-frightened")).TextContent);
        Assert.Equal("status", cut.Find(Id("condition-value-frightened")).GetAttribute("role"));
    }

    [Fact]
    public void TheButtons_NameTheConditionAndTheCreature()
    {
        var cut = Draw(Srd5e(), new() { ["condition_frightened"] = 1 });
        Assert.Equal("Raise Frightened for Ash", cut.Find(Id("condition-up-frightened")).GetAttribute("aria-label"));
        Assert.Equal("Lower Frightened for Ash", cut.Find(Id("condition-down-frightened")).GetAttribute("aria-label"));
        Assert.Equal("Raise Exhaustion for Ash", cut.Find(Id("condition-up-exhaustion")).GetAttribute("aria-label"));
    }

    [Fact]
    public void Lowering_IsOffAtZero_AndRaising_IsOffAtTheMaximum()
    {
        var cut = Draw(Srd5e(), new() { ["Exhaustion"] = 6, ["condition_frightened"] = 0 });
        Assert.True(cut.Find(Id("condition-down-frightened")).HasAttribute("disabled"));
        Assert.False(cut.Find(Id("condition-up-frightened")).HasAttribute("disabled"));
        // Exhaustion's maximum is vitals.exhaustion.maxLevel (6).
        Assert.True(cut.Find(Id("condition-up-exhaustion")).HasAttribute("disabled"));
        Assert.False(cut.Find(Id("condition-down-exhaustion")).HasAttribute("disabled"));
    }

    [Fact]
    public void RaisingIsOff_AtANumberedMaximum()
    {
        var top = Draw(Srd5e(), new() { ["condition_frightened"] = 4 });
        Assert.True(top.Find(Id("condition-up-frightened")).HasAttribute("disabled"));
    }

    [Fact]
    public void AMaximumThatIsAFormula_IsWorkedOutOnTheSheet()
    {
        var system = Srd5e("level - 1");
        var low = Draw(system, new() { ["condition_frightened"] = 2 }, sheet: new() { ["level"] = 3L });
        Assert.True(low.Find(Id("condition-up-frightened")).HasAttribute("disabled"));
    }

    [Fact]
    public void AFormulaMaximum_LetsItRaise_WhileBelowIt()
    {
        var high = Draw(Srd5e("level - 1"), new() { ["condition_frightened"] = 2 }, sheet: new() { ["level"] = 5L });
        Assert.False(high.Find(Id("condition-up-frightened")).HasAttribute("disabled"));
    }

    [Fact]
    public void AClick_ReportsTheConditionAndTheStep()
    {
        var steps = new List<(string, int)>();
        var cut = Draw(Srd5e(), new() { ["condition_frightened"] = 2 }, steps);
        cut.Find(Id("condition-up-frightened")).Click();
        cut.Find(Id("condition-down-frightened")).Click();
        Assert.Equal(new[] { ("Frightened", 1), ("Frightened", -1) }, steps);
    }

    [Fact]
    public void ASystemWithNoValuedConditions_ShowsNothing()
    {
        var cut = Draw(new SystemDefinition(), new());
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void NoSystemAtAll_ShowsNothing() => Assert.Empty(Draw(null, new()).Markup.Trim());

    [Fact]
    public void InRussian_TheNameIsTranslated_AndTheStepStillNamesTheRulesCondition()
    {
        var steps = new List<(string, int)>();
        var cut = Draw(Srd5e(), new() { ["Exhaustion"] = 1 }, steps, culture: "ru");
        Assert.Contains("Истощение", cut.Find(Id("condition-up-exhaustion")).GetAttribute("aria-label"));
        Assert.Contains("Ash", cut.Find(Id("condition-up-exhaustion")).GetAttribute("aria-label"));
        cut.Find(Id("condition-up-exhaustion")).Click();
        Assert.Equal(("Exhaustion", 1), steps.Single());
    }

    private static void Adjusted(IRuntimeCommand command, string condition, int delta)
    {
        var adjust = Assert.IsType<AdjustConditionCommand>(command);
        Assert.Equal(("a1", condition, delta), (adjust.ActorId, adjust.Condition, adjust.Delta));
    }

    // ---- The picker and the add path in the table's command panel ----

    private IRenderedComponent<TableCommandPanel> Panel(SystemDefinition system, Dictionary<string, int> pools, List<IRuntimeCommand> sent, string culture = "en",
        List<Flash>? flashes = null)
    {
        Services.AddSingleton(Localizer.For(culture));
        var actor = new ActorDocument { Id = "a1", Name = "Ash", Kind = ActorKind.PlayerCharacter, Sheet = new Dictionary<string, object> { ["HP"] = 10L } };
        var snapshot = new RuntimeSnapshot(null, Array.Empty<RollLogEntry>(), Array.Empty<SessionNote>(),
            new Dictionary<string, ActorStateSnapshot> { ["a1"] = new(pools, Array.Empty<EffectSnapshot>()) });
        return Render<TableCommandPanel>(p => p.Add(x => x.ActorId, "a1").Add(x => x.Name, "Ash")
            .Add(x => x.Participant, new ParticipantSnapshot("a1", 10, 0, TurnState.Normal, false)).Add(x => x.IsDm, true)
            .Add(x => x.Definition, system.Localize(culture)).Add(x => x.Snapshot, snapshot)
            .Add(x => x.ActorsById, new Dictionary<string, ActorDocument> { ["a1"] = actor })
            .Add(x => x.Send, command => { sent.Add(command); return Task.CompletedTask; })
            .Add(x => x.OnMessage, EventCallback.Factory.Create<Flash>(this, flash => flashes?.Add(flash))));
    }

    [Fact]
    public void ThePanel_HasOneStepperPerValuedCondition_AndSendsTheCommand()
    {
        var sent = new List<IRuntimeCommand>();
        var cut = Panel(Srd5e(), new() { ["Exhaustion"] = 1, ["condition_frightened"] = 2 }, sent);
        Assert.Equal(2, cut.FindAll("[data-testid^='condition-stepper-']").Count);
        cut.Find(Id("condition-up-frightened")).Click();
        Adjusted(sent.Single(), "Frightened", 1);
    }

    [Fact]
    public void ThePicker_ListsConditionsThenSpellEffectsAsTheirOwnGroup_AndValuesWhereTheyHold()
    {
        var cut = Panel(Srd5e(), new() { ["condition_frightened"] = 2 }, new());
        var options = cut.FindAll("#conditions-a1 option");
        var labels = options.Select(o => o.GetAttribute("label")).ToList();
        Assert.Contains("Poisoned", labels);
        Assert.Contains("Frightened 2", labels);
        Assert.Contains("Exhaustion", labels);
        Assert.Contains("Bless (Spell effects)", labels);
        Assert.Contains("Guided (Spell effects)", labels);
        // Every spell effect comes after every condition.
        var firstEffect = labels.FindIndex(l => l!.EndsWith("(Spell effects)"));
        Assert.DoesNotContain(labels.Skip(firstEffect), l => !l!.EndsWith("(Spell effects)"));
    }

    [Fact]
    public async Task AddingAValuedCondition_StartsItAtOne_AndNeverStoresAStatus()
    {
        var sent = new List<IRuntimeCommand>();
        var cut = Panel(Srd5e(), new() { ["condition_frightened"] = 0 }, sent);
        cut.Find(Id("condition-input")).Input("frightened");
        await cut.Find(Id("condition-add-btn")).ClickAsync(new());
        Adjusted(sent.Single(), "Frightened", 1);
        Assert.DoesNotContain(sent, c => c is AddStatusEffectCommand);
    }

    [Fact]
    public async Task AValuedConditionAlreadyHeld_IsNotAddedAgain()
    {
        var sent = new List<IRuntimeCommand>();
        var flashes = new List<Flash>();
        var cut = Panel(Srd5e(), new() { ["condition_frightened"] = 2 }, sent, flashes: flashes);
        cut.Find(Id("condition-input")).Input("Frightened");
        await cut.Find(Id("condition-add-btn")).ClickAsync(new());
        Assert.Empty(sent);
        Assert.Single(flashes);
    }

    [Fact]
    public async Task ATranslatedName_IsMappedBackToTheRulesName()
    {
        var sent = new List<IRuntimeCommand>();
        var cut = Panel(Srd5e(), new(), sent, "ru");
        cut.Find(Id("condition-input")).Input("Истощение");
        await cut.Find(Id("condition-add-btn")).ClickAsync(new());
        Adjusted(sent.Single(), "Exhaustion", 1);

        cut.Find(Id("condition-input")).Input("Отравленный");
        await cut.Find(Id("condition-add-btn")).ClickAsync(new());
        Assert.Equal("Poisoned", Assert.IsType<AddStatusEffectCommand>(sent[1]).EffectId);
    }

    [Fact]
    public async Task AnUnvaluedCondition_IsStillAStatus()
    {
        var sent = new List<IRuntimeCommand>();
        var cut = Panel(Srd5e(), new(), sent);
        cut.Find(Id("condition-input")).Input("Bless");
        await cut.Find(Id("condition-add-btn")).ClickAsync(new());
        Assert.Equal("Bless", Assert.IsType<AddStatusEffectCommand>(sent.Single()).EffectId);
    }

    // ---- The list the sheet page builds ----

    [Fact]
    public void TheSheetPageList_NamesWhatHolds_WithValues()
    {
        var system = Srd5e();
        var sheet = new Dictionary<string, object>
        {
            [SheetModel.ConditionsKey] = new List<object> { "Poisoned", "Bless" },
            ["Exhaustion"] = 2L,
            ["condition_frightened"] = 0L
        };
        var lines = ConditionLines.Holding(system, sheet);
        Assert.Equal(new[] { ("Poisoned", (int?)null), ("Bless", null), ("Exhaustion", 2) }, lines.Select(l => (l.Name, l.Value)));
    }

    [Fact]
    public void TheSheetPageList_IsEmpty_WhenNothingHolds()
    {
        Assert.Empty(ConditionLines.Holding(Srd5e(), new Dictionary<string, object> { ["Exhaustion"] = 0L }));
    }

    [Fact]
    public void Canonical_MapsAReadersNameBack_AndLeavesAnyOtherTextAlone()
    {
        var ru = Srd5e().Localize("ru");
        Assert.Equal("Poisoned", ConditionLines.Canonical(ru, " отравленный "));
        Assert.Equal("Poisoned", ConditionLines.Canonical(ru, "poisoned"));
        Assert.Equal("Something odd", ConditionLines.Canonical(ru, "Something odd"));
    }
}
