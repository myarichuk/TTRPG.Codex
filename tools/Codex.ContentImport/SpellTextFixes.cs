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
        ["confusion"] = """
Each creature in a 10-foot-radius Sphere centered on a point you choose within range must succeed on a Wisdom saving throw, or that target can't take Bonus Actions or Reactions and must roll 1d10 at the start of each of its turns to determine its behavior for that turn, consulting the table below.

| d10 | Behavior for the Turn |
|---|---|
| 1 | The target doesn't take an action, and it uses all its movement to move. Roll 1d4 for the direction: 1, north; 2, east; 3, south; or 4, west. |
| 2-6 | The target doesn't move or take actions. |
| 7-8 | The target doesn't move, and it takes the Attack action to make one melee attack against a random creature within reach. If none are within reach, the target takes no action. |
| 9-10 | The target chooses its behavior. |

At the end of each of its turns, an affected target repeats the save, ending the spell on itself on a success.
""",
        ["creation"] = """
You pull wisps of shadow material from the Shadowfell to create an object within range. It is either an object of vegetable matter (soft goods, rope, wood, and the like) or mineral matter (stone, crystal, metal, and the like). The object must be no larger than a 5-foot Cube, and the object must be of a form and material that you have seen.

The spell's duration depends on the object's material, as shown in the Materials table. If the object is composed of multiple materials, use the shortest duration. Using any object created by this spell as another spell's Material component causes the other spell to fail.

| Material | Duration |
|---|---|
| Vegetable matter | 24 hours |
| Stone or crystal | 12 hours |
| Precious metals | 1 hour |
| Gems | 10 minutes |
| Adamantine or mithral | 1 minute |
""",
        ["divine-word"] = """
You utter a word imbued with power from the Upper Planes. Each creature of your choice in range makes a Charisma saving throw. On a failed save, a target that has 50 Hit Points or fewer suffers an effect based on its current Hit Points, as shown in the Divine Word Effects table. Regardless of its Hit Points, a Celestial, an Elemental, a Fey, or a Fiend target that fails its save is forced back to its plane of origin (if it isn't there already) and can't return to the current plane for 24 hours by any means short of a Wish spell.

| Hit Points | Effect |
|---|---|
| 0-20 | The target dies. |
| 21-30 | The target has the Blinded, Deafened, and Stunned conditions for 1 hour. |
| 31-40 | The target has the Blinded and Deafened conditions for 10 minutes. |
| 41-50 | The target has the Deafened condition for 1 minute. |
""",
        ["prismatic-wall"] = """
A shimmering, multicolored plane of light forms a vertical opaque wall—up to 90 feet long, 30 feet high, and 1 inch thick—centered on a point within range. Alternatively, you shape the wall into a globe up to 30 feet in diameter centered on a point within range. The wall lasts for the duration. If you position the wall in a space occupied by a creature, the spell ends instantly without effect.

The wall sheds Bright Light within 100 feet and Dim Light for an additional 100 feet. You and creatures you designate when you cast the spell can pass through and be near the wall without harm. If another creature that can see the wall moves within 20 feet of it or starts its turn there, the creature must succeed on a Constitution saving throw or have the Blinded condition for 1 minute.

The wall consists of seven layers, each with a different color. When a creature reaches into or passes through the wall, it does so one layer at a time through all the layers. Each layer forces the creature to make a Dexterity saving throw or be affected by that layer's properties as described in the Prismatic Layers table.

The wall, which has AC 10, can be destroyed one layer at a time, in order from red to violet, by means specific to each layer. If a layer is destroyed, it is gone for the duration. Antimagic Field has no effect on the wall, and Dispel Magic can affect only the violet layer.

| Order | Effects |
|---|---|
| 1 | **Red.** *Failed Save:* 12d6 Fire damage. *Successful Save:* Half as much damage. Additional Effects: Nonmagical ranged attacks can't pass through this layer, which is destroyed if it takes at least 25 Cold damage. |
| 2 | **Orange.** *Failed Save:* 12d6 Acid damage. *Successful Save:* Half as much damage. Additional Effects: Magical ranged attacks can't pass through this layer, which is destroyed by a strong wind (such as the one created by Gust of Wind). |
| 3 | **Yellow.** *Failed Save:* 12d6 Lightning damage. *Successful Save:* Half as much damage. Additional Effects: The layer is destroyed if it takes at least 60 Force damage. |
| 4 | **Green.** *Failed Save:* 12d6 Poison damage. *Successful Save:* Half as much damage. Additional Effects: A Passwall spell, or another spell of equal or greater level that can open a portal on a solid surface, destroys this layer. |
| 5 | **Blue.** *Failed Save:* 12d6 Cold damage. *Successful Save:* Half as much damage. Additional Effects: The layer is destroyed if it takes at least 25 Fire damage. |
| 6 | **Indigo.** *Failed Save:* The target has the Restrained condition and makes a Constitution saving throw at the end of each of its turns. If it successfully saves three times, the condition ends. If it fails three times, it has the Petrified condition until it is freed by an effect like the Greater Restoration spell. The successes and failures needn't be consecutive; keep track of both until the target collects three of a kind. Additional Effects: Spells can't be cast through this layer, which is destroyed by Bright Light shed by the Daylight spell. |
| 7 | **Violet.** *Failed Save:* The target has the Blinded condition and makes a Wisdom saving throw at the start of your next turn. On a successful save, the condition ends. On a failed save, the condition ends, and the creature teleports to another plane of existence (GM's choice). Additional Effects: This layer is destroyed by Dispel Magic. |
""",
        ["reincarnate"] = """
You touch a dead Humanoid or a piece of one. If the creature has been dead no longer than 10 days, the spell forms a new body for it and calls the soul to enter that body. Roll 1d10 and consult the table below to determine the body's species, or the GM chooses another playable species.

| 1d10 | Species |
|---|---|
| 1 | Roll again. |
| 2 | Dragonborn |
| 3 | Dwarf |
| 4 | Elf |
| 5 | Gnome |
| 6 | Goliath |
| 7 | Halfling |
| 8 | Human |
| 9 | Orc |
| 10 | Tiefling |

The reincarnated creature makes any choices that a species' description offers, and the creature recalls its former life. It retains the capabilities it had in its original form, except it loses the traits of its previous species and gains the traits of its new one.
""",
        ["scrying"] = """
You can see and hear a creature you choose that is on the same plane of existence as you. The target makes a Wisdom saving throw, which is modified (see the tables below) by how well you know the target and the sort of physical connection you have to it. The target doesn't know what it is making the save against, only that it feels uneasy.

| Your Knowledge of the Target Is … | Save Modifier |
|---|---|
| Secondhand (heard of the target) | +5 |
| Firsthand (met the target) | +0 |
| Extensive (know the target well) | -5 |

| You Have the Target's … | Save Modifier |
|---|---|
| Picture or other likeness | -2 |
| Garment or other possession | -4 |
| Body part, lock of hair, or bit of nail | -10 |

On a successful save, the target isn't affected, and you can't use this spell on it again for 24 hours.

On a failed save, the spell creates an Invisible, intangible sensor within 10 feet of the target. You can see and hear through the sensor as if you were there. The sensor moves with the target, remaining within 10 feet of it for the duration. If something can see the sensor, it appears as a luminous orb about the size of your fist.

Instead of targeting a creature, you can target a location you have seen. When you do so, the sensor appears at that location and doesn't move.
""",
        ["teleport"] = """
This spell instantly transports you and up to eight willing creatures that you can see within range, or a single object that you can see within range, to a destination you select. If you target an object, it must be Large or smaller, and it can't be held or carried by an unwilling creature. The destination you choose must be known to you, and it must be on the same plane of existence as you. Your familiarity with the destination determines whether you arrive there successfully. The GM rolls 1d100 and consults the Teleportation Outcome table and the explanations after it.

| Familiarity | Mishap | Similar Area | Off Target | On Target |
|---|---|---|---|---|
| Permanent circle | — | — | — | 01-00 |
| Linked object | — | — | — | 01-00 |
| Very familiar | 01-05 | 06-13 | 14-24 | 25-00 |
| Seen casually | 01-33 | 34-43 | 44-53 | 54-00 |
| Viewed once or described | 01-43 | 44-53 | 54-73 | 74-00 |
| False destination | 01-50 | 51-00 | — | — |

- "Permanent circle" means a permanent teleportation circle whose sigil sequence you know.
- "Linked object" means you possess an object taken from the desired destination within the last six months, such as a book from a wizard's library.
- "Very familiar" is a place you have visited often, a place you have carefully studied, or a place you can see when you cast the spell.
- "Seen casually" is a place you have seen more than once but with which you aren't very familiar.
- "Viewed once or described" is a place you have seen once, possibly using magic, or a place you know through someone else's description, perhaps from a map.
- "False destination" is a place that doesn't exist. Perhaps you tried to scry an enemy's sanctum but instead viewed an illusion, or you are attempting to teleport to a location that no longer exists.

**Mishap.** The spell's unpredictable magic results in a difficult journey. Each teleporting creature (or the target object) takes 3d10 Force damage, and the GM rerolls on the table to see where you wind up (multiple mishaps can occur, dealing damage each time).

**Similar Area.** You and your group (or the target object) appear in a different area that's visually or thematically similar to the target area. You appear in the closest similar place. If you are heading for your home laboratory, for example, you might appear in another person's laboratory in the same city.

**Off Target.** You and your group (or the target object) appear 2d12 miles away from the destination in a random direction. Roll 1d8 for the direction: 1, east; 2, southeast; 3, south; 4, southwest; 5, west; 6, northwest; 7, north; or 8, northeast.

**On Target.** You and your group (or the target object) appear where you intended.
""",
    };
}
