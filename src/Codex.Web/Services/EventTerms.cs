using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;

namespace Codex.Web.Services;

/// <summary>The game terms inside session log messages (conditions, damage types, rests, pools, slots, users, actors the reader cannot see) in the reader's
/// language, from the campaign's system translation and its member names.</summary>
public static class EventTerms
{
    /// <param name="system">The campaign's system as the rules know it (not a localized copy).</param>
    /// <param name="registry">The packs, whose entries' translations name the features a roll's bonuses come from; without it a bonus is only
    /// translated as far as the system's own words go.</param>
    public static EventTermText For(SystemDefinition? system, Localizer l, IReadOnlyDictionary<string, string> userNames, IContentRegistry? registry = null)
    {
        var culture = l.Culture.Name;
        var shown = system?.Localize(culture);
        var bonuses = RollBonusNames.For(registry, system, culture);
        return (kind, key) => kind switch
        {
            EventTerm.UserKind => userNames.TryGetValue(key, out var name) ? name : null,
            EventTerm.UnseenKind => l["Event.Someone"],
            EventTerm.ConditionKind => system?.DisplayCondition(culture, key),
            EventTerm.DamageTypeKind or EventTerm.RestKind => system?.DisplayOption(culture, string.Empty, key),
            EventTerm.ResourceKind => shown?.Resources.FirstOrDefault(r => r.Key == key)?.Label,
            EventTerm.SlotKind => Slot(system, shown, l, key),
            EventTerm.SystemKind => shown?.DisplayValue(key),
            EventTerm.BonusKind => Bonus(bonuses, key),
            _ => null
        };
    }

    // A roll bonus's key is its label and the feature it came from (EventTerm.Bonus).
    private static string? Bonus(RollBonusNames names, string key)
    {
        var at = key.IndexOf(EventTerm.BonusSeparator);
        return at < 0 ? null : names.Label(key[..at], key[(at + 1)..]);
    }

    // "slots_3" -> "level 3 spell slot", with the pool's slot named as the system's translation names it. English keeps the
    // wording the table wrote, which already reads that way.
    private static string? Slot(SystemDefinition? system, SystemDefinition? shown, Localizer l, string key)
    {
        var rule = system?.SheetRules?.SlotPools.FirstOrDefault(p => p.Owns(key));
        if (rule == null || !int.TryParse(key[(rule.Key.Length + 1)..], out var level)) return null;
        if (l.Culture.TwoLetterISOLanguageName == "en") return null;
        return l["Event.Slot.Other", level, shown?.DisplayValue(rule.UnitName) ?? rule.UnitName];
    }
}
