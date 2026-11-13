using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.ContentImport;

/// <summary>
/// The skills a class lets its first level pick, written as a choice on the class entry (<c>adds: [{ to: skills, choice: class-skills, choose: 2,
/// from: [...] }]</c>), read from the class's own <c>skillChoices</c> text ("Choose 2: Animal Handling, Athletics, ..."). The sheet reads it through
/// the class source <c>{ field: class, firstOnly: true }</c>, so a second class adds none. <c>skillChoices</c> itself stays: it is the count the
/// sheet checks a sheet's ticks against, and the text a player reads.
/// </summary>
public static class ClassSkills
{
    /// <summary>The id of the choice (the engine's <c>SheetRuleEngine.ClassSkillsChoiceId</c>).</summary>
    public const string ChoiceId = "class-skills";

    private static readonly Regex Count = new(@"^\s*Choose\s+(?<any>any\s+)?(?<n>[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Writes the choice on every class entry that has a <c>skillChoices</c> text. Needs the system's skill list; does nothing without one.
    /// Throws when the text names a skill the system does not have.</summary>
    public static void Apply(IReadOnlyList<ContentEntry> entries, SystemDefinition? system)
    {
        if (system?.SheetRules?.Skills is not { List.Count: > 0 } skills) return;
        var problems = new List<string>();
        foreach (var entry in entries.Where(e => e.Type == "class"))
        {
            if (entry.Properties.GetValueOrDefault("skillChoices") is not string text || Count.Match(text) is not { Success: true } count) continue;

            var from = new List<object>();
            if (!count.Groups["any"].Success)
            {
                // "Choose 2: History, Insight, or Religion; Choose one type of tools": the skills are before the first semicolon.
                var list = text.Split(';')[0];
                list = list[(list.IndexOf(':') + 1)..].Trim();
                if (list.StartsWith("from ", StringComparison.OrdinalIgnoreCase)) list = list[5..];
                foreach (var name in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(n => n.StartsWith("or ", StringComparison.OrdinalIgnoreCase) ? n[3..].Trim() : n))
                {
                    var skill = skills.List.FirstOrDefault(s => s.Label.Equals(name, StringComparison.OrdinalIgnoreCase) || (s.CanonicalLabel?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false));
                    if (skill == null) problems.Add($"class/{entry.Id}: \"{name}\" in skillChoices is not a skill of this system.");
                    else from.Add(skill.Key);
                }
            }

            var add = new Dictionary<string, object>
            {
                ["to"] = SheetRuleEngineSkillsTarget,
                ["choice"] = ChoiceId,
                ["choose"] = long.Parse(count.Groups["n"].Value, CultureInfo.InvariantCulture)
            };
            if (from.Count > 0) add["from"] = from;
            entry.Properties["adds"] = new List<object> { add };
        }

        if (problems.Count > 0) throw new GrantTableException(problems);
    }

    private const string SheetRuleEngineSkillsTarget = "skills";
}
