using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>One field of an expanded template: where it is in <see cref="TemplateExpansion.Text"/> and what it holds.</summary>
/// <param name="Mirror">A later use of a name already used: it repeats what is typed in the first and is never stopped at.</param>
public sealed record TemplateField(string Name, int Start, int Length, bool Mirror);

/// <summary>A template's text with every field filled with its default, the fields in order, and where the caret goes when
/// the last field is done (<c>$END$</c>, or the end of the text).</summary>
public sealed record TemplateExpansion(string Text, IReadOnlyList<TemplateField> Fields, int End);

/// <summary>
/// The DM's text templates, in the manner of an IDE's live templates. A template is ordinary text (Markdown, links, mentions,
/// secret blocks) with fields:
/// <list type="bullet">
/// <item><c>$name$</c>: a field to fill in. The same name used again repeats what is typed in the first one.</item>
/// <item><c>$name:default$</c>: a field that starts out holding <c>default</c>.</item>
/// <item><c>$END$</c>: where the caret goes after the last field.</item>
/// <item><c>$$</c>: a dollar sign.</item>
/// </list>
/// A name is letters, digits and underscores, in any script. A <c>$</c> that starts none of these (an item mention typed as
/// <c>$Longsword</c>) is left as it is. Typing a template's abbreviation and Tab in the editor puts the template in and steps
/// through its fields with Tab and Shift+Tab (app-startup.js parses templates the same way); a new page can start from one; and
/// <see cref="AddMissingSections"/> brings a page written earlier up to its template's headings.
/// </summary>
public static class LiveTemplate
{
    public const int MaxAbbreviation = 24;
    public const int MaxName = 80;
    public const int MaxBody = 20000;

    private const string EndName = "END";

    // Keep in step with TemplateToken in app-startup.js.
    private static readonly Regex Token = new(@"\$\$|\$(?<name>[\p{L}\p{N}_]+)(?::(?<def>[^$\r\n]*))?\$", RegexOptions.Compiled);

    private static readonly Regex AbbreviationShape = new(@"^[\p{L}\p{N}_-]+$", RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^(?<hashes>#{1,6})[ \t]+(?<title>.*?)[ \t]*#*[ \t]*$", RegexOptions.Compiled);

    // A list item left with nothing in it once its field is emptied.
    private static readonly Regex EmptyItem = new(@"^\s*(?:[-*+]|\d+\.)$", RegexOptions.Compiled);

    // A secret block left with nothing in it once its fields are emptied: an opening line, blank lines, a closing line.
    private static readonly Regex EmptyBlock = new(@"^[ \t]*:::[ \t]*[^\s:][^\n]*\n(?:[ \t]*\n)*[ \t]*:::[ \t]*$\n?", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Whether <paramref name="abbreviation"/> can be typed before Tab: letters, digits, <c>_</c> and <c>-</c>. Empty is
    /// allowed too: such a template is only offered by the <c>/</c> menu and when a page is started.</summary>
    public static bool IsValidAbbreviation(string? abbreviation) =>
        string.IsNullOrEmpty(abbreviation) || (abbreviation.Length <= MaxAbbreviation && AbbreviationShape.IsMatch(abbreviation));

    /// <summary>The template with every field holding its default (a name's default is the first one given for it).</summary>
    public static TemplateExpansion Expand(string? body)
    {
        body ??= string.Empty;
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Token.Matches(body))
        {
            if (m.Groups["name"].Success && m.Groups["def"].Success && !defaults.ContainsKey(m.Groups["name"].Value))
            {
                defaults[m.Groups["name"].Value] = m.Groups["def"].Value;
            }
        }

        var text = new StringBuilder();
        var fields = new List<TemplateField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? end = null;
        var at = 0;
        foreach (Match m in Token.Matches(body))
        {
            text.Append(body, at, m.Index - at);
            at = m.Index + m.Length;
            if (!m.Groups["name"].Success)
            {
                text.Append('$');
                continue;
            }

            var name = m.Groups["name"].Value;
            if (name == EndName)
            {
                end ??= text.Length;
                continue;
            }

            var value = defaults.GetValueOrDefault(name, string.Empty);
            fields.Add(new TemplateField(name, text.Length, value.Length, !seen.Add(name)));
            text.Append(value);
        }

        text.Append(body, at, body.Length - at);
        return new TemplateExpansion(text.ToString(), fields, end ?? text.Length);
    }

    /// <summary>The names of the fields a template steps through, in order, each once.</summary>
    public static IReadOnlyList<string> FieldNames(string? body) =>
        Expand(body).Fields.Where(f => !f.Mirror).Select(f => f.Name).ToList();

    /// <summary>The template as a sample of what it makes: each field shown as <c>‹name›</c>, or its default when it has one.
    /// For a preview, and for telling the AI what shape a page takes.</summary>
    public static string Sample(string? body)
    {
        var expansion = Expand(body);
        var text = new StringBuilder(expansion.Text);
        foreach (var field in expansion.Fields.Where(f => f.Length == 0).OrderByDescending(f => f.Start))
        {
            text.Insert(field.Start, $"‹{field.Name.Replace('_', ' ')}›");
        }

        return text.ToString();
    }

    /// <summary>The headings of a text, in order, as (level, title).</summary>
    public static IReadOnlyList<(int Level, string Title)> Headings(string? text) =>
        Lines(text).Select(l => Heading.Match(l)).Where(m => m.Success)
            .Select(m => (m.Groups["hashes"].Length, m.Groups["title"].Value.Trim()))
            .Where(h => h.Item2.Length > 0)
            .ToList();

    /// <summary>
    /// <paramref name="text"/> with each section of the template it has no heading for put in: the heading and what the template
    /// has under it, the fields holding their defaults (a field with none is left empty, and a secret block left empty by that is
    /// dropped). A missing section goes just before the next of the template's sections the text has, so the template's order
    /// holds where the text keeps it, or at the end. Headings match by title, ignoring case and level. Null when nothing is
    /// missing, or the template has no headings.
    /// </summary>
    public static string? AddMissingSections(string? text, string? templateBody)
    {
        text ??= string.Empty;
        var sections = Sections(Filled(templateBody));
        if (sections.Count == 0)
        {
            return null;
        }

        var have = new HashSet<string>(Headings(text).Select(h => Key(h.Title)), StringComparer.Ordinal);
        if (sections.All(s => have.Contains(Key(s.Title))))
        {
            return null;
        }

        var lines = Lines(text).ToList();
        // Where each heading of the text is, by title (the first, when one repeats).
        var lineOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            if (Heading.Match(lines[i]) is { Success: true } m && m.Groups["title"].Value.Trim() is { Length: > 0 } title)
            {
                lineOf.TryAdd(Key(title), i);
            }
        }

        var before = new SortedDictionary<int, List<string>>();
        var atEnd = new List<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            if (have.Contains(Key(sections[i].Title)))
            {
                continue;
            }

            var next = sections.Skip(i + 1).FirstOrDefault(s => have.Contains(Key(s.Title)));
            if (next == null)
            {
                atEnd.Add(sections[i].Text);
                continue;
            }

            var line = lineOf[Key(next.Title)];
            if (!before.TryGetValue(line, out var added)) before[line] = added = new List<string>();
            added.Add(sections[i].Text);
        }

        var output = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var section in before.GetValueOrDefault(i) ?? new List<string>())
            {
                AddParagraph(output, section);
            }

            output.Add(lines[i]);
        }

        foreach (var section in atEnd)
        {
            AddParagraph(output, section);
        }

        while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
        return string.Join('\n', output) + "\n";
    }

    // A section as a paragraph of its own: one blank line before it (none at the very start) and one after it.
    private static void AddParagraph(List<string> output, string section)
    {
        while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
        if (output.Count > 0) output.Add(string.Empty);
        output.AddRange(section.TrimEnd('\n').Split('\n'));
        output.Add(string.Empty);
    }

    // The template filled in for a page that already has text: defaults in, empty fields empty, list items and secret blocks left empty by that gone.
    private static string Filled(string? body)
    {
        var text = Expand(body).Text.Replace("\r\n", "\n");
        text = string.Join('\n', text.Split('\n').Select(l => l.TrimEnd()).Where(l => !EmptyItem.IsMatch(l)));
        text = EmptyBlock.Replace(text, string.Empty);
        return Regex.Replace(text, @"\n{3,}", "\n\n");
    }

    private sealed record Section(string Title, string Text);

    // The template cut at its headings: each heading with what follows it up to the next. Text before the first heading is
    // the page's opening and is never added to a page that already has text.
    private static List<Section> Sections(string text)
    {
        var sections = new List<Section>();
        string? title = null;
        var current = new StringBuilder();
        foreach (var line in Lines(text))
        {
            if (Heading.Match(line) is { Success: true } m && m.Groups["title"].Value.Trim() is { Length: > 0 } heading)
            {
                Flush();
                title = heading;
            }

            if (title != null) current.Append(line).Append('\n');
        }

        Flush();
        return sections;

        void Flush()
        {
            if (title != null) sections.Add(new Section(title, current.ToString().TrimEnd() + "\n"));
            current.Clear();
        }
    }

    private static string Key(string title) => title.Trim().TrimEnd(':').Trim().ToLowerInvariant();

    private static IEnumerable<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');
}
