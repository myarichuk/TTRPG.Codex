using System.Globalization;
using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;

namespace Codex.ContentImport;

/// <summary>How the grants file classifies one row (or one feat or background).</summary>
public enum GrantStatus
{
    /// <summary>The row gets at least one grant (a counter, a modifier, entries on a list, a choice).</summary>
    Grant,

    /// <summary>The effect sits on another row.</summary>
    Covered,

    /// <summary>A field or list the system already reads carries it (feat slots, the subclass field, a species value).</summary>
    Already,

    /// <summary>Nothing is written, for the reason the file gives: the player applies it by hand.</summary>
    Text
}

/// <summary>What the importer refused to write: every problem found, one per line.</summary>
public sealed class GrantTableException(IReadOnlyList<string> problems)
    : Exception("The SRD grants file does not fit the source:\n  " + string.Join("\n  ", problems.Take(60)) + (problems.Count > 60 ? $"\n  ... and {problems.Count - 60} more" : string.Empty))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>How many rows of each group the grants file puts in each <see cref="GrantStatus"/>.</summary>
public sealed class GrantCoverage
{
    /// <summary>The groups, in the order the importer reports them.</summary>
    public static readonly string[] Groups = { "classes", "subclasses", "traits", "lineages", "feats", "backgrounds" };

    private readonly Dictionary<string, Dictionary<GrantStatus, int>> _counts = Groups.ToDictionary(g => g, _ => Enum.GetValues<GrantStatus>().ToDictionary(s => s, _ => 0));

    public int Count(string group, GrantStatus status) => _counts[group][status];

    public int Rows(string group) => _counts[group].Values.Sum();

    public int Total(GrantStatus status) => _counts.Values.Sum(c => c[status]);

    internal void Add(string group, GrantStatus status) => _counts[group][status]++;

    public override string ToString() =>
        string.Join("; ", Groups.Select(g => $"{g} {Rows(g)}: " + string.Join(", ", Enum.GetValues<GrantStatus>().Select(s => $"{s} {Count(g, s)}"))));
}

/// <summary>
/// The reviewed mapping from SRD rows to feature grants (<c>Data/srd521-grants.yaml</c>). It classifies every row of every class, subclass,
/// species trait and lineage, and every feat and background, as a grant or as text with a reason; the importer merges the grants into the rows
/// it built and refuses to write anything when the file and the source disagree (see <see cref="Apply"/>).
/// </summary>
public sealed class GrantTable
{
    private static readonly string[] GrantKeys = { "uses", "spends", "modifiers", "adds", "spells", "freeSpells", "resist", "options", "choose", "gate" };
    private static readonly string[] StatusKeys = { "text", "already", "covered" };

    private readonly Dictionary<string, object> _root;

    private GrantTable(Dictionary<string, object> root) => _root = root;

    /// <summary>No mapping: the importer writes rows as it builds them.</summary>
    public static GrantTable None { get; } = new(new Dictionary<string, object>());

    public bool IsEmpty => _root.Count == 0;

    /// <summary>The reviewed file that ships inside the importer.</summary>
    public static GrantTable Embedded()
    {
        using var stream = typeof(GrantTable).Assembly.GetManifestResourceStream("srd521-grants.yaml")
            ?? throw new InvalidDataException("The importer carries no srd521-grants.yaml.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static GrantTable Parse(string yaml)
    {
        var raw = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        return new GrantTable(Normalize(raw) as Dictionary<string, object> ?? new Dictionary<string, object>());
    }

    // YAML scalars come back as strings: whole numbers and true/false become numbers and booleans, keys become strings.
    private static object? Normalize(object? raw) => raw switch
    {
        null => null,
        IDictionary<object, object> map => map.ToDictionary(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture)!, p => Normalize(p.Value)!),
        IEnumerable<object> list when raw is not string => list.Select(Normalize).ToList()!,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        string text when text is "true" or "false" => text == "true",
        _ => raw
    };

    // ---- What a spec is ----

    private sealed class Spec(string group, string label, string? name, IReadOnlyList<long>? levels, Dictionary<string, object> data)
    {
        public string Group { get; } = group;
        public string Label { get; } = label;
        public string? Name { get; } = name;
        public IReadOnlyList<long>? Levels { get; } = levels;
        public Dictionary<string, object> Data { get; } = data;
        public bool Used { get; set; }

        public GrantStatus Status =>
            Data.Keys.Any(k => GrantKeys.Contains(k)) ? GrantStatus.Grant
            : Data.ContainsKey("covered") ? GrantStatus.Covered
            : Data.ContainsKey("already") ? GrantStatus.Already
            : GrantStatus.Text;
    }

    private IEnumerable<Spec> Specs(string section, string entryId, string? sub, List<string> problems)
    {
        if (_root.GetValueOrDefault(section) is not Dictionary<string, object> entries || !entries.TryGetValue(entryId, out var raw)) yield break;
        if (sub != null)
        {
            if (raw is not Dictionary<string, object> parts || !parts.TryGetValue(sub, out raw)) yield break;
        }

        foreach (var item in raw as List<object> ?? new List<object>())
        {
            if (item is not Dictionary<string, object> map || map.GetValueOrDefault("row") is not string row)
            {
                problems.Add($"{section}/{entryId}: a row entry needs a \"row\" name.");
                continue;
            }

            var levels = map.GetValueOrDefault("level") switch
            {
                long one => new[] { one },
                List<object> many => many.OfType<long>().ToArray(),
                _ => null
            };
            var data = map.Where(p => p.Key is not ("row" or "level")).ToDictionary(p => p.Key, p => p.Value);
            yield return new Spec(section, $"{section}/{entryId}: {row}" + (levels is { Length: 1 } ? $" ({levels[0]})" : string.Empty), row, levels, data);
        }
    }

    private Spec? WholeSpec(string section, string entryId)
    {
        if (_root.GetValueOrDefault(section) is not Dictionary<string, object> entries || entries.GetValueOrDefault(entryId) is not Dictionary<string, object> data) return null;
        return new Spec(section, $"{section}/{entryId}", null, null, data);
    }

    // ---- Matching ----

    private static IEnumerable<Dictionary<string, object>> RowsOf(ContentEntry entry, string property) =>
        entry.Properties.GetValueOrDefault(property) is IEnumerable<object> rows ? rows.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();

    private static string NameOf(Dictionary<string, object> row) => row.GetValueOrDefault("name")?.ToString() ?? string.Empty;

    private static long? LevelOf(Dictionary<string, object> row) => row.GetValueOrDefault("level") is long l ? l : null;

    /// <summary>Matches the specs of one group against the rows of an entry. Problems are said, not thrown.</summary>
    private static List<(Spec Spec, Dictionary<string, object> Row)> Match(IReadOnlyList<Spec> specs, List<Dictionary<string, object>> rows, string where, List<string> problems)
    {
        var matched = new List<(Spec, Dictionary<string, object>)>();
        var claimed = new Dictionary<Dictionary<string, object>, Spec>();
        foreach (var spec in specs)
        {
            var found = rows.Where(r => string.Equals(NameOf(r), spec.Name, StringComparison.OrdinalIgnoreCase)
                && (spec.Levels == null || (LevelOf(r) is { } l && spec.Levels.Contains(l)))).ToList();
            if (found.Count == 0)
            {
                problems.Add($"{spec.Label} matches no row of {where}.");
                continue;
            }

            if (found.Count > 1 && spec.Status == GrantStatus.Grant)
            {
                problems.Add($"{spec.Label} matches {found.Count} rows of {where}; give its level.");
                continue;
            }

            spec.Used = true;
            foreach (var row in found)
            {
                if (claimed.TryGetValue(row, out var other))
                {
                    problems.Add($"{where}: the row \"{NameOf(row)}\"{(LevelOf(row) is { } at ? $" ({at})" : string.Empty)} is classified twice ({other.Label}; {spec.Label}).");
                    continue;
                }

                claimed[row] = spec;
                matched.Add((spec, row));
            }
        }

        foreach (var row in rows.Where(r => !claimed.ContainsKey(r)))
        {
            problems.Add($"{where}: the row \"{NameOf(row)}\"{(LevelOf(row) is { } at ? $" ({at})" : string.Empty)} is not in the grants file. Classify it, as a grant or as text.");
        }

        return matched;
    }

    // ---- Applying ----

    /// <summary>
    /// Checks the file against <paramref name="entries"/> and merges its grants into the rows. Nothing but the rows' grant keys changes; text,
    /// names, levels (but a species row's <c>gate</c>) and the order and number of rows stay as they were. Throws a <see cref="GrantTableException"/>
    /// naming every problem when a mapped row matches nothing, a row is classified twice or not at all, a spell, damage type, modifier target,
    /// moment or formula is unknown, a counter has no key or a key has two owners, a spender has no pool, or the grants fail
    /// <see cref="FeatureGrantCheck"/>. Classes, subclasses, species and feats the source does not have are not checked.
    /// </summary>
    public GrantCoverage Apply(IReadOnlyList<ContentEntry> entries, ClassColumns columns, SystemDefinition? system) => Run(entries, columns, system, merge: true);

    /// <summary>The same classification as <see cref="Apply"/> without changing anything (and without the checks that need the source's columns).</summary>
    public GrantCoverage Classify(IReadOnlyList<ContentEntry> entries, SystemDefinition? system) => Run(entries, null, system, merge: false);

    private GrantCoverage Run(IReadOnlyList<ContentEntry> entries, ClassColumns? columns, SystemDefinition? system, bool merge)
    {
        var coverage = new GrantCoverage();
        if (IsEmpty) return coverage;

        var problems = new List<string>();
        var pools = _root.GetValueOrDefault("pools") as Dictionary<string, object> ?? new Dictionary<string, object>();
        var vocabulary = FeatureGrantVocabulary.For(system);
        var spellIds = entries.Where(e => e.Type == "spell").Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var context = new Context(columns, system, vocabulary, spellIds, pools, problems, merge);

        foreach (var entry in entries.Where(e => e.Type == "class")) Rows(entry, "classes", "class", null, "features", coverage, context);
        foreach (var entry in entries.Where(e => e.Type == "subclass")) Rows(entry, "subclasses", "subclass", null, "features", coverage, context);
        foreach (var entry in entries.Where(e => e.Type == "species"))
        {
            Rows(entry, "traits", "species", "traits", "traits", coverage, context);
            Rows(entry, "lineages", "species", "lineages", "lineages", coverage, context);
        }

        foreach (var entry in entries.Where(e => e.Type == "feat")) Whole(entry, "feats", "feat", coverage, context);
        foreach (var entry in entries.Where(e => e.Type == "background")) Whole(entry, "backgrounds", "background", coverage, context);

        // A mapped entry the source does not have: only said for a type the source has entries of at all.
        foreach (var (section, type) in new[] { ("class", "class"), ("subclass", "subclass"), ("species", "species"), ("feat", "feat"), ("background", "background") })
        {
            var present = entries.Where(e => e.Type == type).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (present.Count == 0 || _root.GetValueOrDefault(section) is not Dictionary<string, object> mapped) continue;
            foreach (var id in mapped.Keys.Where(id => !present.Contains(id))) problems.Add($"{section}/{id} is in the grants file but not in the source.");
        }

        context.CheckKeys(entries);
        if (problems.Count > 0) throw new GrantTableException(problems);
        return coverage;
    }

    private void Rows(ContentEntry entry, string group, string section, string? sub, string property, GrantCoverage coverage, Context context)
    {
        var specs = Specs(section, entry.Id, sub, context.Problems).ToList();
        var rows = RowsOf(entry, property).ToList();
        if (specs.Count == 0 && rows.Count == 0) return;
        if (specs.Count == 0 && !(_root.GetValueOrDefault(section) is Dictionary<string, object> mapped && mapped.ContainsKey(entry.Id)))
        {
            // Nothing says anything about this entry: every row is unclassified.
            context.Problems.Add($"{section}/{entry.Id} is not in the grants file ({rows.Count} rows to classify).");
            return;
        }

        var where = $"{section}/{entry.Id}" + (sub != null ? $" {sub}" : string.Empty);
        foreach (var (spec, row) in Match(specs, rows, where, context.Problems))
        {
            coverage.Add(group, spec.Status);
            if (spec.Status == GrantStatus.Grant) context.Merge(entry, row, spec);
        }
    }

    private void Whole(ContentEntry entry, string group, string section, GrantCoverage coverage, Context context)
    {
        if (WholeSpec(section, entry.Id) is not { } spec)
        {
            context.Problems.Add($"{section}/{entry.Id} is not in the grants file.");
            return;
        }

        coverage.Add(group, spec.Status);
        if (spec.Status == GrantStatus.Grant) context.Merge(entry, entry.Properties, spec);
    }

    // ---- Expanding and checking ----

    private sealed class Context(ClassColumns? columns, SystemDefinition? system, FeatureGrantVocabulary? vocabulary, HashSet<string> spellIds,
        Dictionary<string, object> pools, List<string> problems, bool merge)
    {
        public List<string> Problems { get; } = problems;

        // Every counter key and where it was written, and every spender: checked once all entries are done.
        private readonly Dictionary<string, List<(string Where, string? Pool)>> _owners = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Where, string Scope, string Key)> _spenders = new();

        public void Merge(ContentEntry entry, IDictionary<string, object> row, Spec spec)
        {
            if (!merge) return;
            var where = spec.Label;
            var scope = ScopeOf(entry);
            // The row's own options are the ones the grants for each option land on.
            var written = new Dictionary<string, object>();
            if (row.TryGetValue("options", out var options)) written["options"] = options;
            Expand(entry, spec.Data, written, where, scope);
            foreach (var (key, value) in written) row[key] = value;
        }

        /// <summary>The class a subclass belongs to or the entry itself: the group of entries that may spend each other's pools.</summary>
        private static string ScopeOf(ContentEntry entry) =>
            entry.Type == "subclass" && entry.Properties.GetValueOrDefault("class") is string cls ? "class/" + cls : entry.Type + "/" + entry.Id;

        private void Expand(ContentEntry entry, Dictionary<string, object> data, IDictionary<string, object> into, string where, string scope)
        {
            var adds = new List<object>();
            foreach (var (key, value) in data)
            {
                switch (key)
                {
                    case "uses":
                        into["uses"] = ExpandUses(entry, value, where, scope);
                        break;
                    case "spends":
                        into["spends"] = value;
                        if (value is Dictionary<string, object> spend && spend.GetValueOrDefault("key") is string spendKey) _spenders.Add((where, scope, spendKey));
                        else Problems.Add($"{where}: \"spends\" needs a key.");
                        break;
                    case "modifiers":
                        into["modifiers"] = value;
                        CheckModifiers(value, where);
                        break;
                    case "choose":
                        into["choose"] = value;
                        break;
                    case "gate":
                        into["level"] = value;
                        break;
                    case "adds":
                        if (value is List<object> given) adds.AddRange(given);
                        break;
                    case "resist":
                        if (value is List<object> types) adds.Add(new Dictionary<string, object> { ["to"] = "resistances", ["entries"] = types.ToList() });
                        break;
                    case "spells":
                        adds.AddRange(ExpandSpells(value, where));
                        break;
                    case "freeSpells":
                        // The same shape as spells, each cast without a slot: once per Long Rest unless the row's "free" says otherwise.
                        var freeSpec = data.GetValueOrDefault("free") ?? new Dictionary<string, object> { ["max"] = 1L, ["recoversOn"] = new List<object> { "Long Rest" } };
                        foreach (var add in ExpandSpells(value, where).OfType<Dictionary<string, object>>())
                        {
                            add["free"] = ExpandFree(entry, freeSpec, add, where);
                            adds.Add(add);
                        }

                        break;
                    case "free":
                        break;
                    case "options":
                        ExpandOptions(entry, value, into, where, scope);
                        break;
                    case "text" or "already" or "covered":
                        break;
                    default:
                        Problems.Add($"{where}: \"{key}\" is not a grant key.");
                        break;
                }
            }

            if (adds.Count > 0)
            {
                CheckAdds(adds, where);
                into["adds"] = adds;
            }
        }

        private IEnumerable<object> ExpandSpells(object value, string where)
        {
            // A list is spells with no level; a map is level -> spells (0: no level).
            var tiers = value switch
            {
                List<object> list => new List<(long Level, List<object> Spells)> { (0, list) },
                Dictionary<string, object> map => map.Select(p => (Level: long.Parse(p.Key, CultureInfo.InvariantCulture), Spells: p.Value as List<object> ?? new List<object>()))
                    .OrderBy(t => t.Level).ToList(),
                _ => new List<(long, List<object>)>()
            };
            if (tiers.Count == 0) Problems.Add($"{where}: \"spells\" is a list of spell ids, or levels each with a list.");
            foreach (var (level, spells) in tiers)
            {
                var add = new Dictionary<string, object> { ["to"] = "grantedSpells", ["entries"] = spells.ToList() };
                if (level > 0) add["level"] = level;
                foreach (var id in spells.Select(s => s.ToString() ?? string.Empty))
                {
                    if (spellIds.Count > 0 && !spellIds.Contains(id)) Problems.Add($"{where}: \"{id}\" is not a spell id in the source.");
                }

                yield return add;
            }
        }

        private void ExpandOptions(ContentEntry entry, object value, IDictionary<string, object> into, string where, string scope)
        {
            if (value is not Dictionary<string, object> byName)
            {
                Problems.Add($"{where}: \"options\" is a map of option name to grants.");
                return;
            }

            // A row that has no options yet gets a new list, its options named as the file names them (no text).
            var options = into.TryGetValue("options", out var current) && current is List<object> existing ? existing : new List<object>();
            foreach (var (name, spec) in byName)
            {
                var option = options.OfType<Dictionary<string, object>>().FirstOrDefault(o => string.Equals(NameOf(o), name, StringComparison.OrdinalIgnoreCase));
                if (option == null)
                {
                    option = new Dictionary<string, object> { ["name"] = name };
                    options.Add(option);
                }

                if (spec is Dictionary<string, object> optionData) Expand(entry, optionData, option, $"{where}: {name}", scope);
            }

            into["options"] = options;
        }

        private object ExpandUses(ContentEntry entry, object value, string where, string scope)
        {
            if (value is not Dictionary<string, object> uses)
            {
                Problems.Add($"{where}: \"uses\" is a map with a max and a key.");
                return value;
            }

            var merged = new Dictionary<string, object>();
            string? pool = null;
            if (uses.GetValueOrDefault("$ref") is string reference)
            {
                pool = reference;
                if (pools.GetValueOrDefault(reference) is Dictionary<string, object> shared)
                {
                    foreach (var (k, v) in shared) merged[k] = Copy(v);
                }
                else
                {
                    Problems.Add($"{where}: the pool \"{reference}\" is not defined under pools.");
                }
            }

            foreach (var (k, v) in uses.Where(p => p.Key != "$ref")) merged[k] = v;

            ResolveMaxColumn(entry, merged, where);

            if (merged.GetValueOrDefault("key") is not string key || key.Length == 0)
            {
                Problems.Add($"{where}: a counter needs a key.");
            }
            else
            {
                if (!_owners.TryGetValue(key, out var owners)) _owners[key] = owners = new List<(string, string?)>();
                owners.Add((where, pool));
            }

            CheckUses(merged, where);
            return merged;
        }

        /// <summary><c>max: { column: X }</c> becomes the step formula of the Levels file's class_specific X (see <see cref="ClassColumns"/>).</summary>
        private void ResolveMaxColumn(ContentEntry entry, Dictionary<string, object> uses, string where)
        {
            if (uses.GetValueOrDefault("max") is not Dictionary<string, object> column || column.GetValueOrDefault("column") is not string columnName) return;
            var classId = entry.Type == "class" ? entry.Id : entry.Properties.GetValueOrDefault("class")?.ToString();
            if (columns == null || classId == null)
            {
                uses["max"] = 1L; // classifying only: the number is not needed
                return;
            }

            try
            {
                var formula = columns.Formula(classId, columnName);
                uses["max"] = long.TryParse(formula, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : formula;
            }
            catch (InvalidDataException ex)
            {
                Problems.Add($"{where}: {ex.Message}");
            }
        }

        /// <summary>The <c>free</c> of a spell added to the granted spells: <c>atWill</c> or a counter map (the same keys as <c>uses</c>), with its
        /// column worked out and its counter keys (<c>free-&lt;spell id&gt;</c>, or the map's own) registered as owned.</summary>
        private object ExpandFree(ContentEntry entry, object spec, Dictionary<string, object> add, string where)
        {
            if (spec is not Dictionary<string, object> map) return spec;
            var free = (Dictionary<string, object>)Copy(map);
            ResolveMaxColumn(entry, free, where);
            CheckUses(free, where);
            var spells = (add["entries"] as List<object> ?? new List<object>()).Select(s => s.ToString() ?? string.Empty).ToList();
            foreach (var key in free.GetValueOrDefault("key") is string own ? new[] { own } : spells.Select(s => "free-" + s))
            {
                if (!_owners.TryGetValue(key, out var owners)) _owners[key] = owners = new List<(string, string?)>();
                // One counter per spell: the same spell from two features that can never be on a sheet together (two lineages) is one pool.
                owners.Add((where, "free:" + key));
            }

            return free;
        }

        /// <summary>A copy of a value from the file, so two rows never share one list (the exporter would write it as a YAML anchor).</summary>
        private static object Copy(object value) => value switch
        {
            Dictionary<string, object> map => map.ToDictionary(p => p.Key, p => Copy(p.Value)),
            List<object> list => list.Select(Copy).ToList(),
            _ => value
        };

        private void CheckUses(Dictionary<string, object> uses, string where)
        {
            if (vocabulary == null) return;
            if (uses.GetValueOrDefault("max") is string max && vocabulary.CheckFormula(max) is { } error) Problems.Add($"{where}: the maximum \"{max}\": {error}");
            if (uses.GetValueOrDefault("max") == null) Problems.Add($"{where}: a counter needs a max.");
            foreach (var rest in (uses.GetValueOrDefault("recoversOn") as List<object> ?? new List<object>()).Select(r => r.ToString() ?? string.Empty))
            {
                if (!vocabulary.Rests.Contains(rest, StringComparer.OrdinalIgnoreCase)) Problems.Add($"{where}: \"{rest}\" is not a rest of this system.");
            }

            foreach (var recover in (uses.GetValueOrDefault("recovers") as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var on = recover.GetValueOrDefault("on")?.ToString() ?? string.Empty;
                if (!vocabulary.Rests.Concat(vocabulary.Moments).Contains(on, StringComparer.OrdinalIgnoreCase)) Problems.Add($"{where}: \"{on}\" is neither a rest nor a moment.");
                foreach (var formula in new[] { recover.GetValueOrDefault("amount") as string, recover.GetValueOrDefault("when") as string })
                {
                    if (formula is null or "all" or "empty") continue;
                    if (vocabulary.CheckFormula(formula) is { } refillError) Problems.Add($"{where}: the refill \"{formula}\": {refillError}");
                }
            }
        }

        private void CheckModifiers(object value, string where)
        {
            if (vocabulary == null) return;
            foreach (var modifier in (value as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var target = modifier.GetValueOrDefault("target")?.ToString() ?? string.Empty;
                if (!vocabulary.Targets.Any(t => t.Key.Equals(target, StringComparison.OrdinalIgnoreCase))) Problems.Add($"{where}: \"{target}\" is not a modifier target of this system.");
                if (modifier.GetValueOrDefault("type")?.ToString() is { } type && !vocabulary.ModifierTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) Problems.Add($"{where}: \"{type}\" is not a modifier type.");
                if (modifier.GetValueOrDefault("amount") is string amount && vocabulary.CheckFormula(amount) is { } error) Problems.Add($"{where}: the amount \"{amount}\": {error}");
            }
        }

        private void CheckAdds(List<object> adds, string where)
        {
            if (vocabulary == null) return;
            foreach (var add in adds.OfType<Dictionary<string, object>>())
            {
                var to = add.GetValueOrDefault("to")?.ToString() ?? string.Empty;
                if (vocabulary.DamageListKeys.Contains(to, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var type in (add.GetValueOrDefault("entries") as List<object> ?? new List<object>()).Select(e => e.ToString() ?? string.Empty))
                    {
                        if (!vocabulary.DamageTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) Problems.Add($"{where}: \"{type}\" is not a damage type of this system.");
                    }
                }
            }
        }

        /// <summary>After every entry: a key may be written once, or by several rows that all name the same shared pool; a spender needs a pool in its scope.</summary>
        public void CheckKeys(IReadOnlyList<ContentEntry> entries)
        {
            if (!merge) return;
            foreach (var (key, owners) in _owners)
            {
                var shared = owners.All(o => o.Pool != null && o.Pool == owners[0].Pool);
                if (owners.Count > 1 && !shared) Problems.Add($"The counter key \"{key}\" has {owners.Count} owners ({string.Join("; ", owners.Select(o => o.Where))}). Only a shared pool may repeat.");
            }

            // A pool is in scope for its own class and that class's subclasses, or its own species.
            var scopes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var scope = ScopeOf(entry);
                if (!scopes.TryGetValue(scope, out var keys)) scopes[scope] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in RowsOfAll(entry))
                {
                    if (row.TryGetValue("uses", out var raw) && raw is Dictionary<string, object> uses && uses.GetValueOrDefault("key") is string key) keys.Add(key);
                }
            }

            foreach (var (where, scope, key) in _spenders)
            {
                if (!(scopes.TryGetValue(scope, out var keys) && keys.Contains(key))) Problems.Add($"{where}: spends \"{key}\", which no counter of its class or species owns.");
            }

            if (system != null && FeatureGrantVocabulary.For(system) != null)
            {
                foreach (var entry in entries.Where(e => e.Type is "class" or "subclass" or "species" or "feat"))
                {
                    foreach (var problem in FeatureGrantCheck.Problems(system, entry, requireId: true)) Problems.Add($"{entry.Type}/{entry.Id}: {problem}");
                }
            }
        }

        private static IEnumerable<IDictionary<string, object>> RowsOfAll(ContentEntry entry)
        {
            yield return entry.Properties;
            foreach (var property in new[] { "features", "traits", "lineages" })
            {
                if (entry.Properties.GetValueOrDefault(property) is not IEnumerable<object> rows) continue;
                foreach (var row in rows.OfType<IDictionary<string, object>>()) yield return row;
            }
        }
    }
}
