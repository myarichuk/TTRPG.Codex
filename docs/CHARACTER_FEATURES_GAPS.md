# Character drafting, feature grants and the Table: where it stands

Written 2026-11-11 on branch `feat/overhaul-character-creation-homebrewing`, while the work was uncommitted. It records the
path that was agreed, what was built off that path, and what does not work or cannot be expressed yet.

## The agreed path

Agreed after testing an AI-drafted warlock ("Ossic Varn") in the vault:

| # | Step | State |
|---|---|---|
| 1 | The drafter fills the pick lists (spells, feats) and checks subclass and origin feat against the rules | Built |
| 2 | Cast and attack actions: structured spell and weapon data, casting spends a slot and rolls into the dice log | Built (see below) |
| 3 | Table overhaul: initiative collapses outside an encounter; character cards showing AC, gear, spells and actions; GM sees all, players see only what is visible | Built (see below) |
| 4 | The drafter proposes homebrew when nothing fits; the character carries it; the GM approves on joining | Built for subclasses only |

Steps 2 and 3 are what the original complaints were about (no way to cast or mark a spell spent, the Table shows too little).
The work strayed from them into feature grants and the join review, then came back to them.

## Step 2: cast and attack actions

- **Entry data**: a spell or a weapon carries a `use` property (attack kind, abilities, saving throw, dice, damage type,
  healing, dice per higher slot, dice by character level, other dice such as two-handed). The SRD importer writes it
  (`tools/Codex.ContentImport/UseData.cs`) from the source's fields and the rules text; 176 of 339 spells and all 38
  weapons have one. The pack was regenerated from the pinned source; only spells and equipment changed.
- **System model**: `sheetRules.actions` names where actions come from (pick lists, equipped items with a tag), which slot
  pools pay, and formulas for attack bonus, save DC and damage bonus. srd5e declares `cast` and `attack`.
- **Engine**: `SheetRuleEngine.Actions.cs` fills `SheetEvaluation.Actions`: per entry the attack bonus, save and DC, the
  dice with bonuses in, and one cost per slot level that can pay, each with its own dice.
- **Sheet**: an "Actions" panel (built-in widget `actions`). Cast spends the chosen slot and rolls the attack, or the
  dice when there is no attack roll; Damage rolls the dice; a weapon's other dice get their own button. Rolls go to the
  session's log when one is live, else they are shown on the sheet.

What it does not do:

- No targets and no outcome: a hit, a critical, a failed save or half damage is the GM's to apply. A critical's extra
  dice are not rolled.
- One roll per click. Spells with several beams, darts or rays (Eldritch Blast, Magic Missile, Scorching Ray) do not
  know how many; scaling that adds targets or beams instead of dice is not read.
- 163 spells have no `use`: they are listed and casting them spends a slot, nothing rolls. Among them flat healing
  (Heal), and dice the text gives in a shape the importer does not read. Only the first dice in the text are taken, so a
  spell with a second roll (ongoing damage, a different die on later turns) rolls the first only.
- A spell whose damage type is chosen at casting (Chromatic Orb) has dice without a type.
- With more than one casting class, every spell uses the best class's attack and DC, not its own class's.
- Casting always spends a slot: rituals, a granted spell's free casting and at-will casting are not modelled.
  Casting does not put Concentrating on the caster.
- Weapons: range and thrown, ammunition, mastery, two-weapon fighting, a magic weapon's bonus, fighting styles and
  feats are not counted. Training is the first class's only. Unarmed strikes are not an action.
- A magic item is an action only if it has the weapon tag and a `use`; the SRD's magic items have neither.
- Homebrew: the world builder and the homebrew wizard are not told about `use`, so a homebrew spell or weapon is listed
  but rolls nothing unless `use` is written in its properties by hand.
- Spending a slot on the sheet page edits the sheet, like the slot tracker there; it is saved with the sheet.
- Damage types on an action (Fire, Slashing) are shown in English in every language: the system's translations have no
  words for them.

## Step 3: the Table

- **Initiative** is one bar outside an encounter, with "Start encounter" for the GM; it is the full panel once someone
  is in the order.
- **Cards**: one per character of the campaign, and per creature in the order. Everyone sees the top (portrait, name,
  hit points, conditions). The GM, and a player for their own character, also see armor class, speed, the system's
  derived values, spell save DC, equipped gear, slots, feature counters with Use and Regain, the actions with their
  buttons, and (GM) a creature's stat block. Other players see hit points as a band, as in the order.
- The separate slots panel is gone (slots are in the cards). The dice log, quick add, party rest and "End table" are in
  the side column.

What it does not do:

- Damage, healing and conditions are still applied from a creature's row in the order only, so outside an encounter the
  GM has no control for them on the Table.
- A creature's card shows its stat block as text; its attacks have no buttons (monster attacks are prose).
- A card's numbers come from the stored sheet and refresh when the Table reloads its actors; hit points, slots and
  counters are live.
- Rolls from a card are public; there is no secret switch on them.
- Which cards are open is not remembered across a reload.

## Built off the path

- **`:::dm` blocks in Library entries**: removed for players in the homebrew repository, drawn as GM blocks for the GM.
- **Feature grants** (`sheetRules.features` in `system.yaml`): a feature row or a feat may carry `modifiers`, `adds` and
  `uses`, and the sheet applies them. A "Features" panel on the sheet shows them. Counters sync to the live table and
  refill on the rests they name.
- **World builder guidance**: `homebrew_fields` tells the model how to write those three keys, with the system's own names.
- **Carried entries**: a vault character holds its own Library entries (`ActorDocument.Carried`).
- **Join review**: the GM's Accept is now a three-step review (character, what it brings, confirm) with add / use one the
  campaign has / leave out per entry.

## What a feature cannot express yet

A feature's `modifiers` reach only: armor class, maximum hit points, derived values (proficiency bonus, initiative) and
Number actor fields (speed, darkvision). `adds` reach pick lists without a count (granted spells) and the damage
resistance, immunity and vulnerability lists. `uses` make a counter. Everything else stays text:

- Skill, saving throw, weapon, armor, tool and language proficiencies, and expertise.
- Ability score increases from a feature (feats have their own path, features do not).
- Attack and damage bonuses, extra attacks, critical ranges: weapon attacks exist now (step 2), a feature cannot change them.
- Anything conditional on gear ("while wielding a longsword in two hands", "while wearing no armor" beyond the built-in
  unarmored base): a modifier's `when` reads the sheet's numbers, not the inventory.
- A feature that spends another feature's counter (Dusk Form costing 3 Sorcery Points). Counters are independent.
- Dice a feature owns (superiority dice, a bardic die): a counter can hold the count, nothing rolls them.
- Reactive rules ("when you would drop to 0 hit points, instead..."). Deliberately left as text plus a counter.
- Choices inside a feature ("choose one of the following"). There is no model for per-feature choices.
- Spell slots or pact slots granted or changed by a feature.
- A modifier to an attribute score, to a skill bonus, or to spell save DC and spell attack.
- Number-field formulas cannot read derived values (they are applied before the evaluation runs).
- A `base`-type modifier on armor class is ignored while body armor is worn, like a class's own defense.

Scripted features (Jint, which the engine already hosts for system authors) were considered and declined for entries a
model writes: not reviewable by a GM, not visible to the app, silent when wrong, and executable code in shared packs.

## What does not work now

Drafter:

- Proposals are written only for a single reference field whose rules can be met by setting properties (in srd5e: the
  subclass). Lists never get a written entry: no custom spell, feat, fighting style or invocation.
- A subclass is written only if the model leaves the choice empty. There is no "always write one" switch in the dialog.
- Proposals are on for vault drafts only. Cast's "Create with AI" and "Promote to cast" pick from the Library only.
- Nothing is written below the subclass level, or for a class with no published subclass to decline.
- Eldritch Invocation options are not Library entries in the SRD pack, so nobody can pick them, by hand or by AI.
- One extra model call per drafted character with something to choose, and a second when a subclass is written.

SRD content:

- No published entry carries `modifiers`, `adds` or `uses`. The SRD pack is generated; the generator writes `use` on
  spells and weapons (step 2) and nothing for features. So the Fiend Patron's spells, a Dwarf's resistance and every other published feature are still text only.
  (`hitPoints.perLevelBonus` and `armorClass.bonusLists` still do their older, narrower jobs.)

Sheet and table:

- The sheet's Features panel lists counters and granted lines; it does not list plain features with their text.
- At the table, feature counters have Use and Regain on the character's card (step 3).
- The homebrew wizard's collection editor shows a row's name, level and description, and under a collapsed "What it does
  on the sheet" section the row's `modifiers` (target, amount, type, condition), `adds` (list, entries, from level) and
  `uses` (maximum, rests that refill it), offered from the system's own vocabulary (`FeatureGrantVocabulary`) and with
  formulas checked by the engine's parser. A feat-like entry, where the whole entry is the feature, gets the same section
  once, after its fields. Not yet: the same editor on the actor sheet, and a `use` editor for spells and weapons.
- A monster's trait and action rows render inline text, so a `:::dm` block inside one would show raw (GM only).

Join review:

- The GM cannot edit an entry inside the review; changes happen outside the app and the player asks again.
- "Use one the campaign has" lists every entry of the type, not only those the sheet's rules would allow.
- The player is not told which entries were left out or swapped.
- The carried entry stays on the vault character after it is accepted; a second campaign is asked about it again.
- No UI lets a player see, edit or drop the entries a vault character carries, apart from changing the sheet.

Found on the way, not fixed:

- The Quick-add hint says creatures are "added hidden"; they are added as visible strangers.
- The SRD Mage's Spellcasting action is missing its spell list (`monsters/mage.yaml`).
- Monster attacks are prose only.

## Not verified

- Only the homebrew wizard's feature editor was checked in the running app (a subclass's rows and a feat, saved and
  reopened, keyboard and phone width). No other page was opened on this build.
- No real model was called: every AI path is tested with canned replies only.
- Browser end-to-end tests and the book print tests cannot run on this machine (Playwright's Chromium is missing). Two
  end-to-end tests were edited for the collapsed initiative bar (they click "Start encounter" first) and never run.
- The Actions panel and the Table cards were never looked at in a browser: layout, spacing and the phone width are untested.
- Hebrew strings were written without a native review.
