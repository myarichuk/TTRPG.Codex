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
    /// <para><paramref name="slots"/> (this edition's, <see cref="BookSlots.For"/>) adds what the GM put in the book's own slots: a header under
    /// each chapter's title (only when the chapter still begins with its title line) and a cover in place of the campaign's.</para>
    public static BookDocument PlaceArt(BookDocument document, BookDocument compiled, IReadOnlyList<BookEntryPosition> entries, BookArt art, out int placed,
        IReadOnlyList<BookSlot>? slots = null)
    {
        var anchors = BookCompiler.ArtAnchors(entries, art);
        var headers = new Dictionary<string, (string Asset, string Alt)>(StringComparer.Ordinal);
        string? cover = null;
        foreach (var slot in slots ?? [])
        {
            if (art.Slot(slot.Key, slot.Audience) is not { } asset) continue;
            if (slot.Kind == BookSlots.Header) headers[slot.Section] = (asset, slot.Label);
            else if (slot.Kind == BookSlots.Cover) cover = asset;
        }

        var count = 0;
        var sections = document.Sections.Select(section =>
        {
            var mine = anchors.Where(a => a.Entry.Section == section.Id).ToList();
            var header = headers.TryGetValue(section.Id, out var h) ? h : default((string Asset, string Alt)?);
            if (mine.Count == 0 && header == null) return section;
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

            // The header goes under the title, which is the chapter's first line whoever wrote the chapter (a rewrite gets its title from code).
            var headed = header is { } top && lines.Count > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal) ? top : default((string Asset, string Alt)?);
            if (after.Count == 0 && headed == null) return section;
            var output = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                output.Add(raw[i]);
                if (i == 0 && headed is { } art0)
                {
                    count++;
                    Block(output, BookArtPlacement.Header, art0.Alt, art0.Asset);
                }

                if (!after.TryGetValue(i, out var anchor)) continue;
                count++;
                Block(output, anchor.Entry.Placement, anchor.Entry.Alt, anchor.Asset);
            }

            return section with { Markdown = string.Join('\n', output) };
        }).ToList();
        cover ??= art.Cover();
        placed = count + (cover != null ? 1 : 0);
        return document with { Sections = sections, CoverAsset = cover };
    }

    private static void Block(List<string> output, string placement, string alt, string asset)
    {
        output.Add(string.Empty);
        output.Add("{{" + placement);
        output.Add("![" + BookCompiler.Escape(alt) + "](asset:" + asset + ")");
        output.Add("}}");
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
