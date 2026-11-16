# P3: general ranked lists (diff design)

Read against `origin/test/degrees-fixture`. Builder: Sonnet. The srd5e golden (`tests/Codex.Tests/Golden/srd5e/expected.txt`)
must not change by one byte. Don't regenerate it. If it changes, the build is wrong.

## What exists, and the trap
- `SkillRules` holds two ranked lists (`skills`, `saves`); `EvaluateRanks` (Ranks.cs) does ticks, fixed sources, grants, choices, raises and values.
- `Evaluate()` order: Attributes, Derived, Multiclass, **Features** (derived-target modifiers applied, AC and HP amounts parked in `Pending`),
  constraints, lists, slots, inventory, HP, AC, **Skills (ranks)**, checks, spellcasting, proficiencies, actions. So AC can't read a rank, and a
  ranked Perception can't be a `derived` formula. The golden prints `Warnings` in insertion order, so **the skills/saves pass must not move**:
  the general lists get a second, earlier call of the same code (empty for srd5e).

## Decision: alias, don't migrate
`skills` and `saves` stay where they are, with the same YAML and the same code path. New: `sheetRules.ranked`, a list of general ranked lists.
In the engine all of them are `RankedList`s. `BuildRankedLists` builds skills and saves as now. `BuildGeneralLists` builds the new ones.

## YAML shape (invented example)
```yaml
sheetRules:
  ranked:
    # One key, the list's own (no `list:`). Its score is published as the value `perception`.
    - key: perception
      label: Perception
      ranksOf: skills            # the skills' rank keys, labels and values; sheet keys perceptionTrained, perceptionExpert, ...
      attribute: wis
      publish: true
    - key: classDc
      label: Class DC
      ranksOf: skills
      base: 10
      attributeFrom: { field: class, property: keyAttribute }   # the class entry names it; a FieldProperty
      publish: true
    # Several keys, nothing published: armor class and actions read the rank value through a `rank` lookup.
    - key: armor
      label: Armor
      ranksOf: skills
      list:
        - { key: unarmored, label: Unarmored }
        - { key: light,     label: Light armor }
        - { key: medium,    label: Medium armor }
        - { key: heavy,     label: Heavy armor }
    # Own ranks instead (invented): sheetKey optional, default {list}{RankKey} (wardcraftNovice).
    - { key: wardcraft, label: Wardcraft, ranks: [{ key: novice, value: "level + 1" }, { key: adept, value: "level + 3" }], list: [{ key: hearth }, { key: road }] }
  armorClass:
    rank: { list: armor, property: armorCategory, unarmored: unarmored }   # adds the worn armor's category rank value
  actions:   # with a `weapons` list (simple, martial, unarmed) declared like `armor`
    - { key: strikes, ..., rank: { list: weapons, property: category }, attackBonus: "rankValue + abilityMod" }
```
A grant reaches a list exactly as it reaches skills:
- `adds: [{ to: armor, entries: [light, medium], rank: trained }]`
- `{ to: perceptionExpert, entries: [perception] }`: the rank's sheet key, so that rank.
- `{ to: perception, raise: 1 }`. On a one-key list `entries` may be left out; it means the one key.
- `{ to: armor, choice: armor-pick, choose: 1, from: [medium, heavy], rank: expert, minRank: trained }`.
Choices are stored under `choice__{entry}__{id}` as now.

## C# types (src/Codex.Plugin.Abstractions/SheetRules.cs)
Put these next to `RankDefinition`:
```csharp
public class RankedListRule
{
    public string Key { get; set; } = "";              // required; also the `to` of a grant
    public string Label { get; set; } = "";
    public List<RankDefinition> Ranks { get; set; } = new();   // SheetKey optional here
    public string? RanksOf { get; set; }                // skills | saves | another ranked list with its own ranks
    public List<RankedKeyDefinition> List { get; set; } = new();  // empty: one key, Key itself
    public string? Attribute { get; set; }              // per-key attribute overrides it
    public FieldProperty? AttributeFrom { get; set; }   // attribute named by a referenced entry (class keyAttribute)
    public int Base { get; set; }                       // 10 for a DC
    public bool Publish { get; set; }                   // each key's score becomes result.Derived[key]
    public IReadOnlyList<RankedKeyDefinition> Keys() => List.Count > 0 ? List : new[] { new RankedKeyDefinition { Key = Key, Label = Label } };
    public static string DefaultSheetKey(string list, string rank) => list + char.ToUpperInvariant(rank[0]) + rank[1..];
}
public class RankedKeyDefinition { public string Key {get;set;} = ""; public string Label {get;set;} = ""; public string? CanonicalLabel {get;set;} public string? Attribute {get;set;} }
public class RankLookup { public string List {get;set;} = ""; public string Property {get;set;} = ""; public string? Unarmored {get;set;} }
```
- `SheetRules.Ranked: List<RankedListRule> = new()`, `ArmorClassRules.Rank: RankLookup?` and `ActionSource.Rank: RankLookup?`.
- Instance method `IReadOnlyList<RankDefinition> SheetRules.RanksFor(RankedListRule list)` (it needs `this.Skills` and `this.Ranked`) resolves `RanksOf` and fills the default sheet keys
  **on new copies**, never by mutating: a YAML alias or `ranksOf` would otherwise share one object. It returns `Key`, `Label` and `Value`
  from the source rank and `SheetKey = given ?? DefaultSheetKey(list.Key, rank.Key)`. A copied rank never takes the source's own sheet key.
- Update the `FeatureSource` doc comment ("On a ranked list (`to: skills` or `to: saves`...)") to say "or a `ranked` list's key".

## Engine (src/Codex.Core/Rules)
**`SheetRuleEngine.cs`**
- Add to `SheetEvaluation`: `Dictionary<string, List<SkillScore>> Ranked` (OrdinalIgnoreCase, in declaration order: fill it in order) and
  an internal `HashSet<string> RankChoiceKeys` (the choice keys the general pass used).
- In `Evaluate()`, add one line right after `EvaluateFeatures(sheet, result, linked);`: `EvaluateRankedLists(sheet, result);`.
  It runs before constraints, lists, HP, AC, skills and actions. Nothing else in `Evaluate()` moves.

**`SheetRuleEngine.Features.cs`**
- Add `TargetKind.Ranked`. In `KindOf(system, target)`, after the Derived check, add:
  `if (rules.Ranked.Any(l => l.Publish && l.Keys().Any(k => k.Key == target))) return TargetKind.Ranked;`.
  The `default:` branch of the modifier switch already parks amounts in `result.Pending[target]`, so no change is needed there.
- The loader's hybrid rule (`SystemDefinitionLoader.cs:880`, which counts any non-Unknown kind as claimed by sheetRules) then accepts
  Frightened's `perception` target unchanged.

**`SheetRuleEngine.Ranks.cs`**
- `RankTargetOf(to)` searches skills, saves, then each general list, by `Key` or by one of its `RanksFor` sheet keys. Drop the early
  `return null` when `Rules.Skills` is null.
- `BuildGeneralLists()` makes a `RankedList(list.Key, list.Label, RanksFor(list), keys)`. Each key gets
  `Attribute = key.Attribute ?? list.Attribute ?? (attribute named by AttributeFrom on the first class / referenced entry)`.
  Resolve `AttributeFrom` the way `SavingThrowsProperty` is read: `ReferencedEntry(field, sheet)` (first class when the field is the class
  field), then `AttributesNamedIn(property).FirstOrDefault()`.
- Generalise the signature to `EvaluateRanks(sheet, result, IReadOnlyList<RankedList> lists, SkillRules? skillRules)`:
  - Fixed sources (background and class saves) only when `skillRules != null`.
  - Replace `var skills = lists[0]; var saves = lists[1]` and `target.Target == SkillsTarget ? skills : saves` with
    `lists.FirstOrDefault(l => l.Target == target.Target)`. When that is null the add belongs to the other pass: `continue`, no warning.
  - The warning for an add to a non-ranked target (`IsEntryChoice` etc.) fires in the skills pass when `Rules.Skills != null`. Otherwise it
    fires in the general pass. Exactly one place, as today.
  - `keysSeen` is also seeded from `result.RankChoiceKeys`. The general pass adds its own choice keys to it.
  - Unknown-name warning: keep `skill` / `saving throw` exactly as now. A general list uses
    `$"{list.Label}: \"{name}\" is not on this list."` with field key = the sheet key.
  - Output: `Skills` and `SavingThrows` only for those targets. General lists go into `result.Ranked[list.Target]`. `RankValues` are added
    for every list, as now (`LevelUpPlan` filters to `SkillsTarget`, unchanged). Return `lists.FirstOrDefault(l => l.Target == SkillsTarget)`.
  - `ScoreOf` for a general key: `SiteBonus` is not read (no roll site; `Extra = 0`), and the bonus is `list.Base + mod + rank value`.
    The skills/saves `ScoreOf` stays byte-for-byte.
  - On a one-key list, an empty `add.Entries` means `[list.Key]`, for fixed ranks and raises alike.
- New `EvaluateRankedLists(sheet, result)`. It returns if `Rules?.Ranked is not { Count: > 0 }`. Otherwise it calls
  `EvaluateRanks(sheet, result, BuildGeneralLists(sheet, result), null)`, then for each `Publish` list and key:
  `score = WithModifiers(base + mod + rankValue, result.Pending.GetValueOrDefault(key) ?? new(), key, result.Granted)`,
  then `result.Derived[key] = (long)score` and `DerivedInputs[key]` = `{ base, rank, attribute }`. The published value does **not** read
  roll-site targets (`check`, `save`). Frightened reaches it once, through its own `perception` target. Reading `check` too would count
  the condition twice, which is the case the fixture's `Inspired` guard exists to catch.
- `EvaluateSkills` (Skills.cs) passes `BuildRankedLists(rules), rules`. Same behaviour.
- Known limit, to note in a doc comment: a feature *amount* formula that names a published value sees nothing, because amounts are worked
  out in `EvaluateFeatures`, before the ranks. It warns as any formula that can't be read.

**`SheetRuleEngine.Vitals.cs` (armor class)**
- When `rules.Rank is { } r` and the list exists, add one more untyped candidate. Its key is the worn body armor's `r.Property`, or
  `r.Unarmored` when nothing is worn: `new ArmorClassLine("rank", $"{list.Label} ({rankLabel})", value)`. It goes before the features'
  pending lines. A key that isn't on the list, or an untrained one, adds nothing and no line. Values come from
  `result.Ranked[r.List]` (`SkillScore.RankBonus`, the rank value without the attribute).

**`SheetRuleEngine.Actions.cs`**
- Per entry: `names["rankValue"]` = the rank value of the entry's `source.Rank.Property` key in `source.Rank.List` (0 when absent).
  `names` is set only when `source.Rank != null`.

## Hit points: decision 3
**A die-less shape, null by default.** It is needed: `ApplyHitPoints` (current HP following the maximum), `LevelUpPlan`, `HitPointRulesPanel`
and `LevelUpStepper` all read `result.HitPoints`. A derived `maxHp` feeds none of them. P7's real system needs them. The fixture's
numbers pass either way.
- `HitPointRules` gets `string? FlatProperty` (a class entry property, the HP per level, replacing the die) and `FieldProperty? Once`
  (the HP an entry adds once, an ancestry's).
- In `EvaluateHitPoints`, when `FlatProperty != null`:
  - each level's base = that number (a missing one warns like `NoHitDie`, with `"{class} has no hit points per level, ..."`);
  - `HitPointLevel(Die: 0, Base: flat, Modifier, Gain = max(flat + mod + extra, MinimumPerLevel))`;
  - max = levels + `Once` + `Pending`.
  - The mode defaults to `"fixed"` (active) when the sheet has none. `manual` stays possible. No rolls.
- Add trailing optional `int Once = 0, string? OnceSource = null` to `HitPointSummary`. The golden's `hp` lines don't print them.
- Loader:
  - `"sheetRules.hitPoints.once.field '{f}' is not an EntityReference actor field."`
  - `"sheetRules.hitPoints: flatProperty and firstLevel/average cannot both be given."` (only if they are written explicitly; the
    defaults don't count).
- This lands as its own commit (P3c) after the ranks. It edits `Vitals.cs` and is independent of them.

## Loader checks (new file `src/Codex.Core/Systems/RankedListChecks.cs`, one call from SystemDefinitionLoader)
The checks go in their own file to keep the loader diff to one line, because H1 and P4b also edit `SystemDefinitionLoader.cs`.
The signature is `static IEnumerable<string> Problems(SystemDefinition d, HashSet<string> fieldKeys, HashSet<string> listKeys)`, called
beside the skills checks (~line 505). It repeats the sheet-key uniqueness check across the skills, saves and ranked lists together.
The loop over skills and saves stays where it is; the new file adds the general lists' sheet keys to the same comparison.
Messages (path prefix `sheetRules.ranked`):
- `... needs a key on every list.` / `... key '{k}' is declared more than once.` / `... key '{k}' is reserved (skills, saves).`
- `... '{k}' collides with an actor field, a derived value, a sheetRules.lists key, or the armor class or hit point key.` (list key and each published key)
- `... '{k}' needs ranks or ranksOf, not both.` / `... '{k}' ranksOf '{o}' names no list with ranks of its own.`
- Rank checks as the skills loop words them, path `sheetRules.ranked.{k}.ranks` (key, not a number, duplicates, readable value); sheet keys
  unique across every ranked list, skills and saves included: `... sheetKey '{s}' is used by more than one rank.`
- `... '{k}' key '{key}' is declared more than once.` / `... '{k}' uses attribute '{a}', which is not one of the system's attributes.` /
  `... '{k}' attributeFrom.field '{f}' is not an EntityReference actor field.`
- `sheetRules.armorClass.rank.list '{l}' is not a sheetRules.ranked list.` / `... unarmored '{u}' is not a key of {l}.` /
  `sheetRules.actions '{a}' rank.list '{l}' is not a sheetRules.ranked list.`
- Unknown-key detection (`UnknownYamlKeys`, reflection over the types) picks up the new properties by itself; the localised copy
  (`ApplyRuleLabels`) is a shallow copy, so `Ranked` and the new `Rank`/HP properties carry over to ru/he untouched.

## Grants check and vocabulary
- `FeatureGrantVocabulary.cs`: after skills and saves, add `new GrantList(list.Key, list.Label, null, rules.RanksFor(list).Select(Rank),
  list.Keys().Select(k => new GrantName(k.Key, k.Label)))` per general list. `ComputeTargets` and `ComputeFormulaNames` (lines 159, 171) add each published key (they read only `rules.Derived` today, so the
  guide and editor would stop offering `perception`/`classDc`). Add `rankValue` to the formula names when any action has a `Rank`.
- `FeatureGrantCheck.cs` keys off `vocabulary.Lists.IsRanked`, so rank-name checks, choice ids and `rank`/`raise` on general lists come free.
  Only its summary comment changes. It treats `to: perceptionExpert` the same way as `to: skillExpertise` (not checked), as today.
- `FeatureGrantGuide.cs`: it already lists every ranked vocabulary list. Check that its text doesn't say "skills" where it means "a ranked list".
- `SystemDefinition.cs` localised copy (~line 806): translate the general lists' rank labels with `t.Ranks` (by rank key), as skills do.
  The list label stays as written (open question 3).

## Web (minimum)
- `ActorSheet.razor:1222` (known rank sheet keys): add `Rules.Ranked.SelectMany(l => Rules.RanksFor(l)).Select(r => r.SheetKey)`.
- Label fallback for a ranked target = the general list's `Label`: `FeaturesPanel.razor:179-182`, `FeatureGrantEditor.razor:697`, `VaultRequests.razor:457`.
- Published values: `AttributesPanel.razor:184`, `TableCard.razor:304` and `ActorSheet.razor:1109` loop over `Rules.Derived` only; also show
  each published key (label = key's label, signed unless the list has a `base`). A tick panel for general ranks is not in P3 (open question 1).

## Fixture migration (tests/Codex.Tests/Fixtures/reference/degrees)
- `system.yaml`:
  - remove the `classDc` and `perception` derived values (keep `maxHp` until P3c);
  - add `ranked:` with perception (`attribute: wis`), classDc (`base: 10, attributeFrom: { field: class, property: keyAttribute }`),
    armor (`unarmored, light, medium, heavy`), all `ranksOf: skills`, `publish` on the first two;
  - add `armorClass.rank: { list: armor, property: armorCategory, unarmored: unarmored }`.
- `classes.yaml`:
  - vanguard gains `keyAttribute: str`;
  - row `Armor training` becomes `adds: [{ to: armor, entries: [unarmored, light, medium], rank: trained }]` (its modifier goes);
  - a new row `{ name: Watchful, level: 1, adds: [{ to: perception, rank: trained }, { to: classDc, rank: trained }] }`.
- `armor.yaml`: scale coat gains `armorCategory: medium`.
- P3c: `hitPoints: { key: maxHp, currentKey: hp, flatProperty: hp, attribute: con, once: { field: ancestry, property: hp } }`. Class
  `hp: 8` replaces the Hardy modifier, ancestry `hp: 8` replaces its modifier, and the `maxHp` derived value goes. Check that `hp` and
  `hpMode` are not actor fields.
- Rewrite the comments that say "until P3". Update `spec.yaml` above `perception-has-a-rank-of-its-own` to `now: pass`.
- `progress.txt` gets the id, and the README count changes from 25 to 26.

Arithmetic, re-checked (hero5: level 5, Str +4, Dex +2, Wis +1): perception trained 0+7+1 = 8 (as today), with `perceptionExpert`
9+1 = **10**; classDc 10+7+4 = 21 (as today, so 25/21/19/22 hold); AC coat 13+2+7 = 22, Dex 18 capped 22, unarmored 10+2+7 = 19;
maxHp 8+(8+2)x5 = 58, level 1 = 18. The perception-at-6 expectations come from the Pending stacking; `untyped-penalties-stack` stays on P2.

## Spec ids after P3
`degrees.perception-has-a-rank-of-its-own` passes; the 25 passing today still pass; the fails are unchanged (P2, P4b, as P1 recorded).

## Tests (new `tests/Codex.Tests/RankedListTests.cs`, plus loader cases where the loader tests live)
1. A tick in `perceptionExpert` gives level + 4 + Wis, published as `Derived["perception"]`. No rank gives Wis alone. Base 10 is added for classDc.
2. `ranksOf: skills` gives default sheet keys `perceptionTrained` and up, and never the skills' own `skillTrained`. Explicit `sheetKey` wins.
   A YAML alias shared by two lists gives each list its own keys (no shared mutation).
3. Fixed grant `to: armor, entries: [light]`. `to: perceptionExpert` without entries sets that rank. `raise: 1` goes trained to expert and
   stops at the top. `rank` and `raise` together warn.
4. Choice to `armor` with `minRank`: pending, then made. A pick that no longer fits is a violation. The key appears in
   `RankChoiceKeys` and does not collide with a skills choice of the same entry.
5. Modifiers and conditions on a published key count once (an untyped +1 twice gives +2, not +4), and the published value ignores `check`.
6. Armor class rank line: worn category, unarmored key, a category not on the list (no line).
7. Action `rank` gives `rankValue` per entry.
8. A sheet with skills and no general lists evaluates to the same warnings in the same order (golden guard, plus the srd5e golden test
   itself, run unchanged).
9. A system with `ranked` and no `skills` works, and the non-ranked-add warning fires once.
10. Loader: one test per message above. FeatureGrantCheck: `rank: grandmaster` on `to: armor` lists armor's ranks.
11. P3c: flat HP at levels 1 and 5, `once`, the default mode is active, `ApplyHitPoints` keeps current HP in step, and srd5e is unchanged.

## File-by-file outline
- `Plugin.Abstractions/SheetRules.cs`: new types, `Ranked`, `RanksFor`, `ArmorClassRules.Rank`, `ActionSource.Rank`, P3c HP props, doc comments.
- `Core/Rules/SheetRuleEngine.cs`: `SheetEvaluation.Ranked`, `RankChoiceKeys`; one call in `Evaluate()`.
- `Core/Rules/SheetRuleEngine.Ranks.cs`: `RankTargetOf`, `BuildGeneralLists`, generalised `EvaluateRanks`, `EvaluateRankedLists`.
- `.Skills.cs` (signature), `.Features.cs` (`TargetKind.Ranked`), `.Vitals.cs` (AC line, P3c HP), `.Actions.cs` (`rankValue`).
- `Core/Rules/FeatureGrantVocabulary.cs` (lists, targets, names); `FeatureGrantCheck.cs`, `FeatureGrantGuide.cs` (comments, wording).
- `Core/Systems/RankedListChecks.cs` (new) + one line in `SystemDefinitionLoader.cs`; `SystemDefinition.cs` (rank label translation).
- Web files above; optional `tools/Codex.SystemCheck/SpecRunner.cs` `ranks.<list>.<key>` = rank value; fixture folder, `progress.txt`, README.

Commits: P3a types, loader and vocabulary; P3b engine, AC, actions, fixture and tests; P3c flat HP. Run the full test suite after each.
Clean orphaned RavenDB test data first if the disk is near full.

## For P5 (boost rules; same files, after P3)
- P3 adds one line to `Evaluate()`, right after `EvaluateFeatures`, and does not touch `EvaluateAttributes` or `AttributeRules`. P5's region
  of `SheetRules.cs` is separate from P3's types, so the rebase is trivial.
- Ranks (and published values) read attribute modifiers through `ModifierOf` → `result.Attributes.Totals`. Boosts therefore have to be final
  in `EvaluateAttributes`, which runs before Derived and Features.
- So P5's ancestry, background and free boosts must be read straight from the entries, as `EvaluateIncreases` reads picks. They must not be
  feature `adds` grants: those are collected in `EvaluateFeatures`, after the derived values, and an attribute arriving there would miss
  every derived value. If P5 needs choices, reuse `ChoiceKey`/`SetChoice` storage (`choice__{entry}__{id}`) and the pending-choice records.
  Do not route boosts through `RankTargetOf`/`ApplyChoice`. A boost is not a rank: the partial boost above +4 and "four different" break
  the highest-rank-wins rule. And do not change `ApplyChoice`'s semantics.
- `sheetRules.ranked` and `rankValue` are taken names. A boost's attribute filter can reuse the `from: { attribute: [...] }` shape.

## Open questions
1. Is a sheet panel for ticking general ranks (beside `SkillsPanel`) part of P3, or a follow-up Sonnet step? This design leaves it out.
2. Should a list also publish rank numbers (0-4) for `when` formulas ("expert in heavy armor")? Not done; it's easy to add later as
   `rankOf_{list}_{key}`.
3. Translating the general lists' own labels needs a new translation-pack map (`t.RankedLists`?). This design leaves the labels as written.

## Decisions (2026-11-16)

1. No rank-ticking panel in P3; grants and choices are enough. A small UI step after P5 adds it (with en/ru/he strings).
2. No published rank numbers (0 to 4) yet; add them when a `when` formula needs one.
3. The new lists' labels stay as written; the translation-pack map for them is a follow-up, listed in the localization notes.

## Built (differences from the design)
- Serena note: two Serena servers exist (`mcp__serena__*` stayed on the main checkout; `mcp__plugin_serena_serena__*` was activated on the worktree). Two early edits hit the main checkout and were reverted; main is byte-identical again.
- `RankedListRule.Keys()` label falls back to the key. `RanksFor` copies the source's Key/Label/Value; a list's own ranks keep a given sheetKey.
- A grant with `to` and no entries is no longer dropped when the feature is read (`SheetRuleEngine.Features.cs`), so `{ to: perceptionExpert }` and `{ to: perception, rank: trained }` work on a one-key list. `FeatureGrantData.Counts` also counts an add with `rank`/`raise`.
- Loader messages for `ranksOf` use the design's words; an own-ranks list with a rank value is also formula-checked. Sheet keys of general lists are checked together with skills and saves inside `RankedListChecks.cs`.
- Web: general lists' choice target labels (ActorSheet, VaultRequests, FeaturesPanel) and published values (AttributesPanel, TableCard, ActorSheet plate) were added; ranks-of-a-list translation in `SystemDefinition.cs` covers lists with own ranks (a list on `ranksOf: skills` is translated through the skills).
- progress.txt: 31 of 34 became 32 of 34; README sentence says 32 of its 34 (it said 30 before).
- Tests live in `tests/Codex.Tests/GeneralRankedListTests.cs` (the name `RankedListTests` was taken by the skills' tests).
