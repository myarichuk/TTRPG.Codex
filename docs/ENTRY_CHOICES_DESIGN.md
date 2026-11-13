# Choices over Library entries: design

Status: designed 2026-11-13, not built. It builds on Step 10 (`SRD_GRANTS_IMPORT_DESIGN.md`) and free castings
(`FREE_CASTINGS_DESIGN.md`), on a branch stacked on Step 10's.

## What it is

A feature lets the player pick **Library entries**, such as spells: "one level-1 spell from the Cleric, Druid or Wizard
list" (Magic Initiate), or "one level-6 Warlock spell" (Mystic Arcanum). Today a choice can only pick keys of a ranked
list (skills, saves) or of a sheet kind's keyed list (Blades). `FeatureGrantCheck` refuses a choice on any other list, so
these features are text.

After this, a choice can pick entries. Which entries are allowed is written with the **same `where` conditions the
system's lists already use** (`RuleCondition`), so it stays system-agnostic. The picked entries go onto the target list
the same way fixed `adds` do, and a `free` on the item gives each pick a free casting.

## Data

```yaml
# Mystic Arcanum (Warlock 11): one level-6 Warlock spell, cast free once per Long Rest
adds:
  - to: grantedSpells
    choice: mystic-arcanum-6
    choose: 1
    where:
      - { property: classes, contains: Warlock }
      - { property: level, equals: 6 }
    free: { max: 1, recoversOn: [Long Rest] }

# Magic Initiate: pick a class list, then 2 cantrips and one level-1 spell from it; the spell free once per Long Rest
adds:
  - choice: spell-list                 # a value choice: no `to`, nothing goes on a list
    choose: 1
    from: [Cleric, Druid, Wizard]
  - to: grantedSpells                  # not `cantrips`: these do not count against the class's cantrips known, and a list with a limit takes no grants
    choice: cantrips
    choose: 2
    where:
      - { property: level, equals: 0 }
      - { property: classes, containsChoice: spell-list }
  - to: grantedSpells
    choice: spell
    choose: 1
    where:
      - { property: level, equals: 1 }
      - { property: classes, containsChoice: spell-list }
    free: { max: 1, recoversOn: [Long Rest] }

# Signature Spells (Wizard 20): two level-3 spells you already have prepared, each free once per Short or Long Rest
adds:
  - choice: signature                  # mark-only: no `to`, the picks go on no list
    choose: 2
    fromList: spells                   # pick among the sheet's own picks of that list
    where:
      - { property: level, equals: 3 }
    free: { max: 1, recoversOn: [Short Rest, Long Rest] }
```

**The rules:**
- An `adds` item is an **entry choice** when its `to` is a list of Library entries (one with a `targetEntityType`) and it has
  `choice`/`choose`. It has no `entries`. Its options are every candidate entry of that type that meets every `where`
  condition. Candidates are what `resolver.Candidates` returns, so a campaign's homebrew spells count too.
- `fromList: <list>` narrows the options to the entries the sheet already holds on that list. A choice with a `fromList` and **no
  `to`** is **mark-only**: the picks go on no list; they only carry what the item says of them (`free`). So the player can still
  take the spell off the list, and the pick goes stale. A `to` equal to `fromList` is refused by the check ("a choice from a
  list you already hold adds nothing"): the granted entry would keep itself on the list.
- While the value choice a choice reads has no pick, the choice is **blocked**: it offers nothing, grants nothing, and keeps
  its stored picks untouched (no stale violations). They come back when the value is picked again; they go stale only when
  the value is picked and the old picks do not fit it. Picks above `choose` are dropped without a violation.
- Value choices store the **canonical** word (a class's name as the entries write it); the picker displays it translated.
- A **value choice** has no `to` and a plain `from` of values. It is stored like any choice and adds nothing.
  `containsChoice` and `equalsChoice` read it.
- **Two new conditions** on `RuleCondition`, which also work in a list's own `where`:
  - `contains: <value>`: the candidate's list property contains this constant (`classes` contains `Warlock`).
    `containsNameOfField` already does this with a field's entry name, but nothing does it with a constant.
  - `containsChoice: <choice id>` and `equalsChoice: <choice id>`: compare against the value(s) picked in another choice
    of **the same feature copy**. If that choice has no pick yet, the condition fails, so the dependent choice offers
    nothing and says "choose the spell list first".
- `where` on an entry choice is checked when evaluating the sheet, so a pick that no longer fits shows as stale, the way
  row choices show stale picks today.

## Storage

- Picks are stored as today: `choice__{entryId}[#copy]__{choiceId}` (`ChoiceKey`) holds a list. For an entry choice it holds
  **entry ids**; for a value choice, the values.
- `ApplyGranted` writes an entry choice's valid picks onto its `to` list and records them in `granted__<list>`, exactly
  like fixed entries. `StripGranted` takes them off. So the cast action, prepared-spell counts and the Library panel see
  them with no change.
- A second copy of a feat (Magic Initiate twice) already gets its own keys, through the copy number.
- **Free castings on picks** get the counter id `free-<choiceId>-<n>`, scoped like the choice key
  (`free-{entryId}[#copy]-{choiceId}-{n}`), where `n` is the pick's position (1, 2...). The counter belongs to the slot,
  not the spell, so changing the pick keeps what is left and cannot refill it. Its label is "<feature>: <spell name>" and
  follows the pick.

## Engine changes

1. `FeatureChoice` gets `Where: IReadOnlyList<RuleCondition>?`, `FromList: string?` and `IsValueChoice`. `ParseChoice` reads
   them. `FeatureGrantData` round-trips them. `FeatureGrantCheck` allows a choice on an entry list, and a value choice. It
   still refuses an entry choice with no `where` and no `fromList` (it would offer the whole Library), and a
   `containsChoice` naming a choice the entry does not have.
2. `RuleCondition` gets `Contains`, `ContainsChoice` and `EqualsChoice`. `Holds` takes an optional context with the
   current feature copy's picks. Lists' own `where` gets `contains` for free.
3. **Evaluation.** A new partial, `SheetRuleEngine.EntryChoices.cs`, works out the options, reads the picks, splits them
   into valid and stale, and adds a pending notice, as `EvaluateKeyedChoices` does. Dependent choices are evaluated in
   the order they are written in the entry. A value choice must come before the choices that read it, and the check
   enforces that.
4. `SheetEvaluation` exposes the open entry choices in the same shape as the row and keyed choices, so the sheet's
   choices block, the level-up stepper and the join review list them with no new plumbing.
5. **References.** `SheetReferences.Of`, `Without` and `Repointed` include entry ids held in `choice__` keys, so a carried
   homebrew spell picked through a choice reaches the join review, and a swap repoints the pick. This touches the file
   PR #301 changed; build it after #301 is merged.
6. **Free castings:** a `free` on an entry choice creates one counter per valid pick with the slot id above, and costs
   on the picked spell's action as in `FREE_CASTINGS_DESIGN.md`.

## Sheet, table, AI

- **Picker.** `ChoicePicker` gets an entry mode:
  - The options can number in the dozens, so it is a searchable list of entry names, each with its level or type and a
    short line of its description, and a link to the Library card.
  - The title says what the choice wants, built from the feature and the count: "Magic Initiate: choose 1 level-1 spell".
  - A value choice is a plain radio group. A dependent choice says "Choose the spell list first" until it has one.
  - Keyboard and screen reader: a labelled search box and a list with selectable options; works right to left.
- **The AI drafter.** `ChoicesRequest` gets the options of each entry choice as names. When there are more than 40,
  it gets the first 40 by name plus the count of the rest. `ParseChoices` maps the names back to ids and drops unknown
  ones. A value choice is asked first, then its dependent choices with the narrowed options.
- **The homebrew editor** (`FeatureGrantEditor`, `GrantAddEdit`) round-trips entry choices. It shows the `where`
  conditions read-only, as a sentence ("level 6, on the Warlock list"), and lets the author change the count and the
  `free` part. Editing conditions is later work.
- **Strings** (en, ru, he): picker titles, "choose the spell list first", stale and pending notices, and check
  messages. ru and he need native review.

## SRD data (importer, `srd521-grants.yaml`)

| Feature | What it becomes |
|---|---|
| Magic Initiate (origin feat, repeatable) | value choice of list + 2 cantrips + one level-1 spell (all `to: grantedSpells`), `free` once per Long Rest on the spell |
| Mystic Arcanum 11/13/15/17 | one entry choice per level (6 to 9), Warlock list, `free` once per Long Rest |
| Signature Spells | mark-only (`fromList: spells`, no `to`), level 3, choose 2, `free` once per Short or Long Rest |
| Spell Mastery | mark-only (`fromList: spells`, no `to`), one level-1 and one level-2 wizard spell, `free: atWill` |
| Pact of the Tome (invocation) | 3 cantrips from any class list (`where: level 0`), and 2 level-1 ritual spells |
| Eldritch Invocations | a list (`invocations`) with the importer's new entries; prerequisites as the list's `where` (`minLevel lteField level`, and so on); not a choice |

Divine Intervention and Natural Recovery stay text: their limit depends on the casting, not on a pick.

## Tests

- **Parse, round-trip and the check:** an entry choice, a value choice, `fromList`, `contains`, `containsChoice`; an entry
  choice with no limits is refused; a dependent choice placed before its value choice is refused.
- **Magic Initiate:** no list picked means its two dependent choices offer nothing. Picking Wizard narrows them. Switching
  to Cleric makes the old picks stale.
- **Two copies of Magic Initiate** keep separate picks and counters.
- **Mystic Arcanum** at Warlock 11 offers only level-6 Warlock spells. The pick lands on `grantedSpells` and casts free once.
- **Signature Spells** offers only prepared level-3 spells. Unpreparing one makes its pick stale.
- **The free-casting counter** keeps its value when the pick changes.
- **Join review:** a homebrew spell picked through a choice is listed. A swap repoints the pick.
- **Drafter:** names map to ids, and unknown names are dropped.
- **Goldens:** the srd5e golden changes only for the mapped features.

## Not in this design

- Editing `where` conditions in the homebrew editor.
- Choices whose limit depends on the casting (Divine Intervention, Natural Recovery).
- A choice that picks from another character's entries.

## Decisions (2026-11-13)

1. **Re-picking:** the player may change an entry pick on their sheet, like every other choice. The slot-keyed counter
   makes that safe.
2. **Homebrew candidates:** a campaign's homebrew entries are offered when they meet the conditions, as the sheet's own
   lists do.

## Build order and who builds it

| # | Step | Who | Notes |
|---|---|---|---|
| 1 | Parse, round-trip, check (`FeatureChoice`, `FeatureGrantData`, `FeatureGrantCheck`) | Sonnet (the Step 10 session) | Stacked branch from Step 10 |
| 2 | `contains`, `containsChoice`, `equalsChoice` and `Holds` context | Sonnet | |
| 3 | `SheetRuleEngine.EntryChoices.cs`: options, picks, stale, pending; `ApplyGranted`/`StripGranted` | Sonnet | The heart. Opus reviews the diff of steps 1-3 before step 4 starts |
| 4 | Free castings on picks (slot-keyed counters) | Sonnet | Needs free castings built |
| 5 | `SheetReferences` (join review, swaps) | Sonnet | After PR #301 is merged |
| 6 | Picker entry mode and homebrew editor round-trip | Sonnet subagent | In parallel with 7 and 8; different files |
| 7 | AI drafter (`ChoicesRequest`, `ParseChoices`) | Sonnet subagent | In parallel |
| 8 | SRD data in `srd521-grants.yaml` (table above), from the SRD 5.2.1 PDF | Sonnet subagent | In parallel. Rules judgment, so not Haiku |
| 9 | Full suite, goldens, coverage numbers, `CHARACTER_FEATURES_GAPS.md` | Sonnet | |

Haiku: read-only sweeps (for example, "every reader of `choice__` keys" before step 5). It writes nothing; every write
step needs rules or engine judgment.
