using System.Text;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>The hash and length of one normalized source span. The span itself is never stored.</summary>
public sealed class RedactionSpan
{
    public string Sha256 { get; set; } = string.Empty;
    public int Length { get; set; }

    public static RedactionSpan Of(string normalizedSpan) => new() { Sha256 = SpanText.Sha256(normalizedSpan), Length = normalizedSpan.Length };
}

/// <summary>One row of <c>Data/degrees-redactions.yaml</c>. It holds only our own words: a replacement text and a reason that names no names.</summary>
public sealed class RedactionRow
{
    public const string Replace = "replace";
    public const string Cut = "cut";
    public const string DropEntry = "drop-entry";

    /// <summary>The book (pc or mc) the row belongs to. Empty means every book; committed rows should name theirs.</summary>
    public string? Book { get; set; }

    /// <summary>The id of the entry the row applies to. A row for an entry whose own name is reserved gives <see cref="EntryHash"/> instead, so the file never holds the name.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>The lowercase hex SHA-256 of the entry's id, in place of <see cref="Entry"/>. Exactly one of the two is given.</summary>
    public string? EntryHash { get; set; }

    public string Field { get; set; } = string.Empty;
    public RedactionSpan Span { get; set; } = new();
    public string Action { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// For a row that takes a setting's name of more than one word out of a sentence: the phrase hashes of that name (the lowercase words, single spaces, as <see cref="Denylist.PhraseHash"/> hashes them), so the file never
    /// holds the name. They go on the phrase list with the names that rows rename or drop.
    /// </summary>
    public List<string> Phrases { get; set; } = new();

    /// <summary>True when the row names the entry with this id, by id or by the hash of it.</summary>
    public bool Names(string id) => string.IsNullOrEmpty(EntryHash) ? Entry == id : EntryHash == SpanText.Sha256(id);

    /// <summary>The row's entry as a key for duplicate checks: the id, or the hash.</summary>
    public string EntryKey => string.IsNullOrEmpty(EntryHash) ? Entry : "#" + EntryHash;
}

/// <summary>
/// The committed redaction table. It ships empty; each chapter run adds its rows. Loading validates every row, so a malformed table fails
/// before any entry is read. Errors name a row by number and never print a field of it.
/// </summary>
public sealed partial class RedactionTable
{
    public int Normalization { get; set; } = SpanText.Version;
    public List<RedactionRow> Rows { get; set; } = new();

    public static string[] Actions { get; } = { RedactionRow.Replace, RedactionRow.Cut, RedactionRow.DropEntry };

    public static RedactionTable Parse(string yaml)
    {
        RedactionTable table;
        try
        {
            table = BooksConfig.Deserializer.Deserialize<RedactionTable>(yaml) ?? new RedactionTable();
        }
        catch (Exception)
        {
            throw new DegreesException("degrees-redactions.yaml is not valid YAML of the expected shape.");
        }

        table.Validate();
        return table;
    }

    public static RedactionTable Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-redactions.yaml"));

    public void Validate()
    {
        if (Normalization != SpanText.Version)
        {
            throw new DegreesException($"The redaction table was written for text normalization {Normalization}; this tool is at {SpanText.Version}. Re-hash every row.");
        }

        var seen = new HashSet<(string, string, string, int)>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var n = i + 1;
            if ((string.IsNullOrWhiteSpace(row.Entry) == string.IsNullOrWhiteSpace(row.EntryHash)) || string.IsNullOrWhiteSpace(row.Field))
            {
                throw new DegreesException($"Redaction row {n}: field and exactly one of entry and entryHash are required.");
            }

            if (!string.IsNullOrWhiteSpace(row.EntryHash) && !Sha256Hex().IsMatch(row.EntryHash))
            {
                throw new DegreesException($"Redaction row {n}: entryHash needs a lowercase 64-digit sha256.");
            }

            if (row.Book != null && !DegreesOptions.Books.Contains(row.Book))
            {
                throw new DegreesException($"Redaction row {n}: book must be one of {string.Join(", ", DegreesOptions.Books)}.");
            }

            if (!Sha256Hex().IsMatch(row.Span?.Sha256 ?? string.Empty) || row.Span!.Length <= 0)
            {
                throw new DegreesException($"Redaction row {n}: span needs a lowercase 64-digit sha256 and a positive length.");
            }

            if (!Actions.Contains(row.Action))
            {
                throw new DegreesException($"Redaction row {n}: action must be one of {string.Join(", ", Actions)}.");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                throw new DegreesException($"Redaction row {n}: a reason is required.");
            }

            if (row.Action == RedactionRow.Replace)
            {
                if (string.IsNullOrWhiteSpace(row.Text) || row.Text != SpanText.Normalize(row.Text) || row.Text.Contains('\n'))
                {
                    throw new DegreesException($"Redaction row {n}: a replacement needs non-empty text that is normalized and on one line.");
                }
            }
            else if (row.Text != null)
            {
                throw new DegreesException($"Redaction row {n}: only a replace row has text.");
            }

            if (!seen.Add((row.EntryKey, row.Field, row.Span.Sha256, row.Span.Length)))
            {
                throw new DegreesException($"Redaction row {n}: another row already covers the same span.");
            }
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}

/// <summary>An entry as the redaction stage sees it: an id and its text fields.</summary>
public sealed class RedactionEntry
{
    public RedactionEntry(string id, IDictionary<string, string> fields)
    {
        Id = id;
        Fields = new Dictionary<string, string>(fields, StringComparer.Ordinal);
    }

    public string Id { get; }
    public Dictionary<string, string> Fields { get; }
}

/// <summary>Counts only. Never a span, a replacement or a field value.</summary>
public sealed record RedactionReport(int Rows, int Replaced, int Cut, int EntriesDropped, int EntriesKept)
{
    /// <summary>A run that has any unmatched row throws, so a report always means zero.</summary>
    public int Unmatched => 0;

    public override string ToString() =>
        $"redaction: {Rows} row(s) applied ({Replaced} replaced, {Cut} cut, {EntriesDropped} entries dropped); {EntriesKept} entries kept, {Unmatched} unmatched rows";
}

/// <summary>
/// Stage 5. Every field is normalized, split into sentences (and the semicolon clauses of a sentence), and each piece is hashed. A row
/// is applied to the piece with its hash and length. A row that matches nothing, or an entry or field that is missing, fails the whole run
/// before any text changes: a new printing that rewords a line is caught here. A row may match several identical pieces and then applies
/// to all of them. Two rows may not overlap.
/// </summary>
public static class RedactionStage
{
    private sealed record Edit(TextSpan Span, string Replacement, int Row, string Original, bool Cut);

    public static RedactionReport Run(List<RedactionEntry> entries, RedactionTable table)
    {
        table.Validate();
        var byId = new Dictionary<string, RedactionEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!byId.TryAdd(e.Id, e))
            {
                throw new DegreesException("Two entries share an id; redaction rows would be ambiguous.");
            }
        }

        // Everything below works on normalized copies. The entries are written to only after every check has passed.
        var texts = new Dictionary<(string Id, string Field), string>();
        foreach (var e in entries)
        {
            foreach (var (field, value) in e.Fields)
            {
                texts[(e.Id, field)] = SpanText.Normalize(value);
            }
        }

        var edits = new Dictionary<(string Id, string Field), List<Edit>>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var unmatched = new List<int>();
        var replaced = 0;
        var cut = 0;
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var id = byId.Keys.FirstOrDefault(row.Names);
            if (id == null || !texts.TryGetValue((id, row.Field), out var text))
            {
                unmatched.Add(i + 1);
                continue;
            }

            var hits = Pieces(text).Where(p => p.Length == row.Span.Length && SpanText.Sha256(text.Substring(p.Start, p.Length)) == row.Span.Sha256).ToList();
            if (hits.Count == 0)
            {
                unmatched.Add(i + 1);
                continue;
            }

            if (row.Action == RedactionRow.DropEntry)
            {
                dropped.Add(id);
                continue;
            }

            if (!edits.TryGetValue((id, row.Field), out var list))
            {
                edits[(id, row.Field)] = list = new List<Edit>();
            }

            var isCut = row.Action == RedactionRow.Cut;
            list.AddRange(hits.Select(h => new Edit(h, isCut ? string.Empty : row.Text!, i + 1, text.Substring(h.Start, h.Length), isCut)));
            if (isCut)
            {
                cut += hits.Count;
            }
            else
            {
                replaced += hits.Count;
            }
        }

        if (unmatched.Count > 0)
        {
            throw new DegreesException($"redaction: {unmatched.Count} row(s) matched no source span (rows {string.Join(", ", unmatched)}); the printing may differ. Nothing was changed.");
        }

        foreach (var (_, list) in edits)
        {
            var ordered = list.OrderBy(e => e.Span.Start).ToList();
            for (var k = 1; k < ordered.Count; k++)
            {
                if (ordered[k].Span.Start < ordered[k - 1].Span.End)
                {
                    throw new DegreesException($"redaction: rows {ordered[k - 1].Row} and {ordered[k].Row} overlap on one span. Nothing was changed.");
                }
            }
        }

        foreach (var ((id, field), list) in edits)
        {
            var text = texts[(id, field)];
            var sb = new StringBuilder(text);
            foreach (var edit in list.Concat(JoinFixes(text, list)).OrderByDescending(e => e.Span.Start))
            {
                sb.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.Replacement);
            }

            texts[(id, field)] = Tidy(sb.ToString());
        }

        foreach (var e in entries)
        {
            foreach (var field in e.Fields.Keys.ToList())
            {
                e.Fields[field] = texts[(e.Id, field)];
            }
        }

        entries.RemoveAll(e => dropped.Contains(e.Id));
        return new RedactionReport(table.Rows.Count, replaced, cut, dropped.Count, entries.Count);
    }

    /// <summary>
    /// What a cut clause leaves behind. If it was the last clause of its sentence, the clause before it takes its closing mark ("Move; shout." cut
    /// "shout." gives "Move."). If it was the first, the next clause starts the sentence and gets a capital. Neither is done where another row's edit is.
    /// </summary>
    private static List<Edit> JoinFixes(string text, List<Edit> edits)
    {
        bool Touched(int index) => edits.Any(o => index >= o.Span.Start && index < o.Span.End);
        var fixes = new List<Edit>();
        foreach (var e in edits.Where(e => e.Cut))
        {
            var start = e.Span.Start;
            if (e.Original.EndsWith('.') || e.Original.EndsWith('!') || e.Original.EndsWith('?'))
            {
                if (start >= 2 && text[start - 1] == ' ' && text[start - 2] == ';' && !Touched(start - 1) && !Touched(start - 2))
                {
                    fixes.Add(new Edit(new TextSpan(start - 2, 2), e.Original[^1].ToString(), 0, string.Empty, false));
                }
            }
            else if (e.Original.EndsWith(';') && (start == 0 || text[start - 1] == '\n' || (start >= 2 && text[start - 1] == ' ' && ".!?".Contains(text[start - 2]))))
            {
                var next = e.Span.End;
                while (next < text.Length && text[next] == ' ')
                {
                    next++;
                }

                if (next < text.Length && char.IsLower(text[next]) && !Touched(next) && edits.All(o => o.Span.Start != next))
                {
                    fixes.Add(new Edit(new TextSpan(next, 1), char.ToUpperInvariant(text[next]).ToString(), 0, string.Empty, false));
                }
            }
        }

        return fixes;
    }

    /// <summary>The rows of a table that name no entry among <paramref name="entries"/>. The denylist step fails on any: a row it cannot find would silently leave its removed words off the list.</summary>
    public static List<RedactionRow> RowsWithoutEntry(IEnumerable<RedactionEntry> entries, RedactionTable table)
    {
        var list = entries.ToList();
        return table.Rows.Where(row => !list.Any(e => row.Names(e.Id))).ToList();
    }

    /// <summary>The normalized text of every span a row matches, before any edit, with the row and the entry id. Used by the denylist step: redacted text is left-out text.</summary>
    public static List<(RedactionRow Row, string Id, string Text)> SourceSpans(IEnumerable<RedactionEntry> entries, RedactionTable table)
    {
        var list = entries.ToList();
        var result = new List<(RedactionRow, string, string)>();
        foreach (var row in table.Rows)
        {
            var entry = list.FirstOrDefault(e => row.Names(e.Id));
            if (entry == null || !entry.Fields.TryGetValue(row.Field, out var raw))
            {
                continue;
            }

            var text = SpanText.Normalize(raw);
            foreach (var piece in Pieces(text).Where(p => p.Length == row.Span.Length && SpanText.Sha256(text.Substring(p.Start, p.Length)) == row.Span.Sha256))
            {
                result.Add((row, entry.Id, text.Substring(piece.Start, piece.Length)));
            }
        }

        return result;
    }

    /// <summary>The sentences of a text and the clauses of those sentences that have more than one.</summary>
    public static IEnumerable<TextSpan> Pieces(string text)
    {
        foreach (var sentence in SpanText.Sentences(text))
        {
            yield return sentence;
            foreach (var clause in SpanText.Clauses(text, sentence))
            {
                yield return clause;
            }
        }
    }

    /// <summary>Closes the gap a cut leaves: spaces are collapsed again, empty paragraphs go, and a paragraph that now ends on a semicolon ends on a period.</summary>
    private static string Tidy(string text)
    {
        var paragraphs = text.Split('\n')
            .Select(p => Regex.Replace(p, " {2,}", " ").Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.EndsWith(';') ? p[..^1] + "." : p);
        return string.Join('\n', paragraphs);
    }
}
