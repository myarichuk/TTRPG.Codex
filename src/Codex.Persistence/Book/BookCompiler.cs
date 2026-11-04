using System.Text;
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
    public static BookDocument Compile(BookSource source, string language, BookLabels? labels = null)
    {
        labels ??= new BookLabels();
        var chapters = new List<BookSection>();

        void Add(string id, string title, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            body(sb);
            if (sb.Length > 0)
            {
                chapters.Add(new BookSection(id, "# " + Escape(title) + "\n\n" + sb));
            }
        }

        Add("overview", labels.Overview, sb =>
        {
            Paragraph(sb, source.Campaign.Tagline.Length > 0 ? "*" + Escape(source.Campaign.Tagline) + "*" : null);
            Paragraph(sb, source.Campaign.Description);
        });

        Add("cast", labels.Cast, sb =>
        {
            foreach (var actor in source.Actors.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.Append("## ").Append(Escape(actor.Name)).Append("\n\n");
                Paragraph(sb, actor.PublicDescription);
                Paragraph(sb, actor.Biography);
            }
        });

        Add("gazetteer", labels.Gazetteer, sb =>
        {
            foreach (var region in source.Regions.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.Append("## ").Append(Escape(region.Name)).Append("\n\n");
                foreach (var place in region.Locations)
                {
                    sb.Append("### ").Append(Escape(place.Name)).Append("\n\n");
                    Paragraph(sb, place.Description);
                }
            }
        });

        Add("chronicle", labels.Chronicle, sb =>
        {
            foreach (var session in source.Sessions)
            {
                sb.Append("## ").Append(Escape(session.Title)).Append("\n\n");
                Paragraph(sb, session.Recap);
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
                Paragraph(sb, "**" + Escape(fact.Summary) + "**");
                Paragraph(sb, fact.Details);
            }
        });

        Add("pages", labels.Pages, sb =>
        {
            foreach (var page in source.Wiki.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.Append("## ").Append(Escape(page.Title)).Append("\n\n");
                Paragraph(sb, page.Body);
            }
        });

        Add("homebrew", labels.Homebrew, sb =>
        {
            foreach (var entry in source.Homebrew.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.Append("## ").Append(Escape(entry.Name)).Append("\n\n");
                Paragraph(sb, entry.Description);
            }
        });

        return new BookDocument(source.Campaign.Name, source.Campaign.Tagline, language, chapters, labels);
    }

    /// <summary>
    /// Where this audience's existing pictures go: after the heading of the entry they belong to, as <c>(section, heading line, which
    /// repeat of that line)</c>. Only pictures in <paramref name="art"/> are listed, and <paramref name="art"/> was read from the same
    /// source, so an entry the audience may not see has neither a heading nor an anchor. The anchors are computed beside
    /// <see cref="Compile"/>, in its order, so the two cannot disagree about which "Vera" a picture is for, and a picture is placed
    /// after the chapter is written (or rewritten) and never goes through a model.
    /// </summary>
    public static IReadOnlyList<BookArtAnchor> ArtAnchors(BookSource source, BookArt art)
    {
        var anchors = new List<BookArtAnchor>();
        var seen = new Dictionary<(string Section, string Heading), int>();

        void Entry(string section, string prefix, string name, string? asset, string placement)
        {
            var heading = prefix + Escape(name);
            var repeat = seen.GetValueOrDefault((section, heading));
            seen[(section, heading)] = repeat + 1;
            if (asset != null)
            {
                anchors.Add(new BookArtAnchor(section, heading, repeat, asset, name, placement));
            }
        }

        foreach (var actor in source.Actors.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // The flag as the audience's source holds it (a stranger's mask has none): placement does not rely on whoever built the map.
            Entry("cast", "## ", actor.Name, actor.HasPortrait ? art.Portrait(actor.Id) : null, BookArtPlacement.Portrait);
        }

        foreach (var region in source.Regions.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Entry("gazetteer", "## ", region.Name, null, BookArtPlacement.Place);
            foreach (var place in region.Locations)
            {
                Entry("gazetteer", "### ", place.Name, place.HasArt ? art.Location(region.Id, place.Id) : null, BookArtPlacement.Place);
            }
        }

        foreach (var page in source.Wiki.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Entry("pages", "## ", page.Title, page.HasArt ? art.Wiki(page.Id) : null, BookArtPlacement.Place);
        }

        foreach (var entry in source.Homebrew.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Entry("homebrew", "## ", entry.Name, entry.HasArt ? art.Homebrew(entry.Id) : null, BookArtPlacement.Portrait);
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

    private static void Paragraph(StringBuilder sb, string? markdown)
    {
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            sb.Append(markdown.Trim()).Append("\n\n");
        }
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
}

/// <summary>One existing picture and the heading it follows. <paramref name="Repeat"/> is 0 for the first entry with that heading line, 1 for the
/// second, so two characters with one name each get their own picture.</summary>
public sealed record BookArtAnchor(string Section, string Heading, int Repeat, string Asset, string Alt, string Placement);
