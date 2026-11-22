# Companions and familiars: design (P11)

Status: designed 2026-11-22, not built. Step P11 of [PF2E_HAZARDS_IMPLEMENTATION_PLAN.md](PF2E_HAZARDS_IMPLEMENTATION_PLAN.md). It builds on
[CREW_LINKS_DESIGN.md](CREW_LINKS_DESIGN.md) (links between sheets) and [SHEET_CONDITIONS_DESIGN.md](SHEET_CONDITIONS_DESIGN.md).

## What it is

In the four-degrees system a character can have an animal companion or a familiar. Each is a creature of its own at the table: it
has hit points, takes conditions and makes checks. Most of its numbers come from its owner:
- **A companion** has its owner's level. Its attributes, hit points and attacks start from its type (a `companion` entry of the pack),
  and it grows in stages (young, then mature, then a specialised stage) that the owner's feats unlock.
- **A familiar** also has its owner's level and a few hit points per level. Its defences and some of its checks are its owner's numbers.
  Each day, at the daily preparations, the owner picks its abilities from the `familiar-ability` entries, a set number of them plus
  any that feats add.

The pack already holds the companion types and the familiar abilities, as reference entries that no sheet reads yet. This step gives
each its own sheet kind.

The numbers (the hit points per level, the ranks at each stage, how many abilities a familiar has) come from the rule text when the
step is built, not from this design or from memory.

## What the engine has, and the two gaps

Already there:
- **Links** (CL1, CL2): a kind declares `links`, the sheet stores the linked actor's id, and `link('owner', 'level')` reads a number from
  the linked sheet, one step deep and read-only. The owner's sheet can list who links to it (`{ widget: members }`).
- **Kind values, tracks and pick lists**: formulas can read a picked entry's fields (`first(type).hp`).
- **Choices that change only at a moment** (`changes_on__*`): a pick the sheet holds until the next daily preparations.
- **Conditions on a kind sheet**: a condition modifier whose target is a kind target goes to the kind's values (`SheetConditions.KindTargets`).

**Gap 1: the character rules run on every sheet.** `SheetRuleEngine.Evaluate` applies `sheetRules` to any sheet of the system,
whatever its kind. So a companion sheet would get the character build (ancestry, class, boosts, hit points from the class).
- Add `sheetRules.kinds`: the kinds the character rules apply to. Left out, they apply to every kind, as today, so srd5e and the
  other systems do not change.
- The sheet's kind is the one `SheetLayout.KindOf` gives, never the raw stored key: a four-degrees character saved before kinds has no
  kind key and is a `character` only through the default. Comparing the stored string would take the rules off every such sheet.
- On a sheet whose kind is not listed, the engine runs only the kind's own model (values, tracks, lists, features), as it does for a
  system without `sheetRules`.
- Condition routing follows: on such a sheet every condition modifier goes to the kind's values. One whose target the kind does not
  declare reaches nothing. It is not a load error, since the same condition is fine on a character.
- **The load rule that refuses a target both sides claim** (`SystemDefinitionLoader`, from #322) is checked only for the kinds
  `sheetRules.kinds` lists. A companion's `ac` or `saves.fort` is the same name the character rules use, and that is the point: frightened
  reaches both. Checked against every kind, as today, the four-degrees system would no longer load.
- The system's `actorFields` (ancestry, class, faith) are character fields. A kind with its own `layout` shows only the fields its
  layout names. The builder confirms that is so today, and makes it so if not.

**Gap 2: `link()` cannot read all that the character rules work out.** `SheetModel.LinkModel` builds a plain kind model of the linked
sheet, which reads the sheet's stored keys. The character rules write some of their results back onto the sheet (`Recompute`: the AC
into `armorClass`, `HP_Max`, slot pools), so `link('master', 'armorClass')` already works. The saves, skills, Perception and the spell
attack and DC are worked out but not stored, and a familiar needs some of them.
- When the linked sheet's kind is one `sheetRules` applies to, `link()` evaluates it with the rule engine (its own links not followed,
  as now) and reads the result from its `SheetEvaluation`: `saves.<key>` (from `SavingThrows`), `skills.<key>` (from `Skills`), and the
  Perception and spell attack and DC under names P11a picks. `saves.` and `skills.` are what the system checker's host already reports
  (`CurrentEngineHost` in `tools/Codex.SystemCheck/SpecRunner.cs`), so P11a moves that mapping into the engine and the checker uses it
  from there. Values only: no grants cross the link, and nothing is written to the owner.
- **The formula functions read the current model from an ambient slot** (`SheetModel.Need()`). Working out the linked sheet inside a
  `link()` call runs a whole engine pass in the middle of the outer formula, so it must put the outer model back when it returns, on
  an exception too. A test reads two linked values in one formula, and a kind value after them, and checks all three.
- Each linked sheet is worked out once per evaluation (the existing `_linkModels` cache).
- An unknown link (no sheet supplied) keeps CL1's rule: nothing the link made is swept or clamped.
- A name `link()` cannot find gives 0, as now. A load check refuses a `link()` name that neither the linked kind nor the rule
  engine's value names know.

## Data (four-degrees `system.yaml`)

```yaml
sheetRules:
  kinds: [character]

sheetKinds:
  - kind: character
    # ... as now, plus the companions that link to it:
    layout: ...      # adds { widget: members } in a Companions section, if the character kind gains a layout; else the default sheet shows it

  - kind: companion
    label: Animal companion
    role: player
    partyMember: false
    links:
      - { key: owner, kind: character, label: Owner }
    lists:
      - key: type
        label: Type
        type: companion
        rules: [{ check: "count(type, t => true) <= 1", message: "a companion has one type." }]
    values:
      - { key: level, label: Level, formula: "link('owner', 'level')" }
      # Attributes: the type's starting modifiers, plus the stage's increases (from the rule text).
      # Hit points: the type's number, plus a number per level (from the rule text).
      # Ranks in AC, saves, Perception and attacks by stage (from the rule text), each worth level + the rank's step.
      # Each value names its `modifiers:` target so conditions reach it (ac, saves, perception, strike, skill checks).
    tracks:
      - { key: HP, label: Hit Points, max: "...", display: number }
    # The stage: a dropdown on the sheet (young, mature, and the specialised stages). The player sets it when a feat unlocks it.
    layout: [ ... the type, the stage, the numbers, the attacks as roll widgets, conditions ... ]

  - kind: familiar
    label: Familiar
    role: player
    partyMember: false
    links:
      - { key: master, kind: character, label: Master }
    values:
      - { key: level, formula: "link('master', 'level')" }
      - { key: ac,    formula: "link('master', 'armorClass')" }   # stored by the character rules already
      # The saves the rule text gives it from its master: link('master', 'saves.fort') and the others (Gap 2).
      # Perception and the checks the rule text sets from the master's level and spellcasting.
      - { key: abilityCount, label: Abilities per day, formula: "... + modifiers('familiarAbilities')" }
    lists:
      # Picked at the daily preparations: the choice holds until the next one (changes_on dailyPrep).
      - key: abilities
        label: Abilities
        type: familiar-ability
        rules: [{ check: "count(abilities, a => true) <= abilityCount", message: "more abilities than {abilityCount}." }]
    features:
      - { list: abilities }
    tracks:
      - { key: HP, label: Hit Points, max: "...", display: number }
```

Shapes the builder confirms or adapts (none is checked against the loader yet):
- **A pick list's size** has no `max` today, so both limits are list `rules`. The builder confirms a list rule can read a kind value
  (`abilityCount`) and puts the count in its message.
- **`changes_on` on a whole pick list**: the existing mechanism is per choice (`changes_on__*`). If it does not cover a list, the
  builder extends it, or lets the list be edited at any time with a note in the layout's help text.
- **The track key `HP`**: the table's down rule and the HP bar read `HP` and `HP_Max` (`resources`). Both kinds use those keys, so a
  companion at 0 goes down as any creature does.
- **Attacks**: the type's `attacks` rows become strike widgets with the companion's attack bonus. If a roll widget cannot take a row's
  bonus, the builder adds a kind-level `rolls` list, as `sheetRules` has.

## At the table

- **Its own combatant.** A companion or familiar is an actor, so the GM adds it to an encounter like any other. It has its own row,
  HP bar, conditions and turn. It does not join on its own when its owner does; that can come later.
- **Owned by the player, but not a party member.** A kind's `role` only says which kinds the new-actor picker offers for an actor of
  that role (`SheetLayout.KindsFor`). What makes an actor a player character is its `ActorKind`, and about 70 places read
  `ActorKind.PlayerCharacter`: who may edit it, but also party counts, the hero points at the start of a session, and the party size the
  encounter builder will use. So:
  - A companion is a `PlayerCharacter` actor, so its player can edit it, as the CL design assumed for a crew's members.
  - Its kind says `partyMember: false`. P11a lists the places that count the party and makes them skip such kinds; it does not change
    the ones that are about access.
  - The companion and familiar kinds come after `character` in `sheetKinds`, so a new player character is still a character by default.
- **Actions are text.** A minion acts when its owner spends an action to command it. The table does not count actions for anyone,
  so this stays a line on the sheet, not a rule.
- **Support** and the other benefits a type gives its owner stay the type's text. Nothing reaches across the link from the
  companion to the owner (links are read-only and one-way).

## Not in this step

- **Checking the stage against the owner's feats.** `link()` reads numbers, not feats. The stage stays the player's pick, with no
  warning when no feat allows it. A `has_feat` read across the link would be a later step.
- **Specific companions** a class or feat grants (a unique bond creature): their entries can be written later as `companion` entries.
- **Joining an encounter with the owner** automatically.
- **srd5e companions** (a ranger's beast): this design does not need them, but the kind and `sheetRules.kinds` fit them later.

## Tests

- **Load:**
  - `sheetRules.kinds` with an unknown kind is refused;
  - `link()` to a character name the rule engine knows loads, and an unknown one is refused.
- **Gap 1:**
  - a companion sheet gets no ancestry, class or boost rules, and no character hit points;
  - a character sheet is unchanged, and one with no stored kind key still gets the character rules;
  - the four-degrees system loads with a companion value named `ac` beside the character rules' AC;
  - the srd5e and fitd goldens are byte-identical.
- **Gap 2:**
  - a familiar's AC follows its master's AC (an armor change on the master moves it);
  - no owner sheet supplied: the familiar's link is unknown, and its stored values and picks stay.
- **Conditions:**
  - frightened on a companion lowers its checks and AC once;
  - a condition modifier whose target the companion lacks reaches nothing and does not throw.
- **Companion:**
  - its level, attributes and hit points follow its type, its stage and its owner's level, with numbers from the rule text;
  - changing the owner's level moves it.
- **Familiar:**
  - it can pick no more abilities than its count;
  - a feat that raises the count lets it pick one more;
  - the picks hold until the daily preparations.
- **Table:**
  - a companion at 0 HP goes down;
  - the owner's sheet lists it;
  - it gets no hero point at the start of a session, and the party count leaves it out.
- **Strings** in en, ru and he for the new labels; ru and he need native review.

## Build order

| # | Step | Who | Notes |
|---|---|---|---|
| P11a | Engine: `sheetRules.kinds`, kind-only evaluation of other kinds, condition routing and the both-claim rule on them, `link()` reading the rule engine's values, `partyMember`, load checks | Sonnet; Opus reviews | Touches `SheetRuleEngine.cs`, `SheetModel.cs`, `SheetConditions.cs`, `SystemDefinitionLoader.cs`, the party counts. Goldens must not move |
| P11b | Data: the companion and familiar kinds in `system.yaml`, their layouts, the numbers from the rule text, ru and he labels | Sonnet; Opus reviews the numbers against the rule text | After P11a. The rule text goes to the builder on its command line, never into a file |
| P11c | The shapes the data needs that P11a did not cover (a list rule reading a kind value, `changes_on` on a list, kind rolls), and the table tests | Sonnet | Only what P11b turns up |

## Decisions (2026-11-22, to confirm)

1. Companions and familiars are two kinds, each an actor of its own, linked to the character. Each is a player-character actor whose
   kind is not a party member.
2. The character rules are limited to the kinds they name (`sheetRules.kinds`). Every other kind runs its own model only.
3. `link()` reads what the character rules work out, values only, one step deep.
4. The stage is the player's pick. It is not checked against the owner's feats.
5. Numbers come from the rule text at build time.

## P11a as built

The engine half. Tests: `tests/Codex.Tests/CompanionKindsEngineTests.cs` (a small invented system, not the shipped data); the four-degrees `system.yaml` gained `sheetRules.kinds: [character]` and nothing else.

- **`sheetRules.kinds`** (`SheetRules.Kinds`). `SheetRuleEngine.AppliesTo(system, kind | sheet)` is the one test, and it finds the kind through `SheetLayout.KindOf`, so a sheet with no stored kind key is the first kind. On a sheet the rules do not apply to, the engine hands the call to a second, kind-only engine of the same system (`Apart`, `KindOnly`), which behaves as a system with no `sheetRules`: `Evaluate`, `Recompute`, `InitializeSheet`, the feature collection (`ActiveFeatures`, `Bonuses`, `OptIns`), `ApplyGranted`, `ApplyInherited`, `ApplyAttributeTotals`, `ApplySlotPools`, `Printed`, `ConditionBonus`, `EntryPicks`. `SheetModel` keeps only the rules' lists and slot pools for kinds they apply to. Left alone on purpose: `MultipleAttack` and the weapon lookup in `SheetModel` (roll helpers, not the build). `StripGranted`, `ApplyHitPoints`, `ApplyArmorClass`, `ApplyTracks`, `ApplyCounters` and `ApplyChoiceMoments` need no handing over: they act only on what the evaluation or an existing key holds.
- **Conditions.** No new routing code was needed: `SheetModel` already stacks every condition modifier of the sheet onto the kind's values by target, and the sheet-rules side takes none on a kind-only engine. A target the kind lacks reaches nothing and does not throw. `SheetConditions.KindTargets` takes an optional kind filter; the loader's both-claim rule and `PrintedChecks` pass the kinds the rules apply to, so a companion's own `armorClass` or `ac` is no clash. The list-name clash check (`sheetRules.lists` against a kind's list) is still against every kind.
- **`link()`**. `SheetModel.LinkValue` reads, in this order: `SheetEvaluation.WorkedOut()` of the linked sheet when the rules apply to its kind, then the kind's own names and stored keys as before (`level`, `armorClass`, `HP_Max`). The names `WorkedOut()` gives: every `Derived` number (the system's derived values, and a published ranked list's score under its own key: `perception`, `spellAttack`, `spellDc`, `classDc` in four-degrees), `skills.<key>` and `saves.<key>` (the bonus). The `skills.`/`saves.` mapping moved from `SpecRunner` into `WorkedOut()`, and `SpecRunner` calls it. The rule engine runs only for a name only the rules work out (`SheetRuleEngine.WorksOut`: a derived key, a published ranked key, `skills.`, `saves.`); every other name (`level`, `armorClass`, a value of the kind) reads the plain linked model with no pass. One evaluation per linked sheet per `SheetModel`, and the models one `Recompute` builds (the evaluation and the track maxima, the prerequisite checks) share one `SheetModel.LinkCache`, so it is one per linked sheet per Recompute; its own links are not followed. The outer `_current` model is saved and put back in a `finally`. An engine failure on the linked sheet reads 0 for the names only the rules give (the kind's own names still read) and adds a warning, `link:{key}`, to the outer evaluation. The linked sheet's kind is found through `SheetLayout.KindOf` (`SheetLinks.Linked(system, ...)`), so an owner saved before kinds (no `kind` key) is the default kind and reads. An unknown link is unchanged: no sheet supplied, nothing swept or clamped.
- **Load checks.** `sheetRules.kinds` with an unknown kind is refused. `link('key', 'name')` with a literal name is refused when its root (the part before the first dot) is none of: a value, track (and `_Max`), list or keyed list of the linked kind; an attribute, actor field, resource, valued condition or the level field; and, for a kind the rules apply to, a derived or ranked key, `skills`/`saves` (when the rules have skills), the hit point or armor class key, a slot pool, a rules list or the inventory key. The check is for typos, not for stored keys the system never names; it cannot see a name built in a formula.
- **`partyMember`** (`SheetKindDefinition.PartyMember`, default true). `SheetLayout.IsPartyMember(system, sheet)`; `SystemMoments.RunsHandler(system, sheet)` is the one rule: the system's `events.<moment>` handler runs only for a member of the party (the handlers are the party's, such as the session-start hero point); a non-member still gets its own sheet's recoveries (counters, slot pools, `changes_on__` choices, its tracks) at every moment. Applied in the same way on all three paths: `MomentCommand.Apply` (the handler is skipped for a non-member), the sheet page's `FireMomentAsync` (same), and the party bar through `SystemMoments.ReachesPartyMember(system, sheet, moment)` (dawn reaches every character; a member gets any moment that matters to it; a non-member only one its own sheet keys wait for). `SystemMoments.Matters` (so the moment's button on the sheet and the table) counts the handler only where it runs. Changed: `CombatConsole.razor` `Matters` and `MomentAsync` (the party bar's moments), and `TableNotesDrawer.razor` (the "The party" group of who can join a conversation, now with a `Definition` parameter from `TableNotesHost`). Looked at and left: party rest and `OfferPartyChoices` (a rest should heal a companion too), `CardActors` and the card toggles, `HazardsTable`, `CastSection`, `LoreSection`, `ItemsSection`, `PlayedCharacters`, `BookCompiler`, the death-save flag in `TableRules` and `InitiativeRow`, and every `RavenActorRepository` check (access and ownership). `rules.js` and the script host do nothing for every actor: the session-start hero point is the declarative `events.sessionStart` handler, which the console sends per actor. There is no party size, XP or encounter-budget code yet (the encounter builder is only designed), so it must use `IsPartyMember` when it comes.

A kind with its own `layout` shows only the actor fields it names: `SheetLayout.Generic` and a declared kind layout never add the built-in `fields` widget, so a companion shows none of ancestry, class or faith unless its layout lists them.

Where it differs from the design: the kind-only mode is a second engine instance, not a flag read in each pass; condition routing needed only the loader change; the `link()` cache is shared across the models of one `Recompute` (the engine's own), not across a host's separate calls (the sheet page's roll and view models each repeat the evaluation when a rules-only name is read).

Also changed by the review: the sheet page and the table read the character rules only for a sheet of a kind they apply to (`SheetRuleEngine.AppliesTo`): `ActorSheet.Rules` (the header armor class, the hit point, armor class, slot and picks panels, the guards, the opt-ins; the conditions panel keeps reading `system.SheetRules`), `AttributesPanel`, `TableCard`, and `CheckService.PrintedArmorClass`. `PruneChoices` goes through the kind-only engine like the other entry points. `LibraryReferenceCheck` (`WorldStructureChecks.cs`) checks the inventory of every sheet the rules do not run on, so a companion's inventory is no longer checked by nothing. The "reaches nothing" warning for a feature's modifier (`UnreachedKindModifiers`) uses the same per-kind filter as the both-claim rule. Not done: no bUnit test was added for the sheet page or the card (no test of those components reads `Rules` for a kind), and the page's generic-field hiding list (`hiddenKeys`) still names the rules' keys for every kind.
