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
        .IgnoreUnmatchedProperties()
        .Build();

    public static SystemDefinition Load(string path)
    {
        var yaml = File.ReadAllText(path);
        return Parse(yaml, path);
    }

    public static SystemDefinition Parse(string yaml, string? sourceName = null)
    {
        SystemDefinition? definition;
        try
        {
            definition = Deserializer.Deserialize<SystemDefinition>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{sourceName ?? "system.yaml"} is not a valid system definition: {Describe(ex)}", ex);
        }

        if (definition == null)
        {
            throw new InvalidDataException($"{sourceName ?? "system.yaml"} is empty.");
        }

        var errors = Validate(definition).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"{sourceName ?? "system.yaml"} is invalid:\n - " + string.Join("\n - ", errors));
        }

        return definition;
    }

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
            if (attribute.Modifier is not ("none" or "half-minus-five"))
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

        foreach (var error in ValidateSheetRules(definition))
        {
            yield return error;
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

    private static IEnumerable<string> Duplicates(IEnumerable<string> keys) =>
        keys.Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
}
