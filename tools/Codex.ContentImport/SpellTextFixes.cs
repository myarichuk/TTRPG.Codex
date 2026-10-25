namespace Codex.ContentImport;

/// <summary>
/// Spell descriptions the pinned upstream JSON gets wrong (paragraphs and tables run together, one spell spliced into
/// another). The text is the 2024 SRD wording; <see cref="Srd5eImporter"/> uses it in place of the upstream description.
/// </summary>
internal static class SpellTextFixes
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["control-water"] = """
Until the spell ends, you control any water inside an area you choose that is a Cube up to 100 feet on a side, using one of the following effects. As a Magic action on your later turns, you can repeat the same effect or choose a different one.
**Flood.** You cause the water level of all standing water in the area to rise by as much as 20 feet. If you choose an area in a large body of water, you instead create a 20-foot tall wave that travels from one side of the area to the other and then crashes. Any Huge or smaller vehicles in the wave's path are carried with it to the other side. Any Huge or smaller vehicles struck by the wave have a 25 percent chance of capsizing.
The water level remains elevated until the spell ends or you choose a different effect. If this effect produced a wave, the wave repeats on the start of your next turn while the flood effect lasts.
**Part Water.** You part water in the area and create a trench. The trench extends across the spell's area, and the separated water forms a wall to either side. The trench remains until the spell ends or you choose a different effect. The water then slowly fills in the trench over the course of the next round until the normal water level is restored.
**Redirect Flow.** You cause flowing water in the area to move in a direction you choose, even if the water has to flow over obstacles, up walls, or in other unlikely directions. The water in the area moves as you direct it, but once it moves beyond the spell's area, it resumes its flow based on the terrain. The water continues to move in the direction you chose until the spell ends or you choose a different effect.
**Whirlpool.** You cause a whirlpool to form in the center of the area, which must be at least 50 feet square and 25 feet deep. The whirlpool lasts until you choose a different effect or the spell ends. The whirlpool is 5 feet wide at the base, up to 50 feet wide at the top, and 25 feet tall. Any creature in the water and within 25 feet of the whirlpool is pulled 10 feet toward it. When a creature enters the whirlpool for the first time on a turn or ends its turn there, it makes a Strength saving throw. On a failed save, the creature takes 2d8 Bludgeoning damage. On a successful save, the creature takes half as much damage. A creature can swim away from the whirlpool only if it first takes an action to pull away and succeeds on a Strength (Athletics) check against your spell save DC.
""",
        ["control-weather"] = """
You take control of the weather within 5 miles of you for the duration. You must be outdoors to cast this spell, and it ends early if you go indoors.
When you cast the spell, you change the current weather conditions, which are determined by the GM. You can change precipitation, temperature, and wind. It takes 1d4 × 10 minutes for the new conditions to take effect. Once they do so, you can change the conditions again. When the spell ends, the weather gradually returns to normal.
When you change the weather conditions, find a current condition on the following tables and change its stage by one, up or down. When changing the wind, you can change its direction.

**Precipitation**

| Stage | Condition |
|---|---|
| 1 | Clear |
| 2 | Light clouds |
| 3 | Overcast or ground fog |
| 4 | Rain, hail, or snow |
| 5 | Torrential rain, driving hail, or blizzard |

**Temperature**

| Stage | Condition |
|---|---|
| 1 | Heat wave |
| 2 | Hot |
| 3 | Warm |
| 4 | Cool |
| 5 | Cold |
| 6 | Freezing |

**Wind**

| Stage | Condition |
|---|---|
| 1 | Calm |
| 2 | Moderate wind |
| 3 | Strong wind |
| 4 | Gale |
| 5 | Storm |
""",
        ["augury"] = """
You receive an omen from an otherworldly entity about the results of a course of action that you plan to take within the next 30 minutes. The GM chooses the omen from the Omens table.

**Omens**

| Omen | For Results That Will Be … |
|---|---|
| Weal | Good |
| Woe | Bad |
| Weal and woe | Good and bad |
| Indifference | Neither good nor bad |

The spell doesn't account for circumstances, such as other spells, that might change the results.
If you cast the spell more than once before finishing a Long Rest, there is a cumulative 25 percent chance for each casting after the first that you get no answer.
""",
        ["prismatic-spray"] = """
Eight rays of light flash from you in a 60-foot Cone. Each creature in the Cone makes a Dexterity saving throw. For each target, roll 1d8 to determine which color ray affects it, consulting the Prismatic Rays table.

| 1d8 | Ray |
|---|---|
| 1 | **Red.** *Failed Save:* 12d6 Fire damage. *Successful Save:* Half as much damage. |
| 2 | **Orange.** *Failed Save:* 12d6 Acid damage. *Successful Save:* Half as much damage. |
| 3 | **Yellow.** *Failed Save:* 12d6 Lightning damage. *Successful Save:* Half as much damage. |
| 4 | **Green.** *Failed Save:* 12d6 Poison damage. *Successful Save:* Half as much damage. |
| 5 | **Blue.** *Failed Save:* 12d6 Cold damage. *Successful Save:* Half as much damage. |
| 6 | **Indigo.** *Failed Save:* The target has the Restrained condition and makes a Constitution saving throw at the end of each of its turns. If it successfully saves three times, the condition ends. If it fails three times, it has the Petrified condition until it is freed by an effect like the Greater Restoration spell. The successes and failures needn't be consecutive; keep track of both until the target collects three of a kind. |
| 7 | **Violet.** *Failed Save:* The target has the Blinded condition and makes a Wisdom saving throw at the start of your next turn. On a successful save, the condition ends. On a failed save, the condition ends, and the creature teleports to another plane of existence (GM's choice). |
| 8 | **Special.** The target is struck by two rays. Roll twice, rerolling any 8. |
""",
    };
}
