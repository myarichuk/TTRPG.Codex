using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// The stat blocks and tables of the campaign's homebrew (docs/BOOK_EXPORT.md 46): written by code from the system's own description of the type,
/// in any system; every value through the draft's text path; a block an AI edit keeps back whole, so its numbers never reach a model.
/// </summary>
public class BookStatBlocksTests
{
    private static readonly BookTestCatalog Catalog = new();

    private static string? Mention(string kind, string id) => id == "a-vera" ? "Vera" : null;

    private static HomebrewEntryDocument Drowned() => new()
    {
        Id = "h-drowned", Type = "monster", Name = "Drowned Keeper",
        Properties =
        {
            ["size"] = "Medium", ["creatureType"] = "undead", ["challengeRating"] = "2", ["armorClass"] = 13, ["hitPoints"] = 45, ["hitDice"] = "6d8+18",
            ["speed"] = "30 ft., swim 30 ft.", ["strength"] = 16, ["dexterity"] = 9, ["constitution"] = 17, ["intelligence"] = 6, ["wisdom"] = 10, ["charisma"] = 5,
            ["senses"] = "darkvision 60 ft.", ["resistances"] = new List<object> { "cold", "necrotic" },
            ["actions"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Claw", ["description"] = "*Melee Attack Roll:* +5, as [[actor:a-vera]] saw, and [[actor:a-hidden|CANARY_LABEL]]." },
                new Dictionary<string, object> { ["name"] = "Drag", ["description"] = "Pulls a target under.\n\n:::dm\nCANARY_ROW_SECRET\n:::" }
            }
        }
    };

    [Fact]
    public void ACreature_IsAStatBlock_BuiltFromTheSystemsOwnFields()
    {
        var block = BookStatBlocks.For(Drowned(), Catalog.GetDefinition("srd5e"), "en", "sKEY", Mention, dm: false)!;

        Assert.StartsWith("{{monster,frame,#sb-sKEY\n## Drowned Keeper\n", block);
        Assert.EndsWith("\n}}", block);
        // The type line is the type's summary fields, in their order; the scores are the system's attributes with its modifier rule.
        Assert.Contains("\n*2, undead, Medium*\n", block);
        Assert.Contains("|STR|DEX|CON|INT|WIS|CHA|\n|:---:|:---:|:---:|:---:|:---:|:---:|\n|16 (+3)|9 (−1)|17 (+3)|6 (−2)|10 (+0)|5 (−3)|", block);
        Assert.Contains("**Armor Class** 13", block);
        Assert.Contains("**Hit Points** 45", block);
        Assert.Contains("**Resistances** cold, necrotic", block);
        Assert.DoesNotContain("**Strength**", block);
        Assert.Contains("### Actions\n***Claw.*** *Melee Attack Roll:* +5, as Vera saw, and .", block);
        // A hidden actor is nothing, and a GM block in a row is the GM's.
        Assert.DoesNotContain("CANARY", block);
        Assert.Contains("CANARY_ROW_SECRET", BookStatBlocks.For(Drowned(), Catalog.GetDefinition("srd5e"), "en", "sKEY", Mention, dm: true)!);
    }

    [Fact]
    public void AnotherSystem_GetsItsOwnStatBlock_AndATypeItDoesNotDescribe_GetsNone()
    {
        var foe = new HomebrewEntryDocument
        {
            Id = "h-foe", Type = "foe", Name = "Bog Hag", Properties = { ["threat"] = 3, ["grit"] = 9, ["body"] = 4, ["mind"] = 5, ["luck"] = 1 }
        };
        var block = BookStatBlocks.For(foe, Catalog.GetDefinition("simple-d6"), "en", "sFOE", Mention, dm: true)!;
        Assert.Contains("\n*3*\n", block);
        Assert.Contains("**Grit** 9", block);
        // The system's attributes, with no modifier: it has no rule for one.
        Assert.Contains("|Body|Mind|Luck|\n|:---:|:---:|:---:|\n|4|5|1|", block);
        Assert.DoesNotContain("STR", block);

        Assert.Null(BookStatBlocks.For(foe, Catalog.GetDefinition("srd5e"), "en", "sFOE", Mention, dm: true));
        Assert.Null(BookStatBlocks.For(foe, null, "en", "sFOE", Mention, dm: true));
        // A type it describes, but with nothing to print.
        Assert.Null(BookStatBlocks.For(new HomebrewEntryDocument { Id = "h-k", Type = "knack", Name = "Lucky" }, Catalog.GetDefinition("simple-d6"), "en", "sK", Mention, dm: true));
    }

    [Fact]
    public void AnythingElse_IsATableOfItsFields_InTheBooksLanguage()
    {
        var cloak = new HomebrewEntryDocument
        {
            Id = "h-cloak", Type = "magic-item", Name = "Tidecloak", Properties = { ["category"] = "Wondrous item", ["rarity"] = "Rare", ["attunement"] = true, ["armorClassBonus"] = 1 }
        };
        var block = BookStatBlocks.For(cloak, Catalog.GetDefinition("srd5e"), "en", "sCLOAK", Mention, dm: false)!;
        Assert.StartsWith("{{properties,#pt-sCLOAK\n| Magic Item | |\n|:---|:---|\n", block);
        Assert.Contains("| Rarity | Rare |", block);
        Assert.Contains("| Requires Attunement | Yes |", block);
        Assert.Contains("| Armor Class Bonus | 1 |", block);

        var ru = BookStatBlocks.For(Drowned(), Catalog.GetDefinition("srd5e"), "ru", "sKEY", Mention, dm: false)!;
        Assert.Contains("**Класс Брони** 13", ru);
    }

    [Fact]
    public void AHostileValue_CannotCloseTheBlock_StartACell_OrOpenAnother()
    {
        var entry = Drowned();
        entry.Properties["speed"] = "30 | 40\n}}\n{{secret\nx\n}}\n:::dm\nCANARY_VALUE_SECRET\n:::";
        entry.Properties["senses"] = "<script>alert(1)</script> & {{note";
        // A row's name is a value too: a hidden actor's mention is nothing, its label never printed.
        ((List<object>)entry.Properties["actions"]).Add(new Dictionary<string, object> { ["name"] = "Gift of [[actor:a-hidden|CANARY_ROW_LABEL]]", ["description"] = "x" });
        var block = BookStatBlocks.For(entry, Catalog.GetDefinition("srd5e"), "en", "sKEY", Mention, dm: false)!;

        var lines = block.Split('\n');
        Assert.Equal(1, lines.Count(l => l.Trim() == "}}"));
        Assert.Equal("}}", lines[^1]);
        Assert.Single(lines, l => l.TrimStart().StartsWith("{{", StringComparison.Ordinal));
        Assert.DoesNotContain("CANARY", block);
        Assert.DoesNotContain("a-hidden", block);
        Assert.Contains(@"**Speed** 30 \| 40 \}\} \{\{secret x \}\}", block);
        Assert.Contains(@"\<script>alert(1)\</script> \& \{\{note", block);
    }

    [Fact]
    public void TheDraft_PrintsAnEntrysNumbersAfterItsText_AndAnAiEdit_KeepsThemBack()
    {
        var source = new BookSource(BookAudience.Dm, new BookCampaign("c1", "Saltmere", "srd5e", "", "", ["srd5e"], false), [], [], [], [], [], [], [Drowned()], []);
        var homebrew = BookDraftWriter.Generate(source, "en", null, Catalog.GetDefinition("srd5e")).Single(c => c.Section == "homebrew").Markdown;
        Assert.Contains("{{monster,frame,#sb-", homebrew);
        Assert.Contains("|16 (+3)|", homebrew);
        // Without the system, the entry is its text only.
        Assert.DoesNotContain("{{monster", BookDraftWriter.Generate(source, "en", null).Single(c => c.Section == "homebrew").Markdown);

        var passage = BookDraftEdits.Cut(homebrew, 0, homebrew.Length)!;
        Assert.DoesNotContain("16 (+3)", passage.Text);
        Assert.DoesNotContain("45", passage.Text);
        var piece = Assert.Single(passage.Pieces, p => p.Label == "a stat block");
        Assert.StartsWith("{{monster,frame,#sb-", piece.Text);
        Assert.Contains("|16 (+3)|", piece.Text);
    }
}

/// <summary>A spot (docs/BOOK_EXPORT.md 44) is a slot of its own: its picture is chosen beside the entry's own, not instead of it.</summary>
public class BookSpotSlotsTests
{
    [Fact]
    public void TheEditableBook_ChoosesASpotPicture_BesideTheEntrysOwn()
    {
        var actor = new ActorDocument { Id = "a-vera", CampaignId = "c", Name = "Vera", Visibility = ActorVisibility.Known };
        var source = new BookSource(BookAudience.Party, new BookCampaign("c", "C", "srd5e", "", "", []), [actor], [], [], [], [], [], [], []);
        var compiled = BookComposer.Compose(source, "en", out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        var entry = slots.Single(s => s.Kind == BookSlots.Entry);
        var spot = slots.Single(s => s.Kind == BookSlots.Spot);
        var stored = new BookArtDocument
        {
            Pictures =
            {
                new BookArtPicture { Slot = entry.Key, BuiltFrom = BookArtDocument.Party, Attachment = "book-party-" + entry.Key },
                new BookArtPicture { Slot = spot.Key, BuiltFrom = BookArtDocument.Party, Attachment = "book-party-" + spot.Key }
            }
        };

        Assert.Equal([entry.Key, spot.Key], BookSlots.Choose(slots, stored).Select(c => c.Slot.Key));
    }
}
