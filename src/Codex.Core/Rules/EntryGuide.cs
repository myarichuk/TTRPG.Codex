using Codex.Core.AI;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>Everything a writer of entries of one type is told about making them count on the sheet: what a feature does
/// (<see cref="FeatureGrantGuide"/>) and what the entry rolls (<see cref="ActionUseGuide"/>).</summary>
public static class EntryGuide
{
    /// <summary>The guides for <paramref name="entityType"/> as one text, or null when the system reads nothing from it.</summary>
    public static string? For(SystemDefinition system, string entityType) =>
        new[] { FeatureGrantGuide.For(system, entityType), ActionUseGuide.For(system, entityType) }.OfType<string>().ToList() is { Count: > 0 } guides
            ? string.Join("\n\n", guides)
            : null;

    /// <summary>The fields a writer fills for <paramref name="entityType"/> beside the type's own: the properties those guides
    /// describe. They are kept as written (a map or a list of maps) instead of being dropped as unknown keys.</summary>
    public static IReadOnlyList<FieldSpec> Extras(SystemDefinition system, string entityType) => ActionUseGuide.Extras(system, entityType);
}
