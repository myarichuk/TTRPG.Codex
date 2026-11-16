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

    public string Entry { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public RedactionSpan Span { get; set; } = new();
    public string Action { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string Reason { get; set; } = string.Empty;
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
            throw new DegreesException("The redaction table is not valid YAML of the expected shape.");
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
            if (string.IsNullOrWhiteSpace(row.Entry) || string.IsNullOrWhiteSpace(row.Field))
            {
                throw new DegreesException($"Redaction row {n}: entry and field are required.");
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

            if (!seen.Add((row.Entry, row.Field, row.Span.Sha256, row.Span.Length)))
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
    public override string ToString() =>
        $"redaction: {Rows} row(s) applied ({Replaced} replaced, {Cut} cut, {EntriesDropped} entries dropped); {EntriesKept} entries kept, 0 unmatched rows";
}

/// <summary>
/// Stage 5. Every field is normalized, split into sentences (and the semicolon clauses of a sentence), and each piece is hashed. A row
/// is applied to the piece with its hash and length. A row that matches nothing, or an entry or field that is missing, fails the whole run
/// before any text changes: a new printing that rewords a line is caught here. A row may match several identical pieces and then applies
/// to all of them. Two rows may not overlap.
/// </summary>
public static class RedactionStage
{
    private readonly record struct Edit(TextSpan Span, string Replacement);

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

            foreach (var key in e.Fields.Keys.ToList())
            {
                e.Fields[key] = SpanText.Normalize(e.Fields[key]);
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
            if (!byId.TryGetValue(row.Entry, out var entry) || !entry.Fields.TryGetValue(row.Field, out var text))
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
                dropped.Add(row.Entry);
                continue;
            }

            if (!edits.TryGetValue((row.Entry, row.Field), out var list))
            {
                edits[(row.Entry, row.Field)] = list = new List<Edit>();
            }

            list.AddRange(hits.Select(h => new Edit(h, row.Action == RedactionRow.Replace ? row.Text! : string.Empty)));
            if (row.Action == RedactionRow.Replace)
            {
                replaced += hits.Count;
            }
            else
            {
                cut += hits.Count;
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
                    throw new DegreesException("redaction: two rows overlap on one span. Nothing was changed.");
                }
            }
        }

        foreach (var ((id, field), list) in edits)
        {
            var text = byId[id].Fields[field];
            var sb = new StringBuilder(text);
            foreach (var edit in list.OrderByDescending(e => e.Span.Start))
            {
                sb.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.Replacement);
            }

            byId[id].Fields[field] = Tidy(sb.ToString());
        }

        entries.RemoveAll(e => dropped.Contains(e.Id));
        return new RedactionReport(table.Rows.Count, replaced, cut, dropped.Count, entries.Count);
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
