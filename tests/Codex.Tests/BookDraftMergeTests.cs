using Codex.Core.Book;
using Codex.Persistence;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>
/// "Bring up to date" without a database (docs/BOOK_EXPORT.md 41.18): a chapter's units, the merge entry by entry, what a conflict sends a model
/// (never a secret of any of the three versions), and its pieces settled by the code.
/// </summary>
public class BookDraftMergeTests
{
    private const string Head = "{{chapter,#ch-cast\n# Cast\n}}\n\n{{chapter-art\n![Cast](art:s1)\n}}\n\n";
    private const string Tom = "{{entry,#e-tom}}\n## Tomas\n\nTom mends nets on the harbour wall.\n\n";
    private const string Vera = "{{entry,#e-vera}}\n## Vera\n\n{{portrait\n![Vera](art:s2)\n}}\n\nVera keeps the light.\n\n{{secret\nCANARY_VERA\n}}\n\n";
    private const string Ann = "{{entry,#e-ann}}\n## Ann\n\nAnn sells fish.\n";
    private const string Base = Head + Tom + Vera + Ann;

    private static BookMergePlan Plan(string mine, string theirs) => BookDraftMerge.Plan(Base, mine, theirs)!;

    private static BookMergeUnit Unit(BookMergePlan plan, string id) => plan.Units.Single(u => u.Id == id);

    [Fact]
    public void AChapter_SplitsIntoItsHeadAndEntries_AndTheUnitsPutTogetherAreTheChapterExactly()
    {
        var units = BookDraftMerge.Split(Base)!;
        Assert.Equal(["", "e-tom", "e-vera", "e-ann"], units.Select(u => u.Id));
        Assert.Equal(Base, string.Concat(units.Select(u => u.Text)));
        foreach (var text in new[] { "", "No entries at all.\n", Tom, "  {{entry,#e-x}}  \nText", Base + "\n\n\\page {fitted}\n", "{{entry,#e-a}}\n{{entry,#e-b}}\n" })
        {
            Assert.Equal(text, string.Concat(BookDraftMerge.Split(text)!.Select(u => u.Text)));
        }

        // A copied entry: the merge cannot tell which is which.
        Assert.Null(BookDraftMerge.Split(Base + Tom));
        Assert.Null(BookDraftMerge.Plan(Base, Base + Tom, Base));
        // Only a whole marker line is a marker.
        Assert.Null(BookDraftMerge.EntryId("Text {{entry,#e-x}}"));
        Assert.Null(BookDraftMerge.EntryId("{{entry,#e-x}} text"));
        Assert.Equal("e-x", BookDraftMerge.EntryId(" {{entry,#e-x}} "));
    }

    [Fact]
    public void EachUnit_IsTheGmsWhereOnlyTheGmChangedIt_TheCampaignsWhereOnlyTheCampaignDid_AndAConflictWhereBothDid()
    {
        var gmTom = Tom.Replace("mends nets", "GM_WORDS mends nets");
        var newVera = Vera.Replace("keeps the light", "NEW_FACT keeps the light");

        var plan = Plan(Head + gmTom + Vera + Ann, Head + Tom + newVera + Ann);
        Assert.Equal(BookMergeKind.Mine, Unit(plan, "e-tom").Kind);
        Assert.Equal(BookMergeKind.Theirs, Unit(plan, "e-vera").Kind);
        Assert.Equal(BookMergeKind.Same, Unit(plan, "e-ann").Kind);
        Assert.Equal(BookMergeKind.Same, Unit(plan, "").Kind);
        Assert.Equal(Head + gmTom + newVera + Ann, plan.Text());

        // Both made the same change (one with other blank lines): the GM's.
        var same = Plan(Head + Tom + newVera.Replace("\n\n", "\n\n\n") + Ann, Head + Tom + newVera + Ann);
        Assert.Equal(BookMergeKind.Same, Unit(same, "e-vera").Kind);

        // Both changed it differently: a conflict, shown as the campaign's text until a model merges it.
        var both = Plan(Head + Tom + Vera.Replace("keeps the light", "GM_WORDS keeps the light") + Ann, Head + Tom + newVera + Ann);
        var conflict = Unit(both, "e-vera");
        Assert.Equal(BookMergeKind.Conflict, conflict.Kind);
        Assert.Equal(newVera, conflict.Text);
        Assert.Equal(1, both.Count(BookMergeKind.Conflict));
        Assert.Contains("RESOLVED", both.Text(new Dictionary<string, string> { ["e-vera"] = "{{entry,#e-vera}}\nRESOLVED\n\n" }));

        // Nothing changed anywhere: the GM's chapter exactly.
        var mine = Head + gmTom + Vera + Ann;
        Assert.Equal(mine, Plan(mine, Base).Text());
    }

    [Fact]
    public void BlankLines_TrailingSpaces_AndPageBreaks_AreNotEdits_SoTheyMakeNoConflict_AndAKeptUnitKeepsItsBreaks()
    {
        // What Fit pages and a model's tidying do to a chapter, with no word changed.
        var fitted = Head + Tom.Replace("{{entry,#e-tom}}\n", "{{entry,#e-tom}}\n\n").Replace("wall.", "wall.   ") + "\\page {fitted}\n\n" + Vera + "\\column\n\n" + Ann;
        var newTom = Tom.Replace("mends nets", "NEW_FACT mends nets");
        var plan = Plan(fitted, Head + newTom + Vera + Ann);
        Assert.Equal(BookMergeKind.Theirs, Unit(plan, "e-tom").Kind);
        Assert.Equal(0, plan.Count(BookMergeKind.Conflict));
        // Vera's unit is the GM's text exactly, with the break after it.
        Assert.Equal(BookMergeKind.Same, Unit(plan, "e-vera").Kind);
        Assert.Contains(Vera + "\\column\n\n" + Ann, plan.Text());
    }

    [Fact]
    public void NewEntries_GoWhereTheCampaignPutsThem_GoneOnesAreOfferedForRemoval_AndTheGmsOrderAndDeletionsHold()
    {
        var bo = "{{entry,#e-bo}}\n## Bo\n\nBo rows.\n\n";
        // The GM moved Ann first and deleted Tom; the campaign added Bo after Vera.
        var mine = Head + Ann + "\n" + Vera;
        var plan = Plan(mine, Head + Tom + Vera + bo + Ann);
        Assert.Equal(["", "e-ann", "e-vera", "e-bo"], plan.Units.Select(u => u.Id));
        Assert.Equal(BookMergeKind.Added, Unit(plan, "e-bo").Kind);
        Assert.DoesNotContain(plan.Units, u => u.Id == "e-tom");

        // Tom deleted by the GM but changed by the campaign: offered back where the campaign has it. Ann gone from the campaign: offered for removal.
        var newTom = Tom.Replace("mends nets", "NEW_FACT mends nets");
        var back = Plan(Head + Vera + Ann, Head + newTom + Vera);
        Assert.Equal(["", "e-tom", "e-vera", "e-ann"], back.Units.Select(u => u.Id));
        Assert.Equal(BookMergeKind.Restored, Unit(back, "e-tom").Kind);
        Assert.Equal(BookMergeKind.Removed, Unit(back, "e-ann").Kind);
        Assert.DoesNotContain("Ann sells fish", back.Text());
        Assert.Contains("NEW_FACT", back.Text());
        // An entry first in the campaign goes after the head.
        var first = "{{entry,#e-first}}\n## First\n\nFirst.\n\n";
        Assert.Equal(["", "e-first", "e-tom", "e-vera", "e-ann"], Plan(Base, Head + first + Tom + Vera + Ann).Units.Select(u => u.Id));
    }

    // Every way the draft writes a secret (as BookDraftEditTests cuts them), in the base, the GM's text and the campaign's.
    public static TheoryData<string> Secrets => new()
    {
        "{{secret\nCANARY_S\n}}", "{{note,secret\nCANARY_S\n}}", "{{SECRET\nCANARY_S\n}}", "<div class=\"secret\">CANARY_S</div>", "A {{secret CANARY_S}} span.",
        "{{wide\n{{secret\nCANARY_S\n}}\n}}", "<span class='x secret'>CANARY_S</span>", "{{secret\nCANARY_S never closed"
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void AConflict_SendsNoSecretOfAnyVersion_AndTheReplyGetsThePiecesTheCodeSettled(string secret)
    {
        string Entry(string words, string hidden) => "{{entry,#e-vera}}\n## Vera\n\n" + words + "\n\n" + hidden + "\n\n";
        var b = Head + Entry("Vera keeps the light.", secret) + Ann;
        var m = Head + Entry("GM_WORDS Vera keeps the light.", secret) + Ann;
        var n = Head + Entry("Vera keeps the NEW_FACT light.", secret.Replace("CANARY_S", "CANARY_NEW")) + Ann;
        var unit = BookDraftMerge.Plan(b, m, n)!.Units.Single(u => u.Id == "e-vera");
        Assert.Equal(BookMergeKind.Conflict, unit.Kind);

        var conflict = BookDraftMerge.Conflict(unit)!;
        var request = new BookMergeRequest("Cast", "en", "Test game", conflict.Mine, conflict.Base, conflict.Theirs);
        var (system, user) = BookMergePrompts.Build(request);
        Assert.DoesNotContain("CANARY", system + user);
        Assert.Contains("NEW_FACT", user);
        Assert.Contains("GM_WORDS", user);

        // The GM left the secret alone and the campaign changed it: the campaign's goes back in; the model's words go around it.
        var reply = conflict.Mine.Text.Replace("GM_WORDS Vera keeps the light", "GM_WORDS Vera keeps the NEW_FACT light");
        Assert.Empty(BookDraftEdits.Check(conflict.Mine, reply));
        var merged = BookDraftEdits.Restore(conflict.Mine, reply);
        Assert.Contains("CANARY_NEW", merged);
        Assert.DoesNotContain("CANARY_S", merged);
        Assert.Contains("{{entry,#e-vera}}", merged);
    }

    [Fact]
    public void AFittedConflict_StillGoesToTheModel_AndItsBreaksComeBackWhereTheGmHadThem()
    {
        // Fit pages put a break inside the GM's Vera entry and one after it; the base and the campaign's version, generated, have none.
        var gmVera = Vera.Replace("Vera keeps the light.", "GM_WORDS Vera keeps the light.\n\n\\column").Replace("CANARY_VERA\n}}\n\n", "CANARY_VERA\n}}\n\n\\page {fitted}\n\n");
        var unit = Unit(Plan(Head + Tom + gmVera + Ann, Head + Tom + Vera.Replace("keeps the light", "keeps the NEW_FACT light") + Ann), "e-vera");
        Assert.Equal(BookMergeKind.Conflict, unit.Kind);
        var conflict = BookDraftMerge.Conflict(unit);
        Assert.NotNull(conflict);
        Assert.Equal(2, conflict.Mine.Pieces.Count(p => p.Label == BookDraftEdits.BreakLabel));
        var merged = BookDraftEdits.Restore(conflict.Mine, conflict.Mine.Text.Replace("keeps the light", "keeps the NEW_FACT light"));
        Assert.Contains("GM_WORDS Vera keeps the NEW_FACT light.\n\n\\column\n", merged);
        Assert.Contains("CANARY_VERA\n}}\n\n\\page {fitted}\n", merged);
    }

    [Fact]
    public void APieceBothChanged_StaysTheGms_AndPiecesThatDoNotLineUp_KeepTheConflictFromAnyModel()
    {
        string Entry(string words, string pieces) => "{{entry,#e-vera}}\n## Vera\n\n" + words + "\n\n" + pieces + "\n\n";
        var b = Entry("Old.", "{{secret\nCANARY_B\n}}");
        var m = Entry("GM.", "{{secret\nCANARY_M\n}}");
        var n = Entry("New.", "{{secret\nCANARY_N\n}}");
        var conflict = BookDraftMerge.Conflict(BookDraftMerge.Plan(b, m, n)!.Units.Single(u => u.Id == "e-vera"))!;
        Assert.Contains(conflict.Mine.Pieces, p => p.Text.Contains("CANARY_M"));
        Assert.DoesNotContain(conflict.Mine.Pieces, p => p.Text.Contains("CANARY_N"));

        // The campaign added a secret the GM's text has no place for: the code cannot say where it goes, so no model is asked.
        var more = Entry("New.", "{{secret\nCANARY_B\n}}\n\n{{secret\nCANARY_N\n}}");
        Assert.Null(BookDraftMerge.Conflict(BookDraftMerge.Plan(b, m, more)!.Units.Single(u => u.Id == "e-vera")));
        // A picture where the GM's text has a secret: not the same kind of piece.
        var other = Entry("New.", "![x](art:s9)");
        Assert.Null(BookDraftMerge.Conflict(BookDraftMerge.Plan(b, m, other)!.Units.Single(u => u.Id == "e-vera")));
    }

    [Fact]
    public async Task AConflictsReply_PassesTheEditsChecks_AndIsRetriedOnce_ThenLeftToTheCampaignsText()
    {
        var unit = BookDraftMerge.Plan(Base, Head + Tom + Vera.Replace("keeps", "GM_WORDS keeps") + Ann, Head + Tom + Vera.Replace("light", "NEW_FACT light") + Ann)!
            .Units.Single(u => u.Id == "e-vera");
        var conflict = BookDraftMerge.Conflict(unit)!;
        var request = new BookMergeRequest("Cast", "en", null, conflict.Mine, conflict.Base, conflict.Theirs);
        string Mine(IList<ChatMessage> messages)
        {
            var text = messages.Last().Text!;
            var from = text.IndexOf("\n<mine>\n", StringComparison.Ordinal) + 8;
            return text[from..text.IndexOf("\n</mine>", from, StringComparison.Ordinal)].Trim('\n');
        }

        // First a reply that drops a block, then a good one.
        var chat = new ScriptedChat((messages, call) => call == 1 ? "Vera, merged, no blocks." : Mine(messages).Replace("light", "NEW_FACT light"));
        var worker = new BookEditWorker(chat, new BookBudget(BookMergePrompts.CapFor(request)));
        var result = await BookMergePrompts.RunAsync(worker, request);
        Assert.True(result.Replacement != null, result.Reason + ": " + string.Join(" | ", result.Problems));
        Assert.Equal(2, result.Attempts);
        Assert.Contains("GM_WORDS keeps the NEW_FACT light", result.Replacement);
        Assert.Contains("{{secret\nCANARY_VERA\n}}", result.Replacement);
        Assert.Contains("was refused", chat.Requests[1]);

        var never = new ScriptedChat((_, _) => "No blocks at all.");
        var failed = await BookMergePrompts.RunAsync(new BookEditWorker(never, new BookBudget(BookMergePrompts.CapFor(request))), request);
        Assert.Null(failed.Replacement);
        Assert.Equal(BookEditWorker.MaxAttempts, never.Calls);
    }
}
