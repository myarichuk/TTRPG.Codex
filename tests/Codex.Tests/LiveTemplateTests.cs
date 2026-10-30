using Codex.Core;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Tests;

public class LiveTemplateTests
{
    [Fact]
    public void Expand_FillsDefaults_MirrorsRepeatedNames_AndPutsTheCaretAtEnd()
    {
        var expansion = LiveTemplate.Expand("**$name:Bandits$** ambush at $where$.$END$ Then $name$ flee. Costs 5$$.");

        Assert.Equal("**Bandits** ambush at . Then Bandits flee. Costs 5$.", expansion.Text);
        Assert.Collection(expansion.Fields,
            f => Assert.Equal(new TemplateField("name", 2, 7, false), f),
            f => Assert.Equal(new TemplateField("where", 22, 0, false), f),
            f => Assert.Equal(new TemplateField("name", 29, 7, true), f));
        Assert.Equal(23, expansion.End);
    }

    [Fact]
    public void Expand_WithoutEnd_PutsTheCaretAtTheEnd_AndLeavesStrayDollarsAlone()
    {
        var expansion = LiveTemplate.Expand("Gives $Longsword and $12 to $who$");

        Assert.Equal("Gives $Longsword and $12 to ", expansion.Text);
        Assert.Equal(expansion.Text.Length, expansion.End);
        Assert.Equal(new[] { "who" }, LiveTemplate.FieldNames("Gives $Longsword and $12 to $who$"));
    }

    [Fact]
    public void Expand_TakesNamesInAnyScript_AndALaterDefaultForANameWithoutOne()
    {
        var expansion = LiveTemplate.Expand("$имя$ и $имя:Мара$, $שם$");

        Assert.Equal("Мара и Мара, ", expansion.Text);
        Assert.Equal(new[] { "имя", "שם" }, expansion.Fields.Where(f => !f.Mirror).Select(f => f.Name));
    }

    [Fact]
    public void Sample_ShowsEmptyFieldsByName_AndDefaultsAsThemselves()
    {
        Assert.Equal("## Look\n‹look and voice›, aged 40", LiveTemplate.Sample("## Look\n$look_and_voice$, aged $age:40$"));
    }

    [Theory]
    [InlineData("npc", true)]
    [InlineData("enc-2", true)]
    [InlineData("нип", true)]
    [InlineData("", true)]
    [InlineData("two words", false)]
    [InlineData("a$b", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void Abbreviations_AreOneWord(string abbreviation, bool valid) =>
        Assert.Equal(valid, LiveTemplate.IsValidAbbreviation(abbreviation));

    private const string Person = "Opening $who$.\n\n## Look\n$look$\n\n## History\n$history$\n\n## Wants\n$wants$\n\n:::dm\nSecret: $secret$\n:::\n\n## Level\n$level:3$\n";

    [Fact]
    public void AddMissingSections_PutsEachWhereTheTemplateHasIt_AndLeavesWhatIsThereAlone()
    {
        var page = "Mara keeps the lighthouse.\n\n## look\nTall, grey-eyed.\n\n## Wants\nTo be left alone.\n\n## Notes\nMine.";

        var result = LiveTemplate.AddMissingSections(page, Person);

        Assert.Equal(
            "Mara keeps the lighthouse.\n\n## look\nTall, grey-eyed.\n\n## History\n\n## Wants\nTo be left alone.\n\n## Notes\nMine.\n\n## Level\n3\n",
            result);
    }

    [Fact]
    public void AddMissingSections_KeepsWhatHasText_AndDropsSecretBlocksAndListItemsLeftEmpty()
    {
        var template = "## Truth\n:::dm\nWhat it hides: $secret$\n:::\n\n## Hidden\n:::dm\n$more$\n:::\n\n## Clues\n- $clue$\n- Footprints\n1. $step$\n";

        var result = LiveTemplate.AddMissingSections("Intro.", template);

        Assert.Equal("Intro.\n\n## Truth\n:::dm\nWhat it hides:\n:::\n\n## Hidden\n\n## Clues\n- Footprints\n", result);
    }

    [Fact]
    public void AddMissingSections_IsNull_WhenNothingIsMissing_OrTheTemplateHasNoHeadings()
    {
        Assert.Null(LiveTemplate.AddMissingSections("# LOOK\n\n### history:\n## wants\n## level", Person));
        Assert.Null(LiveTemplate.AddMissingSections("Anything.", "**$name$** - $what$"));
    }

    [Fact]
    public void AddMissingSections_ToAnEmptyPage_GivesTheTemplatesSectionsWithoutItsOpening()
    {
        Assert.Equal("## Look\n\n## History\n\n## Wants\n\n:::dm\nSecret:\n:::\n\n## Level\n3\n", LiveTemplate.AddMissingSections("", Person));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("he")]
    public void BuiltInTemplates_InEveryLanguage_AreWellFormed_AndOutlineAsManySectionsAsInEnglish(string language)
    {
        var english = BuiltInTemplates.For(Localizer.For("en"));
        var templates = BuiltInTemplates.For(Localizer.For(language));

        Assert.Equal(english.Select(t => (t.Id, t.Abbreviation, t.PageKind)), templates.Select(t => (t.Id, t.Abbreviation, t.PageKind)));
        foreach (var (template, en) in templates.Zip(english))
        {
            Assert.DoesNotContain("Template.", template.Name);
            Assert.True(LiveTemplate.IsValidAbbreviation(template.Abbreviation));
            Assert.Equal(LiveTemplate.FieldNames(en.Body).Count, LiveTemplate.FieldNames(template.Body).Count);
            // Every "$" is part of a field: nothing of the syntax is left in the text it puts in.
            Assert.DoesNotContain("$", LiveTemplate.Expand(template.Body).Text);
            Assert.Equal(LiveTemplate.Headings(en.Body).Count, LiveTemplate.Headings(template.Body).Count);
            Assert.Equal(SecretText.Blocks(en.Body).Count, SecretText.Blocks(template.Body).Count);
            if (template.PageKind != null) Assert.True(LiveTemplate.Headings(template.Body).Count >= 4, template.Id);
        }
    }

    [Fact]
    public void Headings_IgnoreClosingHashesAndEmptyHeadings()
    {
        Assert.Equal(new[] { (2, "Look"), (3, "Who") }, LiveTemplate.Headings("## Look ##\n#\n###   Who  \nnot # a heading"));
    }
}
