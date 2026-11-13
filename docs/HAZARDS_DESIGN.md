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
| **Notice** | A DC to spot it, the check that spots it, optionally a minimum rank | Perception (passive or active) or Investigation vs DC | Perception vs Stealth DC, minimum proficiency rank | as srd5e (check when imported) |
| **Disable** | The check, DC, minimum rank, successes needed, what a critical failure does | One check (Thieves' Tools, Arcana) vs DC | Skill, rank, often 2 successes, crit failure triggers it | as srd5e |
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
      - { key: noticeDc,      label: Notice DC,       type: Number, category: Notice }
      - { key: noticeCheck,   label: Noticed with,    type: Text,   category: Notice }        # a skill key or label
      - { key: disableDc,     label: Disable DC,      type: Number, category: Disable }
      - { key: disableCheck,  label: Disabled with,   type: Text,   category: Disable }
      - { key: trigger,       label: Trigger,         type: LongText, category: Trigger }
      - { key: attackBonus,   label: Attack bonus,    type: Number, category: Effect }
      - { key: saveAttribute, label: Save,            type: Text,   category: Effect }
      - { key: saveDc,        label: Save DC,         type: Number, category: Effect }
      - { key: damage,        label: Damage,          type: Text,   category: Effect }        # dice: 4d10
      - { key: damageType,    label: Damage type,     type: Text,   category: Effect }
      - { key: effect,        label: Effect,          type: LongText, category: Effect }
      - { key: reset,         label: Reset,           type: LongText, category: Reset }
    hazard:
      notice:  { dc: noticeDc, check: noticeCheck, passive: true }
      disable: { dc: disableDc, check: disableCheck }
      trigger: trigger
      effect:  { attack: attackBonus, save: saveAttribute, saveDc: saveDc, damage: damage, damageType: damageType, text: effect }
      reset:   reset
      level:   level
```

`HazardDefinition` (new, on `ContentTypeDefinition`, `Hazard` property). Every member names a field of the type, so the engine never
reads a hard-coded property:

| Member | Shape | Notes |
|---|---|---|
| `notice` | `{ dc, check, rank, passive }` | `check` is a field holding a skill key or label; `rank` a field holding a minimum rank key (systems with ranked skills); `passive: true` lets Notice compare the sheet's passive score (the passive whose `skill` is the check) |
| `disable` | `{ dc, check, rank, successes, critFail }` | `successes` and `critFail` are fields (a number; a text or `trigger`) |
| `trigger` | a field | text |
| `effect` | `{ attack, save, saveDc, damage, damageType, text }` | Numbers the console can roll; the rest is `text` |
| `durability` | `{ armorClass, hardness, hitPoints, brokenThreshold }` | Any subset |
| `reset` | a field | text |
| `routine` | a field (`Collection` of `{ name, description }`) | Present only on systems with complex hazards |
| `complex` | a field (`Boolean`) | When true on an entry, the entry is registered as a hazard actor (3.3) |
| `level` | a field | What scaling and the world builder read |

The loader checks that every named field exists on the type, that `rank` is used only on a system with ranked skills, and that a
type does not have both `actorBlueprint` and `hazard`.

### 3.2 Scaling, for the world builder and the homebrew wizard

```yaml
hazards:
  scaling:                      # by the hazard's level field; the row with the highest level <= the hazard's applies
    - { level: 1,  noticeDc: <tbd>, disableDc: <tbd>, attackBonus: <tbd>, saveDc: <tbd>, damage: <tbd> }
    - { level: 5,  ... }
    - { level: 11, ... }
    - { level: 17, ... }
```

- **The numbers are not designed yet.** Where they come from depends on the system:
  - **srd5e:** from SRD 5.2.1 if it has a trap table (CC BY 4.0, with its attribution); otherwise this project designs its own,
    checked against the SRD's own typical DCs and monster damage, and recorded as such.
  - **Not from any book we do not license.** The well-known trap damage-by-level table and the trap DC bands in the 5e Dungeon
    Master's Guide are not in the SRD and must not be copied or closely mirrored.
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
| **Notice** | For each PC in the scene: with `notice.passive`, compares the passive score with the DC; otherwise rolls the sheet's check (the same skill roll the sheet's skill button makes). With `notice.rank`, a PC below the rank cannot notice it. The result goes to the GM only: "Notices: Ana (passive 15). Does not: Bo (12)". The GM then marks it noticed for those PCs, which reveals it to them |
| **Disable** | The GM picks the actor who tries. The engine rolls the actor's `disable.check` against the DC. A success adds one to `successes`; reaching `disable.successes` (default 1) disables it. A critical failure, where the system has degrees and `critFail` says so, triggers it |
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
triggers" through the `critFail` field. Until that roll exists, a system without degrees gets success or failure only.

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
| srd5e | **SRD 5.2.1 traps, if the SRD has them.** Check the SRD 5.2.1 PDF (CC BY 4.0) for a traps section before writing any. If present, import them into `srd-5.2.1/hazards` with the SRD attribution | |
| srd5e | `codex-flavor` (the project's own pack) or a new `codex-hazards` pack | 4-6 homebrew examples written by this project: a pit, a dart trap, a falling block, a flooding room (complex), a cursed doorway (save, condition), a gas vent (environmental). Numbers from 3.2 |
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
| Disable: one success disables; with `successes: 2`, two do; a critical failure triggers when `critFail` says so | same |
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
| 2 | srd5e: the `hazard` type and scaling; the SRD 5.2.1 check; 4-6 homebrew examples | S |
| 3 | `PlaceFeature.Hazard` with state; place page link and picker | S-M |
| 4a | Server-side check service: an actor's skill, save, passive or AC from its evaluation, rolled with `dice.check`; GM-entered bonus for printed creatures | M |
| 4b | `HazardCommand` (notice, disable, trigger) on 4a and the damage event; live state in `EncounterDocument.ActiveEffects`, written back at encounter end; the Hazards panel with its state picker; log | M |
| 5 | Complex hazards as actors: per-entry registration gate, `ToActorBlueprint` overload, `QuickAddPanel` split by the `hazard` tag; routine shown on its turn | M |
| 6 | World builder: proposal field, homebrew hazards, scaling check, prompt line | S-M |
| 7 | Homebrew wizard hazard mode; book export stat block | S |
| 8 | Strings en/ru/he (Haiku), accessibility pass | S |

Steps 1-4b give a working feature on srd5e. Step 4a is the largest single piece: it is new, not a reuse. The Pathfinder Second Edition pack adopts it when its own system exists, with `notice.rank`,
`disable.successes`, `critFail` and degrees (4.3).

## 10. Open questions

1. **Does SRD 5.2.1 have a traps section?** Believed yes; verify in the PDF before writing srd5e homebrew that could duplicate it.
2. **Who sees a noticed hazard?** Recommended: only the PCs who noticed it, until the GM reveals it to all.
3. **Hazard state across sessions.** A disabled trap stays disabled; a reset rule ("re-arms after 1 minute") is text the GM applies.
   Automatic reset is later.
4. **Fitd obstacles.** Should Forged in the Dark get a light "danger" type (a clock plus a harm level) later, or stay with the campaign
   clocks it has?
