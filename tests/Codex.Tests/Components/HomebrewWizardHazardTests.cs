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

    private IRenderedComponent<HomebrewWizard> Open(string culture = "en", string type = "hazard", HomebrewEntryDocument? entry = null)
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
            System = Catalog.GetLocalizedDefinition("srd5e", culture),
            Actors = [],
            AllActors = [],
            ReloadAsync = () => Task.CompletedTask,
            Notify = _ => { },
            AccessChangedAsync = () => Task.CompletedTask
        };
        var cut = Render<HomebrewWizard>(p => p.AddCascadingValue(context));
        cut.InvokeAsync(() => cut.Instance.OpenAsync(entry ?? new HomebrewEntryDocument { CampaignId = "c1", Type = type, Name = "Test pit" })).GetAwaiter().GetResult();
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
        // The button is on the step that holds the numbers it fills, so a new hazard is filled where the game master sees it.
        Assert.Empty(cut.FindAll("[data-testid=hazard-fill]"));
        GoTo(cut, "Effect");
        cut.Find("[data-testid=hazard-fill]").Click();
        // No save yet, so a DC would be a shape problem: only the damage is filled, and it says so.
        Assert.Equal("Filled in from the guide for level 5-10: Damage.", cut.Find("[data-testid=hazard-fill-note]").TextContent.Trim());

        Field(cut, "Save").Change("Dexterity");
        cut.Find("[data-testid=hazard-fill]").Click();
        Assert.Equal("Filled in from the guide for level 5-10: Save DC.", cut.Find("[data-testid=hazard-fill-note]").TextContent.Trim());

        Assert.Equal("15", Field(cut, "Save DC").GetAttribute("value"));
        Assert.Equal("4d10", Control(cut, "hazard-damage", "Dice (such as 2d8)").GetAttribute("value"));
        Control(cut, "hazard-damage", "Dice (such as 2d8)").Change("5d10");
        Save(cut);

        Assert.Equal("5d10", HazardNumbers.EditableRows(_saved!.Properties["damage"]).Single()["dice"]);
        Assert.Equal(15L, Convert.ToInt64(_saved.Properties["saveDc"]));
        Assert.Empty(HazardNumbers.ScalingProblems(_saved.ToEntry(), Catalog.GetDefinition("srd5e")!.EntityTypes.Single(t => t.Type == "hazard"), Catalog.GetDefinition("srd5e")!));
    }

    [Fact]
    public void FillingFromTheGuide_ReadsTheHazardsSeverityRow_AndFillsAValueInsideItsRange()
    {
        var cut = Open();
        GoTo(cut, "Profile");
        Field(cut, "Levels").Change("5-10");
        Field(cut, "Severity").Change("Deadly");
        GoTo(cut, "Effect");
        Field(cut, "Save").Change("Dexterity");
        cut.Find("[data-testid=hazard-fill]").Click();

        // Deadly 5-10 is a save DC of 13 to 15 and an average of 14 to 24: the middle of each, damage as the severity-less row's dice.
        Assert.Equal("Filled in from the guide for level 5-10: Save DC, Damage.", cut.Find("[data-testid=hazard-fill-note]").TextContent.Trim());
        Assert.Equal("14", Field(cut, "Save DC").GetAttribute("value"));
        Assert.Equal("3d10", Control(cut, "hazard-damage", "Dice (such as 2d8)").GetAttribute("value"));
        Save(cut);
        Assert.Empty(HazardNumbers.ScalingProblems(_saved!.ToEntry(), Catalog.GetDefinition("srd5e")!.EntityTypes.Single(t => t.Type == "hazard"), Catalog.GetDefinition("srd5e")!));
    }

    [Fact]
    public void FillingFromTheGuide_KeepsEveryDamagePart_AndNeverReplacesANumberTheGmWrote()
    {
        var entry = new HomebrewEntryDocument
        {
            CampaignId = "c1", Type = "hazard", Name = "Spiked pit",
            Properties =
            {
                ["level"] = "5-10", ["saveAttribute"] = "Dexterity", ["saveDc"] = 12,
                ["damage"] = new List<object> { Row(("dice", ""), ("type", "bludgeoning")), Row(("dice", "2d10"), ("type", "piercing")) }
            }
        };
        var cut = Open(entry: entry);
        GoTo(cut, "Effect");
        cut.Find("[data-testid=hazard-fill]").Click();

        // The empty first part gets the guide's dice and keeps its type; the second part and the typed DC are left alone.
        Assert.Equal("Filled in from the guide for level 5-10: Damage.", cut.Find("[data-testid=hazard-fill-note]").TextContent.Trim());
        cut.Find("[data-testid=hazard-fill]").Click();
        Assert.Contains("nothing to fill in", cut.Find("[data-testid=hazard-fill-note]").TextContent);
        Save(cut);

        var parts = HazardNumbers.EditableRows(_saved!.Properties["damage"]);
        Assert.Equal(2, parts.Count);
        Assert.Equal("4d10", parts[0]["dice"]);
        Assert.Equal("bludgeoning", parts[0]["type"]);
        Assert.Equal("2d10", parts[1]["dice"]);
        Assert.Equal(12L, Convert.ToInt64(_saved.Properties["saveDc"]));
    }

    private static Dictionary<string, object> Row(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void RemovingARow_ShowsTheNextRowsOwnText_NeverTheRemovedOnes()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        for (var i = 0; i < 3; i++) cut.Find("[data-testid=hazard-detect-add]").Click();
        Control(cut, "hazard-detect", "Name", 0).Change("First");
        Control(cut, "hazard-detect", "Name", 1).Change("Second");
        Control(cut, "hazard-detect", "Name", 2).Change("Third");
        Control(cut, "hazard-detect", "Notes", 0).Change("note of first");
        Control(cut, "hazard-detect", "Notes", 1).Change("note of second");
        Control(cut, "hazard-detect", "Notes", 2).Change("note of third");

        cut.FindAll("[data-testid=hazard-detect] > [data-testid=hazard-row] .collection-row-head button")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=confirm-accept]").Click());

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=hazard-detect] > [data-testid=hazard-row]").Count));
        Assert.Equal("note of second", Control(cut, "hazard-detect", "Notes", 0).GetAttribute("value"));
        Assert.Equal("note of third", Control(cut, "hazard-detect", "Notes", 1).GetAttribute("value"));
        Assert.Equal("Second", Control(cut, "hazard-detect", "Name", 0).GetAttribute("value"));
    }

    [Fact]
    public void ASavedHazardOfEveryField_OpensInTheWizard_AndSavesUnchanged()
    {
        var entry = new HomebrewEntryDocument
        {
            CampaignId = "c1", Type = "hazard", Name = "The everything trap",
            Properties =
            {
                ["kind"] = "trap", ["severity"] = "Deadly", ["level"] = "5-10", ["trigger"] = "Someone opens the lock.", ["duration"] = "1 minute",
                ["saveAttribute"] = "Constitution", ["saveDc"] = 13, ["onSave"] = "half", ["area"] = "10-foot cone", ["depth"] = "20 feet", ["effect"] = "Gas pours out.",
                ["armorClass"] = 15, ["hitPoints"] = 20, ["damageThreshold"] = 5, ["immunities"] = new List<object> { "poison", "psychic" },
                ["initiative"] = 8, ["reset"] = "Resets after a minute.", ["complex"] = true,
                ["detect"] = new List<object>
                {
                    Row(("id", "lock"), ("name", "Inspect the lock"), ("action", "Search"), ("check", "Perception"), ("dc", 15), ("text", "A thin seam."), ("flavour", "kept")),
                    Row(("name", "Read the glyph"), ("after", "lock"), ("check", "arcana"), ("dc", 15), ("passive", false), ("note", "a key the editor has no column for"))
                },
                ["disarm"] = new List<object>
                {
                    Row(("name", "Pick it"), ("check", "Sleight of Hand"), ("dc", 15), ("successes", 2), ("triggersOn", new List<object> { "failure", "criticalFailure" })),
                    Row(("name", "Wedge it"), ("needs", "An Iron Spike")),
                    Row(("name", "Pick the lock"), ("check", "Thieves' Tools"), ("dc", 14)),
                    Row(("name", "Dispel the ward"), ("check", "Spellcasting"), ("dc", 16))
                },
                ["methods"] = new List<object> { Row(("name", "Smash it"), ("needs", "A hammer"), ("after", "lock")) },
                ["damage"] = new List<object> { Row(("dice", "1d10"), ("type", "poison")), Row(("dice", "1d6"), ("type", "piercing"), ("count", "1d3")) },
                ["conditions"] = new List<object> { Row(("name", "Poisoned"), ("lasts", "1 hour")) },
                ["routine"] = new List<object> { Row(("name", "Crush"), ("description", "The stone rolls."), ("strikes", true)), Row(("name", "Rumble"), ("description", "Dust falls.")) },
                ["higherLevels"] = new List<object>
                {
                    Row(("levels", "11-16"), ("saveDc", 15), ("severity", "Nuisance"), ("area", "20-foot cone"),
                        ("conditions", new List<object> { Row(("name", "Blinded"), ("lasts", "1 minute")) }),
                        ("damage", new List<object> { Row(("dice", "2d10"), ("type", "poison")) }),
                        ("methods", new List<object> { Row(("name", "Cover it"), ("needs", "A sack")) }))
                }
            }
        };
        var before = Canon(entry.Properties);
        var cut = Open(entry: entry);
        foreach (var step in cut.FindAll("[data-testid^='wizard-step-']").Select(s => s.GetAttribute("data-testid")!).ToList()) cut.Find($"[data-testid='{step}']").Click();

        Save(cut);

        Assert.Equal(before, Canon(_saved!.Properties));
        Assert.Equal("The everything trap", _saved.Name);
    }

    [Fact]
    public void ACheckTheSystemDoesNotKnow_IsTypedUnderOther_StoredAsWritten_AndNeverLostByTheSelect()
    {
        var cut = Open();
        GoTo(cut, "Disable");
        cut.Find("[data-testid=hazard-disarm-add]").Click();
        Assert.Empty(cut.FindAll("[data-testid=hazard-disarm] > [data-testid=hazard-row] input[id$='-check-other']"));

        var select = Control(cut, "hazard-disarm", "Check");
        Assert.Contains(select.QuerySelectorAll("option"), o => o.TextContent.Trim() == "Other…");
        select.Change("\u0001other");

        // The text box has its own visible label, and the select keeps showing Other.
        var typed = Control(cut, "hazard-disarm", "Check, as written (a typed bonus is asked for at the table)");
        typed.Change("Spellcasting");
        Control(cut, "hazard-disarm", "DC").Change("16");

        // Moving the select to a listed check does not lose the word: it stays on offer and can be chosen again.
        Control(cut, "hazard-disarm", "Check").Change("perception");
        var word = Control(cut, "hazard-disarm", "Check").QuerySelectorAll("option").First(o => o.GetAttribute("value") == "Spellcasting");
        Assert.Equal("Spellcasting", word.TextContent.Trim());
        Control(cut, "hazard-disarm", "Check").Change("Spellcasting");
        Save(cut);

        var saved = Assert.Single(HazardNumbers.EditableRows(_saved!.Properties["disarm"]));
        Assert.Equal("Spellcasting", saved["check"]);
        Assert.Equal(16L, saved["dc"]);
    }

    [Fact]
    public void FocusFollowsTheAction_ANewRowOpensOnItsFirstBox_ARemovedRowHandsFocusToTheAddButton_OtherToItsTextBox()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();

        var name = Control(cut, "hazard-detect", "Name");
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "codex.focusById" && (string?)i.Arguments[0] == name.Id));

        Control(cut, "hazard-detect", "Check").Change("\u0001other");
        var typed = Control(cut, "hazard-detect", "Check, as written (a typed bonus is asked for at the table)");
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "codex.focusById" && (string?)i.Arguments[0] == typed.Id));

        var add = cut.Find("[data-testid=hazard-detect-add]");
        cut.Find("[data-testid=hazard-detect] > [data-testid=hazard-row] .collection-row-head button").Click();
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "codex.focusById" && (string?)i.Arguments[0] == add.Id));
    }

    [Theory]
    [InlineData("ru", "есть Сл, но нет проверки")]
    [InlineData("he", "יש דרגת קושי אך אין בדיקה")]
    public void TheShapeCheckOfTheWizard_IsSaidInTheLanguage_InTheLiveRegionAndTheBlockedSave(string culture, string words)
    {
        var cut = Open(culture);
        GoTo(cut, "Notice");
        var l = Localizer.For(culture);
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Control(cut, "hazard-detect", l["Wizard.Hazard.Col.Name"]).Change("Look");
        Control(cut, "hazard-detect", l["Wizard.Hazard.Col.Dc"]).Change("15");

        Assert.Equal("polite", cut.Find("[data-testid=hazard-checks]").GetAttribute("aria-live"));
        Assert.Contains(words, cut.Find("[data-testid=hazard-shape]").TextContent);
        Assert.DoesNotContain("has a dc and no check", cut.Markup);

        cut.Find("form.wizard").Submit();
        cut.WaitForAssertion(() => Assert.Contains(words, cut.Find("[role=alert]").TextContent));
    }

    [Fact]
    public void TheGuideCheckOfTheWizard_NamesTheFieldAndTheSeverity_InTheSystemTranslation()
    {
        // The library's types follow the request's culture, as they do for a Hebrew reader.
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("he");
        try
        {
            var cut = Open("he");
            GoTo(cut, "Profile");
            Field(cut, "דרגות").Change("1-4");
            Field(cut, "חומרה").Change("Nuisance");
            GoTo(cut, "Effect");
            Field(cut, "הצלת מגן").Change("Dexterity");
            Field(cut, "דרגת קושי להצלה").Change("19");

            var scale = cut.Find("[data-testid=hazard-scale]").TextContent;
            Assert.Contains("דרגת קושי להצלה", scale);
            Assert.Contains("מטרד", scale);
            Assert.DoesNotContain("Nuisance", scale);
            Assert.DoesNotContain("Save DC", scale);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void ADiceBoxAndALevelBoxAreLeftToRight_AndARouteCannotWaitForItself()
    {
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Assert.Equal("ltr", Control(cut, "hazard-detect", "DC", 0).GetAttribute("dir"));
        Control(cut, "hazard-detect", "Short name (for “Only after”)", 0).Change("lock");
        Control(cut, "hazard-detect", "Short name (for “Only after”)", 1).Change("glyph");

        var first = Control(cut, "hazard-detect", "Only after", 0).QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToList();
        Assert.DoesNotContain("lock", first);
        Assert.Contains("glyph", first);
        GoTo(cut, "Effect");
        cut.Find("[data-testid=hazard-damage-add]").Click();
        Assert.Equal("ltr", Control(cut, "hazard-damage", "Dice (such as 2d8)").GetAttribute("dir"));
    }

    [Fact]
    public void ADisarmRoute_MayWaitForTheDetectRouteThatSharesItsId()
    {
        // Ids are unique per list: "disarm the glyph once the glyph is found" is a disarm route `glyph` after the detect route `glyph`.
        var cut = Open();
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();
        Control(cut, "hazard-detect", "Name").Change("Spot the glyph");
        Control(cut, "hazard-detect", "Short name (for “Only after”)").Change("glyph");
        GoTo(cut, "Disable");
        cut.Find("[data-testid=hazard-disarm-add]").Click();
        Control(cut, "hazard-disarm", "Name").Change("Erase the glyph");
        Control(cut, "hazard-disarm", "Short name (for “Only after”)").Change("glyph");

        var offered = Control(cut, "hazard-disarm", "Only after").QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains("glyph", offered);
    }

    // A value as text that does not depend on the collection types the store happens to give: maps by sorted key, lists in order, everything else as invariant text.
    private static string Canon(object? value) => value switch
    {
        null => "null",
        string s => s,
        System.Collections.IDictionary map => "{" + string.Join(",", map.Keys.Cast<object>().Select(k => (k.ToString() ?? "", map[k])).OrderBy(p => p.Item1, StringComparer.Ordinal).Select(p => p.Item1 + "=" + Canon(p.Item2))) + "}",
        System.Collections.IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Canon)) + "]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    [Theory]
    [InlineData("en", "Perception")]
    [InlineData("ru", "Внимательность")]
    public void TheChecksOnOffer_AreTheSystemsNamesInTheLanguage_AndAreStoredAsTheKey(string culture, string name)
    {
        var cut = Open(culture);
        GoTo(cut, "Notice");
        cut.Find("[data-testid=hazard-detect-add]").Click();

        var row = cut.Find("[data-testid=hazard-detect] > [data-testid=hazard-row]");
        var check = row.QuerySelectorAll("select").First(s => s.QuerySelectorAll("option").Any(o => o.GetAttribute("value") == "perception"));
        var option = check.QuerySelectorAll("option").First(o => o.GetAttribute("value") == "perception");
        Assert.Equal(name, option.TextContent.Trim());
        check.Change("perception");
        Save(cut);

        Assert.Equal("perception", Assert.Single(HazardNumbers.EditableRows(_saved!.Properties["detect"]))["check"]);
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
