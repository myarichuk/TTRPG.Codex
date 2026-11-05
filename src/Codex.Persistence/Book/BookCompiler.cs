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

        void Add(string id, string title, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            current = id;
            body(sb);
            if (sb.Length > 0)
            {
                chapters.Add(new BookSection(id, "# " + Escape(title) + "\n\n" + sb));
            }
        }

        Add("overview", labels.Overview, sb =>
        {
            Paragraph(sb, links, source.Campaign.Tagline.Length > 0 ? "*" + Escape(source.Campaign.Tagline) + "*" : null);
            Paragraph(sb, links, source.Campaign.Description);
        });

        Add("cast", labels.Cast, sb =>
        {
            foreach (var actor in source.Actors.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Head(sb, "## ", actor.Name, "portrait", actor.Id, hasArt: actor.HasPortrait, placement: BookArtPlacement.Portrait, subject: BookSlots.ActorSubject(source.Audience, actor));
                Paragraph(sb, links, actor.PublicDescription);
                Paragraph(sb, links, actor.Biography);
            }
        });

        Add("gazetteer", labels.Gazetteer, sb =>
        {
            foreach (var region in source.Regions.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Head(sb, "## ", region.Name);
                foreach (var place in region.Locations)
                {
                    Head(sb, "### ", place.Name, "location", region.Id, place.Id, place.HasArt, BookArtPlacement.Place, BookSlots.LocationSubject(region.Id, place.Id));
                    Paragraph(sb, links, place.Description);
                }
            }
        });

        Add("chronicle", labels.Chronicle, sb =>
        {
            foreach (var session in source.Sessions)
            {
                sb.Append("## ").Append(Escape(session.Title)).Append("\n\n");
                Paragraph(sb, links, session.Recap);
                if (source.Audience == BookAudience.Dm && !string.IsNullOrWhiteSpace(session.DmSummary))
                {
                    sb.Append(":::dm\n").Append(session.DmSummary.Trim()).Append("\n:::\n\n");
                }
            }
        });

        Add("lore", labels.Lore, sb =>
        {
            // Each fact is a paragraph and its details follow as they were written: a secret block inside them must keep its own
            // lines, or its marker stops being a marker and prints.
            foreach (var fact in source.Facts)
            {
                Paragraph(sb, links, "**" + Escape(fact.Summary) + "**");
                Paragraph(sb, links, fact.Details);
            }
        });

        Add("pages", labels.Pages, sb =>
        {
            foreach (var page in source.Wiki.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                Head(sb, "## ", page.Title, "wiki", page.Id, hasArt: page.HasArt, placement: BookArtPlacement.Place, subject: BookSlots.WikiSubject(page.Id));
                Paragraph(sb, links, page.Body);
            }
        });

        Add("homebrew", labels.Homebrew, sb =>
        {
            foreach (var entry in source.Homebrew.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Head(sb, "## ", entry.Name, "homebrew", entry.Id, hasArt: entry.HasArt, placement: BookArtPlacement.Portrait, subject: BookSlots.HomebrewSubject(entry.Id));
                Paragraph(sb, links, entry.Description);
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
    public string SlotKey => BookSlots.KeyFor(Subject);
}

/// <summary>One existing picture and the entry whose heading it follows.</summary>
public sealed record BookArtAnchor(BookEntryPosition Entry, string Asset);
