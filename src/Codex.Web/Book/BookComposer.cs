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

    /// <summary>As <see cref="Compose(BookSource, string)"/>, with the positions of the headings that can carry a picture (for <see cref="PlaceArt"/>).</summary>
    public static BookDocument Compose(BookSource source, string language, out IReadOnlyList<BookEntryPosition> entries) =>
        BookCompiler.Compile(source, language, Labels(language), out entries);

    /// <summary>
    /// The document with the audience's existing pictures placed: the cover on the title page and each entry's picture after its heading.
    /// This runs on the finished chapters and never before a model: a model is not shown a picture, cannot move or drop one, and the
    /// stored-build key does not see them.
    /// <para>Where a picture goes is decided by what is known about the chapter. A chapter that is still exactly what the compiler wrote
    /// (<paramref name="compiled"/>) is placed by the line the compiler recorded for the entry's heading, so a body line that reads like a
    /// heading, in an earlier entry or inside a code fence, cannot take the picture. A chapter a model rewrote may have changed the
    /// order, the repeats and the lines, and the checks only keep the <i>set</i> of heading texts: its picture is placed only when its heading
    /// is unambiguous (one entry has that text and one line of the chapter reads that way), and otherwise left out. A missing picture
    /// is the safe failure; a picture under another entry's heading is not. <paramref name="placed"/> counts what was placed.</para>
    /// </summary>
    public static BookDocument PlaceArt(BookDocument document, BookDocument compiled, IReadOnlyList<BookEntryPosition> entries, BookArt art, out int placed)
    {
        var anchors = BookCompiler.ArtAnchors(entries, art);
        var count = 0;
        var sections = document.Sections.Select(section =>
        {
            var mine = anchors.Where(a => a.Entry.Section == section.Id).ToList();
            if (mine.Count == 0) return section;
            var raw = section.Markdown.Split('\n');
            // Compared trimmed, written back as they were: two trailing spaces are a line break in this dialect.
            var lines = raw.Select(l => l.TrimEnd('\r', ' ', '\t')).ToList();
            var pristine = compiled.Sections.Any(c => c.Id == section.Id && c.Markdown == section.Markdown);
            var inFence = new bool[lines.Count];
            var fenced = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
                inFence[i] = fenced;
            }

            var after = new Dictionary<int, BookArtAnchor>();
            foreach (var anchor in mine)
            {
                int at;
                if (pristine)
                {
                    at = anchor.Entry.Line;
                    if (at < 0 || at >= lines.Count || lines[at] != anchor.Entry.Heading || inFence[at]) continue;
                }
                else
                {
                    var same = entries.Count(e => e.Section == section.Id && e.Heading == anchor.Entry.Heading);
                    var found = Enumerable.Range(0, lines.Count).Where(i => !inFence[i] && lines[i] == anchor.Entry.Heading).ToList();
                    if (same != 1 || found.Count != 1) continue;
                    at = found[0];
                }

                after[at] = anchor;
            }

            if (after.Count == 0) return section;
            var output = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                output.Add(raw[i]);
                if (!after.TryGetValue(i, out var anchor)) continue;
                count++;
                output.Add(string.Empty);
                output.Add("{{" + anchor.Entry.Placement);
                output.Add("![" + BookCompiler.Escape(anchor.Entry.Alt) + "](asset:" + anchor.Asset + ")");
                output.Add("}}");
            }

            return section with { Markdown = string.Join('\n', output) };
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
