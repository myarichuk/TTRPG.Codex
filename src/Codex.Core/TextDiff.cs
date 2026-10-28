using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>What a piece of a diff is: in both texts, only in the new one, or only in the old one.</summary>
public enum DiffKind
{
    Same,
    Added,
    Removed
}

/// <summary>A run of text a diff keeps, adds or removes.</summary>
public sealed record DiffPiece(DiffKind Kind, string Text);

/// <summary>
/// A word-level diff of two texts, for reviewing an edit beside what it replaces. The common start and end are cut off first (an
/// edit usually changes a passage, not the whole text), and the rest is compared word by word with a longest common subsequence.
/// A middle too big to compare that way (two unrelated long texts) comes back as one removal and one addition: the review still
/// shows both, and nothing is slow. Pure, so it is unit-tested.
/// </summary>
public static partial class TextDiff
{
    /// <summary>The most word pairs the comparison table holds; 4M shorts is 8 MB, once, for a review.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffPiece> Words(string? before, string? after)
    {
        var a = Tokens(before ?? string.Empty);
        var b = Tokens(after ?? string.Empty);
        var pieces = new List<DiffPiece>();

        var start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
        var endA = a.Count;
        var endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        Add(pieces, DiffKind.Same, a.Take(start));
        var n = endA - start;
        var m = endB - start;
        if ((long)n * m > MaxCells)
        {
            Add(pieces, DiffKind.Removed, a.Skip(start).Take(n));
            Add(pieces, DiffKind.Added, b.Skip(start).Take(m));
        }
        else
        {
            Middle(pieces, a, b, start, n, m);
        }

        Add(pieces, DiffKind.Same, a.Skip(endA));
        return Coalesce(pieces);
    }

    /// <summary>True when the two texts differ in more than spacing.</summary>
    public static bool Changed(string? before, string? after) =>
        !string.Equals(Space().Replace(before ?? string.Empty, " ").Trim(), Space().Replace(after ?? string.Empty, " ").Trim(), StringComparison.Ordinal);

    // The longest common subsequence of the middle, walked forwards so the pieces come out in order. Lengths are counted from the
    // end (table[i, j] = common words of a[i..] and b[j..]), so each step forward picks a word both keep, or the side that loses less.
    private static void Middle(List<DiffPiece> pieces, List<string> a, List<string> b, int start, int n, int m)
    {
        var table = new short[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = a[start + i] == b[start + j]
                    ? (short)Math.Min(short.MaxValue, table[i + 1, j + 1] + 1)
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[start + x] == b[start + y])
            {
                Add(pieces, DiffKind.Same, a[start + x]);
                x++;
                y++;
            }
            else if (y < m && (x == n || table[x, y + 1] >= table[x + 1, y]))
            {
                Add(pieces, DiffKind.Added, b[start + y]);
                y++;
            }
            else
            {
                Add(pieces, DiffKind.Removed, a[start + x]);
                x++;
            }
        }
    }

    private static void Add(List<DiffPiece> pieces, DiffKind kind, IEnumerable<string> tokens)
    {
        foreach (var token in tokens) Add(pieces, kind, token);
    }

    private static void Add(List<DiffPiece> pieces, DiffKind kind, string token)
    {
        if (pieces.Count > 0 && pieces[^1].Kind == kind) pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + token };
        else pieces.Add(new DiffPiece(kind, token));
    }

    /// <summary>Joins changes that only a space keeps apart, so "a b c" replaced reads as one removal and one addition, not three of
    /// each: the space goes into both sides. A line break still separates them.</summary>
    private static List<DiffPiece> Coalesce(List<DiffPiece> raw)
    {
        var result = new List<DiffPiece>();
        var removed = new System.Text.StringBuilder();
        var added = new System.Text.StringBuilder();
        for (var i = 0; i < raw.Count; i++)
        {
            var piece = raw[i];
            if (piece.Kind == DiffKind.Removed) removed.Append(piece.Text);
            else if (piece.Kind == DiffKind.Added) added.Append(piece.Text);
            else if (removed.Length + added.Length > 0 && i + 1 < raw.Count && string.IsNullOrWhiteSpace(piece.Text) && !piece.Text.Contains('\n'))
            {
                removed.Append(piece.Text);
                added.Append(piece.Text);
            }
            else
            {
                Flush();
                result.Add(piece);
            }
        }

        Flush();
        return result;

        void Flush()
        {
            // Whitespace both sides end with is unchanged text after them; anything else stays in its change, so the unchanged and
            // removed pieces always rebuild the old text, and the unchanged and added ones the new.
            var oldText = removed.ToString();
            var newText = added.ToString();
            var common = 0;
            while (common < oldText.Length && common < newText.Length && char.IsWhiteSpace(oldText[^(common + 1)]) && oldText[^(common + 1)] == newText[^(common + 1)])
            {
                common++;
            }

            if (oldText.Length > common) result.Add(new DiffPiece(DiffKind.Removed, oldText[..^common]));
            if (newText.Length > common) result.Add(new DiffPiece(DiffKind.Added, newText[..^common]));
            if (common > 0) result.Add(new DiffPiece(DiffKind.Same, oldText[^common..]));
            removed.Clear();
            added.Clear();
        }
    }

    private static List<string> Tokens(string text) => Token().Matches(text.Replace("\r\n", "\n")).Select(m => m.Value).ToList();

    [GeneratedRegex(@"\s+|[^\s]+")]
    private static partial Regex Token();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
