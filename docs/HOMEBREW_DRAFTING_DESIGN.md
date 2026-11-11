# Homebrew in drafting, the feature editor and the join review: design

Written 2026-11-11 on branch `feat/overhaul-character-creation-homebrewing`. It plans the work behind step 4 of
[CHARACTER_FEATURES_GAPS.md](CHARACTER_FEATURES_GAPS.md) ("the drafter proposes homebrew when nothing fits"), which is
built for subclasses only, and the editor and join review work that goes with it. Nothing here is built yet unless it says so.

## Where it stands

- `AiFeatures.ChooseAsync` asks the model for the picks the rules leave open. With `propose`, a reference field the model
  leaves empty (the subclass) is written by `ProposeAsync`, put on the sheet and in `CharacterDraft.Proposed`, and becomes
  `ActorDocument.Carried` on the vault character.
- `CanPropose` accepts only rules a property can be set to meet (`equals`, `equalsField`), and only on reference fields.
  Lists (spells, feats, fighting styles) are never written.
- The join review already covers list picks: `SheetReferences.Of` walks lists, so a carried spell or feat reaches it.
- `ParseChoices` already takes fewer picks than a list's count, so "nothing fits" for a list means "picked fewer".
- The engine knows when a subclass is due (`multiclass.subclassLevel`). Below it nothing is asked, which is right. A class
  with no published subclass gets no options and is skipped, which is the gap.
- The writer is told about `modifiers`, `adds` and `uses` (`FeatureGrantGuide`), not about `use`, so a written spell or
  weapon rolls nothing.

## 1. Drafting: writing entries for lists

1. **Rules the writer can meet.** Turn the easy conditions into properties set on the entry: `equals`, `in` (the first
   value), `gte`, `lte`, `equalsField`, `containsNameOfField` (the named entry's name in a list), `lteField` (the sheet's
   value). Pass `lteLookup` as a hint ("at most 3"). `equalsPropertyOfField` (the origin feat a background names) cannot
   be written for and stays out.
2. **Check with the engine.** Put the written entry in the resolver (promote the test helper `WithProposed`), evaluate the
   sheet, and drop the entry if the rules refuse it. Hand-mapping is only a head start; the engine has the last word.
3. **Leave slots open.** A list's choice hint says to leave slots open when nothing suits; the open slots are written for
   afterwards and added to the list and to `Proposed`.
4. **Cap the calls.** At most one written entry per list and two or three per draft. A high-level caster must not turn into
   ten model calls.
5. **Teach `use`.** A guide beside `FeatureGrantGuide` for the types `sheetRules.actions` reads (spells, items with the
   weapon tag), from the shape documented on `ActionSource`. It goes to `WriteEntryAsync` and to the world builder
   (`WorldBuilderService.Reading`). This comes before step 3, or written spells roll nothing.
6. **Subclass for a class with none.** With no options and the class at or above `subclassLevel`, write one without an
   example to copy the build from.
7. **"Always write one".** Replace `bool propose` with a mode (off, when nothing fits, always) and add the switch to the
   vault draft dialog, with en/ru/he strings.

## 2. Where written entries go

- **Vault (built):** the character carries them; the GM reviews them when it asks to join.
- **Cast "Create with AI" and "Promote to cast", GM:** cheap. Write them straight into the campaign's homebrew after a
  confirm that names them.
- **The same, player in a campaign:** needs a GM approval queue inside the campaign, close to a second join review.

Decided: vault only for now. The GM path can follow; the player path waits for the join review work below.

## 3. Guidance per list ("additional prompt"), with invocations as the test case

A prompt alone does not make invocations work: there is nowhere on the sheet to put them, and most of what they do the
sheet cannot apply. What it takes, generic and in data:

**Content and system data**

1. **A class-option type** for "pick some from your class's menu" (invocations, maneuvers, metamagic): classes, kind,
   minimum level, prerequisites, repeatable.
2. **An `invocations` list**: the right kind, on your class, minimum level at most your class level; the count from an
   `invocations` column in the warlock's class table, written by the importer.
3. **`{ list: invocations }` in `features`**, so their `modifiers`, `adds` and `uses` apply.
4. **Import the published options if the source has them.** The SRD 5.2.1 is believed to list Eldritch Invocation options;
   the pack does not have them as entries. Check the pinned source first: real entries beat homebrew and give the writer
   something to measure against.

**Drafting**

5. **Write for a list with a count and no options at all** (the warlock today). It is skipped now.
6. **A `homebrew` block on a list in `system.yaml`**: `guide` (the system author's advice on what such an entry is and how
   strong it should be at a level) and `anchors` (how many published entries of the type near that level are shown in full).
   The draft dialog gets a free-text box ("homebrew an invocation that lets her speak with crows") passed to the writer.
7. **A dice check where there are dice.** For an entry with `use`, compare its average dice with published entries of the
   same type and level; above the strongest, retry once, then drop it.
8. The engine check from part 1, and the GM's review on joining, stay the last line.

**Will it be balanced?** Plausible, not guaranteed. With anchors, the dice check and the GM's review it is acceptable.
With no published entries to anchor on, it rests on the guide text and is weaker.

**What invocations need from the sheet.** Typical ones add an ability to one spell's damage or let you cast a spell at
will. Neither can be expressed now (see "What a feature cannot express yet" in the gaps doc). For them to work, not just
be listed:

- a modifier that targets one action's attack or damage;
- `adds` that give a spell at will, or a number of free castings per rest, instead of spending a slot.

## 4. Structured spells

Spells are half structured: level, school, range, duration and the like are fields; rolls are `use` (176 of 339 spells);
the effect is prose.

- **Needed:** the `use` guide for the writer (part 1, step 5) and the dice check (part 3, step 7).
- **Worth doing:** a count in `use` (beams, darts, rays) and how it grows with level. Fixes Eldritch Blast, Magic Missile
  and Scorching Ray for published spells and homebrew alike; the importer can read counts from the text.
- **Not now:** areas, targets, conditions applied, outcomes. A large engine change, and those stay the GM's ruling.

## 5. The feature editor

The homebrew wizard's collection editor (`SchemaForm.RenderCollection`) edits a row's name, level and description. It
keeps `modifiers`, `adds` and `uses` but cannot show or change them.

1. **Shared vocabulary.** `FeatureGrantGuide` works out the targets, formula names, lists and rest names in private
   methods. Make that a public vocabulary the guide and the editor both read, so the editor offers exactly what the model
   is told.
2. **A `FeatureGrantEditor` component**, under a collapsed "What it does on the sheet" section per row:
   - modifiers: target (from the vocabulary), amount (number or formula), type, condition;
   - adds: list, entries, from level;
   - uses: maximum (number or formula), rests that refill it;
   - formulas checked with the engine's parser, with the error beside the field.
3. **The same block for a whole entry** where the system reads the entry itself as a feature (a feat), not its rows.
4. en/ru/he strings, keyboard and screen reader friendly, and tests.
5. Later: a matching editor for `use` on spells and weapons.

## 6. The join review

| Problem | Change | Size |
|---|---|---|
| The GM cannot edit an entry in the review | "Edit" opens it in the feature editor; the change is kept on the request's `Brings` before Accept | Medium, needs part 5 |
| "Use one the campaign has" lists every entry of the type | List only what the rules allow for the request's sheet (allowed options for a field, list options for a list) | Small |
| The player is not told what was left out or swapped | Keep the outcome on the request (name; added, swapped for what, left out) and show it on the vault character | Small to medium |
| The carried entry stays and a second campaign asks again | Mostly right: each campaign has its own Library. At most, show which campaigns took it | Small, low priority |
| No UI for a vault character's carried entries | A panel to see, edit (feature editor) and drop them, warning when the sheet still uses one | Medium, needs part 5 |

## Order and who does it

1. Feature editor (part 5): the join review's edit and the carried-entries panel reuse it. Sonnet. Built for items 1 to 4 (vocabulary, `FeatureGrantEditor`, wired into the wizard's rows and whole-entry features, en/ru/he strings, tests); item 5 (a `use` editor) is not.
2. Drafting for lists (part 1), `use` guide first. Sonnet; the "always write one" switch and strings can go to Haiku.
3. Join review (part 6). Sonnet; the outcome notice and strings can go to Haiku.
4. Invocations (part 3): check the source, then the type, list and column; the sheet changes for spell modifiers and at-will
   casting are their own piece. Sonnet, with the rules design reviewed first.
5. Spell counts (part 4). Sonnet.

ru and he wording needs a native review whoever writes it.
