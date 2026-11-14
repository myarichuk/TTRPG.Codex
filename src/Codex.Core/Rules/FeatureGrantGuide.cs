using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// What a writer of entries (a model homebrewing a subclass, a pack's author) is told about making a feature do something on
/// the sheet: where on an entry of a type the system reads features (<see cref="SheetRules.Features"/>), and the names this
/// system gives the things a feature may change. Every name comes from the system; nothing here is any game's word.
/// </summary>
public static class FeatureGrantGuide
{
    /// <summary>The guide for entries of <paramref name="entityType"/>, or null when the system reads no features from them.</summary>
    public static string? For(SystemDefinition system, string entityType)
    {
        if (FeatureGrantVocabulary.For(system) is not { } vocabulary || vocabulary.CarrierFor(entityType) is not { } carrier) return null;
        var rowProperties = carrier.RowProperties;

        var where = rowProperties.Count > 0
            ? $"Each row of {string.Join(" and ", rowProperties.Select(p => $"\"{p}\""))} may carry, beside its name and description,"
            : "The entry may carry, among its properties,";
        var sb = new StringBuilder();
        sb.AppendLine($"What a feature does on the sheet. {where} the keys below; the sheet then applies them, so a player does not have to. " +
                      "Use them for everything the text states as a plain fact about the sheet, keep the text as it is, and leave out of them whatever needs a ruling at the table.");

        var targets = vocabulary.Targets.Select(t => t.Described).ToList();
        if (targets.Count > 0)
        {
            sb.AppendLine($"- \"modifiers\": [{{\"target\": key, \"amount\": number or formula, \"type\": optional}}]. Targets: {string.Join(", ", targets)}. " +
                          $"A formula reads the sheet's numbers ({string.Join(", ", vocabulary.FormulaNames)}) and mod(attribute). Without a type the amount is added." +
                          Stacking(vocabulary));
        }

        var lists = vocabulary.Lists.Where(l => !l.IsRanked && !vocabulary.DamageListKeys.Contains(l.Key)).Select(l => $"{l.Key} ({l.Label}: {l.EntityType} ids or names)").ToList();
        if (vocabulary.DamageListKeys.Count > 0)
        {
            var types = vocabulary.DamageTypes.Count > 0 ? ": " + string.Join(", ", vocabulary.DamageTypes) : "";
            lists.Add($"{string.Join(", ", vocabulary.DamageListKeys)} (damage types{types})");
        }

        if (lists.Count > 0)
        {
            sb.AppendLine($"- \"adds\": [{{\"to\": list key, \"entries\": [..], \"level\": optional}}]: entries the feature puts on a list, from that level on when one is given. Lists: {string.Join("; ", lists)}.");
        }

        RankedLists(sb, vocabulary);

        var rests = vocabulary.Rests;
        sb.Append("- \"uses\": {\"max\": number or formula, \"recoversOn\": [rest names]}: a feature that can be used a limited number of times gets a counter.");
        if (rests.Count > 0) sb.Append($" Rests: {string.Join(", ", rests)}.");
        sb.AppendLine();

        var rest = rests.Count > 0 ? rests[0] : "Long Rest";
        var other = rests.Count > 1 ? rests[1] : rest;
        sb.AppendLine("  - \"key\" in \"uses\" names the pool so features can share it: the feature that owns the pool sets \"max\", features that only spend from it use \"spends\" and set no \"uses\". " +
                      "If two features set a maximum for one key, the highest is used and the sheet warns. Example: {\"uses\": {\"max\": \"level\", \"key\": \"focus\", \"recoversOn\": [\"" + rest + "\"]}}.");
        sb.AppendLine($"  - \"recovers\" in \"uses\" is for refills that are not a full refill on a rest: [{{\"on\": rest name, \"amount\": \"all\" | number | formula | dice, \"when\": optional}}]. " +
                      $"\"on\" may also be {string.Join(", ", vocabulary.Moments.Select(m => $"\"{m}\""))} (initiative: when the creature joins the initiative order; dawn: when the GM calls it). " +
                      "\"when\": \"empty\" refills only when none is left. \"recoversOn\": [X] is the same as {\"on\": X, \"amount\": \"all\"}. " +
                      $"Example: {{\"uses\": {{\"max\": 4, \"recovers\": [{{\"on\": \"{other}\", \"amount\": 1}}, {{\"on\": \"{rest}\", \"amount\": \"all\"}}, {{\"on\": \"initiative\", \"amount\": 1, \"when\": \"empty\"}}]}}}}. " +
                      "\"regain\": \"gm\" lets only the GM add a use back by hand.");
        sb.AppendLine("- \"spends\": {\"key\": pool key, \"amount\": number or formula or \"ask\"}: the feature gets a Use button that spends from a pool another feature owns (its \"uses\".\"key\"), disabled when too little is left; " +
                      "\"ask\" lets the player choose how much each time. The pool must exist on the same sheet. Example: {\"spends\": {\"key\": \"focus\", \"amount\": 1}}.");
        sb.AppendLine("- \"options\" with \"choose\": for \"choose one of the following\" inside a feature. \"options\": [{\"name\": .., \"description\": .., and any of the keys above: \"modifiers\", \"adds\", \"uses\", \"spends\"}] are the things to choose between; " +
                      "\"choose\": {\"id\": required, unique in the entry, \"count\": how many the player picks (default 1), \"onChange\": \"keep\" | \"refill\" | \"empty\", \"changesOn\": [" + (rests.Count > 0 ? $"\"{rests[0]}\"" : "rest names") + "]}. " +
                      "Only the options picked become features, named \"<feature>: <option>\"; the others give nothing. \"onChange\" says what happens to counters when the pick changes: \"keep\" (default) keeps what was spent of a counter both options name with the same \"uses\".\"key\", " +
                      "\"refill\" starts the new option's counters full, \"empty\" at 0. \"changesOn\" only says when the pick is usually changed; it is never a limit. " +
                      "Always write the options as rows, never only in the description, and never without \"choose\" and its \"id\". " +
                      "This \"choose\" belongs to the feature and is an object; the \"choose\" of an \"adds\" item is a number.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The lists that hold each key at a rank (skills, saving throws): the three ways a feature adds to one, a fixed rank, a step up, and a
    /// choice left to the player, each with an example on this system's own keys and ranks. Said plainly that anything "of your choice" is a
    /// choice with an id, never fixed entries.
    /// </summary>
    private static void RankedLists(StringBuilder sb, FeatureGrantVocabulary vocabulary)
    {
        var ranked = vocabulary.Lists.Where(l => l.IsRanked).ToList();
        if (ranked.Count == 0) return;

        var first = ranked[0];
        var ranks = first.Ranks!;
        var topRank = ranks[^1].Key;
        var firstRank = ranks[0].Key;
        var keys = first.Keys is { Count: > 0 } ? first.Keys.Select(k => k.Key).ToList() : new List<string> { "key" };
        string Key(int i) => keys[Math.Min(i, keys.Count - 1)];

        sb.AppendLine("  - Ranked lists hold each key at a rank: " +
                      string.Join("; ", ranked.Select(l => $"\"{l.Key}\" ({l.Label}; keys: {string.Join(", ", (l.Keys ?? Array.Empty<GrantName>()).Select(k => k.Described))}; ranks, lowest first: {string.Join(", ", l.Ranks!.Select(r => r.Described))})")) +
                      ". Add to them in one of three ways, and give no \"level\" unless the feature comes at one:");
        sb.AppendLine($"    - Fixed, the keys the text names: {{\"to\": \"{first.Key}\", \"entries\": [\"{Key(0)}\", \"{Key(1)}\"], \"rank\": \"{firstRank}\"}}. Without \"rank\" the first rank is meant.");
        sb.AppendLine($"    - Raise, each named key one rank up, to the top rank at most: {{\"to\": \"{first.Key}\", \"entries\": [\"{Key(0)}\"], \"raise\": 1}}. \"rank\" and \"raise\" are never given together.");
        sb.AppendLine($"    - Choice, anything \"of your choice\" or \"your choice of\": {{\"to\": \"{first.Key}\", \"choice\": \"expertise-3\", \"choose\": 2, \"from\": [\"{Key(0)}\", \"{Key(1)}\", \"{Key(2)}\"] or \"any\", \"minRank\": \"{firstRank}\", \"maxRank\": \"{topRank}\", \"rank\": \"{topRank}\" or \"raise\": 1}}. " +
                      "The player picks \"choose\" keys; \"from\" lists the keys they may pick from (\"any\" is every key; for saving throws it may be {\"attribute\": [..]}); \"minRank\" and \"maxRank\" say the rank a key must already hold (\"a skill you are proficient in\" is \"minRank\": \"" + firstRank + "\"); \"rank\" is the rank the picks are put at, or \"raise\": 1 lifts them a step. " +
                      "Every choice needs \"choose\" and its own \"choice\" id, unique within the entry (a short slug of the feature's name and level). A choice is never written as fixed \"entries\", because the sheet would then decide for the player.");
    }

    /// <summary>
    /// What the system's modifier types do, in the one rule they share: modifiers of a type form a group that keeps one member, and
    /// a type the system does not name (or none) adds. <c>max</c> and <c>min</c> keep the group's greatest or least amount and set
    /// the number to it when that beats the number's start (a floor or a ceiling, not an addition); <c>highest</c> and
    /// <c>lowest</c> keep one bonus and add it; <c>sum</c> adds all.
    /// </summary>
    private static string Stacking(FeatureGrantVocabulary vocabulary)
    {
        if (vocabulary.StackingRules.Count == 0) return string.Empty;
        string Meaning(string rule) => rule switch
        {
            ModifierStacking.Max => "only the greatest amount of that type counts, and the number becomes at least that much (a floor, not an addition)",
            ModifierStacking.Min => "only the least amount of that type counts, and the number becomes at most that much (a ceiling, not an addition)",
            "highest" => "only the largest bonus of that type is added",
            "lowest" => "only the smallest bonus of that type is added",
            _ => "all of that type are added"
        };

        var types = vocabulary.StackingRules.Select(t => $"\"type\": \"{t.Key}\" ({Meaning(t.Value)})");
        return $" Types: {string.Join("; ", types)}. A type not listed adds.";
    }
}
