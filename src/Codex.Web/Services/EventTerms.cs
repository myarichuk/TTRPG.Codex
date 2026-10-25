using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>The game terms inside session log messages (conditions, damage types, rests, pools, slots, users) in the reader's
/// language, from the campaign's system translation and its member names.</summary>
public static class EventTerms
{
    /// <param name="system">The campaign's system as the rules know it (not a localized copy).</param>
    public static EventTermText For(SystemDefinition? system, Localizer l, IReadOnlyDictionary<string, string> userNames)
    {
        var culture = l.Culture.Name;
        var shown = system?.Localize(culture);
        return (kind, key) => kind switch
        {
            EventTerm.UserKind => userNames.TryGetValue(key, out var name) ? name : null,
            EventTerm.ConditionKind => system?.DisplayCondition(culture, key),
            EventTerm.DamageTypeKind or EventTerm.RestKind => system?.DisplayOption(culture, string.Empty, key),
            EventTerm.ResourceKind => shown?.Resources.FirstOrDefault(r => r.Key == key)?.Label,
            EventTerm.SlotKind => Slot(system, shown, l, key),
            _ => null
        };
    }

    // "slots_3" -> "level 3 spell slot"; the bundled system's two pools have their own wording, any other pool uses its translated name.
    private static string? Slot(SystemDefinition? system, SystemDefinition? shown, Localizer l, string key)
    {
        var rule = system?.SheetRules?.SlotPools.FirstOrDefault(p => p.Owns(key));
        if (rule == null || !int.TryParse(key[(rule.Key.Length + 1)..], out var level)) return null;
        return rule.Label switch
        {
            "Spell slots" => l["Event.Slot.Spell", level],
            "Pact slots" => l["Event.Slot.Pact", level],
            // English keeps the wording the table wrote; it already reads "level 3 sorcery point".
            _ when l.Culture.TwoLetterISOLanguageName == "en" => null,
            _ => l["Event.Slot.Other", level, shown?.SheetRules?.SlotPools.FirstOrDefault(p => p.Key == rule.Key)?.Label ?? rule.Label]
        };
    }
}
