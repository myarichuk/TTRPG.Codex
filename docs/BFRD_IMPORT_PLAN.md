# Black Flag Reference Document: licence, source and gaps

Written 2026-11-12, before any code. It answers two questions: may we ship the BFRD, and what does our system model
lack to play it. Nothing here is built yet.

## 1. Licence: clean, under CC BY 4.0

- The BFRD comes in **two editions**. One is under the **ORC License**: v0.1 (October 2023) and v0.2 (May 2024). The
  other is under **CC BY 4.0**: 1.0 (July 2025), 1.1, and **1.2** (26 August 2026, 414 pages). Kobold's page says you
  may work from either one.
  - Page: https://koboldpress.com/black-flag-roleplaying/
  - CC 1.2 PDF: https://koboldpress.com/wp-content/uploads/2026/09/Black-Flag-Roleplaying-v1-2.pdf
  - Kobold's CC FAQ v2.3: https://koboldpress.com/wp-content/uploads/2026/09/Black-Flag-Creative-Commons-FAQ-v2-3-1.pdf
- **We use the CC 1.2 edition.**
  - It is the same licence as our SRD 5.2.1, so the two sit together with no conflict.
  - It has no Reserved Material and no share-alike. ORC would have made a generated pack "Adapted Licensed Material",
    which we would then have to offer under ORC too.
  - We owe attribution only. The memory rule "no ORC data" therefore still holds.
- **What CC does not cover:** art, maps, trade dress, trademarks and logos.
  - Kobold Press, Tales of the Valiant (and TOV), its logo and Midgard are trademarks.
  - The rules text of 1.2 names none of them except in its copyright line.
- **Attribution.** We carry Kobold's exact sentence, plus the SRD 5.1 credit that the BFRD carries itself, and mark the
  text modified (`modified: true` with notices, as in fitd-srd):
  > This work includes material taken from the Black Flag Reference Document 1.2 ("BFRD 1.2") by Kobold Press and
  > available at https://koboldpress.com/Black-Flag-Roleplaying. The BFRD 1.2 is licensed under the Creative Commons
  > Attribution 4.0 International License available at https://creativecommons.org/licenses/by/4.0/legalcode.
  > Black Flag Roleplaying Reference Document v1.2, © Open Design LLC d/b/a Kobold Press
- **Naming.** We name the system after the document, as with "Fifth Edition SRD (5.2.1)": something like
  **"Black Flag Reference Document (1.2)"**.
  - We never write "Tales of the Valiant" in the UI, the pack or the AI vocabulary.
  - We add a "not affiliated with or endorsed by Kobold Press" line.
  - We skip Kobold's compatibility-logo licence. It needs an application, does not list software, and rules out
    standalone systems.
- **Fix needed elsewhere:** `docs/AUDIT_2026-10-18.md` calls the BFRD CC BY 4.0. That is true only from 1.0 on.

## 2. Source: the PDF, read by an importer

| Source | Licence | Covers | Verdict |
|---|---|---|---|
| BFRD 1.2 PDF (Kobold) | CC BY 4.0 | everything below | **Use it** |
| Open5e API v2 `data/v2/kobold-press/bfrd/` | tagged `cc-by-40`, but its description says "v0.2", an ORC-only edition | classes, features, creatures only | Do not build on it until its source edition is confirmed. At most, use it to cross-check stat blocks |
| Foundry `koboldpress/black-flag` `packs/_source` | ORC | all of it, structured | **No**: ORC data |
| `foundryvtt/black-flag` (2023) | forbids redistribution | | No |

**What the 1.2 PDF holds:**
- 16 classes, each with one subclass (cleric has two). Barbarian, bard, cleric, druid, fighter, mechanist, monk,
  paladin, ranger, rogue, sorcerer, theurge, vanguard, warlock, witch, wizard.
- 6 lineages and 9 heritages.
- Only 3 backgrounds: Criminal, Scholar and Soldier.
- Magic, martial and technical talents.
- Spells by source list (Arcane, Divine, Primordial, Wyrd) and rituals.
- Equipment and about 35 pages of magic items, plus firearms, siege weapons and vehicles.
- Gods, the play rules, and GM rules: Attitude, Dread, hazards, optional Doom.
- At least 230 stat blocks (a header count; the exact number comes with the importer) and the conditions.
- It is a subset of the published books, not the whole Player's Guide or Monster Vault.

**The importer.** We add a `bfrd` command to `tools/Codex.ContentImport`, beside `fitd`.
- **Pinning.** Pin the PDF by URL **plus SHA-256**. Kobold can replace a file at the same URL, which a pinned git
  commit cannot.
- **Text extraction.** Read the text with a library whose licence is compatible; PdfPig (Apache-2.0) is the likely pick.
- **Text fixes.** We need a fix-up pass. Extraction splits glyphs ("Y ou", "T alents", "T ongues") and runs the two
  page columns together. This is the same kind of work `SpellTextFixes.cs` does.
- **Progression tables.** These come out of the PDF as jumbled columns. Like `Data/srd521-class-tables.json`, they are
  best kept as a hand-checked JSON file beside the importer.
- **Shipping.** Release it as its own archive like fitd-srd, not in `bundled.txt`, unless you want it bundled.

## 3. What our system model already covers, and what it lacks

I checked this against `docs/SYSTEM_YAML_REFERENCE.md` and the code (`RuleCondition` and `SlotCombineRule` in
`src/Codex.Plugin.Abstractions/SheetRules.cs`, `SheetRuleEngine.Actions.cs`, `VitalsHandlers.cs`, `TableEvents.cs`).
srd5e's `system.yaml` is the starting point. Most of the BFRD is a fifth-edition-shaped game and fits as data.

### Fits as data (a new `system.yaml`, no engine work)

| BFRD rule | How |
|---|---|
| Ability scores: point buy with 32 points, 8 to 18 | `pointBuy` budget 32, max 18, costs 16→11, 17→13, 18→16 |
| Standard array 16/14/14/13/10/8 | `standardArray` |
| Proficiency bonus, skills, saves, passives, spell DC and attack | as in srd5e |
| Lineage plus heritage | Two reference fields and two `features` sources. `inherits` copies speed, size and darkvision from the lineage |
| Human's extra talent from any list | `extraOriginFeats`-style `maxCount.fromField` |
| Talents: magic, martial, technical | A `talent` entry type with `category`, listed under `lists` |
| Class level-up Improvement (+2, +1/+1, or +1 and a talent) | `increases` with improvement entries; see the talent-count gap below |
| Spell circles 0 to 9, schools | The `spell` type with `level` labelled "Circle" |
| Slots, and multiclass slots (half casters round **down**) | `slotPools` + `combine` (`rounding: down` exists) |
| Warlock: Wyrd slots as a half caster, refilled on a long rest, plus Pact Magic uses per rest | An ordinary slot pool, plus a feature row with `uses: { max, recoversOn: [Short Rest, Long Rest] }`. No `pact` pool |
| Rituals: no slot, a count known, one source list | Their own list and an action with no `spends` (`Spends` is false without it) |
| Multiclassing prerequisites and proficiencies | `multiclass`. The BFRD's table leaves out theurge, vanguard and witch; the importer fills them from each class |
| Hit points, hit dice, short and long rest, death saves (1 counts two failures, 20 brings you back with 1 HP), massive damage, monsters die at 0 | `vitals` as in srd5e |
| Exhaustion: 6 levels, −1 a long rest, death at 6 | `vitals.exhaustion` as in srd5e. Per-level effects stay text, as they do in srd5e |
| Conditions, damage types, equipment, armor, weapons, magic items, rules glossary | Types as in srd5e |
| Book shape for the AI | `ai.bookShape` / `playerBookShape` written for this system, in its own voice, with no trademark |

### Needs importer work only

| What | Why |
|---|---|
| Spells by **source**, not by class | Spells list Arcane, Divine, Primordial or Wyrd; classes cast from a source. Unless we add an engine condition (next table), the importer writes each spell's `classes` from its sources, so `containsNameOfField: class` keeps working. A homebrew class with a source would then not pick up its spells on its own |
| Stat blocks print **ability modifiers**, not scores (`+1 +3 +1 …`), give no hit dice, list Perception and Stealth as numbers, and say "Resistant <condition>" | The `monster` type's `attributeProperties` and `vitals.hitDice` expect scores and dice. The cheapest route is for the importer to write score = 10 + 2 × mod (exact for the modifier, and the modifier is all the stat block uses). Condition resistances go in a tag list as text. This covers every stat block |
| `use` data for spells and weapons | As `UseData.cs` does for the SRD, from the rules text |
| Class progression tables | Hand-checked JSON (see section 2) |

### Needs an engine feature

| Gap | What is missing | Effect without it |
|---|---|---|
| **"Candidate is in a list on the referenced entry"** | `RuleCondition` has `equalsPropertyOfField` (one value) and `containsNameOfField` (the referenced entry's name in the candidate's list), but nothing for the reverse: the candidate's `category`/`id` in a **list property** of the class or background | Three rules cannot be enforced: (1) a class's talent lists (martial; the rogue's and fighter's "martial or technical"; the bard's and the War cleric's expanded lists), (2) a background's three-talent menu (Soldier: Combat Casting, Combat Conditioning or Field Medic), (3) spells by source without the importer workaround. Proposed: `inPropertyOfField: { field, property }`, about 20 lines in `EntryRequirements.cs` and `SheetRuleEngine.cs`, plus the loader check |
| **Talents from Improvement** | The "+1 and a talent" option should raise the talent list's limit by one. `CountRule` cannot count picks of one entry in another list | The talent list has no limit, or the GM checks it |
| **Ability bonus tied to the method** | BFRD gives +2/+1 (to scores ≤16 and ≤17) only when rolling; point buy and the array get none. `BonusRules` is tied to a field (srd5e: background), not to a method, and has no "only up to N" cap | Rolled characters enter their bonus by hand under `manual` |
| **Luck** | A pool of 0 to 5. You gain 1 on a failed attack or save, once a turn. Going over 5 resets it to a d4 roll. You spend 1 point for +1 to a check, or 3 for a reroll | A top-level `resources` entry (`Luck`, max 5) works now as a counter the player moves by hand. The automatic gain needs the console's d20 check to raise an event the system can answer. That check is a plain die plus bonus (`Dice.Check` in `CombatConsole.razor`), and the table raises only damage, heal, turn and roll events. The d4 reset needs a script or a new overflow `then`. Spending would be a button on the roll result. Every PC has Luck, so this is the BFRD's signature rule |
| **Human size Small or Medium** | `inherits` copies one value | The player changes the size by hand (srd5e has the same gap) |

### Stays text (as the same kinds of rule do in srd5e)

Advantage from lineage and heritage traits. Doubling the proficiency bonus on specific checks (heritages, Arcanist).
Weapon options. Firearms, siege weapons and vehicles. Dread, Attitude and hazards. The optional Doom pool, which could
become a campaign table later. Gods and pantheons.

## 4. Proposed order

1. Write the `bfrd` system (`system.yaml` + `spec.yaml` for `Codex.SystemCheck`) using only what fits as data.
2. Build the importer: pin and hash the PDF, extract the text, apply text fixes, add the JSON tables, write the pack
   with attribution and LEGAL.md, and add a CI diff check as fitd has.
3. Add the engine features, in order of payoff: `inPropertyOfField` first (it unlocks talents, backgrounds and spell
   sources), then Luck, then the method bonus and the Improvement talent count.

Open question: do steps 1 and 2 first and leave the gaps as text, or add `inPropertyOfField` first so the first import
already enforces talents and spell lists?
