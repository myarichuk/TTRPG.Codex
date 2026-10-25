using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.ContentImport;

public sealed record ImportResult(int EntryCount, Dictionary<string, int> Types);

/// <summary>
/// Converts the 5e-bits JSON transcription of the SRD into a TTRPG.Codex content pack for the
/// <c>srd5e</c> system. Every entry keeps the structured fields the system's <c>system.yaml</c>
/// declares and gets a readable Markdown description assembled from the source, so the Library
/// renders it without any 5E-specific UI.
/// </summary>
public sealed class Srd5eImporter(string sourceDirectory, string edition = "2024")
{
    private const string SystemId = "srd5e";

    private readonly Dictionary<string, JsonDocument> _documents = new();
    private readonly Lazy<JsonElement?> _classTables = new(LoadClassTables);

    /// <summary>The per-level class tables (cantrips, prepared spells, slots, feat slots) are not in
    /// the 5e-bits 2024 data; they come from the SRD's own class tables, embedded as numbers only.</summary>
    private static JsonElement? LoadClassTables()
    {
        using var stream = typeof(Srd5eImporter).Assembly.GetManifestResourceStream("srd521-class-tables.json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    public async Task<ImportResult> ImportAsync(string outputDirectory, TextWriter log)
    {
        var entries = new List<ContentEntry>();
        entries.AddRange(Classes());
        entries.AddRange(Subclasses());
        entries.AddRange(Feats());
        entries.AddRange(edition == "2024" ? Species() : Races());
        entries.AddRange(Backgrounds());
        entries.AddRange(Spells());
        entries.AddRange(Monsters());
        entries.AddRange(Conditions());
        entries.AddRange(Equipment());
        entries.AddRange(MagicItems());
        entries.AddRange(Rules());

        foreach (var entry in entries)
        {
            entry.SystemId = SystemId;
        }

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

    private PackManifest BuildManifest()
    {
        var is2024 = edition == "2024";
        return new PackManifest
        {
            Id = is2024 ? "srd-5.2.1" : "srd-5.1",
            Name = is2024 ? "System Reference Document 5.2.1" : "System Reference Document 5.1",
            Version = is2024 ? "5.2.1" : "5.1",
            SystemId = SystemId,
            Kind = "rules",
            Priority = 0,
            Description = is2024
                ? "The complete SRD 5.2.1 (2024 rules): classes, subclasses, species, backgrounds, feats, spells, creatures, conditions, equipment, magic items and the rules glossary."
                : "The SRD 5.1 (2014 rules).",
            Authors = { "Wizards of the Coast LLC (rules text)", "5e-bits contributors (JSON transcription)", "TTRPG.Codex content importer (conversion)" },
            License = new LicenseInfo
            {
                Name = "CC-BY-4.0",
                Url = "https://creativecommons.org/licenses/by/4.0/legalcode",
                SourceUrl = is2024 ? "https://www.dndbeyond.com/srd" : "https://dnd.wizards.com/resources/systems-reference-document",
                Modified = true,
                Attribution = is2024
                    ? "This work includes material from the System Reference Document 5.2.1 (“SRD 5.2.1”) by Wizards of the Coast LLC, available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0 International License, available at https://creativecommons.org/licenses/by/4.0/legalcode."
                    : "This work includes material taken from the System Reference Document 5.1 (“SRD 5.1”) by Wizards of the Coast LLC and available at https://dnd.wizards.com/resources/systems-reference-document. The SRD 5.1 is licensed under the Creative Commons Attribution 4.0 International License available at https://creativecommons.org/licenses/by/4.0/legalcode.",
                Notices =
                {
                    "Modified from the original: restructured into YAML entries, layout artefacts removed, structured fields added. No rules were changed. One creature, the Mule, is omitted because the source transcription gives it another creature's stat block.",
                    $"The text was read from the JSON transcription in https://github.com/5e-bits/5e-srd-api (commit {SrdDownloader.Commit}), packages/5e-database, MIT License, Copyright (c) 2018-2020 Adrian Padua, Christopher Ward.",
                    "This pack is “5E compatible”. It is not affiliated with or endorsed by Wizards of the Coast."
                }
            }
        };
    }

    private string LegalText()
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

    // ---- Readers ----

    private JsonElement Root(string file)
    {
        if (!_documents.TryGetValue(file, out var doc))
        {
            var path = Path.Combine(sourceDirectory, $"5e-SRD-{file}.json");
            if (!File.Exists(path))
            {
                return JsonDocument.Parse("[]").RootElement;
            }

            doc = JsonDocument.Parse(File.ReadAllText(path));
            _documents[file] = doc;
        }

        return doc.RootElement;
    }

    private IEnumerable<JsonElement> All(string file) =>
        Root(file).ValueKind == JsonValueKind.Array ? Root(file).EnumerateArray() : Enumerable.Empty<JsonElement>();

    private Dictionary<string, JsonElement> ByIndex(string file) =>
        All(file).Where(e => e.Str("index") != null).ToDictionary(e => e.Str("index")!, e => e);

    private static ContentEntry Entry(string type, JsonElement element, string? description = null) => new()
    {
        Type = type,
        Id = element.Str("index") ?? throw new InvalidDataException($"A {type} record has no index."),
        Name = element.Str("name") ?? element.Str("index")!,
        Description = description ?? element.Text("description") ?? element.Text("desc")
    };

    private static List<object> FeatureRows(IEnumerable<(int? Level, string Name, string? Description)> rows) =>
        rows.Select(r =>
        {
            var map = new Dictionary<string, object> { ["name"] = r.Name };
            if (r.Level.HasValue) map["level"] = (long)r.Level.Value;
            if (!string.IsNullOrWhiteSpace(r.Description)) map["description"] = r.Description!;
            return (object)map;
        }).ToList();

    // ---- Classes ----

    private IEnumerable<ContentEntry> Classes()
    {
        var features = ByIndex("Features");
        var levelsByClass = All("Levels")
            .Where(l => l.Get("subclass") == null)
            .GroupBy(l => l.Get("class")?.Str("index") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Int("level") ?? 0).ToList());

        foreach (var cls in All("Classes"))
        {
            var id = cls.Str("index")!;
            var entry = Entry("class", cls);
            var hitDie = cls.Int("hit_die");
            var primary = cls.Get("primary_ability")?.Str("desc") ?? string.Join(" or ", cls.Names("primary_ability"));
            var saves = cls.Names("saving_throws");
            var proficiencies = cls.Names("proficiencies").Where(p => !p.StartsWith("Saving Throw", StringComparison.OrdinalIgnoreCase)).ToList();
            var armor = proficiencies.Where(p => p.Contains("armor", StringComparison.OrdinalIgnoreCase) || p.Equals("Shields", StringComparison.OrdinalIgnoreCase)).ToList();
            var weapons = proficiencies.Except(armor).ToList();
            var skillChoices = string.Join("; ", cls.Items("proficiency_choices").Select(c => c.Str("desc")).Where(s => s != null));
            var equipment = string.Join("\n", cls.Items("starting_equipment_options").Select(c => c.Str("desc")).Where(s => s != null));
            var subclasses = cls.Names("subclasses");

            var multiclass = cls.Get("multi_classing");
            var multiclassText = multiclass == null ? null : DescribeMulticlass(multiclass.Value);

            var rows = new List<(int? Level, string Name, string? Description)>();
            if (levelsByClass.TryGetValue(id, out var levels))
            {
                foreach (var level in levels)
                {
                    var levelNumber = level.Int("level");
                    foreach (var feature in level.Items("features"))
                    {
                        var featureIndex = feature.Str("index");
                        var description = featureIndex != null && features.TryGetValue(featureIndex, out var full) ? full.Text("description") : null;
                        rows.Add((levelNumber, feature.Str("name") ?? featureIndex ?? "Feature", description));
                    }
                }
            }

            entry.Properties["hitDie"] = hitDie.HasValue ? $"d{hitDie}" : "";
            entry.Properties["primaryAbility"] = primary ?? "";
            entry.Properties["savingThrows"] = saves.Cast<object>().ToList();
            entry.Properties["armorProficiencies"] = armor.Cast<object>().ToList();
            entry.Properties["weaponProficiencies"] = weapons.Cast<object>().ToList();
            entry.Properties["skillChoices"] = skillChoices;
            entry.Properties["startingEquipment"] = equipment;
            if (multiclassText != null) entry.Properties["multiclassing"] = multiclassText;
            if (multiclass != null) entry.Properties["multiclassPrerequisites"] = MulticlassPrerequisites(multiclass.Value);
            entry.Properties["subclasses"] = subclasses.Cast<object>().ToList();
            RenameRepeatedSubclassRows(rows, entry.Name);
            entry.Properties["features"] = FeatureRows(rows);
            if (SpellcastingAbilities.TryGetValue(id, out var castingAbility)) entry.Properties["spellcastingAbility"] = castingAbility;
            AddUnarmoredDefense(entry, rows);
            AddClassTables(entry, id);

            var sb = new StringBuilder();
            sb.AppendLine($"**Primary Ability:** {primary}  ");
            sb.AppendLine($"**Hit Point Die:** D{hitDie} per {entry.Name} level  ");
            sb.AppendLine($"**Saving Throw Proficiencies:** {string.Join(", ", saves)}  ");
            if (skillChoices.Length > 0) sb.AppendLine($"**Skill Proficiencies:** {skillChoices}  ");
            if (weapons.Count > 0) sb.AppendLine($"**Weapon Proficiencies:** {string.Join(", ", weapons)}  ");
            if (armor.Count > 0) sb.AppendLine($"**Armor Training:** {string.Join(", ", armor)}  ");
            if (equipment.Length > 0) sb.AppendLine($"**Starting Equipment:** {equipment}");
            entry.Description = sb.ToString().Trim();
            entry.Tags.Add("class");
            yield return entry;
        }
    }

    /// <summary>The SRD names each class's spellcasting ability in its Spellcasting feature; the sheet rules read it as data.</summary>
    private static readonly Dictionary<string, string> SpellcastingAbilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bard"] = "cha", ["cleric"] = "wis", ["druid"] = "wis", ["paladin"] = "cha", ["ranger"] = "wis",
        ["sorcerer"] = "cha", ["warlock"] = "cha", ["wizard"] = "int"
    };

    /// <summary>The SRD lists "Wizard Subclass" at 3, 6, 10 and 14 (and so on) with the level-3 text each time.
    /// Only the first is the choice; the others are where the chosen subclass gives features.</summary>
    private static void RenameRepeatedSubclassRows(List<(int? Level, string Name, string? Description)> rows, string className)
    {
        var seen = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Name.Equals($"{className} Subclass", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen)
            {
                seen = true;
                continue;
            }

            rows[i] = (rows[i].Level, "Subclass Feature", $"You gain a feature from your {className} subclass at this level. Its features are listed on the subclass.");
        }
    }

    private static readonly Regex UnarmoredText = new(@"base Armor Class equals 10 plus your (?<first>[A-Za-z]+) and (?<second>[A-Za-z]+) modifiers", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Dictionary<string, string> AbilityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = "str", ["Dexterity"] = "dex", ["Constitution"] = "con",
        ["Intelligence"] = "int", ["Wisdom"] = "wis", ["Charisma"] = "cha"
    };

    /// <summary>Reads a class's "base Armor Class equals 10 plus your X and Y modifiers" feature into
    /// unarmoredDefense (a formula) and whether a shield may still be used.</summary>
    private static void AddUnarmoredDefense(ContentEntry entry, List<(int? Level, string Name, string? Description)> rows)
    {
        foreach (var row in rows)
        {
            var match = UnarmoredText.Match(row.Description ?? string.Empty);
            if (!match.Success || !AbilityKeys.TryGetValue(match.Groups["first"].Value, out var first) || !AbilityKeys.TryGetValue(match.Groups["second"].Value, out var second)) continue;
            entry.Properties["unarmoredDefense"] = $"10 + mod({first}) + mod({second})";
            entry.Properties["unarmoredDefenseShield"] = (row.Description ?? string.Empty).Contains("can use a Shield", StringComparison.OrdinalIgnoreCase);
            return;
        }
    }

    /// <summary>Adds casterType, a spellcasting row table and a featSlots row table from the SRD class tables.</summary>
    private void AddClassTables(ContentEntry entry, string classId)
    {
        var tables = _classTables.Value;
        if (tables == null || tables.Value.Get("classes")?.Get(classId) is not { } cls) return;

        var casterType = cls.Str("casterType") ?? "none";
        entry.Properties["casterType"] = casterType;

        var spellcasting = new List<object>();
        var featSlots = new List<object>();
        var styles = 0L;
        foreach (var level in cls.Items("levels"))
        {
            var lvl = (long)(level.Int("level") ?? 0);
            // Fighting styles are their own allowance: the class feature that grants one is not an Ability Score Improvement.
            if (level.Items("features").Any(f => f.GetString() == "Fighting Style")) styles++;
            featSlots.Add(new Dictionary<string, object> { ["level"] = lvl, ["slots"] = (long)(level.Int("featSlots") ?? 0), ["styles"] = styles });
            if (casterType == "none") continue;
            var slots = level.Items("slots").Select(s => s.GetInt32()).ToList();
            spellcasting.Add(new Dictionary<string, object>
            {
                ["level"] = lvl,
                ["cantrips"] = (long)(level.Int("cantrips") ?? 0),
                ["preparedSpells"] = (long)(level.Int("preparedSpells") ?? 0),
                ["maxSpellLevel"] = (long)(level.Int("maxSpellLevel") ?? 0),
                ["slots"] = string.Join("/", slots)
            });
        }

        if (spellcasting.Count > 0) entry.Properties["spellcasting"] = spellcasting;
        entry.Properties["featSlots"] = featSlots;
    }

    private static string DescribeMulticlass(JsonElement multiclass)
    {
        var parts = new List<string>();
        var prerequisites = multiclass.Items("prerequisites").Select(p => $"{p.Name("ability_score")} {p.Int("minimum_score")}+").ToList();
        if (prerequisites.Count > 0) parts.Add("Prerequisite: " + string.Join(", ", prerequisites));
        var options = multiclass.Get("prerequisite_options");
        if (options != null)
        {
            var choices = options.Value.Get("from")?.Items("options").Select(o => $"{o.Name("ability_score")} {o.Int("minimum_score")}+").ToList() ?? new List<string>();
            if (choices.Count > 0) parts.Add("Prerequisite: " + string.Join(" or ", choices));
        }

        var proficiencies = multiclass.Names("proficiencies");
        if (proficiencies.Count > 0) parts.Add("Proficiencies gained: " + string.Join(", ", proficiencies));
        return string.Join(". ", parts);
    }

    /// <summary>The multiclass prerequisites as options the sheet rules can check: each option is
    /// "STR 13+" or "STR 13+, CHA 13+" (all of these), and meeting any one option is enough.</summary>
    private static List<object> MulticlassPrerequisites(JsonElement multiclass)
    {
        var all = multiclass.Items("prerequisites").Select(p => $"{p.Name("ability_score")} {p.Int("minimum_score")}+").ToList();
        var any = multiclass.Get("prerequisite_options")?.Get("from")?.Items("options").Select(o => $"{o.Name("ability_score")} {o.Int("minimum_score")}+").ToList() ?? new List<string>();
        if (any.Count == 0) return all.Count == 0 ? new List<object>() : new List<object> { string.Join(", ", all) };
        return any.Select(option => (object)string.Join(", ", all.Append(option))).ToList();
    }

    // ---- Subclasses ----

    private IEnumerable<ContentEntry> Subclasses()
    {
        foreach (var sub in All("Subclasses"))
        {
            var entry = Entry("subclass", sub);
            entry.Properties["class"] = sub.Get("class")?.Str("index") ?? "";
            entry.Properties["summary"] = sub.Str("summary") ?? "";
            var rows = sub.Items("features").Select(f => (f.Int("level"), f.Str("name") ?? "Feature", Json.Clean(f.Str("description")))).ToList();
            entry.Properties["features"] = FeatureRows(rows);
            var extraStyles = rows.Where(r => r.Item2.Equals("Additional Fighting Style", StringComparison.OrdinalIgnoreCase) && r.Item1.HasValue).ToList();
            if (extraStyles.Count > 0)
            {
                entry.Properties["featSlots"] = Enumerable.Range(1, 20)
                    .Select(lvl => (object)new Dictionary<string, object> { ["level"] = (long)lvl, ["styles"] = (long)extraStyles.Count(r => r.Item1 <= lvl) })
                    .ToList();
            }

            if (sub.Str("summary") is { } summary)
            {
                entry.Description = $"*{summary}*\n\n{entry.Description}";
            }

            entry.Tags.Add(sub.Get("class")?.Str("index") ?? "subclass");
            yield return entry;
        }
    }

    // ---- Feats ----

    private IEnumerable<ContentEntry> Feats()
    {
        foreach (var feat in All("Feats"))
        {
            var entry = Entry("feat", feat);
            var category = feat.Str("type") ?? "general";
            entry.Properties["category"] = category;
            var prereqs = new List<string>();
            if (feat.Get("prerequisites")?.Int("minimum_level") is { } level) prereqs.Add($"Level {level}+");
            if (feat.Get("prerequisite_options")?.Str("desc") is { } desc) prereqs.Add(desc);
            foreach (var p in feat.Items("prerequisites"))
            {
                if (p.Str("desc") is { } d) prereqs.Add(d);
            }

            if (prereqs.Count > 0) entry.Properties["prerequisites"] = string.Join("; ", prereqs);
            if (feat.Bool("repeatable") is { } repeatable) entry.Properties["repeatable"] = repeatable;
            // The level a feat can first be taken: stated for general feats, level 19 for epic boons
            // (the SRD grants them at 19), level 1 otherwise (origin feats, fighting styles via class features).
            entry.Properties["minLevel"] = (long)(feat.Get("prerequisites")?.Int("minimum_level") ?? (category == "epic-boon" ? 19 : 1));
            AddAbilityIncrease(entry);
            AddArmorClassBonus(entry);
            // Every class's Ability Score Improvement feature grants this feat again, so the sheet must
            // allow it more than once even though the source data doesn't flag it.
            if (entry.Id == "ability-score-improvement") entry.Properties["repeatable"] = true;
            entry.Tags.Add(category);
            yield return entry;
        }
    }

    private static readonly Regex ArmorBonusText = new(@"^While you.re wearing (?<armor>[^.]*?armor),? you gain a \+(?<bonus>\d+) bonus to Armor Class", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads "While you're wearing ... armor, you gain a +N bonus to Armor Class" (the Defense
    /// fighting style) into armorClassBonus, which applies only with armor on.</summary>
    private static void AddArmorClassBonus(ContentEntry entry)
    {
        var match = ArmorBonusText.Match(entry.Description ?? string.Empty);
        if (!match.Success) return;
        entry.Properties["armorClassBonus"] = long.Parse(match.Groups["bonus"].Value, CultureInfo.InvariantCulture);
        entry.Properties["armorClassRequiresArmor"] = true;
    }

    private static readonly Regex IncreaseText = new(@"Increase (?:your (?<options>[A-Za-z, ]+?) score|one ability score of your choice) by (?<points>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncreaseCap = new(@"(?:to a maximum of|above) (?<cap>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads a feat's "Increase ... by N, to a maximum of M" into abilityIncrease (points),
    /// abilityIncreaseOptions (empty: any ability) and abilityIncreaseCap, for the sheet rules.</summary>
    private static void AddAbilityIncrease(ContentEntry entry)
    {
        var match = IncreaseText.Match(entry.Description ?? string.Empty);
        if (!match.Success) return;
        entry.Properties["abilityIncrease"] = long.Parse(match.Groups["points"].Value, CultureInfo.InvariantCulture);
        var options = match.Groups["options"].Success
            ? Regex.Split(match.Groups["options"].Value, @",|\bor\b").Select(o => o.Trim()).Where(o => o.Length > 0).Cast<object>().ToList()
            : new List<object>();
        if (options.Count > 0) entry.Properties["abilityIncreaseOptions"] = options;
        var cap = IncreaseCap.Match(entry.Description![match.Index..]);
        if (cap.Success) entry.Properties["abilityIncreaseCap"] = long.Parse(cap.Groups["cap"].Value, CultureInfo.InvariantCulture);
    }

    // ---- Species (2024) / Races (2014) ----

    private IEnumerable<ContentEntry> Species()
    {
        var traits = ByIndex("Traits");
        var subspecies = All("Subspecies").GroupBy(s => s.Get("species")?.Str("index") ?? "").ToDictionary(g => g.Key, g => g.ToList());

        foreach (var species in All("Species"))
        {
            var entry = Entry("species", species);
            entry.Properties["creatureType"] = species.Str("type") ?? "Humanoid";
            entry.Properties["size"] = species.Str("size") ?? "";
            if (species.Int("speed") is { } speed) entry.Properties["speed"] = (long)speed;

            var traitRows = species.Items("traits")
                .Select(t => (Level: (int?)null, Name: t.Str("name") ?? "", Description: t.Str("index") is { } idx && traits.TryGetValue(idx, out var full) ? full.Text("description") : null))
                .ToList();
            entry.Properties["traits"] = FeatureRows(traitRows);
            AddSpeciesValues(entry, traitRows);

            if (subspecies.TryGetValue(entry.Id, out var lineages))
            {
                entry.Properties["lineages"] = lineages.Select(l =>
                {
                    var map = new Dictionary<string, object> { ["name"] = l.Str("name") ?? "" };
                    var lineageTraits = l.Items("traits")
                        .Select(t => t.Str("index") is { } idx && traits.TryGetValue(idx, out var full) ? $"**{full.Str("name")}.** {full.Text("description")}" : t.Str("name"))
                        .Where(s => s != null);
                    var text = string.Join("\n", lineageTraits);
                    if (l.Name("damage_type") is { } damage) text = $"Damage type: {damage}\n{text}";
                    if (text.Length > 0) map["description"] = text;
                    return (object)map;
                }).ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Creature Type:** {entry.Properties["creatureType"]}  ");
            sb.AppendLine($"**Size:** {entry.Properties["size"]}  ");
            if (species.Int("speed") is { } spd) sb.AppendLine($"**Speed:** {spd} feet");
            sb.AppendLine();
            foreach (var trait in traitRows)
            {
                sb.AppendLine($"**{trait.Name}.** {trait.Description}");
                sb.AppendLine();
            }

            entry.Description = sb.ToString().Trim();
            yield return entry;
        }
    }

    private static readonly Regex DarkvisionRange = new(@"(?<feet>\d+)\s*(?:feet|ft)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Numbers the sheet rules read off a species' traits: Darkvision's range, a per-level hit point
    /// bonus (Dwarven Toughness), the extra origin feat of Versatile, and a size when the source gives none.</summary>
    private static void AddSpeciesValues(ContentEntry entry, List<(int? Level, string Name, string? Description)> traits)
    {
        foreach (var trait in traits)
        {
            if (trait.Name.StartsWith("Darkvision", StringComparison.OrdinalIgnoreCase))
            {
                var range = DarkvisionRange.Match(trait.Name + " " + trait.Description);
                if (range.Success) entry.Properties["darkvision"] = long.Parse(range.Groups["feet"].Value, CultureInfo.InvariantCulture);
            }
            else if (trait.Name.Equals("Dwarven Toughness", StringComparison.OrdinalIgnoreCase))
            {
                entry.Properties["hitPointsPerLevel"] = 1L;
            }
            else if (trait.Name.Equals("Versatile", StringComparison.OrdinalIgnoreCase) && (trait.Description ?? "").Contains("Origin feat", StringComparison.OrdinalIgnoreCase))
            {
                entry.Properties["extraOriginFeats"] = 1L;
            }
        }

        // Some species choose Medium or Small and the source leaves it blank; start from Medium.
        if (string.IsNullOrWhiteSpace(entry.Properties.GetValueOrDefault("size")?.ToString())) entry.Properties["size"] = "Medium";
    }

    private IEnumerable<ContentEntry> Races()
    {
        var traits = ByIndex("Traits");
        foreach (var race in All("Races"))
        {
            var entry = Entry("species", race, race.Text("alignment"));
            entry.Properties["size"] = race.Str("size") ?? "";
            if (race.Int("speed") is { } speed) entry.Properties["speed"] = (long)speed;
            var traitRows = race.Items("traits")
                .Select(t => ((int?)null, t.Str("name") ?? "", t.Str("index") is { } idx && traits.TryGetValue(idx, out var full) ? full.Text("desc") : null))
                .ToList();
            entry.Properties["traits"] = FeatureRows(traitRows);
            yield return entry;
        }
    }

    // ---- Backgrounds ----

    private IEnumerable<ContentEntry> Backgrounds()
    {
        foreach (var background in All("Backgrounds"))
        {
            var entry = Entry("background", background);
            var abilities = background.Names("ability_scores");
            var feat = background.Get("feat");
            var proficiencies = background.Names("proficiencies");
            var skills = proficiencies.Where(p => p.StartsWith("Skill:", StringComparison.OrdinalIgnoreCase)).Select(p => p[6..].Trim()).ToList();
            var tool = proficiencies.FirstOrDefault(p => p.StartsWith("Tool:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
            var equipment = string.Join("\n", background.Items("equipment_options").Select(o => o.Str("desc")).Where(s => s != null));

            entry.Properties["abilityScores"] = abilities.Cast<object>().ToList();
            if (feat != null) entry.Properties["feat"] = feat.Value.Str("index") ?? "";
            entry.Properties["skillProficiencies"] = skills.Cast<object>().ToList();
            if (tool != null) entry.Properties["toolProficiency"] = tool;
            entry.Properties["equipment"] = equipment;

            var featLabel = feat == null ? null : feat.Value.Str("name") + (feat.Value.Str("note") is { } note ? $" ({note})" : "");
            var sb = new StringBuilder();
            if (abilities.Count > 0) sb.AppendLine($"**Ability Scores:** {string.Join(", ", abilities)}  ");
            if (featLabel != null) sb.AppendLine($"**Feat:** {featLabel}  ");
            if (skills.Count > 0) sb.AppendLine($"**Skill Proficiencies:** {string.Join(", ", skills)}  ");
            if (tool != null) sb.AppendLine($"**Tool Proficiency:** {tool}  ");
            if (equipment.Length > 0) sb.AppendLine($"**Equipment:** {equipment}");
            entry.Description = sb.ToString().Trim();
            yield return entry;
        }
    }

    // ---- Spells ----

    private static readonly Regex SpellRangeWithComponents = new(@"^(?<range>.+?)\s+Components?:\s*(?<components>[VSM](?:,\s*[VSM])*)$", RegexOptions.Compiled);

    private IEnumerable<ContentEntry> Spells()
    {
        foreach (var spell in All("Spells"))
        {
            var entry = Entry("spell", spell);
            var level = spell.Int("level") ?? 0;
            var school = spell.Name("school") ?? "";
            var classes = spell.Names("classes");
            var components = spell.Names("components");

            entry.Properties["level"] = (long)level;
            entry.Properties["school"] = school;
            entry.Properties["castingTime"] = spell.Str("casting_time") ?? "";
            // The source sometimes runs "Touch Component: V, S" together in the range and leaves components empty.
            var range = spell.Str("range") ?? "";
            var glued = SpellRangeWithComponents.Match(range);
            if (glued.Success)
            {
                range = glued.Groups["range"].Value;
                if (components.Count == 0) components = glued.Groups["components"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            entry.Properties["range"] = range;
            entry.Properties["components"] = components.Cast<object>().ToList();
            if (spell.Str("material") is { } material) entry.Properties["material"] = material;
            entry.Properties["duration"] = spell.Str("duration") ?? "";
            entry.Properties["concentration"] = spell.Bool("concentration") ?? false;
            entry.Properties["ritual"] = spell.Bool("ritual") ?? false;
            entry.Properties["classes"] = classes.Cast<object>().ToList();
            if (spell.Get("damage")?.Name("damage_type") is { } damageType) entry.Properties["damageType"] = damageType;
            if (spell.Text("higher_level") is { } higher) entry.Properties["higherLevel"] = higher;

            var header = level == 0 ? $"{school} Cantrip" : $"Level {level} {school}";
            var sb = new StringBuilder();
            sb.AppendLine($"*{header} ({string.Join(", ", classes)})*");
            sb.AppendLine();
            sb.AppendLine($"**Casting Time:** {entry.Properties["castingTime"]}{((spell.Bool("ritual") ?? false) ? " or Ritual" : "")}  ");
            sb.AppendLine($"**Range:** {entry.Properties["range"]}  ");
            sb.AppendLine($"**Components:** {string.Join(", ", components)}{(spell.Str("material") is { } m ? $" ({m})" : "")}  ");
            sb.AppendLine($"**Duration:** {((spell.Bool("concentration") ?? false) ? "Concentration, " : "")}{entry.Properties["duration"]}");
            sb.AppendLine();
            sb.AppendLine(spell.Text("description"));
            if (spell.Text("higher_level") is { } hl)
            {
                sb.AppendLine();
                sb.AppendLine(level == 0 ? $"**Cantrip Upgrade.** {hl}" : $"**Using a Higher-Level Spell Slot.** {hl}");
            }

            entry.Description = sb.ToString().Trim();
            entry.Tags.Add(level == 0 ? "cantrip" : $"level-{level}");
            entry.Tags.Add(school.ToLowerInvariant());
            entry.Tags.AddRange(classes.Select(c => c.ToLowerInvariant()));
            yield return entry;
        }
    }

    // ---- Monsters ----

    private IEnumerable<ContentEntry> Monsters()
    {
        foreach (var monster in All("Monsters"))
        {
            // The source transcription gives the Mule the Octopus's stat block, so it is left out.
            if (monster.Str("index") == "mule") continue;
            var entry = Entry("monster", monster);
            var size = monster.Str("size") ?? "";
            var type = monster.Str("type") ?? "";
            var alignment = monster.Str("alignment") ?? "";
            var ac = monster.Items("armor_class").FirstOrDefault();
            var acValue = ac.ValueKind == JsonValueKind.Object ? ac.Int("value") : null;
            var acNote = ac.ValueKind == JsonValueKind.Object ? string.Join(", ", ac.Names("armor")) : "";
            var hp = monster.Int("hit_points");
            var hpRoll = monster.Str("hit_points_roll") ?? monster.Str("hit_dice");
            var speed = monster.Get("speed") is { } sp ? string.Join(", ", sp.EnumerateObject().Select(p => p.Name == "walk" ? p.Value.ToString() : $"{p.Name} {p.Value}")) : "";
            var cr = monster.Dbl("challenge_rating") ?? 0;
            var senses = monster.Get("senses") is { } sn ? string.Join(", ", sn.EnumerateObject().Select(p => $"{Humanize(p.Name)} {p.Value}")) : "";
            var saves = monster.Items("proficiencies").Where(p => p.Get("proficiency")?.Str("index")?.StartsWith("saving-throw") == true)
                .Select(p => $"{p.Get("proficiency")!.Value.Str("name")![14..].Trim()} +{p.Int("value")}").ToList();
            var skills = monster.Items("proficiencies").Where(p => p.Get("proficiency")?.Str("index")?.StartsWith("skill") == true)
                .Select(p => $"{p.Get("proficiency")!.Value.Str("name")![6..].Trim()} +{p.Int("value")}").ToList();
            var resistances = monster.Names("damage_resistances");
            var immunities = monster.Names("damage_immunities").Concat(monster.Names("condition_immunities")).ToList();
            var vulnerabilities = monster.Names("damage_vulnerabilities");

            entry.Properties["size"] = size;
            entry.Properties["creatureType"] = type;
            entry.Properties["alignment"] = alignment;
            if (acValue.HasValue) entry.Properties["armorClass"] = (long)acValue.Value;
            if (hp.HasValue) entry.Properties["hitPoints"] = (long)hp.Value;
            if (hpRoll != null) entry.Properties["hitDice"] = hpRoll;
            entry.Properties["speed"] = speed;
            foreach (var ability in new[] { "strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma" })
            {
                if (monster.Int(ability) is { } score) entry.Properties[ability] = (long)score;
            }

            entry.Properties["challengeRating"] = Json.FormatCr(cr);
            if (monster.Int("xp") is { } xp) entry.Properties["xp"] = (long)xp;
            entry.Properties["senses"] = senses;
            entry.Properties["languages"] = monster.Str("languages") ?? "";
            if (skills.Count > 0) entry.Properties["skills"] = string.Join(", ", skills);
            if (saves.Count > 0) entry.Properties["savingThrows"] = string.Join(", ", saves);
            if (resistances.Count > 0) entry.Properties["resistances"] = resistances.Cast<object>().ToList();
            if (immunities.Count > 0) entry.Properties["immunities"] = immunities.Cast<object>().ToList();
            if (vulnerabilities.Count > 0) entry.Properties["vulnerabilities"] = vulnerabilities.Cast<object>().ToList();
            if (monster.Str("gear") is { } gear) entry.Properties["gear"] = gear;

            var traits = ActionRows(monster, "special_abilities");
            var actions = ActionRows(monster, "actions");
            var bonusActions = ActionRows(monster, "bonus_actions");
            var reactions = ActionRows(monster, "reactions");
            var legendary = ActionRows(monster, "legendary_actions");
            if (traits.Count > 0) entry.Properties["traits"] = FeatureRows(traits);
            if (actions.Count > 0) entry.Properties["actions"] = FeatureRows(actions);
            if (bonusActions.Count > 0) entry.Properties["bonusActions"] = FeatureRows(bonusActions);
            if (reactions.Count > 0) entry.Properties["reactions"] = FeatureRows(reactions);
            if (legendary.Count > 0) entry.Properties["legendaryActions"] = FeatureRows(legendary);

            var sb = new StringBuilder();
            sb.AppendLine($"*{size} {Capitalize(type)}, {Capitalize(alignment)}*");
            sb.AppendLine();
            sb.AppendLine($"**AC** {acValue}{(acNote.Length > 0 ? $" ({acNote})" : "")}  ");
            sb.AppendLine($"**HP** {hp}{(hpRoll != null ? $" ({hpRoll})" : "")}  ");
            sb.AppendLine($"**Speed** {speed}  ");
            sb.AppendLine($"**CR** {Json.FormatCr(cr)} (XP {monster.Int("xp")}; PB +{monster.Int("proficiency_bonus")})");
            sb.AppendLine();
            sb.AppendLine("| STR | DEX | CON | INT | WIS | CHA |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            sb.AppendLine($"| {Score(monster, "strength")} | {Score(monster, "dexterity")} | {Score(monster, "constitution")} | {Score(monster, "intelligence")} | {Score(monster, "wisdom")} | {Score(monster, "charisma")} |");
            sb.AppendLine();
            if (saves.Count > 0) sb.AppendLine($"**Saving Throws** {string.Join(", ", saves)}  ");
            if (skills.Count > 0) sb.AppendLine($"**Skills** {string.Join(", ", skills)}  ");
            if (resistances.Count > 0) sb.AppendLine($"**Resistances** {string.Join(", ", resistances)}  ");
            if (immunities.Count > 0) sb.AppendLine($"**Immunities** {string.Join(", ", immunities)}  ");
            if (vulnerabilities.Count > 0) sb.AppendLine($"**Vulnerabilities** {string.Join(", ", vulnerabilities)}  ");
            if (monster.Str("gear") is { } g) sb.AppendLine($"**Gear** {g}  ");
            sb.AppendLine($"**Senses** {senses}  ");
            sb.AppendLine($"**Languages** {monster.Str("languages")}");
            AppendSection(sb, "Traits", traits);
            AppendSection(sb, "Actions", actions);
            AppendSection(sb, "Bonus Actions", bonusActions);
            AppendSection(sb, "Reactions", reactions);
            AppendSection(sb, "Legendary Actions", legendary);

            entry.Description = sb.ToString().Trim();
            entry.Tags.Add(type.ToLowerInvariant());
            entry.Tags.Add(size.ToLowerInvariant());
            entry.Tags.Add($"cr-{Json.FormatCr(cr)}");
            yield return entry;
        }
    }

    private static List<(int? Level, string Name, string? Description)> ActionRows(JsonElement monster, string property) =>
        monster.Items(property)
            .Select(a =>
            {
                var usage = a.Get("usage");
                var name = a.Str("name") ?? "";
                if (usage != null)
                {
                    var times = usage.Value.Int("times");
                    var kind = usage.Value.Str("type");
                    if (times.HasValue && kind != null) name += $" ({times}/{kind.Replace("per ", "")})";
                }

                return ((int?)null, name, Json.Clean(a.Str("desc")));
            })
            .ToList();

    private static void AppendSection(StringBuilder sb, string title, List<(int? Level, string Name, string? Description)> rows)
    {
        if (rows.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"### {title}");
        foreach (var row in rows)
        {
            sb.AppendLine($"**{row.Name}.** {row.Description}");
            sb.AppendLine();
        }
    }

    private static string Score(JsonElement monster, string ability)
    {
        var value = monster.Int(ability) ?? 10;
        var modifier = (int)Math.Floor((value - 10) / 2.0);
        return $"{value} ({(modifier >= 0 ? "+" : "")}{modifier})";
    }

    // ---- Conditions ----

    private IEnumerable<ContentEntry> Conditions()
    {
        foreach (var condition in All("Conditions"))
        {
            var entry = Entry("condition", condition);
            yield return entry;
        }
    }

    /// <summary>Typed armor data for the sheet rules: armorType, armorBase (body armor) or armorClassBonus
    /// (shields), and armorDexCap (absent: uncapped; 0: no Dexterity).</summary>
    private static void AddArmorData(ContentEntry entry, List<string> categories, JsonElement armor)
    {
        var baseValue = armor.Int("base");
        if (!baseValue.HasValue) return;
        var names = categories.Select(c => c.ToLowerInvariant()).ToList();
        var type = names.Contains("shields") ? "shield"
            : names.Contains("heavy armor") ? "heavy"
            : names.Contains("medium armor") ? "medium"
            : "light";
        entry.Properties["armorType"] = type;
        if (type == "shield")
        {
            entry.Properties["armorClassBonus"] = (long)baseValue.Value;
            return;
        }

        entry.Properties["armorBase"] = (long)baseValue.Value;
        if (armor.Bool("dex_bonus") != true) entry.Properties["armorDexCap"] = 0L;
        else if (armor.Int("max_bonus") is { } max and > 0) entry.Properties["armorDexCap"] = (long)max;
    }

    // ---- Equipment ----

    private IEnumerable<ContentEntry> Equipment()
    {
        foreach (var item in All("Equipment"))
        {
            var entry = Entry("equipment", item);
            var categories = item.Names("equipment_categories");
            if (item.Name("equipment_category") is { } single) categories.Add(single);
            var category = categories.FirstOrDefault() ?? "";
            entry.Properties["category"] = category;
            if (item.Get("cost") is { } cost) entry.Properties["cost"] = $"{cost.Str("quantity")} {cost.Str("unit")}".Trim();
            if (item.Dbl("weight") is { } weight) entry.Properties["weight"] = weight % 1 == 0 ? (long)weight : weight;

            var lines = new List<string>();
            if (item.Get("damage") is { } damage)
            {
                var text = $"{damage.Str("damage_dice")} {damage.Name("damage_type")}".Trim();
                if (item.Get("two_handed_damage") is { } two) text += $" (two-handed: {two.Str("damage_dice")})";
                entry.Properties["damage"] = text;
                lines.Add($"**Damage:** {text}");
            }

            var properties = item.Names("properties");
            if (properties.Count > 0)
            {
                entry.Properties["properties"] = properties.Cast<object>().ToList();
                lines.Add($"**Properties:** {string.Join(", ", properties)}");
            }

            if (item.Get("range") is { } range)
            {
                var normal = range.Int("normal");
                var @long = range.Int("long");
                if (@long.HasValue) lines.Add($"**Range:** {normal}/{@long} ft.");
            }

            if (item.Name("mastery") is { } mastery)
            {
                entry.Properties["mastery"] = mastery;
                lines.Add($"**Mastery:** {mastery}");
            }

            if (item.Get("armor_class") is { } armor)
            {
                var text = armor.Int("base")?.ToString() ?? "";
                if (armor.Bool("dex_bonus") == true) text += armor.Int("max_bonus") is { } max and > 0 ? $" + Dex modifier (max {max})" : " + Dex modifier";
                entry.Properties["armorClass"] = text;
                lines.Add($"**Armor Class:** {text}");
                // The SRD lists Hide Armor as Medium; the upstream data files it under Light.
                if (entry.Id == "hide-armor") categories = categories.Select(c => c.Replace("Light", "Medium")).ToList();
                AddArmorData(entry, categories, armor);
            }

            if (item.Int("str_minimum") is { } strMin and > 0)
            {
                entry.Properties["strength"] = (long)strMin;
                lines.Add($"**Strength:** {strMin}");
            }

            if (item.Bool("stealth_disadvantage") == true)
            {
                entry.Properties["stealth"] = true;
                lines.Add("**Stealth:** Disadvantage");
            }

            if (item.Get("cost") is { } c) lines.Add($"**Cost:** {entry.Properties["cost"]}");
            if (entry.Properties.TryGetValue("weight", out var w)) lines.Add($"**Weight:** {w} lb.");

            var sb = new StringBuilder();
            sb.AppendLine($"*{string.Join(", ", categories.Distinct())}*");
            sb.AppendLine();
            sb.AppendLine(string.Join("  \n", lines));
            var description = item.Text("description") ?? item.Text("desc");
            if (description != null)
            {
                sb.AppendLine();
                sb.AppendLine(description);
            }

            var notes = item.Text("notes");
            if (notes != null)
            {
                sb.AppendLine();
                sb.AppendLine(notes);
            }

            entry.Description = sb.ToString().Trim();
            entry.Tags.AddRange(categories.Select(c => c.ToLowerInvariant()).Distinct());
            yield return entry;
        }

        foreach (var poison in All("Poisons"))
        {
            var entry = Entry("equipment", poison);
            entry.Properties["category"] = "Poison";
            if (poison.Int("cost") is { } cost) entry.Properties["cost"] = $"{cost} gp";
            var kind = poison.Str("type");
            entry.Description = $"*Poison ({kind})*\n\n**Cost:** {entry.Properties.GetValueOrDefault("cost")}\n\n{poison.Text("description")}";
            entry.Tags.Add("poison");
            if (kind != null) entry.Tags.Add(kind.ToLowerInvariant());
            yield return entry;
        }
    }

    // ---- Magic items ----

    private static readonly Regex ProtectionText = new(@"\+(?<bonus>\d+) bonus to (?:AC|Armor Class) and saving throws", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private IEnumerable<ContentEntry> MagicItems()
    {
        foreach (var item in All("Magic-Items"))
        {
            if (item.Bool("variant") == true) continue; // variants are listed under their parent

            var entry = Entry("magic-item", item);
            var category = item.Name("equipment_category") ?? "";
            var rarity = item.Get("rarity")?.Str("name") ?? "";
            entry.Properties["category"] = category;
            entry.Properties["rarity"] = rarity;
            if (item.Bool("attunement") is { } attunement) entry.Properties["attunement"] = attunement;
            var variants = item.Names("variants");
            if (variants.Count > 0) entry.Properties["variants"] = variants.Cast<object>().ToList();

            if (ProtectionText.Match(entry.Description ?? string.Empty) is { Success: true } protection)
            {
                entry.Properties["armorClassBonus"] = long.Parse(protection.Groups["bonus"].Value, CultureInfo.InvariantCulture);
            }

            entry.Tags.Add(rarity.ToLowerInvariant());
            entry.Tags.Add(category.ToLowerInvariant());
            yield return entry;
        }
    }

    // ---- Rules glossary ----

    private IEnumerable<ContentEntry> Rules()
    {
        foreach (var skill in All("Skills"))
        {
            var entry = Entry("rule", skill);
            entry.Id = "skill-" + entry.Id;
            entry.Properties["category"] = "Skill";
            entry.Description = $"**Ability:** {skill.Name("ability_score")}\n\n{skill.Text("description")}";
            entry.Tags.Add("skill");
            yield return entry;
        }

        foreach (var (file, category) in new[] { ("Damage-Types", "Damage Type"), ("Weapon-Properties", "Weapon Property"), ("Weapon-Mastery-Properties", "Weapon Mastery"), ("Magic-Schools", "School of Magic"), ("Alignments", "Alignment"), ("Ability-Scores", "Ability Score") })
        {
            foreach (var item in All(file))
            {
                var entry = Entry("rule", item);
                entry.Id = $"{category.ToLowerInvariant().Replace(' ', '-')}-{entry.Id}";
                if (item.Str("full_name") is { } fullName) entry.Name = fullName;
                entry.Properties["category"] = category;
                entry.Tags.Add(category.ToLowerInvariant().Replace(' ', '-'));
                yield return entry;
            }
        }

        foreach (var language in All("Languages"))
        {
            var entry = Entry("rule", language, language.Str("note") ?? (language.Bool("is_rare") == true ? "A rare language." : "A standard language."));
            entry.Id = "language-" + entry.Id;
            entry.Properties["category"] = "Language";
            entry.Tags.Add("language");
            yield return entry;
        }
    }

    private static string Humanize(string key) => key.Replace('_', ' ');

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
}
