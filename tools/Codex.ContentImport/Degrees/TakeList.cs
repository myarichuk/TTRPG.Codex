using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// One row of <c>Data/degrees-chapters.yaml</c>: the bookmark paths taken, how they are parsed, and what they become. A chapter may have several
/// rows (the list of conditions is parsed as entries, its two rule sections as rules). Nothing outside a taken path is read into an entry,
/// and dropped sections are never named: they are absent.
/// </summary>
public sealed class TakeRow
{
    public string Chapter { get; set; } = string.Empty;
    public string Book { get; set; } = string.Empty;
    public string Parser { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;

    /// <summary>The pack folder for the entries; by default the target type's name in the plural.</summary>
    public string? Folder { get; set; }

    /// <summary>The value of the entry's <c>category</c> field, in our own words, when the target type has one.</summary>
    public string? Category { get; set; }

    public bool Approved { get; set; }

    /// <summary>The most columns a page of this row may have. Two unless stated.</summary>
    public int Columns { get; set; } = 2;

    /// <summary>Bookmark paths, top level first. Each path is a section or an entry.</summary>
    public List<List<string>> Paths { get; set; } = new();

    public string FolderName => Folder ?? (Target.EndsWith('y') ? Target[..^1] + "ies" : Target + "s");
}

public sealed partial class TakeList
{
    public static readonly string[] Parsers = { "entries", "statblocks", "table", "glossary", "rules" };

    /// <summary>The parsers that exist. Any other parser in the list fails the run, so a row cannot silently take nothing.</summary>
    public static readonly string[] BuiltParsers = { "entries", "rules" };

    public List<TakeRow> Rows { get; set; } = new();

    private static readonly IDeserializer Strict = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public static TakeList Parse(string yaml)
    {
        TakeList list;
        try
        {
            list = Strict.Deserialize<TakeList>(yaml) ?? new TakeList();
        }
        catch (Exception)
        {
            throw new DegreesException("degrees-chapters.yaml is not valid YAML of the expected shape.");
        }

        list.Validate();
        return list;
    }

    public static TakeList Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-chapters.yaml"));

    public IEnumerable<TakeRow> For(string book) => Rows.Where(r => r.Book == book);

    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            var n = i + 1;
            if (!Key().IsMatch(r.Chapter) || !Key().IsMatch(r.Target) || !(r.Folder == null || Key().IsMatch(r.Folder)))
            {
                throw new DegreesException($"Take-list row {n}: chapter, target and folder must be lowercase keys.");
            }

            if (!DegreesOptions.Books.Contains(r.Book))
            {
                throw new DegreesException($"Take-list row {n}: book must be one of {string.Join(", ", DegreesOptions.Books)}.");
            }

            if (!Parsers.Contains(r.Parser))
            {
                throw new DegreesException($"Take-list row {n}: parser must be one of {string.Join(", ", Parsers)}.");
            }

            if (r.Columns is < 1 or > 4)
            {
                throw new DegreesException($"Take-list row {n}: columns must be 1 to 4.");
            }

            if (r.Paths.Count == 0 || r.Paths.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)))
            {
                throw new DegreesException($"Take-list row {n}: it needs at least one bookmark path, and no empty title.");
            }

            foreach (var path in r.Paths)
            {
                if (!seen.Add(r.Book + "\n" + string.Join("\n", path.Select(t => t.Trim().ToLowerInvariant()))))
                {
                    throw new DegreesException($"Take-list row {n}: a bookmark path is taken twice.");
                }
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex Key();
}
