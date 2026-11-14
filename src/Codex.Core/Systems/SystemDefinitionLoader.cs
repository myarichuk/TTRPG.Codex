using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Core.Systems;

/// <summary>Reads and validates a <c>system.yaml</c> into a <see cref="SystemDefinition"/>.</summary>
public static class SystemDefinitionLoader
{
    public const string FileName = "system.yaml";

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-_]*$", RegexOptions.Compiled);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new RowChooseConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The key under which a thrown <see cref="InvalidDataException"/> carries its problems, one sentence each
    /// (a <c>string[]</c> in <see cref="Exception.Data"/>), so a caller can list them without parsing the message.</summary>
    public const string ErrorsKey = "errors";

    public static SystemDefinition Load(string path) => Load(path, null);

    /// <param name="unknownKeys">Receives the keys the file has and this version does not read (see <see cref="UnknownYamlKeys"/>).</param>
    public static SystemDefinition Load(string path, ICollection<string>? unknownKeys)
    {
        var yaml = File.ReadAllText(path);
        // Messages name the system's folder, not the full path: they are shown to people who don't run the server.
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)));
        var definition = Parse(yaml, string.IsNullOrEmpty(folder) ? FileName : $"{folder}/{FileName}", unknownKeys);
        LoadTranslations(definition, path);
        LoadScripts(definition, path);
        // Compiled code is found only here, beside a system.yaml on the server's disk: Parse alone never loads a DLL.
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory) Rules.CompiledSuppliers.Resolve(definition, directory);
        definition.SetFingerprint(FingerprintOf(definition, path));
        return definition;
    }

    /// <summary>
    /// The version and the hash of each file the system's rules come from (overhaul section 4, rule 8): system.yaml, each
    /// translation that loaded, each script that loaded and every DLL beside system.yaml, whether or not system.yaml names it
    /// (a dependency changes what a named type does).
    /// </summary>
    public static SystemFingerprint FingerprintOf(SystemDefinition definition, string systemYamlPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(systemYamlPath))!;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) { [FileName] = SystemFingerprint.Hash(systemYamlPath) };
        foreach (var locale in definition.Translations.Keys)
        {
            var file = Path.Combine(directory, $"system.{locale}.yaml");
            if (File.Exists(file)) files[$"system.{locale}.yaml"] = SystemFingerprint.Hash(file);
        }

        foreach (var (name, source) in definition.Scripts) files[name.Replace('\\', '/')] = SystemFingerprint.HashText(source);
        foreach (var dll in Directory.GetFiles(directory, "*.dll")) files[Path.GetFileName(dll)] = SystemFingerprint.Hash(dll);
        return new SystemFingerprint(definition.Version, files);
    }

    /// <summary>The largest script file a system may have.</summary>
    public const int MaxScriptBytes = 256 * 1024;

    /// <summary>
    /// Reads each script file the system names (<c>{ script: rules.js#export }</c>, in <c>functions</c> and <c>events</c>) from
    /// the folder of <paramref name="systemYamlPath"/> into <see cref="SystemDefinition.Scripts"/>. Only a <c>.js</c> file in that
    /// folder or below it, at most <see cref="MaxScriptBytes"/>; one that is not is left out, and
    /// <see cref="Rules.SystemScripts.Problems"/> says so, as for a file that is not there.
    /// </summary>
    private static void LoadScripts(SystemDefinition definition, string systemYamlPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(systemYamlPath));
        if (directory == null) return;
        var root = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in Rules.SystemScripts.FilesNamed(definition))
        {
            if (!file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(file)) continue;
            var full = Path.GetFullPath(Path.Combine(directory, file));
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) continue;
            if (new FileInfo(full).Length > MaxScriptBytes) continue;
            definition.AddScript(file, File.ReadAllText(full));
        }
    }

    /// <summary>
    /// Reads <c>system.&lt;locale&gt;.yaml</c> files next to <paramref name="systemYamlPath"/> (system.ru.yaml, system.he.yaml)
    /// into <see cref="SystemDefinition.Translations"/>. A broken translation is skipped rather than failing the system:
    /// the language falls back to the original text.
    /// </summary>
    private static void LoadTranslations(SystemDefinition definition, string systemYamlPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(systemYamlPath));
        if (directory == null) return;

        foreach (var file in Directory.GetFiles(directory, "system.*.yaml"))
        {
            var locale = Path.GetFileNameWithoutExtension(file)["system.".Length..].ToLowerInvariant();
            if (locale.Length is < 2 or > 3 || !locale.All(char.IsLetter)) continue;

            try
            {
                definition.Translations[locale] = Deserializer.Deserialize<SystemTranslation>(File.ReadAllText(file)) ?? new SystemTranslation();
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException)
            {
                System.Diagnostics.Trace.TraceWarning($"Skipped translation {file}: {Describe(ex)}");
            }
        }
    }

    public static SystemDefinition Parse(string yaml, string? sourceName = null) => Parse(yaml, sourceName, null);

    /// <param name="unknownKeys">Receives the keys the file has and this version does not read, as dotted paths. They are
    /// ignored either way (a file written for a newer version still loads); this lets the caller say so.</param>
    public static SystemDefinition Parse(string yaml, string? sourceName, ICollection<string>? unknownKeys)
    {
        SystemDefinition? definition;
        try
        {
            definition = Deserializer.Deserialize<SystemDefinition>(yaml);
        }
        catch (Exception ex)
        {
            throw WithErrors(new InvalidDataException($"{sourceName ?? "system.yaml"} is not a valid system definition: {Describe(ex)}", ex),
                $"not a valid system definition: {Describe(ex)}");
        }

        if (definition == null)
        {
            throw WithErrors(new InvalidDataException($"{sourceName ?? "system.yaml"} is empty."), "the file is empty.");
        }

        var errors = Validate(definition).ToList();
        if (errors.Count > 0)
        {
            throw WithErrors(new InvalidDataException($"{sourceName ?? "system.yaml"} is invalid:\n - " + string.Join("\n - ", errors)), errors.ToArray());
        }

        if (unknownKeys != null)
        {
            foreach (var key in UnknownYamlKeys.Find(yaml, typeof(SystemDefinition), CamelCaseNamingConvention.Instance)) unknownKeys.Add(key);
        }

        return definition;
    }

    private static InvalidDataException WithErrors(InvalidDataException ex, params string[] errors)
    {
        ex.Data[ErrorsKey] = errors;
        return ex;
    }

    /// <summary>The problems a failed <see cref="Parse"/> or <see cref="Load"/> found, one sentence each.</summary>
    public static IReadOnlyList<string> ErrorsOf(Exception ex) =>
        ex.Data[ErrorsKey] as string[] ?? new[] { ex.Message };

    /// <summary>Every problem that would make the definition unusable, as plain sentences.</summary>
    public static IEnumerable<string> Validate(SystemDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            yield return "id is required.";
        }
        else if (!IdPattern.IsMatch(definition.Id))
        {
            yield return $"id '{definition.Id}' must be lower-case letters, digits, '-' or '_', and start with a letter or digit.";
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            yield return "name is required.";
        }

        foreach (var duplicate in Duplicates(definition.Attributes.Select(a => a.Key)))
        {
            yield return $"attribute key '{duplicate}' is declared more than once.";
        }

        foreach (var attribute in definition.Attributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Key)) yield return "every attribute needs a key.";
            if (attribute.Modifier is not ("none" or AttributeDefinition.HalfMinusFiveRule))
            {
                yield return $"attribute '{attribute.Key}' has unknown modifier rule '{attribute.Modifier}' (use 'none' or 'half-minus-five').";
            }
        }

        foreach (var duplicate in Duplicates(definition.Resources.Select(r => r.Key)))
        {
            yield return $"resource key '{duplicate}' is declared more than once.";
        }

        foreach (var resource in definition.Resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Key)) yield return "every resource needs a key.";
        }

        if (definition.Resources.Count(r => r.IsPrimary) > 1)
        {
            yield return "only one resource may be marked isPrimary.";
        }

        foreach (var duplicate in Duplicates(definition.EntityTypes.Select(t => t.Type)))
        {
            yield return $"entity type '{duplicate}' is declared more than once.";
        }

        foreach (var duplicate in Duplicates(definition.EntityTypes.Select(t => t.FolderOrType)))
        {
            yield return $"two entity types share the folder '{duplicate}'.";
        }

        foreach (var type in definition.EntityTypes)
        {
            if (string.IsNullOrWhiteSpace(type.Type))
            {
                yield return "every entity type needs a type key.";
                continue;
            }

            if (!IdPattern.IsMatch(type.Type))
            {
                yield return $"entity type '{type.Type}' must be lower-case letters, digits, '-' or '_'.";
            }

            if (type.FolderOrType is "abilities" or "actors" or "locations")
            {
                yield return $"entity type '{type.Type}' uses the reserved folder '{type.FolderOrType}'.";
            }

            foreach (var duplicate in Duplicates(type.Fields.Select(f => f.Key)))
            {
                yield return $"entity type '{type.Type}' declares field '{duplicate}' more than once.";
            }

            foreach (var field in type.Fields.Where(f => f.Type == FieldType.EntityReference && string.IsNullOrWhiteSpace(f.TargetEntityType)))
            {
                yield return $"field '{field.Key}' on '{type.Type}' is an EntityReference without a targetEntityType.";
            }
        }

        var actorFieldKeys = definition.ActorFields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var role in definition.FieldRoles.Named.Where(r => !actorFieldKeys.Contains(r)).Distinct())
        {
            yield return $"fieldRoles: '{role}' is not an actor field.";
        }

        foreach (var error in ValidateSheetRules(definition))
        {
            yield return error;
        }

        foreach (var error in ValidateMoments(definition))
        {
            yield return error;
        }

        foreach (var error in ValidateKindFeatures(definition))
        {
            yield return error;
        }

        foreach (var error in ValidateKindLinks(definition))
        {
            yield return error;
        }

        foreach (var error in ValidateModifiers(definition))
        {
            yield return error;
        }

        if (definition.Vitals?.Concentration is { } concentration
            && RequiredAttributeError(definition, "vitals.concentration.saveAttribute", concentration.SaveAttribute) is { } concentrationError)
        {
            yield return concentrationError;
        }

        if (definition.Setting is { } setting)
        {
            foreach (var error in ValidateSetting(setting))
            {
                yield return error;
            }
        }

        if (definition.Vitals?.HitDice is { } hitDice
            && RequiredAttributeError(definition, "vitals.hitDice.attribute", hitDice.Attribute) is { } hitDiceError)
        {
            yield return hitDiceError;
        }

        var attributeKeys = definition.Attributes.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceKeys = definition.Resources.SelectMany(r => new[] { r.Key, r.MaxKey }).Where(k => k != null).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (var field in definition.ActorFields)
        {
            if (attributeKeys.Contains(field.Key) || resourceKeys.Contains(field.Key))
            {
                yield return $"actor field '{field.Key}' collides with an attribute or resource key.";
            }

            if (field.Type == FieldType.EntityReference)
            {
                if (string.IsNullOrWhiteSpace(field.TargetEntityType))
                {
                    yield return $"actor field '{field.Key}' is an EntityReference without a targetEntityType.";
                }
                else if (definition.FindType(field.TargetEntityType) == null)
                {
                    yield return $"actor field '{field.Key}' references entity type '{field.TargetEntityType}', which this system does not declare.";
                }
            }
        }
    }

    /// <summary>YamlDotNet wraps the useful message two or three exceptions deep.</summary>
    private static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message)) messages.Add(current.Message);
        }

        return string.Join(" -> ", messages);
    }

    private static IEnumerable<string> ValidateSheetRules(SystemDefinition definition)
    {
        var rules = definition.SheetRules;
        if (rules == null) yield break;

        // Keys that name an attribute or a derived value have no default: a system states them, so it never
        // binds silently to a 5e name ("con", "proficiencyBonus") it happens to share or lack.
        string? AttributeError(string path, string key) => RequiredAttributeError(definition, path, key);
        string? DerivedKeyError(string path, string key) =>
            string.IsNullOrWhiteSpace(key) ? $"{path} is required (the sheetRules.derived key it adds)."
            : rules.Derived.Any(d => d.Key == key) ? null
            : $"{path} '{key}' is not a sheetRules.derived key.";

        if (rules.Enforcement.ToLowerInvariant() is not ("strict" or "warn" or "off"))
        {
            yield return $"sheetRules.enforcement '{rules.Enforcement}' must be strict, warn or off.";
        }

        var fieldKeys = definition.ActorFields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenceFields = definition.ActorFields.Where(f => f.Type == FieldType.EntityReference).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (rules.Attributes is { } attributes)
        {
            foreach (var method in attributes.Methods.Where(m => m is not ("point-buy" or "standard-array" or "manual")))
            {
                yield return $"sheetRules.attributes.methods: unknown method '{method}'.";
            }

            if (attributes.Methods.Contains("point-buy") && attributes.PointBuy == null)
            {
                yield return "sheetRules.attributes: point-buy is offered but pointBuy is not defined.";
            }

            if (attributes.Methods.Contains("standard-array") && attributes.StandardArray.Count != definition.Attributes.Count)
            {
                yield return $"sheetRules.attributes.standardArray must list one score per attribute ({definition.Attributes.Count}).";
            }

            if (attributes.Bonuses is { } bonuses && !referenceFields.Contains(bonuses.FromField))
            {
                yield return $"sheetRules.attributes.bonuses.fromField '{bonuses.FromField}' is not an EntityReference actor field.";
            }
        }

        foreach (var derived in rules.Derived)
        {
            if (string.IsNullOrWhiteSpace(derived.Key) || string.IsNullOrWhiteSpace(derived.Formula))
            {
                yield return "every sheetRules.derived entry needs a key and a formula.";
            }
            else if (fieldKeys.Contains(derived.Key))
            {
                yield return $"sheetRules.derived '{derived.Key}' collides with an actor field; derived fields are computed, not entered.";
            }
        }

        foreach (var constraint in rules.Constraints.Where(c => !referenceFields.Contains(c.Field)))
        {
            yield return $"sheetRules.constraints: '{constraint.Field}' is not an EntityReference actor field.";
        }

        foreach (var duplicate in Duplicates(rules.Lists.Select(l => l.Key)))
        {
            yield return $"sheetRules.lists key '{duplicate}' is declared more than once.";
        }

        foreach (var list in rules.Lists)
        {
            if (string.IsNullOrWhiteSpace(list.Key) || string.IsNullOrWhiteSpace(list.TargetEntityType))
            {
                yield return "every sheetRules.lists entry needs a key and a targetEntityType.";
            }
            else if (definition.FindType(list.TargetEntityType) == null)
            {
                yield return $"sheetRules.lists '{list.Key}' targets entity type '{list.TargetEntityType}', which this system does not declare.";
            }

            if (fieldKeys.Contains(list.Key))
            {
                yield return $"sheetRules.lists '{list.Key}' collides with an actor field.";
            }
        }

        if (rules.Attributes?.Increases is { } increases && !rules.Lists.Any(l => l.Key.Equals(increases.FromList, StringComparison.OrdinalIgnoreCase)))
        {
            yield return $"sheetRules.attributes.increases.fromList '{increases.FromList}' is not a sheetRules.lists key.";
        }

        if (rules.Multiclass is { } multiclass)
        {
            if (!referenceFields.Contains(multiclass.ClassField))
            {
                yield return $"sheetRules.multiclass.classField '{multiclass.ClassField}' is not an EntityReference actor field.";
            }

            if (!fieldKeys.Contains(multiclass.LevelField))
            {
                yield return $"sheetRules.multiclass.levelField '{multiclass.LevelField}' is not an actor field.";
            }

            if (fieldKeys.Contains(multiclass.Key) || fieldKeys.Contains(multiclass.ClassLevelKey))
            {
                yield return "sheetRules.multiclass key and classLevelKey must not collide with actor fields.";
            }

            if (multiclass.SubclassField != null && !referenceFields.Contains(multiclass.SubclassField))
            {
                yield return $"sheetRules.multiclass.subclassField '{multiclass.SubclassField}' is not an EntityReference actor field.";
            }
        }

        foreach (var inherit in rules.Inherits)
        {
            if (!referenceFields.Contains(inherit.Field))
            {
                yield return $"sheetRules.inherits field '{inherit.Field}' is not an EntityReference actor field.";
            }

            if (string.IsNullOrWhiteSpace(inherit.AppliedKey) || fieldKeys.Contains(inherit.AppliedKey))
            {
                yield return $"sheetRules.inherits '{inherit.Field}' needs an appliedKey that is not an actor field.";
            }
        }

        foreach (var error in ValidateFeatureSources(definition, rules))
        {
            yield return error;
        }

        if (rules.Skills is { } skills)
        {
            foreach (var skill in skills.List.Where(s => !definition.Attributes.Any(a => a.Key.Equals(s.Attribute, StringComparison.OrdinalIgnoreCase))))
            {
                yield return $"sheetRules.skills '{skill.Key}' uses attribute '{skill.Attribute}', which is not one of the system's attributes.";
            }

            foreach (var duplicate in Duplicates(skills.List.Select(s => s.Key)))
            {
                yield return $"sheetRules.skills key '{duplicate}' is declared more than once.";
            }

            foreach (var passive in skills.Passives.Where(p => !skills.List.Any(s => s.Key.Equals(p.Skill, StringComparison.OrdinalIgnoreCase))))
            {
                yield return $"sheetRules.skills passive '{passive.Key}' reads skill '{passive.Skill}', which is not declared.";
            }

            // The bonus is what a rank without its own value is worth, so a system that names every rank's value needs none.
            var defaultsUsed = skills.Ranks.Count == 0 || skills.Saves is not { Ranks.Count: > 0 };
            if ((defaultsUsed || !string.IsNullOrWhiteSpace(skills.BonusKey)) && DerivedKeyError("sheetRules.skills.bonusKey", skills.BonusKey) is { } error) yield return error;

            // Ranks: the skills' and the saving throws' lists are checked together, since a sheet key names one rank of one list.
            var listKeys = rules.Lists.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rankKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, ranks, named) in new[]
            {
                ("sheetRules.skills.ranks", skills.SkillRanks(), skills.Ranks.Count > 0),
                ("sheetRules.skills.saves.ranks", skills.SaveRanks(), skills.Saves is { Ranks.Count: > 0 })
            })
            {
                foreach (var duplicate in Duplicates(ranks.Select(r => r.Key)))
                {
                    yield return $"{path} key '{duplicate}' is declared more than once.";
                }

                foreach (var rank in ranks)
                {
                    if (string.IsNullOrWhiteSpace(rank.Key) || string.IsNullOrWhiteSpace(rank.SheetKey))
                    {
                        yield return $"every {path} entry needs a key and a sheetKey.";
                        continue;
                    }

                    if (int.TryParse(rank.Key, out _))
                    {
                        yield return $"{path} key '{rank.Key}' cannot be a number: a grant names a rank by key or by its number.";
                    }

                    if (!rankKeys.Add(rank.SheetKey))
                    {
                        yield return $"{path} sheetKey '{rank.SheetKey}' is used by more than one rank.";
                    }
                    else if (fieldKeys.Contains(rank.SheetKey) || listKeys.Contains(rank.SheetKey))
                    {
                        yield return $"{path} sheetKey '{rank.SheetKey}' collides with an actor field or a sheetRules.lists key.";
                    }

                    // A default rank reads the bonus; a missing bonusKey is said above, not once more per rank.
                    if (!string.IsNullOrWhiteSpace(rank.Value) && (named || !string.IsNullOrWhiteSpace(skills.BonusKey))
                        && Codex.Core.Rules.SheetFormulas.For(definition).Check(rank.Value) is { } formulaError)
                    {
                        yield return $"{path} '{rank.Key}' value '{rank.Value}' cannot be read: {formulaError}";
                    }
                }
            }
        }

        if (rules.Spellcasting is { } spellcasting && DerivedKeyError("sheetRules.spellcasting.bonusKey", spellcasting.BonusKey) is { } spellError)
        {
            yield return spellError;
        }

        if (rules.Proficiencies?.StrengthAttribute is { } strengthAttribute
            && AttributeError("sheetRules.proficiencies.strengthAttribute", strengthAttribute) is { } strengthError)
        {
            yield return strengthError;
        }

        if (rules.HitPoints?.PerLevelBonus is { } perLevel && !referenceFields.Contains(perLevel.Field))
        {
            yield return $"sheetRules.hitPoints.perLevelBonus field '{perLevel.Field}' is not an EntityReference actor field.";
        }

        foreach (var duplicate in Duplicates(rules.SlotPools.Select(p => p.Key)))
        {
            yield return $"sheetRules.slotPools key '{duplicate}' is declared more than once.";
        }

        foreach (var pool in rules.SlotPools)
        {
            if (string.IsNullOrWhiteSpace(pool.Key) || string.IsNullOrWhiteSpace(pool.Table) || string.IsNullOrWhiteSpace(pool.Column))
            {
                yield return "every sheetRules.slotPools entry needs a key, a table and a column.";
            }

            if (!referenceFields.Contains(pool.Field))
            {
                yield return $"sheetRules.slotPools '{pool.Key}' reads field '{pool.Field}', which is not an EntityReference actor field.";
            }

            if (pool.Combine is { } combine && combine.Rounding.ToLowerInvariant() is not ("up" or "down" or "nearest"))
            {
                yield return $"sheetRules.slotPools '{pool.Key}' combine.rounding must be up, down or nearest.";
            }

            if (definition.Resources.Any(r => pool.Owns(r.Key) || (r.MaxKey != null && pool.Owns(r.MaxKey))))
            {
                yield return $"sheetRules.slotPools '{pool.Key}' would overwrite a declared resource.";
            }
        }

        if (rules.HitPoints is { } hitPoints)
        {
            if (hitPoints.FirstLevel is not (HitPointRules.FullDie or "average"))
            {
                yield return $"sheetRules.hitPoints.firstLevel '{hitPoints.FirstLevel}' must be {HitPointRules.FullDie} or average.";
            }

            if (hitPoints.Average is not (HitPointRules.HalfPlusOne or HitPointRules.Half))
            {
                yield return $"sheetRules.hitPoints.average '{hitPoints.Average}' must be {HitPointRules.HalfPlusOne} or {HitPointRules.Half}.";
            }

            if (!referenceFields.Contains(hitPoints.ClassField))
            {
                yield return $"sheetRules.hitPoints.classField '{hitPoints.ClassField}' is not an EntityReference actor field.";
            }

            if (!fieldKeys.Contains(hitPoints.LevelField))
            {
                yield return $"sheetRules.hitPoints.levelField '{hitPoints.LevelField}' is not an actor field.";
            }

            if (AttributeError("sheetRules.hitPoints.attribute", hitPoints.Attribute) is { } error) yield return error;

            foreach (var mode in hitPoints.Modes.Where(m => m is not ("average" or "rolled" or "manual")))
            {
                yield return $"sheetRules.hitPoints.modes: unknown mode '{mode}' (average, rolled or manual).";
            }

            if (!hitPoints.Modes.Contains(hitPoints.Default))
            {
                yield return $"sheetRules.hitPoints.default '{hitPoints.Default}' is not one of its modes.";
            }

            if (fieldKeys.Contains(hitPoints.ModeKey) || fieldKeys.Contains(hitPoints.RollsKey))
            {
                yield return "sheetRules.hitPoints modeKey and rollsKey must not collide with actor fields.";
            }
        }

        if (rules.ArmorClass is { } armorClass)
        {
            foreach (var list in armorClass.BonusLists.Where(l => !rules.Lists.Any(r => r.Key.Equals(l, StringComparison.OrdinalIgnoreCase))))
            {
                yield return $"sheetRules.armorClass.bonusLists '{list}' is not a sheetRules.lists key.";
            }

            if (AttributeError("sheetRules.armorClass.dexAttribute", armorClass.DexAttribute) is { } error) yield return error;

            if (string.IsNullOrWhiteSpace(armorClass.Unarmored))
            {
                yield return "sheetRules.armorClass.unarmored is required (a formula such as \"10 + mod(dex)\").";
            }

            if (fieldKeys.Contains(armorClass.ModeKey))
            {
                yield return "sheetRules.armorClass.modeKey must not collide with an actor field.";
            }
        }

        if (rules.Inventory is { } inventory)
        {
            if (fieldKeys.Contains(inventory.Key))
            {
                yield return $"sheetRules.inventory key '{inventory.Key}' collides with an actor field.";
            }

            foreach (var type in inventory.Types.Where(t => definition.FindType(t) == null))
            {
                yield return $"sheetRules.inventory.types '{type}' is not an entity type this system declares.";
            }
        }

        foreach (var condition in rules.Constraints.SelectMany(c => c.Where).Concat(rules.Lists.SelectMany(l => l.Where)))
        {
            var referencedFields = new[] { condition.EqualsField, condition.ContainsNameOfField, condition.LteField, condition.GteField, condition.EqualsPropertyOfField?.Field, condition.LteLookup?.Field }
                .Where(f => f != null);
            foreach (var field in referencedFields.Where(f => !fieldKeys.Contains(f!)))
            {
                yield return $"a sheet rule on '{condition.Property}' refers to actor field '{field}', which does not exist.";
            }
        }
    }

    /// <summary>Whether <paramref name="choose"/> is sound on its own: an id, a count of 1 or more, a known <c>onChange</c>, and
    /// <c>changesOn</c> naming only the system's rests or a moment the table triggers. One sentence per problem, led by <paramref name="where"/>.</summary>
    public static IEnumerable<string> CheckRowChoose(SystemDefinition definition, RowChoose choose, string where)
    {
        if (string.IsNullOrWhiteSpace(choose.Id)) yield return $"{where}: choose needs an id (it names the choice; the sheet stores the pick under it).";
        if (choose.Count < 1) yield return $"{where}: choose count must be 1 or more.";
        if (choose.OnChange.ToLowerInvariant() is not (RowChoose.Keep or RowChoose.Refill or RowChoose.Empty))
        {
            yield return $"{where}: onChange '{choose.OnChange}' must be keep, refill or empty.";
        }

        foreach (var moment in choose.ChangesOn.Where(m => !Codex.Core.Rules.SystemMoments.ChangeMoments(definition).Contains(m, StringComparer.OrdinalIgnoreCase)))
        {
            yield return $"{where}: changesOn '{moment}' is not one of the system's moments (a rest, one it declares, initiative or dawn).";
        }
    }

    private static readonly Regex MomentKeyPattern = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>The problems with the system's declared <c>moments</c>: a key (letters, digits, '-', '_') that is unique and is not a rest's
    /// name, initiative, dawn or manual; an <c>onSet</c> that names an input value of a sheet kind.</summary>
    private static IEnumerable<string> ValidateMoments(SystemDefinition definition)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Codex.Core.Rules.FeatureRecovery.Initiative, Codex.Core.Rules.FeatureRecovery.Dawn, Codex.Core.Rules.FeatureRecovery.Manual
        };
        foreach (var rest in definition.Vitals?.Rests ?? new List<RestRule>()) taken.Add(rest.Name);
        var inputs = definition.SheetKinds.SelectMany(k => k.Values).Where(v => v.Input).Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var moment in definition.Moments)
        {
            if (string.IsNullOrWhiteSpace(moment.Key))
            {
                yield return "every moment needs a key.";
                continue;
            }

            if (!MomentKeyPattern.IsMatch(moment.Key)) yield return $"moment '{moment.Key}' must be letters, digits, '-' or '_', and start with a letter.";
            if (!taken.Add(moment.Key)) yield return $"moment '{moment.Key}' repeats a rest, initiative, dawn, manual or another moment.";
            if (!string.IsNullOrWhiteSpace(moment.OnSet) && !inputs.Contains(moment.OnSet))
            {
                yield return $"moment '{moment.Key}': onSet '{moment.OnSet}' is not an input value of any sheet kind.";
            }
        }
    }

    /// <summary>The problems with the modifiers a system writes itself (a sheet kind's own, and a condition's effects): opt-ins (<c>ends</c> only on a
    /// condition's effect, a whole number to spend) and a dice amount on a target a pool roll reads.</summary>
    private static IEnumerable<string> ValidateModifiers(SystemDefinition definition)
    {
        foreach (var kind in definition.SheetKinds)
        {
            for (var i = 0; i < kind.Modifiers.Count; i++)
            {
                var m = kind.Modifiers[i];
                foreach (var error in Rules.OptInChecks.Problems(definition, $"sheetKinds.{kind.Kind}.modifiers[{i}]", m.Target, m.Amount, m.OptIn, inCondition: false)) yield return error;
            }
        }

        foreach (var (name, effect) in definition.ConditionEffects)
        {
            for (var i = 0; i < effect.Modifiers.Count; i++)
            {
                var m = effect.Modifiers[i];
                foreach (var error in Rules.OptInChecks.Problems(definition, $"conditionEffects.{name}.modifiers[{i}]", m.Target, m.Amount, m.OptIn, inCondition: true)) yield return error;
            }
        }
    }

    /// <summary>The problems with the feature sources of the system's sheet kinds: only <c>list</c> sources, of a list the kind has and
    /// that holds entries; no <c>field</c> or <c>byField</c>; a <c>choose</c> needs a <c>property</c> of rows; and a list that is a source
    /// may not also have <c>modifiersFrom</c> (its modifiers would count twice).</summary>
    private static IEnumerable<string> ValidateKindFeatures(SystemDefinition definition)
    {
        foreach (var kind in definition.SheetKinds.Where(k => k.Features.Count > 0))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in kind.Features)
            {
                var where = $"sheetKinds.{kind.Kind}.features ({(source.Link != null ? $"link {source.Link}/" : string.Empty)}{source.List ?? source.Field})";
                if (source.Field != null) yield return $"{where}: a sheet kind has no reference fields, so a source names a list, not a field.";
                if (source.ByField != null) yield return $"{where}: byField is not read on a sheet kind (a source may read only what the sheet stores, not a worked-out value).";
                if (source.Link != null)
                {
                    foreach (var error in ValidateLinkSource(definition, kind, source, where)) yield return error;
                    continue;
                }

                var list = source.List == null ? null : kind.Lists.FirstOrDefault(l => l.Key == source.List);
                if (source.List == null)
                {
                    if (source.Field == null) yield return $"{where}: a source needs a list.";
                    continue;
                }

                if (list == null)
                {
                    yield return $"{where}: '{source.List}' is not one of the kind's lists.";
                    continue;
                }

                if (list.Type == null) yield return $"{where}: the list '{list.Key}' holds plain records, not entries, so it carries no features.";
                if (list.ModifiersFrom != null) yield return $"{where}: the list '{list.Key}' also has modifiersFrom, so its modifiers would count twice. Use one or the other.";
                if (source.Choose == null) continue;
                if (source.Property == null) yield return $"{where}: choose picks one of an entry's rows, so the source needs a property that holds rows.";
                foreach (var error in CheckRowChoose(definition, source.Choose, where)) yield return error;
                var scope = source.Entries.Count == 0 ? new[] { "*" } : source.Entries.ToArray();
                if (scope.Any(entry => !seen.Add($"{source.List}/{entry}/{source.Choose.Id}")))
                {
                    yield return $"{where}: the choice id '{source.Choose.Id}' is used twice for the same entry.";
                }
            }
        }
    }

    /// <summary>The problems with a feature source that reads a link (<c>link: crew</c>): the link must be one of the kind's, the source needs a
    /// <c>property</c> (the entry property holding the one grant map the linking sheet gets), and its <c>list</c> must be a list of entries on the
    /// <b>linked</b> kind. A <c>choose</c> is not read here: a grant map's own <c>options</c> and <c>choose</c> do that.</summary>
    private static IEnumerable<string> ValidateLinkSource(SystemDefinition definition, SheetKindDefinition kind, FeatureSource source, string where)
    {
        var link = kind.Links.FirstOrDefault(l => l.Key == source.Link);
        if (link == null)
        {
            yield return $"{where}: '{source.Link}' is not one of the kind's links.";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(source.Property)) yield return $"{where}: a link source needs a property, the entry property that holds the grants the linked entry gives.";
        if (source.Choose != null) yield return $"{where}: choose is not read on a link source; give the grant map its own options and choose.";
        if (source.List == null)
        {
            yield return $"{where}: a link source needs the list of the linked kind it reads.";
            yield break;
        }

        // An unknown linked kind is reported with the link itself.
        if (definition.SheetKinds.FirstOrDefault(k => string.Equals(k.Kind, link.Kind, StringComparison.OrdinalIgnoreCase)) is not { } linkedKind) yield break;
        var list = linkedKind.Lists.FirstOrDefault(l => l.Key == source.List);
        if (list == null) yield return $"{where}: '{source.List}' is not one of the lists of '{linkedKind.Kind}', the kind the link '{link.Key}' points to.";
        else if (list.Type == null) yield return $"{where}: the list '{list.Key}' of '{linkedKind.Kind}' holds plain records, not entries, so it carries no features.";
    }

    private static readonly System.Text.RegularExpressions.Regex LinkCall =
        new(@"\blink\s*\(\s*(?:'(?<key>[^']*)'|""(?<key>[^""]*)"")", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The problems with the links a system's sheet kinds declare: each needs a key (unique in its kind) and the kind of a sheet the system
    /// has; and a <c>link('key', ...)</c> in a formula of the kind must name one of the kind's links (a roll's, one of any kind's).</summary>
    private static IEnumerable<string> ValidateKindLinks(SystemDefinition definition)
    {
        var all = definition.SheetKinds.SelectMany(k => k.Links.Select(l => l.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in definition.SheetKinds)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in kind.Links)
            {
                var where = $"sheetKinds.{kind.Kind}.links ({link.Key})";
                if (string.IsNullOrWhiteSpace(link.Key)) yield return $"sheetKinds.{kind.Kind}.links: a link needs a key.";
                else if (!keys.Add(link.Key)) yield return $"{where}: the key is used twice.";
                if (string.IsNullOrWhiteSpace(link.Kind)) yield return $"{where}: a link needs the kind of sheet it points to.";
                else if (!definition.SheetKinds.Any(k => string.Equals(k.Kind, link.Kind, StringComparison.OrdinalIgnoreCase))) yield return $"{where}: '{link.Kind}' is not one of the system's sheet kinds.";
            }

            foreach (var (at, formula) in KindFormulas(kind))
            {
                foreach (System.Text.RegularExpressions.Match call in LinkCall.Matches(formula))
                {
                    var key = call.Groups["key"].Value;
                    if (!keys.Contains(key)) yield return $"sheetKinds.{kind.Kind}.{at}: link('{key}', ...) names a link the kind does not have.";
                }
            }
        }

        foreach (var roll in definition.Rolls)
        {
            foreach (var formula in (roll.Pool == null ? Enumerable.Empty<string>() : new[] { roll.Pool }).Concat(roll.Add))
            {
                foreach (System.Text.RegularExpressions.Match call in LinkCall.Matches(formula))
                {
                    if (!all.Contains(call.Groups["key"].Value)) yield return $"The roll \"{roll.Key}\": link('{call.Groups["key"].Value}', ...) names a link no sheet kind has.";
                }
            }
        }
    }

    /// <summary>Every formula a sheet kind writes, with where it is.</summary>
    private static IEnumerable<(string At, string Formula)> KindFormulas(SheetKindDefinition kind)
    {
        foreach (var value in kind.Values.Where(v => v.Formula != null)) yield return ($"values.{value.Key}", value.Formula!);
        foreach (var modifier in kind.Modifiers)
        {
            yield return ($"modifiers ({modifier.Target})", modifier.Amount);
            if (modifier.When != null) yield return ($"modifiers ({modifier.Target})", modifier.When);
        }

        foreach (var track in kind.Tracks)
        {
            foreach (var (name, formula) in new[] { ("max", track.Max), ("min", track.Min), ("start", track.Start), ("each", track.Each), ("overflow.by", track.Overflow?.By) })
            {
                if (formula != null) yield return ($"tracks.{track.Key}.{name}", formula);
            }

            foreach (var line in track.Thresholds.Concat(track.Bands))
            {
                foreach (var formula in new[] { line.AtOrBelow, line.Below, line.AtOrAbove, line.Above, line.Equals })
                {
                    if (formula != null) yield return ($"tracks.{track.Key}.thresholds", formula);
                }
            }
        }

        foreach (var list in kind.KeyedLists)
        {
            foreach (var (name, formula) in list.Derive) yield return ($"keyedLists.{list.Key}.derive.{name}", formula);
            foreach (var rule in list.Rules) yield return ($"keyedLists.{list.Key}.rules", rule.Check);
        }

        foreach (var list in kind.Lists)
        {
            foreach (var rule in list.Rules) yield return ($"lists.{list.Key}.rules", rule.Check);
            if (list.ModifiersFrom?.When != null) yield return ($"lists.{list.Key}.modifiersFrom", list.ModifiersFrom.When);
        }
    }

    private static IEnumerable<string> ValidateFeatureSources(SystemDefinition definition, SheetRules rules)
    {
        foreach (var source in rules.Features.Where(s => s.Link != null))
        {
            yield return $"sheetRules.features ({source.Link}/{source.List}): link is read on a sheet kind's features only.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in rules.Features.Where(s => s.Choose != null))
        {
            var where = $"sheetRules.features ({source.Field ?? source.List})";
            if (source.Property == null || source.Field == null)
            {
                yield return $"{where}: choose picks one of an entry's rows, so the source needs a field and a property that holds rows.";
            }

            else if (definition.ActorFields.FirstOrDefault(f => f.Key == source.Field)?.TargetEntityType is { } targetType
                && definition.FindType(targetType) is { } entityType
                && entityType.Fields.FirstOrDefault(f => f.Key == source.Property)?.Type != FieldType.Collection)
            {
                yield return $"{where}: choose needs a property that is a Collection of rows, and '{source.Property}' of '{targetType}' is not.";
            }

            foreach (var error in CheckRowChoose(definition, source.Choose!, where)) yield return error;

            // Two picks of one entry that share an id would share their picks. A source with no entries list reaches every entry of the field.
            var scope = source.Entries.Count == 0 ? new[] { "*" } : source.Entries.ToArray();
            foreach (var entry in scope)
            {
                if (!seen.Add($"{source.Field}/{entry}/{source.Choose!.Id}"))
                {
                    yield return $"{where}: the choice id '{source.Choose.Id}' is used twice for the same entry.";
                    break;
                }
            }
        }
    }

    /// <summary>The setting goes into every prompt that writes content, so it is kept short: a brief, not a setting book.</summary>
    public const int SettingTextMax = 1200, SettingItemMax = 160, SettingItemsMax = 20;

    private static IEnumerable<string> ValidateSetting(SystemSetting setting)
    {
        foreach (var (name, text) in new[] { ("summary", setting.Summary), ("technology", setting.Technology), ("supernatural", setting.Supernatural), ("notes", setting.Notes) })
        {
            if (text is { Length: > SettingTextMax }) yield return $"setting.{name} is {text.Length} characters; keep it to {SettingTextMax}.";
        }

        foreach (var (name, items) in new[] { ("genres", setting.Genres), ("tone", setting.Tone), ("themes", setting.Themes), ("factions", setting.Factions), ("places", setting.Places), ("play", setting.Play), ("avoid", setting.Avoid) })
        {
            if (items.Count > SettingItemsMax) yield return $"setting.{name} has {items.Count} items; keep it to {SettingItemsMax}.";
            foreach (var item in items.Where(i => i is { Length: > SettingItemMax }))
            {
                yield return $"setting.{name}: an item is {item.Length} characters; keep each to {SettingItemMax}.";
            }
        }
    }

    private static string? RequiredAttributeError(SystemDefinition definition, string path, string key) =>
        string.IsNullOrWhiteSpace(key) ? $"{path} is required (one of the system's attribute keys)."
        : definition.Attributes.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ? null
        : $"{path} '{key}' is not one of the system's attributes.";

    private static IEnumerable<string> Duplicates(IEnumerable<string> keys) =>
        keys.Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
}
