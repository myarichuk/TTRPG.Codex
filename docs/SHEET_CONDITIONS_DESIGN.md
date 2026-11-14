# Conditions on sheet-rules sheets, with values: design

Status: designed 2026-11-14, open questions decided (see the end), not built. It closes the gap that `PF2E_HAZARDS_IMPLEMENTATION_PLAN.md` (section 1) names as shared by srd5e
and the PF2e pack (P4): a condition that changes numbers on a `sheetRules` sheet, and a condition with a value (Exhaustion 3, frightened 2).

## What it is

Today a condition changes numbers only on a sheet-kind sheet (Forged in the Dark): `conditionEffects` is read by `SheetModel` alone. On
srd5e a condition is a table status and changes nothing. The roll bonuses work (#306, #313-#315) left two things waiting on this:
- `ends` on a tick box is wired on srd5e but no srd5e bonus can say it, because `ends` belongs to condition effects.
- Bless, Bane and 5.2.1 Exhaustion ("a D20 Test is reduced by 2 times your Exhaustion level"; Speed reduced by 5 times it) have no way to
  reach the rolls.

After this:
- A condition's effects reach a sheet-rules sheet's rolls, armor class, derived values and Number fields, at the table and on the sheet page.
- A condition can carry a **value**. Its effects read the value, and the table raises and lowers it.
- **Exhaustion becomes the first valued condition.** The bespoke exhaustion counter folds into the general one.

## Data

```yaml
# srd5e system.yaml
conditionEffects:
  Exhaustion:                                    # no `value:` here: it comes from vitals.exhaustion (key Exhaustion, maxLevel 6)
    modifiers:
      - { target: attack,     amount: "-2 * value" }
      - { target: save,       amount: "-2 * value" }
      - { target: check,      amount: "-2 * value" }
      - { target: initiative, amount: "-2 * value" }   # a D20 Test, but in srd5e a derived value, not a roll site
      - { target: speed,      amount: "-5 * value" }   # the Number actor field

# A Pathfinder-style valued condition (the PF2e pack, and the pf2 reference fixture)
conditionEffects:
  Frightened:
    value: { max: 4, decrease: { on: turnEnd, by: 1 } }
    modifiers:
      - { target: attack, amount: "-value", type: status }
      - { target: save,   amount: "-value", type: status }
      - { target: check,  amount: "-value", type: status }
      - { target: classDc, amount: "-value", type: status }   # a derived value

# A tick box that ends its condition (the #315 path): +1d4 to one ability check, then the effect is gone.
conditionEffects:
  Guided:
    modifiers:
      - { target: check, amount: 1d4, optIn: { label: Guidance, ends: true } }
```

**The rules:**
- **`value`** on a condition's effects makes it a valued condition:
  - `key`: the pool and sheet key that holds the value. Default `condition_<slug>` (`condition_frightened`).
  - `max`: a number or a formula. A raise stops there.
  - `decrease: { on, by }`: the value drops by `by` at a moment. `on` is `turnEnd`, `turnStart`, a rest, or a declared moment.
- **The condition holds while its value is above 0**, and only then. It is never stored as a status (see "Storage").
- **In an effect's `amount` and `when`, `value` is the condition's value.** It is 1 for a condition with no `value`. The loader refuses a
  system that has a derived value, a sheet value or an actor field named `value`.
- **Targets** are the ones features already use on sheet-rules sheets:
  - the armor class and hit points keys;
  - a derived value or a Number actor field;
  - a roll target from `ROLL_BONUSES_DESIGN.md` ("Roll targets": `attack`, `attack.weapon.melee`, `save.<attr>`, `check.skill.<key>`,
    `damage`...).

  Dice amounts and `optIn` work as they do on features. `ends: true` is allowed only here, as today.
- **No umbrella roll word.** "Every D20 Test" is written as three modifiers (`attack`, `save`, `check`). Open question 2 asks whether PF2e's
  "all checks and DCs" makes that too long.

## Storage

**Statuses stay where they are.** A condition at the table is an `ActiveEffect` with rounds, expiry and held-by. Off the table, a sheet
may hold conditions in the `conditions` key, as sheet-kind sheets do (`SheetModel.ConditionsKey`).

**The live view is the one thing every host reads.** It is a pure function in Codex.Core,
`SheetConditions.LiveView(system, sheet, pools, statuses)`, made from the merge `TableEvents.Apply` does today inside that method. It takes
plain data because the Web hosts hold no `CampaignRuntime`: `TableCard` and `CombatConsole` already get an `ActorStateSnapshot` with
`Pools` and `Effects` (checked 2026-11-14), which is all it needs. The live view is:
- the stored sheet;
- the table's pools over its numeric keys, the `uses_*` counters and the condition value keys;
- `conditions`, which is:
  - the sheet's own conditions;
  - plus the table's statuses;
  - plus every valued condition whose key is above 0.

`TableEvents.Apply`, `CampaignRuntime`'s counter evaluation, `TableCard.LiveSheet`, `CombatConsole` and the sheet page all call it instead
of keeping their own copies.

**A valued condition is never a status.** Its value is its only record:
- It holds while its key is above 0, and the live view names it in `conditions`.
- The picker sends `AdjustConditionCommand(+1)` instead of adding a status.
- There is no status to keep in step, so no pairing rules and no paired undo.

This is how Exhaustion already works (a pool, no status), so live levels need no migration.

**Values live in the pool and on the sheet.** Checked 2026-11-14: Exhaustion's level is a pool in the table's state only.
`ActorSheetSync.ApplySheetToState` mirrors resources, tracks, slot pools and `uses_*` counters, not the exhaustion pool. So the sheet page
cannot see the level today. After this, every valued condition's key is mirrored like a `uses_*` counter:
- The table's pool is the live number.
- The sheet key is written back when it changes.
- Edits from the sheet page and the table merge by delta (`SheetCounters.Of` / `MergeByDelta` list condition value keys).

Exhaustion keeps its pool key `Exhaustion`, so live levels do not move; the first save after this writes them to the sheet.

## Engine

1. **Parse and checks.**
   - `ConditionEffectDefinition` gains `Value: ConditionValueDefinition?` (`key`, `max`, `decrease`).
   - `SystemDefinitionLoader` checks:
     - keys are unique, and none clashes with a resource or a track;
     - `max` parses;
     - `decrease.on` is a moment the system knows;
     - nothing else is named `value`.
   - With `vitals.exhaustion`, the effect named by `exhaustion.condition` gets its `value` from it (`key`, `max` = `maxLevel`), so srd5e
     declares the level once. Writing a `value` on that effect as well is a load error.
2. **Which conditions hold on a sheet-rules sheet.** `SheetRuleEngine` reads `conditions` from the sheet it is given, as `SheetModel`
   does, plus every valued condition whose key is above 0. One static `SheetConditions.Of(system, sheet)` serves both engines.
3. **Effects join the sheet-rules modifier path.**
   - `SiteBonus` takes condition modifiers beside feature modifiers. That covers attack, damage, save, check and skill sites, and so
     `EvaluateChecks`, `SkillScore.Bonus` and `SheetAction.AttackBonus`.
   - The modifiers loop of `EvaluateFeatures` takes them for the armor class, hit points, derived and Number-field targets
     (`KindOf`, `WithModifiers`).
   - `OptInsReaching` offers a condition's tick boxes with `OptInIds.Condition(name, index)`, the id `SheetModel` already uses.
   - Each condition modifier is an `ActiveFeature`-like source named after the condition, so `GrantedModifier`, `RollBonus` and the
     log name it ("Exhaustion −4").
   - `value` reaches `Amount(...)` through a copy of the derived numbers with `value` added, per condition.
   - The early returns that assume features (`EvaluateChecks` when `result.Features.Count == 0`, and `SiteBonus` looping features only)
     must also run when conditions hold. A sheet with conditions and no feature modifiers is the common case.
4. **The hybrid rule (PF2e).** On a system with both `sheetRules` and `sheetKinds`, a condition modifier goes to the sheet-rules side
   when its target is one `KindOf` knows (armor class, hit points, a derived value, a Number field, a roll target), and to `SheetModel`
   when it is a kind value, a keyed entry or a group. A target both sides claim is a load error. So frightened is never counted twice.
5. **Turn-end decrease.** `turnEnd` is raised to the system's `events:` only when a handler exists (`TableEvents.Handles`), which srd5e
   has none of. The turn advance in `RuntimeCommands` gets its own step: after `turnEnd`, lower each valued condition with
   `decrease.on: turnEnd` on the creature whose turn ended. `turnStart` likewise. Rests and moments lower it through `MomentCommand` and
   `RestCommand`. Each drop is logged and undoable with the turn.
6. **Exhaustion folds in.**
   - `AdjustExhaustionCommand` becomes `AdjustConditionCommand(actor, condition, delta)`.
   - Death at the maximum stays on `vitals.exhaustion`.
   - The rest step `ExhaustionDelta` becomes a `decrease: { on: Long Rest, by: 1 }` on the effect. It stays readable from
     `vitals.rests` until the srd5e yaml moves.
   - It is written as `value: { decrease: { on: Long Rest, by: 1 } }` on the Exhaustion effect; `key` and `max` still come from `vitals.exhaustion`, and writing either is a load error.

## Hosts that must pass the live view

Found by searching `src/Codex.Web` and `src/Codex.Persistence` for `Evaluate(`, `Recompute(`, `Bonuses(` and `OptIns(`. Each one evaluates
the stored sheet today, so without this list the feature passes its tests and does nothing at the table:

| Host | Today | After |
|---|---|---|
| `CampaignRuntime.cs:274` (the table's counters) | `actor.Sheet` | `SheetConditions.LiveView` |
| `CombatConsole.razor:490, 520` (cards' evaluation) | `actor.Sheet` | `LiveView` with the actor's snapshot |
| `TableCard.razor` `LiveSheet()` (opt-ins, rolls) | sheet + pools, no statuses | `LiveView` with `State.Pools` and `State.Effects` |
| `RollBonusTray.cs:51, 68` (always-on and ticked) | the sheet it is given | given the live view by its callers |
| `ActorSheet.razor:1245, 2072, 582` (sheet page) | `editSheet` | `editSheet` plus the table's statuses and values **while the actor is at a live table**; off the table, the sheet alone |
| `SheetWidget.razor:671, 687` (named rolls) | `Sheet` | the same live view on the sheet page |

Not changed: the AI drafter, the character assist and the join review (`AiFeatures`, `CharacterAssist`, `VaultRequests`). They evaluate a
sheet being built, where table conditions do not apply.

## Sheet and table

- **One stepper for every valued condition** replaces the bespoke exhaustion one in `TableCommandPanel`: "Frightened 2" with − and +,
  disabled at 0 and at `max`. A condition with no value keeps its toggle.
- **The condition picker** shows a valued condition with its value. Adding it starts at 1.
- **Rolls and cards** name the condition in their bonus list ("Exhaustion −4"), as features are named now.
- **The sheet page** shows the conditions that hold, read-only, while the actor is at a live table. Off the table, only the GM can set
  them there (the `conditions` key and values); players see them read-only.
- **Accessibility:**
  - the stepper's buttons are labelled "Raise Frightened for Ash" and "Lower...";
  - the value is in a `role="status"` span;
  - changes are logged as today;
  - right-to-left works.
- **Strings (en, ru, he):**
  - stepper labels;
  - "Frightened {0}";
  - "{condition} drops to {0}";
  - "{condition} ends";
  - load-check messages.

  ru and he need native review.

## srd5e data

- Exhaustion's effects as in "Data" (attack, save, check −2 × level, speed −5 × level). Both are SRD 5.2.1 rules text.
- Bless, Bane and Guided in the new `effects:` list (decision 1).
- **Stays text.** Most SRD conditions work through advantage, disadvantage, auto-fail or "can't act", which the engine has no word for:
  Blinded, Frightened (5e), Poisoned, Prone, Restrained and the rest. This design does not make them work, and the release note says so.

## Tests

- **Parse and load checks:**
  - a `value` with `key`, `max` and `decrease`;
  - a clashing key;
  - a system with something named `value`;
  - Exhaustion's key against `vitals.exhaustion`.
- **5.2.1 Exhaustion:** level 2 lowers attack, save, ability and skill checks and initiative by 4, and speed by 10. Level 6 kills, as now. A Long Rest
  lowers it by 1.
- **A valued condition at the table:**
  - raising from 0 makes it hold (the live view names it);
  - lowering to 0 ends it;
  - no status is ever written for it;
  - undo restores the value.
- **Turn-end decrease** on srd5e, which has no `events:`: frightened 2 is 1 after its turn, and gone after the next.
- **Sheet and table agree:** a value raised at the table shows on the sheet page after the save, and two edits merge by delta.
- **The `ends` path from #315 is reachable:** Guided offers +1d4 on a check, and rolling with it ticked removes Guided.
- **Hybrid:** in the pf2 fixture, frightened 2 lowers a skill check and the class DC by 2, counted once
  (`PF2E_HAZARDS_IMPLEMENTATION_PLAN.md`, P1). A target claimed by both sides is a load error.
- **Hosts:** a character's table card attack bonus drops while the character is exhausted (a runtime test over `LiveView`, since there
  is no bUnit).
- **Goldens:** srd5e changes only for the Exhaustion effects. fitd is byte-identical.

## Build order and who builds it

| # | Step | Who | Notes |
|---|---|---|---|
| C1 | Parse, load checks, `SheetConditions.Of`, Exhaustion's `value` from `vitals.exhaustion` | Sonnet | Small |
| C2 | Effects on the sheet-rules path: `SiteBonus`, the modifiers loop, opt-ins with condition ids, `value` in formulas, the hybrid routing rule | Sonnet builds; **Opus reviews the diff before C3** | The heart; srd5e golden identical until C5 |
| C3 | `SheetConditions.LiveView` and every host in the table above; condition value keys mirrored and merged by delta | Sonnet; Opus reviews | Touches `TableEvents.cs`, `CampaignRuntime.cs`, `ActorSheetSync.cs`, `SheetCounters.cs`, `TableCard.razor`, `CombatConsole.razor`, `ActorSheet.razor`, `SheetWidget.razor` |
| C4 | `AdjustConditionCommand` replaces `AdjustExhaustionCommand`; turn-start and turn-end decrease; rests through `decrease` | Sonnet | `RuntimeCommands.cs`, `TableRules.cs`, `VitalsHandlers.cs`, `SessionEventRedactor.cs` (it lists the `Exhaustion` event kind) |
| C5 | srd5e data (Exhaustion; Bless, Bane and Guided in `effects:`); golden | Sonnet | Rules judgment, so not Haiku |
| C6 | Stepper, picker, sheet page display; accessibility | Sonnet | `TableCommandPanel.razor` and the picker |
| C7 | Strings en/ru/he; `SYSTEM_YAML_REFERENCE.md` and schema regenerated; `CHARACTER_FEATURES_GAPS.md` | Haiku for strings; Sonnet for docs | |

C1 and C2 come first and in order. C3 and C4 touch different files and can follow in either order, one writer at a time unless the Serena
canary (plan step 0b) has passed. C5 to C7 follow C4.

Every brief starts with the Serena line in `PF2E_HAZARDS_IMPLEMENTATION_PLAN.md` section 6.

## Not in this design

- Advantage and disadvantage, auto-fail, and "can't take actions".
- **Printed creatures.** A monster's stat block is not evaluated and its attacks are prose, so no condition effect reaches a monster's
  numbers. The GM applies them, as today.
- `implies` (Unconscious brings Incapacitated and Prone; PF2e's unconscious brings blinded and off-guard). It is a small later step in
  `SheetConditions.Of`, worth doing next.
- Effects from another creature's sheet (who cast Bless), and concentration ending an effect on several creatures beyond what `HeldBy`
  does today.
- PF2e's dying, wounded and doomed, which interact. They are valued conditions plus the pack's own event handlers (P4 in the plan).

## Decisions (2026-11-14)

1. **Spell effects get their own list.**
   - A new top-level `effects:` list (Bless, Bane, Guided) sits beside `conditions`, and `conditionEffects` may name entries of either.
   - The picker shows them as their own group, "Spell effects", so the 15 SRD conditions stay as the SRD has them.
   - srd5e data (C5):
     - Bless: +1d4 to attack and save;
     - Bane: −1d4 to attack and save;
     - Guided: +1d4 to one check, as a tick box with `ends`.
   - C1 parses and checks the list, and C6 adds the picker group. Each entry is translated like a condition (`SystemTranslation.Conditions`).
2. **No umbrella roll target for now.** Data writes `attack`, `save` and `check` as separate modifiers. An umbrella word is
   considered again only if the pf2 fixture reads badly without one.
3. **Off the table, only the GM sets conditions and values on the sheet page.** Players see them read-only, as with damage today.
