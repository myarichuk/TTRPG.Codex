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

    private static Dictionary<string, object> Row(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static HomebrewEntryDocument Needle() => new()
    {
        Id = "h-needle", Type = "hazard", Name = "Poisoned Needle",
        Properties =
        {
            ["kind"] = "trap", ["severity"] = "Deadly", ["level"] = "1-4", ["trigger"] = "Someone opens the lock, as [[actor:a-vera]] did.", ["complex"] = true,
            ["saveAttribute"] = "Constitution", ["saveDc"] = 11, ["onSave"] = "half", ["reset"] = "Resets after a minute.", ["initiative"] = 8, ["hitPoints"] = 20,
            ["detect"] = new List<object>
            {
                Row(("id", "lock"), ("name", "Inspect the lock"), ("action", "Search"), ("check", "Perception"), ("dc", 15)),
                Row(("name", "Read the glyph"), ("after", "lock"), ("check", "Arcana"), ("dc", 15), ("passive", false))
            },
            ["disarm"] = new List<object>
            {
                Row(("name", "Pick it"), ("check", "Sleight of Hand"), ("dc", 15), ("triggersOn", new List<object> { "failure" })),
                Row(("name", "Wedge it"), ("needs", "An Iron Spike"), ("successes", 2))
            },
            ["damage"] = new List<object> { Row(("dice", "1d10"), ("type", "poison")), Row(("dice", "1d6"), ("type", "piercing"), ("count", "1d3")) },
            ["conditions"] = new List<object> { Row(("name", "Poisoned"), ("lasts", "1 hour")) },
            ["routine"] = new List<object> { Row(("name", "Crush"), ("description", "The stone rolls."), ("strikes", true)), Row(("name", "Rumble"), ("description", "Dust falls.")) },
            ["higherLevels"] = new List<object> { Row(("levels", "5-10"), ("saveDc", 13), ("damage", new List<object> { Row(("dice", "2d10"), ("type", "poison")) })) }
        }
    };

    [Fact]
    public void AHazard_IsAStatBlockOfItsParts_ForTheGmsBookOnly()
    {
        var system = Catalog.GetDefinition("srd5e");
        Assert.Null(BookStatBlocks.For(Needle(), system, "en", "sHZ", Mention, dm: false));
        var block = BookStatBlocks.For(Needle(), system, "en", "sHZ", Mention, dm: true)!;

        Assert.StartsWith("{{monster,frame,#hz-sHZ\n## Poisoned Needle\n*Hazard, Levels 1-4, Deadly, Complex*\n", block);
        Assert.EndsWith("\n}}", block);
        Assert.Contains("**Trigger** Someone opens the lock, as Vera did.", block);
        Assert.Contains("**Save** Constitution, DC 11, half damage on a save", block);
        Assert.Contains("**Damage** 1d10 poison; 1d6 piercing, 1d3 times", block);
        Assert.Contains("**Conditions** Poisoned (1 hour)", block);
        Assert.Contains("### Detect\n***Inspect the lock.*** Search; Perception, DC 15\n\n***Read the glyph.*** Arcana, DC 15; after Inspect the lock; not found by passive scores\n\n", block);
        Assert.Contains("### Disarm\n***Pick it.*** Sleight of Hand, DC 15; a failure sets it off\n\n***Wedge it.*** needs An Iron Spike; 2 successes needed\n\n", block);
        Assert.Contains("***Crush.*** The stone rolls. (applies the effect)\n\n***Rumble.*** Dust falls.\n\n", block);
        Assert.Contains("***5-10.*** Save DC 13; Damage 2d10 poison\n}}", block);
        Assert.Contains("**Reset** Resets after a minute.", block);
        Assert.Contains("**Hit Points** 20", block);
    }

    [Fact]
    public void AHazard_ReadsInRussianAndHebrew_WithTheSystemsWordsAndTheBooksOwn()
    {
        var system = Catalog.GetDefinition("srd5e");
        var ru = BookStatBlocks.For(Needle(), system, "ru", "sHZ", Mention, dm: true)!;
        // The stored check (a label or a key) prints as the skill's name in the book's language, never the English label.
        Assert.Contains("Внимательность, Сл 15", ru);
        Assert.DoesNotContain("Perception", ru);
        Assert.Contains("требуется: An Iron Spike", ru);
        Assert.Contains("половина урона при успехе", ru);

        var he = BookStatBlocks.For(Needle(), system, "he", "sHZ", Mention, dm: true)!;
        Assert.Contains("נדרש: An Iron Spike", he);
        Assert.Contains("חצי נזק בהצלחה", he);
        Assert.Contains("כישלון מפעיל את המפגע", he);
        // Each is one block: opened once, closed once, nothing in it a Markdown reader takes for a block of its own.
        foreach (var block in new[] { ru, he })
        {
            var lines = block.Split('\n');
            Assert.Equal(1, lines.Count(l => l.Trim() == "}}"));
            Assert.Single(lines, l => l.TrimStart().StartsWith("{{", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AHostileHazardValue_CannotCloseTheBlock()
    {
        var entry = Needle();
        entry.Properties["trigger"] = "x\n}}\n{{secret\n:::dm\nCANARY_HZ_SECRET\n:::";
        ((List<object>)entry.Properties["detect"]).Add(Row(("name", "}} {{secret | a"), ("text", "<b>&")));
        var block = BookStatBlocks.For(entry, Catalog.GetDefinition("srd5e"), "en", "sHZ", Mention, dm: false);
        Assert.Null(block);

        var gm = BookStatBlocks.For(entry, Catalog.GetDefinition("srd5e"), "en", "sHZ", Mention, dm: true)!;
        var lines = gm.Split('\n');
        Assert.Equal(1, lines.Count(l => l.Trim() == "}}"));
        Assert.Single(lines, l => l.TrimStart().StartsWith("{{", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGmsDraft_PrintsAHazardAfterItsText_AndAnAiEditKeepsItsNumbersBack()
    {
        var source = new BookSource(BookAudience.Dm, new BookCampaign("c1", "Saltmere", "srd5e", "", "", ["srd5e"], false), [], [], [], [], [], [], [Needle()], []);
        var homebrew = BookDraftWriter.Generate(source, "en", null, Catalog.GetDefinition("srd5e")).Single(c => c.Section == "homebrew").Markdown;
        Assert.Contains("{{monster,frame,#hz-", homebrew);
        var passage = BookDraftEdits.Cut(homebrew, 0, homebrew.Length)!;
        Assert.DoesNotContain("Iron Spike", passage.Text);
        Assert.Contains(passage.Pieces, p => p.Label == "a stat block" && p.Text.Contains("Iron Spike", StringComparison.Ordinal));
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
