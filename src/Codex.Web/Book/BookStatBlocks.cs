using System.Globalization;
using System.Text;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Book;

/// <summary>
/// The numbers of the campaign's own homebrew, written by code from the system's description of the entry's type (docs/BOOK_EXPORT.md 46), never
/// by a model: a creature (a type the system marks <c>actorBlueprint</c>) as Homebrewery's stat block, anything else as a table of its fields.
/// Nothing here knows a game: which fields there are, what they are called, which are the scores and how a score's modifier is worked out all come
/// from <c>system.yaml</c>, so a homebrew system gets its own stat blocks. A type the system does not describe gets no block.
/// <para>Each block carries an id of its own (<c>#sb-KEY</c>, <c>#pt-KEY</c>, KEY the entry's slot key), which is what makes an AI edit keep it
/// back as a piece the program owns (<see cref="Codex.Core.Book.BookDraftEdits"/>): the numbers are never sent to a model. Every value passes
/// through the draft writer's text path (mentions as the edition names them, secret blocks as the edition may read them) and is then flattened so it
/// cannot close the block, start a table row or open another block.</para>
/// </summary>
public static class BookStatBlocks
{
    public const string Creature = "monster";
    public const string Table = "properties";

    /// <summary>The block for <paramref name="entry"/>, or null when the system does not describe its type or nothing in it has a value.</summary>
    public static string? For(HomebrewEntryDocument entry, SystemDefinition? system, string language, string key, Func<string, string, string?> mention, bool dm)
    {
        if (system?.Localize(language) is not { } shown) return null;
        var type = shown.EntityTypes.FirstOrDefault(t => string.Equals(t.Type, entry.Type, StringComparison.OrdinalIgnoreCase));
        if (type == null) return null;
        var words = Codex.Localization.Localizer.For(language);
        string? Value(FieldDefinition field)
        {
            if (!entry.Properties.TryGetValue(field.Key, out var raw) || raw == null) return null;
            var text = raw switch
            {
                bool b => b ? words["Library.Detail.Yes"] : words["Library.Detail.No"],
                string s => field.Display(s),
                IConvertible c => Convert.ToString(c, CultureInfo.InvariantCulture),
                IEnumerable<object> list when list.All(o => o is IConvertible) => string.Join(", ", list.Select(o => field.Display(Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""))),
                _ => null
            };
            return string.IsNullOrWhiteSpace(text) ? null : Inline(text, mention, dm);
        }

        var scores = type.ActorBlueprint ? Scores(type, shown, entry) : [];
        var scoreKeys = scores.Select(s => s.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var summary = type.ActorBlueprint ? type.SummaryFields.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        // Plain fields, in the order the system gives them, grouped by the category it puts them in. Long text is the entry's own prose, a
        // reference is an id; neither is a number to print.
        var plain = type.Fields
            .Where(f => f.Type is not (FieldType.Collection or FieldType.LongText or FieldType.EntityReference or FieldType.ImagePicker))
            .Where(f => !scoreKeys.Contains(f.Key) && !summary.Contains(f.Key))
            .Select(f => (Field: f, Value: Value(f)))
            .Where(p => p.Value != null)
            .ToList();
        var rows = type.Fields.Where(f => f.Type == FieldType.Collection)
            .Select(f => (f.Label, Rows: Rows(entry, f.Key, mention, dm)))
            .Where(c => c.Rows.Count > 0)
            .ToList();

        var sb = new StringBuilder();
        if (type.ActorBlueprint)
        {
            sb.Append("{{").Append(Creature).Append(",frame,#sb-").Append(key).Append('\n');
            sb.Append("## ").Append(BookCompiler.Escape(entry.Name)).Append('\n');
            var line = type.SummaryFields.Select(k => type.Fields.FirstOrDefault(f => string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase)))
                .OfType<FieldDefinition>().Select(Value).OfType<string>().ToList();
            if (line.Count > 0) sb.Append('*').Append(string.Join(", ", line)).Append("*\n");
            foreach (var group in plain.GroupBy(p => p.Field.Category ?? string.Empty))
            {
                sb.Append("___\n");
                foreach (var (field, value) in group) sb.Append("**").Append(BookCompiler.Escape(field.Label)).Append("** ").Append(value).Append("  \n");
            }

            if (scores.Count > 0)
            {
                sb.Append("___\n|").Append(string.Join("|", scores.Select(s => BookCompiler.Escape(s.Label)))).Append("|\n|")
                    .Append(string.Join("|", scores.Select(_ => ":---:"))).Append("|\n|")
                    .Append(string.Join("|", scores.Select(s => s.Text))).Append("|\n");
            }
        }
        else
        {
            if (plain.Count == 0 && rows.Count == 0) return null;
            sb.Append("{{").Append(Table).Append(",#pt-").Append(key).Append('\n');
            if (plain.Count > 0)
            {
                sb.Append("| ").Append(BookCompiler.Escape(type.Label)).Append(" | |\n|:---|:---|\n");
                foreach (var (field, value) in plain) sb.Append("| ").Append(BookCompiler.Escape(field.Label)).Append(" | ").Append(value).Append(" |\n");
            }
        }

        foreach (var (label, list) in rows)
        {
            sb.Append(type.ActorBlueprint ? "### " : "\n##### ").Append(BookCompiler.Escape(label)).Append('\n');
            foreach (var (name, text) in list)
            {
                sb.Append("***").Append(name).Append(".*** ").Append(text).Append("\n\n");
            }
        }

        return sb.ToString().TrimEnd('\n') + "\n}}";
    }

    // The scores' row: each attribute the type maps to a field of its own, under the system's abbreviation, with the system's modifier.
    private static List<(string Field, string Label, string Text)> Scores(ContentTypeDefinition type, SystemDefinition system, HomebrewEntryDocument entry)
    {
        var scores = new List<(string, string, string)>();
        foreach (var attribute in system.Attributes)
        {
            var field = type.AttributeProperties.FirstOrDefault(p => string.Equals(p.Value, attribute.Key, StringComparison.OrdinalIgnoreCase)).Key;
            if (field == null || !entry.Properties.TryGetValue(field, out var raw) || !int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
            {
                continue;
            }

            var modifier = attribute.ModifierFor(score) is { } m ? " (" + (m >= 0 ? "+" + m : "−" + -m) + ")" : string.Empty;
            scores.Add((field, attribute.Abbreviation ?? attribute.Label, score.ToString(CultureInfo.InvariantCulture) + modifier));
        }

        return scores;
    }

    private static List<(string Name, string Text)> Rows(HomebrewEntryDocument entry, string key, Func<string, string, string?> mention, bool dm)
    {
        if (!entry.Properties.TryGetValue(key, out var raw) || raw is not IEnumerable<object> items) return [];
        var rows = new List<(string, string)>();
        foreach (var item in items.OfType<IDictionary<string, object>>())
        {
            var name = item.TryGetValue("name", out var n) && n is IConvertible ? Convert.ToString(n, CultureInfo.InvariantCulture) : null;
            var text = item.TryGetValue("description", out var d) && d is IConvertible ? Convert.ToString(d, CultureInfo.InvariantCulture) : null;
            var shownText = string.IsNullOrWhiteSpace(text) ? string.Empty : Inline(text, mention, dm);
            if (string.IsNullOrWhiteSpace(name) && shownText.Length == 0) continue;
            rows.Add((string.IsNullOrWhiteSpace(name) ? string.Empty : Inline(name, mention, dm), shownText));
        }

        return rows;
    }

    /// <summary>
    /// A value as one line of the block: through the draft writer's text path (which escapes what it writes of a mention, and HTML), then on one
    /// line, with every brace and bar escaped, so nothing in it can end the block, open another, or make a table cell of its own. Emphasis stays.
    /// </summary>
    public static string Inline(string text, Func<string, string, string?> mention, bool dm)
    {
        var converted = BookDraftWriter.Text(text, mention, dm);
        var line = string.Join(' ', converted.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var sb = new StringBuilder(line.Length + 8);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            // A mark the writer already escaped stays as it is.
            if (c == '\\' && i + 1 < line.Length)
            {
                sb.Append(c).Append(line[++i]);
                continue;
            }

            if (c is '{' or '}' or '|') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }
}
