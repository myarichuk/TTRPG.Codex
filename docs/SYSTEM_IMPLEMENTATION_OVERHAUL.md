# System implementation overhaul

Status: proposal, revision 2 (2026-11-08). Nothing below is built yet unless the audit or section 7 says so; steps 0 and 1
are done.
Revision 1 (2026-11-01) proposed five coarse rule hooks (`IRuleHooks`) with three suppliers. Revision 2 keeps the
three suppliers but moves the boundary: the problem is not *how* rules run, it is that the host's contract is made of
5e nouns. A script that computes armor class does not help a system whose armor is per hit location.

## 1. Where things stand (audit, 2026-11-08)

Revision 1's list, checked against the code:

| # | Item | Status | Evidence |
|---|---|---|---|
| 1 | In-app system install | Done (2026-11-09) | About installs, replaces and removes systems from a zip (step 10). The Library's pack import is unchanged. |
| 2 | Reload without restart | Done (2026-11-09) | `PluginLoader.ReloadAsync`, staged and swapped in one step (step 10). No file watcher: install, removal and the admin's *Reload systems* trigger it. |
| 3 | Load errors visible | Done (2026-11-08) | `LoadProblem` (source, subject, system, path) and `FailedSystem` (stated id, errors one by one). About groups them per system, lists failed systems, shows paths to the server admin only. The Library counts its own system's; the new-campaign dialog names failed systems. Failed DLLs are reported (they were only logged). Not done: line numbers in validator messages. (`LoadException` was already reported as a warning; the audit's earlier "no reader" was wrong.) |
| 4 | d20-shaped vocabulary | Partial | Crit marking moved off text search to a recorded check die (`DiceTerm.IsCheckDie`, `DiceRollResult.CheckDieIndex`), but "check die" still means `Sides == 20` and faces 20/1 (`DiceExpressionParser.cs:14`, `DiceResult.razor:69`). Grammar unchanged: no explode, success count, roll-under, bands. Attribute and bonus keys no longer default to 5e names (2026-11-08, see section 6); entry-property names (`hitDie`, `armorBase`...) and `level`/`class` field names still do. |
| 5 | DLL trust boundary | Done (2026-11-09) | Labelled and documented (step 9); never uploaded, only copied onto the server (step 10). Before, every `*.dll` under `plugins/` is `Assembly.LoadFrom`'d (`PluginLoader.cs:296`); no label, no doc statement. |
| 6 | Author's reference, JSON schema | Done (2026-11-09) | `docs/SYSTEM_YAML_REFERENCE.md` and `docs/schema/system.schema.json`, generated from the model (step 10). |
| 7 | Versioning, safe removal | Done (2026-11-09) | Campaign and session stamps; removal asks first (step 10). Before, a campaign whose system is missing is read-only (section 6). Versioning not started: a campaign stores a system id only. |
| — | `IRuleHooks`, Jint | Not started | No Jint reference; DynamicExpresso 2.17.2 is the only evaluator (`Codex.Core.csproj:14`). No `IDiceSource`. |

New findings:

- **The 5e coupling is wider than the rules engine.** 26 files in Web, Core and Persistence name `HitPoints`,
  `ArmorClass`, `SlotPool`, `DeathSave` or `Multiclass`. The sheet page (`ActorSheet.razor`, 1417 lines) composes a fixed
  set of 5e panels: `AttributesPanel`, `ClassesPanel`, `ArmorClassPanel`, `HitPointRulesPanel`, `SkillsPanel`,
  `SlotTracker`, `LevelUpStepper`. The table (`TableRules.cs`) floors the primary resource at zero
  (`pool.Set(poolName, Math.Max(next, 0))`, line 96), and hard-codes death saves, concentration (half damage, clamped)
  and rests. `ActorKind` is a fixed enum (`PlayerCharacter`, `NonPlayerCharacter`, `Monster`, `Models.cs:102`).
  `ResourcePoolComponent` holds integers only (`Components.cs:41`).
- **Formulas can't see beyond the sheet's numbers.** `FormulaContext` (`SheetRuleEngine.cs:881`) holds flat numeric
  sheet keys and attribute defaults. Formulas can't read class entries, per-class levels, list items or tables.
- **Unknown keys were silently dropped.** Both YAML loaders ignore keys the model has no property for (so a newer file
  loads in an older app), which also made a typo (`hitPoint:`) or a whole section this version does not read into
  nothing, without a word. Since 2026-11-08 `UnknownYamlKeys` names each one by path and the loader reports them as a load
  problem; the shipped systems have none. It is also what lets the reference fixtures load today and say what is not read.
- **The sheet's recompute sequence lived in a Razor page.** `ActorSheet.razor` called inherited values, totals,
  evaluation, slots, hit points and armor class in order; nothing else ran the same sequence. It is now
  `SheetRuleEngine.Recompute`, which the page, the golden corpus and the reference runner share.
- **A dead and unsafe script path. Removed 2026-11-08.** `ScriptEvaluator` compiled a pack ability's
  `Effects[].Params.Script` with DynamicExpresso, referencing DefaultEcs `Entity`; `ContentRegistry.ExecuteAbilityAsync`
  had no callers. It was deleted rather than fixed: `Entity` leads back to the raw `World` that B3 remediation meant to
  hide, and DynamicExpresso is not a sandbox. A pack that still carries a `Script` effect loads, with a load warning
  saying the script is ignored.

## 2. Design constraints: three systems that are not 5e

A design is only as general as the systems it was tested against. Three reference systems are the spec. Each is a test
fixture: a `system.yaml`, any scripts, and fixture sheets with expected results. Nothing ships as content unless its
licence allows it (section 8). Written 2026-11-08 (step 1): `tests/Codex.Tests/Fixtures/reference`, whose README holds the
concrete vocabulary and the spec format.

**A. SRD 3.5 (OGL 1.0a): the close cousin.** Same attributes and modifier, so it isolates every place the engine
assumes 5e's answer.
- BAB and base Fort/Ref/Will summed over every class's own progression.
- Skills by ranks: points per level (×4 at first), class vs cross-class cost and cap, synergy, armor check penalty,
  parametrised skills (Knowledge (arcana)).
- Typed bonuses with stacking rules (armor, shield, natural, deflection, dodge, enhancement, size); three AC values
  (normal, touch, flat-footed).
- HP bands: disabled at 0, dying below, dead at −10; nonlethal damage as a second track; DR x/magic and energy
  resistance as subtraction.
- A spell's level per class (Clr 1, Pal 1, Rgr 2); separate slots per class; Vancian preparation (a spell per slot,
  duplicates allowed); bonus slots by ability.
- Prestige class prerequisites (BAB +5, 8 ranks, a feat); threat range 19–20 with a confirmation roll.

**B. A Cyberpunk-RED-shaped homebrew: no levels, no classes.** The fixture uses original mechanics in that shape, no
R. Talsorian text (`LEGAL.md` records their policy).
- STAT + skill + 1d10 against a difficulty; a 10 explodes once, a 1 subtracts a second d10.
- HP = 10 + 5×⌈(BODY+WILL)/2⌉; at half HP every action takes −2; below zero, mortally wounded.
- Humanity is a track; EMP = ⌊Humanity/10⌋; each cyberware item lowers Humanity (a sum over a list).
- Armor is SP per location (head, body) and drops by one on each hit that gets through.
- Death save: roll under BODY with a penalty that grows each time.
- Advancement by spending Improvement Points, not levels; role abilities have ranks.

**C. Blades in the Dark (the Forged in the Dark SRD, CC-BY 3.0, see section 8): the far end.**
No HP, no modifiers, no level, a second kind of sheet, and state that belongs to the campaign.
- Roll a pool of d6 equal to an action rating, keep the highest: 6 full success, 4–5 partial, 1–3 bad. Two 6s:
  critical. Zero dice: roll 2, keep the lowest.
- The GM sets position (controlled, risky, desperate) and effect (limited, standard, great) as inputs to the roll.
  Teamwork: several characters help or lead one roll.
- Action ratings are dots (0–4); an attribute's rating is the *count* of its actions with at least one dot.
- Stress: 9 boxes; overflow clears stress and marks a trauma; the fourth trauma retires the character.
- Harm: text slots in levels (two at level 1, two at 2, one at 3); a full level pushes harm up; level 4 is fatal.
  Armor is boxes ticked to cancel harm.
- Load is chosen per job (light/normal/heavy); items are ticked when used, not chosen in advance.
- XP per attribute, triggered by end-of-session questions.
- The crew is a second sheet with its own tracks (heat, wanted level, rep, coin, tier, hold). Clocks (4/6/8 segments)
  and faction status (−3..+3) belong to the campaign, not to a character.

### What the three systems force

| Requirement | Forced by | Primitive (section 3) |
|---|---|---|
| Sum/max/count over list items and per-class progressions | A, B, C | List aggregates |
| Typed bonuses with stacking rules | A | Modifiers |
| Value of a track feeding a derived value (EMP from Humanity) | B | Values over tracks |
| Thresholds that apply effects (−2 at half HP, dying below 0) | A, B | Track thresholds |
| Negative tracks, boxes, text slots, overflow into another track | A, C | Tracks |
| Several numbers for one thing (touch/flat-footed AC; SP per location) | A, B | Values (no singular AC) |
| Rolls: pools, keep, explode/implode, roll-under, bands, crit by pattern | A, B, C | Rolls |
| Inputs to a roll from another participant (position/effect, a DC) | C | Roll inputs |
| A roll joined by several actors | C | Roll inputs (participants) |
| Damage/hit handling beyond "subtract, floor at zero" | A, B, C | Events → intents |
| Advancement without levels | B, C | Events (`advance`) + lists |
| A second kind of sheet | C | Sheet kinds |
| State owned by the campaign | C | Campaign objects |
| A sheet laid out by the system | A, B, C | Layout |

5e must come out of this unchanged (section 5).

## 3. The model: primitives the host knows

The host knows primitives; a system maps its nouns onto them. No primitive is named after a game concept.

### Values
Numbers on a sheet: entered, derived by formula, or the result of a function (section 4). A derived value can read
tracks, list aggregates and tables, not only the sheet's numbers.

**Modifiers** are typed contributions to a value: `{ target, amount, type, source, when }`. Stacking is data per type:
`sum` (default), `highest`, `lowest`. 3.5: `dodge: sum`, everything else `highest`. 5e: everything `sum`, plus a
*base* chosen as the best of candidates (armor, Unarmored Defense), so `base: best` is a modifier type. Sources are
equipped items, list picks, conditions and track thresholds. Each modifier carries its source, so the working
breakdown shows each line ("Dexterity +2 (limited to +2)").

### Tracks
A track is a resource with a current value: `{ key, max (formula), min (default 0, may be negative), display: number |
boxes | slots }`.
- **Thresholds**: `{ atOrBelow | atOrAbove (formula), effect (a condition or modifiers) }`. A threshold is re-checked
  when the track changes. 3.5's bands, CPR's Seriously Wounded, 5e's Unconscious at 0.
- **Overflow**: `{ into: otherTrack, then: reset | keep }`. Blades stress → trauma; nonlethal into lethal.
- **Slots**: a track whose units hold text (Blades harm, 3.5 Vancian slots holding a spell id), grouped in levels, with
  "push up when full".
- Persistence: numeric tracks stay in `ResourcePoolComponent`; slot contents live on the sheet document, since
  the pool is `int`-only.

### Lists
Library entries picked onto the sheet, with the existing `where` / `maxCount` rules, plus **aggregates**:
`sum(list, property)`, `max`, `min`, `count(list, where)`, `each(list, formula)`.
- Classes are a list with a per-item `classLevel`: BAB = `sum(classes, lookup(item.progression, item.classLevel,
  bab))`.
- Blades' attribute ratings are `count(actions in group, rating >= 1)`. Actions are a fixed *keyed* list (a dictionary
  of ratings), so a list may have fixed keys as well as picks.
- CPR cyberware: Humanity max = `WILL*10 - sum(cyberware, humanityLoss)` (homebrew numbers).
- A property may be a **map** (spell `levels: { Cleric: 1, Ranger: 2 }`), with `levelFor(item, class)` available to
  `where` rules.

### Rolls
A named roll definition:
- **build**: how many dice of what (`pool: rating`, `dice: 1d20`), with a rule for zero dice;
- **add**: values and modifiers;
- **select**: keep highest/lowest N, sum, count successes ≥ N;
- **explode / implode**: once or repeatedly;
- **compare**: against a target (DC, DV, a value on the sheet; `under` for roll-under);
- **outcome**: bands (`6: full, 4-5: partial, 1-3: bad`, `>= dc: success`) and patterns (`two 6s: critical`,
  `natural 20: critical`, threat range plus a confirmation roll).

The result carries the dice, the total and a named **outcome**. The UI marks crits and fumbles from the outcome, never
from dice sides. **Inputs** are named parameters supplied when the roll is made. They can come from the roller, the GM
(position, effect, a DC) or other participants (teamwork). A roll request records who supplied each input, so the
session log and secrecy can show it.

### Events → intents
The table raises events: `damage`, `heal`, `roll` (with outcome), `turnStart`, `turnEnd`, `rest(name)`,
`sessionStart`, `sessionEnd`, `advance`. A handler receives a read-only view of the event and returns **intents**. It
never mutates anything.

| Intent | Meaning |
|---|---|
| `adjust { track, by }` / `set { track, to }` | Change a track (thresholds and overflow follow) |
| `mark { track, slot?, text? }` / `clear { track, slot? }` | Boxes and slots |
| `condition { add \| remove, name, duration? }` | Conditions |
| `roll { definition, inputs }` | A follow-up roll (death save, confirmation, CPR critical injury) |
| `ask { who: gm \| player, choices }` | A choice the host asks for (Blades resist or accept; spend armor) |
| `log { key, args }` | A message to the session log, as a translation key |

The host turns intents into runtime commands (`RuntimeCommands.cs`), so undo, the session log and secrecy keep
working. Built-in handlers cover 5e's current behaviour (section 5).

### Sheet kinds
A system declares its sheet kinds: `character`, `npc`, `creature` for 5e; `scoundrel`, `crew` for Blades. Each kind has
its own values, tracks, lists and layout. `ActorKind` becomes the host's *role* for access and visibility (player-owned,
GM-owned), and the sheet kind is system data on the actor.

### Campaign objects
Tracks and tables owned by the campaign, not an actor: Blades clocks and faction status, a 3.5 party treasure, a CPR
reputation track. They have the same track semantics, visibility (GM only or shown to players), and live beside the
campaign document, in one of their own (step 7 says why).

### Layout
A sheet kind's layout is data: sections of widgets bound to keys.
- **Generic widgets**: `values` (a stat block), `track` (number/boxes/slots), `list`, `table` (keyed list with columns),
  `roll` (a button for a roll definition), `text`, `modifiers` (the working breakdown).
- **Built-in specialised widgets**: today's 5e panels (`armor-class`, `slots`, `classes`, `level-up`, `hit-points`),
  which a system may ask for by name.
- **Compiled widgets**: a Razor component library in a DLL, installed by the server owner. This is the only way to
  ship UI code. Scripts never produce UI.

The book's stat blocks and the AI's view of a sheet read the same layout, so a system that defines one gets them too.

## 4. Who implements a function: built-in, script, compiled

Revision 1 named a supplier per hook. Revision 2 names one per **function**, and every rule above calls functions.

```yaml
functions:
  hitPoints: { script: rules.js#hitPoints }                      # Jint
  derive35:  { type: "Acme.D35.Derive, Acme.D35" }               # compiled
  # sum, max, count, lookup, floor, ceil, mod, levelFor... are built in (C#)

values:
  - { key: EMP,    formula: "floor(Humanity / 10)" }
  - { key: HP_Max, formula: "hitPoints(BODY, WILL)" }

events:
  damage: { script: rules.js#onDamage }                          # returns intents
```

```js
// rules.js. The host passes plain data and reads plain data back.
export function hitPoints(body, will) {
  return { value: 10 + 5 * Math.ceil((body + will) / 2), parts: [["base", 10], ["BODY+WILL", body + will]] };
}
export function onDamage(ev, sheet) {
  const out = [{ adjust: { track: "HP", by: -Math.max(0, ev.amount - sheet[`SP_${ev.location}`]) } }];
  if (ev.amount > sheet[`SP_${ev.location}`]) out.push({ adjust: { track: `SP_${ev.location}`, by: -1 } });
  return out;
}
```

Expressions stay in DynamicExpresso; they are one-liners and already have the working breakdown. A function written
in JS or C# is callable from an expression exactly like a built-in.

### Rules of the contract (revision 1's, sharpened)
1. **Plain data in, plain data out, for every supplier.** Read-only views (`SheetView`, `EventView`, `RollView`) in;
   values, `{ value, parts }`, outcomes or intents out. No `World`, no repositories, no live objects. DLLs included.
2. **One supplier per function, named in YAML.** Absent means built-in. No hidden overrides.
3. **The host owns randomness.** Every supplier rolls through a host `IDiceSource` (`ctx.d(6)` in JS), so tests seed it
   and the log replays the dice actually rolled. A roll intent goes back to the host; scripts never call `Math.random`.
4. **Same arithmetic everywhere.** Integer division and rounding are defined by the host (`floor`, `ceil`, `div`),
   because C# and JS disagree (`7 / 2` is 3 in one, 3.5 in the other). Contract tests run the same formulas through all
   three suppliers.
5. **Same failure handling.** A timeout, exception or invalid result gives a GM-facing message naming system, function,
   supplier and (for scripts) line, and falls back to the built-in behaviour or no value. It never throws through a
   table command.
6. **Dependencies are known.** A function declares what it reads (`reads: [BODY, WILL]`), or the host records the
   reads through the view proxy on first evaluation. Results are cached by the inputs' values, never recomputed per
   render.
7. **Install rights.** YAML and scripts may be uploaded by a GM or admin; a server setting can turn scripts off. DLLs
   only by copying on the server machine. The Library labels a system "contains compiled code".
8. **Versioned.** A campaign records the system version and a hash of the YAML, each script and each DLL, and says so
   when one changes.

### Script supplier (Jint)
- `AllowClr` off, no .NET access. `TimeoutInterval`, `MaxStatements`, `LimitMemory`, `LimitRecursion` and a
  cancellation token on every call, set low. Regex timeouts and string size caps.
- Each script is parsed once (`Engine.PrepareScript`) and run on a small engine pool per system; a Jint engine is not
  thread-safe.
- ES modules with named exports; a generated `codex.d.ts` describes the views and intents, so authors get completion.
- Messages are translation keys resolved against `system.<lang>.yaml`, not literal text.

### Compiled supplier (DLL)
- Same assembly loading as today. The named type is checked against the function's signature at load; a mismatch is a
  load error shown in the Library.
- A whole `ICodexSystemPlugin` stays supported (custom ECS components, a narrative dice roller) and is labelled the same.
- The docs say plainly that a DLL runs with the server's rights.

## 5. 5e on the primitives

srd5e is re-expressed through the primitives, with identical results:

| Today | On the primitives |
|---|---|
| `derived` (proficiency, initiative) | Values |
| `ArmorClassRules` | `base: best` candidates (armor + capped Dex, unarmored, class formula) plus untyped modifiers from items and list picks |
| `SkillRules` (proficient/expert) | A keyed list with a 0/1/2 multiplier on the proficiency bonus; passives as values |
| `HitPointRules` | A built-in `hitPoints` function over the classes list; hp modes stay |
| `SlotPools` | Tracks per spell level, max from a built-in `slotsFor` function (the combine table stays data); `rest` events refill |
| `Down`, `DeathSaves` | HP track threshold (0 → Unconscious); `damage` and `turnStart` handlers, built in, returning intents |
| `Concentration` | A condition plus a built-in `damage` handler that returns a roll intent |
| `Rests`, `HitDice` | `rest` handlers; hit dice as a track |
| `Multiclass` | The classes list with per-item `classLevel`; prerequisites as `where` rules |
| The sheet's panels | srd5e's layout, asking for the built-in widgets by name |

The old `sheetRules` keys keep working for one release. The loader translates them to the primitives and warns, so
homebrew systems written against them don't break overnight.

**Acceptance**: the existing test suite passes unchanged, plus a golden corpus of srd5e sheets (every class at levels 1,
5, 11, 20, three multiclass builds, imported creatures, manual-mode sheets). Each sheet is evaluated before and after;
any difference in a derived value, violation or warning fails the change. Built 2026-11-08: `tests/Codex.Tests/Golden/srd5e`,
67 sheets (four multiclass builds, ten creatures, three manual or rolled sheets, two broken ones) with inputs frozen in
`sheets.json` and everything `Recompute` writes and reports in `expected.txt`; `CODEX_GOLDEN=update` rewrites it after a
change that is meant. It already caught one bug (section 6, "found by the corpus").

## 6. Alongside the model

From revision 1, still needed:
- **Load errors, finished.** Done 2026-11-08 except line numbers in validator messages (audit row 3).
- **Author's reference and JSON schema.** Generated from `SystemDefinition`, published with the docs, referenced from
  `system.yaml` (`# yaml-language-server: $schema=...`) so editors validate as you type.
- **Install and reload from the app.** Upload a system zip, validate, write, reload. Needs a reloadable
  `PluginLoader`; live tables keep the system they started with until they end.
- **Less 5e in the defaults.** Done 2026-11-08 for keys that name an attribute or a derived value: `armorClass.dexAttribute`
  and `.unarmored`, `hitPoints.attribute`, `skills.bonusKey`, `spellcasting.bonusKey`, `vitals.concentration.saveAttribute`,
  `vitals.hitDice.attribute` are required, and the loader names a missing one; `proficiencies.strengthAttribute` became
  optional (left out: no armor Strength check). Entry-property names still default to the SRD's; they only find nothing
  in a system whose entries lack them, so they wait for the primitives (step 2).
- **Safe removal.** Done 2026-11-08 for the reading side: the resolved `CampaignAccess` carries `SystemMissing`;
  `NowAsync` (where every repository write starts) refuses it, as do media writes, live-table sheet saves and starting a
  table. Saving the campaign document says `Campaign.SystemMissing` instead of throwing a bare exception, except to switch
  to an installed system, which is the way out. Reads and export work. A banner says so; Settings shows the missing
  system instead of silently displaying the first installed one. Still open: "removing a system that campaigns use asks
  first" needs in-app removal, which waits for install/reload.

New:
- **Found by the corpus: creatures broke the score cap.** Done 2026-11-08. An imported creature's printed scores above 20 (a
  pit fiend's Strength 26) were reported as "scores can't exceed 20" (`Actor.Rule.ScoreMax`), so under strict enforcement the
  DM overrode a rule problem on every big creature: `SeedFromBlueprint` set the manual method, which skips the
  character-creation checks but not `sheetRules.attributes.max`. A stat block now sets a method of its own, `printed`: the
  totals are the base scores, with no creation checks, no cap, and no background bonuses or increase picks added. `manual` stays capped. The DM can pick "As printed"
  on any sheet. Creatures imported before this are switched once, at the first start after it (`DatabaseUpgrades`,
  `2026-11-08.printed-stat-blocks`): a sheet from a stat block that says manual becomes printed when that leaves every score
  as it is, which is the case unless the DM has since given it a bonus or an increase; those stay manual and the log names
  them. The upgrade is recorded in the database (`codex/upgrades`), so a DM who sets a creature back to manual later is not
  overruled on the next start. Restatting a
  character had a second fault on the same path: the method was only set when the sheet had none, and the old base scores
  and background bonuses won over the block's (the rules total base + bonus), so a point-buy character with Strength 15 + 2
  restatted as a pit fiend still had 17. The block's scores are now the base scores. The character's bonuses and increase picks
  (Ability Score Improvement) stay on the sheet, left out while the sheet is printed, so restatting does not throw away
  their choices and they count again if the DM picks another method; the sheet says so where their pickers were. The corpus diff: the
  ten creatures say `method=printed`, and the five ScoreMax lines of `creature-aboleth` and `creature-pit-fiend` are gone.
- **Systems with no content of their own.** Done 2026-11-08: a `setting:` block in `system.yaml` (genre, tone, themes,
  technology, the supernatural, kinds of factions and places, what play looks like, what does not belong), in general
  terms, never a published setting's names (`CONTENT_PACKS.md` says how). The About page shows it; every content prompt
  gets it after the campaign line (`AiPrompts.WithSetting`), with the campaign winning and names invented. A
  `system.<lang>.yaml` may translate it for readers. Fixture: `tests/Codex.Tests/Fixtures/crews`, a Forged-in-the-Dark-shaped
  system with no packs. Tested: the brief, the world-building chat's request; the other content prompts share one helper in
  `AiFeatures` without a test of their own.
- **Delete the legacy ability-script path.** Done 2026-11-08: `Scripting/ScriptEvaluator.cs`,
  `ContentRegistry.ExecuteAbilityAsync` and `CodexWorld`'s script-only `AddStatus` overload are gone; a pack's `Script`
  effect gives a load warning.
- **Systems distributed separately.** Releases ship `content/` next to the executable (`ContentRoots.cs`), so every
  bundled system is inside every past release asset. Systems other than srd5e ship as separate archives installed
  through the in-app install, so withdrawing one (an OGL licence change, a takedown) means removing one download. With
  safe removal, a withdrawn system does not brick campaigns that already use it.
- **Licence model for more than CC-BY.** `LicenseInfo` gains `textFile` (the full licence travels with the content:
  OGL §10) and `openContent` / `productIdentity` designations (OGL §8); the About page shows them. The system name
  avoids trademarks (OGL §7: "Third Edition SRD (v3.5)", not "D&D", not "d20 System"). `LEGAL.md`'s "single licence
  per system" policy stays and is reworded for OGL-only systems.
- **Authoring tools.** A `tools/Codex.SystemCheck` command: validate a system, run its fixture sheets and seeded rolls
  against expected results, print the working of any value. The in-app "try this function" panel from revision 1 uses
  the same runner. CI runs it for every system in `content/systems` and every reference fixture (section 2).
- **AI and book.** Prompts already take their words from `ai:`. Advancement prompts move from "level up" to the
  `advance` event, so a system without levels gets suggestions too. Stat blocks in the book and in MonsterCard render
  from the layout, so they work for any system with one.
- **Determinism.** The session log records rolls with their dice and the function versions used, so a seeded replay
  reproduces a session. Contract tests rely on this.
- **Performance budget.** Full evaluation of a sheet stays under 5 ms built-in and 20 ms with scripts on the CI machine;
  a script call over 50 ms is logged. Measured by a benchmark test, not assumed.

## 7. Order

Each step lands with srd5e unchanged (section 5) and moves at least one reference fixture closer to green.

0. **Cleanups that need no design.** Done 2026-11-08: legacy script path deleted, load errors finished, attribute and
   bonus keys required, read-only campaigns for a missing system, `setting:` for systems with no content.
1. **Fixtures first.** Done 2026-11-08. The three reference systems (section 2) are in `tests/Codex.Tests/Fixtures/reference`
   with 95 expectations, each naming its step; 0 pass, as they should. `progress.txt` lists the ones that pass and
   `ReferenceFixtureTests` keeps it true in both directions. The golden srd5e corpus is in `tests/Codex.Tests/Golden`.
   Decisions the fixtures forced, now in the README's vocabulary:
   - `conditions` stays a list of names (today's syntax); what a condition does goes in `conditionEffects`.
   - Tracks have `thresholds`, which add effects and can hold together (a serious wound and a mortal one), and `bands`,
     of which one holds at a time (3.5's disabled, dying, dead).
   - Overflow says what happens to the excess: `reset` (stress into trauma), `carry` (heat into wanted level), `keep`.
   - Prerequisites are checked on the character without the pick's own levels; otherwise a prestige class meets its own
     base attack requirement.
   - A per-pick field (`classLevel`, `equipped`, `used`) is a list's `itemFields`; a fixed set of keys (skills, actions)
     is a `keyedList` with `derive` lambdas.
   - Aggregates take lambdas (`sum(classes, c => c.classLevel)`), which DynamicExpresso can parse, rather than property
     names in strings.
   - "No problems" passes on a host that checks nothing, so a purely negative expectation `requires` the positive ones.
2. **Values, modifiers, lists with aggregates.** Widen `FormulaContext`; add the built-in function library; move
   `derived`, AC, skills and HP onto them. The 3.5 sheet math goes green.
   - *The evaluator, done 2026-11-08.* `Codex.Core.Rules.SheetModel` reads the sheet kinds' values, modifiers, lists and
     keyed lists, with `modifierTypes`, `progressions`, `functions` and `conditionEffects`. `SheetRuleEngine.Evaluate`
     runs it for a system that has sheet kinds, so its values land in `Derived` and its rules in `Violations`. Every
     step-2 expectation passes (22 d35, 3 street, 3 crew), plus `street.emp-follows-current-humanity` (step 3), since a
     track's current value is still a number on the sheet. The golden corpus is unchanged. Rather than widening
     `FormulaContext`'s dictionary of doubles, the evaluator gives formulas typed parameters: numbers, strings, and
     lists and items as `DynamicObject`s. DynamicExpresso binds member access dynamically only on a parameter whose
     static type is one. Lambda parameters are typed by the delegate, and given overloads DynamicExpresso parses a lambda
     against just one of them. So each aggregate has a single signature: selectors return `double?` and predicates
     `bool?`. The behaviour this settled is in the reference README.
   - *One interpreter, done 2026-11-08.* srd5e's `derived`, carrying capacity and unarmored / class-defense armor class
     formulas run on `SheetFormulas`; the old private interpreter is gone. Corpus unchanged.
     One change of meaning for a `sheetRules` formula: a whole number is a double, so dividing two negative-signed
     constants rounds down (`-7 / 2` was -3, truncated; it is now `floor(-3.5)`, -4). Nothing in srd5e divides constants.
   - *Review, fixed 2026-11-09.* A formula could call public instance methods on the values it held. Each
     `.Replace('a', 'aa')` doubles a string, so forty in a row ask for a terabyte, and `str.ToString()` reaches a string
     from any number. The deleted interpreter had the same hole. `FormulaSandbox` now walks every parsed formula once,
     before it is cached. It allows calls to the built-ins, string concatenation, and dynamic member reads,
     operators and conversions; it refuses method calls, CLR member reads, `new`, indexing and assignment. Also fixed:
     - formulas over 2000 characters or 32 brackets deep are refused before parsing, since a parser stack overflow kills
       the process;
     - backslash escapes in strings now work;
     - prerequisites remove the right pick when a malformed list holds a non-pick;
     - formula names are cached per formula, not detected under a lock on every evaluation;
     - `DatabaseUpgrades` checks that it read the actor collection itself, not a possibly stale index, before recording
       the upgrade as done.
   - *Deferred to step 6 (decided 2026-11-08): srd5e's skills, armor class and hit points as declarations.* Those stages
     produce structured summaries the 5e panels read (armor class lines tied to items with dex caps, per-level hit point
     rows, skill proficiency and expertise). Re-expressing them now would translate into primitives and straight back for
     the same panels: churn and corpus risk for no visible change. Step 6 decides what the built-in widgets read, and moves
     them then. So does 5e's `base: best` modifier type (section 3), which only 5e armor class needs.
3. **Tracks.** Thresholds, negative minimums, overflow, boxes and slots. HP bands for 3.5 and CPR; Blades stress/trauma
   and harm.
   - *Done 2026-11-09, on the sheet.* `SheetModel` reads `tracks`:
     - maxima as `{key}_Max`;
     - thresholds (several hold) and bands (one holds), whose conditions bring their `conditionEffects`;
     - tracks per item and level (`each`), so d35 has one spell-slot track per casting class and level;
     - slot tracks checking what they hold and how many (`SlotHolds`, `TrackFull`);
     - range checks with negative minimums (`TrackRange`);
     - slots in levels bringing each filled level's condition (Blades' harm).

     The reference runner asks the evaluator for conditions. 33 of 95 pass: every step-3 expectation but street's three
     thresholds, which compare against `HP_Max`, and street works that out in `rules.js` (step 8). Their step tag is
     early. A named function's parameter that shares a name with a built-in (`bonusSpells(score, …)`) was taken for the
     built-in; parameters are now renamed in the body.
   - *Left for step 5 (events):* overflow (stress into trauma), `whenFull: pushUp` and `beyond` (harm), and the start
     values and live-table mirroring of tracks (`ResourcePoolComponent`). All of them act when a track changes, which is a
     table command.
4. **Rolls.** Roll definitions, the grammar extensions (pools, explode/implode, roll-under, success counting), outcomes
   and inputs. `DiceResult` reads the outcome. Add `IDiceSource`.
   - *Done 2026-11-09.* `Codex.Core.Rules.SheetRolls` makes the system's `rolls` against a sheet:
     - inputs, each recorded with who supplied it;
     - dice or pools with zero-dice rules and keep;
     - explode and implode per die;
     - adds;
     - outcomes, with roll-under as a `when` like any other;
     - follow-up rolls that can replace their parent's outcome (3.5's confirmation);
     - intents with amounts worked out at the roll;
     - group rolls.

     Faces come from `IDiceSource`: `RandomDiceSource`, and `SequenceDiceSource` for tests and fixtures.
     `StandardDiceRoller` takes one too. Its grammar gains exploding dice (`3d6!`) and success counting (`5d10>=8`).
     `SheetIntents.Apply` applies `adjust` and `set` to a track within its bounds, so a roll's cost can be checked. Overflow
     and the rest stay step 5's.

     `DiceResult` and `RollLogEntry` carry the outcome and its mark (`critical`, `fumble`). The display marks from the
     mark, and never from a d20 face when there is an outcome. Nothing in the app makes a definition roll yet: the layout's
     `roll` widget is step 6.

     Formulas gained what the fixtures' rolls read:
     - entries by a computed key (`skills[input.skill]`); the sandbox allows index reads, which are getters;
     - die faces that compare as numbers (`d => d == 6`);
     - `score(key)` falling back to a value.

     The loader warns about malformed roll definitions. 58 of 95 pass: every step-4 expectation but
     `street.a-serious-wound-takes-two-from-every-check`, which needs street's `HP_Max` threshold (step 8). Its step tag is
     early, like the step-3 threshold ones.
5. **Events → intents.** Re-express damage, death saves, concentration and rests in `TableRules` as built-in handlers.
   - *Done 2026-11-09, on the sheet.* `Codex.Core.Rules.SheetEvents` runs the system's `events`:
     - lines of `{ when, intents }`, whose formulas read the sheet and the event as `ev`;
     - `builtin: damage`, with damage reduction (the best that applies, bypassed by tag, for some types), resistance by
       type, and a nonlethal track;
     - events that are intents (`adjust`, `mark`...), which are the table's own edits and apply as they come.

     A handler returns intents and changes nothing. `SheetIntents.Apply` then does what they ask of the sheet:
     - overflow (`reset`, `carry`, `keep`, its `ask`; stopping 8 deep);
     - boxes; slots, and slots in levels that push up and bring `beyond`'s condition;
     - the sheet's own conditions.

     It leaves the rest for the table: another actor's intents, rolls, asks and log lines. Track starts are read, and
     Recompute writes each numeric track's maximum and declared start onto the sheet. `ActorSheetSync` mirrors numeric tracks
     into `ResourcePoolComponent`; slot tracks stay text on the sheet. Script handlers are warned about and wait for step 8;
     campaign targets wait for step 7.

     78 of 95 pass: every step-5 expectation.
     - Two fixture fixes: Blades' trauma choices are inline in the overflow's `ask`, not a top-level list nothing reads.
       3.5's nonlethal bands start with `equals: 0`, so a dying character with no nonlethal damage is not also unconscious.
     - Aggregates now take a list reached through a member (`count(ev.answers, a => a)`): the parser would not pass a
       dynamic argument to a parameter typed as a list.
     - Found on the way: `Derived` held whole numbers as `double`, not the `long` its comment promises. A conditional
       `x ? (long)n : n` is a double.
   - *Superseded: moved to step 6, then step 8, and done there (`VitalsHandlers`).*
   - *Not done, and why (decided 2026-11-09): the live table still runs 5e's damage, death saves, concentration and rests as
     `TableRules`' own code, and does not raise declared events.*
     - Intents act on tracks, and srd5e's hit points are not a track until step 6 moves them, with skills and armor class.
       Re-expressing its damage as intents now would mean intents on a pool the model does not know, translated back at
       once.
     - A system with sheet kinds has no usable sheet in the app until step 6's layout. The runtime also has no library to
       work a track's formula out with: it reads what Recompute left on the sheet.
     - So the table raising declared events (damage, heal, turnStart, rest) lands in step 6, with srd5e's hit points as a
       track and a library resolver handed to the runtime (the layout's `roll` widget needs one too, for `from: weapon`).
       Until then the engine and its tests carry the behaviour.
6. **Layout and sheet kinds.** `ActorSheet` renders from layout; the 5e panels become built-in widgets. Blades' crew as a
   second sheet kind. This is the largest UI step.
   srd5e's skills, armor class (with `base: best`) and hit points move onto the primitives here, deferred from step 2.
   The live table raises declared events here too, and srd5e's damage, death saves, concentration and rests become built-in
   handlers over its hit point track, deferred from step 5.
   - *Done 2026-11-09.* Layout is data, and the sheet page renders it:
     - `SheetKindDefinition.Layout` (and a top-level `layout` for a system without sheet kinds) holds sections of widgets.
       `SheetLayout` gives a sheet its kind's declared layout, the generic one for a kind without (every value, track, list
       and keyed list), or the built-in widgets in the order the sheet always had them. srd5e declares that layout, asking for
       the 5e panels by name (`attributes`, `skills`, `fields`, `classes`, `library`, `picks`, `inventory`, `hit-points`,
       `armor-class`, `slots`, `level-up`). The loader names unknown widgets, keys a kind does not have, missing rolls,
       built-in widgets on a sheet kind, and duplicate or empty sections.
     - `ActorSheet` builds its tabs and aside from the layout. The 5e panels are built-in widgets and render as before (the
       browser suite is unchanged). The generic widgets are `SheetWidget`, which reads `SheetView` (values, tracks, rows,
       modifier breakdowns, from `SheetModel`) and writes through `SheetEdits` and `SheetIntents`, so a box track's +
       overflows as the table's would. A new actor gets a sheet kind (by its role, or picked); the crew is a second kind.
     - The `roll` widget makes a definition roll: on the page, or with the table live, at the table (`MakeRollCommand`: in
       the shared roll log with its outcome, its intents applied there).
     - The live table raises declared events (`TableEvents`). For a system with sheet kinds, damage and healing go to its
       `damage`/`heal` handlers instead of the vitals rules, the turn order raises `turnEnd` and `turnStart`, a rest raises
       `rest`, and `RaiseEventCommand` raises any other. The handler sees the cached sheet with the live pools and conditions
       over it; numeric tracks go back to the pools, slot tracks and the sheet's conditions to the stored sheet key by key,
       conditions also as statuses. Another actor's direct intents are applied to them; rolls, asks and log lines are
       logged. Undo covers the sheet keys a change wrote. The table gets the campaign's library when it starts
       (`CampaignRuntimeManager.EntryResolvers`).
     - 5e's armor class runs on the primitives: its candidates and bonuses are typed modifiers through `ModifierStacking`,
       the routine `SheetModel` uses, with a new stacking rule `best` (`base: best` in srd5e's `modifierTypes`). 5e's
       resources and slot pools are tracks the model knows (hit points from 0 to `HP_Max`), so intents act on them.
       The golden corpus is unchanged.
     - Fixed on the way: an entered value's or a per-pick field's YAML default (`default: 0`) reached formulas as text; two
       equal bonuses of a `highest` type both counted; `CachedEntryResolver` did not load a sheet kind's entry types.
     - 80 of 95 pass: both step-6 expectations. The remaining 15 are campaign objects (step 7) and street's scripted rules
       (step 8).
   - *Not done, and why (decided 2026-11-09):*
     - srd5e's damage, death saves, concentration and rests still run as `TableRules`' own code, not as built-in handlers
       returning intents. The precondition step 5 named is met (hit points are a track the model knows), but the intents
       cannot yet say what those rules do: end the effects another creature holds by concentrating, kill (which ends what
       the dead one held and its turn counters), and count death saves both ways (three successes stabilise, three failures
       kill) on counters the ECS keeps, not the sheet. Re-expressing them means intents for those, death saves as tracks with
       thresholds, and the host reacting to `Dead`, against 60 encounter and runtime tests that pin today's behaviour. Moved
       to step 8 (decided 2026-11-09), where a script handler needs the same vocabulary.
     - srd5e's skills (a keyed list with a 0/1/2 multiplier) and hit point maximum (a built-in `hitPoints` function) were
       not moved: the panels read `SkillScore` and `HitPointSummary`, which the stages already produce from the same inputs,
       so moving them changes no result and no panel. They move when a system other than 5e needs those functions.
     - Track edits on the sheet page while the table is live are saved through the sheet merge, not as table commands, so
       they are not in the session log. The table has no control for raising an arbitrary declared event (`sessionEnd` with
       its answers); `RaiseEventCommand` exists for one.
7. **Campaign objects.** Clocks and faction tables. Blades goes green except for scripted parts.
   - *Done 2026-11-09.* `Codex.Core.Rules.CampaignObjects` reads the system's `campaign` (clock sizes and visibilities,
     tables of named rows with bounded fields) and the campaign's state beside it:
     - problems under `campaign.clocks` and `campaign.<table>`: sizes, fills, visibilities, duplicate or missing names,
       fields out of range; the loader checks the section itself and every handler line's `target`;
     - intents that name a `clock` or a `table` (`adjust`, `set`), kept within bounds; a clock that fills raises its
       `clock.full` log line once;
     - copy-on-write edits for the GM (add, resize, show or hide, rename and remove clocks; add, edit and remove rows), and
       what a player may see.

     `SheetEvents` runs `target` lines and sends the campaign's intents to the campaign state when given one, else leaves them
     for the table. The state is a document of its own (`CampaignObjectsDocument`, `CampaignObjects/{campaignId}`), not a
     field of the campaign: a clock ticks every few minutes at the table, and the campaign document is saved whole from
     Settings, so sharing it would have each lose the other's writes. It is exported and deleted with the campaign.
     The live table holds it like an actor: loaded at start, changed by commands (`CampaignEventCommand` for a tick or an
     event with no actor, `EditCampaignObjectsCommand` for the GM's edits; an actor's event or roll whose intents reach a
     clock changes it too), written back key by key, in the snapshot. Without a table the GM's changes go to the store.
     The Table section has a panel for them: clocks drawn as segmented faces with tick buttons, each table's rows with −/+
     for number fields. A player sees what is not kept for the GM, live while the table runs. A log line naming
     something kept for the GM (`EventTerm.GmOnly`) is left out of a player's log by `SessionEventRedactor`.

     83 of 95 pass: every step-7 expectation. One fixture fix: `crew.a-clock-fills-and-says-so` expected only the `log`
     intent, but `expect.intents` is the handler's intents and then what the changes raised, so the `adjust` comes first.
     Every key of the three fixtures' `system.yaml` is read now. The remaining 12 are street's scripted rules (step 8): its
     functions, the thresholds and checks that read `HP_Max`, and its scripted handlers.
   - *Not done, and why:* undo does not cover the campaign's objects (it is per creature, and a clock is no creature; the GM
     ticks back with −). A sheet formula cannot read a clock or a faction's status: nothing in the fixtures asks for it, and
     it would make a sheet's values depend on campaign state the sheet page does not load.
8. **Jint.** Functions first, then event handlers; `codex.d.ts`; `Codex.SystemCheck`. The CPR fixture's scripted rules
   go green.
   Moved here from step 6: srd5e's damage, death saves, concentration and rests as built-in handlers returning intents. The
   intent vocabulary grows by what they need and a script handler needs too: ending effects another creature holds
   (concentration), killing (which ends what the dead one held and its turn counters), and counters the table keeps beside
   the sheet (death saves both ways). Today's `TableRules` code and its 60 encounter and runtime tests are the oracle.
   - *Done 2026-11-09.* **Scripts.** `Codex.Core.Rules.SystemScripts` runs a system's `.js` files with Jint 4.17:
     - **Files.** Only the files system.yaml names are read. Each must be in the system's folder and at most 256 KB.
     - **Engines.** Files are parsed once as ES modules (`Engine.PrepareModule`) and run on a pool of up to four engines
       per system. An engine that failed is thrown away, so a module keeps no state between calls.
     - **Sandbox.** `AllowClr` is off, there are no imports, and `Math.random` throws.
     - **Limits per call.** 500 ms, 100000 statements, 16 MB, 64 calls deep, arrays of 10000, regular expressions within
       100 ms, and 10000 values returned.
     - **Data.** Plain data goes both ways, made afresh for each call.
     - **Failures.** A failure names the system, the function or handler, the script and the line (rule 5). It never throws
       through a table command: a function gets no value and the sheet gets a warning (`Actor.Rule.ScriptError`, the GM's),
       and a handler's event is logged as failed and changes nothing.
     - **Load problems.** The loader names a file that is missing or outside the folder, one that does not parse, an export
       that is not a function, a reference that is not `file#export`, and a function of more than four parameters.

     **Functions** are called from formulas like built-ins. They take numbers, text and flags, and return a number or
     `{ value, parts }`. A script function is given nothing but its arguments, so results are cached by them (rule 6), and
     `reads` is not needed.

     **Handlers** are `export(ev, sheet, ctx)`:
     - `sheet` is a plain view: attributes, values, tracks, lists (a keyed list by key) and conditions.
     - `ctx.call` reaches a named function.
     - `ctx.d` rolls through the host's dice (rule 3), and the table logs the faces.
     - What a handler returns is read as intents in system.yaml's shape, and checked.

     **New intents**, which scripts and handler lines can both use:
     - `set`/`adjust { list, key, field }`, on a keyed-list entry or a pick, within the field's bounds.
     - `kill`, `endConcentration` and `counter`, which the table applies to the creature.

     **`Codex.SystemCheck`** (tools/) is what an author runs:
     - It loads a folder as the server does and lists its problems.
     - It runs the folder's `spec.yaml`. The spec runner moved there from the tests.
     - With `--types`, it writes `codex.d.ts`, with the system's own names as literal types. Street's `rules.js` type-checks
       against it under `tsc --strict --checkJs`, and a test keeps the committed file current.

     **The street fixture** goes green: 95 of 95. One fixture fix: street's `check` added `modifiers('@checks')`, which every
     skill's value already carries through its groups, so a wound counted twice.

     **srd5e's vitals as handlers.**
     - `Codex.Core.Rules.VitalsHandlers` holds the handlers: damage (with concentration), rolled and recorded death saves,
       stabilizing, exhaustion and rests. Each reads a `VitalsView` (pools, conditions, player or not, sheet) and returns
       intents and a report.
     - Each replays `TableRules`' steps on a scratch copy of the view, so a later step reads what an earlier one did, as the
       old code read the entity.
     - `TableRules` now builds the view, calls the handler, and applies the intents to the entity:
       - `set` on pools, `counter`, and conditions with their source;
       - `kill` and `endConcentration` through its own `Kill` and `EndConcentration`, which reach the other creatures.
     - Its entry points and the commands' log lines are unchanged.
     - The 60 oracle tests pass unchanged, as do the 145 encounter, runtime, rest and vitals tests in all.
   - *Not done, and why:*
     - **Install rights and versioning** (rules 7 and 8) are step 10's: there is no server switch that turns scripts off, and
       nothing records a script's hash.
     - **Cancellation.** A call cannot be cancelled from outside. Jint takes one cancellation token per engine, not per
       call, and the 500 ms limit is what stops a call.
     - **A function's `parts`** are read and kept, but the sheet's breakdown does not show them yet.
     - **Log text.** A script's log keys are shown through the system's translations, as YAML handlers' are. Their arguments
       are not formatted into translated sentences.
     - ~~**Healing, temporary hit points and hit-dice spending** stay in `TableRules` as they were.~~ *Done 2026-11-10.*
       They were not in the moved list, but healing had its own copy of getting back up (`TableRules.GetUp`) beside the
       handlers' (a natural 20, a rest from zero). `VitalsHandlers` now has `Heal`, `GrantTemporary`, `SpendHitDice` (the
       dice and the attribute modifier come from the table, as a death save's roll does) and `Revive` (the DM's), all on
       the one `GetUp`; `TableRules` only reads the creature, applies the intents, and does `kill` and `endConcentration`.
       The encounter, runtime, rest and vitals tests pass unchanged.
     - **srd5e's handlers are not named in system.yaml.** The table calls them for a system with `vitals:`, and a sheet-kind
       system cannot name them as `{ builtin }` (its own `builtin: damage` is the primitives' one).
     - **Arithmetic** is the same in formulas and scripts because both are doubles (`7 / 2` is 3.5 in each), so scripts are
       given no `div` or `floor` of the host's. The contract tests across all three suppliers came with the compiled one
       (step 9).
9. **Compiled supplier.** Functions and widget DLLs, with the same contract tests.
   - *Done 2026-11-09.* **The contract** is in `Codex.Plugin.Abstractions` (`Compiled.cs`), the only thing a system's DLL
     references:
     - `ICodexFunction`: an `Arity` from 0 to 4, and `Call(arguments)` returning a `CodexValue`, which is a number with optional
       parts.
     - `ICodexEventHandler`: `Handle(ev, sheet, ctx)` returning `CodexIntent`s. `ctx` is an `ICodexContext` with `Call` and `D`.
     - `ICodexWidget`: `Render(request)` returning `WidgetContent`, made of heading, text, row, pills and action blocks.
     - `CodexMath`: the host's `Floor`, `Ceil` and `Div`. Formulas' `floor`, `ceil` and `div` now call it, so it is one piece
       of code (rule 4).

     **Naming a type.** system.yaml names one as `{ type: "Namespace.Type, Assembly" }`:
     - in `functions`, or as an event handler;
     - in the new top-level `widgets: { name: { type, label } }`, which a layout places by name.

     **Loading.** `Codex.Core.Rules.CompiledSuppliers` resolves every name when `SystemDefinitionLoader.Load` reads a
     system.yaml from disk. `Parse` never loads a DLL.
     - The assembly must be a DLL directly beside system.yaml. It goes into that folder's own `AssemblyLoadContext`, so two
       systems may carry different versions of a dependency.
     - What the host has resolves to the host's copy, Codex.Plugin.Abstractions above all, or the interfaces would be other
       types. A name the host has is refused, so a system cannot instantiate a host type.
     - Each type is instantiated once and kept in `SystemDefinition.Compiled`, which localized copies share.
     - `PluginLoader` no longer scans a folder holding a system.yaml for `ICodexSystemPlugin`s: those DLLs are the system's
       own.

     **Checks at load** (the doc's "checked against the function's signature"):
     - the type is public and concrete, with a public parameterless constructor;
     - it implements the interface its place needs;
     - a function's arity is 0 to 4;
     - nothing else is named in the same place.

     Each mismatch is a load problem naming the type and why, shown with the system on About.

     **Calls** (rules 1 and 5):
     - Plain data is copied in and copied out. Numbers become doubles; any other object fails the call.
     - A function is cached by its arguments, as a script's is.
     - A failure is a `CompiledException` naming the system, what was being worked out and the type. On the sheet it is the
       GM's warning `Actor.Rule.CompiledError`, the script's twin. At the table it is a failed event, as a script's is.

     **Widgets.**
     - `SheetView.Compiled` hands the widget the same sheet view a handler gets, and `SheetWidget` draws the blocks in the
       sheet's style. The widget is asked once per edit, not per render.
     - An action's intents go through `SheetIntents.Apply`, as a track widget's do. What the sheet cannot do itself goes to
       the page.

     **The Library label** (rule 7). `SystemSummary.ContainsCompiledCode` is set for a compiled plugin and for a data system
     that names compiled code. About marks the latter "contains compiled code", with a note that it runs with the server's
     rights.

     **Contract tests.** `tests/Codex.Tests/Fixtures/contract` writes the same functions and handlers three ways:
     - as formulas and handler lines;
     - in `rules.js`;
     - in `tests/Codex.ContractFixture`, a class library referencing Abstractions only, which the test build copies into
       `contract-fixture/`.

     `SupplierContractTests` installs the DLL beside a temporary copy and runs the spec. All 13 expectations pass:
     - the same hit points, the same `div(-7, 2) = -4`, flags and text arguments;
     - the same failure handling, as warnings naming the supplier;
     - the same intents from the hit handler in all three;
     - the same seeded die and `call` in the script and compiled `locate`.

     **The spec format gained two fields:**
     - `dice` for an event: the faces a handler's dice are handed.
     - `expect.warnings`: the GM's warnings, which `violations` never held.

     The page was driven once in Chromium: the widget shows on the sheet, its button takes HP from 12 to 13, and About shows
     the label. No E2E test keeps that, because the shared E2E app's content roots would gain a system.
   - *Not done, and why:*
     - **No limits.** A DLL is not stopped at a time or memory limit. It runs in the server's process with its rights, which the
       docs say plainly.
     - **No unloading.** The load context is not collectible, so replacing a DLL needs a restart. That is part of step 10's
       reload, as are install rights and the hashes of DLLs (rules 7 and 8).
     - **A widget's text is the widget's own.** It is given the reader's language and translates itself. The system's
       `system.<lang>.yaml` does not reach it, and its `label` in `widgets:` is not translated either.
     - **Not the book or the AI.** A widget's blocks are not yet read by the book's stat blocks or the AI's view of a sheet
       (section 3, "Layout").
     - **Not for whole plugins.** An `ICodexSystemPlugin` cannot supply these per function. A plugin that wants them ships a
       system.yaml beside its DLL.
     - **A function's `parts`** are read and kept, as a script's are, and shown nowhere yet (as at step 8).
10. **Install, reload, versioning, separate distribution.** The JSON schema and reference docs land alongside, from
    step 2 onwards.
   - *Done 2026-11-09.*

     **Reload.** `PluginLoader.ReloadAsync` reads every content root again:
     - Systems, packs and problems are built beside the old ones and published in one step. The content registry is filled
       through a staging loader (`IContentPackLoader.Staging`/`Commit`) and swapped in under its lock (`ContentRegistry.ReplaceWith`).
     - A reload that fails leaves the old set. `SystemsReload` says what was added, removed and changed (by fingerprint).
     - A table keeps the definition it started with until it ends: `CampaignRuntime.System` is taken at start.
     - A system folder's DLLs are read from their bytes (never locked) into a load context keyed by their hashes, so a
       replaced DLL gets a new context.
     - `Codex:Systems:Scripts: false` fails a system that names a script, saying why (rule 7).

     **Versions** (rule 8). `SystemFingerprint` is the version and SHA-256 of system.yaml, its translations, its scripts and
     every DLL beside it, with line endings normalized. `ChangesSince` lists the version and each file added, removed or changed.
     - `CampaignDocument.SystemStamp` is recorded when a campaign is made, switched or first saved, and by the upgrade
       `2026-11-09.system-stamps` for campaigns stored before.
     - The GM sees `SystemChangedNotice` on the campaign page (compiled code marked) until *Got it* (`AcceptSystemAsync`, GM only).
     - `SessionDocument.SystemStamps` records each system a table ran the session under.

     **Install** (rule 7, section 6). `SystemArchive` (Core) checks a zip:
     - unpacked into a hidden staging folder: no rooted path, `..` or link; at most 2000 entries, 16 MB a file and 64 MB in
       all, counted as written;
     - never compiled code of any kind; scripts refused when they are off;
     - exactly one system.yaml, loaded as the server loads it; each pack must be for it and is loaded for its problems.

     `SystemInstaller` (Web) puts systems in `Codex:Systems:InstallDirectory` (default `Systems` beside the database), a
     content root:
     - Rights: the admin; anyone signed in with `Codex:Systems:GmInstall`, never while exposed. Reload is the admin's.
     - A checked upload is a plan (what it replaces, the changes, how many campaigns use it) only its sender can install, once.
       Installing moves it into place and reloads, putting the old folder back if the new one does not load.
     - Only systems in the install folder can be replaced or removed. Removal asks first, naming the campaigns that become
       read-only.
     - About has the install panel, *Reload systems* and *Remove*. Driven once in Chromium: install, a campaign on it,
       replace (1.0.0 to 1.1.0, one campaign), the GM's notice and *Got it*, remove (read-only banner), reload.

     **Distribution.** `content/systems/bundled.txt` lists the systems inside the app (srd5e). `packaging/content-for-release.sh`
     and `package.ps1` leave the others out; `packaging/pack-systems.sh` (the release's `systems` job) writes each as
     `system-<id>-<version>.zip` with `Codex.SystemCheck <folder> --pack`, which refuses compiled code.

     **Schema and reference.** `SystemSchema` generates both from `SystemDefinition`, by the property rules `UnknownYamlKeys`
     uses, with the model's XML summaries (Abstractions now writes its XML docs). `Codex.SystemCheck --schema docs` writes them;
     a test keeps them current, and checks every system.yaml in the repository against the schema with what the loader also
     reports as unknown as the only allowed flags. A text property accepts any scalar, as the loader does (`min: 0` is a
     formula). Runtime-only properties are `[NotInYaml]`: left out, and reported as unknown keys. srd5e and simple-d6 name the
     schema in their first line.
   - *Follow-ups, done 2026-11-10* (the first "not done" list):
     - **Every key is described.** All 466 schema properties and every `$defs` class have an XML summary, traced to the code
       that reads them; keys this version does not read say so (`dice.check`, `entityTypes[].icon`, `itemFields[].display`...).
     - **Compiled plugins reload.** `FolderLoadContexts` gives each folder of DLLs (a system's suppliers, a content root's
       plugins) one collectible context keyed by every DLL's hash, read from bytes. A changed plugin DLL is loaded again by the
       next reload; a table that started before keeps the old plugin. Component types are registered once the reload is certain.
     - **Unloading.** A replaced context is retired; every instance it makes is tracked weakly, and `Sweep` (on each load and
       when a table ends) unloads it once none is alive, so no live table can call into it. Contexts of folders gone from
       under a loader's roots are retired too. A test sees the old context collected, and kept while an instance lives.
     - **No mixed rules.** A sheet saved at a running table is merged under the table's definition, and the sheet page shows
       that definition while the table runs.
     - **File watcher.** `Codex:Systems:Watch` (default on in Development): `SystemsWatcher` reloads two seconds after the
       content roots go quiet, ignoring hidden folders and `bin`/`obj`, taking its turn with installs.
     - **Rolls are stamped.** `RollLogEntry.SystemStamp` is the index of the session's system stamp the table ran under.
     - **E2E.** `SystemInstallE2ETests` installs, replaces (one campaign uses it), acknowledges and removes a system in Chromium;
       each E2E class runs its own app, so the installed system is seen by no other test.
   - *Still not done, and why:*
     - **The schema's URL** points at `main`, where it lands when this branch merges; the in-repo systems name it by relative
       path. Nothing to do until the merge.
     - **A context unloads only after a GC** has collected what it made, and only if the plugin holds nothing elsewhere (a
       thread it started, a static of the host's). Component types a replaced plugin registered stay in the registry, so a
       table that started before can still snapshot them; such a context is unloaded but stays in memory.

## 8. Content and licences for the reference systems

| System | Licence | What may ship |
|---|---|---|
| SRD 3.5 | OGL 1.0a | Fixture now. A content pack later, as a separate download, with the OGL text, Section 15 and designations (section 6). |
| CPR-shaped homebrew | Own work | Fixture only; mechanics in that shape, no R. Talsorian text, names or trademarks. |
| Blades in the Dark | Forged in the Dark SRD (December 2017), CC-BY 3.0 Unported | Fixture now; SRD text may ship later as a pack (see below). Should that not work out, a system in that shape ships rules-only, with a `setting:` block in genre terms (the `crews` fixture shows how). |

Blades / Forged in the Dark, checked 2026-11-08 against the publisher's page (https://bladesinthedark.com/licensing):
- **Licence.** The SRD contents are under CC-BY 3.0 Unported. Commercial use is not restricted and the licence is not
  viral, so the app's own licence does not conflict with it. `LicenseInfo` already carries what CC-BY needs.
- **What it covers.** Only the SRD (the core mechanics pages at https://bladesinthedark.com/basics), not the rulebook.
  Excluded: anything not in the SRD, the setting of Duskwall / the Shattered Isles, its NPCs, artwork and maps, and
  translations of the core book. Material must not harm One Seven Design's reputation.
- **Attribution.** A fixed sentence, placed with our copyright notice and the same size: "This work is based on Blades
  in the Dark (found at http://www.bladesinthedark.com/), product of One Seven Design, developed and authored by John
  Harper, ... licensed for our use under the Creative Commons Attribution 3.0 Unported license
  (http://creativecommons.org/licenses/by/3.0/)". Copy it exactly from the page when the pack is made; the elided words
  were not recorded here.
- **Names.** "Blades in the Dark" must not appear in a product's title, and its logo needs permission. The Forged in the
  Dark logo may be used with the notice "Blades in the Dark™ is a trademark of One Seven Design. The Forged in the Dark
  Logo is © One Seven Design, and is used with permission." No implied endorsement. So the system is named after the SRD
  (for example "Forged in the Dark SRD"), never "Blades in the Dark".
- **Not checked.** The CC-BY 3.0 legal code itself and the SRD's own copyright notice (which the "Copying" section asks
  us to keep) were not read; read both before shipping text.

## 9. Open questions

- Should a GM (not only a server admin) be able to install a script system on an internet-facing server? Proposal: yes,
  with low limits and a server switch; confirm.
- Rolls with other actors: teamwork (C) and 3.5 opposed checks need participants and a target's values. Proposal: a
  roll's inputs may name actors, whose views are read-only and limited to keys the layout marks as visible to the
  roller.
- Expression language: keep DynamicExpresso's C# syntax for one-liners, or move formulas to JS so authors learn one
  language? Proposal: keep, with host-defined `floor`/`div` (contract rule 4); revisit if authors trip over it.
- Sheet kinds and `ActorKind`: does every system kind map onto one of three host roles, or do roles become data too?
- Old `sheetRules` keys: how many releases does the compatibility translation stay?
- Ordering of intents from several handlers on one event (a condition's handler and the system's): proposal, system
  first, then conditions in the order they were applied; each intent logged with its source.
