using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.ContentImport;

/// <summary>
/// Converts the Markdown transcription of the Forged in the Dark SRD into a TTRPG.Codex content pack
/// for the <c>fitd-srd</c> system. Every heading that carries text becomes a <c>rule</c> entry whose
/// body is the SRD's own Markdown; the SRD's structured lists (the twelve actions, the trauma
/// conditions, the example special abilities, the crew special abilities and the crew upgrades)
/// become typed entries. Nothing is added that the SRD does not contain.
/// </summary>
public sealed class FitdSrdImporter(string sourceFile)
{
    public const string SystemId = "fitd-srd";
    public const string PackId = "srd-fitd-2017";

    private const string Attribution =
        "This work is based on Blades in the Dark (found at http://www.bladesinthedark.com/), product of One Seven Design, developed and authored by John Harper, and licensed for our use under the Creative Commons Attribution 3.0 Unported license (http://creativecommons.org/licenses/by/3.0/).";

    private const string LicenseUrl = "https://creativecommons.org/licenses/by/3.0/legalcode";

    /// <summary>The entry types the system declares; the importer writes nothing else.</summary>
    public static readonly string[] EntryTypes = { "rule", "action", "trauma", "special-ability", "crew-ability", "crew-upgrade" };

    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ActionRx = new(@"^When you \*\*(\w+)\*\*, ", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^\*\s+\*\*(.+?):?\*\*:?\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex AttributeHeadingRx = new(@"^#{4}\s+\**(Insight|Prowess|Resolve)\**\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BoldNameCellRx = new(@"^\*\*(\w+)\*\*$", RegexOptions.Compiled);
    private static readonly Regex UpgradeCostRx = new(@"\bcosts? (one|two|three|four|five|six|\d+) upgrade", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EntityRx = new(@"&#x([0-9A-Fa-f]+);", RegexOptions.Compiled);
    private static readonly Regex NbspRx = new(@"(?:\s*&nbsp;)+\s*", RegexOptions.Compiled);

    /// <summary>Pure layout artefacts of the transcription (stray or doubled bold markers), fixed one by one.</summary>
    private static readonly (Regex Pattern, string Replacement)[] MarkupFixes =
    {
        (new Regex(@"\*\*([+\-]?\d+) \*\*(\w+)\*\*\*\*", RegexOptions.Compiled), "**$1 $2**"),
        (new Regex(@"\*\*\+2 \*\*\*\*heat\*\*\*\*", RegexOptions.Compiled), "**+2 heat**"),
        (new Regex(@"\*\*\*\*(\w+)\*\*\*\*", RegexOptions.Compiled), "**$1**"),
        (new Regex(@"\*\*(\d)\*\*\*\*(?=\S)", RegexOptions.Compiled), "**$1** **"),
        (new Regex(@"\*\*(reset your) \*\*rep\*\* (to zero)\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase), "**$1 rep $2**"),
        (new Regex(@"\*\*Boat house\*\*\*\*:", RegexOptions.Compiled), "**Boat house**:"),
        (new Regex(@"\bYou get\*\* \*\*", RegexOptions.Compiled), "You get **"),
        (new Regex(@"\(max 6\)\*\*\.", RegexOptions.Compiled), "(max 6)."),
        (new Regex(@"\? \*\*Will anyone sense", RegexOptions.Compiled), "? Will anyone sense"),
        (new Regex(@"is always 6\.\*$", RegexOptions.Compiled), "is always 6."),
        (new Regex(@"\*\*4/5:\*\* level 2 harm\*\*\)", RegexOptions.Compiled), "**4/5:** level 2 harm)"),
        (new Regex(@"\*\*\*Finessed\*\*", RegexOptions.Compiled), "**Finessed**"),
        (new Regex(@"\*\*acquire asset\*\*\*", RegexOptions.Compiled), "**acquire asset**"),
        (new Regex(@"\*\*Prowess\*\*\*\.\*", RegexOptions.Compiled), "**Prowess**.")
    };

    private static readonly Dictionary<string, string[]> ChapterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Basics"] = new[] { "basics" },
        ["The Core System"] = new[] { "core" },
        ["Actions & Attributes"] = new[] { "actions", "attributes" },
        ["Stress & Trauma"] = new[] { "stress", "trauma" },
        ["Progress clocks"] = new[] { "clock" },
        ["Action Roll"] = new[] { "action-roll" },
        ["Effect"] = new[] { "effect" },
        ["Setting Position & Effect"] = new[] { "position", "effect" },
        ["Consequences and Harm"] = new[] { "consequence" },
        ["Resistance and Armor"] = new[] { "resistance" },
        ["Fortune Roll"] = new[] { "fortune-roll" },
        ["Gathering Information"] = new[] { "gather-information" },
        ["Coin and Stash"] = new[] { "coin" },
        ["The Faction Game"] = new[] { "faction" },
        ["Advancement"] = new[] { "advancement" },
        ["Characters"] = new[] { "character" },
        ["Character creation"] = new[] { "character", "creation" },
        ["Character playbook"] = new[] { "character", "playbook" },
        ["Crew creation"] = new[] { "crew", "creation" },
        ["Crew playbook"] = new[] { "crew", "playbook" },
        ["The Score"] = new[] { "score" },
        ["Planning & engagement"] = new[] { "score", "engagement" },
        ["Teamwork"] = new[] { "teamwork" },
        ["Downtime"] = new[] { "downtime" },
        ["Payoff"] = new[] { "payoff", "downtime" },
        ["Heat"] = new[] { "heat", "downtime" },
        ["Entanglements"] = new[] { "entanglement", "downtime" },
        ["Downtime activities"] = new[] { "downtime" },
        ["Magnitude"] = new[] { "magnitude" },
        ["Rituals"] = new[] { "ritual" },
        ["Crafting"] = new[] { "crafting" }
    };

    private static readonly (string Needle, string Tag)[] KeywordTags =
    {
        ("harm", "harm"), ("cohort", "cohort"), ("claim", "claim"), ("clock", "clock"), ("downtime", "downtime"),
        ("entanglement", "entanglement"), ("heat", "heat"), ("trauma", "trauma"), ("stress", "stress"),
        ("ritual", "ritual"), ("crafting", "crafting"), ("resistance", "resistance"), ("armor", "armor"),
        ("flashback", "flashback"), ("teamwork", "teamwork"), ("faction", "faction"), ("turf", "turf"),
        ("vice", "vice"), ("reputation", "reputation"), ("incarceration", "incarceration"), ("prison", "incarceration"),
        ("devil's bargain", "devils-bargain"), ("position", "position"), ("effect", "effect"), ("tier", "tier"),
        ("death", "death"), ("retirement", "retirement"), ("loadout", "loadout")
    };

    private enum Mode { None, SpecialAbilities, CrewAbilities }

    private sealed class Section(int level, string title, string[] path)
    {
        public int Level { get; } = level;
        public string Title { get; } = title;
        public string[] Path { get; } = path;
        public List<string> Lines { get; } = new();
    }

    public async Task<ImportResult> ImportAsync(string outputDirectory, TextWriter log)
    {
        var sections = ParseSections(await File.ReadAllLinesAsync(sourceFile));
        var entries = BuildEntries(sections);
        ApplyGrants(entries);
        foreach (var entry in entries)
        {
            entry.SystemId = SystemId;
        }

        Validate(entries);

        var manifest = BuildManifest();
        var systemPath = FindSystemYaml(outputDirectory);
        var system = systemPath != null ? SystemDefinitionLoader.Load(systemPath) : null;

        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        var exporter = new ContentPackExporter();
        await exporter.ExportToDirectoryAsync(manifest, Array.Empty<AbilityDefinition>(), Array.Empty<ActorDefinition>(), Array.Empty<LocationDefinition>(), outputDirectory, entries, system);

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "LEGAL.md"), LegalText(), new UTF8Encoding(false));

        var counts = entries.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        return new ImportResult(entries.Count, counts);
    }

    private static string? FindSystemYaml(string outputDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(outputDirectory));
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, SystemDefinitionLoader.FileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static PackManifest BuildManifest() => new()
    {
        Id = PackId,
        Name = "Forged in the Dark SRD (December 2017)",
        Version = "2017.12",
        SystemId = SystemId,
        Kind = "rules",
        Priority = 0,
        Description = "The Forged in the Dark System Reference Document: the core rules, the action and attribute system, stress and trauma, clocks, the faction game, character and crew creation, the score, downtime, rituals and crafting.",
        Authors = { "John Harper / One Seven Design (the SRD)", "TTRPG.Codex content importer (conversion)" },
        License = new LicenseInfo
        {
            Name = "CC-BY-3.0",
            Url = LicenseUrl,
            SourceUrl = "https://bladesinthedark.com/",
            Modified = true,
            Attribution = Attribution,
            Notices =
            {
                "Modified from the original: split into one entry per section, the SRD's structured lists (actions, trauma conditions, example special abilities, crew special abilities, crew upgrades) turned into typed entries, and the Markdown and table layout artefacts of the transcription cleaned up. No rules were intentionally changed.",
                $"The text was read from the Markdown transcription at https://github.com/{FitdSrdSource.Repository} (commit {FitdSrdSource.Commit}), file {FitdSrdSource.FileName}, the SRD text published for the game at https://bladesinthedark.com/.",
                "The SRD does not cover the game's published book, its setting, its NPCs, or its artwork and maps. This pack contains none of them beyond what the SRD's own example text quotes.",
                $"The legal text of the licence is at {LicenseUrl}.",
                "This pack is not affiliated with or endorsed by One Seven Design."
            }
        }
    };

    private static string LegalText()
    {
        var manifest = BuildManifest();
        var sb = new StringBuilder();
        sb.AppendLine($"# Legal Information - {manifest.Name}");
        sb.AppendLine();
        sb.AppendLine(manifest.License!.Attribution);
        sb.AppendLine();
        foreach (var notice in manifest.License.Notices)
        {
            sb.AppendLine($"- {notice}");
        }

        return sb.ToString();
    }

    // ---- Parsing ----

    private static List<Section> ParseSections(IEnumerable<string> lines)
    {
        var sections = new List<Section>();
        var path = new string?[4];
        Section? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var heading = HeadingRx.Match(line);
            if (heading.Success && heading.Groups[1].Length <= 3)
            {
                var level = heading.Groups[1].Length;
                var title = Plain(heading.Groups[2].Value);
                path[level] = title;
                for (var deeper = level + 1; deeper <= 3; deeper++)
                {
                    path[deeper] = null;
                }

                current = new Section(level, title, path.Skip(1).Where(p => p != null).Select(p => p!).ToArray());
                sections.Add(current);
            }
            else
            {
                current?.Lines.Add(line);
            }
        }

        return sections;
    }

    private static string Plain(string heading) => heading.Replace("*", string.Empty).Trim();

    // ---- Conversion ----

    private static List<ContentEntry> BuildEntries(List<Section> sections)
    {
        var attributes = ActionAttributes(sections);
        var entries = new List<ContentEntry>();
        var ids = new Dictionary<string, int>();
        var order = 0;
        var mode = Mode.None;
        var abilitySource = string.Empty;

        foreach (var section in sections)
        {
            var lines = Clean(section.Lines);
            var source = string.Join(" > ", section.Path);

            var specialIntro = section is { Level: 2, Title: "Special abilities" };
            var crewIntro = section is { Level: 2, Title: "Crew special abilities" };
            if (mode != Mode.None && !specialIntro && !crewIntro && (section.Level == 1 || section.Title == "Character items"))
            {
                mode = Mode.None;
            }

            if (specialIntro || crewIntro)
            {
                mode = specialIntro ? Mode.SpecialAbilities : Mode.CrewAbilities;
                abilitySource = source;
            }
            else if (mode != Mode.None)
            {
                // Every heading up to the next chapter is an example ability, whatever its level: the SRD sets
                // "Fortitude" as a level-2 heading among the level-3 ones, which must not become the parent of the rest.
                var type = mode == Mode.SpecialAbilities ? "special-ability" : "crew-ability";
                var ability = New(type, section.Title, Normalize(lines), $"{abilitySource} > {section.Title}");
                ability.Tags.Add("example");
                Add(entries, ids, ability, Slug(section.Title));
                continue;
            }

            var remainder = lines;
            if (section is { Level: 2, Title: "Actions" } && section.Path[0] == "Actions & Attributes")
            {
                remainder = ExtractActions(lines, attributes, entries, ids, source);
            }
            else if (section is { Level: 3, Title: "Trauma Conditions" })
            {
                remainder = ExtractBullets(lines, out var items);
                RequireItems(items, section.Title);
                foreach (var (name, text) in items)
                {
                    Add(entries, ids, New("trauma", name, text, source), Slug(name));
                }
            }
            else if (section is { Level: 2, Title: "Crew upgrade Examples" or "Crew upgrades" })
            {
                remainder = ExtractBullets(lines, out var items);
                RequireItems(items, section.Title);
                foreach (var (name, text) in items)
                {
                    var upgrade = New("crew-upgrade", name, text, source);
                    upgrade.Tags.Add("example");
                    if (UpgradeCostRx.Match(text) is { Success: true } cost)
                    {
                        upgrade.Properties["cost"] = UpgradeCost(cost.Groups[1].Value);
                    }

                    Add(entries, ids, upgrade, Slug(name));
                }
            }

            var body = Normalize(remainder);
            if (body.Length == 0)
            {
                continue;
            }

            var rule = New("rule", section.Title, body, source);
            rule.Properties["category"] = section.Path[0];
            rule.Properties["order"] = ++order;
            foreach (var tag in RuleTags(section))
            {
                rule.Tags.Add(tag);
            }

            Add(entries, ids, rule, Slug(string.Join(" ", section.Path)));
        }

        var actionCount = entries.Count(e => e.Type == "action");
        if (actionCount != 12)
        {
            throw new InvalidDataException($"Expected the SRD's 12 actions, found {actionCount}.");
        }

        return entries;
    }

    private static ContentEntry New(string type, string name, string description, string source) => new()
    {
        Type = type,
        Name = name,
        Description = description,
        Source = source
    };

    private static void Add(List<ContentEntry> entries, Dictionary<string, int> ids, ContentEntry entry, string slug)
    {
        if (slug.Length == 0)
        {
            throw new InvalidDataException($"The {entry.Type} '{entry.Name}' has no usable id.");
        }

        var key = $"{entry.Type}:{slug}";
        var count = ids.GetValueOrDefault(key) + 1;
        ids[key] = count;
        entry.Id = count == 1 ? slug : $"{slug}-{count}";
        entries.Add(entry);
    }

    private static void RequireItems(List<(string Name, string Text)> items, string section)
    {
        if (items.Count == 0)
        {
            throw new InvalidDataException($"No list items found in the SRD section '{section}'.");
        }
    }

    private static int UpgradeCost(string word) => word.ToLowerInvariant() switch
    {
        "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        _ => int.Parse(word, CultureInfo.InvariantCulture)
    };

    private static IEnumerable<string> RuleTags(Section section)
    {
        var tags = new List<string>();
        if (ChapterTags.TryGetValue(section.Path[0], out var chapter))
        {
            tags.AddRange(chapter);
        }

        var haystack = string.Join(" | ", section.Path.Skip(1).Append(section.Title)).ToLowerInvariant();
        foreach (var (needle, tag) in KeywordTags)
        {
            if (haystack.Contains(needle))
            {
                tags.Add(tag);
            }
        }

        return tags.Distinct();
    }

    /// <summary>Which attribute each action rolls under, read from the SRD's own example rating tables.</summary>
    private static Dictionary<string, string> ActionAttributes(List<Section> sections)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = sections.FirstOrDefault(s => s.Title.StartsWith("EXAMPLE ACTION", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The example action and attribute rating tables were not found.");

        string? attribute = null;
        foreach (var line in section.Lines)
        {
            var heading = AttributeHeadingRx.Match(line);
            if (heading.Success)
            {
                attribute = heading.Groups[1].Value.ToLowerInvariant();
            }
            else if (attribute != null && line.StartsWith('|') && BoldNameCellRx.Match(Cells(line)[^1]) is { Success: true } name)
            {
                map[name.Groups[1].Value] = attribute;
            }
        }

        return map;
    }

    private static List<string> ExtractActions(List<string> lines, Dictionary<string, string> attributes, List<ContentEntry> entries, Dictionary<string, int> ids, string source)
    {
        var remainder = new List<string>();
        ContentEntry? last = null;
        foreach (var paragraph in Paragraphs(lines))
        {
            var match = ActionRx.Match(paragraph);
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                if (!attributes.TryGetValue(name, out var attribute))
                {
                    throw new InvalidDataException($"No attribute found for the action '{name}'.");
                }

                var action = New("action", name, paragraph, source);
                action.Properties["attribute"] = attribute;
                action.Tags.Add(attribute);
                Add(entries, ids, action, Slug(name));
                last = action;
            }
            else if (last != null && paragraph.StartsWith('*') && paragraph.EndsWith('*'))
            {
                last.Description += "\n\n" + paragraph;
                last = null;
            }
            else
            {
                remainder.Add(paragraph);
                remainder.Add(string.Empty);
                last = null;
            }
        }

        return remainder;
    }

    private static List<string> ExtractBullets(List<string> lines, out List<(string Name, string Text)> items)
    {
        items = new List<(string Name, string Text)>();
        var remainder = new List<string>();
        foreach (var line in lines)
        {
            var match = BulletRx.Match(line);
            if (match.Success)
            {
                items.Add((match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()));
            }
            else
            {
                remainder.Add(line);
            }
        }

        return remainder;
    }

    private static IEnumerable<string> Paragraphs(IEnumerable<string> lines)
    {
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    yield return string.Join('\n', current);
                    current.Clear();
                }
            }
            else
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            yield return string.Join('\n', current);
        }
    }

    // ---- Markdown clean-up ----

    /// <summary>Removes the transcription's layout artefacts line by line: author TODO notes, HTML
    /// entities for the sheet's dots and boxes, doubled bold markers, empty table headers.</summary>
    private static List<string> Clean(IEnumerable<string> lines)
    {
        var cleaned = new List<string>();
        foreach (var raw in lines)
        {
            if (raw.StartsWith("@TODO", StringComparison.Ordinal))
            {
                continue;
            }

            var line = EntityRx.Replace(raw, m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            line = NbspRx.Replace(line, " ");
            foreach (var (pattern, replacement) in MarkupFixes)
            {
                line = pattern.Replace(line, replacement);
            }

            if (line.StartsWith('|'))
            {
                line = line.Replace("[]()", string.Empty);
            }

            cleaned.Add(line.TrimEnd());
        }

        return ConvertTables(cleaned);
    }

    private static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    /// <summary>Rewrites the two tables whose layout only makes sense on the printed sheet: the harm
    /// table and the example rating tables (dots per action).</summary>
    private static List<string> ConvertTables(List<string> lines)
    {
        var result = new List<string>();
        for (var i = 0; i < lines.Count;)
        {
            if (!lines[i].StartsWith('|'))
            {
                result.Add(lines[i++]);
                continue;
            }

            var end = i;
            while (end < lines.Count && lines[end].StartsWith('|'))
            {
                end++;
            }

            var block = lines.GetRange(i, end - i);
            i = end;

            if (block.Any(r => r.Contains("Shattered Right Leg", StringComparison.Ordinal)))
            {
                result.AddRange(new[]
                {
                    "| Level | Harm | Effect |",
                    "| :--: | :-- | :--: |",
                    "| 3 | *Shattered Right Leg* | Need Help |",
                    "| 2 | | -1D |",
                    "| 1 | *Drained*, *Battered* | Reduced Effect |"
                });
            }
            else if (IsRatingTable(block))
            {
                result.Add("| Action | Rating |");
                result.Add("| :-- | :-- |");
                foreach (var row in block.Skip(2))
                {
                    var cells = Cells(row);
                    result.Add($"| {cells[^1]} | {cells[0]}{cells[2]}{cells[3]}{cells[4]} |");
                }
            }
            else
            {
                result.AddRange(block);
            }
        }

        return result;
    }

    private static bool IsRatingTable(List<string> block) =>
        block.Count > 2
        && Cells(block[0]).Length == 6
        && Cells(block[0]).All(c => c.Length == 0)
        && block.Skip(2).All(r => Cells(r) is { Length: 6 } cells && BoldNameCellRx.IsMatch(cells[^1]));

    /// <summary>Trims blank lines and horizontal rules from both ends and collapses runs of blank lines.</summary>
    private static string Normalize(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        static bool Filler(string line) => string.IsNullOrWhiteSpace(line) || line.Trim() == "---";

        var start = 0;
        while (start < list.Count && Filler(list[start])) start++;
        var end = list.Count;
        while (end > start && Filler(list[end - 1])) end--;

        var sb = new StringBuilder();
        var blank = false;
        for (var i = start; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(list[i]))
            {
                blank = true;
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(blank ? "\n\n" : "\n");
            }

            sb.Append(list[i]);
            blank = false;
        }

        return sb.ToString();
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        var dash = false;
        foreach (var c in text.ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }

    // ---- Grants: what the engine reads from an entry's own properties (docs/SHEETKINDS_FEATURES_DESIGN.md, section 4) ----

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    /// <summary>"Special armor": one box, and several abilities still give one (the system stacks the type <c>boxes</c> by highest).</summary>
    private static (string, object) SpecialArmorBox() =>
        ("modifiers", Items(Map(("target", "specialArmorBoxes"), ("amount", 1L), ("type", "boxes"))));

    /// <summary>A counter of limited uses that comes back at a moment the system declares.</summary>
    private static (string, object) Uses(string key, long max, string moment) =>
        ("uses", Map(("key", key), ("max", max), ("recovers", Items(Map(("on", moment))))));

    /// <summary>A bonus die the player ticks before the roll, because the story decides when it holds (docs/ROLL_BONUSES_DESIGN.md). It is a pool
    /// modifier on <paramref name="target"/> that never counts on the sheet; <paramref name="action"/> scopes it to one action when the SRD's text names it.</summary>
    private static Dictionary<string, object> OptIn(string target, string label, string? action = null)
    {
        var modifier = Map(("target", target), ("amount", 1L));
        if (action != null) modifier["when"] = $"input.action == '{action}'";
        modifier["optIn"] = Map(("label", label));
        return modifier;
    }

    /// <summary>One ability's story-decided bonus die: <c>modifiers: [OptIn(...)]</c>.</summary>
    private static (string, object) OptInDie(string target, string label, string? action = null) => ("modifiers", Items(OptIn(target, label, action)));

    /// <summary>What a crew's entry does to each scoundrel linked to the crew (docs/CREW_LINKS_DESIGN.md): the grant map the sheet kind's
    /// <c>link:</c> sources read from the entry's <c>members</c> property.</summary>
    private static (string, object) Members(params (string Key, object Value)[] grants) => ("members", Map(grants));

    /// <summary>A modifier that always counts, on a value the scoundrel kind declares for it (<c>stressBoxes</c>, <c>traumaBoxes</c>, <c>actionCap</c>).</summary>
    private static (string, object) MemberValue(string target) => Members(("modifiers", Items(Map(("target", target), ("amount", 1L)))));

    /// <summary>Deadly: each member raises one of three actions by one. A keyed <c>adds</c> choice only marks a key, so this is a row choice
    /// (<c>options</c> + <c>choose</c>) whose rows each carry a modifier on that action's rating. The pick is the member's own, stored
    /// as <c>choice__deadly__deadly</c> on the member's sheet.</summary>
    private static (string, object) DeadlyChoice() => Members(
        ("options", Items(DeadlyRow("Hunt", "hunt"), DeadlyRow("Prowl", "prowl"), DeadlyRow("Skirmish", "skirmish"))),
        ("choose", Map(("id", "deadly"), ("label", "Deadly"), ("count", 1L))));

    private static Dictionary<string, object> DeadlyRow(string name, string key) =>
        Map(("name", name), ("modifiers", Items(Map(("target", "actions." + key), ("amount", 1L)))));

    /// <summary>The abilities whose rules change a number, add a limited use or leave a choice open, written by hand from the SRD's text and
    /// reviewed: entry type, entry id (the slug of its heading), the properties to set. A rule the engine cannot read stays text: nothing is
    /// derived from the words. The system's own <c>system.yaml</c> names the targets (<c>LoadMax</c>, <c>specialArmorBoxes</c>, <c>turfCount</c>,
    /// <c>@resistance</c>) and the moments (<c>downtime</c>, <c>loadChosen</c>).</summary>
    public static readonly IReadOnlyList<(string Type, string Id, IReadOnlyDictionary<string, object> Properties)> Grants = new (string, string, (string Key, object Value)[])[]
    {
        ("special-ability", "analyst", new[] { Uses("analyst", 2, "downtime") }),
        ("special-ability", "battleborn", new[] { SpecialArmorBox() }),
        ("special-ability", "calculating", new[] { Uses("calculating", 1, "downtime") }),
        ("special-ability", "expertise", new (string, object)[] { ("adds", Items(Map(("to", "actions"), ("choice", "expertise"), ("choose", 1L)))) }),
        ("special-ability", "focused", new[] { SpecialArmorBox() }),
        ("special-ability", "foresight", new[] { Uses("foresight", 2, "loadChosen") }),
        ("special-ability", "fortitude", new[] { SpecialArmorBox() }),
        ("special-ability", "iron-will", new (string, object)[] { ("modifiers", Items(Map(("target", "@resistance"), ("amount", 1L), ("when", "input.attribute == 'resolve'")))) }),
        ("special-ability", "mastermind", new[] { SpecialArmorBox() }),
        ("special-ability", "mule", new (string, object)[] { ("modifiers", Items(Map(("target", "LoadMax"), ("amount", 2L)))) }),
        ("special-ability", "shadow", new[] { SpecialArmorBox() }),
        ("special-ability", "subterfuge", new[] { SpecialArmorBox() }),
        ("special-ability", "warded", new[] { SpecialArmorBox() }),
        ("crew-ability", "fiends", new (string, object)[] { ("modifiers", Items(Map(("target", "turfCount"), ("amount", "wanted")))) }),

        // Story-decided bonus dice: tick boxes on the roll. Each is scoped to its action only where the SRD's text names one.
        // Ambush ("attack from hiding or spring a trap"), Trust in me ("any action can get the bonus") and Scout ("rolls to avoid detection") name none.
        ("special-ability", "ambush", new[] { OptInDie("@pools", "From hiding or a trap") }),
        ("special-ability", "savage", new[] { OptInDie("@pools", "Frightened target", "command") }),
        ("special-ability", "trust-in-me", new[] { OptInDie("@pools", "Intimate target") }),
        ("special-ability", "weaving-the-web", new[] { OptInDie("@pools", "Gathering on a target", "consort") }),
        ("special-ability", "scout", new[] { OptInDie("@pools", "Avoiding detection") }),
        ("special-ability", "bodyguard", new[] { OptInDie("@resistance", "Protecting a teammate") }),

        // What a crew's upgrades and abilities do to each scoundrel linked to the crew (the `members` property; the kind's `link:` sources read it).
        ("crew-upgrade", "composed", new[] { MemberValue("stressBoxes") }),
        ("crew-upgrade", "hardened", new[] { MemberValue("traumaBoxes") }),
        ("crew-upgrade", "mastery", new[] { MemberValue("actionCap") }),
        ("crew-ability", "forged-in-the-fire", new[] { Members(("modifiers", Items(Map(("target", "@resistance"), ("amount", 1L))))) }),
        ("crew-ability", "anointed", new[] { Members(("modifiers", Items(OptIn("@resistance", "Against a supernatural threat")))) }),
        ("crew-ability", "deadly", new[] { DeadlyChoice() })
    }.Select(g => (g.Item1, g.Item2, (IReadOnlyDictionary<string, object>)g.Item3.ToDictionary(p => p.Key, p => p.Value))).ToList();

    /// <summary>Sets each reviewed row's properties on its entry; the description and tags stay as the SRD's text made them. A row that
    /// matches no entry is left for <see cref="Validate"/> to refuse.</summary>
    public static void ApplyGrants(List<ContentEntry> entries)
    {
        foreach (var (type, id, properties) in Grants)
        {
            if (entries.FirstOrDefault(e => e.Type == type && e.Id == id) is not { } entry) continue;
            foreach (var (key, value) in properties) entry.Properties[key] = value;
        }
    }

    public static void Validate(List<ContentEntry> entries)
    {
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (!EntryTypes.Contains(entry.Type))
            {
                throw new InvalidDataException($"Unexpected entry type '{entry.Type}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Description))
            {
                throw new InvalidDataException($"The {entry.Type} '{entry.Id}' is empty.");
            }

            if (!seen.Add(entry.FullId))
            {
                throw new InvalidDataException($"Duplicate entry id {entry.FullId}.");
            }
        }

        // A renamed SRD heading must fail loudly: a grant whose id matches no entry, or an entry of another type, would be dropped silently.
        foreach (var (type, id, _) in Grants)
        {
            if (entries.Any(e => e.Type == type && e.Id == id)) continue;
            var other = entries.FirstOrDefault(e => e.Id == id);
            throw new InvalidDataException(other == null
                ? $"The reviewed grants name the {type} '{id}', which the SRD text did not produce."
                : $"The reviewed grants name '{id}' as a {type}, but the SRD text produced a {other.Type} of that id.");
        }
    }
}
