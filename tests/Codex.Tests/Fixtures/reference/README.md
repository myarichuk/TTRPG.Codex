# Reference systems

The spec the system overhaul (`docs/SYSTEM_IMPLEMENTATION_OVERHAUL.md`) is built against: the three constraint systems of
its section 2, written as data.

| Folder | Shape | What it forces |
|---|---|---|
| `d35/` | d20 System Reference Document 3.5 | Per-class progressions summed, typed bonuses that do not stack, three armor classes, skill ranks with class/cross-class costs, synergy, prestige prerequisites, slots that hold spells, hit points below zero, nonlethal damage, damage reduction, threat range and confirmation |
| `street/` | A street-level homebrew (this project's own numbers) | No levels or classes, a function from a script, a value over a track, thresholds that add up, integer division of odd numbers, explode/implode once, roll-under, armor per location that wears down, advancement by spending points |
| `crew/` | The Forged in the Dark SRD's mechanics | Dice pools, keep highest, zero dice, criticals by pattern, GM-supplied position and effect, group rolls, stress overflowing into trauma, harm in slots that push up, load chosen per job, a second sheet kind, clocks and faction status owned by the campaign |

Each folder has a `system.yaml` in the **proposed** syntax, a pack with just enough content, and a `spec.yaml` of
expectations (`street/` also has `rules.js`, its scripted rules). The words and numbers are this project's own; no
published text is copied (overhaul section 8 records the licences).

This version reads part of each `system.yaml`: attributes, entity types and condition names; since step 2
`modifierTypes`, `progressions`, `functions` (formulas), `conditionEffects` and, in each sheet kind, `values`, `modifiers`,
`lists` and `keyedLists` (`Codex.Core.Rules.SheetModel`); since step 3 `tracks`; since step 4 `rolls`
(`Codex.Core.Rules.SheetRolls`); since step 5 `events` and the parts of tracks that act when a track changes
(`Codex.Core.Rules.SheetEvents`, `SheetIntents`); since step 6 each sheet kind's `layout` (`Codex.Core.Rules.SheetLayout`).
The loader names the rest (the campaign) as keys it does not read, and `ReferenceFixtureTests` checks that it is the only one. Each step of the overhaul makes more of it read.

## How progress is tracked

- `tests/Codex.Tests/Reference/ReferenceRunner.cs` checks every expectation against a host. Today the host is
  `CurrentEngineHost`, which runs the rules engine as it stands (`SheetRuleEngine.Recompute`). Each result is **pass**,
  **fail** (tried and got something else) or **unsupported** (the host has nothing that can do it yet, naming the step).
- `progress.txt` lists the ids that pass. `ReferenceFixtureTests` fails when a listed id stops passing (a regression) and
  when an unlisted id starts passing. In the second case, add the id in the change that made it pass and update the
  overhaul doc's audit.
- The `Report` test prints pass/fail/unsupported per fixture and step, and every failure with what came back.
- An expectation that only says what must *not* happen ("no problems", "no conditions") passes on a host that checks
  nothing. So it names, in `requires`, the expectations that show the check exists, and counts only alongside them.

At step 1 nothing passed: 0 of 95. After step 2's evaluator: 29 (every step-2 expectation, and one of step 3's). After step 3: 33. After step 4: 58. After step 5: 78. After step 6: 80.

## spec.yaml

```yaml
system: ref-d35                 # the system id in ./system.yaml
sheets:                          # named sheets
  rogue3:
    kind: character              # a sheet kind of the system (optional)
    values: { dex: 16 }          # entered values
    lists:                       # picks: a list of maps, or a map by key for a keyed list
      classes: [{ id: rogue, classLevel: 3 }]
      skills: { tumble: { points: 6 } }
    tracks: { HP: 18 }           # current values; slot tracks hold a map of level -> texts
    conditions: [Dying]
campaign: { clocks: [...] }      # state owned by the campaign (optional)
expectations:
  - id: d35.some-rule            # unique, prefixed with the folder name
    step: 2                      # the overhaul step meant to make it pass
    sheet: rogue3                # optional for campaign-only expectations
    with: { values: { dex: 20 } }  # laid over the sheet: maps merge key by key, lists replace; may carry campaign:
    roll: { definition: attack, inputs: { target: 15 }, dice: [19, 9] }  # or participants: { sheet: [faces] }
    event: { type: damage, amount: 8, damageType: slashing }             # one action per expectation, or none
    requires: [d35.other-rule]   # counts only alongside these (for purely negative expectations)
    expect:
      values: { BAB: 4 }         # derived values; a dotted key reaches into a keyed list (skills.tumble)
      violations: [{ field: skills, key: Actor.Rule.RankMax }]   # exactly these, any order; [] for none
      conditions: [Seriously wounded]   # exactly these hold, with no event
      outcome: critical          # a roll's named outcome
      total: 25                  # a roll's total; totals: [26, 16] when one roll leads to another
      kept: [6]                  # dice kept
      diceUsed: 2                # faces the roll consumed from the seeded source
      carries: { position: desperate }   # inputs carried on the result
      intents: [{ adjust: { track: HP, by: -4 } }]   # exactly these, in order
      after: { tracks: { HP: 36 }, conditions: [], values: {}, campaign: {} }  # state after the intents applied
      layout: [Actions, Condition]   # the sheet kind's sections; [] for the generic layout
    why: "the arithmetic, so a wrong number is caught by reading"
```

Unknown keys in a spec are refused, so a typo cannot quietly check nothing.

## The vocabulary the fixtures use

This is the concrete form of overhaul section 3, as the three fixtures needed it. It is the starting point for the
author's reference and JSON schema (overhaul section 6), not a promise of every name.

**Expressions** stay one-liners in DynamicExpresso syntax, with lambdas for aggregates: `sum(classes, c => c.classLevel)`,
`count(where(actions, a => a.group == 'insight'), a => a.rating >= 1)`, `min(list, i => i.maxDex, default)`, `any`,
`first`, `where`. Arithmetic is the host's: `floor`, `ceil`, `div` (integer division), `mod(score)` (the attribute
modifier), `score(key)`, `table(rows, row, column)`, `levelFor(spell, className)`, `progression(name, level)`,
`modifiers(target)`, `has(condition)`. A list item's properties and fields read as `item.property`.

How they behave, as step 2 built them:
- `'text'` is a string, as is `"text"`; there are no characters. Every number is a double, so `7 / 2` is 3.5; rounding
  is `floor`, `ceil` and `div` (integer division, rounding down).
- An item without a property reads it as null. `sum`, `min` and `max` skip it (a ring with no `maxDex` sets no cap), and
  a predicate on it does not hold. `min` and `max` over a list take a third argument, the result when nothing is left.
- `count(list, predicate)` counts; there is no one-argument `count`.
- `mod(x)` is the attribute modifier of a score (`mod(dex)`) or of an attribute named by key (`mod(s.attribute)`).
- A named function (`functions:`) takes up to four arguments and returns a number.
- Values are worked out when first read, so their order does not matter; one that depends on itself is a formula
  problem (a warning naming it), never a crash. A formula that fails is a warning and leaves its value out.
- On the sheet, a modifier whose `when` reads `input.` (what only a roll has) does not hold.
- A keyed entry reads `id` (`knowledge.arcana`), `key` (`knowledge`), its parameter (`subject`: `arcana`), `label`,
  `attribute`, `group`, `groups`, its fields and its `derive` values. Its `value` adds the modifiers on `list.id`,
  `list.key` and each `'@group'`.
- The interpreter knows no .NET types: no `new`, no `typeof`, no static members, no reflection. Every parsed formula is
  also checked (`FormulaSandbox`): it may call the built-in functions and nothing else, so no `.Replace(...)`,
  `.PadLeft(...)` or `.Length` on what it holds, and read members (and entries by key) only of its own items and lists. A formula longer than
  2000 characters, or nested more than 32 brackets deep, is refused before parsing. All of it is tested in
  `SheetModelTests`.
- Inside a string, a backslash escapes the next quote: `'it\'s'`, `"say \"hi\""`.
- A named function's parameters may share a name with a built-in (`bonusSpells(score, level)` beside `score(key)`).

Tracks, as step 3 built them:
- A track's current value is the sheet's number under its key; its maximum reads as `{key}_Max`. A slot track's value is
  the list of what its slots hold (`slots_cleric_1: [bless, bless]`), or for slots in levels a map of level to texts.
- `thresholds` each add their condition while they hold, several at once; of the `bands`, the first that matches holds.
  A comparison may be a number or a formula; a line with none matches always. Conditions they bring bring their
  `conditionEffects` too; a threshold that depends on its own condition is a formula problem.
- `each` + `levels` (`"0..9"`) makes one track per item and level; its `key`, `label` and `holds.message` fill in
  `{item.<property>}` and `{level}`, and its formulas read `item` and `level`. Keys are lower-cased.
- A slot track checks that each thing held is an entry of `holds.type` passing `holds.where`
  (`Actor.Rule.SlotHolds`), the same one any number of times, and no more than the maximum (`Actor.Rule.TrackFull`).
- A numeric track's value must lie within `min` (default 0, may be negative) and its maximum (`Actor.Rule.TrackRange`).
- Slots in `levels` bring the condition of each level that has something in it.
- `overflow`, `whenFull: pushUp`, `beyond` and `start` act when a track changes (below, events).

Events and intents, as step 5 built them:
- An event is `{ type, ...fields }`. Its handler (`events.<type>`) is a list of `{ when, intents }` lines, each adding its
  intents when its `when` holds (no `when`: always), or `{ builtin: name, ...options }`, or `{ script }` (step 8; the loader
  warns, and the event does nothing until then). A handler reads the sheet and the event (`ev.amount`, `ev.inputs.position`)
  and returns intents; it changes nothing. A line with `target` acts on campaign objects (step 7).
- In an intent's fields, `by`, `to`, `level` and `slot` are formulas. `key` (a log line's translation key), `definition` and
  `who` are names. Any other text is itself when it is a bare word or words (`stress`, `cut hand`), otherwise a formula
  (`ev.helper`, `'xp_' + actions[ev.inputs.action].group`). `actor` names whose sheet; none is the sheet the event is on.
- An event whose type is a kind of intent (`adjust`, `set`, `mark`, `clear`, `condition`) is that intent, applied as it
  comes: the table's own edit, with no handler. `expect.intents` is what the handlers returned, then what the changes raised
  (an overflow's `ask`), in order.
- Applying intents to the sheet: `adjust` and `set` keep a numeric track within its bounds. A rise past the maximum of a track
  with `overflow` adds `by` (1 unset; a formula may read `excess`) to `into`, which may overflow in turn, then `reset`s to the
  minimum, `carry`s what went past (once round per filling) or `keep`s the maximum, and raises its `ask`. Overflows stop 8
  deep. `set` never overflows. `mark` and `clear` move a box track by one (or `by`); on a slot track they put `text` into a
  free slot or take it out. Slots in levels fill `level` (the first, unset); a full level puts it a level up with
  `whenFull: pushUp`, and past the last brings `beyond`'s condition onto the sheet. `condition { add | remove }` changes the
  sheet's own conditions. Thresholds and bands are not applied: they follow from the new values. What the sheet cannot do
  (another actor's intents, `roll`, `ask`, `log`, a full level that does not push up) is left for the table.
- A track the sheet has no value for starts at `start` (`max`, `min`, a number or formula); with none declared, at nothing
  (0, within its bounds). Recompute writes each numeric track's maximum (`{key}_Max`) and its declared start onto the
  sheet, and the table mirrors numeric tracks into its pools; slot tracks stay text on the sheet.
- `builtin: damage` takes `{ amount, damageType, tags }`: damage reduction first (the best `amount` among the sheet's
  `reduction.list` entries whose `bypassedByTag` property is not one of the tags, for `onlyTypes`), then resistance (the
  sheet's `resistance.property` map, by type); what is left adds to the `nonlethal` track when the event has its `tag`, and
  otherwise comes off `track`.
- Aggregates take a list reached through a member as well (`count(ev.answers, a => a)`); a list of flags counts its `true`s,
  and `contains` finds text in a list of text.
- The loader warns about a handler of the wrong shape, an unknown builtin or intent kind, a script handler, a handler for an
  intent kind (it never runs), an overflow into a track that is not there, without a maximum, or with an unknown `then`, and
  `whenFull`/`beyond` on anything but slots in levels.
- A list in an intent's fields (a log line's `args`) is text, element by element, except an element that is a dotted path
  (`ev.helper`, `item.name`), which is read. So a clock named "the vault's alarm" stays its name.

At the table, as step 6 built it (`Codex.Persistence.Runtime.TableEvents`):
- For a system with sheet kinds, damage and healing from the combat console raise `damage` and `heal` (`{ amount,
  damageType, critical, source }`) when the system handles them, instead of the table's vitals rules; the next turn raises
  `turnEnd` for whose turn ended and `turnStart` for whose begins; a rest raises `rest` (`{ name }`) after the rest's own
  rules. `RaiseEventCommand` raises any declared event, or an intent kind as the GM's edit.
- The handler runs on the sheet as the table has it: the cached sheet with the live pools over its numeric tracks, and the
  table's conditions joined to the sheet's own. What it changes goes back where the table keeps it: numeric tracks to the
  pools, slot tracks and the sheet's conditions to the stored sheet (only the keys it changed), conditions also as statuses.
- What is left: another actor's `adjust`, `set`, `mark`, `clear` or `condition` is applied to that actor (one level deep);
  `roll`, `ask` and `log` go to the session log. A handler that cannot run is said in the log and changes nothing.
- A roll from a sheet's `roll` widget while the table is live is the table's (`MakeRollCommand`): in the shared roll log
  with its outcome and mark, its intents applied as an event's. Both are undone like damage, sheet keys included.

Layout, as step 6 built it:
- A sheet kind's `layout` is a list of sections `{ section, label, icon, aside, widgets }`. A section is a tab, or with
  `aside: true` shown beside every tab. A kind without one gets the generic layout: one section with every value, track
  (generated tracks by their prefix: `slots_*`), list and keyed list.
- Widgets: `values` (`keys`: values, attributes, `{track}_Max`, `list.id`), `track` (`key`, or `prefix*` for generated
  ones; numbers get − and +, boxes are clicked, slots take text, slots in levels a level and text), `list` and `table`
  (`key`, `columns` of per-pick fields, entry properties or derived values, `groupBy`), `roll` (`roll`: the roll's inputs
  from the roller, the GM or a weapon, then the button), `text` (`key`) and `modifiers` (`target`: each modifier that
  reached it, and whether it counted). A per-pick field is edited in place; everything else is worked out.
- A system without sheet kinds may lay out its sheet at the top level (`layout:`) with the host's built-in widgets:
  `attributes`, `skills`, `fields`, `classes`, `library`, `picks`, `inventory`, `hit-points`, `armor-class`, `slots`,
  `level-up`. srd5e does; a system without a layout gets the same.
- The loader names an unknown widget, a key the kind does not have, a missing roll, a built-in widget on a sheet kind, a
  top-level layout in a system with sheet kinds, and sections without a name, without widgets or named twice.
- `modifierTypes` has a fourth rule, `best`: exactly one modifier of the type counts, the greatest. 5e's armor class base is
  one (`base: best`): worn armor, else the best of Unarmored Defense, each class's defense and 10 + Dexterity.

Rolls, as step 4 built them:
- Inputs: each declared input is what the request supplied, else for `from: parent` the parent roll's, for `from: weapon`
  the `property` of the item the `weapon` input names (a pick on the sheet, else the library's entry), else its `default`.
  One with none of these fails the roll naming it, as does a value not among its `options`. Inputs the definition does not
  declare pass through. The result records each input's value and who supplied it (`roller`, `gm`, `weapon`, `parent`,
  `default`, or whatever the caller names).
- Formulas read the sheet, every input by its key (`target`) and as `input.key`; then `natural` (the first die as rolled),
  `kept` (the kept dice added up), `dice` (every face, explosions included) and `zeroDice`; then `total` for the outcomes;
  then `outcome` and, for a group roll, `results` (each participant's `actor`, `outcome`, `total`). Modifiers whose `when`
  reads `input.` hold or not by the roll's inputs. A formula that fails fails the roll, naming the formula.
- `dice` is one group of dice (`1d20`, `4d6kh3`) with an optional number. A `pool` of fewer than one die rolls `zeroDice`
  instead, or fails without it; more than 100 fails. `keep` keeps the highest or lowest N by value.
- `explode` and `implode` act per die: a die showing `on` adds (or takes away) another die, which explodes (or implodes)
  again while it shows `on`, up to `times` (100 at most). An imploding die's dice never explode, nor the reverse. A die's
  value is its face plus what it exploded into, less what it imploded by.
- Outcomes: the first whose `when` holds; one without `when` always does. `mark: critical` or `fumble` is how the dice
  display marks it; unset, an outcome named `critical` or `fumble` is marked so. The display marks from this, never from a
  face.
- `then`: each line whose `when` holds (reading `outcome`) makes its `roll` (which sees the parent's inputs) and adds its
  `intents`. A follow-up whose outcome has `replacesParent` becomes the parent's outcome. Rolls leading to rolls stop at 8
  deep. `totals` lists the roll's total, then each follow-up's.
- A group roll has `participants: { roll }` instead of dice: each participant makes that roll with the group's inputs and
  their own dice, and `result: best` (or `worst`) is the participant outcome earliest (or latest) in that roll's outcomes.
- Intents are `{ kind: { fields } }`. `by` and `to` are worked out when the roll is made (`6 - kept`); `actor` naming an
  input is whoever that input names (`leader`). The roll returns them, and `SheetIntents.Apply` applies them as it applies an
  event's (above).
- `score(key)` reads a value of the sheet kind when no attribute has that key (Blades' `insight`), and formulas may read an
  entry by a key worked out in the formula: `skills[input.skill].value`. Die faces compare as numbers: `d => d == 6`.
- The loader warns about a roll with not exactly one of `dice`, `pool` or `participants`, a pool without a die, a roll
  defined twice, an input from somewhere other than `roller`, `gm`, `weapon` or `parent`, and a `then` or `participants`
  naming a roll that is not defined.

| Section | Holds |
|---|---|
| `functions` | Named functions: `{ formula: "(a, b) => ..." }` (built in) or `{ script: file#export, reads: [...] }` (Jint). Callable from any expression |
| `progressions` | Named per-level formulas (`bab.good: "level"`) that entries point at by name |
| `modifierTypes` | `default` stacking, `penalties` stacking, and `types: { dodge: sum }`. Stacking is `sum`, `highest`, `lowest` or `best` |
| `conditionEffects` | By condition name: `modifiers` that apply while it holds. `conditions` stays the list of names |
| `sheetKinds` | `kind`, `label`, `role` (the host role: `player`, `npc`, `creature`, `shared`), and the parts below |
| `campaign` | Campaign objects: `clocks` (allowed `segments`, `visibility`) and `tables` (`key`, `itemFields`) |
| `rolls` | Roll definitions (below) |
| `events` | By event name: handlers (below) |

Inside a sheet kind:

| Part | Holds |
|---|---|
| `values` | `{ key, formula }`, with `modifiers: target or [targets]` to add stacked modifiers, `without: [types]` (or `{ type, positiveOnly }`) to leave some out, and `input: true` with `options` and `default` for values the player picks |
| `modifiers` | `{ target, amount, type, source, when }`. A target may be a group: `'@armorPenalty'` reaches every key listing that group |
| `lists` | Picks of a type: `itemFields` (per-pick fields such as `classLevel`, `equipped`, `used`), `modifiersFrom: { property, when }`, `prerequisites: property` (checked on the character without the pick's own levels), `rules` |
| `keyedLists` | A fixed set of `keys` (with `attribute`, `group`/`groups`, `parameter` for one entry per subject), `itemFields`, `derive` (per-key values as lambdas: `ranks`, `maxRanks`, `value`) and `rules` |
| `tracks` | `{ key, max, min, start, display: number, boxes or slots }`, plus `thresholds` (each adds its condition while it holds: `atOrBelow`, `below`, `atOrAbove`, `equals`), `bands` (one at a time, the first that matches), `overflow: { into, by, then: reset, carry or keep, ask }`, and for slots `holds`, `levels`, `whenFull: pushUp`, `beyond`. `each` + `levels` generates tracks per list item and level (spell slots per class) |
| `layout` | Sections of widgets: `values`, `track`, `list`, `table`, `roll`, `text`, `modifiers` (above, "Layout") |
| `rules` | `{ check, key, message }`: a problem with that translation key when the check is false |

A roll definition has `dice` (`1d20`) or `pool` + `die` with `zeroDice` and `keep`; `add` (expressions);
`explode`/`implode` (`{ on, times }`); `inputs` (`{ key, from: roller, gm, weapon or parent, options, default }`);
`outcomes` (named, the first whose `when` holds, reading `natural`, `total`, `kept`, `dice`, `zeroDice`); `carries`;
`then` (follow-up rolls or intents); and for group rolls `participants` and `result: best`.

An event handler is `{ builtin: name, ...options }`, `{ script: file#export }`, or a list of `{ when, intents }`. Intents
are those of overhaul section 3: `adjust`, `set`, `mark`, `clear`, `condition`, `roll`, `ask` and `log`, each optionally
naming an `actor`.
