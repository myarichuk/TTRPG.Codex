using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>The game master's Hazards panel: a list of hazards, each a group with a heading and real buttons that name the action and the hazard, characters as a
/// fieldset of check boxes, the last result in a polite status region, and a request for each action. Read in every language.</summary>
public class HazardsPanelTests : ComponentTestBase
{
    private static readonly HazardWho Ana = new("a1", "Ana");
    private static readonly HazardWho Bo = new("a2", "Bo");

    private static HazardView Pit(string id = "hazard:r/p1", string name = "Spiked Pit", HazardState state = HazardState.Hidden, bool inLibrary = true, int waiting = 0) =>
        new(id, inLibrary ? new HazardSite("r", "p1", name, new PlacedHazard { Entry = "hazard/spiked-pit" }, new Codex.Plugin.Abstractions.ContentEntry { Id = "spiked-pit", Name = name },
                new Codex.Plugin.Abstractions.ContentTypeDefinition { Type = "hazard" }) : null,
            name, "Pit room", state, 0, new[] { "Ana" },
            new[] { new HazardWay("lid", "The lid (Investigation, DC 15)"), new HazardWay("sign", "A sign") },
            new[] { new HazardWay("wedge", "Wedge the lid (needs a spike)") }, new[] { "Avoid" }, waiting);

    private static readonly HazardNeed Need = new("k1", "g1", "Goblin", HazardAskKind.Bonus, "Dexterity");

    private IRenderedComponent<HazardsPanel> Draw(string culture = "en", IReadOnlyList<HazardView>? hazards = null, Action<HazardRequest>? onRequest = null,
        IReadOnlyList<HazardPass>? passes = null, IReadOnlyList<HazardNeed>? needNumber = null, IReadOnlyList<string>? status = null, string askedFor = "hazard:r/p1|Trigger|",
        IReadOnlyDictionary<string, int>? suggested = null)
    {
        Services.AddSingleton(Localizer.For(culture));
        return Render<HazardsPanel>(p => p
            .Add(x => x.Hazards, hazards ?? new[] { Pit() })
            .Add(x => x.Who, new[] { Ana, Bo })
            .Add(x => x.Places, new[] { new HazardPlace("p1", "Pit room") })
            .Add(x => x.Passes, passes ?? Array.Empty<HazardPass>())
            .Add(x => x.PassesFor, passes is { Count: > 0 } ? "hazard:r/p1" : null)
            .Add(x => x.NeedNumber, needNumber ?? Array.Empty<HazardNeed>())
            .Add(x => x.AskedFor, askedFor)
            .Add(x => x.Suggested, suggested ?? new Dictionary<string, int>())
            .Add(x => x.Status, status ?? Array.Empty<string>())
            .Add(x => x.OnRequest, (HazardRequest r) => onRequest?.Invoke(r)));
    }

    [Fact]
    public void EachHazard_IsAGroupWithAHeadingAndItsStateInWords_AndButtonsThatNameTheActionAndTheHazard()
    {
        var cut = Draw();

        var article = cut.Find("[data-testid=hazard-0] article");
        var heading = cut.Find("#" + article.GetAttribute("aria-labelledby"));
        Assert.Contains("Spiked Pit", heading.TextContent);
        Assert.Contains("Hidden", cut.Find("[data-testid=hazard-0-state]").TextContent);
        Assert.Equal("Notice: Spiked Pit", cut.Find("[data-testid=hazard-0-notice]").TextContent.Trim());
        Assert.Equal("Trigger: Spiked Pit", cut.Find("[data-testid=hazard-0-trigger]").TextContent.Trim());
        Assert.Equal("Find: Spiked Pit", cut.Find("[data-testid=hazard-0-find]").TextContent.Trim());
        Assert.Equal("Disable: Spiked Pit", cut.Find("[data-testid=hazard-0-disable]").TextContent.Trim());
        Assert.All(cut.FindAll("button"), b => Assert.False(string.IsNullOrWhiteSpace(b.TextContent)));
        Assert.Contains("Noticed by Ana", cut.Find("[data-testid=hazard-0]").TextContent);
    }

    [Fact]
    public void TheCharacters_AreAFieldsetWithALegend_AndEveryControlHasALabel()
    {
        var cut = Draw();

        var fieldset = cut.Find("fieldset[data-testid=hazard-who]");
        Assert.Equal("Characters", fieldset.QuerySelector("legend")!.TextContent);
        Assert.Equal(2, fieldset.QuerySelectorAll("input[type=checkbox]").Length);
        foreach (var input in cut.FindAll("input, select"))
        {
            var id = input.GetAttribute("id");
            Assert.False(string.IsNullOrEmpty(id));
            Assert.NotEmpty(cut.FindAll($"label[for='{id}']"));
        }

        Assert.Equal("Way to find Spiked Pit", cut.Find("label[for=hazard-0-find-way]").TextContent);
    }

    [Fact]
    public void TheLastResult_IsInAPoliteStatusRegion_AndShownInWords()
    {
        var cut = Draw(status: new[] { "Spiked Pit: Ana finds it (The lid).", "Ana: Perception 17 vs DC 15, success" });

        var region = cut.Find("[role=status]");
        Assert.Equal("polite", region.GetAttribute("aria-live"));
        Assert.Contains("Ana finds it", region.TextContent);
        Assert.Contains("Last result", cut.Find("[data-testid=hazard-result]").TextContent);
    }

    [Fact]
    public void WithNobodyTicked_TheActionsAreOff_AndTickingTurnsThemOn()
    {
        var cut = Draw();
        Assert.True(cut.Find("[data-testid=hazard-0-notice]").HasAttribute("disabled"));

        cut.Find("[data-testid=hazard-who-a1]").Change(true);

        Assert.False(cut.Find("[data-testid=hazard-0-notice]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=hazard-0-disable]").HasAttribute("disabled"));
    }

    [Fact]
    public void NoticeAndTrigger_UseEveryoneTicked_FindAndDisable_TheFirstOneWithTheChosenWay()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add);
        cut.Find("[data-testid=hazard-who-a2]").Change(true);
        cut.Find("[data-testid=hazard-who-a1]").Change(true);

        cut.Find("[data-testid=hazard-0-notice]").Click();
        cut.Find("[data-testid=hazard-0-trigger]").Click();
        cut.Find("[data-testid=hazard-0-find-way]").Change("sign");
        cut.Find("[data-testid=hazard-0-find]").Click();
        cut.Find("[data-testid=hazard-0-disable]").Click();

        // The list shows Ana before Bo, whichever was ticked first.
        Assert.Equal(new[] { "a1", "a2" }, requests[0].ActorIds);
        Assert.Equal(HazardAction.Trigger, requests[1].Action);
        Assert.Equal(new[] { "a1", "a2" }, requests[1].ActorIds);
        Assert.Equal((HazardAction.Find, "sign"), (requests[2].Action, requests[2].Route));
        Assert.Equal(new[] { "a1" }, requests[2].ActorIds);
        Assert.Equal((HazardAction.Disable, "wedge"), (requests[3].Action, requests[3].Route));
        Assert.Equal(new[] { "a1" }, requests[3].ActorIds);
    }

    [Fact]
    public void TheStatePicker_SendsAStateRequest()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add);

        cut.Find("[data-testid=hazard-0-state-pick]").Change("Disabled");
        cut.Find("[data-testid=hazard-0-set-state]").Click();

        Assert.Equal((HazardAction.SetState, HazardState.Disabled), (requests.Single().Action, requests.Single().State));
    }

    [Fact]
    public void MarkAsNoticed_IsOfferedOnlyForTheHazardTheSweepWasFor_AndNamesWhoPassedAndWhichWay()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add, passes: new[] { new HazardPass("a1", "lid"), new HazardPass("a2", "lid") });

        var mark = cut.Find("[data-testid=hazard-0-mark]");
        Assert.Contains("Ana, Bo", mark.TextContent);
        mark.Click();

        var request = requests.Single();
        Assert.Equal(HazardAction.MarkNoticed, request.Action);
        Assert.Equal(new[] { "a1", "a2" }, request.ActorIds);
        Assert.Equal(new[] { "lid" }, request.Routes);
    }

    [Fact]
    public void WithNoSweepResult_ThereIsNothingToMark() => Assert.Empty(Draw().FindAll("[data-testid=hazard-0-mark]"));

    [Fact]
    public void ACreatureWhoseNumberIsNeeded_GetsALabelledNumberField_AndItsNumberGoesWithTheNextRequest()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add, needNumber: new[] { Need });

        var input = cut.Find("input[type=number]");
        Assert.Equal("Goblin: Dexterity bonus", cut.Find($"label[for='{input.GetAttribute("id")}']").TextContent);
        Assert.NotNull(input.GetAttribute("aria-describedby"));
        input.Change("3");
        cut.Find("[data-testid=hazard-who-a1]").Change(true);
        cut.Find("[data-testid=hazard-0-trigger]").Click();

        Assert.Equal(3, requests.Single().Bonuses!["k1"]);

        // A typed number answers that one question: the next request, for anything else, carries none.
        cut.Find("[data-testid=hazard-0-notice]").Click();
        Assert.Null(requests.Last().Bonuses);
    }

    [Fact]
    public void ANumberTypedForSomeoneWhoWasNotAsked_IsNotSent()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add, needNumber: new[] { Need });
        cut.Find("input[type=number]").Change("3");
        cut.Render(p => p.Add(x => x.NeedNumber, Array.Empty<HazardNeed>()));
        cut.Find("[data-testid=hazard-who-a1]").Change(true);

        cut.Find("[data-testid=hazard-0-trigger]").Click();

        Assert.Null(requests.Single().Bonuses);
    }

    [Fact]
    public void TwoNumbersForOneCreature_AreTwoLabelledFields_AndGoBackUnderTheirOwnKeys()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add, needNumber: new[]
        {
            new HazardNeed("k-passive", "g1", "Goblin", HazardAskKind.Passive, "Perception"),
            new HazardNeed("k-bonus", "g1", "Goblin", HazardAskKind.Bonus, "Arcana"),
            new HazardNeed("k-ac", "g1", "Goblin", HazardAskKind.ArmorClass, string.Empty)
        });

        var inputs = cut.FindAll("input[type=number]");
        Assert.Equal(3, inputs.Count);
        Assert.Equal(new[] { "Goblin: Perception score (passive)", "Goblin: Arcana bonus", "Goblin: armor class" },
            inputs.Select(i => cut.Find($"label[for='{i.GetAttribute("id")}']").TextContent).ToArray());
        cut.Find("#hazard-number-0").Change("13");
        cut.Find("#hazard-number-1").Change("2");
        cut.Find("#hazard-number-2").Change("15");
        cut.Find("[data-testid=hazard-who-a1]").Change(true);
        cut.Find("[data-testid=hazard-0-trigger]").Click();

        var sent = requests.Single().Bonuses!;
        Assert.Equal((13, 2, 15), (sent["k-passive"], sent["k-bonus"], sent["k-ac"]));
    }

    [Fact]
    public void ANumberTypedForOneAction_IsNotSentWithAnotherAction_OnlyWithTheRepeatOfTheRequestThatAskedForIt()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(onRequest: requests.Add, needNumber: new[] { Need });
        cut.Find("input[type=number]").Change("3");
        cut.Find("[data-testid=hazard-who-a1]").Change(true);

        // A Notice is not the Trigger that asked: the number is not sent with it, and is not kept for later either.
        cut.Find("[data-testid=hazard-0-notice]").Click();
        Assert.Null(requests.Single().Bonuses);
        cut.Find("[data-testid=hazard-0-trigger]").Click();
        Assert.Null(requests.Last().Bonuses);
    }

    [Fact]
    public void TheUndoButton_IsOfferedOnlyWhenTheHazardsLastChangeCanBeTakenBack_AndAsksToUndoThatHazard()
    {
        var undone = new List<HazardView>();
        Services.AddSingleton(Localizer.For("en"));
        var cut = Render<HazardsPanel>(p => p
            .Add(x => x.Hazards, new[] { Pit() with { CanUndo = true } })
            .Add(x => x.Who, new[] { Ana })
            .Add(x => x.OnUndo, (HazardView h) => undone.Add(h)));

        Assert.Equal("Undo last change: Spiked Pit", cut.Find("[data-testid=hazard-0-undo]").TextContent.Trim());
        cut.Find("[data-testid=hazard-0-undo]").Click();

        Assert.Equal("hazard:r/p1", undone.Single().Id);
    }

    [Fact]
    public void WithNothingToTakeBack_ThereIsNoUndoButton() => Assert.Empty(Draw().FindAll("[data-testid=hazard-0-undo]"));

    [Fact]
    public void AHazardGoneFromTheLibrary_SaysSo_AndOffersNoAction()
    {
        var cut = Draw(hazards: new[] { Pit(inLibrary: false) });

        Assert.Contains("no longer in the Library", cut.Find("[data-testid=hazard-0]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=hazard-0-notice]"));
    }

    [Fact]
    public void WaitingWays_AreCounted() =>
        Assert.Contains("2 more ways open", Draw(hazards: new[] { Pit(waiting: 2) }).Find("[data-testid=hazard-0]").TextContent);

    private static HazardView Stone(bool inOrder = true, bool itsTurn = false, bool hasEffect = true) =>
        Pit(name: "Rolling Stone") with
        {
            Turn = new HazardTurn("stone1", new[] { new Codex.Core.Systems.HazardStep("Roll", "Moves 60 feet."), new Codex.Core.Systems.HazardStep("Crush", "On any creature's turn.", Strikes: true) }, inOrder, itsTurn, hasEffect)
        };

    [Fact]
    public void ARoutine_ListsEachStep_NamesTheHazardOnItsButtons_AndSaysWhoseTurnItIs()
    {
        var cut = Draw(hazards: new[] { Stone(itsTurn: true) });

        var section = cut.Find("[data-testid=hazard-0-routine]");
        Assert.Contains("Routine of Rolling Stone", cut.Find("#" + section.GetAttribute("aria-labelledby")).TextContent);
        Assert.Contains("In the initiative order", cut.Find("[data-testid=hazard-0-order]").TextContent);
        Assert.Contains("Its turn now", cut.Find("[data-testid=hazard-0-its-turn]").TextContent);
        Assert.Contains("Moves 60 feet.", cut.Find("[data-testid=hazard-0-step-0]").TextContent);
        Assert.Equal("Note step Crush of Rolling Stone", cut.Find("[data-testid=hazard-0-step-1-note]").TextContent.Trim());
        Assert.Equal("Step Crush of Rolling Stone on the ticked creatures", cut.Find("[data-testid=hazard-0-step-1-on]").TextContent.Trim());
        Assert.Contains("any creature's turn", section.TextContent);
    }

    [Fact]
    public void ANoteOfAStep_NamesTheStepAndNoCreatures_AndCrushOnTickedCreatures_NamesThem()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(hazards: new[] { Stone() }, onRequest: requests.Add);
        Assert.True(cut.Find("[data-testid=hazard-0-step-1-on]").HasAttribute("disabled"));

        cut.Find("[data-testid=hazard-0-step-1-note]").Click();
        cut.Find("[data-testid=hazard-who-a2]").Change(true);
        cut.Find("[data-testid=hazard-0-step-1-on]").Click();

        Assert.Equal(new[] { (HazardAction.Routine, "Crush", 0), (HazardAction.Routine, "Crush", 1) }, requests.Select(r => (r.Action, r.Route!, r.ActorIds.Count)));
        Assert.Equal(new[] { "a2" }, requests[1].ActorIds);
    }

    [Fact]
    public void OnlyTheRowTheDataMarksAsStriking_OffersTheTickedCreatures()
    {
        var cut = Draw(hazards: new[] { Stone() });

        // Roll and Crush are both steps; only Crush has `strikes: true`, so only it has the button that applies the effect.
        Assert.Empty(cut.FindAll("[data-testid=hazard-0-step-0-on]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=hazard-0-step-0-note]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=hazard-0-step-1-on]"));
    }

    [Fact]
    public void AHazardWithNoEffect_OffersNoStrikeEvenOnAStrikingRow() =>
        Assert.Empty(Draw(hazards: new[] { Stone(hasEffect: false) }).FindAll("[data-testid=hazard-0-step-1-on]"));

    [Fact]
    public void CrushOnACreatureThatNeedsANumber_SendsTheTypedNumberWithTheRepeat_AndASuggestedOneStands()
    {
        var requests = new List<HazardRequest>();
        var cut = Draw(hazards: new[] { Stone() }, onRequest: requests.Add, needNumber: new[] { Need, new HazardNeed("k2", "g2", "Orc", HazardAskKind.Bonus, "Dexterity") },
            askedFor: "hazard:r/p1|Routine|Crush", suggested: new Dictionary<string, int> { ["k2"] = 1 });
        cut.Find("[data-testid=hazard-number-0]").Change("4");
        cut.Find("[data-testid=hazard-who-a1]").Change(true);

        cut.Find("[data-testid=hazard-0-step-1-on]").Click();

        var sent = requests.Single();
        Assert.Equal((HazardAction.Routine, "Crush"), (sent.Action, sent.Route));
        Assert.Equal(4, sent.Bonuses!["k1"]);
        Assert.Equal(1, sent.Bonuses["k2"]);

        // The number answers that question once: a note of the same step, and a Crush that was not the one that asked, carry none.
        cut.Find("[data-testid=hazard-0-step-1-note]").Click();
        Assert.Null(requests.Last().Bonuses);
        cut.Render(p => p.Add(x => x.AskedFor, "hazard:r/p1|Trigger|"));
        cut.Find("[data-testid=hazard-0-step-1-on]").Click();
        Assert.Null(requests.Last().Bonuses);
    }

    [Fact]
    public void AHazardOutOfTheOrder_SaysSo_AndOffersNoStep()
    {
        var cut = Draw(hazards: new[] { Stone(inOrder: false) });

        Assert.Contains("Out of the initiative order", cut.Find("[data-testid=hazard-0-order]").TextContent);
        Assert.True(cut.Find("[data-testid=hazard-0-step-0-note]").HasAttribute("disabled"));
        Assert.DoesNotContain(cut.FindAll("[data-testid=hazard-0-its-turn]"), _ => true);
    }

    [Fact]
    public void ASimpleHazard_HasNoRoutine() => Assert.Empty(Draw().FindAll("[data-testid=hazard-0-routine]"));

    [Theory]
    [InlineData("ru", "Порядок действий: Rolling Stone", "Сейчас её ход")]
    [InlineData("he", "סדר הפעולות של Rolling Stone", "התור שלה עכשיו")]
    public void ARoutine_IsTranslated(string culture, string heading, string itsTurn)
    {
        var cut = Draw(culture, new[] { Stone(itsTurn: true) });

        Assert.Contains(heading, cut.Find("[data-testid=hazard-0-routine]").TextContent);
        Assert.Contains(itsTurn, cut.Markup);
        Assert.DoesNotContain("Combat.Hazard", cut.Markup);
    }

    [Fact]
    public void NoHazards_SaysHowToPlaceOne() =>
        Assert.Contains("Place one on a place's page", Draw(hazards: Array.Empty<HazardView>()).Find("[data-testid=hazards-none]").TextContent);

    [Theory]
    [InlineData("ru", "Заметить: Spiked Pit", "Скрыта")]
    [InlineData("he", "זיהוי: Spiked Pit", "מוסתרת")]
    public void EveryWord_IsTranslated_AndNoKeyShowsThrough(string culture, string notice, string state)
    {
        var cut = Draw(culture);

        Assert.Equal(notice, cut.Find("[data-testid=hazard-0-notice]").TextContent.Trim());
        Assert.Contains(state, cut.Find("[data-testid=hazard-0-state]").TextContent);
        Assert.DoesNotContain("Combat.Hazard", cut.Markup);
    }

    [Fact]
    public void TheMarkup_UsesNoPhysicalLeftOrRight_SoItMirrorsInHebrew()
    {
        var cut = Draw("he", status: new[] { "x" });

        var classes = cut.FindAll("[class]").SelectMany(e => e.GetAttribute("class")!.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();
        Assert.DoesNotContain(classes, c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^(ml|mr|pl|pr|left|right|float-left|float-right|text-left|text-right)(-|$)"));
        Assert.DoesNotContain("left:", cut.Markup);
        Assert.DoesNotContain("margin-left", cut.Markup);
    }
}
