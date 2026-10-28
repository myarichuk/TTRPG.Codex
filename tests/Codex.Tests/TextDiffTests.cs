using Codex.Core;
using Codex.Persistence;

namespace Codex.Tests;

/// <summary>The word diff an edit is reviewed with, and picking homebrew by tag for an export.</summary>
public class TextDiffTests
{
    // Whatever the pieces, the unchanged and removed ones are the old text and the unchanged and added ones the new.
    private static void AssertRebuilds(string before, string after, IReadOnlyList<DiffPiece> pieces)
    {
        Assert.Equal(before, string.Concat(pieces.Where(p => p.Kind != DiffKind.Added).Select(p => p.Text)));
        Assert.Equal(after, string.Concat(pieces.Where(p => p.Kind != DiffKind.Removed).Select(p => p.Text)));
    }

    [Fact]
    public void SameText_IsOneUnchangedPiece_AndSpacingAloneIsNoChange()
    {
        var pieces = TextDiff.Words("The bell rings.", "The bell rings.");

        Assert.Equal(new[] { new DiffPiece(DiffKind.Same, "The bell rings.") }, pieces);
        Assert.False(TextDiff.Changed("The bell\n rings.", "The  bell rings. "));
        Assert.True(TextDiff.Changed("The bell rings.", "The bell rang."));
    }

    [Theory]
    [InlineData("The bell rings twice at low tide.", "The cracked bell rings once at low tide.")]
    [InlineData("a b c d", "a c d")]
    [InlineData("a c d", "a b c d")]
    [InlineData("## Arrival\nGulls wheel.\n\n## Who\nWendel.", "## Arrival\nGulls wheel over the racks.\n\n## Who is here\nWendel Tarr.")]
    [InlineData("", "All new.")]
    [InlineData("All gone.", "")]
    [InlineData("x y", "p x q y r")]
    public void Pieces_RebuildBothTexts(string before, string after) => AssertRebuilds(before, after, TextDiff.Words(before, after));

    [Fact]
    public void AReplacedRun_ReadsAsOneRemovalAndOneAddition()
    {
        var pieces = TextDiff.Words("Keep this one two three and that.", "Keep this four five six and that.");

        Assert.Equal(new[] { DiffKind.Same, DiffKind.Removed, DiffKind.Added, DiffKind.Same }, pieces.Select(p => p.Kind));
        Assert.Equal("one two three", pieces[1].Text);
        Assert.Equal("four five six", pieces[2].Text);
        AssertRebuilds("Keep this one two three and that.", "Keep this four five six and that.", pieces);
    }

    [Fact]
    public void TwoLongUnrelatedTexts_AreOneRemovalAndOneAddition_RatherThanASlowComparison()
    {
        var before = string.Join(' ', Enumerable.Range(0, 3000).Select(i => $"old{i}"));
        var after = string.Join(' ', Enumerable.Range(0, 3000).Select(i => $"new{i}"));

        var pieces = TextDiff.Words(before, after);

        Assert.Equal(new[] { DiffKind.Removed, DiffKind.Added }, pieces.Select(p => p.Kind));
        AssertRebuilds(before, after, pieces);
    }

    [Fact]
    public void HomebrewTags_CountsEachTagOncePerEntry_AndPicksEntriesCarryingAnyTag_WhateverTheirCaseOrSpacing()
    {
        var ward = new HomebrewEntryDocument { Name = "Brine Ward", Tags = { "Grey Coast", "salt", "grey  coast" } };
        var rime = new HomebrewEntryDocument { Name = "Rimeburst", Tags = { "grey coast", "cold" } };
        var plain = new HomebrewEntryDocument { Name = "Plain" };
        var all = new[] { ward, rime, plain };

        Assert.Equal(new[] { ("Grey Coast", 2), ("cold", 1), ("salt", 1) }, HomebrewTags.Tally(all));
        Assert.Equal(new[] { ward, rime }, HomebrewTags.Tagged(all, new[] { "GREY COAST " }));
        Assert.Equal(new[] { ward, rime }, HomebrewTags.Tagged(all, new[] { "salt", "cold" }));
        Assert.Equal(all, HomebrewTags.Tagged(all, Array.Empty<string>())); // nothing picked: everything
    }
}
