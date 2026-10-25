using Codex.Core.Systems;
using Codex.Localization;
using Codex.Web.Components.Shared;
using Codex.Web.Services;

namespace Codex.Tests;

/// <summary>Entry tags are shown in the reader's language; the tags themselves stay as the packs wrote them.</summary>
public class TagLabelsTests
{
    private static readonly Codex.Plugin.Abstractions.SystemDefinition Srd =
        SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    [Theory]
    [InlineData("he", "magic-item", "rare", "נדיר")]           // the type's own rarity option
    [InlineData("he", "equipment", "inhaled", "רעל שאיפה")]    // the system's value translations
    [InlineData("ru", "spell", "evocation", "Воплощение")]
    [InlineData("ru", "monster", "large", "Большой")]
    [InlineData("he", "spell", "level-3", "דרגה 3")]
    [InlineData("ru", "monster", "cr-1/4", "Опасность 1/4")]
    [InlineData("en", "monster", "cr-1/4", "Challenge 1/4")]
    [InlineData("en", "equipment", "inhaled", "inhaled")]
    [InlineData("he", "equipment", "made-up-tag", "made-up-tag")]
    public void Show_TranslatesTheLabel(string culture, string type, string tag, string expected)
    {
        var shown = TagLabels.Show(tag, Srd.Localize(culture).FindType(type), Srd, Localizer.For(culture));

        Assert.Equal(expected, shown);
    }

    [Fact]
    public void EveryShippedTag_HasAHebrewAndRussianLabel()
    {
        var tags = Directory.EnumerateFiles(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "packs", "srd-5.2.1"), "*.yaml", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines)
            .Select(line => line.TrimEnd())
            .Aggregate((Tags: new HashSet<string>(), InTags: false), (state, line) =>
            {
                if (line == "tags:") return (state.Tags, true);
                if (state.InTags && line.StartsWith("- ")) state.Tags.Add(line[2..].Trim('"', '\''));
                else state.InTags = false;
                return state;
            }).Tags;

        foreach (var culture in new[] { "he", "ru" })
        {
            var l = Localizer.For(culture);
            var untranslated = tags.Where(t => TagLabels.Show(t, null, Srd, l) == t).Order().ToList();
            Assert.True(untranslated.Count == 0, $"{culture}: {string.Join(", ", untranslated)}");
        }
    }

    [Fact]
    public void RowsAndValues_NeverPrintANestedMapAsText()
    {
        var row = new Dictionary<string, object> { ["name"] = "Bite", ["description"] = new Dictionary<string, object> { ["text"] = "x" }, ["uses"] = 2L };

        Assert.Equal("Bite", RefCard.Cell(row, "name"));
        Assert.Null(RefCard.Cell(row, "description"));
        Assert.Equal("2", RefCard.Cell(row, "uses"));
        Assert.True(RefCard.IsPlain(new List<object> { "a", 1L }));
        Assert.False(RefCard.IsPlain(new List<object> { new Dictionary<string, object>() }));
        Assert.False(RefCard.IsPlain(new Dictionary<string, object>()));
    }
}
