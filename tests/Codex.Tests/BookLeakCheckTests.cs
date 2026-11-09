using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// The party leak check (docs/BOOK_EXPORT.md 41.19): what only the GM edition has, looked for in the party draft as words, so the generator's
/// escapes, emphasis, wrapping and case do not hide it, while what the generated party draft says itself is never a hit.
/// </summary>
public class BookLeakCheckTests
{
    private static readonly BookLeakNeedle Secret = new(BookLeakKind.Secret, "The duke poisoned the harbour well himself, at night.", "Vera", BookLeakCheck.SecretMinTokens);
    private static readonly BookLeakNeedle Hidden = new(BookLeakKind.HiddenName, "Ash Morrow", null);

    private static BookLeakReport Find(string chapter, string allowed = "", params BookLeakNeedle[] needles) =>
        BookLeakCheck.Find(needles.Length == 0 ? [Secret, Hidden] : needles, [allowed], [("cast", chapter)]);

    [Theory]
    [InlineData("The duke poisoned the harbour well himself, at night.")]
    [InlineData(@"The duke poisoned the harbour well himself\, at night\.")]
    [InlineData("THE DUKE poisoned the\nharbour well\n*himself*, at **night**.")]
    // Half of it is enough: any six words in a row.
    [InlineData("Rumour has it the duke poisoned the harbour well. Nobody believes it.")]
    public void APastedSecret_IsFound_EscapedReCasedReWrappedOrHalfOfIt(string chapter)
    {
        var report = Find(chapter);

        var hit = Assert.Single(report.Hits);
        Assert.Equal(BookLeakKind.Secret, hit.Kind);
        Assert.Equal("cast", hit.Section);
        Assert.Equal("Vera", hit.From);
    }

    [Fact]
    public void AHiddenName_IsFoundAsWholeWords_NeverInsideALongerWord()
    {
        Assert.Single(Find("We met Ash   Morrow at the gate.").Hits);
        Assert.Single(Find(@"\#\# Ash\_Morrow").Hits);
        Assert.Empty(Find("Ashes of Morrowind.").Hits);
        Assert.Empty(Find("Ash came, and Morrow left.").Hits);
    }

    [Fact]
    public void WhatTheGeneratedPartyDraftSays_IsNeverAHit()
    {
        // A secret that repeats a public phrase, and a hidden character whose name the party's own text uses.
        const string allowed = "Everyone knows the duke poisoned the harbour well himself, at night. Ash Morrow is a street.";

        Assert.Equal(0, Find("The duke poisoned the harbour well himself, at night. Ash Morrow.", allowed).Total);
    }

    [Fact]
    public void OnlyTheWindowsThePartyDraftLacks_AreLookedFor()
    {
        // The secret's first six words are public; its last six are not.
        const string allowed = "The duke poisoned the harbour well";

        Assert.Equal(0, Find("The duke poisoned the harbour well.", allowed).Total);
        Assert.Equal(1, Find("the harbour well himself, at night", allowed).Total);
    }

    [Fact]
    public void ASecretTooShort_OrANameTooShort_IsNotLookedFor()
    {
        var shortSecret = new BookLeakNeedle(BookLeakKind.Secret, "Poisoned.", null, BookLeakCheck.SecretMinTokens);
        var shortName = new BookLeakNeedle(BookLeakKind.HiddenName, "Al", null);

        Assert.Equal(0, Find("Poisoned. Al was here.", "", shortSecret, shortName).Total);
    }

    [Fact]
    public void HebrewAndRussianText_MatchesWithoutPointsOrCase()
    {
        var hebrew = new BookLeakNeedle(BookLeakKind.Secret, "הדוכס הרעיל את הבאר בנמל", null, BookLeakCheck.SecretMinTokens);
        var russian = new BookLeakNeedle(BookLeakKind.HiddenName, "Вера Холлоуэй", null);

        Assert.Equal(1, Find("נאמר כי הַדּוּכָּס הרעיל את הבאר בנמל.", "", hebrew).Total);
        Assert.Equal(1, Find("ВЕРА ХОЛЛОУЭЙ пришла.", "", russian).Total);
    }

    [Fact]
    public void ThePartyStylesheet_IsLookedInToo_UnderItsOwnSection()
    {
        var report = BookLeakCheck.Find([Hidden], [""], [("cast", "Nothing here."), (BookLeakCheck.Css, ".page::after { content: \"Ash Morrow\"; }")]);

        Assert.Equal(BookLeakCheck.Css, Assert.Single(report.Hits).Section);
    }

    [Fact]
    public void OneHitPerNeedleAndChapter_AndTheListIsCapped_ButCounted()
    {
        var needles = Enumerable.Range(0, 30).Select(i => new BookLeakNeedle(BookLeakKind.HiddenName, "Name" + i + "x", null)).ToArray();
        var chapter = string.Join(" ", needles.Select(n => n.Text + " " + n.Text));

        var report = BookLeakCheck.Find(needles, [""], [("a", chapter), ("b", "Name0x")]);

        Assert.Equal(31, report.Total);
        Assert.Equal(BookLeakCheck.MaxHits, report.Hits.Count);
    }

    [Fact]
    public void ADraftsSecretBlocks_AreReadWithTheEntryTheyAreIn()
    {
        const string draft = "{{entry,#e-abc}}\n### Vera \\*Holloway\\*\n\nPublic words.\n\n{{secret\nShe is the \\*duke's\\* daughter.\n{{note\nInner.\n}}\nStill secret.\n}}\n\n{{secret,wide}}\n";

        var secret = Assert.Single(BookLeakCheck.Secrets(draft));

        Assert.Equal("Vera *Holloway*", secret.From);
        Assert.Equal("She is the *duke's* daughter.\n{{note\nInner.\n}}\nStill secret.", secret.Text);
    }

    [Fact]
    public void TheNeedles_AreWhatOnlyTheGmSourceHas_AStrangerByTheirRealName_ButNotTheirAlias()
    {
        var campaign = new BookCampaign("c", "C", "srd5e", "", "", []);
        var known = new ActorDocument { Id = "known", Name = "Vera Holloway", Biography = "Public life.\n\n:::dm\nShe is the duke's daughter.\n:::" };
        var stranger = new ActorDocument { Id = "stranger", Name = "Ash Morrow", Biography = "Ash was born in the drowned quarter." };
        var hidden = new ActorDocument { Id = "hidden", Name = "Silent Brother", Biography = "He keeps the [[actor:known|ignored]] ledger." };
        var gmPage = new WikiPageDocument { Id = "w1", Title = "The Duke's Plan", Body = "Flood the lower city at midsummer." };
        var gm = new BookSource(BookAudience.Dm, campaign, [known, stranger, hidden], [], [], [], [gmPage], [], [], []);
        var party = new BookSource(BookAudience.Party, campaign,
            [new ActorDocument { Id = "known", Name = "Vera Holloway", Biography = "Public life." }, new ActorDocument { Id = "stranger", Name = "the hooded stranger" }],
            [], [], [], [], [], [], []);

        var needles = BookLeakCheck.Needles(gm, party, []);

        Assert.Contains(needles, n => n is { Kind: BookLeakKind.RealName, Text: "Ash Morrow", From: "the hooded stranger" });
        Assert.Contains(needles, n => n is { Kind: BookLeakKind.HiddenName, Text: "Silent Brother" });
        Assert.Contains(needles, n => n is { Kind: BookLeakKind.GmOnly, Text: "The Duke's Plan" });
        Assert.Contains(needles, n => n.Kind == BookLeakKind.GmOnly && n.Text == "Flood the lower city at midsummer.");
        // A hidden character's history, its mentions as names; a stranger's too.
        Assert.Contains(needles, n => n.Kind == BookLeakKind.GmOnly && n.Text == "He keeps the Vera Holloway ledger.");
        Assert.Contains(needles, n => n.Kind == BookLeakKind.GmOnly && n.Text == "Ash was born in the drowned quarter.");
        // Nothing the party has: not a known character's name or history, not the alias.
        Assert.DoesNotContain(needles, n => n.Text.Contains("Public life") || n.Text == "Vera Holloway" || n.Text.Contains("hooded"));
    }

    [Fact]
    public void AGmSessionSummary_IsANeedle_EvenOfASessionThePartyHas_AndAPlannedSessionsTitleToo()
    {
        var campaign = new BookCampaign("c", "C", "srd5e", "", "", []);
        var played = new SessionDocument { Id = "s1", Title = "The Drowned Bell", Recap = "We rang it.", DmSummary = "They never found the second bell." };
        var planned = new SessionDocument { Id = "s2", Title = "The Duke Unmasked", Recap = "Prep notes for the unmasking." };
        var gm = new BookSource(BookAudience.Dm, campaign, [], [played, planned], [], [], [], [], [], []);
        var party = new BookSource(BookAudience.Party, campaign, [], [new SessionDocument { Id = "s1", Title = "The Drowned Bell", Recap = "We rang it." }], [], [], [], [], [], []);

        var needles = BookLeakCheck.Needles(gm, party, []);

        Assert.Equal(new[] { "Prep notes for the unmasking.", "The Duke Unmasked", "They never found the second bell." },
            needles.Select(n => n.Text).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASecretInAGmDraft_IsANeedle_AndTheSameSecretTwiceIsOne()
    {
        var campaign = new BookCampaign("c", "C", "srd5e", "", "", []);
        var source = new BookSource(BookAudience.Dm, campaign, [], [], [], [], [], [], [], []);
        var drafts = new[] { "{{secret\nThe vault key is under the altar.\n}}", "## Room\n\n{{secret\nThe vault key is *under* the altar.\n}}" };

        var needles = BookLeakCheck.Needles(source, source, drafts);

        Assert.Equal(BookLeakKind.Secret, Assert.Single(needles).Kind);
    }
}
