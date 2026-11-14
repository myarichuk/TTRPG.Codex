# Roll bonuses: design

Status: designed 2026-11-14, not built. It closes two gaps that earlier designs left open, and does it in one model:
srd5e's planned `on` bonuses (FEATURE_GRANTS_DESIGN.md, part B: "attack bonuses on some actions") and the opt-in bonuses
the Forged in the Dark system needs when the story decides them (SHEETKINDS_FEATURES_DESIGN.md, 3.4 and S6, open question 3).

## What it is

A feature or a condition can add to **some rolls**: "+2 to ranged weapon attacks" (Archery), "+1d4 to attack rolls and
saving throws" (Bless), "+1d to resistance rolls" (Forged in the fire). Some of these always apply. Others apply only when
the story says so: "+1d when you attack from hiding" (Ambush), "+1d4 to one ability check" (Guidance). The sheet cannot
tell when those hold, so the player ticks them before the roll.

**One idea covers both systems: a roll reads a set of targets.** A modifier applies to a roll when its `target` is in that
set. A named roll (`rolls:` in system.yaml) reads the targets its formulas name (`modifiers('@resistance')`). A sheet-rules
roll (an attack, a check, a save) reads a fixed set the engine names for it (see "Roll targets"). Nothing else is needed to
scope a bonus, so the `on: { action, tags }` shape planned in FEATURE_GRANTS_DESIGN.md is **dropped** in favour of
scoped targets.

## Data

```yaml
# Archery fighting style: always on, ranged weapon attacks only.
modifiers:
  - { target: attack.weapon.ranged, amount: 2 }

# Bless, as a condition effect the GM or player sets: always on while the condition is set. Dice, not a number.
conditionEffects:
  Blessed:
    modifiers:
      - { target: attack, amount: 1d4, source: Bless }
      - { target: save,   amount: 1d4, source: Bless }

# Guidance: a tick box on ability checks; using it ends the condition.
conditionEffects:
  Guided:
    modifiers:
      - { target: check, amount: 1d4, source: Guidance, optIn: { label: Guidance, ends: true } }

# Ambush (Forged in the Dark): a tick box on action rolls.
modifiers:
  - { target: '@pools', amount: 1, optIn: { label: Attacking from hiding } }

# Sacred Weapon: a tick box while it is up; ticking it on an attack adds the Charisma modifier.
modifiers:
  - { target: attack.weapon, amount: chaMod, optIn: { label: Sacred Weapon } }

# A bonus that costs a use when ticked (homebrew; the SRD's Bardic Inspiration belongs to another character).
modifiers:
  - { target: check, amount: 1d6, optIn: { label: Lucky charm, spends: { key: lucky-charm, amount: 1 } } }
```

**The rules:**
- `optIn` makes a modifier a tick box. It never counts on the sheet or in an always-on total. It counts in a roll only when
  the roll's targets include its target, its own `when` holds, and the player ticked it for this roll.
  - `label`: what the tick box says. Default: the modifier's source (the feature's or condition's name).
  - `spends: { key, amount }`: ticking it and rolling spends from that pool, the same pool `uses`/`spends` name (a
    `uses_*` counter or a resource). A tick box whose pool is short is disabled and says so.
  - `ends: true`: only on a condition's effects. Rolling with it ticked removes the condition.
- **Dice amounts.** `amount` may be dice (`1d4`, `-1d4`, `2d6`) where the roll is an expression roll (sheet-rules attacks,
  checks and saves). The dice are added to the roll's expression, never folded into a number on the sheet, so the sheet's
  attack bonus stays a number. In a pool roll (`pool:`), `amount` is a number of dice, as today; a dice amount there is a
  load error.
- `when` keeps its meaning: it is about the character (`level >= 5`, `input.attribute == 'resolve'`). The target scopes
  the roll. `optIn` is the player's say.
- The **source** of every bonus that counts is kept, and the roll's label lists the ones that came from dice or a tick:
  "Stealth check (+1d4 Guidance)". Plain always-on numbers stay folded in, as today.

## Roll targets

Named rolls (`rolls:`, both systems): the literal targets in the roll's `pool` and `add` formulas
(`modifiers('@resistance')`), plus what those reach today (a keyed entry's `@group`, see `SheetModel.Targets`). The engine
reads them from the formula text once per definition.

Sheet-rules rolls (srd5e). Names are generic roll words, never a system's own words. Each roll reads the targets listed,
from general to narrow:

| Roll | Targets | Where it is rolled today |
|---|---|---|
| Weapon attack | `attack`, `attack.weapon`, `attack.melee` or `attack.ranged`, `attack.weapon.melee` or `attack.weapon.ranged`, `attack.entry.<entry id>` | `ActionsPanel.UseAsync`, `TableCard.RollAsync` |
| Spell attack | `attack`, `attack.spell`, `attack.melee` or `attack.ranged`, `attack.spell.melee` or `attack.spell.ranged`, `attack.entry.<entry id>` | Same, and `SkillsPanel` (spell attack) |
| Damage | `damage`, `damage.weapon` or `damage.spell`, `damage.melee` or `damage.ranged`, `damage.entry.<entry id>` | `ActionsPanel.RollDamageAsync` |
| Saving throw | `save`, `save.<attribute>` | `SkillsPanel` |
| Ability check | `check`, `check.<attribute>` | `AttributesPanel` |
| Skill check | `check`, `check.<attribute of the skill>`, `check.skill.<skill key>` | `SkillsPanel` |

- Always-on **numbers** on these targets are added where the bonus is worked out today: `EvaluateActions` adds attack and
  damage targets to `SheetAction.AttackBonus` and to the damage expression (and to per-slot damage). The skill, save and
  check targets are added to the bonuses those panels already show. `SheetAction` gains `Bonuses`: which source added what.
- Always-on **dice** and every **opt-in** wait for the roll.
- Melee or ranged comes from the action's `use.attack`. The entry's tags are not targets: the SRD's Archery is "ranged
  weapon", which `attack.ranged` on a weapon covers.
- A modifier target that no roll reads and that is not a sheet value is a load warning, as unknown targets are today.

## Engine

1. `ModifierDefinition` (`SheetKinds.cs`), `FeatureModifier` and `SheetModel.Modifier` gain `OptIn` (`label`, `spends`,
   `ends`). The parse of feature grants (`ParseModifiers` in `SheetRuleEngine.Features.cs`), `FeatureGrantData` and
   `FeatureGrantCheck` round-trip and check it. `SystemDefinitionLoader` checks it on conditions' effects and a kind's own
   modifiers. `ends` is refused outside a condition's effects.
2. **Opt-ins never count by default.** `SheetModel.Stack` and the sheet-rules modifier loops skip an opt-in modifier
   unless its id is in the roll's ticked set. An opt-in's id is stable: `{source id}#{index}` for a feature, `condition:{name}#{index}` for a condition effect.
3. **What a roll offers**:
   - `SheetRolls.OptIns(system, resolver, sheet, rollKey, inputs)` lists the opt-ins a named roll offers.
   - `SheetRuleEngine.OptIns(sheet, RollSite)` lists those a sheet-rules roll offers. `RollSite` is the roll kind plus its targets (the table above).
   - Each result has: id, label, amount text, source, `spends` and whether it can be paid, and `ends`.
4. **Making the roll with ticks:**
   - Named rolls: `SheetRollRequest` gains `OptIns` (ticked ids). The pool's `modifiers(...)` then count them. The result gains `Bonuses`: source, label, amount, ticked or always.
   - Sheet-rules rolls: `ActionRoll` and `RollRequest` gain the always-on dice and the ticked opt-ins. The host appends their dice to the expression and the bonus to the label.
   - In both, a ticked `spends` is spent through the existing spend path. A ticked `ends` removes the condition: through a `SheetIntent` on the live table, or on the sheet when not live.
5. Group rolls: each participant's own opt-ins are not offered. The group roll has no tick boxes in this design.

## Sheet and table

- **Named rolls (the roll widget, `SheetWidget.razor` `case "roll"`):** under the inputs, a "Bonuses" group of checkboxes
  for the roll's opt-ins, unticked each time, each saying its label and amount ("Attacking from hiding: +1d"). A disabled one
  says why ("No uses left"). Always-on bonuses are listed as plain text above them, so the player sees why the pool is what
  it is.
- **Sheet-rules rolls:** the sheet's dice tray (where the roll mode select is) gains "Bonuses for the next roll": the
  sheet's opt-ins, each with the rolls it applies to ("Guidance: +1d4 on ability and skill checks"). A ticked one applies to
  the next roll it fits, and is then unticked. An opt-in that does not fit the next roll stays ticked. This keeps one
  click per roll, the way advantage already works.
- **Table cards:** the same tray, compact, in the card's roll area (`TableCard.razor`). The GM sees a creature's own.
- **Log:** the dice log and the session log show the bonuses that counted, with their sources.
- Strings in en, ru, he. Checkboxes are labelled, grouped in a `fieldset` with a legend, and keyboard-reachable. They work right to left. ru and he need native review.
- **The homebrew editor** (`FeatureGrantEditor`, `GrantModifierEdit`) round-trips `optIn` and lets the author tick "Player
  chooses at the roll", with the label and the pool to spend. Target pickers offer the roll targets above for sheet-rules
  systems, and a named roll's targets for kinds (`FeatureGrantVocabulary`).
- **AI:** `FeatureGrantGuide` and the world builder's `homebrew_fields` learn `optIn` and the roll targets.

## Data to write

srd5e (`tools/Codex.ContentImport`, `srd521-grants.yaml`, from the SRD 5.2.1 PDF; never by hand in the pack):

| Entry | Grant |
|---|---|
| Archery (fighting style) | `{ target: attack.weapon.ranged, amount: 2 }` (not `attack.ranged`, which would also catch ranged spell attacks) |
| Sacred Weapon (Channel Divinity) | opt-in on `attack.weapon`, amount `chaMod` (or the system's name for it), label Sacred Weapon |
| Bless, Bane | conditions `Blessed` and `Baned` with always-on `1d4` / `-1d4` on `attack` and `save` |
| Guidance, Resistance | conditions `Guided` (opt-in `1d4` on `check`, `ends`) and `Resisting` (opt-in `1d4` on `save`, `ends`) |

The four new conditions go in srd5e's `conditions` list, after the SRD's own, with a comment that they stand for spell
effects. Their ru and he names follow the existing pack's spell names.

Forged in the Dark (`content/systems/fitd-srd`, through its importer):
- Opt-ins on `@pools` with a short label: Ambush, Savage, Trust in me, Weaving the web (Consort), Scout.
- Bodyguard's resist bonus: opt-in on `@resistance`.
- Each scoped to its action with `when: input.action == '...'` where the text names the action.

Anointed (crew) and Forged in the fire (crew) need crew links (CREW_LINKS_DESIGN.md).

## Tests

- Parse, round-trip and check: `optIn` with label, spends and ends; `ends` outside a condition refused; dice amount in a pool roll refused.
- A named roll offers only the opt-ins whose target it reads. A ticked one adds its dice to the pool, and an unticked one does not.
- Iron will (`when: input.attribute`) still counts with no tick.
- srd5e:
  - Archery adds 2 to a longbow's attack bonus, and nothing to a longsword's or to a ranged spell's.
  - Blessed adds `1d4` to the attack expression and the save expression, and nothing to checks.
  - Guided offers a tick on a skill check, and none on a save. Rolling with it removes the condition.
- Spends: a tick with an empty pool is not offered as payable. A ticked roll spends one use.
- Golden: srd5e and fitd goldens change only for the entries written.

## Build order and who builds it

| # | Step | Who | Notes |
|---|---|---|---|
| RB1 | Engine: `optIn` model, parse, checks, roll targets, `OptIns`, ticked rolls, dice amounts, spends, ends | Sonnet session | The heart. Opus reviews the diff before RB2 and CL1 start |
| RB2 | Sheet and table UI: roll widget bonuses, the dice tray, cards, log, strings, homebrew editor | Sonnet session | After RB1. In parallel with RB3 and CL1 (different files) |
| RB3 | Data: srd5e importer and conditions, fitd abilities | Sonnet session | After RB1. Rules judgment, so not Haiku |

## Decisions (2026-11-14)

1. One model, scoped targets. `on:` is dropped.
2. Story-decided bonuses are tick boxes per roll, unticked each time. In the sheet-rules tray, a tick lasts until the next roll it fits.
3. Spell effects from another creature (Bless, Guidance) are conditions on the receiving sheet. Nothing reaches across sheets in this design.
4. Group rolls get no tick boxes yet.
