using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>What is wrong with a system's <c>sheetRules.ranked</c> lists, and with the places that read them (<c>armorClass.rank</c>, an action's
/// <c>rank</c>). Kept apart from the loader's other checks; the loader makes one call.</summary>
internal static class RankedListChecks
{
    private static readonly string[] Reserved = { SheetRuleEngine.SkillsTarget, SheetRuleEngine.SavesTarget };

    private static IEnumerable<string> Duplicates(IEnumerable<string> keys) =>
        keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);

    public static IEnumerable<string> Problems(SystemDefinition d, HashSet<string> fieldKeys, HashSet<string> listKeys)
    {
        if (d.SheetRules is not { } rules) yield break;
        var referenceFields = d.ActorFields.Where(f => f.Type == FieldType.EntityReference).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Names a list or a published value may not take: the sheet's own, and what the other rules write to.
        var taken = new HashSet<string>(fieldKeys.Concat(listKeys).Concat(rules.Derived.Select(x => x.Key)), StringComparer.OrdinalIgnoreCase);
        foreach (var own in new[] { rules.ArmorClass?.Key, rules.HitPoints?.Key, rules.HitPoints?.CurrentKey })
        {
            if (!string.IsNullOrEmpty(own)) taken.Add(own);
        }

        // Sheet keys already used by the skills' and the saves' ranks.
        var sheetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rules.Skills is { } skills)
        {
            foreach (var rank in skills.SkillRanks().Concat(skills.SaveRanks())) sheetKeys.Add(rank.SheetKey);
        }

        if (rules.Ranked.Any(l => string.IsNullOrWhiteSpace(l.Key))) yield return "sheetRules.ranked needs a key on every list.";

        var named = rules.Ranked.Where(l => !string.IsNullOrWhiteSpace(l.Key)).ToList();
        foreach (var duplicate in Duplicates(named.Select(l => l.Key))) yield return $"sheetRules.ranked key '{duplicate}' is declared more than once.";

        var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in named)
        {
            var k = list.Key;
            var path = $"sheetRules.ranked.{k}.ranks";
            if (Reserved.Contains(k, StringComparer.OrdinalIgnoreCase)) yield return $"sheetRules.ranked key '{k}' is reserved (skills, saves).";

            var names = new List<string> { k };
            if (list.Publish) names.AddRange(list.Keys().Select(x => x.Key));
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (list.Publish && !published.Add(name))
                {
                    yield return $"sheetRules.ranked '{name}' is published by more than one ranked list.";
                }
                else if (taken.Contains(name))
                {
                    yield return $"sheetRules.ranked '{name}' collides with an actor field, a derived value, a sheetRules.lists key, or the armor class or hit point key.";
                }
            }

            if ((list.Ranks.Count > 0) == !string.IsNullOrEmpty(list.RanksOf))
            {
                yield return $"sheetRules.ranked '{k}' needs ranks or ranksOf, not both.";
            }
            else if (!string.IsNullOrEmpty(list.RanksOf))
            {
                var o = list.RanksOf;
                var ok = ((o.Equals(SheetRuleEngine.SkillsTarget, StringComparison.OrdinalIgnoreCase) || o.Equals(SheetRuleEngine.SavesTarget, StringComparison.OrdinalIgnoreCase)) && rules.Skills != null)
                    || rules.Ranked.Any(x => x != list && x.Key.Equals(o, StringComparison.OrdinalIgnoreCase) && x.Ranks.Count > 0);
                if (!ok) yield return $"sheetRules.ranked '{k}' ranksOf '{o}' names no list with ranks of its own.";
            }

            var ranks = rules.RanksFor(list);
            foreach (var duplicate in Duplicates(ranks.Select(r => r.Key))) yield return $"{path} key '{duplicate}' is declared more than once.";
            if (list.Ranks.Any(r => string.IsNullOrWhiteSpace(r.Key))) yield return $"every {path} entry needs a key.";
            foreach (var rank in ranks)
            {
                if (int.TryParse(rank.Key, out _)) yield return $"{path} key '{rank.Key}' cannot be a number: a grant names a rank by key or by its number.";
                if (!sheetKeys.Add(rank.SheetKey)) yield return $"{path} sheetKey '{rank.SheetKey}' is used by more than one rank.";
                else if (taken.Contains(rank.SheetKey)) yield return $"{path} sheetKey '{rank.SheetKey}' collides with an actor field, a derived value, a sheetRules.lists key, or the armor class or hit point key.";
            }

            foreach (var rank in list.Ranks.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
            {
                if (SheetFormulas.For(d).Check(rank.Value) is { } formulaError) yield return $"{path} '{rank.Key}' value '{rank.Value}' cannot be read: {formulaError}";
            }

            foreach (var duplicate in Duplicates(list.Keys().Select(x => x.Key))) yield return $"sheetRules.ranked '{k}' key '{duplicate}' is declared more than once.";

            foreach (var attribute in new[] { list.Attribute }.Concat(list.List.Select(x => x.Attribute)).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!d.Attributes.Any(a => a.Key.Equals(attribute, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return $"sheetRules.ranked '{k}' uses attribute '{attribute}', which is not one of the system's attributes.";
                }
            }

            if (list.AttributeFrom is { } from && !referenceFields.Contains(from.Field))
            {
                yield return $"sheetRules.ranked '{k}' attributeFrom.field '{from.Field}' is not an EntityReference actor field.";
            }
        }

        if (rules.ArmorClass?.Rank is { } armor)
        {
            var target = named.FirstOrDefault(l => l.Key.Equals(armor.List, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                yield return $"sheetRules.armorClass.rank.list '{armor.List}' is not a sheetRules.ranked list.";
            }
            else if (!string.IsNullOrEmpty(armor.Unarmored) && !target.Keys().Any(x => x.Key.Equals(armor.Unarmored, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"sheetRules.armorClass.rank.unarmored '{armor.Unarmored}' is not a key of {armor.List}.";
            }
        }

        foreach (var action in rules.Actions.Where(a => a.Rank != null))
        {
            if (!named.Any(l => l.Key.Equals(action.Rank!.List, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"sheetRules.actions '{action.Key}' rank.list '{action.Rank!.List}' is not a sheetRules.ranked list.";
            }
        }
    }
}
