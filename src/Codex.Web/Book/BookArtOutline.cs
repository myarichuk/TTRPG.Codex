using System.Security.Cryptography;
using System.Text;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>One slot as the art director would be shown it, and a hash of exactly that (<see cref="BookArtSuggestion.Inputs"/>).</summary>
/// <param name="SpotOf">For an entry's spot: the entry's own slot key. A spot is never asked about alone: the director may suggest one with its
/// entry (docs/BOOK_EXPORT.md 28.3, step 7).</param>
public sealed record BookOutlineSlot(BookSlot Slot, string Kind, string Text, string Inputs, string? SpotOf = null);

public sealed record BookOutlineChapter(string Id, string Title, IReadOnlyList<BookOutlineSlot> Slots);

/// <summary>
/// What the art director sees of an edition: per chapter, each slot's heading and a short excerpt of its entry. The excerpts are cut from the
/// chapter <i>after</i> it is prepared the way every chapter a model sees is prepared (<see cref="BookWork.Prepare"/>): secrets out, names as the
/// edition gives them. A line that marks where each entry starts is put in place of its heading first, and the prepared text is split on those
/// lines; a secret that runs past an entry's end (one never closed) swallows the next entry's marker, and that entry is then shown with no text,
/// never with text a secret held.
/// </summary>
public static class BookArtOutline
{
    public const int ExcerptChars = 700;

    private const char Mark = '\u0002';

    public static IReadOnlyList<BookOutlineChapter> For(BookSource source, BookDocument compiled, IReadOnlyList<BookEntryPosition> entries, IReadOnlyList<BookSlot> slots)
    {
        var render = new BookMarkdownOptions { Mention = BookCompiler.Mentions(source) };
        var chapters = new List<BookOutlineChapter>();
        var bySection = entries.GroupBy(e => e.Section).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Line).ToList(), StringComparer.Ordinal);
        var slotByKey = slots.ToDictionary(s => s.Key, StringComparer.Ordinal);
        string? overview = null;

        foreach (var section in compiled.Sections)
        {
            var lines = section.Markdown.Split('\n');
            var mine = bySection.GetValueOrDefault(section.Id) ?? [];
            for (var i = 0; i < mine.Count; i++)
            {
                var at = mine[i].Line;
                if (at > 0 && at < lines.Length && lines[at].TrimEnd('\r', ' ', '\t') == mine[i].Heading) lines[at] = Marker(i);
            }

            var body = string.Join('\n', lines.Skip(lines.Length > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal) ? 1 : 0));
            var prepared = BookWork.Prepare(body, render.Mention, render).Text;
            var parts = Split(prepared);
            var title = slots.FirstOrDefault(s => s.Kind == BookSlots.Header && s.Section == section.Id)?.Label ?? section.Id;
            var outline = new List<BookOutlineSlot>();
            if (slots.FirstOrDefault(s => s.Kind == BookSlots.Header && s.Section == section.Id) is { } header)
            {
                outline.Add(Make(header, "header", Excerpt(string.Join("\n\n", parts.Values))));
            }

            for (var i = 0; i < mine.Count; i++)
            {
                if (!slotByKey.TryGetValue(mine[i].SlotKey, out var slot)) continue;
                var excerpt = Excerpt(parts.GetValueOrDefault(i, string.Empty));
                outline.Add(Make(slot, mine[i].Kind, excerpt));
                if (slotByKey.TryGetValue(BookSlots.SpotKey(mine[i]), out var spot)) outline.Add(Make(spot, "spot", excerpt) with { SpotOf = slot.Key });
            }

            if (section.Id == "overview") overview = Excerpt(string.Join("\n\n", parts.Values));
            chapters.Add(new BookOutlineChapter(section.Id, title, outline));
        }

        // The cover is asked about with the campaign's overview, as the reader would first meet it.
        if (slots.FirstOrDefault(s => s.Kind == BookSlots.Cover) is { } cover)
        {
            chapters.Insert(0, new BookOutlineChapter("cover", cover.Label, [Make(cover, "cover", overview ?? string.Empty)]));
        }

        return chapters;
    }

    /// <summary>The edition's proper names, for the visual check: what it calls its characters (not a stranger's mask, which is a description),
    /// places, regions, pages and entries, and the campaign. Never sent to a model.</summary>
    public static IReadOnlyList<string> Names(BookSource source, IReadOnlyList<BookEntryPosition> entries)
    {
        var names = entries.Where(e => !e.IsGroup && !e.Subject.StartsWith("mask:", StringComparison.Ordinal)).Select(e => e.Alt)
            .Concat(source.Regions.Select(r => r.Name))
            .Append(source.Campaign.Name);
        return names.Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static BookOutlineSlot Make(BookSlot slot, string kind, string text) => new(slot, kind, text, Hash(kind, slot.Label, text));

    private static string Hash(string kind, string label, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(BookDirectorPrompts.Version + "\n" + kind + "\n" + label + "\n" + text));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static string Marker(int index) => Mark + "entry-" + index + Mark;

    // Part -1 is the text before the first entry; part i is entry i's. An entry whose marker a secret swallowed has no part.
    private static Dictionary<int, string> Split(string prepared)
    {
        var parts = new Dictionary<int, string>();
        var current = -1;
        var sb = new StringBuilder();
        foreach (var line in prepared.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 2 && t[0] == Mark && t[^1] == Mark && t[1..^1].StartsWith("entry-", StringComparison.Ordinal) && int.TryParse(t[7..^1], out var index))
            {
                parts[current] = sb.ToString();
                sb.Clear();
                current = index;
                continue;
            }

            sb.Append(line).Append('\n');
        }

        parts[current] = sb.ToString();
        return parts;
    }

    // An excerpt: no block markers (the director places nothing), no stray marks, whitespace folded, cut at a word near the limit.
    private static string Excerpt(string text)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(text.Replace(Mark, ' '), @"\{\{block:\d+\}\}", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
        if (plain.Length <= ExcerptChars) return plain;
        var cut = plain.LastIndexOf(' ', ExcerptChars);
        return plain[..(cut > ExcerptChars / 2 ? cut : ExcerptChars)] + " …";
    }
}
