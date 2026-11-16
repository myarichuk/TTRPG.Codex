# Design: the local importer for the two ORC-licensed rulebooks (with the redaction step's place in it)

Written 2026-11-16 against `origin/main` and the P10 branch. It builds on the ORC handoff and import survey notes in this folder and does
not repeat them. Structural facts below come from throwaway probes run locally (page counts, bookmark depths, font names and sizes,
word-box positions). No book text was printed or kept.

The two books are named only by their short keys everywhere in this repository: `pc` is the player book and `mc` is the monster book.
The one place a product name may appear is the upstream credit line in `tools/Codex.ContentImport/Data/degrees-books.yaml`, because the ORC
License requires it. The books, their file names and everything extracted from them stay outside the git tree.

## 0. What the probes showed (structure only)

| | `pc` | `mc` |
|---|---|---|
| Pages | 466 | 378 |
| Bookmarks per depth (0/1/2/3/4/5) | 13 / 95 / 460 / 634 / 281 / 1 | 6 / 37 / 230 / 360 / 22 |
| Top level | Frontmatter, Chapters 1 to 8, Conditions Appendix, Character Sheet, Glossary & Index, Backmatter | Frontmatter, Introduction, Monsters (letters A to Z), Ability Glossary, Creatures by Level, Backmatter |
| Bookmarks titled "Sidebar..." | 81 | 2 |

- **Bookmarks are not a full spine.** Chapter 5 (Feats) and Glossary & Index have no child bookmarks, so entries there are cut
  by header font. The conditions appendix has three sections, and its list of conditions has 46 children. That number must be
  checked against the header count before it becomes the smoke test's count (section 6).
- **Fonts (letters per font on the appendix pages):** rules prose is Sabon 9pt. Structured text, stat lines and sidebars are GoodOT 9pt.
  Entry headers are GoodOT-Bold 13pt. Statblock and table headers are GoodOT-CondBold 12pt. Section heads are Taroca 16pt and 22pt.
  The side tabs are GoodOT-CondBold 9pt at x 534 to 596. The page number is GoodOT-Bold 12pt, near x 21 to 41 on left pages and x 562 to 582 on right pages.
  Chapter openers carry a TimesNewRoman-BoldItalic 12pt block, which is narrative and dropped by font. The action glyphs are a separate
  icon font whose name contains the trademark (section 7).
- **Layout:** the page is 603 x 783pt with two columns. Sidebars sit inside one column in GoodOT 9pt, among Sabon prose. Furniture
  changes sides between left and right pages. Grouping words by baseline across the whole page merges the two columns, so columns are
  split by the x gap between them, per page.
- `tools/Codex.ContentImport` did not reference PdfPig before P8a. Only `Codex.Web` and `Codex.Tests` did, both at 0.1.16. P8a adds that same version.

## 1. Where imported content lands

**Decision: a second pack in the `four-degrees` system, beside `homebrew-core`. Hand-written system.yaml changes come in their own
reviewed step before the chapter that needs them. The importer never writes system.yaml.**

- Pack folder `content/systems/four-degrees/packs/rules-core/`, id `four-degrees-rules`. It holds `pc` material.
  `mc` material goes in a third pack, `packs/rules-creatures/`, id `four-degrees-creatures`, so that each pack's
  manifest credits exactly the book it uses (section 5). Neither pack goes in `bundled.txt`.
- **Not a replacement for the homebrew.** `homebrew-core` is the material that ships if the import is withdrawn. Its tests,
  `DegreesSystemSmokeTests`, `FourDegreesRunesTests` and `HazardDegreesTests`, pin it. It also covers what we do not hold
  (the GM book's items and runes, P10).
- **Not a second system.** That would mean copying 420 lines of engine yaml, and P7 built `four-degrees` as the skeleton for exactly this.
- **system.yaml must change, by hand.** Today four-degrees declares no `condition`, `spell`, `trait`, `heritage`, `rule` or
  `skill-action` type. Its six skills, its conditions (Shaken, Wobbly, Pinned) and its score-based attributes are all invented.
  Imported entries refer to the book's skills and conditions by name. Each chapter's prerequisite step therefore adds the entity
  types and fields it needs, and where needed the vocabulary. It is Sonnet-written and Opus-reviewed, in our own modelling.
  `conditions:` and `conditionEffects` are engine modelling (judgment), not import output. srd5e splits them the same way:
  names and effects are in system.yaml, the text is in the pack's `conditions/` entries.
- **Entries tagged `homebrew`** stay in `homebrew-core`, keep the tag, and keep their counts. A new rule holds every
  four-degrees entry: it has either the `homebrew` tag or a `source:` line from the importer, never both and never neither.
  A test asserts no id clashes between the packs (the homebrew weapon and armor ids are the likely clashes).
- **Invented vocabulary versus the book's:** Shaken, Wobbly and Pinned appear in 7 test files and several components.
  Retiring them is a user decision (section 8, Q3). Until it is made, the book's conditions are Library entries only,
  and the table keeps the invented ones.
- **The new packs' manifests must not reserve imported names.** The homebrew manifest's Reserved Material line ("every
  name invented for it") stays on `homebrew-core` only. The imported packs say they designate no Reserved Material of their
  own, because adding terms to Licensed Material breaks ORC II.b.

## 2. The importer's shape

```
dotnet run --project tools/Codex.ContentImport -- degrees [run|fingerprint|notice] \
  --book <pc|mc> --pdf <path> [--work <dir>]
```

Later steps add `--out <pack dir>` and `--check`. P8a writes no pack output.

- **Named without the trademark.** The command is `degrees` and the code lives in `tools/Codex.ContentImport/Degrees/`.
  The earlier plan named a trademark-bearing folder, which contradicted its own naming rule, so the plan row is updated with the first commit.
- **Safety gates, before anything is read:**
  - The tool refuses a `--pdf` or `--work` path that resolves, symbolic links included, inside the git work tree. It walks up the
    directories looking for `.git` (a folder, or the file a linked worktree has), since a gitignore entry alone is not enough.
  - `.gitignore` also gains `tools/**/*.pdf` and `.degrees-work/` as a backstop.
  - The default `--work` is `<temp>/codex-degrees/<book>`.
  - Paths come only from the command line. No book file name is committed.
- **Fingerprint by structure, not by file hash.** A hash could tell one buyer's copy from another, so it is never committed.
  The committed `Data/degrees-books.yaml` holds, per book key:
  - the page count and the bookmark count at each depth (the section 0 numbers) and the printing;
  - the bookmark path of its ORC Notice;
  - the expected credit line, in segments where the book breaks it across columns or pages.

  A mismatch stops the run with the message "different printing; re-review every chapter".
- **Committed configuration (no book text):**
  - `Data/degrees-chapters.yaml` (later) is the **take-list**. Each row has a chapter key, the bookmark path(s) taken, the parser
    (`entries`, `statblocks`, `table`, `glossary`, `rules`), the target entity type, and `approved: true|false`.
    Nothing outside a taken path is read into an entry.
  - **Dropped material is never named.** The setting, religion, example-of-play and map sections are simply absent from the
    list, because several bookmark titles contain setting names or the trademark.
  - `Data/degrees-fonts.yaml` maps font roles to family substrings, size ranges and x bands. The roles are body, rules, entry header,
    block header, section, opener, furniture, icon, display and tag. Table rows have no font role of their own.
- **Pipeline.** Each stage writes JSON into `--work` (local, never committed), so a reviewer can look at the source locally:
  1. `layout.json`: every word with its font, size, bounding box, page and role.
  2. `spine.json`: the bookmark tree with page ranges.
  3. `blocks.json`: the classification. Furniture is identified by font, x band and page side. Columns are split by the x gap.
     Sidebars are found by font plus bounding box, or by a "Sidebar" bookmark, and split out; they are dropped. Openers are dropped by
     font. Tables are rebuilt from word boxes: column x ranges are found from a header row in the table font, then rows are cut by baseline.
     **An unclassified font fails the run.** So does a block in a taken range that no parser claims.
  4. `entries.json`: parsed entries. The header is cut by the entry-header font. Then come the trait line, the labelled fields and the
     degree sections. Text is normalized: en-dash minus, quotes, ligatures, hyphenation at line ends, icon glyphs mapped to an
     `actions` value, and cross-references stripped or turned into entry links.
  5. `redacted.json`: after the redaction step (section 4).
  6. Pack YAML, regenerated **for all approved chapters** on every run. `--out` wipes the pack, as the srd5e importer does,
     so one chapter run is "approve one more row", never "append".
- **Deterministic:**
  - The output is ordered by entry id, with invariant culture, no timestamps, PdfPig pinned and a fixed YAML emitter.
  - `--check` regenerates into a temp folder and fails on any byte difference from `--out`. It runs locally only, since CI has no PDF.
  - Nobody edits the generated YAML by hand. A hand edit shows up as a `--check` failure on the next run.
- **No LLM in the pipeline.** Agents write parser code and table rows. They never write or rewrite entry text.

## 3. Chapter order, and one chapter per agent

The order runs from the cleanest mechanics to the hardest:

| # | Chapter (take-list key) | Parser | Prerequisite step (hand-written, before the run) |
|---|---|---|---|
| 0 | none: infrastructure | | P8a (section 9); redaction mechanism with empty tables |
| 1 | `conditions` (Conditions Appendix: the list, plus the two rule sections as `rule`) | entries | `condition` and `rule` types in system.yaml |
| 2 | `glossary-traits` (trait definitions only; the index is not taken) | glossary | `trait` type; it also feeds the allowlist |
| 3 | `feats` (Chapter 5; no child bookmarks, so header font only) | entries | `feat` fields (level, traits, prerequisites, actions) |
| 4 | `skills` (Chapter 4: skill actions) | entries + rules | **vocabulary step**: the book's skill list in system.yaml (Q3) |
| 5 | `spells` (Chapter 7: spells, cantrips, focus spells, rituals; spell lists as tables) | entries + table | `spell` type |
| 6 | `equipment` (Chapter 6) | table + entries | armor, weapon and gear fields; id clash check with homebrew |
| 7 | `ancestries` (Chapter 2, without the flavor pages and the regional language rows) | entries | `heritage` and `language`; boosts shape (Q4) |
| 8 | `classes` (Chapter 3; the cleric reads the nameless faith shape; no deity table) | entries + table | class feature rows; the faith decision is already option A |
| 9 | `rules` (Chapters 1 and 8 as `rule` entries, without the example of play or the setting sections) | rules | none |
| 10+ | `mc`: ability glossary, then creature traits, then creatures one letter range per run | statblocks | creature fields (Q4: modifiers versus scores); elite/weak in system.yaml |

**One chapter per agent.** Each is a fresh Sonnet agent in a **local** session, because the PDFs never go to the cloud. Its brief has:
- the plan's brief header and the PDF brief rules;
- the chapter key, and this design's sections 2, 4 and 6;
- the files it may touch: its parser under `Degrees/`, its take-list row, its redaction rows, its count rows in the smoke test,
  and the generated pack folders;
- the files it must not touch: system.yaml (already done by the prerequisite step) and other chapters' parsers, unless a
  shared fix is named in its report.

The agent reads `blocks.json` and `entries.json` locally to debug its parser. It never pastes them into a report, a commit
message or a PR.

## 4. The redaction step (P9): before any pack output

**Order: P8a (no pack output) -> redaction mechanism (empty tables, tests green) -> chapter 1 -> every later chapter adds its own rows.**
Pack YAML is staged only after the redaction stage reports zero unmatched rows and the denylist and allowlist tests pass locally.

- **The redaction table stores no source text.** A row that held the exact source text would commit the very
  proper noun it removes. Each row in `Data/degrees-redactions.yaml` is:
  - `entry` (id), `field`, and `span` (the SHA-256 and character length of the exact normalized source span, which is a
    sentence or a clause);
  - `action`: `replace` (with our replacement text), `cut` or `drop-entry`;
  - `reason`, in our words, naming no names.

  The importer splits the field into sentences and hashes each one. A row whose hash is not found **fails the run**, so a new
  printing that rewords a line is caught. A replacement is redacted Licensed Material, so the file is ORC data, noted in LEGAL.md as
  `Data/srd521-traps.yaml` is for CC BY. The Opus reviewer reads each row beside the source span, in `entries.json`, locally.
- **The allowlist test, which is the real gate,** runs in CI over the generated packs. Every capitalized word that is not at
  the start of a sentence must be one of these:
  - a shipped entry name;
  - a skill, condition or trait key;
  - a term from the glossary allowlist. `Data/degrees-allowlist.txt` holds game terms only, and those are Licensed Material.

  A flagged word is either added to the allowlist (it is a game term) or redacted. It is never ignored.
- **The denylist is stored as hashes.** A local command, `degrees denylist`, hashes every capitalized token of the sections
  the take-list leaves out (the setting and religion pages). It writes `Data/degrees-denylist.sha256`, one lowercased-word
  hash per line, with no names. The CI test hashes every word of the packs and fails on a match, reporting the file, the entry and
  the hash, never the word. It sits beside the existing `NoFileOfTheSystemNamesAPublisherASettingOrAGame` regex, which also covers the
  `source:` lines.

## 5. Provenance and attribution

- **Per entry:** the existing `ContentEntry.Source` string, in the fitd pack's form:
  `source: <book key> > <Chapter> > <Section>`, for example `pc > ...`. It is built from the bookmark path and passed through the allowlist and denylist
  checks. A section title that would fail them is replaced by its chapter. There are no page numbers and no quotes.
  The printing is in the manifest, not on every entry.
- **Book key in `source`:** decided: the opaque keys `pc` and `mc`, never a book title. The full credit line is in the manifest.
- **What was dropped:** the importer writes `import-report.yaml` into each pack. Per approved chapter it gives entry counts by type,
  the count of dropped blocks by reason (sidebar, opener, flavor, untaken section) and the redaction row ids applied. It holds counts and ids only.
- **Attribution:**
  - `rules-core`'s manifest carries the `pc` credit line that is already in system.yaml.
  - On the first `mc` run, `rules-creatures` gets the `mc` credit line in its own manifest. It is committed already, in `degrees-books.yaml`, for that use.
  - system.yaml's attribution grows to name both books only when system.yaml itself restates `mc` rules (the elite/weak
    step). It does so as a single quoted `attribution:` line, because the regex test exempts only lines starting with
    `attribution:`. A block scalar would fail it.
  - In every case the importer extracts the notice from the bookmarked ORC Notice page and **asserts** it equals the committed
    string (`degrees notice`). It never writes the string from memory, and it prints only which segment failed, never text.
  - The LEGAL.md paragraphs ("Four Degrees" and "deliberately not included") are updated by the chapter 1 run, and again when `mc` first lands.

## 6. A chapter run's "done", and the Opus review

**Done** means all of these:
1. The `degrees` run exits 0: no unclassified font, no unclaimed block, zero unmatched redaction rows, and the fingerprint matches.
2. `--check` passes (two runs, byte-identical).
3. **The counts come from the document, two ways:**
   - the bookmark children of the taken section, where they exist;
   - the entry-header-font count in the taken range.

   The two must agree; if they don't, the run explains the difference in the report. The result is pinned in a new
   `DegreesRulesPackTests` theory, in the style of `SrdPackSmokeTests`: counts per type, no empty required fields, every entry
   with `source:` and without `homebrew`, the licence block, and no id clashes.
4. The allowlist and denylist tests, `DegreesSystemSmokeTests` (unchanged counts), the goldens and the whole suite pass.
5. `git status` shows no PDF, no work-dir file and no file outside the allowed list.

**The Opus reviewer** gets the diff and this design, never the implementer's summary. It works locally with `--work` open and checks:
- **Fidelity:** 10 random entries plus every entry with a redaction row, compared field by field with the source span. It checks
  numbers, action costs, traits, degree sections and heightening, and that nothing is present that the source lacks (recall).
- **Flavor:** no narrative or flavor sentence survives (openers, lore lines, sidebars).
- **Reserved Material:** it reads the allowlist test's flagged-word report, checks every new allowlist word really is a game term,
  and checks every redaction keeps the mechanic.
- **Earlier chapters' files are byte-identical** unless the report names a shared parser fix.
- The manifests and LEGAL.md are intact, and the attribution matches the extracted notice.
- No book text appears in the commit message, the PR body or the report.

## 7. Risks

- **The trademark in code.** The icon font's name contains the trademark. The font map matches it by a neutral suffix, and a test
  scans `tools/Codex.ContentImport/Degrees/`, the font map and this document for publisher, setting and game names.
- **Extraction errors that look plausible.** A column merge or a sidebar run into an ability gives fluent but wrong text. This is why
  the unclaimed-block failure, the two-way counts and the reviewer's field-by-field sample all exist.
- **Fragile hashing.** Span hashes break if normalization changes. Normalization is versioned in `degrees-books.yaml`, and changing it
  means re-hashing every row in one reviewed commit.
- **Recall.** A model "fixing" a parsed field from memory is the same leak as copying. Output is never edited by hand; `--check` catches it.
- **Size.** About 840 feats, 450 spells and 440 creatures in total, so loader and test time grow. The smoke test loads only the four-degrees folder.
- **Engine gaps.** Dying, persistent damage, frequencies and heightening beyond dice import as text. Modelling them is separate work.
- **Legal (see the handoff note):** the purchase terms of use versus the ORC grant, publisher-coined monster and language names, and the
  wording for compatibility.

## 8. Questions for the user

Decided on 2026-11-16: Q2 (opaque keys `pc` and `mc`) and Q7 (local agents may read the extracted text; it is inference, not training).

1. **First publication.** The chapter 1 commit is the first time book-derived text enters a public repo. Hold the lawyer hour
   on the terms of use before that commit, or after?
2. ~~Provenance book key~~: decided, `pc` and `mc`.
3. **Invented vocabulary:** when the book's conditions and skills land, retire Shaken, Wobbly, Pinned and the six invented skills
   (moving 7 test files to the `degrees` fixture or to the real conditions)? Or keep both sets, with the invented ones marked homebrew?
   Recommended: retire, in the vocabulary step before chapter 4.
4. **Attributes:** four-degrees uses scores (`half-minus-five`), and the homebrew creatures carry `str`-`cha` as scores. The books
   use modifiers only. Switch to modifiers before chapter 7 (ancestries) and before `mc`? Recommended: yes.
5. **Homebrew ancestries and classes** next to the imported ones: keep them visible (tagged homebrew), or make `homebrew-core` a
   pack that is off by default once `rules-core` exists?
6. **Publisher-coined monster names:** ship them under the working position that they are licensed, or hold `mc` until the lawyer has answered?
7. ~~Local agents reading extracted text~~: confirmed.

## 9. First build step: P8a, infrastructure with no pack output

- Add PdfPig 0.1.16 to `Codex.ContentImport.csproj`.
- Add the `degrees` command, the out-of-repo path guard, `.gitignore` lines, `degrees-books.yaml` (fingerprints, notice paths and credit lines)
  and `degrees-fonts.yaml`.
- Build stages 1 to 3: layout, spine, blocks with the font roles. The unclassified-font and unclaimed-block failures report only
  counts and positions.
- Add a `degrees notice` subcommand that asserts the committed credit line equals the extracted one.
- Tests: an options-parse test, a path-guard test, and a synthetic-PDF test. The synthetic PDF is generated in the test (PdfPig can
  write one) to show furniture and columns are classified. CI never sees a book.

Done when both books fingerprint, every font on every page has a role, and the notice assertion passes locally. The redaction
mechanism (empty tables, allowlist and denylist tests) is the next step, and chapter 1 (conditions) follows it.

P8a result, run locally: both books fingerprint, every letter of both has a font role, and the notice assertion passes for both.
The unclaimed-block failure waits for the take-list, which arrives with chapter 1.
