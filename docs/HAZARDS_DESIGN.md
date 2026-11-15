# Hazards across systems: design

Written 2026-11-13 on branch `claude/exciting-keller-1af6u9`. Nothing here is built. It plans traps, environmental hazards and haunts
as an engine feature any system can switch on, a few homebrew examples per system, and the world builder making them. It came out of
the Pathfinder Second Edition survey ([ORC_PF2E_IMPORT_SURVEY.md](ORC_PF2E_IMPORT_SURVEY.md)), but it is not specific to that system.

Ground rules, as for every engine feature:
- No game's names or numbers in C#. A system declares in `system.yaml` which parts of a hazard it uses and which fields hold them.
- Data first. A hazard is a Library entry like a spell or a creature, so homebrew, packs, translation and the book export get it for free.
- Each system's homebrew examples are written by this project, never copied from a book we do not license.

## Decision in brief

- **A hazard is an entity type with a `hazard:` block**, the way a creature type is an entity type with `actorBlueprint: true`. The block
  maps the parts every hazard can have (notice, disable, trigger, effect, durability, reset, routine) onto the type's own fields, and
  sets the system's rules for them (which checks notice and disable, whether a rank is needed).
- **Placed on a place.** `PlaceFeature` gains a `Hazard` reference beside today's free-text `Trap`. The free text stays for notes.
- **At the table** the GM sees the hazards of the encounter's place, and gets three actions on each: **Notice** (check the party against
  the notice DC), **Disable** (a roll against the disable DC by the actor who tries) and **Trigger** (roll the effect against targets
  the GM picks, through the existing damage path).
- **Complex hazards** (they act every round) join initiative as an actor with the `hazard` tag (a new per-entry registration, 3.3), and
  never appear in the Bestiary or among creatures in Quick Add.
- **A new server-side check roll** (4.2): the app has none today; every table roll happens in the browser.
- **The world builder** proposes hazards as homebrew (`kind: homebrew, type: hazard`) and links them from a place's feature. It scales
  them with a system-declared table (`hazards.scaling`), so it does not guess DCs.
- **Forged in the Dark declares no hazards.** Its dangers are clocks and consequences, which the campaign already has.

## 1. What exists today

| Place | What it does | Gap |
|---|---|---|
| `PlaceFeature` (`Codex.Persistence/SpatialModels.cs`): `Container`, `Lock`, `Trap`, ... | Free-text notes on a place's feature, GM only | `Trap` is prose; nothing at the table can roll it |
| `ProposalFeature` / `DraftFeature` (world builder) | The model writes `"trap": "..."` and `"lock": "DC 15"` on a feature | Same: text only |
| `ContentTypeDefinition.ActorBlueprint` | A creature type: registered as an actor, `hitPointsProperty`, `attributeProperties`; drives the Bestiary, the homebrew wizard's creature mode, `BookStatBlocks`, the world builder's creature checks | A hazard would need HP and a turn, but must not be treated as a creature everywhere the flag is read |
| `EncounterDocument` | `LocationId`, `Participants` (actors), and `ActiveEffects` "not owned by a single actor (area effects, hazards)" | The comment anticipates hazards; nothing fills it |
| Turn events (`RuntimeCommands`, `TableEvents`) | `turnStart` and `turnEnd` are raised; effects expire at a turn boundary | Enough for a complex hazard's routine |
| Creature actions (srd5e monsters) | `name` + `description` text; the GM rolls with the console's dice | Hazard effects can follow the same text-first shape, with a few numbers the console can roll |
| `skills.passives` (srd5e passive Perception) | A derived score on each sheet | The Notice action can compare it with the DC directly |

Systems today: srd5e has no trap or hazard type (only the *find traps* spell and gear like a hunting trap). fitd-srd has none. The BFRD
import plan lists hazards among the GM rules it leaves as text. A Pathfinder Second Edition pack will need them fully, but they are in
GM Core, which is not held yet.

## 2. The parts of a hazard

| Part | Meaning | srd5e | Pathfinder 2e (later) | BFRD (later) |
|---|---|---|---|---|
| **Notice** | Routes: for each, the check and DC or what it needs instead, optionally a minimum rank | Perception (passive or active) or Investigation vs DC | Perception vs Stealth DC, minimum proficiency rank | as srd5e (check when imported) |
| **Disable** | Routes: for each, the check and DC or what it needs instead, minimum rank, successes needed, which outcomes set it off | Often no check (cut the wire, wedge an Iron Spike); sometimes one check (Sleight of Hand) vs DC | Skill, rank, often 2 successes, crit failure triggers it | as srd5e |
| **Trigger** | What sets it off | text | text | text |
| **Effect** | An attack or a save, damage dice and type, conditions | attack bonus or save attribute + DC; damage | same, with degrees of success | same |
| **Durability** | AC, Hardness, HP, broken threshold, immunities | optional AC and HP | AC, Hardness, HP, BT | optional |
| **Reset** | How and when it re-arms | text | text | text |
| **Routine** | What a complex hazard does each round | rare; allowed | complex hazards: initiative, a routine of actions | |
| **Level** | What it is scaled by | CR or a "for levels 5-10" band | hazard level | level band |

Every part is optional. A system lists the parts it uses; the editor and the table show only those.

## 3. YAML

### 3.1 The type

```yaml
entityTypes:
  - type: hazard
    label: Hazard
    pluralLabel: Hazards
    folder: hazards
    playerFacing: false
    summaryFields: [level, kind]
    filterFields: [level, kind]
    fields:
      - { key: kind,          label: Kind,            type: Text, category: Profile }        # trap, environmental, haunt
      - { key: level,         label: Level,           type: Number, category: Profile }
      - { key: detect,        label: Detect,          type: Collection, category: Notice }   # route rows, 3.1.1
      - { key: disarm,        label: Disarm,          type: Collection, category: Disable }  # route rows
      - { key: trigger,       label: Trigger,         type: LongText, category: Trigger }
      - { key: duration,      label: Duration,        type: Text,   category: Trigger }
      - { key: attackBonus,   label: Attack bonus,    type: Number, category: Effect }
      - { key: saveAttribute, label: Save,            type: Text,   category: Effect }        # empty: no save
      - { key: saveDc,        label: Save DC,         type: Number, category: Effect }
      - { key: onSave,        label: On a save,       type: Dropdown, category: Effect, options: [half, none] }
      - { key: damage,        label: Damage,          type: Collection, category: Effect }    # rows { dice, type, count }
      - { key: conditions,    label: Conditions,      type: Collection, category: Effect }    # rows { name, lasts }
      - { key: area,          label: Area,            type: Text,   category: Effect }
      - { key: effect,        label: Effect,          type: LongText, category: Effect }
      - { key: reset,         label: Reset,           type: LongText, category: Reset }       # empty: inert after it goes off
    hazard:
      notice:  { routes: detect, passive: true }
      disable: { routes: disarm }
      trigger: trigger
      duration: duration
      effect:  { attack: attackBonus, save: saveAttribute, saveDc: saveDc, onSave: onSave, damage: damage, conditions: conditions, area: area, text: effect }
      reset:   reset
      level:   level
```

`HazardDefinition` (new, on `ContentTypeDefinition`, `Hazard` property). Every member names a field of the type, so the engine never
reads a hard-coded property:

| Member | Shape | Notes |
|---|---|---|
| `notice` | `{ routes, passive }` | `routes` is a Collection field of route rows (3.1.1); `passive: true` lets Notice compare the sheet's passive score for a route's check (the passive whose `skill` is the check), unless the route says `passive: false` |
| `disable` | `{ routes }` | A Collection field of route rows, the same shape. Rank, successes and what a critical failure does are row keys (`rank`, `successes`, `triggersOn`), not fields |
| `trigger` | a field | text |
| `duration` | a field | text: "Instantaneous", "Until the stone stops rolling" |
| `initiative` | a Number field | A complex hazard's Initiative bonus, read by H5's actor registration |
| `effect` | `{ attack, save, saveDc, onSave, damage, conditions, area, text }` | One effect with lists inside it: an optional save (an empty `save` on an entry means none), `onSave` (`half` or `none`: what a passed save does to the damage; empty means `none`; only with a save, and a save has its DC), `damage` (Collection of `{ dice, type, count }`), `conditions` (Collection of `{ name, lasts }`, applied on a failed save, or always without one), `area` (text); the rest is `text` |
| `durability` | `{ armorClass, hardness, hitPoints, brokenThreshold, damageThreshold, immunities }` | Any subset. `damageThreshold` is a Number (a hit below it does nothing; not Hardness, not the broken threshold); `immunities` a TagList |
| `reset` | a field | text |
| `routine` | a field (`Collection` of `{ name, description }`) | Present only on systems with complex hazards |
| `complex` | a field (`Boolean`) | When true on an entry, the entry is registered as a hazard actor (3.3) |
| `level` | a field | What scaling and the world builder read: a number, or a level range (`1-4`) where the system prints ranges |
| `severity` | a field | A system's own words for how dangerous it is (srd5e: Nuisance, Deadly) |
| `higherLevels` | a field (`Collection` of `{ levels, ...changed fields }`) | The entry's own scaled versions (srd5e's "At Higher Levels"). Placing a hazard picks the row for the party's level, and the table uses that row's numbers. Fields a row does not name keep the base value |
| `methods` | a field (`Collection` of route rows, `name` required) | Extra ways to deal with it beyond detect and disarm, each a button at the table: srd5e's Avoid, Escape, Destroy, Slow, Set |

The loader checks that every named field exists on the type and has the right kind, that an `onSave` Dropdown offers only values the engine
knows (`half`, `none`), and that a type does not have both `actorBlueprint` and `hazard`. Row contents are checked per entry (3.1.1).

#### 3.1.1 Route rows

`detect`, `disarm` and `methods` rows have fixed keys, engine words like the `routine` and `higherLevels` row keys:

| key | meaning |
|---|---|
| `id` | the name other rows refer to by `after`; unique in its list |
| `name` | what the button says |
| `action` | the method word, free text: `Search`, `Study`, `Action`, `10 minutes` |
| `check` | a skill or attribute by key or label; empty means no check is rolled |
| `dc` | whole number; only with `check` |
| `needs` | what it takes besides a check, or instead of one ("An Iron Spike or a similar object"). A row with no `check` needs `needs` or `text` |
| `after` | the `id` of a detect route that must have succeeded first |
| `triggersOn` | outcome keys (`failure`, later `criticalFailure`) that set the hazard off |
| `rank`, `successes`, `passive` | minimum rank (ranked systems only), successes needed (default 1), `false` to stop passive scores for this route |
| `text` | the rest, in the project's words |

`damage` rows are `{ dice, type, count }` (`count` is dice or a whole number, default 1: Poisoned Darts is `1d6 poison` hitting `1d3` times).
Numbers a higher-levels band changes are top-level fields (`saveDc`, `area`, `depth`, `severity`, `damage`); a band restates a whole list (the pit
restates both damage parts). A DC that rises with a band inside a list (the Falling Net's Escape) is restated with that band's `methods`.

### 3.2 Scaling, for the world builder and the homebrew wizard

```yaml
hazards:
  scaling:                      # by the hazard's level field; the row with the highest level <= the hazard's applies
    - { level: 1,  attackBonus: <tbd>, saveDc: <tbd>, damage: <tbd> }
    - { level: 5,  ... }
    - { level: 11, ... }
    - { level: 17, ... }
```

- **The numbers are not designed yet.** Where they come from depends on the system:
  - **srd5e:** SRD 5.2.1 has no shared trap table, but each of its 8 traps scales itself by level band ("At Higher Levels": rows for
    levels 5-10, 11-16 and 17-20 over the base 1-4), and only Collapsing Roof and Fire-Casting Statue share one damage progression (2d10, 4d10, 10d10, 18d10); Poisoned Needle uses the
    same dice one band later (2d10 at 5-10), with its own save DCs (11, 13, 15, 17). Save
    DCs rise by band at different rates per trap; detect and disarm DCs are fixed per trap; there are no attack bonuses. So srd5e's
    `hazards.scaling` is **derived from the SRD's own trap progressions** (CC BY 4.0, with the SRD attribution), with damage per
    band and severity (Nuisance, Deadly) as columns, and no attack-bonus column. The derivation is written down beside the table.
  - **Not from any book we do not license.** The 5e Dungeon Master's Guide's generic severity table (setback, dangerous, deadly)
    and its generic save DC and attack bonus bands are not in the SRD and must not be copied. The damage progression above is
    usable only because the SRD's own traps print it.
  - **Pathfinder Second Edition:** the DC-by-level table, once GM Core is held and its notice read.
- The table is guidance: the world builder's check (`propose_changes`) warns when a hazard's number is more than one row away from its
  level, and the homebrew wizard fills a new hazard's numbers from it. Nothing is ever refused.
- `SystemTranslation` needs nothing new: hazard field labels translate like any type's fields.

### 3.3 Complex hazards in initiative

An entry whose `complex` field is true is also registered as an actor, with the tag `hazard` and `Metadata["entryType"] = "hazard"`.
It gets an initiative roll and a turn like a creature. On its turn the table shows its routine. Its HP and AC (when it has them)
take damage through the same damage event as any actor.

This is **new code**, not a reuse of the flag:
- Registration is per type today (`YamlContentPackLoader` registers an actor only when `type.ActorBlueprint`). The loader gains a
  per-entry gate: a `hazard` type registers the entries whose `complex` field is true.
- `ToActorBlueprint` reads `type.HitPointsProperty`. It gains an overload that takes the hit point field from `hazard.durability`.

Two kinds of reader need care:
- **Readers of the `ActorBlueprint` flag** (the Bestiary, the homebrew wizard's creature mode, `BookStatBlocks`, the world builder's
  creature checks, `PromoteToCastDialog`, `CastService`) keep reading only that flag, so hazards never reach them.
- **Readers of the actor registry.** The only one is `QuickAddPanel` (`ContentRegistry.GetActorsBySystem`), which puts "a creature at
  the table". It splits the list by the `hazard` tag: creatures as now, complex hazards in their own "Hazards" group. Any new reader of
  `GetActorsBySystem` must do the same; a test pins it (8).

A simple hazard (not complex) never becomes an actor. It lives on its place and acts only through the three table actions.

## 4. At the table

### 4.1 Where hazards show

The **Hazards** panel of the GM's table lists the hazards on the encounter's place (`EncounterDocument.LocationId`) and its child
places, with their state. Outside an encounter, the same list shows for the place the GM has open. Players never see a hazard until
it is noticed or triggered.

The state of a placed hazard belongs to the placement, not to the Library entry, so one Library hazard can be placed many times:

```
PlaceFeature.Hazard      : { entry: "hazard/spiked-pit", state: hidden | noticed | disabled | triggered, successes: 0, noticedBy: [actorIds] }
```

**Where live state lives, and undo.** Table undo today is per actor: `IUndoableCommand` names one `ActorId`, and
`UndoLastChangeCommand` takes back the last change to that creature. It does not cover place documents. So:
- **During an encounter**, the hazard's live state is kept in the runtime, in `EncounterDocument.ActiveEffects` (whose comment already
  anticipates hazards), keyed by place feature. It is written back to `PlaceFeature.Hazard` when the encounter ends.
- **Outside an encounter**, Notice, Disable and Trigger change `PlaceFeature.Hazard` directly, as an ordinary place edit.
- **What can be undone:** the damage a Trigger deals is one per-target damage command, so the existing per-actor undo takes it back. A
  change of the hazard's own state (noticed, one disable success, disabled) is not an undoable command in the first version. The panel
  has a state picker the GM uses to set it back by hand, and the log records every change. A hazard-scoped undo is later.

### 4.2 The three actions

| Action | What it does |
|---|---|
| **Notice** | For each PC in the scene: with `notice.passive`, compares the passive score with the DC; otherwise rolls the sheet's check (the same skill roll the sheet's skill button makes). The same goes for each detect route that has a check, with its own check and DC; a route with no check (Detect Magic) is never done by the sweep. With a route's `rank`, a PC below the rank cannot try it. The result goes to the GM only: "Notices: Ana (passive 15). Does not: Bo (12)". The GM then marks it noticed for those PCs, which reveals it to them |
| **Disable** | The GM picks the actor who tries. The GM also picks the route (3.1.1); a route with no `check` needs no roll and is marked done. The engine rolls the actor's route check against the route's DC. A success adds one to `successes`; reaching the route's `successes` (default 1) disables it. An outcome whose key is in the route's `triggersOn` sets it off (`failure` for the Poisoned Needle, `criticalFailure` on a system with degrees) |
| **Trigger** | The GM picks the targets. With `effect.attack`, it rolls the attack against each target's armor class; with `effect.save`, it asks each target's saving throw against `saveDc`. Damage goes through the existing damage event, so resistances and the system's `events: damage` handler apply. Conditions in `effect.text` stay text for the GM to add |

Each action is a logged table command (`HazardCommand(placeId, action, actorIds, ...)`); see 4.1 for what can be undone.

**The numbers exist; the server-side roll does not.** Each sheet's evaluation already exposes what the actions need:
`SheetEvaluation.Skills`, `SavingThrows`, `Passives` and `ArmorClass`. But every roll at the table today happens in the browser: the
Actions panel and the console roll a die and add a bonus (`ActionsPanel` raises `OnRoll` with the bonus). There is no runtime command
that rolls "this actor's Perception" or "this actor's Dexterity save", and no request-a-check from the GM. So the hazard command
needs a **new roll step**:
- `HazardCommand` reads each actor's evaluation, takes the bonus for the named check, save or AC, and rolls the system's check die
  (`dice.check`) on the server, so the result is in the log and every screen at once.
- Creatures are different. A printed creature sheet (srd5e monsters) has saves and skills as text (`savingThrows: "Dex +2"`), not
  evaluated numbers. For a creature target, the panel asks the GM for the bonus, prefilled when a parse of that text finds one.
- This roll step is the first server-side "an actor makes a check" in the app. Write it as a reusable service, so a later
  "GM asks the party for a check" feature uses it too.

### 4.3 Systems with degrees of success

Pathfinder Second Edition's disable and save outcomes have four degrees. Those come from the system's `rolls` (the degree roll the
Pathfinder survey plans to prove first). The hazard command asks the system's roll for its outcome and maps `criticalFailure` to "it
triggers" through a route's `triggersOn` list (outcome keys). Until that roll exists, a system without degrees gets success or failure only.

## 5. Places and the world builder

- `PlaceFeature` gains `Hazard` (4.1). `Trap` stays as free text, for anything the GM wants to note beside it.
- `ProposalFeature` gains `"hazard": "existing:homebrew:hazard/<id>" | "<ref of a homebrew entry in this proposal>" | "hazard/<library id>"`.
  `WorldBundleParser` resolves it like other links.
- The world builder can propose a hazard as homebrew (`{ "kind": "homebrew", "type": "hazard", "name": ..., "properties": {...} }`), as
  it already does for monsters and spells. `homebrew_fields(type: hazard)` tells it the fields. Its prompt gains one line on hazards:
  pick a level from the party's, take the numbers from `hazards.scaling`, write the trigger and reset as short rules text.
- `CheckAsync` warns when a homebrew hazard's numbers are more than one scaling row from its level (3.2), and when a feature names a
  hazard that does not resolve.
- On a system with no hazard type, the world builder keeps writing `"trap": "..."` text, as today.

## 6. Content

| System | Pack | What |
|---|---|---|
| srd5e | `srd-5.2.1/hazards` | **The SRD's 8 traps** (Gameplay Toolbox, "Traps", pp. 199-201): Collapsing Roof, Falling Net, Fire-Casting Statue, Hidden Pit, Poisoned Darts, Poisoned Needle, Rolling Stone, Spiked Pit. Each has severity and level range, trigger, duration, the effect with its save and damage, Detect and Disarm (sometimes several routes: Fire-Casting Statue has four detect routes, Detect Magic, the glyph, reading the glyph and the plate, and two disarm routes, defacing the glyph and wedging the plate; Poisoned Darts has two of each), the extra methods it needs (Avoid, Escape, Destroy, Slow, Set) and "At Higher Levels" (all but Rolling Stone). Imported by `Srd5eImporter` with the SRD attribution. The importer's source is the 5e-bits JSON, which has no traps, so they come from a reviewed data file, `Data/srd521-traps.yaml`, written from the SRD 5.2.1 document, with a small loader, the way #302 added the invocations (`Data/srd521-invocations.yaml`, `Invocations.cs`). No PDF reading in the importer |
| srd5e | `codex-flavor` (the project's own pack) or a new `codex-hazards` pack | Only what the SRD lacks: a complex hazard (a flooding room) and a magical one (a cursed doorway), written by this project with numbers from 3.2. The SRD's traps cover the simple mechanical ones |
| srd5e | rules, not hazards | The SRD's environmental rules (Burning, Dehydration, Falling, Malnutrition, Suffocation; and Deep Water, Extreme Cold, Extreme Heat, Frigid Water, Heavy Precipitation, High Altitude, Slippery Ice, Strong Wind, Thin Ice) stay `rule` entries. They apply to a whole place, not a placed object. A later step may let a place name one as its environment |
| fitd-srd | none | Dangers are clocks and consequences. No `hazard` type |
| Pathfinder Second Edition pack | its own ORC pack | 2-3 homebrew hazards tagged homebrew until GM Core is held; then GM Core's hazards replace them. Homebrew built on ORC rules ships under the ORC with the rest of that pack |
| BFRD | its own pack | Check the BFRD PDF's hazards when that importer is written; it is CC BY 4.0 |

Homebrew examples carry the `homebrew` tag and an id prefix (`homebrew-`), so a later licensed import of a hazard with the same name
does not collide with them.

## 7. UI

| Where | What |
|---|---|
| Library | Hazards browse like any type: filters by kind and level; the entry page groups fields by category (Notice, Disable, Trigger, Effect, Reset) |
| Homebrew wizard | A hazard mode: pick level and kind, the numbers come from `hazards.scaling` and stay editable |
| Place page (GM) | A feature shows its hazard as a link plus its state, and a "Place a hazard" picker |
| GM table | The Hazards panel (4.1) with Notice, Disable and Trigger per hazard |
| Encounter builder | A "Hazards" group beside creatures, listing complex hazards only |
| Book export | Hazards render as a stat block like creatures, in the GM book only |

**Accessibility.**
- The panel is a list of hazards, each a group with a heading (its name and state) and three real buttons whose text names the action
  and the hazard ("Notice: Spiked pit").
- Results go to a polite status region and to the log ("Spiked pit: Ana notices it (passive 15 vs DC 15)"), never only to colour.
- State is text ("Hidden", "Noticed by Ana", "Disabled", "Triggered"), with an icon beside it, not instead of it.
- Target and actor pickers are fieldsets of checkboxes or radios with a legend; everything works by keyboard. Check RTL in Hebrew.

**Strings (en, ru, he)**, in the existing `Strings.Combat` and `Strings.Event` files: `Hazard.Panel`, `Hazard.Notice`, `Hazard.Disable`,
`Hazard.Trigger`, `Hazard.State.Hidden` / `Noticed` / `Disabled` / `Triggered`, `Hazard.Noticed` ("{0} notices {1} ({2} vs DC {3})"),
`Hazard.Missed`, `Hazard.DisableProgress` ("{0}: {1} of {2} successes"), `Hazard.Disabled`, `Hazard.Triggered`, `Hazard.RankTooLow`.
No new resx prefix.

## 8. Tests

| Test | File |
|---|---|
| The loader reads `hazard:`; refuses an unknown field, `rank` on a system without ranked skills, and `actorBlueprint` + `hazard` together | `SystemDefinitionLoaderTests` |
| A complex hazard registers as an actor tagged `hazard`; a simple one does not; neither shows in Bestiary candidates | new `HazardTypeTests` |
| Notice: passive compare; active roll; a PC below the minimum rank cannot notice | new `HazardCommandTests` |
| Disable: one success disables; with `successes: 2`, two do; an outcome in a route's `triggersOn` triggers it | same |
| Trigger: attack vs AC per target; save vs DC per target; damage reaches the damage event (resistance applies); undoing a target's damage restores its HP | same |
| The check service rolls a PC's skill, save and passive from its evaluation; a printed creature uses the GM's bonus | new `ActorCheckTests` |
| Live state in an encounter is written back to the place feature when the encounter ends | `HazardCommandTests` |
| `QuickAddPanel` lists complex hazards only in its Hazards group, never among creatures | bUnit |
| `PlaceFeature.Hazard` round-trips; one Library hazard placed twice keeps two states | persistence tests |
| World builder: a proposal with a homebrew hazard linked from a feature saves both; a number far from the scaling row warns; an unresolved hazard link is an error | `WorldBundleParserTests`, `WorldBuilderServiceTests` |
| srd5e golden corpus unchanged | Golden |
| A reference fixture gains one hazard expectation per part (notice, disable, trigger) | `tests/Codex.Tests/Fixtures/reference` |

## 9. Build order

| # | Step | Size |
|---|---|---|
| 1 | `HazardDefinition` on `ContentTypeDefinition`, `hazards.scaling` on `SystemDefinition`, loader checks, XML docs, schema docs regenerated | S |
| 2 | srd5e: the `hazard` type and scaling derived from the SRD's traps; the 8 SRD traps from a reviewed `Data/srd521-traps.yaml` and a loader, as the invocations are; 2 homebrew examples for what the SRD lacks | S-M |
| 3 | `PlaceFeature.Hazard` with state; place page link and picker | S-M |
| 4a | Server-side check service: an actor's skill, save, passive or AC from its evaluation, rolled with `dice.check`; GM-entered bonus for printed creatures | M |
| 4b | `HazardCommand` (notice, disable, trigger) on 4a and the damage event; live state in `EncounterDocument.ActiveEffects`, written back at encounter end; the Hazards panel with its state picker; log | M |
| 5 | Complex hazards as actors: per-entry registration gate, `ToActorBlueprint` overload, `QuickAddPanel` split by the `hazard` tag; routine shown on its turn | M |
| 6 | World builder: proposal field, homebrew hazards, scaling check, prompt line | S-M |
| 7 | Homebrew wizard hazard mode; book export stat block | S |
| 8 | Strings en/ru/he (Haiku), accessibility pass | S |

Steps 1-4b give a working feature on srd5e. Step 4a is the largest single piece: it is new, not a reuse. The Pathfinder Second Edition pack adopts it when its own system exists, with the route-row keys `rank`, `successes` and
`triggersOn: [criticalFailure]` (in place of `notice.rank`, `disable.successes` and `critFail`) and degrees (4.3).

## 10. Open questions

1. ~~Does SRD 5.2.1 have a traps section?~~ **Answered: yes**, 8 traps with per-trap level scaling (see 3.2 and 6). Checked on
   2026-11-13 against the official PDF (`media.dndbeyond.com/compendium-images/srd/5.2/SRD_CC_v5.2.1.pdf`), text only; table
   layout not inspected. SRD 5.1 is believed to have had none (not checked).
2. **Who sees a noticed hazard?** Recommended: only the PCs who noticed it, until the GM reveals it to all.
3. **Hazard state across sessions.** A disabled trap stays disabled; a reset rule ("re-arms after 1 minute") is text the GM applies.
   Automatic reset is later.
4. **Fitd obstacles.** Should Forged in the Dark get a light "danger" type (a clock plus a harm level) later, or stay with the campaign
   clocks it has?

## 11. Built

Steps 1 to 3 (H1 to H3) are built on `feat/hazards`. What differs from the sections above:

- **H1.** `HazardDefinition` has the members of 3.1, plus the groups as classes (`HazardNotice`, `HazardDisable`, `HazardEffect`,
  `HazardDurability`). `hazards.scaling` is `HazardRules.Scaling`, a list of rows read as `Dictionary<string, string>` (a number or dice
  string each), so the schema generator and the loader need no new type. The loader also checks field kinds, which 3.1 did not list: a
  DC, attack, successes or durability part must name a Number field; `complex` a Boolean; `routine`, `higherLevels` and `methods` a
  Collection; the text parts a Text, LongText or Dropdown (`level` may be Number or Text). A scaling row needs a whole-number `level`, no
  repeated level, and keys that are fields of some hazard type; `hazards.scaling` without a hazard type is an error. "Ranked skills" means
  `sheetRules.skills.ranks` is non-empty. `Localize` copies the `hazard` block.
- **H2.** srd5e's `hazard` type has `level` as **Text** (`1-4`, `5-10`), since the SRD prints ranges; scaling rows use a number, so
  3.2's "lowest level of the range" is the reading for a later check. The type declares `complex` and `routine` (for the flooding room)
  but nothing reads them until H5. `hazards.scaling` has no `severity` column (3.2 suggested one): its four rows are Collapsing Roof's
  progression (save DC 13, 15, 17, 19; damage 2d10, 4d10, 10d10, 18d10), a Deadly trap with a save. Other Deadly traps differ: Fire-Casting Statue
  keeps DC 15 and scales its cone, Poisoned Darts keeps DC 13 and scales per dart, and the pits have no save and scale depth with d6 and d8 dice. Detect and disarm DCs are not columns because the
  SRD fixes them per trap. The traps carry **no tags**: `kind: trap` is a property, because the shipped-tag test wants a Hebrew and a
  Russian label for every tag. The two homebrew hazards are a new pack, `codex-hazards` (kind world, depends on the SRD pack), not
  `codex-flavor`, which is a text overlay only. Their ids are `homebrew-flooding-room` and `homebrew-cursed-doorway`. The hazard type
  and its field labels are translated in `system.ru.yaml` and `system.he.yaml`; the field descriptions and categories stay English
  there. The trap data was first drafted from memory and was wrong almost everywhere (see "Hazard shape" below). The pack files in `srd-5.2.1/hazards/` were written by
  running `Traps.Entries()` through `ContentPackExporter`, not by the full importer (which needs the network).
- **H3.** `PlaceFeature.Hazard` is a `PlacedHazard { Entry: "hazard/<id>", State, Successes, NoticedBy }` with a `HazardState` enum
  (Hidden, Noticed, Disabled, Triggered). `Entry` is `type/id`, so a hazard type with another key also works. A dangling entry is
  kept and shown with its id and a "no longer in the Library" option, not dropped. The Places edit form shows the picker only when the
  system declares a type with a `hazard` block; the notes line shows a pill with the state and a link while the entry is in the Library.
  The state picker on the form is the manual state control 4.1 asks for; the table's Hazards panel comes with H4b. Strings are in
  `Strings.Campaign` (`Campaign.Places.Feature.Hazard*` and `.State.*`), not `Strings.Combat` or `Strings.Event`, since they belong to the
  Places form. The Russian and Hebrew strings need native review. The world builder's `hazard` proposal field (step 6) is not started.
- **After review (H1 to H3).**
  - A placement carries `IsHomebrew` (as the Bestiary does), so a homebrew entry with a pack entry's id is a different choice. The picker's value is
    `homebrew:type/id` for the campaign's own entry. H4b looks the entry up with `CampaignLibrary.GetAsync(..., isHomebrew)`.
  - A placement carries `Levels`: the `levels` of the entry's own scaled row, picked when it is placed (null: the base numbers). Section 3.1 stands:
    the table reads numbers only through `HazardNumbers.For(entry, type, placement.Levels)` (`Codex.Core/Systems/HazardNumbers.cs`), so a pit placed for
    level 3 does not turn into 4d10 when the party reaches level 5.
  - The place form writes a hazard back only when the game master changed it (`PlacedHazardForm`); an untouched one keeps what is stored at save time.
    Choosing another hazard resets its state, counts and version. **For H4b:** the table's writes must patch only `Feature.Hazard` on the stored place (a
    repository method of its own, not `UpsertLocationAsync` with a whole copy), and writing back at encounter end must skip a placement whose stored state
    changed after the encounter took its copy. Not built yet; `UpsertLocationAsync` still replaces the whole location.
  - The loader also refuses: a passive notice without routes, a second hazard type (the picker offers one), a scaling value that is not a whole number
    for a Number field, damage that is not dice, and field names that differ in case (with a hint). The content loader reports a scaled row naming a key
    that is not a field of the type (or differs in case), and an other-way row with no name.
  - Check names are stored as the content writes them, as labels (`Perception`, `Dexterity`, `Thieves' Tools`). H4a's lookup finds a check by key or label
    ignoring case (decision 2 below). A test lists the checks that need a typed bonus (`Thieves' Tools`, `Spellcasting`: only the homebrew hazards use them now); any other
    value must be a skill or attribute key or label, so a misspelling fails CI.
  - A test compares the shipped trap files with `Data/srd521-traps.yaml`, so a corrected data file fails until the pack is regenerated.
  - Tags: `hazard` is not a tag (the type says it); the tag test now reads `codex-hazards` too. The importer types numbers by key name (`dc`, `*Dc`, `successes`,
    `armorClass`, `hitPoints`, `damageThreshold`, `initiative`) and `complex` as a Boolean, through a fixed key list in `Traps.cs`.
- **Hazard shape (after reading the SRD).** The data file was checked against the SRD 5.2.1 text (printed pp. 199-201) and the old shape could not hold
  it. What changed and why: a hazard has **detect and disarm as lists of route rows** (a trap has up to three of each, and most disarms need no
  check at all: cutting a wire, wedging an Iron Spike, plugging holes), **one effect with an optional save** (the pits have none), **damage as a list of
  parts** (the Spiked Pit is 1d6 bludgeoning plus 2d8 piercing; the darts are 1d6 poison, 1d3 times), conditions, an area, a duration, a depth,
  an `onSave` of `half` or `none`, and durability and initiative for the Rolling Stone. The SRD traps no longer use `Thieves' Tools` as a check
  (only the Falling Net's Set the Trap needs the tools, in `needs`). Values corrected: Collapsing Roof save DC 13 and Perception DC 11, no
  Restrained; Falling Net Perception DC 11, Athletics Escape that rises with the save DC (10, 12, 14, 16), Set DC 13; Fire-Casting Statue Perception DC
  10 and 15, Arcana DC 15, the cone grows by band while the DC stays 15; Hidden Pit has no save and is found by Study and Investigation DC 15;
  Poisoned Darts save DC 13, 1d6 per dart and 1d3 darts, no half; Poisoned Needle Constitution DC 11, 1d10 poison, half on a save, found by Perception DC 15,
  disarmed by Sleight of Hand DC 15 (a failure sets it off), bands DC 13, 15, 17; Rolling Stone is Deadly at 11-16 and a Nuisance at 17-20, no half,
  Prone, a complex hazard with Initiative +8; Spiked Pit has no save. Invented rows (Avoid, Escape and Destroy where the SRD has none) and the
  invented resets are gone: only the statue and the darts reset. `HazardNumbers` reads the rows as `HazardRoute`, `HazardDamagePart` and
  `HazardCondition`, and `HazardNumbers.Problems` checks the rows per entry (a dc only with a check; a route with no check names what it needs; unique
  ids; `after` names a detect route; a damage part is dice; a condition is one of the system's). Choices: the statue's reading of the glyph is a detect route (after the glyph) and defacing it a disarm route, neither needing the other, the Falling Net restates its `methods` per band, and passive Perception may find a trap
  whose route is a Search action. `docs/schema/system.schema.json` and `SYSTEM_YAML_REFERENCE.md` are regenerated. The Russian and Hebrew labels of the
  new fields need native review.
- **H4a (check service).** Built as section 12 has it: `CheckService.cs` (records, `ActorChecks`, `CheckService`), `runtime.Checks`, `MakeCheckCommand`. What differs:
  - **`MakeCheckCommand.Result`** is a public property, so a caller or test can read what the check came to (the command is applied once, as `MakeRollCommand.Rolled` is).
  - **`ActorChecks.PassiveOf`** is added beside `HasPassive` (the passive rule that scores a skill); a Passive request resolves to the passive spec, whose label is the *skill's* ("Perception"), so
    `Check.Passive` reads "passive Perception 15 vs DC 15".
  - **Test 16 changed:** srd5e has Passive Investigation (and Insight), so the "no passive" skill in the tests is Stealth; Investigation does have one.
  - **A typed bonus on a passive or an AC read is the score itself** (`CheckBonus.Value` is "the passive score, or the AC"), not a number added to 10.
  - **Unresolved vs NeedsBonus:** a check the system does not know (Thieves' Tools) with no typed bonus is `NeedsBonus`; with one it rolls, with a `Spec` of the request's own name and an empty key. `Unresolved` is a passive asked of a check with no passive (or an unknown one).
  - **Printed creatures:** skill, save and passive are `NeedsBonus` without a typed bonus; their attribute check is still evaluated (the printed method makes the score the base score, so the modifier is right), and AC is printed. `IsPrinted` reads the printed method stamp `SeedFromBlueprint` writes (plus no class levels), not `BlueprintId`: a homebrew creature has none (`CastService` leaves it null) and must still be printed. Section 12.2's text said "BlueprintId set"; it now says the stamp. A creature restatted to another method is evaluated.
  - **Opt-ins and dice:** a ticked flat opt-in is added to the bonus (and is in `Parts`); a ticked dice opt-in (Guidance's 1d4) and an always-on dice amount (Bane's -1d4 on a save, `engine.Bonuses(...).Dice`) are appended to the roll expression, not folded into the number. This holds for a typed bonus too (a printed goblin under Bane with a typed +2 rolls `1d20+2-1d4`; a ticked flat box is added on top of the typed number). Ticking spends nothing and ends no condition (a GM's roll for a hazard is not the player's roll). The tick-box and dice reads share the evaluation's guard: a formula the table cannot work out gives no boxes and no dice, never a throw, so a `MakeAll` is not left half done with rolls recorded.
  - **AC is read from the live sheet** (`LiveSheetOf`), also for a printed or manual one, and a printed AC does not need the evaluation to succeed.
  - **Failures:** a passive the sheet has none of is logged by `Check.Unresolved` ("has no passive score, so nothing was read"), not as a missing bonus. `MakeCheckCommand` changes no actor state, so its `AffectedActorIds` is empty (as `RollDiceCommand`'s); an open check's label is a `SystemKind` term.
  - **`Kind = Passive` with a skill's name** ("Perception") resolves to that skill's passive; a skill with none is not found. H4b may set either.
  - **Player's own roll:** `ActorSheet` reads its last roll back through `TableVisibility.OwnLast` (own user, never secret), since a secret hazard check on the same actor and label can land in the log between the roll and the read.
  - **Degrees seam (P6):** `ActorChecks.Grade(total, natural, dc)` takes no system, so P6 adds an overload (or the service branches) and the Web panel must use the same one. The skipped placeholder test describes the target.
  - **Not done:** the AC label is still the English "Armor Class" in code and an AC read says "passive Armor Class ... vs DC" through `MakeCheckCommand`; the loader does not check `printedSkills`/`printedSaves`; `Suggest` takes the first actorBlueprint type that names the field.
  - **AC:** `Active` auto-AC is the evaluated total; a manual-mode or printed sheet reads `sheet[armorClass.Key]` (source `Printed`). A request with `Kind = ArmorClass` is how `ArmorClassOf` is made.
  - **`SheetEvaluation.AttributeModifiers`** is filled for every attribute at the start of `EvaluateSkills`, so it exists for a system with `sheetRules` but no skills; a system without `sheetRules` is never evaluated, and its attribute check needs a typed bonus.
  - **`printedSkills` / `printedSaves`** are two new optional properties of an `actorBlueprint` type (`ContentTypeDefinition.PrintedSkills`, `PrintedSaves`), named in srd5e's `monster` type; schema and reference regenerated. The loader does not check that the field exists.
  - **Event strings** (`Strings.Event`, en/ru/he; ru and he need native review): the three of 12.4, plus `Check.Failed` (unknown actor, bad dice) and `Check.Outcome.Success` / `.Failure` / `.Other`, because the outcome word is a part of its own. The event type is `DiceRoll`, so no new type label. A secret check's label is a `GmOnly` term.
  - **Not built (as section 12 says):** `dice.checkRoll` and degrees (P6). A skipped test stands for it.

- **H4a gaps closed (before H4b).** The `MakeAll` guard test now really throws inside the tick-box and always-on-dice read (a resolver that throws only from `OptInsReaching`;
  with the guard removed the test fails, with it restored it passes; the old formula test never reached a throw because the engine swallows bad formulas). A live-versus-stored
  armor class test (a table status takes 2 off, the stored sheet does not) fails if the evaluation reads the stored sheet. The armor class label is no longer English in code: the
  spec's label is the system's own label of the armor class field (else its sheet key), `ActorChecks.ArmorClassName(system)` names the request, and an armor class read says
  itself from `Event.Check.ArmorClass` (en, ru, he), with the GM-only mark of a secret read as an extra argument the sentence does not print. `CheckMessages.Of` is the one place a check's log
  sentence is made, for `MakeCheckCommand` and the hazard command.
- **H4b (the hazard command and panel).** Built: `HazardCommand` (`HazardCommand.cs`), the ledger of live state (`HazardLedger.cs`), `IRegionRepository.PatchPlacedHazardAsync`,
  `SetEncounterPlaceCommand`, and in the Web `HazardsTable` (loads places, entries and the encounter's live copy, sends commands) over the presentational `HazardsPanel`, drawn in the
  console's GM column. What differs from the design above, and why:
  - **Six actions, not three.** `Notice` (a sweep: reports only, never records), `Find` (one character, one detect route: a check is rolled, a no-check route is done by what it
    needs; a success records the route in the placement's `Found` and the character in `NoticedBy`, and a Hidden hazard becomes Noticed), `MarkNoticed` (4.2's "the GM then marks it noticed":
    adds the characters and the routes that were open when the GM pressed it), `Disable`, `Trigger` and `SetState` (the state picker, a logged command; Hidden also clears successes,
    noticed-by and found). `PlacedHazard.Found` is new (route ids), carried by `Tidy` and by `PlacedHazardForm`.
  - **Numbers only through `HazardNumbers.For(entry, type, placement.Levels)`**; checks only through `runtime.Checks` with `Secret = true`. A sweep asks for a passive (`Passive = true`
    with the route's skill name) only where `notice.passive` holds, the route does not say `passive: false` and `ActorChecks.HasPassive` is true; otherwise it rolls (Arcana). It skips routes
    with no check and routes whose `after` is not found, and ones already found. `Find` and `Disable` roll actively.
  - **Disable:** a route with `after` is refused until its route is in `Found`; a route with no check is a success without a roll; each success adds one to `Successes` and
    reaching the route's `successes` disables (every successful disarm route disarms; a detect route never does). A failed check whose `CheckOutcome.Key` is in the route's
    `triggersOn` sets the hazard off **on the character who tried** (choice made once: the effect hits the one who tried, state Triggered; the GM can still Trigger on others).
  - **Trigger/effect:** damage is rolled once per part (a part's `count` is dice or a number) and every die is a secret roll entry; each target does an attack against armor class (when the
    system's hazard block names an `attack` field; srd5e's has none, so that branch is covered by a test that names one) and/or the save (`Kind = Save`); a passed save takes half or none
    by `onSave`. Each damage part is applied by `ApplyDamageCommand` through the new `CampaignRuntime.ApplyUndoably`, which the loop now also uses for every undoable command. **Conditions
    stay text** in the log line for the GM to add (4.2), not applied. A printed creature's save or armor class is `NeedsBonus`: nothing is done for it, the panel shows a number field for it, and
    the typed number goes with the repeat of that request only (same hazard, action and route), prefilled from
    the printed sheet when `ActorChecks.Suggest` finds one (the panel's field shows it; the GM can type another). **The repeat only finishes:** a trigger that hit some creatures and waited on others
    hands back its rolled damage (`DamageRolled`) and who waited (`NeedsNumber`); `HazardsTable` keeps them, and the same action pressed again is sent as `HazardCommand(..., Rolled)` for the waiting
    creatures alone, with the same roll, so no one is hit twice. What the damage did (immune, resisted, vulnerable, temporary hit points, down, dead, a concentration save owed or lost) is said
    in the GM-only log line as `Event.Hazard.*`, not only the amount. **Each typed number is keyed by the question** (`HazardAsk.Key`: character, kind (bonus, passive score, armor class) and check), so in one sweep a printed creature's passive Perception score and its Arcana bonus are two numbers, and in a strike its armor class and its save bonus are two; the panel asks for each with its own label (`Combat.Hazard.NumberFor`, `NumberForScore`, `NumberForArmor`). A repeat of an attack re-rolls the attack for the waiting creature (only the damage is kept).
  - **Undo (a change from 4.1, which said a hazard-scoped undo comes later; the task asks for it now).** All of a target's damage parts are one undo point
    (`CampaignRuntime.ApplyUndoably(actorId, kind, changes)`, which the loop's per-command version now calls too), so a Spiked Pit's two parts come back together, and each creature's own
    Undo button still takes its damage back. The hazard's last change (a find, a disarm success, a marking, the state picker, **a trigger or a set-off with the damage it dealt**) is taken back
    by `UndoHazardCommand` as one: the ledger keeps the state just before and just after each hazard's last change and, for a trigger, each hurt creature with its undo point
    (`CampaignRuntime.UndoToken`). The undo runs only while the hazard still stands as the change left it (the encounter's live copy, else the placement as the panel just read it) **and** each
    hurt creature still has that damage as its last change; otherwise nothing is touched (never half undone) and the GM-only log says which (`Refused.CannotUndo` or `Refused.DamageChanged`).
    A second trigger on a hazard already Triggered makes its own undo point. The finishing repeat for the creatures that waited joins the first trigger's point. The snapshot lists the
    hazards that can offer it (`HazardUndo`) and the panel shows "Undo last change: {hazard}". The hazard's undo is lost when the table restarts.
  - **Live state.** Kept as `LiveHazard { Taken, Current }` under `hazard:{regionId}/{locationId}` in `EncounterDocument.ActiveEffects` while an encounter runs (read back defensively, since the
    database returns the object as JSON; a test reloads it), and in the snapshot as `LiveHazards` for the panel. Outside an encounter a change is queued and written after the command. Both
    writes go through `IRegionRepository.PatchPlacedHazardAsync(campaignId, regionId, locationId, change)`, which is one write under the repo's multi-writer rule (`RavenUpdate.RunAsync`: optimistic concurrency, `change` applied to a fresh load and run again when the
    place was saved in between) that changes `Feature.Hazard` alone (campaign-scoped, no user access: only a DM's table runs it). A place form's own save is still a whole-place save; it can
    wipe a patch only in the moment between its own load and save. At encounter end each changed placement is written **only if the stored one still equals what the
    encounter took** (checked inside that same write); a placement whose live copy equals its taken copy is not written at all, and the encounter forgets the keys. Outside an encounter a
    write goes over **the state the command began from only**, so a second tab that read the same old state does not overwrite the first. A write that is refused, or fails, does not stop the
    others or the command's log: it is said as a GM-only line (`Event.Hazard.NotWritten`, `Event.Hazard.WriteFailed`), and the failed change is not retried. `CampaignRuntimeManager` takes `IRegionRepository` as an optional
    last parameter and sets `runtime.Places`.
  - **The encounter's place.** Nothing set `EncounterDocument.LocationId`, so 4.1's "the encounter's place" did not exist. The panel has a place picker (each place with a hazard in or under
    it; "All places" is the default); while an encounter runs the choice is saved on it by `SetEncounterPlaceCommand` and the snapshot carries it. Outside an encounter the choice is only the
    page's. **Decision (the user's):** the picker stays as the manual override. The default should be set automatically when an encounter is started from a prepared
    encounter, which does not exist yet (the app has no encounter builder; encounters are formed at the table through `QuickAddPanel`), so until that builder is built the picker defaults to
    "All places".
  - **For P12 (known gap):** repeating an attack for a creature that was waiting for a number re-rolls its attack; only the damage is kept.
  - **Secrecy.** Every message the command writes has `EventTerm.GmOnly(hazard name)` as its first argument, there are no `PlayerParts`, the event type is `Hazard` (`Event.Type.Hazard`),
    every roll entry is secret, damage is not logged through `ApplyDamageCommand.Describe` (which has a players' version), and the panel is drawn only for the GM. Tests check the redactor (both
    views) and `TableVisibility.Rolls` for a player.
  - **Not built:** `rank` (a route's minimum rank is shown nowhere and not enforced; srd5e has no ranks, so `Hazard.RankTooLow` is not written), the `methods` rows as buttons (the panel lists their
    names), conditions applied automatically, the encounter-builder "Hazards" group and everything from H5. Rolling Stone's Crush save on any creature's turn is H5's (section 12.6, item 5).
  - **Strings.** Panel: `Combat.Hazard.*` (`Strings.Combat`); log: `Event.Hazard.*` and `Event.Type.Hazard` (`Strings.Event`), en, ru, he; ru and he need native review. The design's
    `Hazard.Noticed` ("{0} notices {1} ({2} vs DC {3})") is not used: the check's own line says the roll and DC, and `Hazard.Noticed` says who found which route.
  - **Panel details.** A number typed for a printed creature goes with the next request only, for the creatures that were asked, and is then cleared (it was a save bonus; it must never become
    that creature's Perception or another hazard's save). The panel's loaded copy of the places is read again when the encounter running or the hazards it holds change, so it is not stale after an
    encounter ends and writes back.
  - **Set-off targets (choice kept, 12.6):** a disarm that sets the hazard off hits the one who tried, as the Poisoned Needle says; an area hazard or a critical failure that should reach more
    creatures is a content or contract decision for a later step (today the GM presses Trigger for the others, which rolls its own damage).
  - **Tests:** `HazardCommandTests` (30), `HazardsPanelTests` (19), `HazardsTableTests` (3), the `PlacedHazard.Found` cases in the persistence and form tests, and the three H4a-gap tests; the whole
    filtered suite passes.

## 12. H4a: the server-side check service (interface)

Plan row H4a. The new file is `src/Codex.Persistence/Runtime/CheckService.cs`, with small changes to `CampaignRuntime.cs` and
`SheetRuleEngine`. H4b (`HazardCommand`) is the first caller. A later "the GM asks for a check" feature is the second, through
`MakeCheckCommand`.

### 12.1 Decisions

1. **The service runs inside a command, never as a command of its own.** `HazardCommand.Apply` makes several checks (Notice for every PC,
   Trigger for every target) and writes them up as one event. So the service is a method the command calls on the runtime's
   single-writer loop: `runtime.Checks.Make(...)`. It rolls, records one `RollLogEntry` for each die roll, and returns results. Writing
   the `SessionEvent` is the caller's job. `MakeCheckCommand` is a thin wrapper for a single check.

   The half that involves no dice (`ActorChecks`: finding the check, working out the bonus, grading) is pure and static. The Web panel
   can then prefill a number off the loop from the snapshot's live sheet, the way `SheetConditions.LiveView` is used today.

2. **Where the bonus comes from, first source that has one wins:**
   1. **Typed**: the bonus the GM entered.
   2. **Evaluated**: the actor's live sheet. That is `runtime.LiveSheetOf(actor)` (stored sheet, plus the table's pools, statuses and
      valued conditions) through `SheetRuleEngine.EvaluateLive`, so conditions count.
   3. **Printed**: the creature's own numbers. Only AC is read from these, as `sheet[sheetRules.armorClass.Key]`, because the monster
      type's `armorClass` property uses that same key.
   4. **Unresolved**: no source has a bonus. The result is `NeedsBonus` and nothing is rolled.

   A creature with printed numbers never gets an evaluated skill or save. Its evaluation would come to the bare attribute modifier, which
   is wrong without anyone noticing. A parse of the printed text ("DEX +3, Perception +10") is offered only as the panel's prefill (`Suggest`),
   never used on its own.

   The check is found by key **or label**, ignoring case, in this order: skill, save (when the caller says it is a save), attribute
   (an attribute check), passive. Anything else is unresolved: Thieves' Tools, Spellcasting.

3. **Degrees come later as data.** The outcome is a string `Key` plus `Passed`, not an enum of game words. Today the grade is
   `dice.check` + bonus against the DC: `success` or `failure`, with the natural die kept. When a system sets `dice.checkRoll` (new and
   optional, the key of a `rolls:` entry with inputs `bonus` and `dc`), the service grades through that named roll's `outcomes` instead.
   Those are already data with `when` formulas (`total >= input.dc + 10`, the natural 20 and 1 shift), so four degrees need no new C# here.
   P6 adds `checkRoll` and `passes: true` on `RollOutcomeDefinition`. P12 maps `triggersOn: [criticalFailure]` by outcome key (see 12.6: the keys are a content contract). Neither changes these signatures.

### 12.2 Signatures

```csharp
namespace Codex.Persistence.Runtime;

/// <summary>What is rolled or read. Attribute: an attribute's check on its own. Passive: a sheet's passive score, no die.</summary>
public enum CheckKind { Skill, Save, Attribute, Passive, ArmorClass }

/// <summary>A check the system knows: its kind and its key on the sheet (a skill key, an attribute key, a passive key; empty for AC).</summary>
public sealed record CheckSpec(CheckKind Kind, string Key, string Label);

public enum CheckBonusSource { Typed, Evaluated, Printed }

/// <summary>The number added to the die (or the passive score, or the AC) and where it came from; Parts are the feature and condition
/// bonuses that reached it (SkillScore.Bonuses, CheckSources), for the log's "what counted".</summary>
public sealed record CheckBonus(int Value, CheckBonusSource Source, IReadOnlyList<Codex.Core.Rules.RollBonus> Parts);

public sealed record CheckRequest
{
    public required string ActorId { get; init; }
    /// <summary>As content writes it: "Perception", "perception", "Dexterity", "dex", "Thieves' Tools".</summary>
    public required string Check { get; init; }
    /// <summary>Forces the reading: a hazard's effect.save is a Save of that attribute, never an attribute check.</summary>
    public CheckKind? Kind { get; init; }
    public int? Dc { get; init; }
    /// <summary>The GM's number. Beats everything; required where the check is unresolved or the actor is printed.</summary>
    public int? Bonus { get; init; }
    /// <summary>Compare the passive score instead of rolling (a notice route, when notice.passive holds and the route allows it; see ActorChecks.HasPassive in 12.6).</summary>
    public bool Passive { get; init; }
    /// <summary>The dice to roll in place of dice.check, for an edge the data has no word for (the GM picks "2d20kh1"). Null: dice.check.</summary>
    public string? Dice { get; init; }
    public IReadOnlyCollection<string> OptIns { get; init; } = Array.Empty<string>();
    /// <summary>GM only: the roll log entry is secret and the result is not for players. Default true for anything a hazard asks.</summary>
    public bool Secret { get; init; } = true;
}

/// <summary>The grade: "success" / "failure" today, the named roll's outcome name later. Mark as RollLogEntry.Mark.</summary>
public sealed record CheckOutcome(string Key, bool Passed, string? Mark = null);

public enum CheckFailure { None, UnknownActor, Unresolved, NeedsBonus, BadDice }

public sealed record CheckResult(
    string ActorId,
    CheckSpec? Spec,
    CheckBonus? Bonus,
    int? Natural,          // the check die's face; null for a passive or AC read
    int Total,             // die + bonus, or the passive score, or the AC
    int? Dc,
    CheckOutcome? Outcome, // null when there was no DC to grade against
    RollLogEntry? Logged,  // the entry recorded, null for a passive, an AC read or a failure
    CheckFailure Failed);

/// <summary>The pure half: no dice, no runtime. Usable by the Web panel on a snapshot.</summary>
public static class ActorChecks
{
    public static CheckSpec? Resolve(SystemDefinition system, string check, CheckKind? kind = null);

    /// <summary>Printed iff the actor's sheet was seeded from a stat block (the sheet's method is `printed`, which `SeedFromBlueprint` stamps for
    /// every blueprint, homebrew ones included: their `BlueprintId` is null) and the evaluation has no class levels. A cast NPC with a full
    /// sheet is evaluated.</summary>
    public static bool IsPrinted(SystemDefinition system, ActorDocument actor, Codex.Core.Rules.SheetEvaluation evaluation);

    /// <summary>Null: no source has it, the caller must supply a typed bonus.</summary>
    public static CheckBonus? BonusOf(SystemDefinition system, ActorDocument actor, Codex.Core.Rules.SheetEvaluation live, CheckSpec spec);

    /// <summary>The prefill for a printed creature: a "Label +N" match of the spec's key or label in the printed text fields the system
    /// names (see below). Never used without the GM.</summary>
    public static int? Suggest(SystemDefinition system, ActorDocument actor, CheckSpec spec);

    /// <summary>The default grade; replaced by the named check roll when dice.checkRoll is set.</summary>
    public static CheckOutcome Grade(int total, int natural, int dc);
}

/// <summary>The rolling half, on the command loop only (it reads entities and records rolls). runtime.Checks.</summary>
public sealed class CheckService(CampaignRuntime runtime)
{
    public CheckResult Make(CheckRequest request, string rollerUserId);
    public IReadOnlyList<CheckResult> MakeAll(IEnumerable<CheckRequest> requests, string rollerUserId);

    /// <summary>A target's AC for an attack: evaluated, else printed; null when neither (the GM types it).</summary>
    public CheckResult ArmorClassOf(string actorId);
}

/// <summary>One check on its own, logged ("Check.Made" / "Check.Passive", GM-only parts when Secret). For the GM's "ask for a check".</summary>
public sealed record MakeCheckCommand(CheckRequest Request, string RollerUserId) : IRuntimeCommand;
```

### 12.3 Changes beside the new file

- **`CampaignRuntime`:** add `public CheckService Checks { get; }`. `LiveSheetOf`, `LinkedFor` and `Resolver` are already there; the
  service calls `new SheetRuleEngine(System, Resolver).EvaluateLive(LiveSheetOf(doc), LinkedFor(doc.Sheet))` once per actor per `Make`.
- **`SheetRuleEngine`:** expose the attribute modifier the attributes panel adds. `SheetEvaluation.AttributeModifiers[key]`, filled where
  `ModifierOf` already runs. Today the panel works it out on its own (`AttributesPanel.razor:68`).
- **Bonus from an evaluation:**
  - Skill: `Skills[key].Bonus`. It already includes `Extra` from the roll sites `check.skill.<key>`, `check.<attr>` and `check`, condition
    modifiers among them.
  - Save: `SavingThrows[key].Bonus`.
  - Attribute: `AttributeModifiers[key] + CheckBonuses[key]`.
  - Passive: the `Passives` entry whose rule's `skill` is the key.
  - AC: `ArmorClass.Total`.
  - Opt-ins: requests that name them get the site's tick boxes through `engine.OptIns(live, engine.SkillSite(key))` and friends, added to
    the bonus.
- **Printed text for `Suggest`:** reading C# for `savingThrows` or `skills` would put game names in code, so the system says which fields
  hold them. Add `actorBlueprint` options on the type: `printedSkills: skills` and `printedSaves: savingThrows`, as text fields. Without
  them, `Suggest` returns null.
- **Dice:** `runtime.DiceRoller.Roll($"{request.Dice ?? system.Dice.Check}{bonus:+#;-#;+0}")`. Natural is `Kept[CheckDieIndex]`. A
  `FormatException` becomes `BadDice`, and the loop keeps running.

### 12.4 Logging

- **Each rolled check:** one `RollLogEntry` with `ActorId`, `Label` = the check's label (localized through `Name`, as `MakeRollCommand`
  does), `Modifier` = the bonus, and `Outcome` and `Mark` from the grade. `IsSecret = request.Secret`. The DC is not in the label: the
  player-facing log shows only the label, and secret entries are already removed for players (`TableVisibility.cs:34`,
  `RavenSessionRepository.cs:58`).
- **A passive or an AC read:** records no roll.
- **The service writes no `SessionEvent`.** `CheckResult` carries everything the caller's message needs. When `Secret` is set, the
  caller's parts carry an `EventTerm.GmOnly` term, so `SessionEventRedactor` drops them for players. A Notice therefore never tells a
  player that a hidden hazard exists.
- **Strings**, in `Strings.Event`:
  - `Check.Made`: "{0}: {1} {2} vs DC {3}, {4}"
  - `Check.Passive`: "{0}: passive {1} {2} vs DC {3}"
  - `Check.NeedsBonus`

### 12.5 Tests (new `ActorCheckTests`, plus `CheckServiceTests` on a runtime with a scripted `IDiceRoller`)

1. `Resolve` finds a skill by key and by label ignoring case ("Perception", "perception"), an attribute by label ("Dexterity" -> `dex`),
   a save only when the kind is `Save`, and returns null for "Thieves' Tools" and "Spellcasting".
2. A PC's skill check rolls `dice.check` + `Skills[key].Bonus`: a scripted d20 of 12 with bonus +5 makes 17, a `success` against DC 15,
   with `Natural` 12.
3. A condition that holds at the table (an active effect on the entity, not on the stored sheet) changes the bonus. This proves the live
   view is read. A valued condition at 0 does not.
4. An opt-in the request ticks is added; one it does not tick is not.
5. Save and attribute checks use `SavingThrows` and `AttributeModifiers + CheckBonuses`.
6. Passive: no die is rolled, no roll is logged, and `Total` is the passive score. A sheet without that passive is `Unresolved`.
7. AC: a PC reads `ArmorClass.Total` (with a condition that changes AC). A printed creature reads `sheet[armorClass.Key]`.
8. Printed creature: a skill with no typed bonus is `NeedsBonus`, and nothing is rolled or logged. With `Bonus = 4` it rolls +4.
   `Suggest` finds +10 in "History +12, Perception +10" only when the type names `printedSkills`.
9. A typed bonus beats the evaluated one, and `Source` is `Typed`.
10. `Secret` gives a secret roll entry. Players' snapshot roll logs and the stored session leave it out.
11. A bad `Dice` override gives `BadDice`, and the runtime still takes the next command.
12. `MakeAll` with three actors gives three results in order and three log entries.
13. `MakeCheckCommand` describes itself with a GM-only part when secret. The redactor's player view has no event for it.
14. Grading: `Grade` gives `success` at total == DC and `failure` one below. A placeholder test marked `Skip` stands for the `checkRoll`
    path ("P6: four degrees through dice.checkRoll"), so P6 has a target in place.
15. Content (from review 5): every `check` in the `detect`, `disarm` and `methods` rows of the srd5e hazard packs, and every `saveAttribute`,
    resolves, or is on the test's list of values that need a typed bonus (`Thieves' Tools`, `Spellcasting`: the homebrew hazards only; the SRD
    traps ask for tools in `needs`). (The list test exists today in `Srd5eHazardsTests`, using skill and attribute keys and labels directly;
    switch it to `ActorChecks.Resolve` when that exists.)
16. `ActorChecks.HasPassive`: true for a skill with a passive on the sheet (Perception), false for Investigation, an attribute and a tool.

### 12.6 What the hazard shape amendment adds to H4a

The signatures above do not change. `CheckRequest` takes `Check`, `Dc`, `Passive` and `Kind` per call, and H4b fills them from one `HazardRoute`
(`HazardNumbers.NoticeRoutes`, `DisableRoutes`, `Methods`). A route with no `check` never calls the service, and a Notice sweep never completes it: the GM or a player picks it, and H4b then records it as done. Nothing in 12
reads a `HazardNotice` or `HazardDisable` member.

1. **Passive is per route.** Decision C: passive Perception can find a trap whose detect route is a Search action, so the SRD traps carry no
   `passive: false`. But `CheckRequest.Passive` on an Investigation route resolves to `Unresolved` (test 6). Add the pure method
   `ActorChecks.HasPassive(SystemDefinition system, CheckSpec spec)` so H4b asks for a passive only where the sheet has one and otherwise rolls.
2. **`CheckOutcome.Key` is a content contract.** A route's `triggersOn` lists outcome keys (`failure` today; P6 and P12 add `criticalFailure`), so the
   keys are not renamed.
3. **Found routes are recorded per placement (H4b).** A route's `after` needs per-route state. `PlacedHazard.NoticedBy` is per actor, not per route, so
   H4b adds something like `Found: [routeId]`: the statue's deface and read-the-glyph routes are offered only once `glyph` is found, and the plate's
   wedge once `plate` is. A detect route may come `after` another detect route (read-the-glyph after the glyph). A successful disarm route always
   disarms; a successful detect route only marks a route found.
4. **No-check routes are never done by a Notice sweep.** The sweep covers only routes that have a check (passive where `HasPassive`, else rolled). A
   no-check route (the statue's Detect Magic, a pit's wedge) is done only when the GM or a player picks it.
5. **For H5: the Rolling Stone's Crush save applies on other creatures' turns**, whenever a creature enters the stone's space while it rolls, not only on
   the stone's own initiative count. `routine` rows are not all steps of the hazard's own turn.
