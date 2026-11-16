# P4b: implied conditions, and conditions on printed creatures: design

Status: designed 2026-11-16 (Opus, read-only, on `origin/main`). The build is by Sonnet. Plan row P4b in `PF2E_HAZARDS_IMPLEMENTATION_PLAN.md`,
with P1 decisions 2 and 5. It follows `SHEET_CONDITIONS_DESIGN.md` (built, C1 to C7). Part (c), the level badge on the initiative row, is a
separate small UI step and is not covered here. Examples use invented names. Before the build: P1's fixture (`tests/Codex.Tests/Fixtures/reference/degrees/`,
still on `origin/test/degrees-fixture`) must be on main, and the build must not run beside H1 (both edit `SystemDefinitionLoader.cs`).

## (a) `implies`

### Data

```yaml
conditionEffects:
  Senseless:                       # holds while it is listed; brings two others while it holds
    implies: [Dazzled, Rooted]
  Overwhelmed:                     # a plain condition bringing a valued one at a fixed value
    implies: [{ condition: Shaken, value: 2 }]
  Dread:                           # a valued condition bringing a valued one that follows its own value
    value: { max: 4, decrease: { on: turnEnd, by: 1 } }
    implies: [{ condition: Shaken, value: "value" }]
  Shaken:
    value: { max: 4 }
    modifiers: [{ target: check, amount: "-value", type: status }]
```

- An item of `implies` is either a name, or a map `{ condition, value }`. Parse it with an `IYamlTypeConverter`, following
  `RowChooseConverter.cs`.
- An entry may have `implies` and no `modifiers`. srd5e needs this, because its conditions have no numbers.
- `value` is allowed only when the implied condition is valued. It is a number or a formula, and in it `value` means the source's value
  (1 when the source has no value). If you leave it out, a valued condition is implied at 1.

```csharp
// SheetKinds.cs, on ConditionEffectDefinition
public List<ConditionImplication> Implies { get; set; } = new();
public sealed class ConditionImplication { public string Condition { get; set; } = ""; public string? Value { get; set; } }
```

### Loader checks (`SystemDefinitionLoader.cs`, a new `ValidateImplications`)
Each check is an error at `conditionEffects.<name>.implies[<i>]`:
- The name is not one the system knows. Known names are `conditions`, `effects`, `conditionEffects` keys and the vitals condition names.
- The entry names its own condition, or names the same one twice.
- There is a cycle. Do a depth-first walk over the graph and name the loop: `Dread -> Shaken -> Dread`.
- There is a `value` on an implied condition that is not valued.
- There is a `value` on the exhaustion condition. Death at the top level hangs on the stored level, and an implied level never kills.
- The `value` formula does not parse (`SheetFormulas.For(definition).Check`).
- The `value` is a number above the implied condition's numeric `max`. A formula is clamped to `max` when it is worked out.

### Holding: expansion on read, nothing stored
The one rule: **`implies` is expanded every time held conditions are read, and never written.** There are no stored copies, so ending a source
needs no clean-up.

`SheetConditions` becomes `static partial`. Put the new code in a new file, `SheetConditions.Holding.cs`, which keeps `SheetConditions.cs` under 300 lines.

```csharp
public sealed record HeldCondition(string Name, double Value, bool Listed, IReadOnlyList<string> From);

/// What holds on its own: today's Of body, renamed. Valued conditions above 0, the listed ones, and a valued one at 0 dropped.
public static List<string> Listed(SystemDefinition system, IReadOnlyDictionary<string, object> sheet);
/// Listed, then implies expanded to a fixpoint. Listed ones come first, in order, then implied ones in the order they are found. Names compare case-insensitively.
public static IReadOnlyList<HeldCondition> Holding(SystemDefinition system, IReadOnlyDictionary<string, object> sheet);
/// Holding(...).Select(h => h.Name). Same signature as today, so SheetModel, ConditionModifiers and ConditionLines get implied ones for free.
public static List<string> Of(SystemDefinition system, IReadOnlyDictionary<string, object> sheet);
/// The source of WithValue's number becomes the held value (below), not the stored key alone.
public static IReadOnlyDictionary<string, object> WithValue(string condition, double value, IReadOnlyDictionary<string, object> sheet);
```

**How the value is worked out.** A condition's held value is the highest of:
- its own (`ValueOf` today: the stored key, or 1 for a condition with no value);
- the value each holder that implies it brings, worked out on `WithValue(holder, holderValue, sheet)` and clamped to `max`.

Values only go up and the graph has no cycles, so repeated passes stop after at most `ConditionEffects.Count` passes. An implied value at or
below 0 does not make the condition hold. A condition that is both listed and implied has `Listed = true`, and its `From` names the holders.

These are the rules of the game: the highest value counts and nothing adds up. Dread 3 brings Shaken 3. Shaken 1 stored, held with Dread 3, is
Shaken 3. When Dread drops to 1, Shaken is 1.

**Callers that change:**
- `SheetModel.cs:642` and `SheetRuleEngine.Conditions.cs:21,30` loop over `Holding` and pass `held.Value` instead of calling `ValueOf`.
  `ValueOf` stays for the stored number. Its callers are `AdjustCondition`, the stepper and `ConditionMax`.
- `LiveView` (`SheetConditions.cs:122`) writes **`Listed(system, live)`** to `conditions`, not `Of`. This matters because `TableEvents.Change`
  (`TableEvents.cs:288-313`) diffs `before` and `after` on that raw list. If the live list held implied names:
  - a handler that clears an implied Prone would log "removed" and call `RemoveStatus` on something never stored;
  - an implied name could be copied into `own`.

  So readers expand on read (`Of`, `Holding`), and the raw list stays the listed record.
- `ConditionLines.Holding` (Web) reads `SheetConditions.Holding`. `ConditionLine` gains `From` (`IReadOnlyList<string>`) and takes its value from
  `HeldCondition.Value`.
- `TableCard.razor` shows an implied line as a pill with no remove button. Its label is `"{0}, from {1}"` (new string
  `Combat.Condition.ImpliedBy` in en, ru and he; ru and he need native review), and the pill keeps `aria-label` text with the same words.
  `OnlyOnSheet` needs no change: it is the stored list.
- `SheetEvents.ScriptView` already reads `model` conditions (`SheetModel._conditions = Of`), so scripts see implied conditions. Do not change it.

**Not expanded (deliberately):** `TableRules.View`'s `VitalsView.conditions` stays the table's statuses. The vitals rules read only their own
conditions (down, dead, stable, concentration), which are never implied. Revisit this with dying and wounded.

### What removing does
- Removing acts only on the listed record: a status removed, a list entry dropped, or a value set to 0. Every condition that source brought ends
  on the next read, unless it is listed itself or another holder still brings it.
- An implied-only condition cannot be removed while its source holds. The card offers no button for it. A `condition` intent or a remove
  command that names it changes nothing and logs nothing, because the diff runs on the listed list.
- Undo needs nothing new, because nothing implied is stored.
- The valued stepper (`TableCommandPanel`) keeps showing and moving the **stored** value. The held line shows the held value with its source.
  So "Shaken 0 stored, held at 3 from Dread" reads correctly: the stepper shows 0, and the line shows "Shaken 3, from Dread".

### The turn-end drop moves into Core (decision 2)
Put a pure rule beside `Holding`, in `SheetConditions.Holding.cs`:

```csharp
public sealed record ConditionDrop(string Condition, string Key, int From, int To);
/// Each valued condition whose decrease.on is moment (OrdinalIgnoreCase) and whose stored number is above 0, lowered by `by`, never below 0.
public static IReadOnlyList<ConditionDrop> Drops(SystemDefinition system, Func<string, int?> stored, string moment);
/// Drops over a sheet, written back as numbers (0 stays written as 0, not removed). Returns what changed.
public static IReadOnlyList<ConditionDrop> Drop(SystemDefinition system, Dictionary<string, object> sheet, string moment);
```

- **`TableRules.DecreaseConditions`** keeps its signature and messages. Its loop becomes `SheetConditions.Drops(system, k => pool.Pools.TryGetValue(k,
  out var v) ? v : null, moment)`, followed by `pool.Set` and the log lines. The rule lives in Core now, and Persistence only applies it to the pools.
- **`SheetEvents.Raise` does not drop.** At the table `RuntimeCommands.cs:563-566` raises `turnEnd` when a handler exists and then calls
  `DecreaseConditionsUndoably`. A drop inside Raise would therefore drop twice on every system with a `turnEnd` handler, the PF2e pack included.
- **`CurrentEngineHost.Raise`** (`tools/Codex.SystemCheck/SpecRunner.cs:409`) does what the table does: it raises the event, then calls
  `SheetConditions.Drop(definition, after, type)` when `type` is `turnEnd`, `turnStart`, a rest or a declared moment (`SystemMoments.IsMoment`).
  This turns `degrees.frightened-drops-by-one-at-the-end-of-the-turn` and `degrees.frightened-ends-at-zero` to pass.
- The sheet page's moment button (`ActorSheet.razor:1543`, off the table) calls `Drop` on `editSheet` after its Raise, so the sheet page agrees
  with the table.
- A valued condition held only by implication has stored 0, so it never drops. It follows its source down.

### Loader warning (decision 5)
A pack entry reached only through a sheet kind's feature source, with a modifier aimed at a target that only `sheetRules` reads, counts zero
times. Today that happens silently.

- **Where the warning goes.** It cannot be in `SystemDefinitionLoader`, because system.yaml never sees pack entries. It goes in
  `YamlContentPackLoader` when an entry is registered against `definitionOf(systemId)`, through `Problem(...)`. `PluginLoader.cs:406` then shows it
  as a load problem.
- **The check is pure Core**, in `SheetConditions.cs` beside `KindTargets`:

  ```csharp
  /// On a hybrid system: for an entry whose Type is the type of a list some sheet kind names in `features`, and is not a type sheetRules collects, each
  /// modifier whose target SheetRuleEngine.KindOf knows and KindTargets does not. Message, invented example:
  /// "Trinket 'Lucky pebble': modifiers[0] aims at 'classDc', which only the sheet rules read; a sheet kind's feature never reaches it, so it counts for nothing."
  public static IEnumerable<string> UnreachedKindModifiers(SystemDefinition system, ContentEntry entry);
  ```
- **Which types sheetRules collects:** the `targetEntityType` of the fields its `features` name, and the entry types of its `list` sources. Read
  them from the same definition `CollectFeatures` reads. Do not keep a second list.

### Files (a)
`SheetKinds.cs`; `SystemDefinitionLoader.cs`, plus a converter file; `SheetConditions.cs` and the new `SheetConditions.Holding.cs`; `SheetModel.cs`; `SheetRuleEngine.Conditions.cs`; `TableRules.cs`; `TableEvents.cs`: read only, to confirm the diff is on `Listed`; no change expected; `ConditionLines.cs`; `TableCard.razor`; `ActorSheet.razor` (the moment drop); `YamlContentPackLoader.cs`; `SpecRunner.cs`; `SYSTEM_YAML_REFERENCE.md` and the regenerated schema; the resx files (en, ru, he).

## (b) Conditions on a printed creature's numbers

### What is there today (checked on main)
- `ActorSheetSync.SeedFromBlueprint` copies a creature entry's properties onto the actor's sheet and sets `attrMethod: printed`
  (`SheetRuleEngine.PrintedMethod`). That marker is how a sheet is known to be a printed creature.
- `CombatConsole.EvaluationOf` and `TableCard` already evaluate a creature's live view with `EvaluateLive`. Condition modifiers reach the numbers
  the engine **works out** for it: saves and skills from its scores, and derived values. They do not reach the numbers it **prints**:
  - AC is in manual mode, and `TableCard.Stats()` (line 292) shows the stored `armorClass`;
  - printed saves, Perception, Strikes and DCs are not read at all.
- `MonsterCard.razor` renders the Library entry (`Blueprint`), not the sheet. It stays the printed reference and is not changed.
- srd5e prints saves, skills, attacks and DCs as prose ("DEX +3", "+9 to hit"). **That prose is never parsed.** Only structured numbers can be matched.

### Data: the system says which printed numbers answer to which target

```yaml
sheetRules:
  printed:                                     # read only on a sheet whose attrMethod is printed
    - { from: defense,   target: armorClass }  # the armor class key
    - { from: alertness, target: perception }  # a derived value
    - { from: hardiness, target: save.con }    # the narrowest roll target; reads every target of SaveSite(con)
    - { from: powerDc,   target: classDc }
    - { list: attacks, bonus: bonus, range: reach, name: name }   # each row is a Strike: attack site, melee or ranged from row[range]
```

```csharp
// SheetRules.cs
public List<PrintedNumberDefinition> Printed { get; set; } = new();
public sealed class PrintedNumberDefinition { public string? From, Target, List, Bonus, Range, Name; }
```

**Loader checks.** Each of these is an error:
- an entry has both `from` and `list`, or neither;
- a `target` that is not:
  - the armor class key,
  - a derived value,
  - a Number field,
  - `save.<attribute or save key>`, or
  - `check.skill.<key>`;
- a target that `KindTargets` claims. The hybrid rule says a printed number belongs to the sheet-rules side, so on a hybrid system a kind
  value is not a printed target; that case is deferred.
- a `list` row without `bonus`.

### How a printed number reads conditions
This is a new partial file, `SheetRuleEngine.Printed.cs`. It is a separate pure method, **not part of `Evaluate`**, so `SheetEvaluation`, `Recompute`
and both goldens are unchanged by construction.

```csharp
public sealed record PrintedNumber(string Key, string Label, string Target, int Printed, int Total, IReadOnlyList<RollBonus> Bonuses, IReadOnlyList<RollDice> Dice);
/// Empty unless the sheet's attrMethod is printed and sheetRules.printed is set. Never written to the sheet.
public IReadOnlyList<PrintedNumber> Printed(IReadOnlyDictionary<string, object> live);
```

- **Base.** The base is the sheet number at `from`, or for a Strike, `row[bonus]`. A missing or non-numeric value gives no entry, and the host
  falls back to what it shows today.
- **Which modifiers count.** Only **condition** modifiers (`ConditionModifiers(live)`), never features or score working. A printed number already
  includes the creature's own abilities. The modifier's target must be one this number reads:

  | Number | Targets it reads |
  |---|---|
  | armor class, derived, Number field | the key alone |
  | `save.<k>` | `SaveSite(k).Targets` |
  | `check.skill.<k>` | `SkillSite(k).Targets` |
  | Strike row | `attack`, `attack.weapon`, `attack.melee` or `attack.ranged` (from `row[range]`, melee when absent), `attack.weapon.melee` or `attack.weapon.ranged` |

  Reuse `SiteBonus(features, new RollSite(kind, targets), derived, null)` with the condition features alone. Stacking (`ModifierStacking`,
  `system.ModifierTypes`) then works as it does everywhere else.
- **Dice and tick boxes.** Dice amounts (a +1d4) go to `Dice`. They are shown and rolled, never added to `Total`. Opt-ins are not offered on
  printed numbers (deferred).
- **Hosts use the printed number instead of the worked-out site, never on top of it.** Otherwise the same frightened counts twice:
  - `TableCard.Stats()` shows `Total` for armor class and for any derived key that has a printed entry, with the printed number in the title
    ("printed 18; Shaken −2"). It also lists printed saves and Strikes for whoever may see them in full.
  - `CombatConsole` computes the list once per card, beside `EvaluationOf`, and passes it in as a new `TableCard` parameter `Printed`.
  - A host that later rolls a printed Strike or save (P6, hazards) rolls `Total` plus `Dice`, and does not call `SiteBonus` again.
- **What it means for PF2e (P7).** The pack's creature entries must carry these numbers as structured properties: AC, saves, Perception,
  DCs, and Strikes as rows with a bonus. That is a data requirement on P7, recorded here.

**Manual mode on a character.** `TableCard.Stats()` also drops condition AC modifiers for a character in manual AC mode. (b) does not change
that: it fixes printed creatures only. See the open questions.

### Files (b)
`SheetRules.cs`; `SystemDefinitionLoader.cs`: one more validate method, the same file as (a), so the same writer, in order; the new `SheetRuleEngine.Printed.cs`; `TableCard.razor`; `CombatConsole.razor`; the resx file for the title text; `SYSTEM_YAML_REFERENCE.md` and the schema.

Before editing the table hosts, list the files H4a and H4b touch (plan, section 4).

## Tests

- **(a) load:** a name item and a map item; an unknown name; a condition implying itself; a cycle of three, with the message naming the loop;
  a `value` on an unvalued condition; a `value` on exhaustion; a bad formula; an entry with `implies` and no `modifiers` loads.
- **(a) holding:** Senseless brings Dazzled and Rooted, and `Of` lists all three while `LiveView`'s raw `conditions` holds Senseless alone;
  Dread 3 brings Shaken 3 and a check is −3; Shaken stored at 1 with Dread 3 is held at 3, counted once; Dread at 0 ends Shaken; a two-step
  chain; on a sheet-kind system, `SheetModel` sees an implied condition in a `when`.
- **(a) at the table** (runtime tests): removing the source status ends the implied ones, and no status was ever written for them; a handler
  that clears an implied-only condition logs nothing; the card line has `From` and no remove button (a `ConditionLines` test; there is no bUnit).
- **(a) drop:** `Drops` on a dictionary; the existing C4 tests of `TableRules.DecreaseConditions` pass unchanged; the two `degrees.*` turn-end
  expectations pass, and `progress.txt` gains their ids.
- **(a) warning:** a hybrid fixture entry aimed at `classDc` warns once; one aimed at `senseDc` does not; a non-hybrid system never warns.
- **(b):** a printed creature with defense 18 and Shaken 2 (on `armorClass`, status) gives `Total` 16, and `Recompute` leaves `armorClass` at 18;
  a save printed at +7 with frightened 1 gives +6; a ranged Strike reads `attack.ranged`, not `attack.melee`; dice go to `Dice`; a feature
  modifier on the creature does not count; a sheet whose method is not `printed` gives an empty list; each load check; a runtime test that a
  creature card's AC drops while it is Shaken.
- **Goldens:** srd5e and fitd `expected.txt` stay **byte-identical**; run both and do not regenerate. No golden sheet holds a condition, and
  printed numbers are outside `SheetEvaluation`, so neither (a), (b) nor the srd5e data step below can move them.

## srd5e data (one small step after (a))
- Add `conditionEffects` entries that carry only `implies`, from the SRD 5.2.1 text.
- **Read the SRD 5.2.1 conditions text for each one before writing it.** Do not write from memory (the earlier exhaustion numbers written from
  memory were wrong).
- The expected entries are Incapacitated brought by Unconscious, Paralyzed, Petrified and Stunned, and Prone by Unconscious. For Prone, see
  question 1 first.
- srd5e gets no `printed:`. Its creature numbers other than AC are prose, and no srd5e condition targets AC.

## Deferred
Dying, wounded and doomed stay their own design. They come after (a), on valued conditions plus the pack's handlers; (c), the level badge for every valued condition on the initiative row, is a separate UI step; Implied conditions in `VitalsView`; Tick boxes on printed numbers; Kind values as printed targets on a hybrid; Feature modifiers on printed creatures; Parsing prose stat blocks; A roll button for printed Strikes (P6); Log lines for implied conditions starting and ending.

## Open questions
1. **A condition that stays after its source ends.** Some rules leave a creature prone after it wakes, and `implies` would stand it up.
   - Option A: an implied item with `stays: true`, which writes the condition as its own status at the moment the source ends. Every
     `RemoveStatus` path and expiry would need a hook.
   - Option B: srd5e Unconscious implies Incapacitated only, and the down rule adds Prone as a status.

   B is recommended now; A goes with dying.
2. Should manual AC on a character take condition modifiers (`TableCard.Stats()` line 292)? This is the same gap as (b), for characters.
   Recommended: yes, in the same `Stats()` change. It would show the stored value plus the condition modifiers, never write them, and leave
   goldens identical.
3. Homebrew entries skip `YamlContentPackLoader`. Should the decision 5 warning also run where homebrew is saved?

## Decisions (2026-11-16)

1. Prone after Unconscious: `implies` entries take an optional `stays: true`. When the implying condition is removed, a `stays` implied
   condition is written to the stored list (once, as a real condition). srd5e Unconscious implies Incapacitated and Prone (`stays: true`),
   which matches the SRD 5.2.1 Unconscious text ("Inert": both conditions; Prone remains when it ends). The removal path in
   `TableEvents.Change` logs the stay as its own line.
2. Manual AC on a character takes condition modifiers too (`TableCard.Stats`).
3. Homebrew saves run the decision 5 warning as well.
