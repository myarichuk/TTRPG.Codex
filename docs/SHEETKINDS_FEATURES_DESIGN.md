# Feature sources on sheet kinds: design

Written 2026-11-13 on branch `feat/sheetkinds-feature-sources`. Nothing in this doc is built yet. It plans how a `sheetKinds` system
(Forged in the Dark, `content/systems/fitd-srd`) gets the feature engine that `sheetRules` systems already have: counters, refills at
moments, row and key choices, the picker, and table sync. The feature engine is described in
[FEATURE_GRANTS_DESIGN.md](FEATURE_GRANTS_DESIGN.md) (sections 6 to 8) and [SYSTEM_YAML_REFERENCE.md](SYSTEM_YAML_REFERENCE.md).

Ground rules: no Blades names in C#. Systems declare their own moments, and Blades has no rests. Spending stays per system, and the
engine does not change for it (see 2.5).

## Decision in brief

- **Seam.** A sheet kind gets its own `features:` list. It uses the same `FeatureSource` type as `sheetRules.features`, limited to `list`
  sources. The engine's feature collection, counters, spenders and row choices are lifted so both paths call them. **Modifiers** from a
  kind's features go into `SheetModel`'s own modifier pipeline, never through `sheetRules`' `KindOf`.
- **Moments.** A top-level `moments:` list in `system.yaml` declares the system's own moments (`downtime`, `loadChosen`). One resolver
  treats rests, `initiative`, `dawn` and declared moments the same way. `DawnCommand` becomes a general `MomentCommand`.
- **Pools.** A feature's limited uses are `uses_*` counters, as in srd5e, so table sync works with no change. Armor boxes stay sheet-kind
  tracks. A feature changes a track's size (Mule's load, the special armor box). It does not spend from it. The system resets tracks at a
  moment with its own `events:` handler (`set` intents).
- **Rolls.** "+1d" bonuses that roll inputs can decide (Iron will: resistance with Resolve) are modifiers with `when: input.*` now.
  Bonuses that depend on the story (Ambush, Savage) stay text until opt-in roll bonuses exist. srd5e has the same gap for `on` bonuses.

## 1. What is sheetRules-only today, and the seam

### 1.1 Where the engine assumes `sheetRules`

| Place | What it does | Why a sheet kind gets nothing |
|---|---|---|
| `SheetRuleEngine.Evaluate` (`SheetRuleEngine.cs`) | Runs `EvaluateModel` for kinds, then `if (Rules == null) return result;` | `EvaluateFeatures` sits after that return, so it never runs for fitd |
| `SheetRuleEngine.CollectFeatures` (`SheetRuleEngine.Features.cs`) | `if (Rules is not { Features.Count: > 0 })` returns nothing; list sources look in `rules.Lists`; field sources use `ClassViews` | A kind's lists are `SheetKindDefinition.Lists`, which it never reads |
| `SheetRuleEngine.RawList` + `ParsePick` | Read picks as strings (`id\|choice:n`) | `SheetModel.PickList` also takes `{ id, used, ... }` maps as picks; `RawList` would turn a map into its `ToString()` |
| `SheetRuleEngine.EvaluateFeatures`, the modifiers loop | `KindOf(target)` knows only armor class, hit points, `Rules.Derived` and Number actor fields | A kind's values (`LoadMax`), keyed entries (`actions.hunt`) and groups (`@pools`) are all `Unknown`, so every one would be warned about |
| `EvaluateFeatures`, the spend loop | A pool is a counter, a `system.Resources` key or `Rules!.SlotPools` | Null access with no `Rules`; a kind's tracks are not pools here |
| `SheetRuleEngine.WorkedOutDerived` | `Rules!.Derived` | Null access |
| `SheetRuleEngine.ApplyGranted`, `PruneChoices` | Return when `Rules == null` | No grants are written and no stale `choice__` keys are pruned |
| `SheetRuleEngine.EvaluateRanks` (`SheetRuleEngine.Ranks.cs`) | The only source of key `PendingChoice`s, through `RankTargetOf` | Needs `Rules.Skills`; a kind's keyed list (`actions`) is not a ranked list |
| `FeatureRecovery.Moments` / `IsMoment` | Hard-coded `initiative`, `dawn`, `manual` | No place for `downtime` |
| `SystemDefinitionLoader.ChangeMoments`, `CheckRowChoose` | `changesOn` must be a `vitals.rests` name or initiative/dawn | fitd has no `vitals` |
| `FeatureGrantCheck.SystemDefinitionMoments`, `FeatureGrantVocabulary.Moments`, `FeatureGrantGuide` text | The same hard-coded list, three more times | |
| `SheetLayout.BuiltinWidgets["features"]` | Says it reads `"sheetRules.features"` | The layout check may refuse `{ widget: features }` on fitd |
| `ActorSheet.razor`: `Shows("features")` and `case "features"` | Gated on `Rules is { Features.Count: > 0 }` | Never shown |
| `FeaturesPanel.razor` | `[EditorRequired] SheetRules Rules` for its labels | Cannot be given one |
| `CombatConsole.razor`: `Rests`, `DawnMatters`, `DawnAsync` | Party buttons for `vitals.rests` and dawn | No buttons for declared moments |

These already work for any system and need no change:
- `SheetRuleEngine.ApplyCounters` and `ApplyChoiceMoments` (they read the evaluation).
- `EvaluateRowChoices` (`SheetRuleEngine.RowChoices.cs`).
- `ActorSheetSync.ApplySheetToState`, which mirrors every `uses_*` key.
- `SheetCounters.Of` / `MergeByDelta`, which merge `uses_*` keys.
- `TableRules.RefillCounters`, which reads `_Rest` from the sheet.
- `TableCard`, which `CombatConsole` gives `engine.Evaluate(sheet)` for any system.

### 1.2 Options

| Option | What it means | Verdict |
|---|---|---|
| A. Let fitd declare `sheetRules: { features: [...] }` | Reuse as is | **No.** A non-null `sheetRules` switches on every other stage. List sources point at `sheetRules.lists`, not at a kind's lists. Two kinds (scoundrel, crew) need different sources. Modifier targets still go through `KindOf` |
| B. A `features:` block on `SheetKindDefinition`, with the collection lifted so both paths call it | One engine for counters, spends, choices and moments. Each path keeps its own modifier pipeline | **Yes** |
| C. Copy the feature code into `SheetModel` | | No: two engines would drift |

**Recommended: B.** It is the smallest change that keeps one engine. `CollectFeatures` gains kind sources. `EvaluateFeatures` loses its
`Rules!` assumptions and also runs at the end of the kind path. Modifiers are the only part that differs by host:
- `sheetRules` features go to `KindOf` / `WithModifiers`, as today.
- Sheet-kind features go to `SheetModel.Modifiers()`, next to `modifiersFrom` and `conditionEffects`. That pipeline already stacks by
  `modifierTypes`, and its `when` can read roll inputs.

### 1.3 The lift, concretely

1. **`ActiveFeature` gains `bool OnKind`.** It is true for features that come from a kind's source.
2. **A static collector, `SheetRuleEngine.KindFeatures(system, resolver, sheet, collected)`.**
   - It reads the sheet's kind (`SheetLayout.KindOf`).
   - For each `{ list }` source, it reads the pick ids from that kind's list. A pick is a string or a map's `id`, as in
     `SheetModel.PickList`.
   - It resolves each id by the list's `type` and calls the existing `AddFeatures` with `view = sheet`.
   - It reads the raw sheet only (ids and `choice__` keys), never a model value, so `SheetModel` can call it without recursion.
   - The instance `CollectFeatures` appends its result after the `sheetRules` sources.
3. **`Evaluate`:**
   - `if (Rules == null) { if (kind has features) EvaluateFeatures(sheet, result); return result; }`.
   - `sheetRules` systems keep their order. Features still run after derived values.
4. **`EvaluateFeatures`:**
   - The modifiers loop takes only `!OnKind` features.
   - The counters, spenders and row choices take all features.
   - `Amount` already works out formulas through `SheetModel.Formula`, with `DerivedNumbers(result)`. For a kind, `EvaluateModel` has put
     the model's numbers in `result.Derived`, so `uses.max: "tier + 1"` works.
   - `WorkedOutDerived` and the spend loop become null-safe (`Rules?.`).
5. **`SheetModel.Modifiers()`** adds every `OnKind` feature's `modifiers`. The source is the feature's name, there is no item scope, and
   `when` is read as a formula.
6. **The loader refuses a kind list that is both a `features` source and has `modifiersFrom`.** Its modifiers would count twice. fitd has
   no `modifiersFrom` today, so nothing breaks.

## 2. YAML

### 2.1 A sheet kind's feature sources

```yaml
sheetKinds:
  - kind: scoundrel
    features:
      - { list: abilities }        # each picked special ability is one feature; its own properties carry the grants
    lists:
      - { key: abilities, label: Special abilities, type: special-ability }
  - kind: crew
    features:
      - { list: abilities }
      - { list: upgrades }
```

On a kind, `FeatureSource` takes `list` (a key in that kind's `lists`), `property`, `entries`, `choose` and `levelColumn`. The loader
refuses these on a kind:
- `field`: kinds have no reference fields.
- `byField`: the collector may not read model values.
- a `list` that is not one of the kind's lists.

All three can be added later, when a system needs them.

An ability entry carries the same keys as in srd5e (`modifiers`, `adds`, `uses`, `spends`, `options` + `choose`):

```yaml
# special-abilities/foresight.yaml
uses: { key: foresight, max: 2, recovers: [{ on: loadChosen }] }    # "two times per score"; load is chosen as each score starts
# special-abilities/mule.yaml
modifiers: [{ target: LoadMax, amount: 2 }]
# special-abilities/expertise.yaml
adds: [{ to: actions, choice: expertise, choose: 1 }]
```

### 2.2 Moments

A moment is a named time at which things refill or reset. Today there are three kinds, with a fourth added:

| Kind | Where it is declared | Fired by |
|---|---|---|
| Rest | `vitals.rests[].name` | The rest button (sheet, Table card, party panel); `RestCommand` |
| `initiative` | Built in | The initiative roll at the table |
| `dawn` | Built in | The GM's dawn button |
| **Declared (new)** | `moments:` in `system.yaml` | Its button (sheet, party panel), and optionally a value being set |

`manual` stays a refill word only. It is not a moment.

```yaml
# fitd-srd/system.yaml
moments:
  - { key: downtime,   label: Downtime }
  - { key: loadChosen, label: Load chosen, onSet: load }    # fires when the load input is set on the sheet
```

`MomentDefinition { key (required, unique), label, onSet }`:
- `key` may not repeat a rest's name, `initiative`, `dawn` or `manual`.
- `onSet` names an `input` value of a sheet kind.
- Labels translate through `system.<locale>.yaml` (`moments: { downtime: ... }`), as choice labels do.

**One resolver.** `SystemMoments.Of(system)` returns every moment with its kind: rests, then declared moments, then `initiative` and `dawn`.
It replaces the five hard-coded lists in 1.1:
- `FeatureRecovery.IsMoment(on)` becomes `SystemMoments.IsRest(system, on)` where callers mean "is a rest".
  - `FeatureCounter.RecoversOn` stays "the rests among the rules".
  - `UsesRests` takes the system.
- Loader checks (`CheckRowChoose`, `recovers.on`), `FeatureGrantCheck`, `FeatureGrantVocabulary.Moments` and the guide text all read the
  resolver.
- srd5e declares no `moments`, so it gets exactly today's list.

### 2.3 How moments fire

| Where | What happens |
|---|---|
| The sheet page, `moments` widget (new generic widget, 6.1) | `FireMoment(m)`:<br>- `RefillFeatureCounters(m)` (exists).<br>- `SheetEvents.Raise(system, resolver, editSheet, new SheetEvent(m, {}))` when `SheetEvents.Handles(system, m)`.<br>- `OfferChoicesAfter(m)` (exists).<br>- A flash status that says what came back.<br>`RestSlots` becomes the rest-specific case of the same method. |
| The sheet page, `onSet` | `OnRulesInputChanged`: when the changed key is a moment's `onSet`, fire that moment after the value is written and before the recompute. The `moments` widget still offers the button, so choosing the same load again is one click. |
| The live table | **`MomentCommand(ActorId, Moment)`** replaces `DawnCommand`. It runs `TableRules.RefillCounters(..., moment)`, then `TableEvents.Raise(..., new SheetEvent(moment, ...))` when `TableEvents.Handles(runtime, moment)`. It is undoable like a rest. The log uses `Rest.Dawn` / `Rest.DawnWith` for dawn, and `Rest.Moment` / `Rest.MomentWith` for declared moments. Commands are not stored by type name, so nothing needs a migration. |
| Party panel (`CombatConsole`) | `DawnMatters` becomes `Matters(moment)`: some sheet at the table has a `uses_*` rule for it, a `changes_on__*` key names it, or an `events:` handler for it touches the sheet's kind (defined in 2.4). Rests stay as they are. Each declared moment that matters gets a button that sends `MomentCommand` only to the actors whose sheet it touches, by the same test. |

This is a generalisation, not a second system:
- A rest is a moment with `vitals` rules attached. `RestCommand` already refills counters and raises a `rest` event.
- Dawn is a moment with no rules. Initiative is a moment the roll fires.
- Declared moments add a name and a label, nothing else.
- Counters refill by the same `recovers: [{ on, amount, when }]` and the same `_Rest` encoding, which the table reads without evaluating.

### 2.4 Which sheets a moment touches

`events:` handlers are system-wide, but a sheet has only its own kind's tracks. Today `SheetIntents.Applier.Set` writes the key even when
`Track(key)` is null, so `set { track: special_armor }` would put a stray `special_armor: 0` on a crew sheet. Two rules fix this:

- **A handler line touches a kind** when one of its intents names, as a literal `track:`, a track that the kind declares. The `moments`
  widget, `Matters(moment)` and the party buttons all use this test, together with "a `uses_*` rule or a `changes_on__*` key on this
  sheet names the moment".
- **On a system with sheet kinds, `SheetIntents` leaves an `adjust`, `set`, `mark` or `clear` intent unapplied when its track is not
  one of the sheet's kind's tracks.** The intent goes to `Left`, like an intent for another actor, and nothing is written. This is a
  small guard in `Applier.Apply`, with a test. A handler line that names a crew track and a scoundrel track then changes only what each
  sheet has.

### 2.5 Pools: counters and boxes ("spent uses work per system")

The engine has two pool shapes. Neither needs a direction flag:
- **Counters** hold what is left: `uses_*`, slot pools and resources. A spend subtracts. A refill goes up toward the maximum. `ActionCost`,
  `FeatureSpenders`, `AdjustResourceCommand` and `MergeByDelta` all assume this.
- **Box tracks** of a sheet kind hold what is marked: stress, the armor boxes, heat. `armor`, `heavy_armor` and `special_armor` have no
  `start`, so `ApplyTracks` leaves them at 0, which means unmarked. Marking raises the value. "Restored" means set back to 0.

Each system decides with data which shape a rule uses:
- **Limited uses per score or per downtime** (Foresight, Analyst, Calculating) are counters: `uses` + `recovers`. They get the Features
  panel and Table card buttons, refills at moments and table sync, as srd5e's counters do.
- **Armor** stays the kind's box tracks. The player marks a box on the track widget, as on a paper sheet. A feature does not spend a box.
  It changes the box's size (special armor, 3.3). The system resets boxes at a moment with its own event handler:

```yaml
events:
  loadChosen:   # "All of your armor is restored when you choose your load for the next score."
    - { intents: [{ set: { track: armor, to: 0 } }, { set: { track: heavy_armor, to: 0 } }, { set: { track: special_armor, to: 0 } }] }
  downtime:     # "Your special armor is restored at the beginning of downtime."
    - { intents: [{ set: { track: special_armor, to: 0 } }] }
```

**Rejected: `spends: { key: special_armor }`.** `ActionCost.Left` reads 0 as "nothing left", so a fresh sheet's button would be disabled,
and a refill to the maximum would mark the box. Making that work needs a "this pool counts up" flag in `ActionCost`, `FeatureSpenders`,
`AdjustResourceCommand` and the delta merge, for seven abilities that only say "tick the box". The box already exists, and so does the
intent language that resets it.

## 3. What grant targets mean on a sheet-kind sheet

| Grant | On a kind sheet | Engine work |
|---|---|---|
| `modifiers` | `SheetModel` modifiers. The target is:<br>- a value that declares `modifiers:` for it (`LoadMax`),<br>- a keyed entry (`actions.hunt`, read through `derive: value`),<br>- or a group (`'@pools'`, `'@resistance'`).<br>They stack by `modifierTypes`. `when` is a formula over the sheet and, during a roll, over `input.*` | Section 1.3 step 5 only |
| `uses` | A `uses_*` counter, exactly as in srd5e. `max` is a number or a formula over the kind's values. `recovers.on` is any moment from the resolver | None beyond the lift |
| `spends` | A Use button that spends a feature counter (`uses_*`) or a counter-shaped pool (a resource). Never a box track (2.5). The loader warns when `spends.key` names a kind track | None beyond the lift |
| `options` + `choose` | A row choice, as in srd5e | None (`EvaluateRowChoices` is generic) |
| `adds` with `choice`, `to` a keyed list of the kind | A key choice: the player picks keys of that list (Expertise: one action). It is stored as `choice__{entryId}__{id}`, shown in the picker and listed on the feature as "Expertise: Skirmish". It has no effect on numbers yet | New `EvaluateKeyedChoices`: a `PendingChoice` with `Target` = the list key, `Rank` null, `Options` = the list's keys and labels, and the same warning keys (`ChoicePending`, `ChoiceStale`) |
| `adds` with `rank` / `raise` on a keyed list | Not read. The loader warns. This is the later hook for "+1 action rating" (Deadly), as the ranks design foresaw | Later |
| `adds` of entries to a pick list | Not read on a kind. The loader warns. No SRD ability needs it | Later |

### 3.1 Mule: derived values

The load limits are 3 / 5 / 6 by default and 5 / 7 / 8 with Mule, a flat +2 on each. `system.yaml` changes `LoadMax` to
`{ key: LoadMax, formula: "...", modifiers: LoadMax }`. The ability carries `modifiers: [{ target: LoadMax, amount: 2 }]`. The `Actor.Rule.Load`
check then reads the higher limit.

### 3.2 Expertise: a choice among the sheet's own keys

`adds: [{ to: actions, choice: expertise, choose: 1 }]` offers the twelve actions. The rule itself ("you suffer at most 1 stress when you
lead a group action with it") stays text. The `group` roll's stress line in `system.yaml` could read the pick later, through a formula
function over `choice__` keys. That is not part of this design.

### 3.3 Battleborn and special armor: a pool the ability gives

The SRD says: "If you don't have any special abilities that use special armor, then you can't use that armor box at all." So the seven
abilities give the box, and the player marks it:

```yaml
modifierTypes: { types: { boxes: highest } }                     # several abilities still give one box
values:
  - { key: specialArmorBoxes, label: Special armor boxes, formula: "0", modifiers: specialArmorBoxes }
tracks:
  - { key: special_armor, label: Special armor, max: "specialArmorBoxes", display: boxes }
```

Each special-armor ability carries `modifiers: [{ target: specialArmorBoxes, amount: 1, type: boxes }]`. What the box is used for stays text.
This hides the box on live sheets that have no such ability (see Migration, and open question 1).

### 3.4 "+1d" to some rolls

| Case | Now | Why |
|---|---|---|
| The roll's own inputs decide it (Iron will: resistance with Resolve) | **Model now.** `modifiers: [{ target: '@resistance', amount: 1, when: "input.attribute == 'resolve'" }]`, and the resistance pool becomes `"score(input.attribute) + modifiers('@resistance')"`. This uses its own target, so the harm condition's `-1` on `@pools` does not start touching resistance rolls | `SheetModel(..., input)` already runs `when` against roll inputs |
| The story decides it (Ambush: "attack from hiding"; Savage: "a frightened target"; Trust in me; Weaving the web; Scout; Bodyguard's resist) | **Text.** The player adds it through the `bonusDice` input ("Assist, push or bargain") | Nothing on the sheet can tell. A modifier with a made-up `when` would be wrong |

**Later, shared with srd5e:** opt-in roll bonuses. A modifier marked `optIn: true` with a short label shows as a checkbox in the roll
dialog. Ticking it sets `input.optIn.<id>`, and its `when` reads that. This is the sheet-kind form of srd5e's planned
`on: { action, tags }` bonuses (Archery, Sacred Weapon) and of feature dice shown beside an attack. Both gaps should share one design (see
Shared engine changes, S6).

## 4. Every fitd-srd ability

**M** = mechanical (a grant changes a number or adds a counter). **C** = counter only: the rule is text, but its limit is tracked.
**T** = text. Moments: `LC` = `loadChosen`, `DT` = `downtime`.

### 4.1 Special abilities (42)

| Ability | Class | Grant the importer writes | Stays text |
|---|---|---|---|
| A little something on the side | T | none | +2 stash at the end of downtime. It needs moment effects on a feature (later) |
| Alchemist | T | none | +1 result level when crafting; the starting formula |
| Ambush | T | none | Story-decided +1d |
| Analyst | C | `uses: { key: analyst, max: 2, recovers: [{ on: downtime }] }` | Where the ticks go (clocks) |
| Arcane fighter | T | none | Potency |
| Arcane mind | T | none | Awareness; story-decided +1d |
| Artificer | T | none | As Alchemist |
| Battleborn | M | `modifiers: [{ target: specialArmorBoxes, amount: 1, type: boxes }]` | What the box does |
| Bodyguard | T | none | +1d resist when protecting (story-decided); +1 effect |
| Calculating | C | `uses: { key: calculating, max: 1, recovers: [{ on: downtime }] }` | Who gets the activity |
| Connected | T | none | +1 result level in downtime |
| Daredevil | T | none | An optional trade the player makes per roll |
| Expertise | M | `adds: [{ to: actions, choice: expertise, choose: 1 }]` | The 1-stress cap on group actions |
| Focused | M | special armor box, as Battleborn | Use |
| Foresight | C | `uses: { key: foresight, max: 2, recovers: [{ on: loadChosen }] }` | Assist without stress |
| Fortitude | M | special armor box | Use |
| Infiltrator | T | none | Effect not reduced by tier |
| Iron will | M | `modifiers: [{ target: '@resistance', amount: 1, when: "input.attribute == 'resolve'" }]` | Immunity to terror |
| Leader | T | none | Cohort rules (cohorts are not sheets yet) |
| Like looking into a mirror | T | none | |
| Mastermind | M | special armor box | Use |
| Mesmerism | T | none | |
| Mule | M | `modifiers: [{ target: LoadMax, amount: 2 }]` | |
| Not to be trifled with | T | none | Push yourself: stress is marked on the track |
| Reflexes | T | none | |
| Ritual | T | none | |
| Rook's gambit | T | none | 2 stress, marked on the track |
| Saboteur | T | none | |
| Savage | T | none | Story-decided +1d |
| Scout | T | none | Story-decided +1 effect and +1d |
| Shadow | M | special armor box | Use |
| Sharpshooter | T | none | Push yourself |
| Strange methods | T | none | As Alchemist |
| Subterfuge | M | special armor box | Use |
| Supernatural ward | T | none | |
| The devil's footsteps | T | none | Push yourself |
| Tough as nails | T | none | Shifts harm penalties down a level. It needs a condition remap (later) |
| Trust in me | T | none | Story-decided +1d |
| Vengeful | T | none | An extra xp trigger. `sessionEnd` answers are counted, not named |
| Vigorous | T | none | No healing clock or healing roll in the system |
| Warded | M | special armor box | Use |
| Weaving the web | T | none | Story-decided +1d to Consort and engagement |

That is 10 mechanical (Mule, Iron will, Expertise and the 7 special-armor abilities), 3 counters (Analyst, Calculating, Foresight) and
29 text.

### 4.2 Crew special abilities (16), on the crew sheet

| Ability | Class | Grant | Note |
|---|---|---|---|
| Fiends | M | `modifiers: [{ target: turfCount, amount: "wanted" }]` | Needs the crew yaml in 4.4 |
| Accord | T | none | Reads campaign faction status, which a sheet formula cannot reach |
| Deadly, Forged in the fire, Anointed | T | none | They change each PC's sheet. Crew-to-PC effects are open question 2 |
| All hands, Death veil, High society, Just passing through, No traces, Pack rats, Patron, Predators, Second story, Synchronized, War dogs | T | none | Downtime, heat, rep, engagement and group-roll rules. High society, Just passing through and No traces need moment effects on a feature (later) |

### 4.3 Crew upgrades (18)

All stay text in this pass. Composed (+1 stress box), Hardened (+1 trauma box) and Mastery (rating cap 4) reach each PC, which is open
question 2. Vault (coin capacity 8 / 16) would need a crew coin maximum, which the crew sheet does not have. Adding one could clamp live
crews' coin, so it is left out.

### 4.4 Crew yaml for Fiends

```yaml
values:
  - { key: turf, input: true, default: 0 }
  - { key: turfCount, label: Turf counted, formula: "turf", modifiers: turfCount }
tracks:
  - { key: rep, label: Reputation, max: "max(12 - turfCount, 6)", display: boxes }   # "the minimum rep cost ... is always 6"
```

With turf at 6 or less, `max(12 - turfCount, 6)` equals today's `12 - turf`, so no live crew's number moves.

## 5. Importer, golden corpus, schema docs

**FitdSrdImporter** (`tools/Codex.ContentImport/FitdSrdImporter.cs`):
- Add a static, reviewed table `Grants`: entry id → the properties to set. It covers the 13 abilities with a grant in 4.1, plus Fiends.
  Write it out by hand, never with a regex over the text.
- After `BuildEntries`, call a new `ApplyGrants(entries)`. It copies each row into `entry.Properties`. Description and tags stay untouched.
- `Validate` throws when a table id matches no imported entry, or when it matches an entry of the wrong type. A renamed SRD heading then
  fails loudly.
- Keep the special-armor modifier in one helper (`SpecialArmorBox()`) so the 7 rows cannot drift apart.
- Regenerate the pack with the `fitd` command. Only these 14 files should change; review the diff.

**Tests that change:**
- `FitdSrdPackSmokeTests.EveryProperty_IsAFieldTheSystemDeclaresForItsType` will fail as soon as an ability carries `modifiers`. Exempt the
  grant keys through one shared constant, a new `FeatureGrantData.GrantKeys` (`modifiers`, `adds`, `uses`, `spends`, `options`, `choose`).
  Take it from the key list already passed to `CopyExtra`, and have `CopyExtra`'s call use it. Do not declare them as entity fields: the editor would show them as text boxes.
- Add `SpecialAbilities_CarryTheReviewedGrants`, which checks each table row against the loaded pack.

**Golden corpus:**
- The srd5e corpus (`tests/Codex.Tests/Golden`) is the gate for the shared lift. It must stay byte-identical. Run it without
  `CODEX_GOLDEN=update`; any diff is a regression in steps 1 to 6.
- Add `FitdGoldenCorpusTests` on the same harness, with a handful of scoundrel and crew sheets:
  - a plain scoundrel;
  - Mule at each load;
  - Battleborn with the box marked;
  - Foresight half spent;
  - Expertise picked and unpicked;
  - a crew with Fiends.
  Create its `expected.txt` with `CODEX_GOLDEN=sheets`, then `update`, and review it.

**Schema docs:** `MomentDefinition`, `SystemDefinition.Moments` and `SheetKindDefinition.Features` need XML docs. Then run
`tools/Codex.SystemCheck --schema docs` and commit `docs/SYSTEM_YAML_REFERENCE.md` and `docs/schema/system.schema.json`. Also update:
- the `FeatureSource` doc text: "on a sheet kind: `list` only";
- the `RowChoose.ChangesOn` and `recovers.on` docs: "a moment: a rest, a declared moment, initiative or dawn".

## 6. UI

### 6.1 Where things appear on a scoundrel sheet

| Section (fitd layout) | Widget | Shows |
|---|---|---|
| Abilities | `list` (abilities), then **`features`** (built-in, now allowed on kinds) | - `ChoicesBlock` (Expertise: "choose 1 action").<br>- `FeaturesPanel`: Foresight 2 of 2 with Use / Regain, Calculating, Analyst.<br>- The grant lines ("Load limit +2 · Mule", "Special armor boxes 1 · Battleborn").<br>A kind sheet shows no rest buttons in the panel; moments have their own widget |
| Condition | the tracks, then **`moments`** (new generic widget) | One button per moment that touches this sheet: "Downtime", "Load chosen" |
| Load | `values` (load) | Setting the load fires `loadChosen` |

Changes:
- `SheetLayout.GenericWidgets` gains `moments`.
- `BuiltinWidgets["features"]` reads `"features"`: either `sheetRules.features` or a kind's `features`.
- `ActorSheet`'s `Shows("features")` and `case "features"` check "the sheet has feature sources" instead of `Rules`.
- `FeaturesPanel.Rules` becomes optional. `TargetLabel` falls back to the kind's value labels. For kind features, `Lines` reads
  `evaluation.Model.Modifiers` entries whose source is an active feature.
- The `ChoicesBlock` shows when the kind has features.

**The `moments` widget** shows a moment only when it matters on this sheet:
- a `uses_*` rule names it,
- an `events:` handler for it touches this kind (2.4),
- or a `changes_on__*` key names it.

Each button gets a hidden line naming what it brings back ("Restores Armor, Heavy armor, Special armor; refills Foresight"). The widget
reads this from the counters' rules and from the literal `track:` keys of the handler's `set` / `adjust` intents.

**At the table:**
- The `TableCard` already lists `uses_*` counters with Use / Regain.
- The party panel ("Party rest", renamed in copy only to "Rests and moments" where the system has declared moments) gets the moment buttons
  from 2.3.
- `RestChoicesNotice` is reused for any moment.

### 6.2 Accessibility

- Moment buttons are real buttons whose visible text is the moment's name. `aria-describedby` points at the "Restores ..." line.
- After a moment fires, one polite status says what came back ("Load chosen: Armor, Heavy armor restored; Foresight refilled to 2"). It
  uses the existing flash with `role="status"`.
- The key choice reuses `ChoicePicker` unchanged: a fieldset and legend, checkboxes or radios, and "1 of 1 chosen" in a polite live region.
- A box that an ability gives says so in text, not only in colour: "Special armor (Battleborn)". When no ability gives it, the track shows
  "No ability gives this box" instead of an empty row.
- Everything works by keyboard, in tab order. Check RTL for Hebrew, especially the box rows and the moment buttons.

### 6.3 Strings (en, ru, he)

| Key | en |
|---|---|
| `Actor.Moments.Title` (Strings.Actor) | Moments |
| `Actor.Moments.Restores` | Restores {0} |
| `Actor.Moments.Refills` | Refills {0} |
| `Actor.Moments.Done` | {0}: {1} |
| `Actor.Moments.Nothing` | {0}: nothing to restore. |
| `Actor.Tracks.NoSource` | No ability gives this box. |
| `Actor.Tracks.GivenBy` | {0} ({1}) |
| `Combat.PartyMoments` (Strings.Combat) | Rests and moments |
| `Combat.MomentButton` | {0} for everyone |
| `Rest.Moment` (Strings.Event) | {0}: {1}. |
| `Rest.MomentWith` | {0}: {1}: |
| `Rest.TrackRestored` | {0} restored |

System strings: fitd-srd has no `system.<locale>.yaml` yet, so moment, value and track labels show in English. Adding
`system.ru.yaml` / `system.he.yaml` for fitd is a separate task that needs a native review. The resx strings are Haiku work. Each new key
goes in the existing `Strings.Actor`, `Strings.Combat` and `Strings.Event` files, so no new resx prefix is needed.

## 7. Tests, risks, migration, build order

### 7.1 Tests

| Test | File |
|---|---|
| A kind with `features: [{ list: abilities }]` collects one feature per pick, for string picks and `{ id }` map picks | `SheetKindFeaturesTests` (new) |
| `uses` on a kind writes `uses_foresight` / `_Max` / `_Rest` / `_Label`. The counter starts full and drops when the ability is removed | same |
| Mule: `LoadMax` is 5 / 7 / 8, and the Load rule reads it | same |
| Iron will: the resistance pool is +1 with `attribute: resolve`, and +0 with prowess | same, through `SheetRolls` |
| Special armor: max 0 without an ability; 1 with one; 1 with two (`boxes: highest`) | same |
| Expertise: a pending choice of the 12 actions; picking writes `choice__expertise__expertise`; a removed action is `ChoiceStale` | same |
| A kind feature's modifiers never reach `KindOf` (no `FeatureError` warning) | same |
| The loader refuses `field` / `byField` / an unknown list on a kind source, and `modifiersFrom` together with a feature source on one list | `SystemDefinitionLoaderTests` |
| Moments: the resolver lists rests, declared, initiative and dawn. A declared key that clashes is refused. `changesOn: downtime` is accepted on fitd and refused on srd5e | `SystemMomentsTests` (new) |
| `MomentCommand("downtime")` refills `uses_*` rules naming it, runs the `downtime` handler (special armor set to 0), logs, and undoes | `TableEventsTests` / runtime command tests |
| `MomentCommand("dawn")` does exactly what `DawnCommand` did (port the existing dawn tests) | existing dawn tests |
| On the sheet, setting `load` fires `loadChosen` once and clears all three armor boxes | bUnit `ActorSheet` test |
| srd5e golden corpus unchanged; fitd golden as in 5 | Golden |
| Pack: the reviewed grants are on the 14 entries; the smoke test exempts the grant keys | `FitdSrdPackSmokeTests` |

### 7.2 Risks

- **The lift moves srd5e numbers by mistake.** The srd5e golden must stay byte-identical. Steps 3 and 4 touch `EvaluateFeatures`; review
  them with Opus.
- **Recursion.** `SheetModel.Modifiers()` calls the collector. The collector must read only the raw sheet. A unit test builds a sheet
  whose ability has a `uses.max` formula reading a model value, and checks that the evaluation does not loop.
- **Cost.** `SheetModel` is built often (rolls, intents, events, views). Cache the collector's result per `SheetModel` instance. It is
  already lazy (`_modifiers`).
- **A moment fired on the sheet during a live table can be lost.** `MergeByDelta` keeps the table's value for a key the editor left
  equal to its baseline. Example: the sheet opens at Foresight 2 of 2, the table spends one (live 1), and the sheet fires `loadChosen`
  (back to 2, equal to the baseline). The merge keeps 1, and an armor reset to 0 is lost the same way. srd5e rests fired from the sheet
  have the same gap. Fix (S10): while the actor is at a live table, the sheet sends `MomentCommand` / `RestCommand` and does not apply
  the moment locally.
- **Sheet edits to tracks during a live table are last-writer-wins.** `SheetCounters.Of` does not list sheet-kind tracks. This gap exists
  today, and `onSet` moments fired on the sheet make it more visible. Fix it in the same branch: list each numeric, non-slot kind track
  in `SheetCounters.Of` with `{key}_Max`, so the edit merges by delta.
- **Homebrew special-armor abilities lose the box** until they carry the modifier. The AI guide (step 11) must teach the shape. See open
  question 1.
- **Moment names in data.** A typo in `recovers.on` refills nothing. The loader and `FeatureGrantCheck` refuse unknown moments, as they
  refuse unknown rests today.

### 7.3 Migration of live Blades sheets

- No stored value is rewritten. Picks, track values and inputs stay as they are.
- New keys appear on the next recompute: `uses_foresight`, `uses_analyst`, `uses_calculating`, each starting full. A pending Expertise
  choice is a warning only. fitd has no `sheetRules`, so enforcement is `off` and nothing is ever blocked.
- Mule sheets: `LoadMax` rises by 2. Load warnings may clear.
- Sheets with no special-armor ability: `special_armor_Max` becomes 0. A box marked by hand stays stored and shows again if such an
  ability is picked. A range warning may show until it is cleared.
- Crew sheets: the rep maximum does not move (4.4).
- Ability ids are the importer's slugs and are unchanged, so every pick still resolves after the reimport.

### 7.4 Build order

Each step is one commit with its tests and suits Sonnet unless marked.

| # | Step | Size |
|---|---|---|
| 1 | `MomentDefinition`, `SystemDefinition.Moments`, `SystemMoments` resolver. Replace the five hard-coded lists. Loader checks. srd5e golden unchanged | S–M, Opus reviews |
| 2 | `MomentCommand` replaces `DawnCommand`; it raises the moment's event. Party panel `Matters(moment)` and buttons | S |
| 3 | `SheetKindDefinition.Features`. Loader checks (list only, no field/byField, no `modifiersFrom` clash). `KindFeatures` collector with map picks. `ActiveFeature.OnKind`. `Evaluate` runs `EvaluateFeatures` on the kind path; null-safe spend and derived code | M, **Opus designs the diff and reviews** |
| 4 | Kind feature modifiers in `SheetModel.Modifiers()`, with a per-instance cache | S–M, Opus reviews |
| 5 | `EvaluateKeyedChoices` (key choices on a kind's keyed list); `PruneChoices` and `ChangeChoice` for kind sources | S–M |
| 6 | `SheetCounters.Of` lists kind tracks (the merge fix); the `SheetIntents` guard for tracks the kind lacks (2.4) | S |
| 7 | fitd `system.yaml`: `features`, `moments`, `LoadMax` modifiers, the `'@resistance'` term, `specialArmorBoxes` + `modifierTypes`, `events: loadChosen / downtime`, crew `turfCount`, layout widgets | S |
| 8 | UI: `features` widget on kinds (`FeaturesPanel.Rules` optional, gates, `BuiltinWidgets`), `moments` widget, `onSet` firing, live-table routing (S10), accessibility. Box sources: `SheetView.TrackSources(key)` returns the sources in `Model.Modifiers[value]` when a track's `max` is a bare value key with `modifiers:`. The track widget shows "Special armor (Battleborn)" or "No ability gives this box" from it | M |
| 9 | Importer `Grants` table, `ApplyGrants`, `Validate`; smoke test exemption; regenerate the pack; review the 14-file diff | S |
| 10 | Strings en/ru/he (Haiku); schema docs regenerated; fitd golden corpus | S |
| 11 | AI: `FeatureGrantVocabulary` / `FeatureGrantGuide` / `FeatureGrantCheck` per sheet kind (targets = values with `modifiers:`, keyed entries, groups; keyed lists for choices; the system's moments). `FeatureGrantEditor` offers the same | M, later |

## Shared engine changes

These touch `sheetRules` systems too, so they need to be reconciled with the srd5e importer pass (Step 10 of FEATURE_GRANTS_DESIGN).

| # | Change | Effect on srd5e |
|---|---|---|
| S1 | **Moments resolver.** `SystemMoments.Of/IsRest` replaces `FeatureRecovery.Moments/IsMoment`, `SystemDefinitionLoader.ChangeMoments`, `FeatureGrantCheck.SystemDefinitionMoments`, `FeatureGrantVocabulary.Moments` and the guide text. `UsesRests` and `FeatureCounter.RecoversOn` ask "is a rest", not "is not a built-in moment" | None. With no `moments:`, the list is the same. The importer keeps writing `recovers.on` as rest names, `initiative` or `dawn` |
| S2 | **`DawnCommand` → `MomentCommand(actor, moment)`**, which also raises the moment's event when the system handles it. `DawnMatters` → `Matters(moment)` | Dawn behaves the same and logs the same keys |
| S3 | **Feature collection lifted:** `ActiveFeature.OnKind`, the `KindFeatures` collector, `EvaluateFeatures` callable without `Rules`, and null-safe `WorkedOutDerived` and spend resolution. The modifiers loop skips `OnKind` features | None. Gate: golden byte-identical |
| S4 | **`PendingChoice` for a kind's keyed list** (`Target` = list key, no rank), from `EvaluateKeyedChoices`. `ChoicesBlock`, `ChoicePicker` and `ChangeChoice` are unchanged | None |
| S5 | **`PruneChoices`** also counts entries held through kind sources | None |
| S6 | **Roll-scoped bonuses.** On kinds they are `when: input.*` now. srd5e's planned `on: { action, tags, entry }` should be built as the same thing: a `when` over the action's scope. Opt-in story-decided bonuses (`optIn`) are one later design for both | Shapes the srd5e `on` design. Nothing changes now |
| S7 | **No change to `ActionCost` or the `uses_` key scheme.** Box tracks are not spend pools (2.5). The srd5e importer can rely on `uses_{slug(key)}` as it is | None |
| S8 | **`SheetCounters.Of` lists sheet-kind tracks** | None (srd5e has no kinds) |
| S9 | **`FeaturesPanel.Rules` optional; `BuiltinWidgets["features"]` reads `features`** | None |
| S10 | **Moments and rests fired on the sheet while the actor is at a live table go through `MomentCommand` / `RestCommand`**, not a local refill followed by a merge | Fixes a lost-refill gap srd5e sheet rests have today |

## Open questions

1. **Should the special armor box be hidden on sheets with no ability that gives it?** The SRD says such a character "can't use that
   armor box at all". Recommended: yes, as in 3.3. The cost is that a live sheet with a homebrew special-armor ability that does not carry
   the modifier loses the box until the entry is fixed. The alternative keeps `max: 1` for everyone, and the seven abilities then carry
   no grant at all.
2. **Crew-to-PC effects** (Deadly, Forged in the fire, Anointed, Composed, Hardened, Mastery): a crew's abilities and upgrades change
   every member's sheet. This needs a link from a scoundrel to its crew sheet, and feature sources that read across it. Recommended: leave
   these as text now, and write a separate design for crew links.
3. **Opt-in roll bonuses** for story-decided "+1d" (Ambush, Savage, Trust in me, Weaving the web, Scout, Bodyguard, Anointed):
   recommended later, in one design with srd5e's `on` bonuses (S6). Until then the player uses the roll's bonus dice.

## Decisions (2026-11-13)

- Special armor box: hidden on sheets where no ability grants it (open question 1: yes).
- Crew-to-PC effects and story-decided "+1d" bonuses stay text; each gets its own later design (questions 2 and 3, as recommended).
- Shared changes S1 to S10 are accepted. S1 must keep `initiative` and `dawn` built in, so srd5e keeps accepting them without declaring them (the srd5e importer pass uses `initiative`).
- Build order: steps 1 to 10 on this branch. Step 11 (AI per sheet kind) is later. The srd5e importer pass (docs/SRD_GRANTS_IMPORT_DESIGN.md) branches from here after steps 1 and 3 have landed.
