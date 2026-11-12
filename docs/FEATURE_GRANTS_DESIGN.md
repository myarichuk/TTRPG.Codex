# Closing the feature-grant gaps: design

Written 2026-11-12 on branch `feat/overhaul-character-creation-homebrewing`. It plans how to close "What a feature cannot
express yet" in [CHARACTER_FEATURES_GAPS.md](CHARACTER_FEATURES_GAPS.md), and the SRD importer pass that goes with it.
It merges three read-only design reviews (by Opus subagents); their main claims were checked against the code. Nothing here
is built yet.

## Why it matters

A feature that cannot express something keeps its text, so a player can still apply it by hand; the sheet's numbers are
then wrong until they do. For 5e play the gaps that bite by level 3 are skill and save proficiencies, weapon attack and
damage bonuses, Extra Attack, and spells a feature lets you cast without a slot. The other half of the problem is content:
no published SRD entry carries grants at all, because the importer writes none. Closing the engine gaps pays off only with
the importer pass at the end (see "Importer").

Decisions that stand: everything stays generic and in `system.yaml` (no 5e names in C#); no scripting for entries a model
writes; whatever is added is taught to the AI (`FeatureGrantGuide`), offered by the editor (`FeatureGrantVocabulary`,
`FeatureGrantEditor`), and where possible written by the importer.

## Found in the code (checked)

- **Bug (fixed 2026-11-12): Number-field grants with a formula that names a derived value were dropped.** `ApplyGranted` works amounts out with
  no derived values (`Amount(..., null, null)`, `SheetRuleEngine.Features.cs`). A formula naming `proficiencyBonus` throws,
  the exception is swallowed and nothing is written. `EvaluateFeatures`' `TargetKind.Field` branch then works the same
  modifier out with derived values and lists it in `result.Granted`, so the Features panel shows a grant the sheet never got.
  Now `ApplyGranted` works the derived values out from the sheet first (`WorkedOutDerived`), and both places turn modifiers
  into amounts through `ModifierAmounts`, so what is listed is what was applied; a failing formula is warned about once, by
  the evaluation. Tests in `FeatureGrantsTests`.
- **Skill grants already work in the engine.** An `adds.to` key that is not a pick list takes the `list == null` branch and is
  written to the sheet with `granted__` bookkeeping. Only the vocabulary hides it (it offers pick lists and damage fields).
- **A shared counter half-exists.** `uses.key` is read; a second feature naming the same key is skipped, so the first
  listed sets the maximum. `GrantUses` has no `Key`, the editor cannot set it, the guide does not mention it.
- **Features are evaluated before the inventory** (`Evaluate`: features, then slot pools, inventory, ..., actions), which is why
  no grant can depend on gear.
- **Table events do not run for srd5e**: `TableEvents.Handles` needs `SheetKinds`, so only systems such as fitd get them.
- **Importer:** Dragonborn `lineages` are imported as rows nothing reads; Circle of the Land's row has the whole intro as its
  name; `srd521-class-tables.json` lacks the class-specific columns (Sorcery Points, Bardic Die, Martial Arts, Rages,
  Channel Divinity) that the source transcription has.

## What the source audit changed

[SRD_SOURCE_AUDIT.md](SRD_SOURCE_AUDIT.md) counted the pinned SRD text and had Haiku sort the flagged cases. It changes the plan:

- **Extra Attack and critical range set a value, they do not add.** Improved and Superior Critical sit in one feature list;
  Fighter's Extra Attack rises to three and four attacks by name. Use a modifier type that keeps the highest (`type: set`,
  highest wins) instead of `base` modifiers that stack.
- **Gear conditions need armor categories.** Most say "no Heavy armor" or "no armor or Shield": `equipped(tag)` must reach
  the armor's category (light, medium, heavy) and shields, not just "armor".
- **Flat `on` bonuses are rare** (3 in the SRD: Archery, Two-Weapon Fighting, Sacred Weapon). The common case is extra dice on
  a hit (11: Sneak Attack, Brutal Strike, Blessed Strikes). Those are a feature's own roll shown beside the attack (C), which
  the player adds; `on` modifiers can wait behind feature dice.
- **Feature dice often grow through a second feature** (Improved Brutal Strike), not a level map on one row. A later feature
  needs a way to replace an earlier one's die, besides `atLevels`.
- **The class-table counters are already in the source** (the Levels file's class-specific values: Sorcery Points, Rages,
  Bardic and Martial Arts dice, Sneak Attack, Channel Divinity, Wild Shape, Invocations). The importer reads them without
  touching the text; counters (C) get real numbers cheaply.
- **Invocations, Metamagic and Maneuvers are not in the source.** Class-option lists (D) have nothing to import; they wait for
  another openly licensed source or stay homebrew. Row choices (Dragonborn, land type) are unaffected.
- **Player choice is the most common break** (34 of 123): every expertise grant, most proficiencies and resistances. Fixed
  `adds` cover few SRD entries; a pick list on the feature carries the rest, so the choice pieces (D) matter more than counted.
- **`use` needs more than an editor:** a list of rolls (11 spells have a second roll), a count for beams (Magic Missile, Scorching
  Ray, Eldritch Blast), a damage type chosen from a list (13), and a flat number for healing (Heal, Mass Heal).
- **Not in the plan yet:** condition immunity (3, a display list like resistances, small) and auras that reach allies (text).
- **Monster attacks have structured fields** (attack bonus, damage) for 422 of 440: the importer reads those, not the prose.

## The shared foundation: a generic action cost

Two reviews arrived at the same change independently. Today an `ActionCost` is "a slot of pool P at level L" and
`SheetAction.Spends` is slot-only. Generalise it to **"spend sheet key K by N, or nothing"**, with slot levels as one kind.
Free castings, spending Sorcery Points, and a feature's own dice all rest on it; `ActionsPanel` already spends by key
(`OnSpend(key, -1, ...)`), and `AdjustResourceCommand` takes any delta. Design it once, before the pieces that use it.
The command should refuse a spend larger than what is left (today it clamps at 0, so two spenders racing near 0 succeed).

## The pieces

Sizes: S small, M medium, L large.

### A. Sheet numbers

| Gap | Shape | Engine | Size |
|---|---|---|---|
| Derived values in field formulas (the bug) | none | `ApplyGranted` works out derived values first (a helper shared with `EvaluateFeatures`), passes a real `warn`; the Field branch reuses those amounts. One pass, never a fixed point | S |
| Skill proficiencies and expertise | `adds: [{to: skillProficiencies, entries: [perception]}]` | Grants live in the evaluation, not the sheet: union them in `EvaluateSkills` as locked ticks with their source, like background skills. Fix the `ChoiceCount` warning that would count them as chosen | S |
| Saves, armor, weapon training | `sheetRules.skills.savesKey`, `proficiencies.armorKey/weaponKey`; `adds` to them | Union with the first class's; `WeaponTrained` takes a set, so attack bonuses move | M |
| Tools, languages | `grantLists: [{key: tools}, {key: languages}]` | Display only, in the Features panel | S |
| Attribute, skill, save, spell DC/attack modifiers | `target: wis` with `max: 25`; `skill.perception`, `save.*`; `sheetRules.spellcasting.saveKey/attackKey` | Skills, saves, spellcasting take them through `Pending` like armor class. Attributes are resolved before attributes are evaluated, so their amounts may not read attributes or derived values (refused with a warning). Make `ApplyAttributeTotals` write `EvaluateAttributes`' totals so the two sums cannot drift | M |
| Ability score increases from a feature | Fixed ones are attribute modifiers with `max` (Primal Champion, Body and Mind). Chosen ones stay on the feats path until choices (D) exist | rides on the row above | S |
| `base` armor class under body armor | The skip is right for every SRD case. Add `armored`/`shielded` names for armor class `when`, with the gear piece (B) | | S, low value |

### B. Attacks, gear and free castings

| Gap | Shape | Engine | Size |
|---|---|---|---|
| Extra Attack, critical range | Derived values in `system.yaml` (`attacks`, `critFaces`), raised by `base` modifiers; `actions[].count` and `critical` | Works with today's derived values; the Actions panel shows "×2" and "crit 19–20" | S |
| Attack/damage/DC bonuses on some actions | `modifiers: [{target: attackBonus, amount: 2, on: {action: attack, tags: [ranged weapons]}}]`; `on` takes `action`, `tags`, `entry` | A new action target kind, kept in `Pending` with its filter and added in `EvaluateActions` (also to per-slot damage). `when` stays about the character, `on` about each action. `SheetAction` carries which feature added what | M |
| Gear conditions | `when: "equipped(\"armor\") == 0"` | An `equipped(tag)` builtin; move `EvaluateInventory` before `EvaluateFeatures` and make the inventory reachable from `ApplyGranted`. Hands and grip stay text | S–M, ordering risk |
| Spells without a slot | `adds: [{to: grantedSpells, entries: [hunters-mark], free: {max: proficiencyBonus, recoversOn: [Long Rest]}}]`, `free: atWill`; rituals as a free cost rule on the action source | A free casting spends a `free_{feature}_{spell}` counter through the generic cost. Player-chosen spells (Magic Initiate, Mystic Arcanum) need a per-pick `free` choice | M |
| A magic weapon's +N | An inventory choice `plus` (0–3) on the row, read by attacks and armor class. Equipped items as a full feature source is a separate, later task | | S–M |
| Slots granted by a feature | No SRD feature does it | Apply after `EvaluateSlotPools` | S, homebrew only, last or never |

### C. Counters and dice

| Gap | Shape | Engine | Size |
|---|---|---|---|
| Spending another feature's counter | `uses: {key: sorcery-points, ...}` on the owner; `spends: {key: sorcery-points, amount: 2}` on the spender (`amount: ask` for Lay on Hands) | A "Use (2)" button through the generic cost, disabled below the cost. Warn when two features set a maximum for one key | S–M |
| Dice a feature owns | `use` on a feature row (`damage: 1d6, atLevels: {5: 1d8}`); `actions: [{key: feature, features: true}]`; `uses.max` may take `atLevels` | Features become an action source; `atLevels` read from the feature's own class level, not the character's | M, needs the `use` editor |
| Reactive rules | Stay text. Show the feature's description beside its counter on the Table card. An optional `reminds: {on: down}` only if playtesting shows they get forgotten | | S |

### D. Choices inside a feature

- **The choice picks an entry** (fighting styles, invocations, Metamagic, maneuvers): a list, as fighting styles already are,
  with the class-option type from part 3 of [HOMEBREW_DRAFTING_DESIGN.md](HOMEBREW_DRAFTING_DESIGN.md). L.
- **The choice picks one of an entry's own rows** (Dragonborn ancestry, land type): `features: [{field: species, property:
  lineages, choice: lineage}]`; the sheet stores `choice__lineage` and only that row becomes a feature. An unmade choice is a
  sheet violation. The drafter (`ChooseAsync`) and the join review (`SheetReferences.Of`) must learn `choice__` keys. M.
- Decide whether changing a choice is build-time only (swapping mid-day can refill counters). The land type is the
  exception: it changes on a long rest.

### E. Refilling a pool

Today a counter refills in full on the rests it names (`uses.recoversOn: [Long Rest]`, rest names from `system.yaml`), and the
Table card's Regain adds one. The SRD needs more:

| Case | Example | Shape |
|---|---|---|
| Part of a pool | Wild Shape: one back on a short rest, all on a long rest; Hit Dice by half | `recovers: [{on: Short Rest, amount: 1}, {on: Long Rest, amount: all}]` |
| A rolled amount | A magic item "regains 1d6 + 1 charges at dawn" | `amount: 1d6+1`; `on: dawn` is a moment the GM triggers, like a party rest |
| On an event, not a rest | "When you roll Initiative and have no uses left, you regain one" | `{on: initiative, amount: 1, when: empty}` |
| Paid from another pool | Font of Magic (points into a slot and back); spend a slot to regain a use | No new shape: an action whose cost is pool A and whose effect fills pool B, through the generic cost |
| Chosen at the rest | Arcane Recovery: slots whose levels add up to half your wizard level | A choice in the rest dialog; later, with the rest dialog |
| Who refills by hand | The GM decides | `regain: gm` hides Regain from the owner; default owner and GM |

- `amount` takes `all`, a number, a formula or dice; `when` takes `empty` or a formula. `recoversOn: [X]` stays as the shorthand
  for `recovers: [{on: X, amount: all}]`.
- **The maximum moves**: it grows with level and features. When it drops, what is left drops with it; a change to the build
  never refills a pool for free.
- **Every refill goes in the session log**, so the GM sees who spent and regained what.
- Size: M (the recover rules, the initiative and dawn moments, `regain: gm`); the rest-time choice is its own S–M later.

## The `use` editor

Needed by C (feature dice) and listed as missing for spells and weapons. Build it once, for all three.

## Importer

Hold changes to the importer until the engine pieces it writes for are built, then do one pass and one reimport, so the
same 600-odd entries are reviewed once. The pack is generated, so this costs nothing in the meantime.

1. **Done: an audit of the source, writing nothing** ([SRD_SOURCE_AUDIT.md](SRD_SOURCE_AUDIT.md), scripts in `tools/srd-audit`). Deterministic checks over the pinned SRD text count what the text says
   that the data cannot hold yet: spells with two rolls, beam counts, flat healing, chosen damage types, monster attacks,
   "resistance to", "proficiency in", "without a spell slot", "choose one of", "spend N points". It tells which pieces matter
   most and tests the shapes above against real text before anything is built. Haiku can sort the flagged cases into
   categories; it reports, it does not decide.
2. **After the engine work:** teach the importer each shape as reviewed per-class or per-pattern tables, not broad regexes;
   every pattern refuses "of your choice". Also: class-table columns, the lineages rows, the Circle of the Land row, `use`
   for the 163 spells without one, monster attacks, magic weapons.
3. **Review the reimport:** the deterministic checks again, then Haiku over the flagged entries and a sample of clean ones,
   then a stronger model or a person decides each fix, and the fix goes into the importer, never by hand into the pack.

Expect few fixed grants for some pieces (under 10 fixed proficiency grants and 2 attribute increases in the SRD); their
value is mostly homebrew. The free castings, Extra Attack, critical range and the class-table counters are common.

## Order

1. The derived-values bug (A, done), and feature descriptions on Table cards (C).
2. The source audit (importer step 1, done: [SRD_SOURCE_AUDIT.md](SRD_SOURCE_AUDIT.md)).
3. The generic action cost (done: `ActionCost` spends N from any resource pool, a slot level or a feature's counter, or is free; an overspend is refused).
4. Extra Attack and critical range (B); skills and expertise (A).
5. Action-targeted bonuses `on` (B); attribute, skill, save and spell modifiers (A).
6. Shared counters and `spends` (C, done: `uses.key`, highest maximum wins with a warning, `spends: {key, amount | ask}`, key migration); refill rules (E, done: `recovers`, `regain: gm`, initiative and dawn moments; left: the rest-time choice and pool-to-pool actions); free castings (B).
7. The `use` editor; feature dice (C).
8. Gear conditions and the stage reorder (B); saves, armor and weapon training (A).
9. Row choices (D); then the class-option lists (D, with invocations).
10. The importer pass and reimport, then its review.
11. Later or never: magic weapon `plus` and items as a feature source, slot modifiers, `reminds`.

Who: the generic cost, the stage reorder and choices change the engine's shape; design them with Opus and review the
result. The rest is routine once the shape is set, and suits Sonnet; strings and editor fields can go to Haiku.

## Risks

- **Numbers on existing sheets move** when the reimport adds grants, and when weapon training is united (attack bonuses).
  Correct, but visible: release notes. Players who compensated by hand (+4 typed into a score, extra ticks) get counted twice.
- **Double counting between paths**: the Defense feat already adds through `armorClassBonus` and `bonusLists`. One path per
  effect; a test should fail when one feat yields two armor class lines.
- **Counter keys**: adding `key` to an existing counter renames it, so a live sheet's spent uses reset. Carry the value
  across once. Free-casting counters need keys that survive reimports and renames.
- **Ordering**: the gear reorder and the derived-first helper both touch the evaluation order; keep it one pass with no
  cycles, and refuse formulas that would need one.
- **The AI**: the model may put per-action filters in `when` or hard-code dice. The guide gives one example of each, and the
  formula check rejects an unknown `on.action`.
- **Translations**: new labels ("Attacks per action", "Free", "Ritual", choice names) need en/ru/he strings and system
  translations, with native review.

## Ranked lists and grant choices (design)

Written 2026-11-12. It replaces the A rows "Skill proficiencies and expertise" and the saves half of "Saves, armor, weapon
training", and pulls row choices (D) forward onto the same storage. Nothing here is built.

### 1. Ranks on a list

A ranked list is a fixed set of keys, each held at a rank. A system declares the ranks in order; rank 0 is "none". Each rank
names the sheet list that holds the keys the player set at it, and, optionally, what it is worth.

```yaml
# srd5e: no yaml change. With no ranks block, skills and saves get these defaults, on today's skill keys.
skills:
  bonusKey: proficiencyBonus
  ranks:
    - { key: proficient, label: Proficient, sheetKey: skillProficiencies, value: proficiencyBonus }
    - { key: expert,     label: Expert,     sheetKey: skillExpertise,     value: "proficiencyBonus * 2" }
  saves:                                   # saving throws: a ranked list over the attributes (new key, empty on live sheets)
    ranks: [{ key: proficient, label: Proficient, sheetKey: saveProficiencies, value: proficiencyBonus }]
```
```yaml
# A PF2-like system: four ranks, each worth level plus a step; saves rank the same way.
skills:
  ranks:
    - { key: trained,   label: Trained,   sheetKey: skillsTrained,   value: "level + 2" }
    - { key: expert,    label: Expert,    sheetKey: skillsExpert,    value: "level + 4" }
    - { key: master,    label: Master,    sheetKey: skillsMaster,    value: "level + 6" }
    - { key: legendary, label: Legendary, sheetKey: skillsLegendary, value: "level + 8" }
```

- **A key's own rank is the highest list holding it.** A lower list need not hold it too. Live 5e sheets already have every
  expert skill in the proficient list (the panel drops expertise with proficiency), so they read the same. No rewrite.
- **Why lists, not one map** (`skillRanks: {perception: 2}`): no migration at all, and `SkillsPanel`, `CharacterAssist`'s
  field specs and `ActorSheet`'s key list (line ~1083) already read lists. Four lists for PF2 cost nothing.
- **`value` is optional.** Without it a rank is worth its number, which a roll formula can read (`skills[x].rank`).
- **fitd does not move.** Its action dots are already a ranked list in the sheet-kinds engine: `keyedLists.actions` with a
  `rating` item field, 0 to 4, `display: dots`, read by the roll pool. No feature pipeline runs on fitd and no fitd content
  grants dots (playbooks are per game). When a sheet-kind system needs grants, the same `adds` shape becomes a `highest`
  modifier on `actions.<key>`, which keyed entries already take.
- **What else fits.** Saves fold in now: a fixed key set (the attributes), and PF2 ranks them. The first class's
  `savingThrows` becomes a fixed source at rank 1, as the background is for skills; players may now tick saves too (a
  feat's), as they tick skills. Armor and weapon training fit the
  shape (PF2 ranks them), but their keys are open names matched against tags, and `trained * proficiencyBonus` in the attack
  formula would become the rank's value, so attack numbers move. Fold them in later, with piece B's gear work. Tools and
  languages stay unranked `grantLists` (display only). No SRD 5.2.1 grant ranks a tool.

### 2. Grants

```yaml
adds:
  - { to: skills, entries: [perception], rank: proficient }    # fixed; no rank means rank 1, as adds do today
  - { to: saves,  entries: [wis, cha] }                        # Slippery Mind
  - { to: skills, entries: [stealth], raise: 1 }               # one step up, capped at the top rank
  - { to: skills, choice: expertise-1, choose: 2, minRank: proficient, rank: expert }          # Rogue Expertise (also expertise-6)
  - { to: skills, choice: keen-senses, choose: 1, from: [insight, perception, survival] }      # Keen Senses
  - { to: skills, choice: skilled, choose: 3, from: any }                                      # Skilled
  - { to: skills, choice: increase, choose: 1, raise: 1, maxRank: master, level: 3 }          # PF2 skill increase
```

- `rank` takes a rank key or number. `raise` and `rank` exclude each other.
- `from` takes keys, `any` (the default), or a filter on a key's own fields (`{attribute: [str, dex]}`).
- `minRank` and `maxRank` ("a skill you are proficient in") are the only conditions. There is no formula `onlyIf`: the two
  cover every SRD case, and the AI cannot get them wrong in ways a formula allows.
- **The choice id.** `choice` is required on a choice grant and must be unique within the entry (the loader and the draft
  check refuse duplicates). The picks are stored on the sheet as `choice__<entryId>__<choice>`, a list of keys.
  - A repeatable list pick (Skilled taken twice) adds its copy number: `choice__<entryId>#2__<choice>`.
  - Ids made from the row name or the grant's position would break on a reimport, a reorder or a translation. The editor
    and the importer default to the row's slug plus its level (`expertise-6`): Rogue has two rows named Expertise.
- **When the feature goes**, from a level drop or a feat removed: its picks stay on the sheet but count nothing, so they come
  back if the feature does. They are dropped when the entry leaves the sheet entirely (on save, prune `choice__` keys whose
  entry is no longer referenced).
- **When the source entry changes** (a `from` narrowed, a count lowered): picks are never changed quietly. A pick that no
  longer fits, or one too many, is a violation saying "choose again".
- **Adopting earlier ticks.** When a choice is unmade but the player's own ticks already put enough eligible keys at its
  rank (a Rogue who ticked expertise by hand before the reimport), the engine counts those as the picks and shows the
  choice as "confirm". Confirming writes the `choice__` key. Nothing is asked twice and no rank is lost.

### 3. The engine

A new `EvaluateRanks` replaces the skill and save parts of `EvaluateSkills`. It runs after `EvaluateFeatures` and derived
values, as skills do now. It makes one pass per list:

1. **Base:** the player's own ranks (the highest sheet list), then fixed sources: the background, the class's saves, and
   fixed `adds`. The highest wins. Ranks are not modifiers, so this is a plain max, not `sheetRules.stacking`.
2. **Choices with `rank`,** in order of `minRank`, lowest first. Each checks its `minRank`/`maxRank` against the ranks so far,
   leaving out its own picks. Skilled's picks can therefore feed Expertise, and nothing loops.
3. **Raises** last, fixed and chosen. Each counts once and stops at the top rank. Two raises stack (PF2 increases do).
4. **Value:** the rank's `value` formula, then skill and save modifiers (piece A), then passives.

- `SkillScore` gains `Rank`, `RankLabel` and `Sources` (rank, feature or entry name). `Proficient` and `Expert` stay as
  rank ≥ 1 and rank ≥ 2 for current callers.
- **The player's own ticks.** A granted rank shows as a locked tick with its source. The player may still set a higher rank;
  a tick at or below a granted rank stays stored but changes nothing. "Expertise without proficiency" stops being a warning,
  since the highest rank wins.
- **`ChoiceCount`** counts only the player's own rank-1 ticks beyond every granted or chosen one, not just the background's.
- **Unmade choices.** `SheetEvaluation.PendingChoices` lists `PendingChoice(key, label, source, count, made, options,
  rank)`. Each pending one is a **warning** with key `Actor.Rule.ChoicePending`, not a violation: under strict enforcement
  `ActorSheet.SaveNowAsync` refuses a player's save with violations, which would lock every Rogue out after the reimport.
  `LevelUpPlan.Build` leaves `ChoicePending` out of `NewViolations`, so a choice the level opens shows once, as a picker.
- **No double storage.** Ranked targets are left out of `ApplyGranted`'s `list == null` branch, since grants live only in the
  evaluation. A one-time cleanup on load removes entries listed in `granted__skillProficiencies` from that list and drops the
  key. It is rare: no published entry grants skills yet.

### 4. The UI

- **Sheet (`SkillsPanel`).**
  - Two ranks keep today's two checkboxes. More ranks become one select per skill, labelled with the skill's name.
  - A locked tick is checked and disabled, with "· Rogue: Expertise" in its label text (not only in a tooltip).
  - Pending choices show in a "Choices to make" block at the top of the panel, and as a badge on the feature in the
    Features panel. Each is a fieldset (legend "Expertise (Rogue 1): choose 2 skills you are proficient in") of checkboxes
    for the allowed keys only. A further pick is disabled once N are chosen, with "1 of 2 chosen" beside it.
  - A choice that is made folds to a line with Change. Picks are changed there, not on the skill rows.
- **Level-up stepper.** Today it only lists "N more X". Add the choices the next level opens (the after evaluation's pending
  minus the before's), using the same picker on the copy sheet. Apply works with a choice still open: it stays pending on
  the sheet. The same goes for subclass and list picks later. M.
- **Homebrew editor (`FeatureGrantEditor`).**
  - The add row gains a mode (fixed / raise / choice), a rank select, and entries as a multi-select of the list's keys.
  - For a choice: count, from (any / these / filter), min and max rank, and the choice id. The id defaults to a slug of the
    feature name, can be edited, and is checked for uniqueness.
  - `FeatureGrantVocabulary.GrantList` gains `Ranks` and its key set, so the editor and the guide offer the ranked lists.
- **Accessibility.** Use fieldset and legend, and announce the "chosen" count in a polite live region. Locked state and
  source are in text, not colour. Every control is reachable and named by keyboard, and RTL is checked for Hebrew.

### 5. The AI

- **Drafter (`AiFeatures.ChooseAsync`).** After the lists and fields are picked, re-evaluate and add one `ChoiceSpec` per
  `PendingChoice` (key = the choice key, max = count, options = the allowed keys with labels). Feats picked in the first
  round bring their choices, so this is at most one more call. `ParseChoices` needs no change: it already maps ids or names
  and caps at the count. `ChoiceOptions` needs an overload for keys that are not entries.
- **Guide (`FeatureGrantGuide`).** List each ranked list with its rank keys. Give one fixed example and one choice example.
  Say that "of your choice" is always a `choice` with `choose`, never fixed `entries`, and that the `choice` id is required.
  The draft check refuses a choice without an id or a count, and an unknown rank.
- **Join review.** `SheetReferences.Of` is unchanged: skill keys and row names are not entries. The review reads the
  evaluation instead. Today `VaultRequests` lists the violations and does not block Accept; add the `ChoicePending`
  warnings to that list, and show each feature's picks. The GM's Edit can change picks.

### 6. Row choices (from D)

Dragonborn ancestry and land type pick one of an entry's own rows: `features: [{field: species, property: lineages,
choice: lineage}]`. Use the same storage: `choice__<entryId>__lineage` holds the row's name. The same `PendingChoice` has the
rows as options, and the same picker, drafter loop and review check serve it. Only the picked row becomes an
`ActiveFeature`. **Recommended:** build it straight after the skill choices. With the storage shared it is S–M, not M.
The land type's change on a long rest is a later rest-dialog step.

### 7. Risks, migration, build order

- **Live sheets.** srd5e needs no yaml change, and no stored value is rewritten. Numbers move only when content grants
  ranks (the reimport) or when weapon training is folded in later. Choices the reimport adds adopt the player's own ticks
  (section 2), so existing characters are asked to confirm, not to choose again. A skill the player ticked and a feature now grants
  counts once, shown "also from X".
- **Repeatable picks.** Removing the first copy of a repeated feat renumbers the later copy's `#2` key. Its picks then show
  as "choose again" instead of moving silently. That is acceptable.
- **Translations:** rank labels and the choice strings, as in Risks above.

| # | Step | Size | Who |
|---|---|---|---|
| 1 | `ranks` on skills (defaulting to today's keys), loader checks, `EvaluateRanks`, `SkillScore.Rank/Sources` | M | Sonnet, after the stacking/action work lands (same files); Opus reviews the migration |
| 2 | Fixed and `raise` rank grants; leave ranked targets out of `ApplyGranted`; the `granted__` cleanup | S | Sonnet |
| 3 | Saves as a ranked list (class saves as a fixed source) | S | Sonnet |
| 4 | Choice grants: parsing, `choice__` keys, ordered evaluation, `PendingChoices`, violations, pruning | M | Opus designs the review; Sonnet builds |
| 5 | Sheet UI: locked ticks, rank select, choice picker, accessibility | M | Sonnet |
| 6 | Level-up stepper makes choices | M | Sonnet |
| 7 | Vocabulary, editor fields, guide text, draft check; strings | M | Sonnet; strings Haiku |
| 8 | Drafter loop and join review check | S–M | Sonnet |
| 9 | Row choices on the same storage | S–M | Opus reviews |
| 10 | Importer: the 5 expertise and 8 chosen proficiency grants, then the fixed ones | S | With the importer pass |
| — | Armor and weapon training as ranks | M | Later, with B's gear piece |

### 8. Open questions

**Decided 2026-11-12: all four as recommended** (lists per rank; a choice changes any time, logged; an unmade choice is a
warning; armor and weapon training with the gear work).

1. **Storage: one sheet list per rank (recommended), or one rank map per list?** Lists need no migration and keep every
   current reader working. A map is tidier for four-rank systems.
2. **When may a made choice change?** Recommended: any time from the sheet, by its owner, with the change in the session log.
   The level-up stepper is just the first place it is asked. Build-time-only locking adds states for little gain.
3. **Should an unmade choice be a warning or a violation?** Recommended: a warning, listed in the join review for the GM
   to judge. A violation would block saving under strict enforcement; a pick that no longer fits stays a violation.
4. **Armor and weapon training as ranks now or with the gear work?** Recommended: with the gear work. They move attack
   numbers and need the armor categories anyway.
