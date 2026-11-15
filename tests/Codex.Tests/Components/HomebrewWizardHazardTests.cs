using AngleSharp.Dom;
using Bunit;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Campaign;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>
/// The homebrew wizard's hazard mode (docs/HAZARDS_DESIGN.md 7, H7): the lists of a hazard as labelled row editors in the steps the type's hazard block implies, a save that
/// the table's own checks can stop (the shape) or only warn about (numbers off the system's guide), blank rows that are never saved, numbers filled from the guide, and the
/// same words in every language. The real srd5e system and its hazard type are used throughout.
/// </summary>
public class HomebrewWizardHazardTests : ComponentTestBase
{
    private static readonly BookTestCatalog Catalog = new();
    private readonly IHomebrewRepository _homebrew = Substitute.For<IHomebrewRepository>();
    private HomebrewEntryDocument? _saved;

    private IRenderedComponent<HomebrewWizard> Open(string culture = "en", string type = "hazard")
    {
        Services.AddSingleton(Localizer.For(culture));
        var registry = new ContentRegistry();
        _homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Task.FromResult<IEnumerable<HomebrewEntryDocument>>(Array.Empty<HomebrewEntryDocument>()));
        _homebrew.TrySaveAsync(Arg.Do<HomebrewEntryDocument>(e => _saved = e), Arg.Any<CampaignAccess>()).Returns(true);
        Services.AddSingleton(_homebrew);
        Services.AddSingleton<IContentRegistry>(registry);
        Services.AddSingleton<ISystemCatalog>(Catalog);
        Services.AddLogging();
        Services.AddSingleton(new CampaignLibrary(registry, _homebrew, Catalog));
        Services.AddSingleton(new AiFeatures(null!, registry, Catalog));
        Services.AddSingleton(new ArtService(null!, _homebrew, null!, null!, null!, null!));
        var context = new CampaignContext
        {
            Campaign = new CampaignDocument { Id = "c1", SystemId = "srd5e", Name = "Salt" },
            Access = new CampaignAccess("c1", "dm", CampaignRole.DM),
            UserId = "dm",
            System = Catalog.GetDefinition("srd5e"),
            Actors = [],
            AllActors = [],
            ReloadAsync = () => Task.CompletedTask,
            Notify = _ => { },
            AccessChangedAsync = () => Task.CompletedTask
        };
        var cut = Render<HomebrewWizard>(p => p.AddCascadingValue(context));
        cut.InvokeAsync(() => cut.Instance.OpenAsync(new HomebrewEntryDocument { CampaignId = "c1", Type = type, Name = "Test pit" })).GetAwaiter().GetResult();
        return cut;
    }

    private static void GoTo(IRenderedComponent<HomebrewWizard> cut, string step) => cut.Find($"[data-testid='wizard-step-cat-{step}']").Click();

    /// <summary>The control a row's visible label names: found through the label's own <c>for</c>, as a screen reader finds it.</summary>
    private static IElement Control(IRenderedComponent<HomebrewWizard> cut, string group, string label, int row = 0)
    {
        var rows = cut.FindAll($"[data-testid='{group}'] > [data-testid=hazard-row]");
        var named = rows[row].QuerySelectorAll("label").First(l => l.TextContent.Trim() == label);
        return cut.Find("#" + named.GetAttribute("for"));
    }

    private static IElement Field(IRenderedComponent<HomebrewWizard> cut, string label) =>
        cut.Find("#" + cut.FindAll("label.field-label").First(l => l.TextContent.Replace("*", string.Empty).Trim() == label).GetAttribute("for"));

    private void Save(IRenderedComponent<HomebrewWizard> cut)
    {
        cut.Find("form.wizard").Submit();
        cut.WaitForAssertion(() => Assert.NotNull(_saved));
    }

    [Fact]
    public void AHazardGetsAStepForEachPart_AndTheRoutineAndTheBandsHaveTheirOwn()
    {
        var cut = Open();

        var steps = cut.FindAll("[data-testid^='wizard-step-']").Select(s => s.TextContent.Trim()).ToList();
        foreach (var expected in new[] { "Profile", "Notice", "Disable", "Trigger", "Effect", "Durability", "Reset", "Routine", "Level bands" })
        {
            Assert.Contains(steps, s => s.EndsWith(expected, StringComparison.Ordinal));
        }

        // The other ways go beside the disarm routes; the routine and the bands are steps of their own, with their own words.
        GoTo(cut, "Disable");
        Assert.NotEmpty(cut.FindAll("[data-testid=hazard-disarm]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=hazard-methods]"));
        GoTo(cut, "hazard:routine");
        Assert.NotEmpty(cut.FindAll("[data-testid=hazard-routine]"));
        Assert.Contains("at most one step", cut.Find(".wizard-body").TextContent);
        GoTo(cut, "hazard:bands");
        Assert.NotEmpty(cut.FindAll("[data-testid=hazard-higherLevels]"));
    }

    [Fact]
    public void ARouteRow_HasAVisibleLabelForEveryControl_AndIsSavedInTheShapeTheTableReads()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();

        var row = cut.Find("[data-testid=hazard-detect] > [data-testid=hazard-row]");
        Assert.Equal("group", row.GetAttribute("role"));
        Assert.Contains("Detect route 1", cut.Find("#" + row.GetAttribute("aria-labelledby")).TextContent);
        foreach (var control in row.QuerySelectorAll("input,select,textarea"))
        {
            Assert.NotNull(row.QuerySelector($"label[for='{control.Id}']"));
        }

        Control(cut, "hazard-detect", "Name").Change("Inspect the lock");
        Control(cut, "hazard-detect", "Action (Search, Study)").Change("Search");
        Control(cut, "hazard-detect", "Check").Change("Perception");
        Control(cut, "hazard-detect", "DC").Change("15");
        Control(cut, "hazard-detect", "Short name (for “Only after”)").Change("lock");
        Control(cut, "hazard-detect", "Passive scores can find it").Change(false);
        Control(cut, "hazard-detect", "Check").Change("Perception");
        // The check box and its label are one control: the stored key is passive: false.
        Save(cut);

        var saved = Assert.Single(HazardNumbers.EditableRows(_saved!.Properties["detect"]));
        Assert.Equal("Inspect the lock", saved["name"]);
        Assert.Equal("Search", saved["action"]);
        Assert.Equal("Perception", saved["check"]);
        Assert.Equal(15L, saved["dc"]);
        Assert.Equal("lock", saved["id"]);
        Assert.Equal(false, saved["passive"]);
    }

    [Fact]
    public void ADisarmRouteThatIsSetOffByAFailure_KeepsTheWord_AndAnAfterNamesADetectRoute()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Control(cut, "hazard-detect", "Name").Change("The lock");
        Control(cut, "hazard-detect", "Needs (instead of a check)").Change("Magic");
        Control(cut, "hazard-detect", "Short name (for “Only after”)").Change("lock");
        GoTo(cut, "Disable");
        cut.Find("[data-testid=hazard-disarm-add]").Click();
        Control(cut, "hazard-disarm", "Name").Change("Pick it");
        Control(cut, "hazard-disarm", "Check").Change("Sleight of Hand");
        Control(cut, "hazard-disarm", "DC").Change("15");
        Control(cut, "hazard-disarm", "A failure sets the hazard off").Change(true);
        Control(cut, "hazard-disarm", "Only after").Change("lock");
        Save(cut);

        var row = Assert.Single(HazardNumbers.EditableRows(_saved!.Properties["disarm"]));
        Assert.Equal(["failure"], ((IEnumerable<object>)row["triggersOn"]).Select(o => o.ToString()));
        Assert.Equal("lock", row["after"]);
    }

    [Fact]
    public void AnOtherWayRow_CanWaitForADetectRoute_AndNeedsNoCheckWhenItSaysWhatItTakes()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Control(cut, "hazard-detect", "Name").Change("The lock");
        Control(cut, "hazard-detect", "Needs (instead of a check)").Change("Magic");
        Control(cut, "hazard-detect", "Short name (for “Only after”)").Change("lock");
        GoTo(cut, "Disable");
        cut.Find("[data-testid=hazard-methods-add]").Click();
        Control(cut, "hazard-methods", "Name").Change("Smash it");
        Control(cut, "hazard-methods", "Needs (instead of a check)").Change("A hammer");
        Control(cut, "hazard-methods", "Only after").Change("lock");
        Assert.Empty(cut.FindAll("[data-testid=hazard-shape]"));
        Save(cut);

        Assert.Equal("lock", Assert.Single(HazardNumbers.EditableRows(_saved!.Properties["methods"]))["after"]);
    }

    [Fact]
    public void ARouteTheTableCannotRun_IsSaidAsYouWrite_AndStopsTheSave()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Control(cut, "hazard-detect", "Name").Change("Look");
        Control(cut, "hazard-detect", "DC").Change("15");

        // Said in a polite live region, in words, with no check to roll the DC against.
        var shape = cut.Find("[data-testid=hazard-checks]");
        Assert.Equal("polite", shape.GetAttribute("aria-live"));
        Assert.Contains("has a dc and no check", cut.Find("[data-testid=hazard-shape]").TextContent);

        cut.Find("form.wizard").Submit();
        cut.WaitForAssertion(() => Assert.Contains("The table cannot run this hazard yet.", cut.Find("[role=alert]").TextContent));
        Assert.Null(_saved);
        _homebrew.DidNotReceive().TrySaveAsync(Arg.Any<HomebrewEntryDocument>(), Arg.Any<CampaignAccess>());

        // Mending it lets it save.
        GoTo(cut, "Notice");
        Control(cut, "hazard-detect", "Check").Change("Perception");
        Assert.Empty(cut.FindAll("[data-testid=hazard-shape]"));
        Save(cut);
    }

    [Fact]
    public void NumbersOffTheSystemsGuide_AreWarnedAbout_ButNeverStopTheSave()
    {
        var cut = Open();
        GoTo(cut, "Profile");
        Field(cut, "Levels").Change("5");
        GoTo(cut, "Effect");
        Field(cut, "Save").Change("Constitution");
        Field(cut, "Save DC").Change("40");

        Assert.Contains("Save DC 40 is high for a level 5 hazard", cut.Find("[data-testid=hazard-scale]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=hazard-shape]"));

        Save(cut);
        Assert.Equal(40L, Convert.ToInt64(_saved!.Properties["saveDc"]));
    }

    [Fact]
    public void FillingFromTheGuide_PutsInTheSaveDcAndTheDamageForTheLevel_AndTheyStayEditable()
    {
        var cut = Open();
        GoTo(cut, "Profile");
        Field(cut, "Levels").Change("5-10");
        cut.Find("[data-testid=hazard-fill]").Click();
        // No save yet, so a DC would be a shape problem: only the damage is filled, and it says so.
        Assert.Equal("Filled in from the guide for level 5-10: Damage.", cut.Find("[data-testid=hazard-fill-note]").TextContent.Trim());

        GoTo(cut, "Effect");
        Field(cut, "Save").Change("Dexterity");
        GoTo(cut, "Profile");
        cut.Find("[data-testid=hazard-fill]").Click();
        Assert.Contains("Save DC, Damage", cut.Find("[data-testid=hazard-fill-note]").TextContent);

        GoTo(cut, "Effect");
        Assert.Equal("15", Field(cut, "Save DC").GetAttribute("value"));
        Assert.Equal("4d10", Control(cut, "hazard-damage", "Dice (such as 2d8)").GetAttribute("value"));
        Control(cut, "hazard-damage", "Dice (such as 2d8)").Change("5d10");
        Save(cut);

        Assert.Equal("5d10", HazardNumbers.EditableRows(_saved!.Properties["damage"]).Single()["dice"]);
        Assert.Equal(15L, Convert.ToInt64(_saved.Properties["saveDc"]));
        Assert.Empty(HazardNumbers.ScalingProblems(_saved.ToEntry(), Catalog.GetDefinition("srd5e")!.EntityTypes.Single(t => t.Type == "hazard"), Catalog.GetDefinition("srd5e")!));
    }

    [Fact]
    public void ARowLeftBlank_IsNeverSaved()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();
        GoTo(cut, "Effect");
        cut.Find("[data-testid=hazard-damage-add]").Click();
        cut.Find("[data-testid=hazard-conditions-add]").Click();
        GoTo(cut, "hazard:routine");
        cut.Find("[data-testid=hazard-routine-add]").Click();

        Save(cut);

        Assert.DoesNotContain("detect", _saved!.Properties.Keys);
        Assert.DoesNotContain("damage", _saved.Properties.Keys);
        Assert.DoesNotContain("conditions", _saved.Properties.Keys);
        Assert.DoesNotContain("routine", _saved.Properties.Keys);
    }

    [Fact]
    public void ABandRestatesItsDamageAndTheComplexHazardsOneStrikingStep_AndBothAreSaved()
    {
        var cut = Open();
        GoTo(cut, "hazard:routine");
        cut.Find("[data-testid=hazard-routine-add]").Click();
        Control(cut, "hazard-routine", "Name").Change("Crush");
        Control(cut, "hazard-routine", "Applies the hazard's effect to creatures").Change(true);
        // A striking step needs an effect to apply, which the same checks say.
        Assert.Contains("has no effect", cut.Find("[data-testid=hazard-shape]").TextContent);
        GoTo(cut, "Effect");
        cut.Find("[data-testid=hazard-damage-add]").Click();
        Control(cut, "hazard-damage", "Dice (such as 2d8)").Change("2d10");
        Assert.Empty(cut.FindAll("[data-testid=hazard-shape]"));

        GoTo(cut, "hazard:bands");
        cut.Find("[data-testid=hazard-higherLevels-add]").Click();
        Control(cut, "hazard-higherLevels", "Levels (such as 5-10)").Change("5-10");
        cut.Find("[data-testid=hazard-higherLevels-damage-add]").Click();
        cut.Find("[data-testid=hazard-higherLevels] [data-testid=hazard-higherLevels-damage] [data-testid=hazard-row] input[type=text]").Change("4d10");
        Save(cut);

        var band = Assert.Single(HazardNumbers.EditableRows(_saved!.Properties["higherLevels"]));
        Assert.Equal("5-10", band["levels"]);
        Assert.Equal("4d10", Assert.Single(HazardNumbers.EditableRows(band["damage"]))["dice"]);
        Assert.True((bool)Assert.Single(HazardNumbers.EditableRows(_saved.Properties["routine"]))["strikes"]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryStepOfTheHazardMode_ReadsInTheLanguage_WithNoKeyLeftShowing(string culture)
    {
        var cut = Open(culture);
        foreach (var step in cut.FindAll("[data-testid^='wizard-step-']").Select(s => s.GetAttribute("data-testid")!).ToList())
        {
            cut.Find($"[data-testid='{step}']").Click();
            Assert.DoesNotContain("Wizard.Hazard.", cut.Markup);
            Assert.DoesNotContain("Actor.Form.", cut.Markup);
        }

        GoTo(cut, "Notice");
        var add = cut.Find("[data-testid=hazard-detect-add]").TextContent.Trim();
        Assert.Equal(Localizer.For(culture)["Wizard.Hazard.Add", Localizer.For(culture)["Wizard.Hazard.Row.detect"]], add);
        if (culture != "en") Assert.NotEqual("Add detect route", add);
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Assert.StartsWith(char.ToUpperInvariant(Localizer.For(culture)["Wizard.Hazard.Row.detect"][0]).ToString(), cut.Find("[data-testid=hazard-row] .label-caps").TextContent.Trim());
    }

    [Fact]
    public void AnotherTypeIsAsItWas_WithNoHazardChecksOrRowEditors()
    {
        var cut = Open(type: "magic-item");

        Assert.Empty(cut.FindAll("[data-testid=hazard-checks]"));
        Assert.Empty(cut.FindAll("[data-testid^=hazard-]"));
        Assert.DoesNotContain("Level bands", cut.Markup);
    }

}
