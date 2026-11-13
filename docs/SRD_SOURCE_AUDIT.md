# SRD source audit

Written 2026-11-12. Importer step 1 of [FEATURE_GRANTS_DESIGN.md](FEATURE_GRANTS_DESIGN.md): count what the pinned SRD text says that the data cannot hold yet. Nothing in the importer or the pack was changed.

**These are regex counts, not ground truth.** Each count is a floor or a rough number; "False positives and misses" below says where it is off. A Haiku pass should sort the flagged samples before anyone trusts a number.

## Source and script

- Source: the 5e-bits JSON at the pinned commit `85acb251dad0b044a72c554c2d7f2d0c11e43504`, edition 2024 (`SrdDownloader.Commit`). The audit downloaded Spells, Features, Subclasses, Traits, Feats, Species, Subspecies, Backgrounds, Classes, Levels, Monsters, Equipment and Magic-Items into the scratch folder (not the repo).
- Corpus: 339 spells; 328 feature texts (186 class features, 58 subclass features, 67 species traits, 17 feats); 341 monsters. Backgrounds carry no text in the source (feat, ability scores, skills and tools are already structured fields).
- The spell `use` coverage is read from the generated pack (`content/systems/srd5e/packs/srd-5.2.1/spells/*.yaml`), read only: 176 with `use`, 163 without, as the task said.
- Script: [tools/srd-audit/audit.py](../tools/srd-audit/audit.py). Download the source files into `tools/srd-audit/src/` first (not committed; the names and commit are in `SrdDownloader.cs`), then from that folder run `python3 -I audit.py` (flags: `--src`, `--pack`, `--tables`, `--out`). It writes `flagged.jsonl` (one object per category and entry: category, entry, type, snippet of at most 20 words) and `summary.json`. `show.py <category prefix> <n>` prints samples.
- Hand labels from the Haiku sort: [tools/srd-audit/labels.txt](../tools/srd-audit/labels.txt) (`idx|label|reason`, against this run's `flagged.jsonl`); `build_sorted.py` joins them back (`SRD_AUDIT_DIR` names the folder with `flagged.jsonl`).

## Headline table

Design pieces are those in FEATURE_GRANTS_DESIGN.md: A sheet numbers, B attacks/gear/free castings, C counters and dice, D choices inside a feature.

| Category | Count | Piece | Does the planned shape fit? |
|---|---|---|---|
| Spells with 2+ damage rolls | 11 (+4 with other extra dice) | `use` editor | `use` holds one roll; all 11 lose the second. Needs a list of rolls |
| Spells with a beam/dart/ray count | 6 | `use` editor | No shape yet. `use` has no count. Not in A-D |
| Spells whose slot level adds targets or beams | 23 (+ Eldritch Blast by cantrip level) | `use` editor | Only 3 add beams/bolts (Magic Missile, Scorching Ray, Chain Lightning); 20 add targets and need no sheet data |
| Flat healing (a number, no dice) | 2 (Heal 70, Mass Heal 700) | `use` | `heals` + a number works; today's `use.damage` is a dice string |
| Damage type chosen at casting | 8 (+4 that choose a resistance, not damage) | `use` | `damageType` has no "chosen" value |
| Spells without `use` | 163 | importer | See buckets below. 139 have no dice at all; 14 have dice, 10 a flat number |
| Skill/save/armor/weapon proficiency | 5 fixed + 8 choice | A | Only 2 are truly unconditional (Slippery Mind, Disciplined Survivor). Fits |
| Expertise | 5 (all "of your choice") | A | Fits as a pick; no fixed expertise exists |
| Resistance to damage types | 8 fixed + 10 choice or depends | A/D | Fixed fits. The 10 depend on a choice (Dragonborn, Fiendish Legacy, Elemental Affinity) |
| Condition immunity | 3 | not planned | No piece lists it. Display only |
| Ability score increase from a feature | 2 fixed (Primal Champion, Body and Mind) | A | Fits. The 8 "choice" hits are the feat path (ASI, Boons) |
| Free castings (no slot, at will, once per rest, rituals) | 16 + 3 + 2 + 1 (overlap) | B | Mostly fits. See notes: 6 let the player choose the spell |
| Option menus in a feature | 21 | D | About 10 are real build-time choices; the rest are per-use menus that stay text |
| Spends another feature's pool | 23 features | C | Fits |
| Feature-owned dice | 10 features | C | Fits if the importer takes the numbers from the Levels file, not the text |
| Dice that grow by level (in text) | 7 | C | Does not fit `atLevels` on one row (see below) |
| Extra Attack | 7 | B | Fits |
| Critical range | 2 real (Improved, Superior Critical) | B | Fits only if the bonuses do not add up |
| Attack/damage bonus on some actions | 14 | B | Only 3 fit `on` modifiers. The rest are extra-damage riders |
| Gear conditions | 17 | B | `equipped("armor")` is too coarse |
| Reactive rules | 12 Reaction, 6 "when you hit", 2 "drop to 0" | C | Stay text, as planned |
| Speed / AC / darkvision (reference) | 12 / 5 / 3 | A (done) | Already expressible |
| Class-table columns missing | 11 classes, 18 columns | importer | The source has them (see below) |
| Monster attacks as prose | 440 attacks in 1038 actions | importer | 93% parse; 63% are one plain attack plus one damage |
| Invocation, Metamagic, Maneuver options as entries | 0 of 0 | D | Not in the source at all |

## Spells

**Two or more rolls (11).** Acid Arrow (4d4 now, 2d4 later), Vitriolic Sphere (10d4 then 5d4), Ice Knife (1d10 piercing, 2d6 cold burst), Ice Storm (2d10 Bludgeoning and 4d6 Cold), Wall of Ice, Weird, Tsunami, Conjure Elemental, Storm of Vengeance, Conjure Celestial, Prismatic Spray. In 9 `use.damage` is the first roll and the other is dropped; in Conjure Celestial and Prismatic Spray `use` holds a roll that is one mode out of several. Ice Storm and Ice Knife show the damage types differ per roll, so one `damageType` per spell is wrong for them. Four more spells carry dice that are not damage (Arcane Hand, Earthquake, Teleport, Wish).

**Beams and darts (6).** Magic Missile (three darts), Scorching Ray (three rays), Eldritch Blast (beams at levels 5, 11, 17), Chain Lightning (three bolts, plus one per slot level), Prismatic Spray (eight rays, each a different effect), Storm of Vengeance. The pack's `use` for Magic Missile is `1d4+1 Force` with no count; Eldritch Blast is `1d10 Force` with no beams; Scorching Ray is `2d6` with no count. Three more hits (Moonbeam, Ray of Enfeeblement, Delayed Blast Fireball) are a single beam and are false positives.

**Slot level adds targets or beams (23).** 20 are the "target one additional creature" kind (Bless, Bane, Hold Person, Longstrider). Those need no sheet data. Only Magic Missile (+1 dart), Scorching Ray (+1 ray) and Chain Lightning (+1 bolt) change what the dice are rolled for.

**Flat healing (2).** Heal ("restoring 70 Hit Points") and Mass Heal (700, divided). Three more spells have flat Hit Point numbers that are not healing (Death Ward, Aura of Life, Spare the Dying: "regains 1 Hit Point", "drops to 1").

**Damage type chosen (8).** Chromatic Orb ("damage of the chosen type"), Dragon's Breath, Sorcerous Burst ("a type you choose"), Forbiddance, Glyph of Warding, Conjure Elemental, Alter Self, Resistance. Chromatic Orb's pack `use` has dice and `perSlot` but no `damageType`. Four more choose a Resistance, not damage (Protection from Energy, Hallow, Wish, Summon Dragon).

**The 163 spells without `use`:**

| Bucket | Count | Examples |
|---|---|---|
| No dice, no number (utility, buffs, wards) | 133 | Aid, Alarm, Arcane Eye, Barkskin |
| No dice, summon with a stat block | 6 | Animate Dead and similar |
| Flat Hit Point numbers | 10 | Heal, Mass Heal (rest are not rolls) |
| Dice that add to a roll (not damage) | 3 | Bless, Guidance, Resistance (+1d4) |
| Dice only in cantrip scaling | 2 | Shillelagh, True Strike |
| Dice for duration, counts or a table | 5 | Blink, Control Weather, Reincarnate, Time Stop, Heroes' Feast |
| Healing or temporary Hit Points with dice | 3 | False Life (2d4+4), Prayer of Healing, Regenerate (4d8+15) |
| Dice in a shape not parsed | 1 | Forbiddance (5d10, type chosen) |

So only 14 of the 163 have dice at all (3 +1d4 dice, 2 cantrip rows, 5 duration/count/table dice, 3 healing, 1 Forbiddance), and 10 more have a flat Hit Point number (Heal and Mass Heal are the only real healing). The other 139 have nothing to roll. The design's "use for the 163" is mostly a non-task: for 139 of them there is no data to write.

## Features, traits, feats

**Proficiency (13: 5 fixed, 8 choice).** Choice: Skilled, Skillful, Bonus Proficiencies (three skills), Primal Knowledge, Weapon Mastery (Paladin, Ranger, Rogue), Scholar. Of the "fixed": Disciplined Survivor (all saving throws) and Slippery Mind (Wisdom and Charisma saves) are really unconditional. Divine Order and Primal Order give Martial weapons and armor training only if you pick one of two options (D). Keen Senses is "Insight, Perception, or Survival" (a pick from three). Background grants are already structured fields, so they need no text work. By kind: skills 8, weapons 5, saves 2, armor 2, tools 1, languages 0.

**Expertise (5).** Bard, Rogue, Ranger Expertise, Deft Explorer, Scholar. All are "of your choice" among skills you are proficient in. There is no fixed expertise in the SRD, so this is a pick, not an `adds` entry list.

**Resistance (8 fixed, 10 choice/depends).** Fixed: Rage (three physical types, only while raging), Dwarven Resilience (Poison), Superior Defense (all but Force), Boon of the Night Spirit, Tiefling traits. Depends on a choice: Dragonborn Damage Resistance rows (by ancestry), Fiendish Resilience and Superior Hunter's Defense (chosen at use), Elemental Affinity (chosen at level 6), Nature's Ward (land type). Rage's resistance and Superior Defense are conditional ("while your Rage is active"), and `when` on the feature needs a state for that. No damage immunity exists in the feature text; 3 features give condition immunity (Mindless Rage, Aura of Courage, Aura of Devotion), two of them an aura that also covers allies.

**Ability score increases (2).** Primal Champion and Body and Mind: +4 to two scores, maximum 25. Fits the `max` modifier. Grappler and the seven Boons and the Ability Score Improvement feat are the feat path.

**Free castings.** Spell without a slot: Divine Intervention, Natural Recovery, Paladin's Smite, Faithful Steed, Favored Enemy (twice), Dragon Companion, Contact Patron, Mystic Arcanum, Spell Mastery, Signature Spells, Elven Lineage, Fiendish Legacy, Gnomish Lineage (x2), Magic Initiate. At will: Spell Mastery, Dragon Companion (material). Ritual: Ritual Adept. These need per-count and per-recovery shapes: "once", "twice" (Favored Enemy), "Proficiency Bonus times" (Gnomish Lineage), and "once per Long Rest" with a regain rule. Six let the player choose the spell (Mystic Arcanum, Magic Initiate, Spell Mastery, Signature Spells, Natural Recovery, Divine Intervention), so the planned per-pick `free` choice is not optional. The Wizard's Spell Mastery also casts "at the lowest level", which has no place in the shape.

**Option menus (21).** Build-time picks on the feature row (D): Divine Order, Primal Order, Blessed Strikes, Elemental Fury, Hunter's Prey, Defensive Tactics, Draconic Ancestry, Giant Ancestry, Gnomish Lineage, Circle of the Land (land type, changes on a long rest). The rest are options picked at the moment of use and stay text: Rage's extend options, Open Hand Technique, Cunning Strike, Devious Strikes, Cunning Action, Fast Hands, Self-Restoration, Abjure Foes.

**Spending (23 features).** Pools: Focus Points 7 (Monk), Sorcery Points 4, Channel Divinity 3, Wild Shape 3, Bardic Inspiration 2, Lay On Hands 2, Rage 1, Second Wind 1. The Levels file has a column for every owner except Bardic Inspiration (uses equal Charisma modifier) and Lay On Hands (5 times Paladin level, no column). Lay On Hands also spends 5 points from the pool as a fixed price (Poisoned, Restoring Touch), so `amount: ask` needs a second fixed price on the same pool. Uses can be regained by spending a different pool (Dragon Wings: "unless you spend 3 Sorcery Points", Intimidating Presence: "expend a use of your Rage"). A counter regaining by spending another pool is not in the design.

**Feature-owned dice (10) and level scaling (7).** Bardic Inspiration die, Martial Arts die, Sneak Attack, Rage damage bonus all have columns in the Levels file (`bardic_inspiration_die`, `martial_arts_die`, `sneak_attack {dice_count, dice_value}`, `rage_damage_bonus`), so the importer does not need the text. The text-only ones scale differently: Brutal Strike 1d10 becomes 2d10 through a second feature (Improved Brutal Strike, level 17), Divine Strike/Blessed Strikes 1d8 to 2d8 (Improved Blessed Strikes, level 14), Primal Strike likewise. In these the upgrade is a different feature row, not an `atLevels` map on one row. Only Land's Aid, Breath Weapon (5 rows, one per ancestry) and Bardic Inspiration scale in a single feature's text.

**Extra Attack (7).** Barbarian, Fighter (5, 11 "Two Extra Attacks", 20 "Three Extra Attacks"), Monk, Paladin, Ranger. Fits: Fighter's later rows say "attack three times" and "four times", so the derived value must be set (max), not added. Same pattern in **Critical range**: Improved Critical (19-20, level 3) and Superior Critical (18-20, level 15) are both in the Champion's feature list at level 15, so adding them would give an 18-20 range plus a +1; the amounts must combine as a minimum. A third hit (Survivor, "18-20 on a Death Saving Throw") is a false positive.

**Attack/damage bonuses (14).** Only three are the "+N to attacks or damage of some actions" the design plans for: Archery (+2 to ranged weapon attacks), Two Weapon Fighting (add the modifier to the off-hand damage), Sacred Weapon (add Charisma to attack rolls with that weapon). The other 11 are extra-damage riders: Sneak Attack (1d6, once per turn, with Advantage or an adjacent ally), Brutal Strike, Improved Brutal Strike, Radiant Strikes (+1d8 Radiant), Divine Strike, Primal Strike, Hunter's Prey (+1d8 to a hurt target), Frenzy (Rage damage dice), Rage Damage, Boon of Irresistible Offense. These add dice to a hit under a condition, once per turn or per use. The planned `modifiers: [{target: attackBonus, ...}]` shape cannot hold them; they need a "damage rider" with dice, a condition and a frequency. They also own dice (Sneak Attack) so this overlaps with C.

**Gear conditions (17).** Typical: "aren't wearing Heavy armor" (Rage, Fast Movement, Roving), "aren't wearing any armor" (Unarmored Defense, Draconic Resilience), "aren't wearing armor or wielding a Shield" (Monk Unarmored Defense, Unarmored Movement, Acrobatic Movement), "unarmed or wielding only Monk weapons" (Martial Arts), "wearing Light, Medium, or Heavy armor" (Defense feat), "holding with two hands" (Great Weapon Fighting), "if you aren't carrying the weapon" (Sacred Weapon). So `equipped("armor") == 0` covers 3 or 4 cases. The rest need a category (heavy), a shield test, a weapon tag (Monk weapon) or a hand count. The design leaves hands and grip as text; Great Weapon Fighting and Martial Arts then stay text. Add `equipped("heavy armor")` and `equipped("shield")` to the vocabulary at least.

**Reactive rules.** 12 features mention a Reaction (Cutting Words, Uncanny Dodge, Deflect Attacks, Retaliation and others); 2 are "drop to 0 Hit Points" (Relentless Rage, Relentless Endurance); 6 fire when you hit (Stunning Strike, Blessed Strikes). All stay text with their counters, as planned.

**Reference (already expressible).** Speed 12 (several are Fly/Climb speeds or Speed reductions, not changes), armor class 5 (Unarmored Defense x2, Draconic Resilience, Defense), darkvision 3.

## Class-table columns

`srd521-class-tables.json` has only: `cantrips`, `featSlots`, `features`, `level`, `maxSpellLevel`, `pactSlotLevel`, `preparedSpells`, `slots`. The `Levels` source (`5e-SRD-Levels.json`) carries `class_specific` for all 11 non-Wizard classes, 20 levels each, and the importer does not read it:

| Class | Columns in the source |
|---|---|
| Barbarian | `rage_count`, `rage_damage_bonus`, `weapon_mastery` |
| Bard | `bardic_inspiration_die` |
| Cleric | `channel_divinity_charges` |
| Druid | `wild_shape_uses` |
| Fighter | `second_wind_uses`, `weapon_mastery` |
| Monk | `martial_arts_die`, `focus_points`, `unarmored_movement_bonus` |
| Paladin | `channel_divinity_charges` |
| Ranger | `favored_enemies` |
| Rogue | `sneak_attack` (dice_count, dice_value) |
| Sorcerer | `sorcery_points` |
| Warlock | `eldritch_invocations` |

Not in the source: Druid's Wild Shape Max CR and Known Forms, Paladin's Lay On Hands pool, Bard's Inspiration uses. The `eldritch_invocations` column counts how many to pick, but not which. Values for the early levels are 0 where the feature does not exist yet (Cleric Channel Divinity at level 1), which is faithful to the table.

## Monsters

341 monsters, 1038 actions, bonus actions, reactions and legendary actions. 440 are prose attack rolls ("Melee Attack Roll: +9, reach 15 ft. Hit: 12 (2d6 + 5) Bludgeoning damage."). Other actions: 188 Multiattack, 175 save-based, 235 others. 422 of the 440 also have structured `damage` and `attack_bonus` fields in the source, so the importer could read those instead of the text.

- One regex (header with bonus, reach or range, first damage) matches 410 of 440 (93%). The 30 that miss are mostly "reach 5 feet" spelled out (Djinni), a flat damage with no dice (Awakened Shrub: "1 Slashing damage"), or text with an odd middle clause.
- Fully plain (one damage type, no alternative damage, no recharge): 276 of 440 (63%). The rest of the shapes:
  - Two or more damage parts joined by "plus": 136 (Assassin: 7 Piercing plus 17 Poison). Three of these are a flat 1 Piercing plus dice (Spider, Scorpion, Flying Snake).
  - "or" alternative damage (versatile, or a bigger roll on a condition): 13 (Chimera Bite, Mimic Bite).
  - "Melee or Ranged" with reach and range: 23 (Gladiator Spear, Bugbear Stalker Javelin).
  - A saving throw in the same action: 18 (Cockatrice Petrifying Bite, Death Dog Bite).
  - A condition rider in the text (Grappled, Prone and so on): 94, on top of the damage.
  - A conditional Advantage written into the attack bonus (Ankheg Bite, Mimic Bite): 9.
  - Recharge in the name: none in attack prose (the recharge actions are save-based).

## Invocations, Metamagic, Maneuvers, Fighting Styles

Not itemizable from the source. A search of every feature, feat, subclass and spell text (and the Classes, Equipment, Magic-Items and Monsters files) finds none of the SRD's invocations (Agonizing Blast, Pact of the Blade, Lessons of the First Ones and the rest), none of the ten Metamagic options (Careful Spell, Twinned Spell and so on), and no Maneuvers (the SRD has no Battle Master). The Warlock's `Eldritch Invocations` and the Sorcerer's `Metamagic` rows are only the intro, with a count column. Fighting Styles are separate entries: Archery, Defense, Great Weapon Fighting and Two Weapon Fighting are 4 `fighting-style` feats; Blessed Warrior and Druidic Warrior are inline in the Paladin and Ranger Fighting Style text. So D's "class-option lists with invocations" would need the option text from the SRD 5.2.1 document itself (another source, as with the 11 spell fixes in `SpellTextFixes.cs`); the pinned JSON cannot supply it. (Only the files listed under "Source" were searched; the other 5e-bits files were not downloaded.)

## Surprises

1. **The Levels file already holds the class-table counters** (Sorcery Points, Martial Arts die, Rage count and damage, Bardic die, Sneak Attack dice, Channel Divinity, Wild Shape, Focus Points, Second Wind, Invocations), 20 levels each. The design says the "source transcription" has them; it is this file. No text reading needed.
2. **Invocations, Metamagic and Maneuvers are not in the source.** The class-option list piece (D) cannot be fed by the importer from this data.
3. **Most "attack and damage bonus" text is an extra-damage rider, not a flat modifier.** 3 of 14 fit the planned `on` modifier.
4. **Few of the 163 spells without `use` have a roll to write.** 139 have no dice at all; 14 have dice, mostly not damage.
5. **Critical range and Extra Attack are set-to-maximum, not additive.** Improved and Superior Critical both sit in one feature list. Fighter's Extra Attack rises by name, not by +1.
6. **Features are listed twice in the source.** `Features` holds subclass features under the class (46 of 232), and `Subclasses` embeds them again (58). Counting both gives 12 features twice. The script counts each once.
7. **Free castings are common but a third let the player choose the spell.**
8. **Condition immunity and aura grants (to allies) exist** and are not in the design (Aura of Courage, Aura of Devotion).
9. **Monster attacks already have structured fields** (`damage`, `attack_bonus`) for 422 of 440 prose attacks.
10. **Proficiency grants that are fixed are rare**: 2 unconditional. The design's "under 10" is right, and low.

## False positives and misses

- Counts are per distinct entry and category, not per occurrence, from regexes tuned by hand against samples. No category was verified entry by entry.
- Proficiency/fixed-vs-choice looks at one sentence. Sentences that offer a choice in a neighbouring sentence count as fixed (Divine Order, Primal Order, Keen Senses).
- "ASI from feature" fixed lists Grappler (a pick of Strength or Dexterity); that one is a false positive.
- Spell "two rolls" ignores dice that only appear in the scaling sentence. It could miss spells where the second roll is written without the word damage near it, and it flags mode spells (Conjure Celestial) as two rolls.
- Beams/darts: "unnamed" hits are false positives (Moonbeam, Ray of Enfeeblement, Delayed Blast Fireball). Spells that say "a number of rays" in other words are missed.
- Flat healing needs "regain/restore N Hit Points". Wording like "heals 20 Hit Points" or "Hit Points equal to 10" is not caught. 
- Option menus and spend counts take any "one of the following" or "expend", which includes per-use menus; a person has to split build-time from use-time choices.
- Gear conditions matches "while ... wearing / wielding / holding". It also hits "training with Heavy armor" in Divine Order and Primal Order. Some conditions without those words (for example "Armor Class while Shield is equipped") are missed.
- Feature text for the 11 spells that `SpellTextFixes.cs` replaces comes from the raw transcription here (the fixed text is not applied), so those spells may be mis-counted.
- Monsters: "clean" means the main regex matched and no extra damage or alternative was found; flat-damage and spelled-out "feet" cases fall out as irregular, which overstates irregularity a little.

## The Haiku sort

Haiku read the text of each of the 288 flagged feature and spell records (all but the 133 spells with no dice or number, and the reference counts) and labelled it against the planned shape: **136 fit, 123 break, 26 false positives, 3 unsure**. It reports; the calls below are mine, checked against its examples.

Why shapes break, most common first:

1. **The player chooses (34).** Every expertise row, the chosen proficiencies, chosen resistances and the feat-path increases. They need the choice pieces (D) or a pick list, not fixed `adds`.
2. **Damage type chosen per cast (13).** Chromatic Orb, Dragon's Breath, Conjure Elemental: `use` needs a "chosen from" list of types.
3. **Extra dice on a hit (11).** Sneak Attack, Brutal Strike, Blessed Strikes, Divine Smite-like riders. Not an `on` bonus: a feature-owned roll (C) shown beside the attack, applied by the player.
4. **A target the shapes do not name (8).** Bless and Guidance (other creatures' rolls), Warding Bond, Bardic Inspiration. They stay text; a feature's own dice can roll them.
5. **Choices made each time, not at build (8).** Hunter's Prey, Open Hand Technique, Circle of the Land spells. Per-use menus stay text.

Unsure: Heal and Mass Heal (does `use` take a bare number), Arcane Apotheosis (a spend that costs nothing). Fits rest on two assumptions: `use` can scale a count by slot or cantrip level, and a spell's `use` can take `atLevels` dice.
