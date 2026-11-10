using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Core.Book;

namespace Codex.Persistence;

/// <summary>
/// The deterministic book: a <see cref="BookSource"/> laid out as chapters of Markdown, with no model involved. It reads nothing but
/// the source, so a party book can say nothing the party source does not hold, and it takes the DM's own words only from the DM
/// source (<see cref="BookSource.Audience"/> is checked where a DM-only field is used). Names and titles are written into headings
/// escaped, so a character called <c>**Bob** [[actor:x]]</c> is a name, not markup. Free text (a biography, a recap) is already
/// the campaign's Markdown and goes in as written.
/// </summary>
public static class BookCompiler
{
    public static BookDocument Compile(BookSource source, string language, BookLabels? labels = null) =>
        Compile(source, language, labels, out _);

    /// <summary>As <see cref="Compile(BookSource, string, BookLabels?)"/>, and says where each entry that can carry a picture has its heading:
    /// the line, counted in the chapter's own Markdown, written at the moment the heading is written. A picture is placed from this and
    /// not by looking for the heading's text again, because a body line that reads like the heading would be found first.</summary>
    public static BookDocument Compile(BookSource source, string language, BookLabels? labels, out IReadOnlyList<BookEntryPosition> entries)
    {
        labels ??= new BookLabels();
        var links = PageLinks(source);
        var chapters = new List<BookSection>();
        var positions = new List<BookEntryPosition>();
        var current = string.Empty;

        // The heading of an entry, and its position. The chapter is "# title", a blank line, then this builder's text: two lines in.
        void Head(StringBuilder sb, string prefix, string name, string? kind = null, string owner = "", string sub = "", bool hasArt = false, string placement = "", string subject = "")
        {
            var line = 2;
            foreach (var c in sb.ToString().AsSpan()) if (c == '\n') line++;
            var heading = prefix + Escape(name);
            sb.Append(heading).Append("\n\n");
            if (kind != null) positions.Add(new BookEntryPosition(current, heading, line, kind, owner, sub, name, placement, hasArt, subject, source.Audience));
        }

        bool Add(string id, Func<string> title, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            current = id;
            body(sb);
            if (sb.Length == 0) return false;
            chapters.Add(new BookSection(id, "# " + Escape(title()) + "\n\n" + sb));
            return true;
        }

        // A group of entries inside a chapter (a region, a kind of page, a type of homebrew): a heading with a marker and an anchor of its
        // own, so a merge or an AI edit takes it as a unit and the contents can point at it, but no picture's place (BookSlots.For).
        void Group(StringBuilder sb, string label, string kind, string owner, string subject) =>
            Head(sb, "## ", label, kind, owner, subject: subject);

        // The shape of a published adventure (docs/BOOK_EXPORT.md 51): what the book is, what has happened, the world, and the people and
        // the homebrew as appendices at the back.
        Add("overview", () => labels.Overview, sb =>
        {
            Paragraph(sb, links, source.Campaign.Tagline.Length > 0 ? "*" + Escape(source.Campaign.Tagline) + "*" : null);
            Paragraph(sb, links, source.Campaign.Description);
        });

        Add("chronicle", () => labels.Chronicle, sb =>
        {
            foreach (var session in source.Sessions)
            {
                // Each session is an entry, so it has a scene picture's place (28.3, step 7) and is a unit of its own for AI edits.
                Head(sb, "## ", session.Title, "scene", session.Id, placement: BookArtPlacement.Place, subject: BookSlots.SessionSubject(session.Id));
                Paragraph(sb, links, session.Recap);
                if (source.Audience == BookAudience.Dm && !string.IsNullOrWhiteSpace(session.DmSummary))
                {
                    sb.Append(":::dm\n").Append(session.DmSummary.Trim()).Append("\n:::\n\n");
                }
            }
        });

        Add("gazetteer", () => labels.Gazetteer, sb =>
        {
            foreach (var region in source.Regions.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Group(sb, region.Name, BookEntryPosition.Region, region.Id, BookSlots.RegionSubject(region.Id));
                foreach (var place in region.Locations)
                {
                    Head(sb, "### ", place.Name, "location", region.Id, place.Id, place.HasArt, BookArtPlacement.Place, BookSlots.LocationSubject(region.Id, place.Id));
                    Paragraph(sb, links, place.Description);
                }
            }
        });

        Add("lore", () => labels.Lore, sb =>
        {
            // Each fact is a paragraph and its details follow as they were written: a secret block inside them must keep its own
            // lines, or its marker stops being a marker and prints.
            foreach (var fact in source.Facts)
            {
                Paragraph(sb, links, "**" + Escape(fact.Summary) + "**");
                Paragraph(sb, links, fact.Details);
            }
        });

        Add("pages", () => labels.Pages, sb =>
        {
            // By the wiki's kinds, what the party is to do first: quests, who is behind them, who and where, and the rest.
            (WikiPageKind Kind, string Label)[] kinds =
            [
                (WikiPageKind.Quest, labels.Quests), (WikiPageKind.Faction, labels.Factions), (WikiPageKind.Person, labels.People),
                (WikiPageKind.Place, labels.Places), (WikiPageKind.General, labels.OtherPages)
            ];
            foreach (var (kind, label) in kinds)
            {
                var pages = source.Wiki.Where(p => p.Kind == kind).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (pages.Count == 0) continue;
                Group(sb, label, BookEntryPosition.Group, "pages/" + kind, BookSlots.GroupSubject("pages", kind.ToString()));
                foreach (var page in pages)
                {
                    Head(sb, "### ", page.Title, "wiki", page.Id, hasArt: page.HasArt, placement: BookArtPlacement.Place, subject: BookSlots.WikiSubject(page.Id));
                    Paragraph(sb, links, page.Body);
                }
            }
        });

        // The appendices are lettered in the order they are in the book: a campaign with no cast to print has its homebrew as the first.
        var appendix = 0;
        string Lettered(string title) =>
            appendix < labels.AppendixLetters.Length ? string.Format(CultureInfo.InvariantCulture, labels.Appendix, labels.AppendixLetters[appendix], title) : title;

        // The people the party meets, not the party: the book is the adventure as a source book, for a table that brings its own characters.
        // A player's own copy (BookSource.OwnedActorIds) still has that player's characters, which is where what only they were told is.
        if (Add("cast", () => Lettered(labels.Cast), sb =>
            {
                foreach (var actor in source.Actors.Where(a => a.Kind != ActorKind.PlayerCharacter || source.OwnedActorIds.Contains(a.Id)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Head(sb, "## ", actor.Name, "portrait", actor.Id, hasArt: actor.HasPortrait, placement: BookArtPlacement.Portrait, subject: BookSlots.ActorSubject(source.Audience, actor));
                    Paragraph(sb, links, actor.PublicDescription);
                    Paragraph(sb, links, actor.Biography);
                }
            }))
        {
            appendix++;
        }

        Add("homebrew", () => Lettered(labels.Homebrew), sb =>
        {
            // By the system's types (its creatures, its items), each under the name the system gives it.
            var types = source.Homebrew.GroupBy(e => e.Type.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Type: g.Key, Label: TypeLabel(labels, g.Key), Entries: g.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
                .OrderBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Type, StringComparer.Ordinal);
            foreach (var (type, label, list) in types)
            {
                Group(sb, label, BookEntryPosition.Group, "homebrew/" + type, BookSlots.GroupSubject("homebrew", type.ToLowerInvariant()));
                foreach (var entry in list)
                {
                    Head(sb, "### ", entry.Name, "homebrew", entry.Id, hasArt: entry.HasArt, placement: BookArtPlacement.Portrait, subject: BookSlots.HomebrewSubject(entry.Id));
                    Paragraph(sb, links, entry.Description);
                }
            }
        });

        entries = positions;
        return new BookDocument(source.Campaign.Name, source.Campaign.Tagline, language, chapters, labels);
    }

    /// <summary>
    /// The pictures this audience has, matched to the entries <see cref="Compile(BookSource, string, BookLabels?, out IReadOnlyList{BookEntryPosition})"/>
    /// wrote (so the entries are the audience's own, and the picture flag is the one its source holds: a stranger's mask has none).
    /// <paramref name="art"/> was read from the same source; an entry it has no picture for is simply not listed.
    /// </summary>
    public static IReadOnlyList<BookArtAnchor> ArtAnchors(IReadOnlyList<BookEntryPosition> entries, BookArt art)
    {
        var anchors = new List<BookArtAnchor>();
        foreach (var entry in entries)
        {
            // A picture the GM put in this entry's slot for the book comes first; it is looked up for the audience the entry was
            // compiled for, so a GM-edition picture cannot come back for a party entry even from the GM's map.
            var asset = art.Slot(entry.SlotKey, entry.Audience) ?? (!entry.HasArt ? null : entry.Kind switch
            {
                "portrait" => art.Portrait(entry.Owner),
                "location" => art.Location(entry.Owner, entry.Sub),
                "wiki" => art.Wiki(entry.Owner),
                "homebrew" => art.Homebrew(entry.Owner),
                _ => null
            });
            if (asset != null) anchors.Add(new BookArtAnchor(entry, asset));
        }

        return anchors;
    }

    /// <summary>
    /// The names a reader of this book may be given for a stored mention. They come from the source, which was read for the same
    /// audience, so a stranger's mention of a hidden character resolves to nothing and a masked one to its mask. Items resolve to nothing.
    /// </summary>
    public static Func<string, string, string?> Mentions(BookSource source)
    {
        var actors = source.Actors.ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        var places = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var region in source.Regions)
        {
            places[region.Id] = region.Name;
            foreach (var place in region.Locations)
            {
                places[place.Id] = place.Name;
            }
        }

        return (kind, id) => kind switch
        {
            "actor" => actors.GetValueOrDefault(id),
            "place" => places.GetValueOrDefault(id),
            _ => null
        };
    }

    /// <summary>
    /// The names this edition gives its people, places, pages and homebrew, for the facts check of an AI edit (docs/BOOK_EXPORT.md 42). They
    /// come from the source, read for the same audience, so the party edition's are what the party may read (a masked character's mask);
    /// a party edit is never checked against the GM's names.
    /// </summary>
    public static IReadOnlyList<string> Names(BookSource source) =>
        source.Actors.Select(a => a.Name)
            .Concat(source.Regions.SelectMany(r => r.Locations.Select(l => l.Name).Prepend(r.Name)))
            .Concat(source.Wiki.Select(w => w.Title))
            .Concat(source.Homebrew.Select(h => h.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string TypeLabel(BookLabels labels, string type) =>
        labels.HomebrewType?.Invoke(type) is { } label && !string.IsNullOrWhiteSpace(label) ? label.Trim() : type.Length > 0 ? type : labels.Homebrew;

    private static void Paragraph(StringBuilder sb, Func<string, string> links, string? markdown)
    {
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            sb.Append(links(markdown).Trim()).Append("\n\n");
        }
    }

    // Anything the renderer would read as [[inner]] (BookMarkdownRenderer.Bracket: up to the first "]]", no "[" and no line break inside),
    // and the longer ones it would not, which are rewritten too: this errs toward rewriting.
    private static readonly Regex PageLink = new(@"\[\[(?<inner>[^\[\n]*?)\]\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    // Anything either reader takes for a stored mention (BookWork.Prepare reads one in any case, with spaces before the colon) is left to them:
    // both fail closed, and a mention's label is never printed. Reading "[[Actor:x|Name]]" as a page link here would print the label.
    private static readonly Regex StoredMention = new(@"^\s*(actor|place|item)\s*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    /// <summary>
    /// What a <c>[[Page]]</c> or <c>[[Page|shown]]</c> link in free text may say in this edition. The GM edition keeps every link. The party
    /// edition keeps a link to a page its source holds (the party may open it); a link to any other page, one the party may not see or one that
    /// does not exist (the party source cannot tell them apart), prints its <c>|shown</c> words as plain text, the GM's own words for the
    /// reader, or nothing when it has none: the page's title is never printed, and so never reaches a model either, since the chapter a
    /// model is shown and the director's outline are both cut from this text. Stored mentions are left to the mention resolver.
    /// </summary>
    private static Func<string, string> PageLinks(BookSource source)
    {
        if (source.Audience == BookAudience.Dm) return text => text;
        var pages = source.Wiki;
        return text => !text.Contains("[[", StringComparison.Ordinal) ? text : PageLink.Replace(text, m =>
        {
            var inner = m.Groups["inner"].Value;
            var pipe = inner.IndexOf('|');
            var target = (pipe < 0 ? inner : inner[..pipe]).Trim();
            if (StoredMention.IsMatch(target)) return m.Value;
            if (target.Length > 0 && WikiLinks.TargetOfTitle(target, pages) != null) return m.Value;
            var shown = pipe < 0 ? string.Empty : inner[(pipe + 1)..].Trim();
            return shown.Length == 0 ? string.Empty : Escape(shown);
        });
    }

    /// <summary>A name or title as literal text: every character the dialect treats as markup is escaped, ":" too, so no title can
    /// open a secret block.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if (c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|' or '~' or ':' or '"' or '\'' or '&')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}

/// <summary>The block class a picture is laid out with (see the theme's <c>.art-portrait</c> and <c>.art-place</c>).</summary>
public static class BookArtPlacement
{
    /// <summary>Narrow, beside the entry's first lines.</summary>
    public const string Portrait = "art-portrait";

    /// <summary>Full width of the column, under the heading.</summary>
    public const string Place = "art-place";

    /// <summary>Across both columns, under the chapter title.</summary>
    public const string Header = "art-header";
}

/// <summary>An entry of the compiled book that has a heading of its own, and where that heading is: <paramref name="Line"/> counts lines of the
/// chapter's Markdown from zero. <paramref name="HasArt"/> is the picture flag the audience's source holds for it. <paramref name="Subject"/> is
/// what its picture slot is about as <paramref name="Audience"/> sees it (<see cref="BookSlots"/>).</summary>
public sealed record BookEntryPosition(string Section, string Heading, int Line, string Kind, string Owner, string Sub, string Alt, string Placement, bool HasArt, string Subject, BookAudience Audience)
{
    /// <summary>The kind of a region of the gazetteer: a group whose entries are its places.</summary>
    public const string Region = "region";

    /// <summary>The kind of any other group (a kind of page, a type of homebrew).</summary>
    public const string Group = "group";

    /// <summary>The entry's key: its picture slot's, and the anchor of its marker in the draft (<c>#e-KEY</c>).</summary>
    public string SlotKey => BookSlots.KeyFor(Subject);

    /// <summary>A heading that groups entries (docs/BOOK_EXPORT.md 51): it has a marker and an anchor, and no picture's place.</summary>
    public bool IsGroup => Kind is Region or Group;

    /// <summary>The level of its heading: 2 for <c>##</c>.</summary>
    public int Level => Heading.TakeWhile(c => c == '#').Count();

    /// <summary>Listed under its chapter in the contents: every entry and every region, but not a place (it is under its region) and not a
    /// group of pages or homebrew (its entries are listed).</summary>
    public bool InContents => Kind == Region || (!IsGroup && Kind != "location");
}

/// <summary>One existing picture and the entry whose heading it follows.</summary>
public sealed record BookArtAnchor(BookEntryPosition Entry, string Asset);
