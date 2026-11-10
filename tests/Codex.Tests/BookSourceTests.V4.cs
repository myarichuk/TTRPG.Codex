using System.Text.RegularExpressions;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// The generator's fourth version (docs/BOOK_EXPORT.md 51): the book in the shape of a published adventure, the campaign's own headings written
/// under their entry, contents of two levels, and names that point at their entries. Each is tried with hostile input, in both editions.
/// </summary>
public partial class BookSourceTests
{
    private static string Key(string subject) => BookSlots.KeyFor(subject);

    private static string All(IEnumerable<BookDraftGenerated> draft) => string.Join("\n", draft.Select(c => c.Markdown));

    private static string ChapterOf(IEnumerable<BookDraftGenerated> draft, string section) => draft.Single(c => c.Section == section).Markdown;

    private static HashSet<string> Markers(IEnumerable<BookDraftGenerated> draft) =>
        draft.SelectMany(c => Regex.Matches(c.Markdown, @"^\{\{entry,#e-([a-z0-9]+)\}\}$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)).ToHashSet();

    [Fact]
    public async Task TheBook_HasAnAdventuresShape_TheCastWithoutThePlayersCharacters_AndGroupsWithMarkersOfTheirOwn()
    {
        var (dm, _) = await SeedAsync();
        var party = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Party))!, "en", null);
        var gm = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Dm))!, "en", null);

        string[] order = ["front", "overview", "chronicle", "gazetteer", "lore", "pages", "cast", "homebrew"];
        Assert.Equal(order, party.Select(c => c.Section));
        Assert.Equal(order, gm.Select(c => c.Section));
        Assert.Equal(["Contents", "Introduction", "The story so far", "Gazetteer", "Lore", "Pages", "Appendix A: Cast", "Appendix B: Homebrew"], party.Select(c => c.Title));
        Assert.StartsWith("{{chapter,#ch-cast\n# Appendix A\\: Cast\n}}", ChapterOf(party, "cast"));

        // The people the party meets, not the party's own characters, in either edition; their names stay where the text names them.
        foreach (var draft in new[] { party, gm })
        {
            Assert.DoesNotContain("PUBLIC_PC_BIO", All(draft));
            Assert.DoesNotContain(Key("actor:a-pc"), All(draft));
            Assert.Contains("## PUBLIC\\_Vera\n", ChapterOf(draft, "cast"));
        }

        // A region, a kind of page and a type of homebrew are headings with a marker of their own, their entries a level under them.
        Assert.Contains("{{entry,#e-" + Key(BookSlots.RegionSubject("r-coast")) + "}}\n## PUBLIC\\_Grey Coast\n", ChapterOf(party, "gazetteer"));
        Assert.Contains("{{entry,#e-" + Key(BookSlots.LocationSubject("r-coast", "l-known")) + "}}\n### PUBLIC\\_Saltmere\n", ChapterOf(party, "gazetteer"));
        Assert.Contains("{{entry,#e-" + Key(BookSlots.GroupSubject("pages", "General")) + "}}\n## Other pages\n", ChapterOf(party, "pages"));
        Assert.Contains("{{entry,#e-" + Key(BookSlots.WikiSubject("w-party")) + "}}\n### PUBLIC\\_Docks\n", ChapterOf(party, "pages"));
        Assert.Contains("{{entry,#e-" + Key(BookSlots.GroupSubject("homebrew", "magic-item")) + "}}\n## magic\\-item\n", ChapterOf(party, "homebrew"));
        Assert.Contains("\n### PUBLIC\\_Homebrew Beast\n", ChapterOf(party, "homebrew"));
        // The GM's creatures are a group the party's book does not have, not even as a heading.
        Assert.Contains("\n## monster\n", ChapterOf(gm, "homebrew"));
        Assert.DoesNotContain("monster", ChapterOf(party, "homebrew"));

        // A group has no picture's place: no slot is made for it.
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var compiled = BookComposer.Compose(source, "en", out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        Assert.Contains(entries, e => e.IsGroup);
        Assert.DoesNotContain(slots, s => s.Entry is { IsGroup: true });
        Assert.DoesNotContain(slots, s => s.Key == Key(BookSlots.RegionSubject("r-coast")));
    }

    [Fact]
    public async Task WithNoCastToPrint_TheHomebrewIsTheFirstAppendix()
    {
        var id = "book-app-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Lone", SystemId = "srd5e", OwnerId = DmUser, Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM } }, ActivePacks = { "srd5e" }
        });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);
        await _actors.SaveAsync(new ActorDocument { Id = "a-only-pc-" + id, CampaignId = id, Name = "PUBLIC_OnlyPc", Kind = ActorKind.PlayerCharacter, OwnerUserId = PlayerUser });
        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-" + id, CampaignId = id, EntryId = "ring", Type = "magic-item", Name = "PUBLIC_Ring", IsPublished = true, AuthorUserId = DmUser });

        var draft = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Dm))!, "en", null);

        Assert.DoesNotContain(draft, c => c.Section == "cast");
        Assert.Equal("Appendix A: Homebrew", draft.Single(c => c.Section == "homebrew").Title);
    }

    [Fact]
    public async Task TheContents_ListEachChapterThenItsEntries_NeverAPlaceUnderARegion_AndInThePartyBookNothingThePartyLacks()
    {
        var (dm, _) = await SeedAsync();
        var partySource = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var own = new[]
        {
            new BookDraftOwn("own-" + new string('a', 32), "GM_OWN [first]", ""), new BookDraftOwn("own-" + new string('b', 32), "GM_OWN_AFTER_LORE", "lore"),
            new BookDraftOwn("own-" + new string('c', 32), "GM_OWN_LOST", "no-such-chapter")
        };
        var party = BookDraftWriter.Generate(partySource, "en", null, own: own);
        var gm = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Dm))!, "en", null);
        var front = ChapterOf(party, "front");
        var contents = front[front.IndexOf("{{toc,wide", StringComparison.Ordinal)..];

        // The first level: the chapters in book order, the GM's own where the draft has them, each title as literal text.
        var chapters = Regex.Matches(contents, @"^- \[(?<t>.*)\]\(#ch-(?<id>[a-z0-9-]+)\)$", RegexOptions.Multiline).Select(m => m.Groups["id"].Value).ToList();
        Assert.Equal(["own-" + new string('a', 32), "overview", "chronicle", "gazetteer", "lore", "own-" + new string('b', 32), "pages", "cast", "homebrew", "own-" + new string('c', 32)], chapters);
        Assert.Contains("- [GM\\_OWN \\[first\\]](#ch-own-" + new string('a', 32) + ")\n", contents);

        // The second level: every entry of the chapter, by its own anchor, which the draft has.
        var listed = Regex.Matches(contents, @"^  - \[.*\]\(#e-(?<k>[a-z0-9]+)\)$", RegexOptions.Multiline).Select(m => m.Groups["k"].Value).ToList();
        Assert.NotEmpty(listed);
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Subset(Markers(party), listed.ToHashSet());
        Assert.Equal(Regex.Matches(contents, @"\(#e-").Count, listed.Count);
        Assert.Contains("- [Gazetteer](#ch-gazetteer)\n  - [PUBLIC\\_Grey Coast](#e-" + Key(BookSlots.RegionSubject("r-coast")) + ")\n- [Lore]", contents);
        Assert.Contains("  - [PUBLIC\\_Docks](#e-" + Key(BookSlots.WikiSubject("w-party")) + ")\n", contents);
        Assert.Contains("  - [PUBLIC\\_hooded stranger](#e-" + Key("mask:a-unknown") + ")\n", contents);
        // A place is under its region, and a group's entries are listed, not the group.
        Assert.DoesNotContain(Key(BookSlots.LocationSubject("r-coast", "l-known")), contents);
        Assert.DoesNotContain(Key(BookSlots.GroupSubject("pages", "General")), contents);

        // Nothing the party source lacks: no name, and no key that only the GM edition has.
        Assert.DoesNotContain("CANARY", front);
        var gmOnly = Markers(gm).Except(Markers(party)).ToList();
        Assert.Contains(Key(BookSlots.WikiSubject("w-dm")), gmOnly);
        Assert.Contains(Key("actor:a-hidden"), gmOnly);
        Assert.Contains(Key("actor:a-unknown"), gmOnly);
        foreach (var key in gmOnly) Assert.DoesNotContain(key, All(party));
        Assert.Contains("  - [CANARY\\_WIKI\\_DM\\_TITLE](#e-" + Key(BookSlots.WikiSubject("w-dm")) + ")\n", ChapterOf(gm, "front"));
    }

    [Fact]
    public async Task AHeadingInTheCampaignsText_IsWrittenUnderItsEntry_NotInsideAFence_AndInASecretOnlyForTheGm_OnTheLineItWasOn()
    {
        var (dm, id) = await SeedAsync();
        const string hostile = "PUBLIC_BIO_VERA\n\n# PUBLIC_H1\n\n```\n# PUBLIC_FENCED\n```\n\n~~~md\n## PUBLIC_TILDE\n~~~\n\n    # PUBLIC_INDENTED\n\n#PUBLIC_NOSPACE\n\n"
                               + "##### PUBLIC_H5\n\n   ## PUBLIC_THREE_SPACES ##\n\n:::dm\n## CANARY_SECRET_HEADING\n\n```\n# CANARY_SECRET_FENCED\n```\n:::\n\nPUBLIC_AFTER\n===\n\n#";
        await _actors.UpdateAsync("a-known", a => a.Biography = hostile);
        var page = (await _wiki.GetVisibleBySlugAsync("docks", dm))!;
        page.Body = "PUBLIC_DOCKS_BODY\n\n# PUBLIC_DOCK_H1\n\n###### PUBLIC_DOCK_H6";
        await _wiki.SaveAsync(page);
        var campaign = (await _campaigns.GetAsync(id))!;
        campaign.Description = "PUBLIC_DESCRIPTION\n\n# PUBLIC_DESC_H1\n\n:::dm\n# CANARY_DESC_SECRET_H\n:::";
        await _campaigns.SaveAsync(campaign);

        var partySource = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var party = BookDraftWriter.Generate(partySource, "en", null);
        var gm = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Dm))!, "en", null);

        // Under a "##" entry: one becomes three, five becomes six (the deepest there is); what Markdown does not read as a heading stays.
        var cast = ChapterOf(party, "cast");
        Assert.Contains("PUBLIC_BIO_VERA\n\n### PUBLIC_H1\n\n```\n# PUBLIC_FENCED\n```\n\n~~~md\n## PUBLIC_TILDE\n~~~\n\n    # PUBLIC_INDENTED\n\n#PUBLIC_NOSPACE\n\n"
                        + "###### PUBLIC_H5\n\n   #### PUBLIC_THREE_SPACES ##\n\n", cast);
        Assert.Contains("PUBLIC_AFTER\n===\n\n###", cast);
        Assert.DoesNotContain("CANARY", All(party));
        // The GM's secret is boxed, its heading under the entry too, its fence left alone.
        Assert.Contains("{{secret\n#### CANARY_SECRET_HEADING\n\n```\n# CANARY_SECRET_FENCED\n```\n}}", ChapterOf(gm, "cast"));
        // Under a "###" entry, and under the chapter's own title before any entry.
        Assert.Contains("PUBLIC_DOCKS_BODY\n\n#### PUBLIC_DOCK_H1\n\n###### PUBLIC_DOCK_H6", ChapterOf(party, "pages"));
        Assert.Contains("PUBLIC_DESCRIPTION\n\n## PUBLIC_DESC_H1", ChapterOf(party, "overview"));
        Assert.Contains("{{secret\n## CANARY_DESC_SECRET_H\n}}", ChapterOf(gm, "overview"));

        // No chapter has a first-level heading but its title, and every entry the compiler recorded still has its marker over its own heading:
        // no line moved.
        var compiled = BookComposer.Compose(partySource, "en", out var entries);
        foreach (var chapter in party.Where(c => c.Section != "front"))
        {
            var lines = chapter.Markdown.Split('\n');
            var fenced = false;
            var top = 0;
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal)) fenced = !fenced;
                else if (!fenced && Regex.IsMatch(line, @"^ {0,3}# ")) top++;
            }

            Assert.Equal(1, top);
            foreach (var entry in entries.Where(e => e.Section == chapter.Section))
            {
                Assert.Contains("{{entry,#e-" + entry.SlotKey + "}}\n" + entry.Heading + "\n", chapter.Markdown);
            }
        }

        Assert.Equal(compiled.Sections.Count + 1, party.Count);
    }

    [Fact]
    public void TheTextPath_KeepsItsLines_AndLeavesHeadingsAlone_WhenNoLevelIsGiven()
    {
        const string text = "# One\ntext\n## Two\n```\n# code\n```\n###### Six\n";
        string? None(string kind, string id) => null;

        Assert.Equal(text, BookDraftWriter.Text(text, None, dm: true));
        var under = BookDraftWriter.Text(text, None, dm: true, level: 3);
        Assert.Equal("#### One\ntext\n##### Two\n```\n# code\n```\n###### Six\n", under);
        Assert.Equal(text.Split('\n').Length, under.Split('\n').Length);
        // A fence that runs across a secret block is still a fence after it.
        Assert.Equal("```\n# a\n\n{{secret\n# s\n}}\n\n# b\n```\n### c\n", BookDraftWriter.Text("```\n# a\n:::dm\n# s\n:::\n# b\n```\n# c\n", None, dm: true, level: 2));
        // A fence left open swallows what follows it, as Markdown would read it.
        Assert.Equal("```\n# a\n\n# b\n", BookDraftWriter.Text("```\n# a\n\n# b\n", None, dm: true, level: 2));
    }

    [Fact]
    public async Task AName_PointsAtItsEntry_OnlyWhereThisEditionsBookHasOne_AndNeverAtTheEntryItIsIn()
    {
        var (dm, _) = await SeedAsync();
        await _actors.UpdateAsync("a-known", a => a.Biography =
            "Stranger [[actor:a-unknown]]; hidden [[actor:a-hidden|CANARY_LABEL]]; hero [[actor:a-pc]]; town [[place:l-known]]; coast [[place:r-coast]]; "
            + "lair [[place:l-hidden]]; page [[PUBLIC_Docks|the docks]]; secret page [[CANARY_WIKI_DM_TITLE|PUBLIC_SHOWN]] [[CANARY_WIKI_DM_TITLE]]; self [[actor:a-known]]; "
            + "unwritten [[Not Written Yet]]; item [[item:x]].");

        var party = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Party))!, "en", null);
        var gm = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Dm))!, "en", null);

        Assert.Contains(
            "Stranger [PUBLIC\\_hooded stranger](#e-" + Key("mask:a-unknown") + "); hidden ; hero PUBLIC\\_PC\\_Mira; "
            + "town [PUBLIC\\_Saltmere](#e-" + Key(BookSlots.LocationSubject("r-coast", "l-known")) + "); "
            + "coast [PUBLIC\\_Grey Coast](#e-" + Key(BookSlots.RegionSubject("r-coast")) + "); lair ; "
            + "page [the docks](#e-" + Key(BookSlots.WikiSubject("w-party")) + "); secret page PUBLIC\\_SHOWN ; self PUBLIC\\_Vera; unwritten ; item .",
            ChapterOf(party, "cast"));
        // The GM edition has the entries the party's lacks, under its own keys: the character behind the mask, the hidden one, the GM's page.
        Assert.Contains(
            "Stranger [CANARY\\_UNKNOWN\\_REALNAME](#e-" + Key("actor:a-unknown") + "); hidden [CANARY\\_HIDDEN\\_NAME](#e-" + Key("actor:a-hidden") + "); hero PUBLIC\\_PC\\_Mira; ",
            ChapterOf(gm, "cast"));
        Assert.Contains("secret page [PUBLIC\\_SHOWN](#e-" + Key(BookSlots.WikiSubject("w-dm")) + ") [CANARY\\_WIKI\\_DM\\_TITLE](#e-" + Key(BookSlots.WikiSubject("w-dm")) + "); self PUBLIC\\_Vera; unwritten Not Written Yet; item .",
            ChapterOf(gm, "cast"));

        // In each edition every link inside the book lands on something that edition's draft has; the party's on nothing only the GM's has.
        foreach (var draft in new[] { party, gm })
        {
            var markers = Markers(draft);
            var chapters = draft.Select(c => c.Section).ToHashSet();
            var links = Regex.Matches(All(draft), @"\]\(#(?<kind>e|ch)-(?<id>[a-z0-9-]+)\)").ToList();
            Assert.True(links.Count > 10);
            foreach (Match link in links)
            {
                Assert.True(link.Groups["kind"].Value == "e" ? markers.Contains(link.Groups["id"].Value) : chapters.Contains(link.Groups["id"].Value), link.Value);
            }
        }

        foreach (var key in Markers(gm).Except(Markers(party))) Assert.DoesNotContain(key, All(party));
        Assert.DoesNotContain("CANARY", All(party));
    }

    [Fact]
    public void ALink_IsNeverWrittenForANameTheEditionDoesNotGive_AndItsWordsAreLiteralText()
    {
        var links = new BookDraftLinks((_, _) => "skey", _ => "skey");

        // The resolver gives no name (a hidden character): nothing is printed, whatever the links would say and whatever the mention's label is.
        var hidden = BookDraftWriter.Text("Met [[actor:x|CANARY_LABEL]] and [[ Actor : y ]].\n", (_, _) => null, dm: false, level: 2, links);
        Assert.Equal("Met  and .\n", hidden);
        // A name that reads like markup is the link's words and nothing more; it cannot close the link or open another.
        var named = BookDraftWriter.Text("[[actor:x]] [[Page|see*(here)]]\n", (_, _) => "A](http://evil.example) [b", dm: false, level: 2, links);
        Assert.Equal("[A\\]\\(http\\://evil\\.example\\) \\[b](#e-skey) [see\\*\\(here\\)](#e-skey)\n", named);
        // The entry the text is in is never linked from its own text.
        Assert.Equal("Vera\n", BookDraftWriter.Text("[[actor:x]]\n", (_, _) => "Vera", dm: false, level: 2, links with { Self = "skey" }));
    }

    [Fact]
    public void AnAiEdit_MayKeepOrDropALinkThePassageHad_ButNotWriteAnother()
    {
        const string chapter = "{{chapter,#ch-cast\n# Cast\n}}\n\n{{entry,#e-s1}}\n## Vera\n\nVera met [the stranger](#e-s2) at the docks.\n";
        var passage = BookDraftEdits.Cut(chapter, 0, chapter.Length)!;
        Assert.Contains("[the stranger](#e-s2)", passage.Text);

        Assert.Empty(BookDraftEdits.Check(passage, passage.Text));
        Assert.Empty(BookDraftEdits.Check(passage, passage.Text.Replace("[the stranger](#e-s2)", "the stranger")));
        Assert.NotEmpty(BookDraftEdits.Check(passage, passage.Text.Replace("the docks", "[the docks](#e-s3)")));
        Assert.NotEmpty(BookDraftEdits.Check(passage, passage.Text.Replace("(#e-s2)", "(#ch-front)")));
    }
}
