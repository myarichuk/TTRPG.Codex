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
  The user decided to retire them (section 8, Q3). Chapter 1 still ships the book's conditions as Library entries only,
  and the table keeps the invented ones. Replacing `conditions:` and `conditionEffects` with the book's is its own reviewed step
  after chapter 1. It reads the effects from the extracted text, never from memory, and moves the 7 test files.
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
  - `entry` (id) or `entryHash` (the SHA-256 of the id, for a row that drops an entry whose own name the notice reserves: the id is the name, so the file never holds
    it), `field`, and `span` (the SHA-256 and character length of the exact normalized source span, which is a
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

All seven were decided by the user on 2026-11-16:

1. **First publication:** no lawyer step. The project relies on the ORC grant, follows the licence to the letter and uses no trademark.
2. **Provenance book key:** the opaque keys `pc` and `mc`.
3. **Invented vocabulary:** retire Shaken, Wobbly, Pinned and the six invented skills when the book's conditions and skills land.
   The conditions go in their own step after chapter 1, and the skills in the vocabulary step before chapter 4.
4. **Attributes:** switch to modifiers, as the current core rules are written, before chapter 7 and before `mc`.
5. **Homebrew ancestries and classes:** `homebrew-core` stays a pack, off by default once imported ancestries exist.
   A screen with no entries shows a plain empty state that points to homebrew, rather than skipping the step.
6. **Monster names:** `mc` is Licensed Material and ships. The book's own capitalization decides what is a proper noun, and
   proper nouns that are Reserved Material are renamed or dropped.
7. **Local agents reading extracted text:** confirmed. It is inference, not training.

## 9. First build step: P8a, infrastructure with no pack output

- Add PdfPig 0.1.16 to `Codex.ContentImport.csproj`.
- Add the `degrees` command, the out-of-repo path guard, `.gitignore` lines, `degrees-books.yaml` (fingerprints, notice paths and credit lines)
  and `degrees-fonts.yaml`.
- Build stages 1 to 3: layout, spine, blocks with the font roles. The unclassified-font and unclaimed-block failures report only
  counts and positions.
- Add a `degrees notice` subcommand that asserts the committed credit line equals the extracted one.
- Tests: an options-parse test, a path-guard test, and a synthetic-PDF test. The synthetic PDF is generated in the test (PdfPig can
  write one) to show furniture and columns are classified. CI never sees a book.

Deferred from section 2 to chapter 1, before any pack output: furniture by page side (P8a uses font and x bands only), splitting out and
dropping sidebars (P8a only flags the "Sidebar" bookmarks), dropping openers (P8a classifies them as the `opener` role, and they still
become blocks), rebuilding tables from word boxes, support for more than two columns, and failing on a one-column page inside a taken range
(P8a only reports such pages). Slug text outside the page box and production metadata get their own roles and never become blocks.

Done when both books fingerprint, every font on every page has a role, and the notice assertion passes locally. The redaction
mechanism (empty tables, allowlist and denylist tests) is the next step, and chapter 1 (conditions) follows it.

P8a result, run locally: both books fingerprint, every letter of both has a font role, and the notice assertion passes for both.
The unclaimed-block failure waits for the take-list, which arrives with chapter 1.

P9 result: the redaction table (empty), the redaction stage, the hashed denylist and the allowlist gate are built and tested on generated fixtures.
Both gates fail on a pack-file shape they do not understand, name findings by file, entry number and field path (never an id, and a denylisted word
is printed as its hash), check names, keys, one-word values and list items like any other text, and exempt a sentence-initial word only when its
lowercase form is used elsewhere in the pack. Two things move to chapter 1 because they need real input:
- The CI gate test pins the list of generated pack folders (`ExpectedGeneratedPacks`, empty today) and fails on an unlisted folder, so it cannot pass
  silently. Chapter 1 adds its pack there, and adds trait keys to the game terms once the system has a trait type.
- The `degrees denylist` generator needs the take-list (to know which sections are left out) and a common-word exclusion, so a real run does not hash
  words such as "The". `Denylist.Format` is the core it will call.

## 10. Chapter 1 in three steps

1. **Prerequisite:** `condition` and `rule` entity types in the four-degrees `system.yaml`. `conditions:` and `conditionEffects` do not change.
2. **Ch1a, the pipeline, with synthetic tests and no pack committed:** the items deferred from P8a (furniture by page side, sidebars split out
   and dropped, openers dropped, tables rebuilt from word boxes, more than two columns, failing on a one-column page in a taken range), the
   take-list `Data/degrees-chapters.yaml` and the unclaimed-block failure, stage 4 (`entries.json`), the fixed YAML emitter with `--out` and
   `--check`, `import-report.yaml`, and the `degrees denylist` generator with its common-word exclusion. Trait keys wait for chapter 2.
3. **Ch1b, the first pack:** the approved `conditions` row, its redaction rows and allowlist words, `packs/rules-core/` with its manifest,
   its entry in `ExpectedGeneratedPacks`, `DegreesRulesPackTests` with counts taken two ways from the document, and the LEGAL.md paragraphs.

**Built (steps 1 and 2), and what differs from the text above:**
- Step 1: `condition` (no fields) and `rule` (one `category` text field) are declared in four-degrees `system.yaml`, modelled on srd5e. Nothing else there changed.
- Furniture by page side is a `parity: odd|even` key on a font rule (PDF page number), so the side tabs and the tab numeral are even pages only and the page number
  sits on the outer edge of each side. Both books still classify every letter.
- Openers are dropped as before but are kept in `BlocksResult.Dropped` (with the dropped sidebars) so the denylist can hash their words. A heading in the section or display font
  that runs across a gutter is one block in the first column. Three or more columns are split only for rows that say `columns: 3` or more.
- A sidebar is a block-header block and the body blocks after it, split out only on pages a "Sidebar" bookmark covers. A table is a block-header line with two or more
  cells (words more than 10pt apart) and the lines under it; columns come from the header cells, a line with no word in the first column continues the row above.
  Whitespace-only words, which the word extractor yields for each space, are dropped before lines are cut.
- The take-list holds one row per parser and target, with several paths per row. A path's range runs from after its title block to the next bookmark of the same or a higher level.
  If that bookmark starts on a page of its own the range ends at the page boundary (as the spine's page ranges do) and fails if parser-readable text sits above that page's title block.
  The conditions list has 46 bookmark children; 4 of them are sidebar bookmarks, and the 42 left match the 42 entry headers.
- Failures print counts, roles and page/column positions only: a path not in the book, a title block not on its page, a one-column page in a taken range (unless the row says
  `columns: 1`), two rows taking the same blocks, a block no parser claims, a parser not built (`statblocks`, `table`, `glossary`).
- Stage 4: the `entries` and `rules` parsers. A paragraph ends at a short line, and a bold run at a line start that is a known field label (the four degree names and a short fixed list)
  starts one. Labelled fields and degree sections are rendered into the description as `**Label.** text` because the new types have no fields for them. The trait line and the header's
  action icon are read into `entries.json` but not written to the pack: traits wait for chapter 2, and the type has no `actions` field. Parenthesized page references are dropped; other
  page references stay and are counted as `pageReferencesLeft` in the report, for redaction rows.
- `--out <pack dir>` writes the generated files of the approved rows and keeps `manifest.yaml`. It refuses a folder that holds other files and no manifest, and the root of a git
  tree. `--check` regenerates into a temp folder and lists differing, missing and unexpected files by path. `import-report.yaml` is written into the pack as a list of mappings
  (so the CI gate can read it) and into the work folder.
- `degrees denylist --book <k> --pdf <p> [--out <file>]` merges the hashes into the file (default: `denylist.sha256` in the work folder; a file with a line that is not a hash is refused).
  Left off the list: one-letter words, a word that appears in lowercase three or more times in the book, and a word in the committed, reviewed `Data/degrees-denylist-waivers.txt`
  (empty today). A word in a taken range or on the allowlist is NOT left off, so the gate can fire on a name that reached a pack; a real game term that a left-out section also
  capitalizes needs a waiver line instead of silent exclusion. Run with only the conditions rows it left about 2000 words as hashes, because nearly the whole book counts as left out.
  It should be regenerated, and committed, once every chapter's rows are on the take-list.
- After the Opus review: `--out` accepts only `four-degrees/packs/rules-core` and `rules-creatures` (and a manifest there must carry `four-degrees-rules` or `four-degrees-creatures`)
  and checks the take-list targets and folders against the `system.yaml` two folders above the pack. A table needs a header with two or more cells and at least one row whose cells each sit
  under one header cell; a two-cell header that fails that (a stat-block header, mostly) stays an ordinary block and is counted in the console line (29 tables, 1396 left as blocks on
  `pc`: the guard is conservative, and later chapters tune it). A sidebar page fails a taken range when it yields fewer sidebars than the bookmarks that start on it, or when body text
  opens a column after one that ended in a sidebar. Redaction rows take an optional `book`; a path two bookmarks share fails; an id may not repeat across types; fields, degrees and
  paragraphs render in source order, and a paragraph after a labelled one stays its own paragraph.
- Not done here: trait keys in the allowlist (chapter 2), the three other parsers, redaction rows, the manifest and the pack (Ch1b).

**Built (step 3, Ch1b), as reviewed:**
- `packs/rules-core/` holds 42 conditions and 3 rules (45 entries). The conditions are 46 bookmark children minus 4 sidebars, and 42 entry headers in the taken range, which agree.
  The rules are the appendix's own intro rule (an `own` path: the text between a section's title and its first child section, one entry named by the row's `ownName`, so
  that no section's intro is dropped silently) and its two rule sections. The manifest is `four-degrees-rules`, with the attribution and ORC Notice copied from the system and a
  Reserved Material notice that designates none of its own.
- 5 redaction rows, all `replace`, all removing a page pointer (the report's `pageReferencesLeft` is 0). The review's six rewordings were deleted: the gates were fixed instead.
  - The sentence splitter no longer reads a contraction's last letter and stop as an initial. Normalization is version 2, and an em dash stays an em dash (only the hyphen,
    en dash and minus sign become `-`).
  - The gate treats a capital after an opening quote as a sentence start, and lets a sentence open with a word the book writes in lowercase at least three times. Those words are
    committed as hashes only (`degrees-common.sha256`, written by `degrees denylist` beside the denylist); a name is rarely written in lowercase, so it stays flagged.
- Text fidelity: a space the extraction leaves before a stop, comma, semicolon or colon is removed, and a first-line indent (more than four points right of the block's edge, outside
  a bullet) starts a paragraph, so paragraph breaks no longer depend on a short last line.
- The denylist is committed and the gate no longer passes trivially. `degrees-chapters.yaml` has a `planned:` list (paths only: later chapters' depth-0 titles and three of chapter 1's
  rules sections), so "left out" means only material on neither list, plus dropped openers and sidebars. The book keys are never hashed. A planned section whose edges are not
  blocks falls back to whole pages up to the page the next section starts on (a shared page is left out, which hashes more, never fewer). The glossary is not planned yet: its
  words would flag as untaken until its chapter lands. 17 reviewed game terms and ordinary words are in `degrees-denylist-waivers.txt`. A test runs the allowlist gate (with the common
  words) over every title in the take-list.
- The CI gate test pins `rules-core` in `ExpectedGeneratedPacks`, and lists every flagged word (entry number, field path).
- Not done: rules sidebars of the conditions list are dropped
  with the other sidebars (a later `rules` row can take a named one); trailing and parenthesized page pointers are still removed by redaction rows, not by a parser rule.

## 11. Chapter 2 (glossary traits): the prerequisite and the chapter 1 leftovers

**Built (no chapter 2 run yet, and no new take-list row):**
- The `trait` type is declared in four-degrees `system.yaml` (no fields, folder `traits`), like `condition`. `PackWordCheck.TraitTerms` reads the id and name words of every
  entry in a pack's `traits` folder as game terms, and the CI gate test adds them to the allowlist. Until chapter 2 generates trait entries it adds nothing; a synthetic pack tests it.
- The take-list title test lets only a title's first word through as a common word of the book (like the pack gate); the generic words of the chapter titles are now allowlisted.
- A word on the denylist is never exempt as a sentence opener through the common list, so a one-word name that is also an ordinary word is still caught.
- The importer computes the two counts itself (`ImportPipeline.CheckEntryCounts`): the non-sidebar bookmark children of a taken `entries` section and the entry-header blocks of its
  range. A disagreement fails the run with both counts unless the row has a `countsDiffer` reason; a section with no child bookmarks is not compared. On chapter 1: 42 and 42.
  The generated `rules-core` pack is byte-identical (`--check` passes).
- Regenerating the denylist after planning the glossary needs the file deleted first, since `MergeDenylist` only adds hashes (R3 of the chapter 1 review; the rebuild belongs to the chapter 2 run).

**Built (the chapter 2 run, `glossary-traits`), and what differs from the text above:**
- `packs/rules-core/` now holds 125 traits beside the 42 conditions and 3 rules (170 entries; the conditions and rules are byte-identical to chapter 1). The `glossary` parser reads the
  `Glossary & Index` section, which is one alphabetical list of the index and the glossary with no child bookmarks and no entry-header font. An entry starts at a line with a bold
  term; a trait definition is one whose term is followed by the plain word "(trait)" and some text. The text runs to the next bold term or to a line that ends in a page list (the
  unbolded sub-entries of the index follow such a line), and a trailing page list, with a short mark of another book before a page, is cut. Every other entry, the index, is read
  past and not kept. A name is written with a capital first letter; the id is the same slug as everywhere. The entry has no fields, only the description.
- Two counts, as section 6 asks, for a section with no bookmark children and no header font: the 129 definitions plus the 24 trait lines that only point to a page or another
  book (153) against the 153 occurrences of the trait mark in the range. `ImportPipeline.CheckGlossaryCounts` fails the run if they differ and the row has no `countsDiffer` reason;
  they agree. Four of the 129 are dropped by redaction rows (see below), so the pack holds 125.
- The bookmarks end the glossary a page early: its last lines are at the top of the page the backmatter starts on, and the backmatter's title is not a block. A row may say
  `spillsOnto: <page>`; the range then also takes the first block of each column of that page when it starts at the page's top edge (within three points of the highest block of the page
  before). A column that starts lower holds none of the section. A taken range keeps a list of such extra blocks (`TakenRange.Extra`), which the take stage, the parser and the
  denylist generator all read.
- The index is not taken, so it is left out for the denylist: the parser returns the lines it read past (`GlossaryScan.Dropped`: the index, the other entries, the pointer-only
  trait lines) and the denylist generator hashes their capitalized words with the other left-out text. The words of the take-list's own titles are left off the denylist in code,
  like the book keys, since they are in every `source:` line.
- Redaction: 14 `replace` rows, each naming a hash and a length. They turn a sentence that names a place of the setting (the afterlife and outer realms, a plane, a creation myth)
  into a plain statement of the same meaning (two rows now give the trait a short description of its own, so it is not defined by a circle or by nothing). Four rows are
  `drop-entry`, for traits whose names the setting invented for a kind of being; the notice reserves proper nouns and the descriptive terms derived from them. Those four name their
  entry by `entryHash`, so the table holds no name. The denylist step hashes the capitalized words of every redacted span (not the first word of the span) and the name of every dropped
  entry, with its plural, whatever the case and however often the book writes it in lowercase: redacted text is left-out text although it sits in a taken range, and a coined name
  the book writes in lowercase would otherwise pass both gates (the name of a kept trait is a game term through `TraitTerms`). The folklore-derived kind names (an angel, a demon, a devil, a changeling and the like) stay, and the reviewer should read that line again.
  No page pointer needs a row: the page-list rule is in the parser.
- Gate changes, in place of rewording: a plural of a game term (an action or a trait written with a final s or es) is a game term; and a word the book capitalizes at the start of a
  sentence and nowhere else, and also writes in lowercase somewhere, is an ordinary opening word (`ImportPipeline.OpenerWords`), unless the denylist has it. The lowercase use is required
  because a name that only ever opens a sentence (the first of a list of sample names, say) has none. These words, and the waived words, are hashed into `degrees-common.sha256` beside the
  common lowercase words (the file has no other consumer than the opener check), but only the lowercase-three-times kind is left off the denylist. One pack sentence opens on a word with
  no lowercase use; it is a reviewed waiver. Trait names count as game terms through
  `TraitTerms`, so no trait word was added to the allowlist; two generic words of the section title were.
- The denylist was deleted and regenerated with `degrees denylist` (1,030 hashes), and so was the common-word file (4,711 hashes). The waiver file kept 16 of the 17 chapter 1 words
  (each still needed: the words are capitalized in sections the take-list leaves out; one, a word of a taken title, is now left off the denylist in code) and gained 3 (the plural of
  a trait, and two ordinary words that open a sentence in a trait's rules). A row's `spillsOnto` that is not the page the next section starts on now fails the run.
- `LEGAL.md` and the pack manifest say the pack now holds the glossary's trait definitions.

## 12. Chapter 3 (feats): the prerequisite and the run

**Built, and what differs from section 3:**
- Prerequisite commit: the `feat` type in four-degrees `system.yaml` gains `level` (number), `actions` (dropdown: one, two, three, reaction, free; words, because a bare `1` is read back as a number), `traits` (tag list),
  `prerequisites`, `trigger`, `requirements`, `special` (long text) and `frequency`, `cost` (text), with an icon and summary and filter fields. Nothing else in the file changed. Its one other user is the sheet's `feats` list
  (an entry reference); the `features: { list: feats }` source reads a pick's `modifiers`, `uses`, `spends`, `options` and `adds`, and none of the new keys collides with those.
- `packs/rules-core/` now also holds 126 feats (the general and skill feats of the book's feats chapter; 296 entries in all). The feats chapter has no child bookmarks, its entries are not in the entry-header font, and its header
  is `NAME [icon] FEAT n` in the block-header font, set in capitals. A row says `headerRole: block-header` and `levelWord: feat`: a header block has that role and ends in the word and a number, the name is put in
  title case (a small joining word stays lowercase), and the level and the action icon come from the header. The trait line (the `tag` block after the header) is a list of lowercase trait keys; all but one are keys of
  shipped traits (that word has no definition in the glossary and is kept as it is). Labelled lines (prerequisites, frequency, trigger, requirements, cost, special) become the type's own fields; the rest, with the degrees of
  success, is the description. A redaction row can name any of those fields.
- The chapter's title is two blocks in the section font ("CHAPTER 5:" above "FEATS"), so the title search accepts a run of section blocks. The range starts at the first header: the introduction and the three pages of
  overview tables before it are read past (`TakenRange.LeadStart`). They are game text of a section the take-list takes, so they are not "left out" for the denylist (as they were not when the chapter was only planned),
  and their page, which has one column, is not held to the one-column check.
- Counts, from the document four ways (`ImportPipeline.CheckLevelCounts`, run beside `CheckGlossaryCounts`): 126 header-font blocks of the header shape, 126 trait-line blocks, 126 lines ending in the level marker, and 126
  rows in the overview tables of the lead. The first three read the same slice and the same role classification, so a feat lost to a sidebar split would lower all of them; the tables are the independent count, and the
  run also compares their (name, level) pairs with the headers' as sets. Any difference fails the run when the row has no `countsDiffer` reason. By level: 67, 21, 3, 20, 1 and 14 feats of levels 1, 2, 3, 7, 11 and 15.
  The count check runs in the importer (locally); CI pins only the result (the 126 and the level distribution).
- Parser fixes, all for rows with a level word (the conditions and glossary output is byte-identical, `--check` passes): the feats' labelled paragraphs wrap with a hanging indent (a wrapped line is set in from the edge, a line at the edge
  is the next paragraph); a block the extraction cut in the middle of a paragraph is not a paragraph of its own; a block that opens with a line that is only a bold label is a small table, one row to a line; a line that begins in lowercase after a line set short by the layout continues its paragraph; a bold run-in heading at the start of
  a paragraph (a pet's statistics) is a label; a lone line in capitals set in from the column edge (a label of the page's art, three of them) is not entry text; a line that is only a page pointer continues the line before it.
  The two example tables of two feats (crafting and performance specialties) are kept as one paragraph line per row, not as table blocks. A bold run-in heading that starts with a bullet renders as a list item (`- **Name.** text`).
  Not done, from the review's nits: the stray art labels dropped are not counted in `import-report.yaml` (three here), and any bold run-in heading is a label in a run-on row, so a later chapter whose run-in headings are names
  should be read again.
- Redaction: 11 rows, all on the description. Seven turn a sentence that sent the reader to a page, a table or another book into the same sentence without the pointer, three cut a sentence that was only a pointer, and one
  removes two words the setting coined for its own dances from a list of examples. The word gates read capitalized words only, so a coined word written in lowercase passed both; this one was found by listing every lowercase word of
  the pack that the book writes fewer than three times and reading the list. `RedactedWords` now also hashes every word a row takes out, in any case (words the replacement keeps, and the ordinary words the book writes in lowercase
  three times or more, are left off), so such a word stays on the denylist whatever a later chapter says. No
  feat is dropped: none of the 126 depends on a named place, group or deity (a few say "deity" or "plane" as plain game words).
- Gate changes, in place of rewording: a bold label (`**Success.**`) is a sentence of its own, so the word after it opens a sentence (the degrees of success are in feats and the first gate version read "You" after them as the middle of one).
- Allowlist: 277 words (the words of the feats' names, skills, actions and the example lists). Waivers: 42 words (skill names, a weapon name, pet abilities and ordinary words) that a left-out section also capitalizes. The denylist was
  rebuilt from scratch with the real command (994 hashes) and so was the common-word file (4,751).
- Pinned in `DegreesRulesPackTests`: 126 feats, the level distribution, the shape of every feat's level, traits and fields (and exactly one trait key with no glossary entry), the entry total (296) and the widened `source` pattern (`pc > Chapter 5: Feats`; a chapter's own number is not a page number).
- `LEGAL.md` and the pack manifest say the pack now also holds the general and skill feats.

## 13. Vocabulary step: the book's conditions and skills

**Built (decisions in section 8, Q3; section 3 row 4), and what differs from section 3:**
- Hand modelling, not import output. `conditions:` in four-degrees `system.yaml` is the 42 names of the `rules-core` condition entries, in their capitalization; Shaken, Wobbly and Pinned are retired. `Heartened` stays as the one spell effect.
- `conditionEffects` holds what the engine can express, read from each entry's text and nothing else. Valued: Clumsy, Doomed, Drained, Dying, Enfeebled, Frightened, Sickened, Slowed, Stunned, Stupefied, Wounded. Only Dying has a maximum (4, the value at which the creature dies;
  the entry lowers that by Doomed, which the plain 4 does not follow). Frightened drops by 1 at turn end; Doomed and Drained drop by 1 at the `dailyPrep` moment, the table's night's rest. Drained also lowers `HP_Max` by its value times the level (at least 1). Penalties: Clumsy, Drained, Enfeebled and Stupefied on the rolls of one attribute (`check.<attr>`, `save.<attr>`, armor class, ranged attacks, perception and the sense DC where the attribute is Wisdom),
  Frightened and Sickened on every check and DC (including armor class), Fatigued, Fascinated, Off-guard, Prone, Unconscious and Encumbered's speed. `implies`: Confused, Paralyzed, Grabbed, Restrained, Prone (Off-guard); Dying (Unconscious);
  Unconscious (Blinded, Off-guard, and Prone that stays); Encumbered (Clumsy 1); Invisible and Unnoticed (Undetected). Grabbed and Restrained also bring Immobilized.
- Not modelled, and carried by the entry text alone: the hit points Drained takes away at once (only the lower maximum is modelled), Dying's maximum following Doomed, the floor under a speed penalty, one condition overriding another (Blinded over
  Dazzled, Restrained over Grabbed, Stunned over Slowed), conditions that depend on a sense or on who is looking (Blinded, Deafened, Concealed, Hidden, Undetected), Enfeebled's Strength-based melee attacks (a target cannot tell which melee attack
  uses Strength), actions lost to Slowed, Stunned and Quickened, the attitudes, Persistent Damage, Petrified, Broken, Controlled, Fleeing, and the death rules of Dying, Wounded and Doomed.
- Skills: the book's 16 skills with their key attributes (Acrobatics, Arcana, Athletics, Crafting, Deception, Diplomacy, Intimidation, Medicine, Nature, Occultism, Performance, Religion, Society, Stealth, Survival, Thievery) replace the six invented ones.
  The key `craft` became `crafting`. Lore, the open-ended skill (each subject counts as a skill of its own, on Intelligence), is not in the list: the sheet's skill list is fixed, and what a Lore subject is stays a decision for chapter 4.
- Homebrew pack: references moved to the closest book equivalent by effect, numbers and `homebrew` tags unchanged. Shaken became Frightened, Wobbly Clumsy (the class feature's two values), Pinned Grabbed; the hazards and background say Crafting.
- Translations (`system.ru.yaml`, `system.he.yaml`): all 42 condition names and the 16 skills, in plain game terms. They need a native review.
- Allowlist: `craft` joined the importer's game terms, because the old skill label `Craft` used to supply it and the book's activity of that name is capitalized in the feats.
- Tests moved: the four-degrees smoke test (a held condition now comes from Encumbered, a printed creature reads Frightened and Clumsy), the hazard test (crafting), the redaction fixture. New smoke tests show Clumsy alone (Dexterity save, ranged Strike, armor class, not the melee Strike),
  Drained's lower maximum hit points and Constitution save, and the Dying maximum and the `dailyPrep` drops. The inline-system tests (checks, implies, printed numbers, condition values) named the invented Shaken for a generic valued condition on other systems; it is now Frightened
  (Unnerved in the one check test whose fixture already has a valued Frightened), with their own effects, and nothing in them reads the four-degrees file.
- Not done: Stupefied's spell attacks and spell DCs (with the spells chapter), a Lore decision before chapter 7 (backgrounds grant Lore subjects), and no migration for local dev data that holds `craft` or the retired condition names (four-degrees has not shipped).
- Left: the ashfall fixture keeps its own Shaken, and generic examples in the docs and the code comments (Dread, Shaken) are not four-degrees.
