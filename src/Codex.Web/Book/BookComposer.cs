using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>
/// The one place a book is composed from a source, so the page, the PDF and the AI build all see the same chapters: the labels in the
/// reader's language, the deterministic chapters, the prepared chapter a model is given, and the stored rewrite that may stand in for
/// a chapter (only while it was made from exactly what the chapter is made from now).
/// </summary>
public static class BookComposer
{
    /// <summary>A chapter with less text than this is not worth a model call: it prints as written.</summary>
    public const int MinCharsToPolish = 200;

    public static string Edition(BookAudience audience) => audience == BookAudience.Dm ? "dm" : "party";

    public static BookLabels Labels(string language)
    {
        var words = Localizer.For(language);
        return new BookLabels(
            Contents: words["Campaign.Book.Label.Contents"], Overview: words["Campaign.Book.Label.Overview"], Cast: words["Campaign.Book.Label.Cast"],
            Gazetteer: words["Campaign.Book.Label.Gazetteer"], Chronicle: words["Campaign.Book.Label.Chronicle"], Lore: words["Campaign.Book.Label.Lore"],
            Pages: words["Campaign.Book.Label.Pages"], Homebrew: words["Campaign.Book.Label.Homebrew"]);
    }

    public static BookDocument Compose(BookSource source, string language) => BookCompiler.Compile(source, language, Labels(language));

    /// <summary>
    /// The document with the audience's existing pictures placed: the cover on the title page and each entry's picture after its heading.
    /// This runs on the finished chapters (deterministic or rewritten) and never before a model: a model is not shown a picture, cannot
    /// move or drop one, and the stored-build key does not see them. A rewritten chapter whose heading no longer reads as the entry's
    /// name simply gets no picture, and <paramref name="placed"/> counts what was placed so a panel can say so.
    /// </summary>
    public static BookDocument PlaceArt(BookDocument document, BookSource source, BookArt art, out int placed)
    {
        var anchors = BookCompiler.ArtAnchors(source, art);
        var count = 0;
        var sections = document.Sections.Select(section =>
        {
            var mine = anchors.Where(a => a.Section == section.Id).ToList();
            if (mine.Count == 0) return section;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var lines = new List<string>();
            var fence = false;
            foreach (var raw in section.Markdown.Split('\n'))
            {
                var line = raw.TrimEnd('\r', ' ', '\t');
                lines.Add(line);
                // A fenced code block is text, not structure: a line in it that looks like a heading is not one, and a picture block
                // placed there would print as literal braces.
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                {
                    fence = !fence;
                    continue;
                }

                if (fence) continue;
                var repeat = seen.GetValueOrDefault(line);
                seen[line] = repeat + 1;
                var anchor = mine.FirstOrDefault(a => a.Heading == line && a.Repeat == repeat);
                if (anchor == null) continue;
                count++;
                lines.Add(string.Empty);
                lines.Add("{{" + anchor.Placement);
                lines.Add("![" + BookCompiler.Escape(anchor.Alt) + "](asset:" + anchor.Asset + ")");
                lines.Add("}}");
            }

            return section with { Markdown = string.Join('\n', lines) };
        }).ToList();
        placed = count + (art.Cover() != null ? 1 : 0);
        return document with { Sections = sections, CoverAsset = art.Cover() };
    }

    /// <summary>Each chapter prepared for a model, by id. The language is the primary subtag, so "en" and "en-US" are one key.</summary>
    public static Dictionary<string, BookWorkItem> Items(BookDocument document, BookSource source, string style)
    {
        var language = BookThemes.Primary(document.Language);
        var render = new BookMarkdownOptions { Mention = BookCompiler.Mentions(source) };
        return document.Sections.ToDictionary(s => s.Id, s => BookBuildRunner.Item(s, language, style, render.Mention, render), StringComparer.Ordinal);
    }

    /// <summary>A stored rewrite as a chapter. The boxes come from the chapter as it is read now, never from the store: the key says they are
    /// the same, and the DM's secret words then stay out of the stored build altogether.</summary>
    public static BookSection Stored(BookBuildSection kept, BookWorkItem now) =>
        new(kept.Id, kept.Markdown, now.Work.Blocks.Count == 0 ? null : now.Work.Blocks.ToDictionary(b => b.Number, b => b.Html));

    public static bool TooShort(BookWorkItem item) => item.Work.Text.Length < MinCharsToPolish;

    /// <summary>The document with every chapter that has a stored rewrite built from what it is made from now replaced by that rewrite.
    /// Any other chapter keeps its deterministic text: a chapter that changed, was hidden from this edition, or was built for another
    /// language, edition, style or prompt version never comes back from the store.</summary>
    public static BookDocument Overlay(BookDocument document, BookSource source, BookBuildDocument? stored)
    {
        if (stored == null) return document;
        var edition = Edition(source.Audience);
        if (stored.Audience != edition) return document;
        var items = Items(document, source, stored.Style);
        var sections = document.Sections.Select(s =>
        {
            var kept = stored.Sections.FirstOrDefault(b => b.Id == s.Id);
            return kept is { Refined: true } && items.TryGetValue(s.Id, out var item) && kept.Key == BookBuildKey.For(item, edition)
                ? Stored(kept, item)
                : s;
        }).ToList();
        return document with { Sections = sections };
    }
}
