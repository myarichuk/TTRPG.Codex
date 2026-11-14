# SRD grants import: design (Step 10)

Written 2026-11-13 on branch `feat/sheetkinds-feature-sources`. This is step 10 of the order in
[FEATURE_GRANTS_DESIGN.md](FEATURE_GRANTS_DESIGN.md): the importer pass that writes real grants into the generated SRD pack.
Nothing here is built. It was checked against the engine code (`SheetRuleEngine.Features.cs`, `SheetRuleEngine.Ranks.cs`,
`FeatureGrantData.cs`, `FeatureGrantCheck.cs`, `FeatureGrantVocabulary.cs`), `content/systems/srd5e/system.yaml`, the importer
(`tools/Codex.ContentImport/Srd5eImporter.cs`) and the generated pack.

## 1. What this step does

Today almost every published SRD entry carries its mechanics as text. The importer writes only two row choices: the Circle of
the Land's land type and the species lineages. This step teaches the importer to write grants on feature rows:

- pools with refills (Rage, Focus, Channel Divinity, Wild Shape, Sorcery Points and others);
- Breath Weapon as one pool across the Dragonborn lineages;
- Expertise as rank choices; fixed saving throws;
- Extra Attack and critical range as modifiers that keep the best value;
- damage resistances and granted spells.

Everything else stays text, with a stated reason, so a player still applies it by hand.

## 2. What the engine already does (checked in code)

| Need | Built? | Where |
|---|---|---|
| A counter on a row: `uses: { max, key, recoversOn, recovers, regain }` | Yes | `SheetRuleEngine.Feature`, `EvaluateFeatures` |
| A shared pool: rows naming one `uses.key` share one counter | Yes. The highest maximum wins and the sheet warns (`Actor.Rule.CounterKeyShared`) | `EvaluateFeatures` |
| A spender: `spends: { key, amount }`, `amount: ask` | Yes. Warns `Actor.Rule.SpendNoPool` when no pool has the key | `EvaluateFeatures` |
| Part refills, rolled refills, `initiative`, `dawn` | Yes. `when` is `empty` or a formula, worked out at evaluation with the row's own view (`classLevel` works) | `ResolveRecovers` |
| Extra Attack and critical range | Yes. Derived values `attacks` (1) and `critFrom` (20); types `highest` (max) and `lowest` (min) in `modifierTypes` | `system.yaml` |
| Skill and save ranks, fixed and chosen; `minRank`; Confirm for adopted ticks | Yes. Only a choice with `minRank` adopts | `SheetRuleEngine.Ranks.cs` |
| Row choices: `options` + `choose` on a row; `choose` on a source | Yes | `SheetRuleEngine.RowChoices.cs` |
| Granted spells and resistances: `adds` to `grantedSpells` and `resistances` | Yes. A spell the player already held stays theirs; only what the grant added is removed later | `ApplyGranted` |
| Level gates on `adds.level` and on rows | Only on sources with a `byField`. Species and feat sources have none, so a gated add there **never applies** | `AddApplies`, `AddFeatures` |
| Formulas with `?:` | Yes (fitd's `LoadMax` uses one) | `SheetFormulas` |
| Attribute, skill or save modifiers; `on` action bonuses; free castings; gear conditions; feature dice; weapon and armor training as grants; tools and languages lists | **No** | |

**Breath Weapon needs no engine change.** Only the picked lineage row is a feature, so the ten rows can all name
`uses.key: breath-weapon` and only one is ever active. The shared-key warning never fires. The source's `onChange: keep`
already keeps what was spent when the ancestry changes (`ChangeChoice`, `ApplyCounters`).

## 3. Rules for every mapping

1. **One owner row per pool.** The row that first grants the feature owns the pool and carries all its scaling. A later row
   of the same name (Action Surge 17, Indomitable 13 and 17) carries nothing. Two owners would warn and lose the second's
   refills.
2. **Scaling lives in `uses.max`.** When the class table has a column, the importer writes a `classLevel` step formula from it
   (`classLevel >= 17 ? 6 : classLevel >= 12 ? 5 : classLevel >= 6 ? 4 : classLevel >= 3 ? 3 : 2`). When the value equals the
   class level it writes `classLevel`.
3. **Every pool has an explicit `key`.** Keys are frozen once shipped. The engine only migrates a counter from the keyless
   default key (`LegacyKeys`), never from an earlier explicit key. A later rename needs its own migration.
4. **Granted spell lists come from a reviewed table**, transcribed from the SRD 5.2.1 document, not from the row text. The
   transcription is broken in places (the Oath of Devotion Spells row is spliced with the paladin spell list).
5. **No grant without its condition.** A bonus that holds "while raging" or "without heavy armor" stays text until the engine
   can test that condition. A wrong number is worse than a missing one.
6. **One path per effect.** Defense keeps `armorClassBonus`; Dwarven Toughness keeps `hitPointsPerLevel`; Versatile keeps
   `extraOriginFeats`; Unarmored Defense keeps the class's `unarmoredDefense`. No modifier duplicates them.
7. **Row counts never change.** Translation packs match rows by index and need equal counts (`EntryDisplay.Row`). The importer
   adds keys to rows (and `options` inside a row) but never adds, splits, merges or reorders rows.
8. **"Of your choice" is always a choice**, never fixed `entries`.

## 4. How mappings are expressed

### Data, not code

One reviewed file, `tools/Codex.ContentImport/Data/srd521-grants.yaml`, embedded like `srd521-class-tables.json`. It is keyed
by entry type, entry id, row name and, where names repeat, level (Expertise, Indomitable, Mystic Arcanum, Metamagic). It
classifies **every** row of every class, subclass, species and feat: either a grant or `text` with a reason code.

```yaml
class:
  barbarian:
    - row: Rage
      level: 1
      uses: { key: rage, max: { column: rage_count }, recovers: [{ on: Short Rest, amount: 1 }, { on: Long Rest }] }
    - { row: Extra Attack, level: 5, modifiers: [{ target: attacks, amount: 2, type: highest }] }
    - { row: Danger Sense, text: advantage }
subclass:
  life-domain:
    - { row: Life Domain Spells, spells: { 3: [aid, bless, cure-wounds, lesser-restoration], 5: [mass-healing-word, revivify],
        7: [aura-of-life, death-ward], 9: [greater-restoration, mass-cure-wounds] } }
    - { row: Preserve Life, spends: { key: cleric-channel-divinity, amount: 1 } }
  circle-of-the-land:
    - row: Circle of the Land Spells
      options:
        Arid: { spells: { 3: [blur, burning-hands, fire-bolt], 5: [fireball], 7: [blight], 9: [wall-of-stone] },
                adds: [{ to: resistances, entries: [fire], level: 10 }] }
species:
  dragonborn:
    lineages:
      - { row: "Draconic Ancestor: Black", adds: [{ to: resistances, entries: [acid] }], uses: { $ref: breath-weapon } }
pools:                      # shared definitions, named once
  breath-weapon: { key: breath-weapon, max: proficiencyBonus, recoversOn: [Long Rest] }
```

Shorthands the importer expands:

| Shorthand | Becomes |
|---|---|
| `max: { column: X }` | A `classLevel` step formula from the Levels file's `class_specific.X` (or `classLevel` when equal) |
| `spells: { 3: [a, b] }` | `adds: [{ to: grantedSpells, entries: [a, b], level: 3 }]`; no level key for a list with no level |
| `uses: { $ref: name }` | The `pools` entry of that name, copied |
| `text: reason` | Nothing written. The row is counted as text |

### Code

- `GrantTable.cs` (new): loads the file, matches rows, expands shorthands, merges each map into its row.
- `ClassColumns.cs` (new): reads `class_specific` from `5e-SRD-Levels.json` (already downloaded; `Classes()` reads that file
  but not this field) and builds the step formulas.
- Applied after the rows are built, and after `SplitLandTypes`, so the land options exist.
- The importer **throws**, and writes nothing, when:
  - a mapped row matches no row, or a row is classified twice;
  - a pack row is not in the file (a new or renamed SRD row must be classified);
  - a spell id is not in the Spells index (watch the curly apostrophe: `dragons-breath`);
  - a damage type is not in `vitals.damage.types`; a modifier target is not in `FeatureGrantVocabulary.Targets`;
  - a `recovers.on` or `changesOn` is not a rest or a moment; a formula fails `FeatureGrantVocabulary.CheckFormula`;
  - a `uses` has no `key`; two entries name one key outside the shared pools; a `spends.key` has no owner in its class,
    subclass's class or species;
  - `FeatureGrantCheck.Problems(system, entry, requireId: true)` reports anything.

### Keeping it reviewable

- The data file is the review surface. A reviewer reads one line per row, beside the SRD text.
- The generated pack diff shows the result per entry. The importer never touches text, so the diff is grants only.
- A coverage test counts the file's statuses and fails when they change without the numbers in section 7 being updated.

## 5. Pools

| Key | Owner row | Max | Refill | Spenders |
|---|---|---|---|---|
| `rage` | Barbarian Rage 1 | column `rage_count` | Short Rest 1, Long Rest all | |
| `persistent-rage` | Persistent Rage 15 | 1 | Long Rest | |
| `bardic-inspiration` | Bardic Inspiration 1 | `max(1, mod(cha))` | Long Rest all; Short Rest all `when: "classLevel >= 5"` (Font of Inspiration) | Cutting Words, Peerless Skill |
| `cleric-channel-divinity` | Cleric Channel Divinity 2 | column `channel_divinity_charges` | Short Rest 1, Long Rest all | Preserve Life |
| `divine-intervention` | Divine Intervention 10 | 1 | Long Rest | |
| `wild-shape` | Wild Shape 2 | column `wild_shape_uses` | Short Rest 1, Long Rest all | Wild Companion, Land's Aid, Nature's Sanctuary |
| `wild-resurgence` | Wild Resurgence 5 | 1 | Long Rest | |
| `natural-recovery` | Natural Recovery 6 | 1 | Long Rest | |
| `second-wind` | Second Wind 1 | column `second_wind_uses` | Short Rest 1, Long Rest all | Tactical Mind |
| `action-surge` | Action Surge 2 | `classLevel >= 17 ? 2 : 1` | Short Rest all, Long Rest all | |
| `indomitable` | Indomitable 9 | `classLevel >= 17 ? 3 : classLevel >= 13 ? 2 : 1` | Long Rest | |
| `focus-points` | Monk's Focus 2 | `classLevel` | Short Rest all, Long Rest all | Deflect Attacks 1, Stunning Strike 1, Disciplined Survivor 1, Superior Defense 3, Quivering Palm 4 |
| `uncanny-metabolism` | Uncanny Metabolism 2 | 1 | Long Rest | |
| `wholeness-of-body` | Wholeness of Body 6 | `max(1, mod(wis))` | Long Rest | |
| `lay-on-hands` | Lay On Hands 1 | `5 * classLevel` | Long Rest | Lay On Hands `ask`, Restoring Touch 5 |
| `paladin-channel-divinity` | Paladin Channel Divinity 3 | column `channel_divinity_charges` | Short Rest 1, Long Rest all | Sacred Weapon, Abjure Foes |
| `holy-nimbus` | Holy Nimbus 20 | 1 | Long Rest | |
| `free-divine-smite` | Paladin's Smite 2 | 1 | Long Rest | |
| `free-find-steed` | Faithful Steed 5 | 1 | Long Rest | |
| `free-hunters-mark` | Favored Enemy 1 | column `favored_enemies` | Long Rest | |
| `tireless` | Tireless 10 | `max(1, mod(wis))` | Long Rest | |
| `natures-veil` | Nature's Veil 14 | `max(1, mod(wis))` | Long Rest | |
| `stroke-of-luck` | Stroke of Luck 20 | 1 | Short Rest, Long Rest | |
| `innate-sorcery` | Innate Sorcery 1 | 2 | Long Rest | |
| `sorcery-points` | Font of Magic 2 | `classLevel` | Long Rest | Sorcery Incarnate 2 |
| `sorcerous-restoration` | Sorcerous Restoration 5 | 1 | Long Rest | |
| `dragon-wings` | Dragon Wings 14 | 1 | Long Rest | |
| `free-summon-dragon` | Dragon Companion 18 | 1 | Long Rest | |
| `magical-cunning` | Magical Cunning 2 | 1 | Long Rest | |
| `free-contact-other-plane` | Contact Patron 9 | 1 | Long Rest | |
| `dark-ones-own-luck` | Dark One's Own Luck 6 | `max(1, mod(cha))` | Long Rest | |
| `hurl-through-hell` | Hurl Through Hell 14 | 1 | Long Rest | |
| `arcane-recovery` | Arcane Recovery 1 | 1 | Long Rest | |
| `intimidating-presence` | Intimidating Presence 14 | 1 | Long Rest | |
| `breath-weapon` | each Draconic Ancestor row (shared) | `proficiencyBonus` | Long Rest | |
| `draconic-flight` | Draconic Flight (row `level: 5`) | 1 | Long Rest | |
| `giant-ancestry` | each Giant Ancestry row (shared) | `proficiencyBonus` | Long Rest | |
| `large-form` | Large Form (row `level: 5`) | 1 | Long Rest | |
| `stonecunning` | Stonecunning | `proficiencyBonus` | Long Rest | |
| `free-speak-with-animals` | Gnomish Lineage: Forest Gnome | `proficiencyBonus` | Long Rest | |
| `adrenaline-rush` | Adrenaline Rush | `proficiencyBonus` | Short Rest, Long Rest | |
| `relentless-endurance` | Relentless Endurance | 1 | Long Rest | |
| `boon-of-fate` | Boon of Fate (feat) | 1 | initiative, Short Rest, Long Rest | |
| `boon-of-spell-recall` | Boon of Spell Recall (feat) | 1 | Long Rest | |

- **Channel Divinity is per class** (`cleric-` and `paladin-`), so a Cleric/Paladin multiclass has two pools and no warning.
  Whether they should merge is open question Q5.
- **`free-<spell>` counters** stand for "cast it without a slot, once per rest". They do not cast; the spell is also in
  `grantedSpells`, so it can be cast with a slot. When free castings are built they must adopt these keys.
- Values the Levels file gives (`rage_count`, `channel_divinity_charges`, `wild_shape_uses`, `second_wind_uses`,
  `favored_enemies`, `focus_points`, `sorcery_points`) are read, not typed. Action Surge, Indomitable, Lay On Hands and Bardic
  Inspiration have no column; their formulas are in the data file.

## 6. Every entry

Status: **grant** (written), **held** (written once open question Q1 or Q2 is answered), **covered** (the effect sits on
another row), **already** (structured by an existing field or list), **text**.

### Rows every class has

| Row | Status | Why |
|---|---|---|
| Ability Score Improvement, Epic Boon | already | Feat slots (`featSlots`) and the feats list |
| `<Class>` Subclass, Subclass Feature | already | The subclass field; a placeholder row |
| Spellcasting, Pact Magic | already | Slot pools, lists, `spellcastingAbility` |
| Fighting Style | already | The `fightingStyles` list |
| Unarmored Defense (Barbarian, Monk) | already | The class's `unarmoredDefense` |
| Weapon Mastery (Barbarian, Fighter, Paladin, Ranger, Rogue) | text | Masteries are not a list the sheet holds |

### Text reasons

| Code | Meaning | Unblocked by |
|---|---|---|
| `rider` | Extra dice or damage on a hit, once per turn | Feature dice (C), `on` bonuses (B) |
| `die` | A die the feature owns (Martial Arts, Bardic die, Sneak Attack) | Feature dice (C) |
| `advantage` | Advantage, a reroll or a floor on a roll | Nothing planned |
| `reactive` | Fires on an event (a Reaction, dropping to 0) | Stays text |
| `per-use` | A menu chosen each use | Stays text |
| `gear` | Needs a gear condition (no heavy armor, no armor or shield) | Gear conditions (B) |
| `state` | Holds only while raging or similar | A state the sheet can test |
| `attribute` | Changes an ability score | Attribute modifiers (A) |
| `skill-bonus` | Adds to skill or save checks, not a rank | Skill and save modifiers (A) |
| `training` | Weapon, armor, tool or language training | Training ranks (A), `grantLists` |
| `chosen-spell` | The player picks the spell, often cast free | Free castings with a per-pick choice (B) |
| `not-in-source` | Options the pinned JSON does not have (invocations, Metamagic) | Another source |
| `moment-test` | Refill on initiative with a test the engine lacks ("fewer than 2", "none left" plus a level) | A richer `when` |
| `sense` | Blindsight, Truesight, Tremorsense, climb or fly speed: no sheet field | New fields |
| `aura` | Reaches allies | Stays text |
| `condition` | Condition immunity | A display list (not planned) |
| `other` | Narrative or table-judged | Stays text |

### Barbarian

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Rage | grant | pool `rage`. Damage bonus `rider`; resistances `state` |
| 2 | Danger Sense | text | `advantage` |
| 2 | Reckless Attack | text | `advantage` |
| 3 | Primal Knowledge | held (Q1) | `{ to: skills, choice: primal-knowledge-3, choose: 1, from: [animal-handling, athletics, intimidation, nature, perception, survival] }`. Rage-powered checks text |
| 5 | Extra Attack | grant | `{ target: attacks, amount: 2, type: highest }` |
| 5 | Fast Movement | text | `gear` (+10 speed without heavy armor) |
| 7 | Feral Instinct, Instinctive Pounce | text | `advantage`, `other` |
| 9, 13, 17 | Brutal Strike, Improved Brutal Strike | text | `rider` |
| 11 | Relentless Rage | text | `reactive` |
| 15 | Persistent Rage | grant | pool `persistent-rage` (the initiative refill of Rage is used by hand) |
| 18 | Indomitable Might | text | `advantage` |
| 20 | Primal Champion | text | `attribute` |

### Bard

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Bardic Inspiration | grant | pool `bardic-inspiration`. Die `die` |
| 2, 9 | Expertise | grant | `{ to: skills, choice: expertise-2 / expertise-9, choose: 2, minRank: proficient, rank: expert }` |
| 2 | Jack of All Trades | text | `skill-bonus` |
| 5 | Font of Inspiration | covered | Short Rest refill on `bardic-inspiration` |
| 7 | Countercharm | text | `reactive` |
| 10 | Magical Secrets | text | `chosen-spell` (list access) |
| 18 | Superior Inspiration | text | `moment-test` |
| 20 | Words of Creation | grant | `spells: [power-word-heal, power-word-kill]` |

### Cleric

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Divine Order | text | `training` / `skill-bonus` in both options; no option carries a grant |
| 2 | Channel Divinity | grant | pool `cleric-channel-divinity` |
| 5 | Sear Undead | text | `rider` |
| 7, 14 | Blessed Strikes, Improved Blessed Strikes | text | `rider`; no option carries a grant |
| 10 | Divine Intervention | grant | pool `divine-intervention`. Spell `chosen-spell` |
| 20 | Greater Divine Intervention | text | `other` (2d4 long rests) |

### Druid

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Druidic | grant | `spells: [speak-with-animals]`. Language `training` |
| 1 | Primal Order | text | `training` / `skill-bonus`; no option carries a grant |
| 2 | Wild Shape | grant | pool `wild-shape` |
| 2 | Wild Companion | grant | `spends: { key: wild-shape, amount: 1 }` |
| 5 | Wild Resurgence | grant | pool `wild-resurgence` (the slot exchange is text) |
| 7, 15 | Elemental Fury, Improved Elemental Fury | text | `rider` |
| 18 | Beast Spells | text | `other` |
| 20 | Archdruid | text | `moment-test` |

### Fighter

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Second Wind | grant | pool `second-wind`. Healing dice `die` |
| 2 | Action Surge | grant | pool `action-surge` |
| 17 | Action Surge | covered | on the level 2 row |
| 2 | Tactical Mind | grant | `spends: { key: second-wind, amount: 1 }` |
| 5 | Extra Attack | grant | `attacks` 2 `highest` |
| 5, 9, 13 | Tactical Shift, Tactical Master, Studied Attacks | text | `other`, `per-use`, `advantage` |
| 9 | Indomitable | grant | pool `indomitable` |
| 13, 17 | Indomitable | covered | on the level 9 row |
| 11 | Two Extra Attacks | grant | `attacks` 3 `highest` |
| 20 | Three Extra Attacks | grant | `attacks` 4 `highest` |

### Monk

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Martial Arts | text | `die` |
| 2 | Monk's Focus | grant | pool `focus-points` |
| 2 | Unarmored Movement | text | `gear` |
| 2 | Uncanny Metabolism | grant | pool `uncanny-metabolism` (the Focus refill is by hand) |
| 3 | Deflect Attacks | grant | `spends: { key: focus-points, amount: 1 }` (redirect). Reduction `reactive` |
| 4, 6, 7, 9 | Slow Fall, Empowered Strikes, Evasion, Acrobatic Movement | text | `reactive`, `other`, `reactive`, `gear` |
| 5 | Extra Attack | grant | `attacks` 2 `highest` |
| 5 | Stunning Strike | grant | `spends: { key: focus-points, amount: 1 }` |
| 10, 13 | Heightened Focus, Self-Restoration, Deflect Energy | text | `other`, `other`, `reactive` |
| 14 | Disciplined Survivor | grant | `{ to: saves, entries: [str, dex, con, int, wis, cha] }`; `spends: { key: focus-points, amount: 1 }` |
| 15 | Perfect Focus | text | `moment-test` |
| 18 | Superior Defense | grant | `spends: { key: focus-points, amount: 3 }`. Resistances `state` |
| 20 | Body and Mind | text | `attribute` |

### Paladin

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Lay On Hands | grant | pool `lay-on-hands`; `spends: { key: lay-on-hands, amount: ask }` |
| 2 | Paladin's Smite | grant | `spells: [divine-smite]`; pool `free-divine-smite` |
| 3 | Channel Divinity | grant | pool `paladin-channel-divinity` |
| 5 | Extra Attack | grant | `attacks` 2 `highest` |
| 5 | Faithful Steed | grant | `spells: [find-steed]`; pool `free-find-steed` |
| 6, 10, 18 | Aura of Protection, Aura of Courage, Aura Expansion | text | `aura` (`skill-bonus`, `condition`) |
| 9 | Abjure Foes | grant | `spends: { key: paladin-channel-divinity, amount: 1 }` |
| 11 | Radiant Strikes | text | `rider` |
| 14 | Restoring Touch | grant | `spends: { key: lay-on-hands, amount: 5 }` |

### Ranger

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Favored Enemy | grant | `spells: [hunters-mark]`; pool `free-hunters-mark` |
| 2 | Deft Explorer | grant | `{ to: skills, choice: deft-explorer-2, choose: 1, minRank: proficient, rank: expert }`. Languages `training` |
| 5 | Extra Attack | grant | `attacks` 2 `highest` |
| 6 | Roving | text | `gear`; climb and swim speeds `sense` |
| 9 | Expertise | grant | `{ to: skills, choice: expertise-9, choose: 2, minRank: proficient, rank: expert }` |
| 10 | Tireless | grant | pool `tireless`. Temporary hit points `die` |
| 13, 17, 20 | Relentless Hunter, Precise Hunter, Foe Slayer | text | `other`, `advantage`, `rider` |
| 14 | Nature's Veil | grant | pool `natures-veil` |
| 18 | Feral Senses | text | `sense` |

### Rogue

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1, 6 | Expertise | grant | `{ to: skills, choice: expertise-1 / expertise-6, choose: 2, minRank: proficient, rank: expert }` |
| 1 | Sneak Attack | text | `die` |
| 1 | Thieves' Cant | text | `training` (language) |
| 2, 3 | Cunning Action, Steady Aim | text | `per-use`, `advantage` |
| 5, 11, 14 | Cunning Strike, Improved Cunning Strike, Devious Strikes | text | `per-use` |
| 5, 7 | Uncanny Dodge, Evasion | text | `reactive` |
| 7 | Reliable Talent | text | `advantage` |
| 15 | Slippery Mind | grant | `{ to: saves, entries: [wis, cha] }` |
| 18 | Elusive | text | `other` |
| 20 | Stroke of Luck | grant | pool `stroke-of-luck` |

### Sorcerer

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Innate Sorcery | grant | pool `innate-sorcery`. The +1 DC and Advantage `state` |
| 2 | Font of Magic | grant | pool `sorcery-points`. Slot exchange text (pool to pool) |
| 2, 10, 17 | Metamagic | text | `not-in-source` |
| 5 | Sorcerous Restoration | grant | pool `sorcerous-restoration` |
| 7 | Sorcery Incarnate | grant | `spends: { key: sorcery-points, amount: 2 }` |
| 20 | Arcane Apotheosis | text | `other` |

### Warlock

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Eldritch Invocations | text | `not-in-source` |
| 2 | Magical Cunning | grant | pool `magical-cunning` |
| 9 | Contact Patron | grant | `spells: [contact-other-plane]`; pool `free-contact-other-plane` |
| 11, 13, 15, 17 | Mystic Arcanum | text | `chosen-spell` |
| 20 | Eldritch Master | text | `other` |

### Wizard

| Lvl | Row | Status | Grant |
|---|---|---|---|
| 1 | Arcane Recovery | grant | pool `arcane-recovery`. The slot pick is the rest-time choice (not built) |
| 1 | Ritual Adept | text | `chosen-spell` (rituals) |
| 2 | Scholar | grant | `{ to: skills, choice: scholar-2, choose: 1, minRank: proficient, rank: expert, from: [arcana, history, investigation, medicine, nature, religion] }` |
| 5, 18, 20 | Memorize Spell, Spell Mastery, Signature Spells | text | `chosen-spell` |

### Subclasses

| Subclass | Lvl | Row | Status | Grant |
|---|---|---|---|---|
| Path of the Berserker | 3 | Frenzy | text | `rider` |
| | 6 | Mindless Rage | text | `state`, `condition` |
| | 10 | Retaliation | text | `reactive` |
| | 14 | Intimidating Presence | grant | pool `intimidating-presence` (the Rage-spend refill is by hand) |
| College of Lore | 3 | Bonus Proficiencies | held (Q1) | `{ to: skills, choice: bonus-proficiencies-3, choose: 3 }` |
| | 3 | Cutting Words | grant | `spends: { key: bardic-inspiration, amount: 1 }` |
| | 6 | Magical Discoveries | text | `chosen-spell` |
| | 14 | Peerless Skill | grant | `spends: { key: bardic-inspiration, amount: 1 }` |
| Life Domain | 3 | Disciple of Life | text | `rider` |
| | 3 | Life Domain Spells | grant | `spells: { 3: [aid, bless, cure-wounds, lesser-restoration], 5: [mass-healing-word, revivify], 7: [aura-of-life, death-ward], 9: [greater-restoration, mass-cure-wounds] }` |
| | 3 | Preserve Life | grant | `spends: { key: cleric-channel-divinity, amount: 1 }` |
| | 6, 17 | Blessed Healer, Supreme Healing | text | `rider` |
| Circle of the Land | 3 | Circle of the Land Spells | grant | Each land option gets `spells` by druid level 3/5/7/9 and, from level 10, `{ to: resistances, entries: [<type>], level: 10 }`: Arid fire, Polar cold, Temperate lightning, Tropical poison |
| | 3 | Land's Aid | grant | `spends: { key: wild-shape, amount: 1 }`. Dice `die` |
| | 6 | Natural Recovery | grant | pool `natural-recovery` |
| | 10 | Nature's Ward | covered | Resistance on the land options. Poisoned immunity `condition` |
| | 14 | Nature's Sanctuary | grant | `spends: { key: wild-shape, amount: 1 }` |
| Champion | 3 | Improved Critical | grant | `{ target: critFrom, amount: 19, type: lowest }` |
| | 3 | Remarkable Athlete | text | `advantage` |
| | 7 | Additional Fighting Style | already | The subclass's `featSlots` styles |
| | 10, 18 | Heroic Warrior, Survivor | text | `other` |
| | 15 | Superior Critical | grant | `{ target: critFrom, amount: 18, type: lowest }` |
| Warrior of the Open Hand | 3 | Open Hand Technique | text | `per-use` |
| | 6 | Wholeness of Body | grant | pool `wholeness-of-body`. Dice `die` |
| | 11 | Fleet Step | text | `other` |
| | 17 | Quivering Palm | grant | `spends: { key: focus-points, amount: 4 }` |
| Oath of Devotion | 3 | Oath of Devotion Spells | grant | `spells: { 3: [protection-from-evil-and-good, shield-of-faith], 5: [aid, zone-of-truth], 9: [beacon-of-hope, dispel-magic], 13: [freedom-of-movement, guardian-of-faith], 17: [commune, flame-strike] }` |
| | 3 | Sacred Weapon | grant | `spends: { key: paladin-channel-divinity, amount: 1 }`. Attack bonus waits for `on` |
| | 7 | Aura of Devotion | text | `aura`, `condition` |
| | 15 | Smite of Protection | text | `aura` |
| | 20 | Holy Nimbus | grant | pool `holy-nimbus` |
| Hunter | 3–15 | all five rows | text | `per-use`, `rider`, `reactive` |
| Thief | 3–17 | all five rows | text | `per-use`, `sense`, `advantage`, `other` |
| Draconic Sorcery | 3 | Draconic Resilience | grant | `{ target: HP_Max, amount: classLevel }`; `{ target: armorClass, amount: "10 + mod(dex) + mod(cha)", type: base }` |
| | 3 | Draconic Spells | grant | `spells: { 3: [alter-self, chromatic-orb, command, dragons-breath], 5: [fear, fly], 7: [arcane-eye, charm-monster], 9: [legend-lore, summon-dragon] }` |
| | 6 | Elemental Affinity | held (Q2) | `options`: Acid, Cold, Fire, Lightning, Poison, each `adds: [{ to: resistances, entries: [<type>] }]`; `choose: { id: elemental-affinity }`. The damage bonus is `rider` |
| | 14 | Dragon Wings | grant | pool `dragon-wings` (the 3-point alternative is text) |
| | 18 | Dragon Companion | grant | pool `free-summon-dragon` (the spell comes from Draconic Spells) |
| Evoker | 3–14 | all five rows | text | `other`, `rider` |
| Fiend Patron | 3 | Dark One's Blessing | text | `reactive` (temporary hit points) |
| | 3 | Fiend Spells | grant | `spells: { 3: [burning-hands, command, scorching-ray, suggestion], 5: [fireball, stinking-cloud], 7: [fire-shield, wall-of-fire], 9: [geas, insect-plague] }` |
| | 6 | Dark One's Own Luck | grant | pool `dark-ones-own-luck` |
| | 10 | Fiendish Resilience | held (Q2) | `options`: the 12 damage types but Force, each a resistance; `choose: { id: fiendish-resilience, changesOn: [Short Rest, Long Rest] }` |
| | 14 | Hurl Through Hell | grant | pool `hurl-through-hell` |

### Species

Two sources change in `system.yaml` (see E1): species traits and lineages are gated by the character level.

| Species | Row | Status | Grant |
|---|---|---|---|
| Dragonborn | Darkvision (60 ft.) | already | `darkvision` field |
| | Draconic Flight | grant | row `level: 5`; pool `draconic-flight`. Fly speed `sense` |
| | 10 lineage rows (Draconic Ancestor: …) | grant | `adds: [{ to: resistances, entries: [<type>] }]`; pool `breath-weapon` (shared). Black acid, Blue lightning, Brass fire, Bronze lightning, Copper acid, Gold fire, Green poison, Red fire, Silver cold, White cold. Breath dice `die` |
| Dwarf | Darkvision (120 ft.) | already | |
| | Dwarven Resilience | grant | `{ to: resistances, entries: [poison] }`. Save advantage `advantage` |
| | Dwarven Toughness | already | `hitPointsPerLevel` |
| | Stonecunning | grant | pool `stonecunning`. Tremorsense `sense` |
| Elf | Darkvision (60 ft.) | already | |
| | Elven Lineage | text | The intro; the lineage rows carry the grants |
| | Fey Ancestry, Trance | text | `advantage`, `other` |
| | Keen Senses | held (Q1) | `{ to: skills, choice: keen-senses, choose: 1, from: [insight, perception, survival] }` |
| | Drow | grant | `{ target: darkvision, amount: 120, type: base }`; `spells: [dancing-lights]`, `{ 3: [faerie-fire], 5: [darkness] }` |
| | High Elf | grant | `spells: [prestidigitation]`, `{ 3: [detect-magic], 5: [misty-step] }`. Cantrip swap text |
| | Wood Elf | grant | `{ target: speed, amount: 35, type: base }`; `spells: [druidcraft]`, `{ 3: [longstrider], 5: [pass-without-trace] }` |
| Gnome | Darkvision (60 ft.) | already | |
| | Gnomish Cunning, Gnomish Lineage | text | `advantage`; the intro |
| | Forest Gnome | grant | `spells: [minor-illusion, speak-with-animals]`; pool `free-speak-with-animals` |
| | Rock Gnome | grant | `spells: [mending, prestidigitation]`. Clockwork device text |
| Goliath | Giant Ancestry | text | The intro |
| | 6 lineage rows | grant | pool `giant-ancestry` (shared). Effects text |
| | Large Form | grant | row `level: 5`; pool `large-form` |
| | Powerful Build | text | `advantage` |
| Halfling | Brave, Halfling Nimbleness, Luck, Naturally Stealthy | text | `advantage`, `other` |
| Human | Resourceful | text | `other` (Heroic Inspiration) |
| | Skillful | held (Q1) | `{ to: skills, choice: skillful, choose: 1 }` |
| | Versatile | already | `extraOriginFeats` |
| Orc | Adrenaline Rush | grant | pool `adrenaline-rush`. Temporary hit points text |
| | Darkvision (120 ft.) | already | |
| | Relentless Endurance | grant | pool `relentless-endurance` |
| Tiefling | Darkvision (60 ft.) | already | |
| | Fiendish Legacy | text | The intro |
| | Otherworldly Presence | grant | `spells: [thaumaturgy]` |
| | Abyssal | grant | resistance poison; `spells: [poison-spray]`, `{ 3: [ray-of-sickness], 5: [hold-person] }` |
| | Chthonic | grant | resistance necrotic; `spells: [chill-touch]`, `{ 3: [false-life], 5: [ray-of-enfeeblement] }` |
| | Infernal | grant | resistance fire; `spells: [fire-bolt]`, `{ 3: [hellish-rebuke], 5: [darkness] }` |

The once-per-rest free castings of the lineage spells stay text: a row holds one `uses`, and most lineages have two free spells.
The Forest Gnome has one, so it gets a counter.

**The transcription drops lineage details.** It has no levels for lineage spells, and its Wood Elf and Rock Gnome rows name
fewer spells than the SRD 5.2.1 tables. The data file follows the SRD document, and the review step checks each lineage row
against it (open question Q6).

### Backgrounds

| Background | Status | Why |
|---|---|---|
| Acolyte, Criminal, Sage, Soldier | already | Skills are a fixed rank source (`backgroundProperty`); the origin feat is `originFeat`; the scores are `bonuses`. The tool is `training` text |

Class skill picks also stay on today's path: the player ticks them and `classChoicesProperty` caps the count. Turning them into
choices gains nothing and would make every existing character choose again (open question Q8).

### Feats

| Feat | Status | Grant or reason |
|---|---|---|
| Ability Score Improvement, Grappler | already | `abilityIncrease` path. Grappler's rest is `advantage`, `other` |
| Alert | grant | `{ target: initiative, amount: proficiencyBonus }`, once E1b reads the origin feat. Initiative Swap text |
| Archery, Two-Weapon Fighting | text | Wait for `on` bonuses |
| Boon of Combat Prowess, Dimensional Travel, Irresistible Offense, Truesight, the Night Spirit | text | `other`, `rider`, `sense`, `state`. Their +1 is the `abilityIncrease` path |
| Boon of Fate | grant | pool `boon-of-fate` |
| Boon of Spell Recall | grant | pool `boon-of-spell-recall` |
| Defense | already | `armorClassBonus` |
| Great Weapon Fighting, Savage Attacker | text | `rider` |
| Magic Initiate | text | `chosen-spell`: a choice on a non-ranked list is refused by `FeatureGrantCheck`. A second copy would also share a keyless counter |
| Skilled | text | Skills or tools: a choice only goes to a ranked list, and tools are not one |

## 7. Coverage

Counted per row from the generated pack (classes 258 rows, subclasses 58, species traits 30, lineage rows 24, feats 17,
backgrounds 4: 391 in all). The script that counted is the same classification the data file will hold.

| Group | Rows | Grant | Held | Covered | Already | Text |
|---|---|---|---|---|---|---|
| Classes | 258 | 52 | 1 | 4 | 123 | 78 |
| Subclasses | 58 | 23 | 3 | 1 | 1 | 30 |
| Species traits | 30 | 7 | 2 | 0 | 8 | 13 |
| Lineage rows | 24 | 24 | 0 | 0 | 0 | 0 |
| Feats | 17 | 3 | 0 | 0 | 3 | 11 |
| Backgrounds | 4 | 0 | 0 | 0 | 4 | 0 |
| **All** | **391** | **109** | **6** | **5** | **139** | **132** |

- Leaving out the 139 rows already structured or placeholders, 252 rows say something: **120 mapped** (109 written, 6 held,
  5 covered by another row) and **132 stay text**.
- "Grant" means the row gets at least one grant. Many keep part of their text (a Rage's damage bonus, a Breath Weapon's dice).
- What stays text, by reason: riders and owned dice about a third; advantage, reactive and per-use rules about a third;
  the rest is gear, state, attribute, chosen spells, senses, auras and options not in the source.

## 8. Shared engine changes

Step 10 needs **two** changes, both `system.yaml` edits. The rest are optional or for reconciliation with the FitD design.

| # | Change | Needed? | Size |
|---|---|---|---|
| E1 | `byField: level` on the species `traits` source and the species `lineages` source | **Required.** Without it, `adds.level` on a species row never applies (`AddApplies` needs a level key), and a trait row's `level: 5` is ignored. Rows without a level still apply. Option rows inherit their parent's level key (`AddRow` passes `levelKey` on), so the land options' gated spells and resistance already work | S, yaml only, no schema change |
| E1b | `{ field: originFeat }` in `sheetRules.features` | **Required for Alert.** Nothing reads the background's origin feat as a feature today; the `feats` list refuses origin feats, so Alert's grant would apply only as a Human's extra origin feat | S, yaml only |
| E2 | Adopt a rank-1 choice. When a choice has no `minRank` and nothing stored, count the player's own rank-1 ticks beyond the background's skills and the first class's `classChoicesProperty` allowance. If enough fit `from`, show Confirm, as Expertise does | Only if Q1 is answered "map them now" | S–M, in `SheetRuleEngine.Ranks.cs` |
| E3 | `uses.label`: the counter's name on the Table and in the log. Today it is the row's name (`Draconic Ancestor: Black`, not "Breath Weapon") | Optional | S |

Points the FitD design (`sheetKinds` feature sources, system-declared moments) must keep:

- **Moments are hardcoded today.** `FeatureRecovery.Moments` and `FeatureGrantCheck`'s `SystemDefinitionMoments` list
  `initiative` and `dawn` as constants. Step 10 uses only rests and `initiative` (Boon of Fate). If system-declared moments
  replace the constants, srd5e must still accept `initiative` and `dawn` without declaring them, or `system.yaml` must
  declare them before the reimport.
- **Refills on `changesOn`.** Fiendish Resilience (held) uses `changesOn: [Short Rest, Long Rest]`, checked against `rests`.
- **Counter keys.** Step 10 freezes the keys in section 5. Free castings, when built, adopt the `free-<spell>` counters
  instead of making `free_{feature}_{spell}` keys of their own.
- **Not needed by Step 10:** new moments, action `on` bonuses, free castings, feature dice, gear conditions.

## 9. The gaps the docs list as not built

| Gap | Step 10 needs it? | What happens to the entries |
|---|---|---|
| Free castings | No | The spell goes in `grantedSpells`; single free spells get a `free-<spell>` counter; the rest stays text |
| Action `on` bonuses | No | Archery, Two-Weapon Fighting, Sacred Weapon's bonus stay text (3 entries) |
| The Mage's spell list | No | A monster fix, unrelated to feature grants. Leave it for the monster pass |
| Eldritch Invocation entries | No | Not in the source. Eldritch Invocations, Metamagic stay text |
| Attribute, skill and save modifiers | No | Primal Champion, Body and Mind, Jack of All Trades, Aura of Protection stay text |
| Gear conditions | No | Fast Movement, Unarmored Movement, Roving stay text |
| Feature dice | No | Martial Arts, Sneak Attack, Bardic die, Breath dice stay text. The `class_specific` die columns are read later |

## 10. What the generated pack must pass

1. The importer's own checks (section 4): it throws before writing.
2. `SrdPackSmokeTests.LoadsWithoutWarnings`: the loader reports nothing (row `choose` checks included).
3. A new test: `FeatureGrantCheck.Problems(system, entry, requireId: true)` is empty for every class, subclass, species and feat.
4. A new sweep test: every class at levels 1 to 20 with its subclass, and every species with each lineage picked, evaluates with
   none of `Actor.Rule.FeatureError`, `Actor.Rule.CounterKeyShared`, `Actor.Rule.SpendNoPool`, `Actor.Rule.ChoiceStale`.
5. The golden corpus, regenerated after each step (`CODEX_GOLDEN=update`), with its diff reviewed like code. Then search
   `expected.txt` for `ChoicePending`, `ChoiceRowPending`, `CounterKeyShared`, `SpendNoPool` and `FeatureError`. The corpus
   sheets carry no `choice__` keys, so this is the migration test: only `ChoiceConfirm` may appear, and `ChoicePending` only for
   Expertise on sheets with no expertise ticks.
6. The translation pack tests (`ShippedTranslationPackTests`): every row kept, counts equal.
7. Schema docs: run `dotnet run --project tools/Codex.SystemCheck --no-build -- --schema docs` after any engine change (E2, E3).
   E1 changes no schema; the command must then produce no diff.

## 11. Live sheets

| What | What happens | Risk |
|---|---|---|
| Counters | Appear full the first time the sheet is recomputed. No published entry has counters today, so none is lost | Counters are written by `Recompute`, which only the sheet page runs. A character shows them at the Table after its sheet is opened once. See Q4 |
| Counter keys | Explicit from the first release | A later rename resets spent uses unless it adds a migration |
| Granted spells, resistances | Added to the sheet with `granted__` bookkeeping. A spell the player had already stays theirs | None |
| Speed, darkvision (`base`) | Raised only when lower | None |
| Attacks, critical range, Alert's initiative | Derived; shown at once | Numbers move: release note |
| Draconic Resilience | Hit points and armor class change | A player who compensated by hand (`armorClassMisc`, typed hit points) is counted twice: release note |
| Expertise | A sheet whose player ticked expertise shows **Confirm** (adoption). A sheet without the ticks shows "Choices to make" | A warning, never a save refusal |
| Rank-1 choices (held) | Would show "Choices to make" on every existing elf, human, Lore bard and barbarian 3+ | Held until Q1 |
| New row choices (held) | Would show a pending pick on Draconic 6+ and Fiend 10+ | Held until Q2 |
| Land type | Existing picks keep working (names unchanged); their spells and resistance appear | None |
| Lineage | Existing picks keep working; Breath Weapon and Giant Ancestry counters appear | None |

## 12. Translations

- **Grants carry no text.** Counter names, choice labels and option names come from the rows, which the translation packs
  already translate by index.
- **New options need translated rows.** `srd-5.2.1-ru` and `srd-5.2.1-he` must give `options` in the same order for: Circle of
  the Land Spells (4 lands; the ru and he rows have no options today, so this gap exists already), and, if Q2 is yes, Elemental
  Affinity (5) and Fiendish Resilience (12). Names only; the descriptions may stay empty.
- **No new `sheetRules.choices` labels.** Step 10 adds no source-level choice. E3 would add none either: a label is a row word.
- **No new UI strings** (resx), unless E2 or E3 adds a message. Then en/ru/he together, with a native review for Hebrew.
- **Translation packs must not copy grant keys** (`uses`, `adds`, `modifiers`, `spends`, `options` grants). The checker's
  "fields the rules read" error should cover them; build step 9 adds the keys to its list if it does not.
- Known limit: `ApplyCounters` stores the English row name in `<key>_Label`, and the session log shows it as stored.

## 13. Tests

| Test | Where | Checks |
|---|---|---|
| Step formula | `ImporterTests` | A fake `class_specific` column becomes a formula whose value matches the column at every level 1–20 |
| Row matching | `ImporterTests` | An unknown row, a row mapped twice, an unclassified row, an unknown spell id each throw |
| Shorthands | `ImporterTests` | `spells`, `$ref` and `column` expand as section 4 says |
| Coverage | new `SrdGrantCoverageTests` | The status counts in section 7; every pack row classified |
| Grant check | new | `FeatureGrantCheck.Problems` empty for every entry |
| Sweep | new | Section 10 item 4 |
| Breath Weapon | `FeatureGrantsTests`, against the pack | Pick Black, spend one, change to Red: one left spent, resistance moves from acid to fire |
| Pools | `FeatureGrantsTests` | Barbarian 1/3/6/12/17 rage maximum 2/3/4/5/6; a short rest gives one back; Bard 5 refills on a short rest, Bard 4 does not |
| Ranks | `FeatureGrantsTests` | Rogue 1 with two expertise ticks shows Confirm; Monk 14 is proficient in every save |
| Level gates (E1) | `FeatureGrantsTests` | Drow at level 2 has no Faerie Fire, at 3 has it; Dragonborn 4 has no Draconic Flight counter, 5 has it |
| Critical | `FeatureGrantsTests` | Champion 3 crit from 19, 15 from 18; Fighter 20 attacks 4 |

## 14. Risks

- **The transcription is wrong in places.** Spell tables and lineage details come from the SRD document, typed into the data
  file. A typo there is a wrong grant. The review step checks every spell list against the document.
- **Numbers move on live sheets** (attacks, critical range, initiative, hit points, armor class). Correct, but visible.
- **Hand compensation counted twice**, as in FEATURE_GRANTS_DESIGN.md's risks.
- **Frozen keys.** A wrong key shipped is hard to fix. The review step reads every key in section 5.
- **Repeated row names.** Matching by name and level, never by position. A new SRD revision that renames a row fails the import.
- **The FitD moments change** could break `initiative` and `dawn` for srd5e if the constants move (section 8).
- **The AI copies the pack.** Published entries become examples the drafter reads. Their shapes must pass the draft check,
  which item 3 of section 10 guarantees.

## 15. Build order

Each step is small, ends green, and is checked by the command in its last column. Regenerate the pack with
`dotnet run --project tools/Codex.ContentImport -- srd5e --source <dir> --out content/systems/srd5e/packs/srd-5.2.1`.

| # | Step | Check |
|---|---|---|
| 1 | E1: add `byField: level` to the species traits and lineages sources. E1b: add `{ field: originFeat }` to the feature sources. Tests: the level gate with a hand-made entry; a background naming a feat with a modifier applies it | Tests pass; the golden corpus does not change |
| 2 | `ClassColumns.cs`: read `class_specific`, build step formulas. Unit test only; nothing written yet | `ImporterTests` |
| 3 | `GrantTable.cs` and an empty `srd521-grants.yaml` that classifies every row as `text`. The importer checks (section 4) and the coverage test | Reimport: pack unchanged; coverage test passes with all text |
| 4 | Pools of section 5, owner rows only (no spenders) | Reimport; golden update; search for `CounterKeyShared`: none |
| 5 | Spenders | Reimport; golden; search for `SpendNoPool`: none |
| 6 | Extra Attack, critical range, Alert, Draconic Resilience, Wood Elf speed, Drow darkvision | Reimport; golden; the critical test |
| 7 | Resistances and granted spells, with the reviewed spell tables; land options with their spells and resistance; species level gates (`level: 5` rows) | Reimport; golden; Breath Weapon and level-gate tests |
| 8 | Expertise and fixed saves | Reimport; golden: only `ChoiceConfirm`, and `ChoicePending` for Expertise without ticks |
| 9 | Translation packs: land options in ru and he; the checker refuses grant keys | `ShippedTranslationPackTests`; `Codex.TranslationCheck` for both languages |
| 10 | Grant check and sweep tests over the whole pack | All tests |
| 11 | Review: every changed entry against the SRD 5.2.1 document (a person or a strong model), then Haiku over a sample. Fixes go into the data file, never into the pack | The review list is empty |
| 12 | Held items, once Q1 and Q2 are answered: E2 then the rank-1 choices; the two new row choices and their translations | As steps 8 and 9 |
| 13 | Docs: CHARACTER_FEATURES_GAPS.md ("No published entry carries…" is no longer true), FEATURE_GRANTS_DESIGN.md order step 10, release notes for moved numbers | Read through |

Steps 2 to 10 suit Sonnet. Step 11 needs Opus or a person. Step 12's E2 wants an Opus review of the adoption rule.

## 16. Open questions

1. **Q1. Rank-1 feature choices** (Keen Senses, Skillful, Primal Knowledge, Bonus Proficiencies). Mapped, they prompt every
   existing character with that build, because only a `minRank` choice adopts the player's ticks. Recommended: build E2 first,
   then map them, so existing sheets show Confirm. Otherwise they stay text for now.
2. **Q2. New row choices** (Elemental Affinity, Fiendish Resilience). They grant a resistance, but row choices have no
   adoption, so existing Draconic 6+ and Fiend 10+ characters see a pending pick. Recommended: map them; the notice never
   blocks a save and the pick is new information. Other build-time menus (Divine Order, Primal Order, Blessed Strikes, Elemental
   Fury, Hunter's Prey, Defensive Tactics) stay text, since no option carries a grant.
3. **Q3. Skilled.** Leave it text (skills or tools), or map it as three skills and accept that a tool pick leaves it pending?
   Recommended: text until tools are a list.
4. **Q4. Counters on sheets nobody opens.** Recommended for Step 10: a release note ("open each sheet once"). Later: recompute
   srd5e sheets once when the pack version changes.
5. **Q5. Channel Divinity across Cleric and Paladin.** Two pools (recommended, no warning) or one shared pool (the highest
   maximum, with a warning)?
6. **Q6. Lineage details the transcription drops** (spell levels, the Wood Elf's Druidcraft, the Rock Gnome's Mending). Take them
   from the SRD 5.2.1 document into the data file (recommended), or only what the JSON says?
7. **Q7. E3, `uses.label`.** Worth it now so the Table says "Breath Weapon", or later?
8. **Q8. Class and background skill picks as rank choices.** The brief asked for them; this design declines. Background skills
   are already a fixed rank source. Class picks are the player's ticks, capped by `classChoicesProperty`. As a choice they would
   need a class source read for the first class only (the class source runs once per class under multiclassing, and a second
   class gives fewer skills), and every existing character would be asked to choose again. Recommended: keep today's path.

## Decisions (2026-11-13)

- Q1 and Q8: build E2 first (a choice adopts the player's existing ticks, so old sheets show Confirm, not a fresh pick). Then class skill picks, Keen Senses, Skillful, Primal Knowledge and Bonus Proficiencies become choices.
- Q2: map Elemental Affinity and Fiendish Resilience now.
- Q3: Skilled stays text until tools are a list.
- Q4: recompute every sheet once when the pack version changes, so counters appear at the Table without opening each sheet.
- Q5: two Channel Divinity pools for a Cleric/Paladin.
- Q6: take lineage details from the SRD 5.2.1 document into the data file.
- Q7: build E3 (`uses.label`) now.
- Moments: use the shared resolver from docs/SHEETKINDS_FEATURES_DESIGN.md (S1); `initiative` and `dawn` stay built in.
