using System.Text;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>
/// One player's own copy of the party book (docs/BOOK_EXPORT.md 45): the party draft as the GM keeps it, with what only that player's characters
/// may read added in notes. What is added is found by compiling the edition twice, once from the party's source and once from the player's
/// (<see cref="BookSourceReader.ReadForPlayerAsync"/>), and taking the paragraphs the player's copy has that the party's does not: the
/// <c>:::for</c> blocks naming one of their characters, lore revealed to them. The player's source is read through that player's own access and
/// cleaned for their characters, so nothing in it is more than they may open in the app; the party's compile is a subsequence of theirs, entry by
/// entry, because both are written by the same compiler from the same records, one with more of each text kept.
/// <para>Each entry's extras go in a <c>{{note</c> under that entry in the draft (found by its <c>{{entry,#e-KEY}}</c> marker, the entry's slot
/// key, which is the same in both copies), before the next entry; the text before a chapter's first entry (lore, an overview) gets its note before
/// that entry. Whatever has no place in the draft (an entry the GM deleted, an entry only this player has, a chapter the party book has not got)
/// goes at the chapter's end or in a chapter of its own at the book's end. The copy is made when it is asked for: never stored, never sent to a
/// model, and the party's pictures only.</para>
/// </summary>
public static class BookPlayerBook
{
    // A section's part before its first entry.
    private const string Before = "";

    /// <summary>The draft's chapters with the player's notes in place. <paramref name="heading"/> titles each note ("Only for Mira").</summary>
    public static IReadOnlyList<BookDraftPageChapter> Overlay(IReadOnlyList<BookDraftPageChapter> chapters, BookSource party, BookSource player, string language,
        string heading)
    {
        var partyBook = BookComposer.Compose(party, language, out var partyEntries);
        var playerBook = BookComposer.Compose(player, language, out var playerEntries);
        var partySections = partyBook.Sections.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var mention = BookCompiler.Mentions(player);
        var result = chapters.ToList();

        foreach (var section in playerBook.Sections)
        {
            var mine = Units(section, playerEntries);
            var theirs = partySections.TryGetValue(section.Id, out var shared) ? Units(shared, partyEntries) : new Dictionary<string, List<string>>();
            var notes = new List<(string Unit, string Text)>();
            foreach (var (unit, paragraphs) in mine)
            {
                var extra = Extra(paragraphs, theirs.GetValueOrDefault(unit) ?? []);
                if (extra.Count == 0) continue;
                // Under the note's own title (a fifth-level heading): a heading in the text is the smallest there is.
                var text = BookDraftWriter.Text(string.Join("\n\n", extra) + "\n", mention, dm: false, level: 5).Trim('\n');
                if (text.Length > 0) notes.Add((unit, text));
            }

            if (notes.Count == 0) continue;
            var at = result.FindIndex(c => c.Section == section.Id);
            if (at < 0)
            {
                var title = section.Markdown.ReplaceLineEndings("\n").Split('\n')[0];
                var body = new StringBuilder("{{chapter,#ch-").Append(section.Id).Append('\n').Append(title).Append("\n}}\n\n");
                foreach (var note in notes) body.Append(Note(heading, note.Text)).Append("\n\n");
                result.Add(new BookDraftPageChapter(section.Id, title.TrimStart('#', ' '), body.ToString().TrimEnd('\n') + "\n", 0));
                continue;
            }

            var markdown = result[at].Markdown;
            foreach (var note in notes) markdown = Insert(markdown, note.Unit, Note(heading, note.Text));
            result[at] = result[at] with { Markdown = markdown };
        }

        return result;
    }

    /// <summary>The note's own Markdown: Homebrewery's note block, its heading the note's title.</summary>
    public static string Note(string heading, string text) => "{{note\n##### " + BookCompiler.Escape(heading) + "\n\n" + text + "\n}}";

    // A section's paragraphs by unit: the part before its first entry, then each entry's (its heading line included), keyed by the entry's slot key.
    private static Dictionary<string, List<string>> Units(BookSection section, IReadOnlyList<BookEntryPosition> entries)
    {
        var starts = entries.Where(e => e.Section == section.Id).ToDictionary(e => e.Line);
        var lines = section.Markdown.ReplaceLineEndings("\n").Split('\n');
        var units = new Dictionary<string, List<string>>(StringComparer.Ordinal) { [Before] = [] };
        var unit = Before;
        var paragraph = new StringBuilder();
        void Flush()
        {
            var text = paragraph.ToString().Trim('\n');
            if (text.Trim().Length > 0) units[unit].Add(text);
            paragraph.Clear();
        }

        // The first line is the chapter's title, never part of an entry.
        for (var i = 1; i < lines.Length; i++)
        {
            if (starts.TryGetValue(i, out var entry) && lines[i] == entry.Heading)
            {
                Flush();
                unit = entry.SlotKey;
                units.TryAdd(unit, []);
            }

            if (lines[i].Trim().Length == 0)
            {
                Flush();
                continue;
            }

            paragraph.Append(lines[i]).Append('\n');
        }

        Flush();
        return units;
    }

    /// <summary>The paragraphs of <paramref name="mine"/> that are not in <paramref name="theirs"/>, in order: everything off a longest common
    /// subsequence of the two. A paragraph the party's copy has in another form (a <c>:::for</c> block written inside it, with no blank line
    /// around it) is the player's in full.</summary>
    public static IReadOnlyList<string> Extra(IReadOnlyList<string> mine, IReadOnlyList<string> theirs)
    {
        if (theirs.Count == 0) return mine;
        var n = mine.Count;
        var m = theirs.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = mine[i] == theirs[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var extra = new List<string>();
        for (int i = 0, j = 0; i < n;)
        {
            if (j < m && mine[i] == theirs[j])
            {
                i++;
                j++;
            }
            else if (j < m && lcs[i, j + 1] >= lcs[i + 1, j]) j++;
            else extra.Add(mine[i++]);
        }

        return extra;
    }

    // The note goes after the entry the unit names, before the next entry's marker (and before the page breaks that lead to it), so it stays on
    // the entry's page; the part before the first entry goes before that entry. No marker for the unit: the chapter's end.
    private static string Insert(string markdown, string unit, string note)
    {
        var lines = markdown.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').ToList();
        var start = unit == Before ? 0 : lines.FindIndex(l => l.Trim() == "{{entry,#e-" + unit + "}}");
        var at = lines.Count;
        if (start >= 0)
        {
            var next = lines.FindIndex(unit == Before ? 0 : start + 1, l => l.TrimStart().StartsWith("{{entry,", StringComparison.Ordinal));
            if (next >= 0)
            {
                at = next;
                while (at > start + 1 && (lines[at - 1].Trim().Length == 0 || BookDraftText.IsBreak(lines[at - 1].Trim()))) at--;
            }
        }

        // A blank line on each side, unless one is there already: the GM's own text is left as it is.
        var block = note.Split('\n').ToList();
        if (at < lines.Count && lines[at].Trim().Length > 0) block.Add(string.Empty);
        if (at > 0 && lines[at - 1].Trim().Length > 0) block.Insert(0, string.Empty);
        lines.InsertRange(at, block);
        return string.Join('\n', lines) + "\n";
    }
}
