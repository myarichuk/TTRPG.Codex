using System.Text;
using Codex.Core.Book;

namespace Codex.Persistence;

/// <summary>A part of a chapter the merge works in: the head (<paramref name="Id"/> empty: the chapter's title, its picture and whatever comes
/// before the first entry) or one entry, from its <c>{{entry,#e-…}}</c> marker to the next. <paramref name="Text"/> is exactly the chapter's
/// text there, blank lines and breaks included, so the units of a chapter put together are the chapter.</summary>
public sealed record BookDraftUnit(string Id, string Text);

/// <summary>How the merge settled a unit (docs/BOOK_EXPORT.md 41.18).</summary>
public enum BookMergeKind
{
    /// <summary>Nobody changed it, or both made the same change: the GM's text.</summary>
    Same,
    /// <summary>The GM changed it and the campaign did not: the GM's text.</summary>
    Mine,
    /// <summary>The campaign changed it and the GM did not: the campaign's text.</summary>
    Theirs,
    /// <summary>Both changed it: the AI's merge when there is one, otherwise the campaign's text, which the GM may reject.</summary>
    Conflict,
    /// <summary>An entry new in the campaign.</summary>
    Added,
    /// <summary>An entry the campaign no longer has (hidden or deleted): offered for removal.</summary>
    Removed,
    /// <summary>An entry the GM had deleted and the campaign has changed since: offered back.</summary>
    Restored
}

/// <summary>One unit's three versions and what the merge puts in its place (empty: nothing).</summary>
public sealed record BookMergeUnit(string Id, BookMergeKind Kind, string Base, string Mine, string Theirs, string Text);

/// <summary>A chapter's merge, unit by unit, in the order the result has them.</summary>
public sealed record BookMergePlan(IReadOnlyList<BookMergeUnit> Units)
{
    public int Count(BookMergeKind kind) => Units.Count(u => u.Kind == kind);

    /// <summary>The chapter the plan makes, with <paramref name="resolved"/> standing in for the conflicts it has an answer for.</summary>
    public string Text(IReadOnlyDictionary<string, string>? resolved = null) =>
        BookDraftMerge.Join(Units.Select(u => u.Kind == BookMergeKind.Conflict && resolved != null && resolved.TryGetValue(u.Id, out var r) ? r : u.Text));
}

/// <summary>A conflict as the model gets it: the GM's unit cut like an AI edit (its pieces kept back, each settled by the code already), and
/// the base and the campaign's version with every piece left out.</summary>
public sealed record BookMergeConflict(string Id, BookEditPassage Mine, string Base, string Theirs);

/// <summary>
/// "Bring up to date" (docs/BOOK_EXPORT.md 41.3, 41.18): a three-way merge of the chapter as generated when the GM's edits began (the base), the
/// GM's text (mine) and the chapter as the campaign makes it now (theirs), <b>entry by entry, by the code</b>. Entries are matched by the id the
/// generator gives each, never by a line diff. Per unit: the campaign did not change it, the GM's; the GM did not, the campaign's; both made the
/// same change, the GM's; otherwise a conflict, the only part a model is ever asked about. Units are compared without page and column breaks,
/// blank lines or trailing spaces, so neither Fit pages nor a model's tidying makes a unit "edited". A unit kept is the GM's text exactly,
/// breaks included.
/// </summary>
public static class BookDraftMerge
{
    private const string Marker = "{{entry,#";

    /// <summary>The chapter's units, or null when an entry's id is there twice (a copied entry): the merge cannot tell which is which.</summary>
    public static IReadOnlyList<BookDraftUnit>? Split(string chapter)
    {
        var units = new List<BookDraftUnit>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var id = string.Empty;
        var start = 0;
        var at = 0;
        while (at < chapter.Length)
        {
            var end = chapter.IndexOf('\n', at);
            var next = end < 0 ? chapter.Length : end + 1;
            if (EntryId(chapter[at..(end < 0 ? chapter.Length : end)]) is { } found)
            {
                if (at > start || id.Length > 0) units.Add(new BookDraftUnit(id, chapter[start..at]));
                if (!ids.Add(found)) return null;
                id = found;
                start = at;
            }

            at = next;
        }

        if (start < chapter.Length || id.Length > 0 || units.Count == 0) units.Add(new BookDraftUnit(id, chapter[start..]));
        return units;
    }

    /// <summary>The id of an entry's marker line (<c>{{entry,#e-…}}</c>, spaces around it allowed), or null for any other line.</summary>
    public static string? EntryId(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith(Marker, StringComparison.Ordinal) || !t.EndsWith("}}", StringComparison.Ordinal)) return null;
        var id = t[Marker.Length..^2].Trim();
        return id.Length is > 0 and <= 120 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    /// <summary>A unit as the merge compares it: no page or column breaks, no blank lines, no trailing spaces.</summary>
    public static string Normal(string text) =>
        string.Join('\n', BookDraftText.Content(text).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));

    private static bool Same(string a, string b) => Normal(a) == Normal(b);

    /// <summary>The merge, or null when a version has an entry twice.</summary>
    public static BookMergePlan? Plan(string @base, string mine, string theirs)
    {
        if (Split(@base) is not { } b || Split(mine) is not { } m || Split(theirs) is not { } n) return null;
        var was = b.ToDictionary(u => u.Id, u => u.Text, StringComparer.Ordinal);
        var now = n.ToDictionary(u => u.Id, u => u.Text, StringComparer.Ordinal);
        var result = new List<BookMergeUnit>();

        // The GM's order, unit by unit.
        foreach (var unit in m)
        {
            was.TryGetValue(unit.Id, out var bt);
            if (!now.TryGetValue(unit.Id, out var nt))
            {
                // The campaign no longer has it: offered for removal. One the generator never wrote (an id the GM typed) is the GM's.
                result.Add(bt == null && unit.Id.Length > 0
                    ? new BookMergeUnit(unit.Id, BookMergeKind.Mine, string.Empty, unit.Text, string.Empty, unit.Text)
                    : new BookMergeUnit(unit.Id, BookMergeKind.Removed, bt ?? string.Empty, unit.Text, string.Empty, string.Empty));
                continue;
            }

            bt ??= string.Empty;
            var kind = Same(nt, bt) ? Same(unit.Text, bt) ? BookMergeKind.Same : BookMergeKind.Mine
                : Same(unit.Text, bt) ? BookMergeKind.Theirs
                : Same(unit.Text, nt) ? BookMergeKind.Same
                : BookMergeKind.Conflict;
            result.Add(new BookMergeUnit(unit.Id, kind, bt, unit.Text, nt, kind == BookMergeKind.Theirs || kind == BookMergeKind.Conflict ? nt : unit.Text));
        }

        // What the campaign has and the GM's text does not: new entries, and ones the GM deleted (back only if the campaign changed them since).
        var placed = result.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < n.Count; i++)
        {
            var unit = n[i];
            if (placed.Contains(unit.Id)) continue;
            BookMergeUnit added;
            if (was.TryGetValue(unit.Id, out var bt))
            {
                if (Same(unit.Text, bt)) continue;
                added = new BookMergeUnit(unit.Id, BookMergeKind.Restored, bt, string.Empty, unit.Text, unit.Text);
            }
            else
            {
                added = new BookMergeUnit(unit.Id, BookMergeKind.Added, string.Empty, string.Empty, unit.Text, unit.Text);
            }

            // After the unit the campaign puts before it, where the GM's text has that one; else at the end.
            var after = -1;
            for (var j = i - 1; j >= 0 && after < 0; j--) after = result.FindIndex(u => u.Id == n[j].Id);
            result.Insert(after < 0 ? result.Count : after + 1, added);
            placed.Add(unit.Id);
        }

        return new BookMergePlan(result);
    }

    /// <summary>Units' texts put together: each that is not empty, a blank line between two that would otherwise run together.</summary>
    public static string Join(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            if (sb.Length > 0 && !EndsWithBlank(sb)) sb.Append(sb[^1] == '\n' ? "\n" : "\n\n");
            sb.Append(part);
        }

        return sb.ToString();
    }

    private static bool EndsWithBlank(StringBuilder sb) => sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n';

    /// <summary>
    /// A conflict ready for a model, or null when its pieces cannot be settled by the code: the GM's and the campaign's versions must have the
    /// same pieces, of the same kinds, in the same order. Each piece is settled as a unit is (the campaign's when the GM left it as it was, the
    /// GM's otherwise), and put back in its place after; the model sees only the GM's text with markers, and the base and the campaign's text
    /// with every piece left out. So no secret of any of the three versions is ever sent.
    /// </summary>
    public static BookMergeConflict? Conflict(BookMergeUnit unit)
    {
        if (unit.Kind != BookMergeKind.Conflict) return null;
        if (BookDraftEdits.Cut(unit.Mine, 0, unit.Mine.Length) is not { } mine || BookDraftEdits.Cut(unit.Theirs, 0, unit.Theirs.Length) is not { } theirs) return null;
        if (mine.Pieces.Count != theirs.Pieces.Count || mine.Pieces.Zip(theirs.Pieces).Any(p => p.First.Label != p.Second.Label)) return null;
        var @base = BookDraftEdits.Cut(unit.Base, 0, unit.Base.Length);
        var lined = @base != null && @base.Pieces.Count == mine.Pieces.Count && @base.Pieces.Zip(mine.Pieces).All(p => p.First.Label == p.Second.Label);
        var pieces = mine.Pieces.Select((p, i) =>
        {
            var t = theirs.Pieces[i].Text;
            var b = lined ? @base!.Pieces[i].Text : null;
            // The campaign's piece when the GM left it as it was; the GM's when the campaign did not change it, or both did.
            return b != null && Same(p.Text, b) && !Same(t, b) ? p with { Text = t } : p;
        }).ToList();
        return new BookMergeConflict(unit.Id, mine with { Pieces = pieces }, BookDraftEdits.Bare(unit.Base), BookDraftEdits.Bare(unit.Theirs));
    }
}
