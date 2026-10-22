using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;

namespace Codex.Tests;

public class RefCardTests
{
    [Fact]
    public void Body_DropsTheItalicHeaderAndLabelledLines()
    {
        const string description = "*Level 2 Evocation (Wizard)*\n\n**Casting Time:** Action  \n**Range:** 90 feet\n\nA green arrow streaks out.\n\n**Using a Higher-Level Spell Slot.** More.";

        var body = RefCard.Body(description, "Casting Time", "Range", "Components", "Duration");

        Assert.StartsWith("A green arrow", body);
        Assert.Contains("Higher-Level", body);
        Assert.DoesNotContain("Casting Time", body);
    }

    [Theory]
    [InlineData(10, "+0")]
    [InlineData(0, "−5")]
    [InlineData(21, "+5")]
    [InlineData(9, "−1")]
    [InlineData(1, "−5")]
    public void Modifier_FollowsTheScore(int score, string expected) => Assert.Equal(expected, RefCard.Modifier(score));

    [Fact]
    public void CanShow_NeedsTheStructuredFields()
    {
        var bare = new ContentEntry { Id = "x", Type = "monster", Name = "Bare" };
        Assert.False(MonsterCard.CanShow(bare));
        Assert.False(SpellCard.CanShow(new ContentEntry { Id = "s", Type = "spell", Name = "S" }));
        Assert.True(ItemCard.CanShow(new ContentEntry { Id = "i", Type = "equipment", Name = "I", Properties = { ["category"] = "Armor" } }));
    }

    [Theory]
    [InlineData("Martial Melee Weapons", "weapon")]
    [InlineData("Wondrous Items", "wondrous")]
    [InlineData("Artisan's Tools", "tool")]
    [InlineData("Adventuring Gear", "gear")]
    [InlineData("Rings", "ring")]
    public void ItemKey_MapsCategoriesToGlyphs(string category, string expected) => Assert.Equal(expected, DomainIconMap.ItemKey(category));
}
