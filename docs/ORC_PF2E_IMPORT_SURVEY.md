# Pathfinder Second Edition Remaster pack: import survey

Written 2026-11-13 on branch `claude/exciting-keller-1af6u9`, after [ORC_PF2E_HANDOFF.md](ORC_PF2E_HANDOFF.md). This is a survey, not a
design: what the two purchased books contain, how to get it out of the PDFs, what must be filtered, and what the engine can and cannot
express yet. Nothing here is built. Not legal advice.

All findings come from a local, throwaway text dump (PdfPig 0.1.16, the repo's own version) kept in the session scratchpad. No PDF
text, page images or statblocks are in this repo or in this document. The examples below are synthetic.

## 1. The books we hold

| | Player Core | Monster Core |
|---|---|---|
| Product code | PZO12001E | PZO12003E |
| Bought from | Paizo's store, as PDFs (Remaster line) | same |
| Pages | 466 | 378 |
| Printing | Second printing 2026 (PDF built Jan 2026) | First printing (PDF built Mar 2024) |
| ORC notice | PDF p. 464 (printed 463). **Verified** | PDF p. 373. **Verified** |
| Expressly Designated Licensed Material | None | None |
| Reserved Material clause | Same text in both: trademarks, proper nouns (characters, deities, locations, etc., and adjectives, names, titles and descriptive terms derived from them), artworks, characters, dialogue, locations, organizations, plots, storylines, trade dress | same |
| Requested credit line | "Pathfinder Player Core © 2023 Paizo Inc., Authors: ..." (full author list in the notice) | "Pathfinder Monster Core © 2024 Paizo Inc., Authors: ..." |
| Bookmarks | 1,484 (chapter → section → entry) | 655 (letter → family → creature) |
| Encrypted | No | No |
| Personalized watermark | **None found.** The order number does not appear in any page's text, the raw file bytes or the annotations; the buyer's name and email do not appear in any page's text (Player Core's 1,522 annotations are all internal links; Monster Core has none). Text inside images was not checked | same |

PDF page = printed page + 1 in both books.

The pack's notice should copy each credit line, with its full author list, **from the extracted notice text by the importer**, not
by hand. That closes handoff §5 item 5 for these two books and handoff §8's watermark and Monster Core notice items.

GM Core is **not** held. Player Core points into it 10 times ("GMC 262", "GMC 282-283"), mostly for magic items, runes and
wands. The DC-by-level table, encounter budgets, treasure and most magic items live there. See open question 1.

## 2. What is in the books, and what the pack takes

### 2.1 Player Core (PDF pages)

| Chapter | Pages | Take | Entity type(s) | Notes |
|---|---|---|---|---|
| 1 Introduction: key terms, format of rules, character creation, leveling | 6-31 | Rules text, the creation steps, the level table | `rule`, system.yaml | "Example of Play" (13-14) is narrative: drop |
| 1 Setting: map, regions, example characters, regional languages | 32-35 | **Drop whole** | none | All Reserved: places, peoples, adjectives |
| 1 Religion: deities and devotee benefits | 36-41 | **Drop names and flavor; keep the shape** | see 4.2 | Mechanics bound to proper nouns |
| 2 Ancestries (8), versatile heritages, backgrounds, languages | 42-91 | Ancestry stats, heritages, ancestry feats, backgrounds | `ancestry`, `heritage`, `feat`, `background`, `language` | Each ancestry opens with flavor/society pages: drop |
| 3 Classes (8), companions, familiars, archetypes | 92-225 | Class tables and features, class feats, companion and familiar stats, multiclass archetypes | `class`, `feat`, `companion`, `familiar-ability`, `archetype` | "Sample" sidebars name deities and places: drop |
| 4 Skills | 226-249 | Skill actions and DCs | `skill-action`, system.yaml skills | Very clean |
| 5 Feats (general and skill) | 250-267 | All | `feat` | Cleanest chapter |
| 6 Equipment | 268-297 | Armor, shields, weapons, gear, weapon traits, critical specialization | `armor`, `shield`, `weapon`, `gear`, `trait` | **Tables**, not headed entries (see 3.3) |
| 7 Spells: rules, lists, spells, focus spells, rituals | 298-397 | All | `spell` (cantrip, focus and ritual are kinds), `trait` | A few phrase-level redactions (4.3) |
| 8 Playing the game | 398-442 | Rules text | `rule`, system.yaml | Very clean |
| Conditions appendix | 443-448 | All | `condition` | Clean; needs valued conditions (5.2) |
| Character sheet | 449-452 | Layout reference only | none | Trade dress: never copied |
| Glossary and index | 453-463 | Trait definitions; the term list for the denylist allowlist | `trait` | Strip page numbers |

Approximate entry counts from header lines (a header that wraps is missed, so treat these as floors): **287 spells, 40 cantrips,
124 focus spells, 840 feats.** The smoke test should pin the exact numbers once the parser runs.

### 2.2 Monster Core

| Section | Pages | Take | Entity type(s) |
|---|---|---|---|
| Introduction: reading statblocks, elite/weak adjustments | 6-8 | Rules | system.yaml (elite/weak as a creature adjustment) |
| Monsters A-Z | 9-358 | Statblocks and abilities. **Drop** family lore paragraphs and the boxed sidebars (lair, treasure, society) | `creature` |
| Ability glossary | 359-361 | All | `creature-ability` (Grab, Engulf, ...: statblocks reference them by page) |
| Creature traits, rituals, languages | 362-366 | All | `trait`, `spell` (rituals) |
| Creatures by type and by level | 366-372 | Index only; regenerate it from data | none |

About **443 creatures** by header count.

## 3. How we extract

### 3.1 What the text looks like

The text layer is clean. Action glyphs extract as `[one-action]`, `[two-actions]`, `[three-actions]`, `[reaction]`, `[free-action]`
(and `[one-action] TO [three-actions]` for variable actions). Every entry follows one shape:

```
NAME [action glyph?] KIND LEVEL          e.g. a header line ending "SPELL 3", "FEAT 1", "CREATURE 7"
TRAIT TRAIT TRAIT                        upper case, rarity first when not common
Label value; Label value                 Traditions, Range, Targets, Duration, Prerequisites, Requirements, Trigger, Frequency
Body text ...
Heightened (+1) ... / Critical Success ... / Special ...
```

Statblocks use fixed labels too (Perception; Languages; Skills; Str/Dex/Con/Int/Wis/Cha; AC; Fort/Ref/Will; HP; Hardness;
Immunities; Weaknesses; Resistances; Speed; Melee/Ranged with the action glyph, bonus, traits and Damage; then abilities).

### 3.2 The importer

A new `pf2` command in `tools/Codex.ContentImport` taking the PDF path as an argument. It runs locally only, and its output pack is
the only thing committed.

1. **Spine from bookmarks.** `TryGetBookmarks` gives chapter, section and (for classes, ancestries, monsters) entry boundaries with
   page numbers. Use it to say which parser a page range goes to and which ranges are dropped (section 2). Do not infer chapters from
   running heads.
2. **Strip page furniture by font and position, not by text.** The dump's font histogram separates body (GoodOT 9pt), rules prose
   (Sabon 9pt), headers (GoodOT-CondBold 12pt) and the side navigation tabs and doubled page numbers, which come out as lines like
   `9090` or a column of chapter names at the end of every page. Drop letters by font and x-position band.
3. **Sidebars split columns.** `ContentOrderTextExtractor` sometimes runs a boxed sidebar into a statblock's last ability (seen in
   Monster Core: an ability's text breaks off for a sidebar heading and resumes in short lines). Detect sidebar boxes by font and
   bounding box and pull them out before parsing. Sidebars are dropped anyway.
4. **Tables by word boxes.** Equipment, class advancement, spell lists and the language lists are tables. Use word bounding boxes
   (column x-ranges per table), not content-order text.
5. **Entry parsers** per shape in 3.1: header regex, trait line, labelled fields, then body sections. Statblocks get their own parser.
6. **Normalize.** The minus sign is an en dash (`–2`); curly quotes; ligatures; hyphenation at line ends; the `[glyph]` tokens into
   an `actions` field (`1`, `2`, `3`, `reaction`, `free`, `1-3`).
7. **Cross-references.** "(page 359)", "page 288" and "GMC 282-283" are stripped or, where the target is an entry we import (a
   monster ability, a condition, a trait), turned into an entry link. A reference into GM Core becomes plain text with no link.
8. **Redaction table** (4.3), then **provenance** on every entry: book, printing, chapter, section; plus a per-entry list of what
   was dropped.
9. **Deterministic, no LLM rewriting**, as the handoff decided.

The pack is regenerated, never hand-edited, like `srd5e`.

## 4. Reserved Material: what must be filtered

### 4.1 How much there is

A scan for proper nouns from the setting chapter, counted per chapter (the scan's false positives, skill names and common words, taken
out by hand):

| Chapter | Real hits | Where |
|---|---|---|
| Setting (32-41) | about 200 | Everything: dropped whole |
| Ancestries (42-91) | about 35 on ~20 pages | Flavor pages, the language page's "Inner Sea" and the common tongue's local name, a few heritage lines |
| Classes (92-225) | about 12 | Cleric's deity section, witch patron examples, "sample" sidebars, a wizard school example naming two academies |
| Skills, feats, equipment | under 5 | One or two example sentences |
| Spells (298-397) | about 20 on ~10 pages | One spell with a per-deity variant list; resurrection spells naming the goddess of death and her realm; two summoning examples |
| Playing the game, conditions | 0-1 | |

So Paizo kept setting names out of the rules text almost entirely. Filtering is **a reviewed list of a few dozen phrase edits plus
whole-section drops**, not a general NLP problem.

The scan itself was crude: it seeded names from pages 32-41 only, so it misses names that appear only elsewhere, and it flagged
skill names. The real check (4.4) uses an allowlist instead.

### 4.2 Deities: the handoff's open question is answered

**Player Core ties the cleric's divine font and domains to a named deity.** The cleric's Divine Font says the font spell is the one
listed in your deity's entry ("heal", "harm", or a choice of both). Each deity entry carries, besides its name and flavor: edicts,
anathema, divine attribute, cleric spells (three, by rank), divine font, sanctification (holy/unholy), divine skill, four domains and a
favored weapon.

So the pack cannot ship the deity table, and the cleric does not work without something in its place. Options:

| Option | What ships | Verdict |
|---|---|---|
| A. Free-form "faith" built by the player | A `faith` entity type with exactly those mechanical fields and no name or text. The player (or GM) makes their own faith in the homebrew editor; the cleric's class features read it | **Recommended.** No Reserved Material at all. Fits the homebrew wizard |
| B. Anonymous bundles ("Faith 1: heal, Strength or Constitution, Athletics, 4 domains...") | 19 nameless entries copying each deity's mechanics | Risky: the combination is recognisably the deity. The AxE allows renaming, but a 1:1 table invites a "sufficiently delineated" argument. Ask the lawyer (7) |
| C. Leave the deity a text field | Nothing | Cleric loses divine font and domains: not acceptable |

Domains themselves are generic words (dreams, luck, might) and the domain spells are focus spells: both ship. Edicts and anathema are
role-play text the player writes into their own faith.

### 4.3 Phrase-level redactions

Entries with a proper noun inside mechanics are kept, with the phrase cut or made generic. Model the table on
`tools/Codex.ContentImport/SpellTextFixes.cs`:

- keyed by entry id, with the **exact** source text and its replacement (synthetic example: "unless the goddess Zerla has claimed
  the soul" → "unless the soul has been claimed");
- the importer **fails** when a row's source text is not found, so a new printing that rewords the line is caught (same rule as the
  sheetkinds importer's `Validate`);
- whole-entry drops (the deity list) and whole-section drops (sidebars, example characters, flavor pages) are listed in the same file
  with a reason.

The one spell with a per-deity variant list needs either option A's faith (the variant is read from the faith's favored weapon) or
dropping the variants. Decide with 4.2.

### 4.4 The denylist test

Replace the crude scan with a test over the generated pack:

1. **Allowlist of game terms** from the books' own glossaries: Player Core's glossary/index (453-463) and Monster Core's creature
   traits and creatures-by-type lists, plus skills, conditions, traits and every entry name we ship.
2. Flag every capitalized word not at a sentence start that is not on the allowlist. Review the flags once; the reviewed set becomes
   `denylist.txt`, and the smoke test fails on any denylisted word in the pack.
3. Seed the denylist with the setting chapter's names and all 19 deity names.

### 4.5 Grey areas for the lawyer (one hour)

- **Paizo-coined common nouns:** monster names (many families have invented names), versatile heritage and mixed-ancestry names, and
  language names (several planar and subterranean languages). They are written lower case in running text and the ORC grant covers
  statblocks, so the working position is "licensed", but the book's own notice reserves "names ... derived from proper nouns". A
  language named after a place (the common tongue's local name, the regional languages) is clearly reserved and is dropped.
- Cosmology words inside trait definitions (a trait that names a plane as the source of its energy). Working position: rewrite to the
  generic phrase ("positive energy" style) through the redaction table.
- Option B in 4.2.

## 5. What the engine has to do: fit and gaps

### 5.1 Which engine path

The engine has two hosts:
- **`sheetRules`** (srd5e): ranked skills and saves, level-gated class feature rows (`byField`, `levelColumn`), feature grants,
  counters, row and key choices from #297, lists with prerequisites, slot pools. But its HP (`HitPointRules`: hit die per level),
  AC (`ArmorClassRules`: armor base + capped Dex) and attributes (scores) are 5e-shaped.
- **`sheetKinds`** (fitd, and the `d35`/`street`/`crew` reference fixtures): generic values, formulas, tracks, typed modifiers,
  `conditionEffects`, `rolls` with named outcomes, events and intents. The `d35` fixture already proves per-class progressions, typed
  bonuses, threat and confirmation on this path. But it has no level-gated feature rows and no ranked lists. Since #299 a kind has `list` feature sources (counters, keyed
  choices, modifiers into the model; `SHEETKINDS_FEATURES_DESIGN.md`), but entry choices, free castings, actions and slot pools are
  still `sheetRules`-only (checked 2026-11-14; see `PF2E_HAZARDS_IMPLEMENTATION_PLAN.md`).

`SheetRuleEngine.Evaluate` runs the kind model **and then** `sheetRules` on the same sheet, so a system may declare both. No system does
today, so this is untested. `conditionEffects` and `rolls` are only read by the kind model (`SheetModel`, `SheetRolls`).

**Recommendation for the spike: `sheetRules` for character building (ranks, class features by level, feats, choices), plus one sheet
kind for the play-time numbers (rolls with four degrees, valued conditions as tracks, condition effects).** It is the shortest way to
reuse both #297 and the generic roll engine, and it is the only path where both halves exist today. Prove it first as a reference
fixture with our own numbers (5.4), before any Paizo text is involved.

### 5.2 Mechanic by mechanic

| Mechanic | Fit | How, or what is missing |
|---|---|---|
| Proficiency ranks (untrained, trained, expert, master, legendary) | **Fit** for skills and saves | `skills.ranks` and `saves.ranks` with `value: level + 2` ... `level + 8`; untrained is rank 0 (worth 0). #297's `rank` / `raise` grants and `minRank`/`maxRank` choices cover class features and skill increases |
| Ranks for Perception, weapon/armor categories, class DC, spell DC/attack per tradition | **Gap** | Ranked lists exist only for `skills` and `saves`. Needs a general ranked list (a rank list per key set: `perception`, `simple`, `martial`, `unarmored`, `light`, `classDc`, `spell`). Largest engine item for characters |
| Attribute modifiers (no scores) | **Fit** | `attributes` with `modifier: none`, default 0 |
| Boosts and flaws (ancestry, background, class, four free at 1st; four at 5/10/15/20; above +4 a boost gives +1 only every second time) | **Partial** | `attributes.bonuses` covers a background's "one of these two, plus one free". Free boosts, flaws, "four different attributes" and the partial boost above +4 are not expressible. Needs a boost rule set, or a function |
| HP = ancestry HP + (class HP + Con) × level | **Partial** | `HitPointRules` is hit-die based. Likely yaml-only: `inherits` copies ancestry and class HP onto sheet keys, then a derived formula. Verify a derived value can write the HP maximum |
| AC = 10 + Dex (capped by armor) + armor proficiency + item bonus | **Partial** | `ArmorClassRules` has the Dex cap and item bonus but no proficiency term. A formula with the armor rank works once the general ranked list exists |
| Class DC, spell DC = 10 + ... | **Fit** | `spellcasting.saveBase: 10`; class DC as a derived value |
| Typed bonuses (circumstance, status, item): highest bonus of each type | **Fit** | `modifierTypes: { types: { circumstance: highest, status: highest, item: highest } }` |
| Typed penalties: worst of each type; **untyped penalties all add** | **Gap (small)** | `ModifierTypesDefinition.Penalties` is one rule for every type's penalties. Needs a per-type penalty rule, or untyped exempted |
| Four degrees of success (±10 from the DC, nat 20/1 shift one step) | **Fit, to prove** | A roll's `outcomes` read `total` and `natural`; the DC comes in as an input. The nat shift needs either four nested formulas or a `functions:` entry `degree(total, dc, natural)`. Prove in the fixture |
| Action cost (1, 2, 3, reaction, free, variable) | **Fit** | An entry field; shown, not enforced |
| Multiple attack penalty (−5/−10, agile −4/−8) | **Gap** | Turn state at the table. Start as a roll input (`attackNumber`), enforce later |
| Traits | **Fit** | A `trait` entity type and a tags field; entries link to traits |
| Rarity (common, uncommon, rare, unique) | **Fit** | A field and a list `where` (uncommon needs GM access: a warning) |
| Feats by slot (ancestry, class, skill, general at fixed levels) | **Fit** | One list per slot with `maxCount` by level lookup and `where` on the feat's traits and level, as srd5e does feats |
| Prerequisites ("trained in Medicine", "Strength +2", "a feat") | **Partial** | List `where` conditions can test fields; testing a rank or another pick needs a rule condition over ranks |
| Spell slots by rank; prepared and spontaneous; cantrips | **Fit** | `slotPools` from class tables |
| Heightening (fixed and +N), cantrips heightened to half level | **Partial** | `perSlot` / `atLevels` cover dice; non-dice heightening (more targets, longer duration) stays text |
| Focus points (pool max 3, refocus) | **Fit** | A resource or `uses` counter; refocus is a moment (below) |
| Hero points | **Fit** | A resource with a session-start refill (moment) |
| Moments: daily preparations, refocus, session start, encounter start | **Fit (built in #299)** | `moments:` in `system.yaml` and `MomentCommand`; PF2e declares `dailyPreparations`, `refocus`, `sessionStart` |
| Frequencies: "once per 10 minutes", "once per hour", "once per day" | **Gap** | Cooldowns are not moments. Start as counters refilled by a manual button; a timed cooldown is a later design |
| Valued conditions (frightened 2, clumsy 1, drained, enfeebled, stupefied, sickened, slowed, stunned, dying, wounded, doomed) | **Gap** | `conditions` is a list of names; `conditionEffects` modifiers do not read a value. Workable now as a track per valued condition plus modifiers with `amount: "-frightened"`; a first-class valued condition is cleaner. Exhaustion is the only valued case today |
| Dying/wounded/doomed (dying 4 − doomed kills; recovery check DC 10 + dying) | **Gap** | `DeathSaveRules` is 5e's three/three. Express as tracks and an event handler on the kind side |
| Persistent damage, weakness, resistance, immunity | **Partial** | `DamageTypeRules` has resistances/immunities/vulnerabilities; persistent damage is a valued condition plus an end-of-turn flat check (later) |
| Bulk (L = 1/10, 10 L = 1 Bulk, limit 5 + Str, encumbered) | **Partial** | `inventory.weight` + `capacity`; L as 0.1 needs decimals in the formula engine (check) |
| Shields (raise, hardness, HP, broken threshold) | **Partial** | Item fields; Shield Block is a reaction that reduces damage: text first |
| Weapon damage dice, traits, striking runes, critical specialization | **Partial** | `actions` from inventory; runes are GM Core items |
| Companions and familiars | **Gap** | A second sheet per companion. The sheet-kinds "crew" shape (a second kind) is the closest; leave as text first |
| Archetypes (dedication, then archetype feats) | **Fit** | A feat list with `where` on the dedication pick |
| Creature statblocks | **Fit** | Creatures are `printed` sheets as srd5e monsters are |
| Elite/weak adjustments (+2/−2 to most numbers, HP by level band) | **Gap (small)** | A creature-level modifier set; maybe a sheet input with `modifiers` gated on it |

### 5.3 Features the sheetkinds branch plans that PF2e also needs

- **S1 moments resolver** and **S2 `MomentCommand`**: daily preparations, refocus, session start (hero points).
- **S6 roll-scoped bonuses** (`when: input.*` now, opt-in later): PF2e is full of "circumstance bonus when X" lines; the opt-in design
  should take PF2e's case into account.
- **S10 sheet-fired moments go through the table command.**
- If PF2e uses the hybrid in 5.1, the **S3 lift** (features without `sheetRules`) is not needed for PF2e itself, but its null-safety
  changes are on the same code.

### 5.4 Engine work, in order (proposal)

1. **Reference fixture `tests/Codex.Tests/Fixtures/reference/pf2/`** with our own numbers and invented entries (no Paizo text), and a
   `spec.yaml` of expectations: ranks worth level + 2/4/6/8, four degrees with the nat shift, typed bonuses and penalties, frightened 2
   lowering checks and DCs, HP from ancestry + class, AC with armor rank, MAP as an input. It shows which rows of 5.2 really are gaps,
   in the existing pass/fail/unsupported harness. This is also where the hybrid (`sheetRules` + one kind) is proven or rejected.
2. Per-type penalty rule.
3. General ranked lists (Perception, weapon and armor categories, class DC, spell per tradition).
4. Valued conditions (first-class, or track-backed by convention).
5. Boost rules.
6. Moments (shared with the sheetkinds branch, S1/S2).
7. Then the system yaml and the importer.

## 6. Pack layout and licensing (from the handoff, confirmed)

- New folder under `content/systems/` with its own `manifest.yaml` license block: ORC, `paizo.com/orclicense`, the ORC Notice, both
  credit lines verbatim, our own adaptation credit, our own non-endorsement line. Not in `bundled.txt`.
- System name without "Pathfinder"; "Pathfinder Second Edition" in full only where compatibility is stated; never "PF2e" in
  user-facing strings.
- No `inherits` from or into `srd5e` or `fitd-srd` entries (ORC cannot flow into CC BY).
- `.gitignore` entries for `*.pdf` under the import tool and for any dump folder.
- A dated snapshot of Paizo's Terms of Use beside the notice.

## 7. Open questions

1. **GM Core.** Magic items, runes, the DC-by-level table, encounter budgets and treasure are there. Buy it (and read its notice) before
   the pack is called complete, or ship a Player Core + Monster Core pack with mundane equipment only?
2. **Faith (4.2):** option A (player-built faith, recommended) or B (anonymous bundles, needs the lawyer)?
3. ~~**Engine path (5.1):** the hybrid, or wait for the sheetkinds features lift and go kinds-only?~~ The lift is merged (#299), and
   kinds-only still lacks ranks, entry choices, actions and slots, so the hybrid stands. The pf2 reference fixture proves it
   (`PF2E_HAZARDS_IMPLEMENTATION_PLAN.md`, P1).
4. **Companions and familiars:** text first, or a second sheet kind from the start?
5. The lawyer's list: handoff §7, plus 4.5 here.
