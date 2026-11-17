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

    /// <summary>
    /// Bookmark paths whose OWN text is taken: what sits between the section's title and its first child section (which this row, or another, takes on its own path).
    /// Used by the <c>rules</c> parser, one entry per path, named by <see cref="OwnName"/>. It is never dropped silently: a section's intro is either here or fails nothing, so
    /// leaving it out is a decision the take-list shows.
    /// </summary>
    public List<List<string>> Own { get; set; } = new();

    /// <summary>The name, in our own words, of the entry made from an <see cref="Own"/> path (its title is not the entry's name). Defaults to the path's last title.</summary>
    public string? OwnName { get; set; }

    /// <summary>
    /// For an <c>entries</c> row: why the bookmark children of a taken section (minus the sidebar bookmarks) and the entry-header-font blocks of its range may disagree, in our
    /// own words. Without it, a disagreement fails the run (see <see cref="ImportPipeline.CheckEntryCounts"/>). Empty when they agree.
    /// </summary>
    public string? CountsDiffer { get; set; }

    /// <summary>The bookmark path of a taken range, whichever list it came from.</summary>
    public List<string> PathOf(TakenRange range) => range.Own ? Own[range.PathNumber - Paths.Count - 1] : Paths[range.PathNumber - 1];

    public string FolderName => Folder ?? (Target.EndsWith('y') ? Target[..^1] + "ies" : Target + "s");
}

/// <summary>The planned bookmark paths of one book (see <see cref="TakeList.Planned"/>).</summary>
public sealed class PlannedRow
{
    public string Book { get; set; } = string.Empty;
    public List<List<string>> Paths { get; set; } = new();
}

public sealed partial class TakeList
{
    public static readonly string[] Parsers = { "entries", "statblocks", "table", "glossary", "rules" };

    /// <summary>The parsers that exist. Any other parser in the list fails the run, so a row cannot silently take nothing.</summary>
    public static readonly string[] BuiltParsers = { "entries", "rules" };

    public List<TakeRow> Rows { get; set; } = new();

    /// <summary>
    /// Sections of a book that a later chapter will take. A planned path is not read, parsed or approved: it only tells the denylist generator that the section is not
    /// left out, so its words are not hashed as if they were setting material. Anything on neither list is left out.
    /// </summary>
    public List<PlannedRow> Planned { get; set; } = new();

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

    private sealed class SystemTypes
    {
        public List<SystemType> EntityTypes { get; set; } = new();
    }

    private sealed class SystemType
    {
        public string Type { get; set; } = string.Empty;
        public string? Folder { get; set; }
    }

    /// <summary>
    /// Checks the rows' targets against the system's own <c>entityTypes</c>: each target must be a declared type, and the pack folder must be that type's
    /// <c>folder</c>, because the loader types a folder by the system's declaration and treats any other name as an undeclared type that shows nowhere.
    /// </summary>
    public void CheckAgainstSystem(string systemYaml)
    {
        SystemTypes system;
        try
        {
            system = BooksConfig.Deserializer.Deserialize<SystemTypes>(systemYaml) ?? new SystemTypes();
        }
        catch (Exception)
        {
            throw new DegreesException("system.yaml could not be read for its entity types.");
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            var type = system.EntityTypes.FirstOrDefault(t => t.Type == Rows[i].Target)
                ?? throw new DegreesException($"Take-list row {i + 1}: the system declares no entity type for the target.");
            if ((type.Folder ?? type.Type) != Rows[i].FolderName)
            {
                throw new DegreesException($"Take-list row {i + 1}: the pack folder is not the target type's folder in the system.");
            }
        }
    }

    public IEnumerable<TakeRow> For(string book) => Rows.Where(r => r.Book == book);

    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var other = Rows.GroupBy(r => r.FolderName, StringComparer.Ordinal).FirstOrDefault(g => g.Select(r => r.Target).Distinct().Count() > 1);
        if (other != null)
        {
            throw new DegreesException("Take-list: two different targets share one pack folder, so their entries would merge into one file.");
        }

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

            if (r.Own.Count > 0 && r.Parser != "rules")
            {
                throw new DegreesException($"Take-list row {n}: own-text paths belong to the rules parser.");
            }

            if (r.Own.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)) || (r.Own.Count > 0 && string.IsNullOrWhiteSpace(r.OwnName)))
            {
                throw new DegreesException($"Take-list row {n}: an own-text path needs titles and the row needs an ownName.");
            }

            foreach (var path in r.Own)
            {
                if (!seen.Add("own\n" + r.Book + "\n" + string.Join("\n", path.Select(t => t.Trim().ToLowerInvariant()))))
                {
                    throw new DegreesException($"Take-list row {n}: an own-text path is taken twice.");
                }
            }
        }

        foreach (var planned in Planned)
        {
            if (!DegreesOptions.Books.Contains(planned.Book) || planned.Paths.Count == 0 || planned.Paths.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)))
            {
                throw new DegreesException("Take-list: a planned entry needs a book and bookmark paths with no empty title.");
            }

            if (planned.Paths.Any(p => Rows.Any(r => r.Book == planned.Book && r.Paths.Any(t => t.SequenceEqual(p, StringComparer.OrdinalIgnoreCase)))))
            {
                throw new DegreesException("Take-list: a planned path is also taken.");
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex Key();
}
