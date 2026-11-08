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
| 1 | In-app system install | Not started | The Library imports content-pack zips only (`LibrarySection.razor:32`); a system is still copied into a content folder by hand. |
| 2 | Reload without restart | Not started | `PluginLoader.LoadAndInitializeAsync` returns once loaded (`PluginLoader.cs:61`); no watcher. |
| 3 | Load errors visible | Done (2026-11-08) | `LoadProblem` (source, subject, system, path) and `FailedSystem` (stated id, errors one by one). About groups them per system, lists failed systems, shows paths to the server admin only. The Library counts its own system's; the new-campaign dialog names failed systems. Failed DLLs are reported (they were only logged). Not done: line numbers in validator messages. (`LoadException` was already reported as a warning; the audit's earlier "no reader" was wrong.) |
| 4 | d20-shaped vocabulary | Partial | Crit marking moved off text search to a recorded check die (`DiceTerm.IsCheckDie`, `DiceRollResult.CheckDieIndex`), but "check die" still means `Sides == 20` and faces 20/1 (`DiceExpressionParser.cs:14`, `DiceResult.razor:69`). Grammar unchanged: no explode, success count, roll-under, bands. Attribute and bonus keys no longer default to 5e names (2026-11-08, see section 6); entry-property names (`hitDie`, `armorBase`...) and `level`/`class` field names still do. |
| 5 | DLL trust boundary | Not started | Every `*.dll` under `plugins/` is `Assembly.LoadFrom`'d (`PluginLoader.cs:296`); no label, no doc statement. |
| 6 | Author's reference, JSON schema | Not started | No `system.yaml` reference in `docs/`, no schema file. |
| 7 | Versioning, safe removal | Partial (2026-11-08) | A campaign whose system is missing is read-only (section 6). Versioning not started: a campaign stores a system id only. |
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
reputation track. They have the same track semantics, visibility (GM only or shown to players), and live in the
campaign document.

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
  on any sheet, which is how a creature imported before this gets uncapped (stored sheets are not rewritten). Restatting a
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
3. **Tracks.** Thresholds, negative minimums, overflow, boxes and slots. HP bands for 3.5 and CPR; Blades stress/trauma
   and harm.
4. **Rolls.** Roll definitions, the grammar extensions (pools, explode/implode, roll-under, success counting), outcomes
   and inputs. `DiceResult` reads the outcome. Add `IDiceSource`.
5. **Events → intents.** Re-express damage, death saves, concentration and rests in `TableRules` as built-in handlers.
6. **Layout and sheet kinds.** `ActorSheet` renders from layout; the 5e panels become built-in widgets. Blades' crew as a
   second sheet kind. This is the largest UI step.
7. **Campaign objects.** Clocks and faction tables. Blades goes green except for scripted parts.
8. **Jint.** Functions first, then event handlers; `codex.d.ts`; `Codex.SystemCheck`. The CPR fixture's scripted rules
   go green.
9. **Compiled supplier.** Functions and widget DLLs, with the same contract tests.
10. **Install, reload, versioning, separate distribution.** The JSON schema and reference docs land alongside, from
    step 2 onwards.

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
