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

## 14. Chapter 4 (skill actions): the prerequisite and the run

**Prerequisite commit (hand-written):** the `action` type in four-degrees `system.yaml` (label Action / Actions, folder `actions`, icon `zap`, summary fields `skill` and `actions`, filter fields `skill` and `proficiency`). Fields: `actions`
(the feat dropdown: one, two, three, reaction, free; empty is a timed activity of minutes, hours or days, not an action), `traits` (tag list), `skill` (a dropdown of the 16 skill keys and `lore`; empty is a general skill action that several skills use),
`proficiency` (untrained, trained), `frequency`, `trigger` and `requirements`. The chapter prints only a requirements line among the labelled lines, so `frequency` and `trigger` are the type's own, for other books. The key `action` and the folder `actions` do not clash: the
sheet's `actions:` block is a layout key of `sheetRules`, the loader reserves `abilities`, `actors` and `locations`, and another system (the fitd one) already declares the same type key and folder. Russian and Hebrew labels were added in `system.ru.yaml` and `system.he.yaml`
(the type, `skill` and `proficiency`, in plain game terms, to be reviewed by a native speaker).

**Built (the run), and what differs from section 3:**
- `packs/rules-core/` now also holds 50 `action` entries and 33 `rule` entries of the category Skills (379 entries in all; the first 296 are byte-identical). The chapter has bookmarks only for the general actions, the skills and their trained-action subsections;
  the per-skill actions have none. A new `actions` parser reads a range heading by heading: a heading in the section font ("Name (Attr)") is a skill; an entry-header ending in "(Trained)" or "(Untrained)" introduces a general skill action (its prose, which names the
  skills that can use it, is the start of the action's description); one ending in "Trained Action(s)" switches the skill's actions to trained, and the prose under it (which only points to the general actions) is read past; any other entry-header, and a block-header in
  title case, is a subsection whose prose is a rule; a block-header with an action icon in its line or a trait line right below it is an action; a block-header in capitals with neither is the caption of a table. Every block of a range must fall in one of these, or the run fails.
  The entry name is the printed heading in title case (not the bookmark), `proficiency` comes from the bookmark title for a general action and from the skill's untrained list or its trained part for the others, and `skill` from the skill's heading.
- **The one-row-two-types extension, as the brief asked, is a documented row option and not a looser gate:** the `actions` row writes its prose as `rule` entries with the row's `category` (the overlap gate is untouched: one row, one range). `leadName` names the one rule made from the prose before the first
  heading (`Skill Descriptions`; the general actions' range has only a table there, which is read past). `CheckAgainstSystem` also requires the system to declare a `rule` type with the folder `rules`. Other new row options, all validated and tested: `tailFromHeader` (a `rules` row ends at its first
  block-header heading, and the rest of the section is read past and not counted as left out), and `tableRows` (a block of mostly multi-cell lines, or one that opens with a bold label line, is a table the extraction did not rebuild: one row to a line, as the line reads, since
  the gaps between its cells are too small to cut cells by). An `own`-only row (no `paths`) is a row. The `rules` and `actions` parsers start after a section's title block even when it is an entry-header; a sidebar bookmark no longer ends a section (the next real one does).
- **Take-list:** six rows. Rules (category Skills), one entry each: the chapter's own introduction (`Skills`), the checks' own text, Key Attribute, Skill Actions, Armor and Skills, Secret Checks, and Exploration and Downtime Activities (`tailFromHeader`). Actions: General Skill Actions (6 actions and 4
  subsections: Crafting Goods for the Market, Practicing a Trade, Staging a Performance, Learned Spells; and the income table as a rule of its own, see below), and Skills (44 actions; 17 skill descriptions, the lead `Skill Descriptions`, Performance's Basic Competence and Performance Traits, and Crafting's Alchemical and Magical Items).
  "Chapter 4: Skills" moved out of `planned:`.

**What became a rule and what was read past (the decision):** every skill's prose became a rule entry named by the skill, because it mixes description with rules (what Recall Knowledge a skill allows, the basic actions it replaces a roll for) and splitting it is not clean; the setting words in it were redacted
(4 rows, below). Read past, as a taken section's own text that is not rules (counted as taken, not as left out): the overview table of skills with their pages and key attributes and the table of actions by skill (the tail of Exploration and Downtime Activities, from its first caption); the proficiency table at the top of the
general skill actions; and the boxes of sample tasks and the one worked example (`Sample ... Tasks`, `... Example`), which are lists of examples by proficiency with names in them. The prose under each skill's "Trained Action(s)" heading (what the skill must be trained for, and the tradition's qualifiers) is NOT read past: it goes on in the skill's own rule entry (the review found qualifier lines only it holds). Read-past blocks are counted in `import-report.yaml` (`readPastBlocks`: the lead and tail tables, and the boxes); the boxes are also hashed for the denylist, while the tables (names of actions and skills the pack says) are game text of a taken section and are only counted, as section 12 says of a lead.
The income table is a rule entry of its own (`Income Earned`), because the book sets it in a box between two blocks of Crafting Goods for the Market's text; the entries it interrupts go on after it. The chapter's tables (the simple DCs, Income Earned, Learning a Spell, the additional traits of Performance) are rows of text, one row to a line, and none is rendered as markdown: the extraction found no table header in the table font (a caption is in
the block-header font and a header row is bold body text, over two lines for the income table), the guard that a table needs a header with aligned cells stays, and the gaps between the cells of these lines are too small to cut cells by (the income table's rows run two cells together).

**Lore (decision):** Lore's own prose is a rule entry (`Lore`); the Lore section has no action of its own (its only trained-action text is a pointer to the general Earn Income action), so there is no action with `skill: lore` yet. The `lore` option stays in the `skill` dropdown for the books that have one.
The list of Lore subjects (the sidebar of common subcategories) stays out, and so do the three example subjects in the Lore prose and the example in Practicing a Trade (4 redaction rows). The sheet's fixed skill list does not gain Lore here; how a character holds Lore subjects is decided with the backgrounds (chapter 7).

**Sidebars:** 13 sidebar bookmarks (18 sidebars, five bookmarks name two); all stay out. **The sidebar split changed:** the chapter's pages put action headings, table captions and subsection headings in the same font as a sidebar's heading, so the old rule ("every block-header run on a page a Sidebar bookmark covers")
swallowed every action after the first sidebar of a page, which is most of the skills' pages. A page now has the titles of its sidebars from its bookmarks (`Sidebars: X & Y` is two titles), and only a heading that matches one (its letters, within two typos, or a part of it at either end for a title set across a gutter) starts a sidebar. A sidebar's subheadings are in title case
and sit inside it; a heading in capitals that matches no title, an action's (icon or trait line), a caption, is the page's own and ends it; so does body text set at the column's edge, left of the sidebar's own indent, and so does a body line that opens with a bold label of an action's text (a degree of success, a requirements line): the block is cut there (a sidebar set at the column's edge, as one of the Recall Knowledge ones is, has no indent to end on, and Subsist's degrees went with it before the review). The titles are tried after the action test, so a short action name inside a sidebar's title is not taken for it. A page that finds fewer headings than titles, or has body text straight after a column that ended inside
a sidebar, fails a taken range as before. The other chapters' pages use the same rule where their headings match their titles (the first 296 entries are byte-identical, `--check` passes). 16 sidebar pages elsewhere (pages of the ancestry and class chapters) have a heading that does not match its bookmark's title; they are flagged (the import line lists them, and no taken range may hold one), and
they are split by the old rule, so the denylist hashes what it did. The old rule's drops on title-matched pages are kept (`BlocksResult.LegacyDropped`) and hashed for the denylist on pages no row takes, so a sidebar or a box that no bookmark names (a whole-page sidebar whose indent differs by a few points, boxes of hints and examples in the spells and rules chapters) is still left-out text. The denylist went from 994 to 898 hashes (101 lost, 5 gained): every hash lost is a waived word, or a word capitalized only in sections that are planned or taken (the bold run-in headings the old font rule dropped as openers, and the words of this chapter), which are game text and not left out. The 5 gained are the two restored hyphen compounds and three words of redacted spans. The pages of the spells and rules chapters that hold such boxes should be read again by their reviews. The rules-bearing sidebars left out, by title: Improving Skills; Extra Preparation (with Ending or Interrupting Tasks); Magical Traditions and Skills;
Recall Knowledge Skills; Recall Knowledge Questions; Falling; Multiple Attacks with Athletics; Leap; Forced Movement; Consumables and Ammunition; Formulas; Changing Attitudes; Commanded Animals; Examining Forgeries; Being Stealthy; Unobservable Stealth. Income Examples and Common Lore Subcategories are examples and a list of
subjects. Chapter 8 ("Playing the Game") has bookmarks for Falling, Forced Movement, Multiple Attack Penalty, Observed, Hidden and Undetected, and Earning Income (and a sidebar on skill downtime activities), by title; it has none for the Leap rule, the attitudes (the conditions appendix covers the conditions, not the steps), the Recall Knowledge tables, the
magical traditions' skills, preparation for a task, commanded animals, forgeries or consumables and formulas. A later `rules` row (section 3, row 9) can take any of them.

**Counts, from the document (`ImportPipeline.CheckActionCounts`, run beside the others):** the 23 non-sidebar bookmark children of the two taken sections (6 general actions, 17 skills; Earn Income's 3 subsections and Learn a Spell's one are depth 3, so not counted) against what the blocks hold: 6 action headings and 17 skill headings, which agree (this check accepts no `countsDiffer` reason). Action headings (a heading with an icon or a trait line below it)
against trait lines against action icons: 50, 48 and 29; they differ and the rows say why in `countsDiffer` (two jumps have an icon and no trait line, and the 21 actions that take no set number of actions have no icon, whatever their traits; every heading has one of the two). The chapter does have an overview table of actions by skill (page 228); it has two columns, untrained and trained, and no skill column
(the skills are in the table's order only). It is read past, and its cells (a new `LineInfo.Cells` list in `blocks.json`, from the gaps between words, with the page pointers and trait markers cut) give 50 distinct (name, proficiency) pairs, which equal the parsed actions' as sets; a difference fails the run whatever the row says, and so does a missing table: a `tailFromHeader` row with no heading, or a tail with no two-cell table, fails. A fourth count compares the bold degree labels that open a line of the actions rows' text (145) with the degree sections parsed, and fails on a dropped sidebar block on those pages that holds one. The skill of an action is checked by the order of the book (an action
belongs to the skill heading above it), not by the table. The import line prints all of it. Pinned in `DegreesRulesPackTests`: 50 actions, 36 rules (3 older and 33 new), the total 379, the counts per skill (general 6, acrobatics 4, arcana 1, athletics 10, crafting 3, deception 4, diplomacy 3, intimidation 2, medicine 4, nature 1, performance 1, society 1, stealth 3, survival 3, thievery 4; none for Lore, Occultism and Religion), per proficiency (31 untrained, 19 trained), per cost (24 one action, 5 two, 21 none), 18 with a requirements line, that
every trait key is a shipped trait, no `category` on an action, and the widened `source` pattern (`pc > Chapter 4: Skills > Skills > Medicine`, up to a section and a skill; no page numbers). The 15 new tests in `DegreesSkillActionsTests` drive the sidebar split, the `actions` parser, the table and the counts with synthetic books.

**Ids:** an action's id is its name's slug, and an id may not repeat across types (a redaction row names an entry by id alone). When an action's slug is another type's id (a feat, a condition, a trait or a rule), the action takes the suffix `-action` (`EntriesStage.ActionIdSuffix`, tested with a synthetic clash). No action name of this chapter clashes, so the suffix is used nowhere in the pack
yet. The book writes no two actions of one name, so two of one type with one id still fail the run.

**Parser fixes, and what they did to the earlier chapters (nothing: `--check` passes, the first 296 entries are byte-identical):**
- The font file's opener rule took every `TimesNewRoman` face. The bold roman at body size is the run-in heading of a list item (an action's name in a skill's list of what it can Recall), and the run lost its words. The opener rule is now the bold-italic face; the bold roman is `rules` at body size and an opener at 12 points and up.
- A bullet in a plain face before a bold run-in heading is part of its label (`• Name`), so the heading becomes a label (`- **Name.** ...`).
- A block whose lines start at four or more different places (text set around a piece of art) has no first-line indents, so its lines are not paragraphs. A general action's introduction is read as prose, with first-line indents, and ends the paragraph before the action's own text, which is set with hanging indents like a feat's. For a `tableRows` row, a line that begins in lowercase after an unfinished line continues it.
- A sidebar bookmark does not end a section, and a `rules` or `actions` section starts after its title block.
- The three review findings of chapter 1 were already fixed before this run and have tests: fields and degree sections keep their source order, and a paragraph after a labelled one stays its own (`ADashAtALineEndJoinsWithoutASpace_AndFieldsAndDegreesKeepTheirSourceOrder`); the table detector needs a header with two or more cells and rows that sit under its cells, so prose under a two-cell header is
  not a table (`ATwoCellHeaderWithProseUnderIt_IsNotATable_AndIsCountedAsRejected`); a missing next title at a page boundary fails the run (`ARangeFailsWhenTheNextSectionsTitleIsNotABlockOnItsPage`).

**Redaction:** 29 new rows, so 63 in all (50 replace, 9 cut, 4 drop-entry), 0 unmatched. 19 remove a pointer to a page, a table, a sidebar, an example box or another book (13 `replace`, 6 `cut`; the importer's own table captions are kept: the entry that holds a table says it); 4 name the planes of the setting in the lists of what a skill can Recall (each becomes "the planes tied to" the skill's subject, or just "planes");
4 replace a list of example Lore subjects (the three in the Lore prose and the one in Practicing a Trade, which are ordinary words as much as names; a word one of them took out is on the denylist, so a later chapter that says it needs a waiver) with plain words; 2 restore the hyphen two compounds lost at a line end. The sweep of lowercase words the book writes fewer than three times (264 words of the new text, read as a list) found no coined or setting word; the hyphen artefacts were the only odd ones. The boxes of examples the actions parser reads past are hashed for the denylist with the other left-out text (`EntriesStage.ExampleBoxes`).

**Gates:** the denylist (898 hashes) and the common-word file (4,759) were deleted and regenerated with the real command, since the chapter is no longer planned. Allowlist: 55 words, 52 of them game terms (the words of the actions', subsections' and tables' names, gerunds of action names the text capitalizes, and the generic words of the chapter's titles), except three ordinary words the gate itself needs, tried out and put back: `assembling` and `fixing` open a sentence and are rarer than the three lowercase uses that exempt a sentence opener, and `when` opens the paragraph after a table row that ends without a stop. Waivers: 21 words, each an action name or a form of one, the abbreviation of a non-player character, or an ordinary word that an untaken section (the index, mostly) also capitalizes; the ordinary ones (`alterations`, `assisted`, `circus`, `exceeding`, `fix`, `investigating`, `locks`, `lowered`, `remedy`, `unobtrusive`, `orate`) are in the pack's text, which the denylist gate would otherwise fail on, and could only be avoided by redacting more of the book's sentences.

**Not done:** the key attribute of each skill is in `system.yaml`, not in its rule entry (the heading's abbreviation is not text of the entry); the income table and the table of spell prices are rows of text, not markdown tables; no action carries `skill: lore`; a skill's untrained and trained lists are not linked to the general actions that name the skill (the general action's own text names them); the Recall Knowledge and Earning Income sidebars wait for the `rules` chapter. The
21 actions with no icon (those that take no set number of actions) have an empty `actions` field; nothing else marks them as such beyond their traits. A phrase-hash list beside the word denylist, for multi-word names made of common words (the planes of the setting), is not built; the pack holds none today. `import-report.yaml` now has one row for each type a row wrote (an actions row also writes rules), and `readPastBlocks` where a row reads blocks past. A title-matched sidebar split cannot tell a sidebar from the page's text where their indents differ by a few points and no label ends it (a whole-page sidebar of the spells chapter): that is why the old rule's drops are hashed.

## 15. Chapter 5a (spells): the prerequisite and the run

Chapter 7 of the book is two steps: 5a (this one) takes its spellcasting rules and the spells and cantrips of the four spell lists; 5b will take the focus spells and the rituals, which are the last two children of the book's "Spell Lists"
(`planned:` in the take-list names exactly those two paths, so they are not counted as left out text and nothing of them is read).

**Prerequisite commit (hand-written):** the `spell` type in four-degrees `system.yaml` (label Spell / Spells, folder `spells`, icon `wand`, summary fields `rank` and `kind`, filter fields `rank`, `kind` and `traditions`). Fields, each from what the entries print:
`kind` (a dropdown: spell, cantrip, focus, the word the header sets before the rank; a dropdown and not a yes/no so the focus spells of 5b need no second change), `rank` (a number; a cantrip's printed number, which is 1 for every
cantrip of the spell lists, is the rank it is cast at), `actions` (the feat dropdown: one, two, three, reaction, free; empty for a spell with no action icon), `traits` and `traditions` (tag lists; the four tradition keys), and the labelled lines the stat block prints:
`castTime` (text: the minutes or hours of a long casting, and the range of action costs of six spells), `cost`, `trigger`, `requirements`, `range`, `area`, `targets`, `defense` and `duration`. There is no `heightened` field: the book prints each heightened
entry as a labelled line at the end of the text, so they stay in the description, in their source order (see below). The key `spell` and the folder `spells` clash with nothing: the loader reserves `abilities`, `actors` and `locations`, the sheet has no `spells` key, the
canonical-label guard has nothing to say, and the srd5e system's own `spell` type is another system. Russian and Hebrew labels for the type, `kind`, `rank` and `traditions` were added to `system.ru.yaml` and `system.he.yaml` (plain game terms, to be reviewed by a native speaker).
Stupefied's spell attacks and spell DCs stay not modelled (the spell attack modifier and DC are not part of an entry).

**Built (the run), and what differs from section 3:**
- `packs/rules-core/` now also holds 324 `spell` entries (299 spells and 25 cantrips) and 35 `rule` entries of the category Spells (738 entries in all; the first 379 are byte-identical, `--check` passes twice). Section 3 had the chapter as `entries` + `table`; the lists are
  read past by a new `spelllists` parser and the spells by the `entries` parser with a level word, as the feats are.
- **Spells.** A header is `NAME [icon] SPELL n` or `NAME [icon] CANTRIP n` in the block-header font. A row's `levelWord` can now name several words (`spell|cantrip`); the one found is the entry's `kind`, and the number is its `rank`. The name is put in title case, as the feats' names are.
  Six spells have a range of action costs in the header (`[1 action] TO [3 actions]`, and two to three): the dropdown names one cost, so `actions` stays empty and `castTime` says "1 to 3 actions". The trait line is lowercase trait keys (a line that wraps is two tag blocks, read as one);
  the labelled lines become the type's fields (`Cast` is `castTime`, `Target` and `Targets` are `targets`). A second label in the middle of a line, after a semicolon (a line of the shape `Label value; Label value`, which the stat block prints for its range and targets, its defense and duration, its cast time and requirements), is cut there: blocks now keep the bold runs of a line
  after its leading label (`LineInfo.Bold`), and only a bold run that is a stat label starts a field. Any other bold run at a line start (the attack lines of a battle form, a stray label of the page's art) is plain text. The rank of a heightened entry that the book sets in the plain face
  (`Heightened` then `(+1)`) is made part of the label, so every entry reads `**Heightened (+1).** ...`. Degrees of success and heightened entries stay in the description, in source order, as `**Critical Success.** ...` and `**Heightened (+1).** ...`: a heightened entry is a labelled line at the end, not
  a section of its own. A line set in from the edge under a paragraph with no label of its own is the hanging indent of an inset (a reaction, an affliction), so it continues the paragraph; the spells' text has no first-line indents. A spell whose slug is the id of another entry takes the suffix `-spell`
  (`EntriesStage.SpellIdSuffix`, tested with a synthetic clash, and pinned here): `darkness-spell`, `fear-spell`, `light-spell`, `possession-spell`, `scrying-spell` and `sleep-spell`, each named like a trait. No other id clashes with any type.
- **Rules sections.** One `rule` entry for the chapter's own introduction (`Spellcasting`) and one for each of the 15 sections: a section that has child bookmarks is its own text before its first child (an `own` path, now named by its last title when the row has no `ownName`), and each child is an entry of its own (17).
  A heading with no bookmark inside a section (the sub-subsection of Heightened Spells, the name of the Refocus activity, the format line of the example under Reading Spells) is a paragraph of the entry (`subheadings`); the Refocus activity stays in the text of Focus Spells and does not become an action entry.
  The example under Reading Spells is set ragged-right with hanging indents, like a feat's text, so body-font blocks of a `subheadings` row are read in the run-on way and its labels (Tradition, Cast, Range, Area, and Targets, Defense and Duration, Heightened) render as bold run-in headings.
  Sections whose text goes on across a page break above the next title (the next page or two are sidebars) need their path named in the row's `runsToTitle`, which takes the blocks up to the next title instead of failing; an own-text path skips a sidebar bookmark that comes before its first child.
- **Boxes among the spells.** The chapter sets three boxes with no bookmark between its spells, each a heading in the header font that has no spell's shape: a table of the highest creature level a summoning spell can bring, the statistics of the creature of one spell, and a table of another book's dragons for the dragon form spell.
  The row names the first two (`boxes`): each is a rule entry of the category Spells (`The Summon Trait`, `Phantasmal Minion Statistics`). The third is read past (`readPastBoxes: 1`; the number must be the one stated, so a box is never dropped silently): it is a list of the creature kinds of a book this importer does not read, and its words are hashed
  for the denylist with the other left-out text.
- **Sidebars:** 5 sidebars in 4 sidebar bookmarks (the book has `Sidebars: X and Y` here, not `&`: the title splitter now splits the plural bookmark on ` and ` when it has no ampersand, as `Sidebars: A and B & C` stays two). All stay out. The rules-bearing ones, by title: Magical Traditions; The Four Essences; Other Spell Traits (a glossary of
  traits with their rules: darkness and light, minion, summoned, morph, polymorph, illusion); Subtle Spells; Spellshape. The one on other spell traits holds the only rules the pack lacks for several traits it names (`morph`, `polymorph`, `summon`, `minion` and `summoned` have no entry among the glossary's trait entries; `darkness`, `light` and `illusion` have one), and the one on spellshape the rules of a trait with no entry either (`subtle` has one); they stay out here and are listed under not done. A later `rules` row can take any of them.

**Counts, from the document, independent of the spell parser's text handling:**
- **Headers, trait lines, traditions lines:** 325 spell headers in the range, 325 trait lines (one under each header; a box's own creature line is not one), and 325 lines that open with the traditions label. They agree (a row's `countsDiffer` would let them differ; this row has none).
- **The four lists:** the lists (a heading per rank, and a bold name at each entry's line start; two letter marks per line are read past) name 229 arcane, 136 divine, 192 occult and 166 primal spells at their ranks, 723 in all. The union of the (name, rank) of each tradition's list is compared as a set with the
  (name, rank) of the parsed spells that say that tradition, per tradition, and a mismatch fails the run (`ImportPipeline.CheckSpellCounts`); they agree. A spell on no list would fail it too unless the row says why (none is: 0). That comparison caught the six spells whose header has two icons ("Bind" read as part of a name), and a table the
  detector read two spell headers as (below).
- **Rule sections:** the non-sidebar bookmark children of every own-text section against the headings of the right font up to the last child's own (a heading in the section font under a chapter, the entry-header font under a section), `ImportPipeline.CheckRuleSectionCounts`: for this chapter the chapter has 16 children and its
  16 headings, and six sections have 3, 3, 3, 4, 3 and 1 (33 in all); the import line adds the earlier chapters' own-text sections (44 = 44 in 10 sections).
- **Degrees and heightened entries:** the bold degree labels (and heightened labels) that open a line in the spells range are counted against the degree sections and heightened entries the parser made, as the actions are (339 and 265), and a dropped sidebar block on a taken page that holds one fails the run.
- Pinned in `DegreesRulesPackTests`: 324 spells (299 spells, 25 cantrips, none of kind focus), 58, 55, 34, 33, 39, 24, 20, 15, 12 and 9 spells of ranks 1 to 10, every cantrip printed as rank 1, 229, 135, 192 and 166 spells in the four traditions (the divine count lost the dropped spell), 8 with one action, 229 with two, 35 with three, 4 reactions,
  48 with no icon (42 with a cast time and 6 with a range of actions), the counts of each labelled field (254 range, 67 area, 173 targets, 116 defense, 226 duration, 4 trigger, 3 requirements, 2 cost), the 339 degree sections and 265 heightened entries, the six suffixed ids, the source (`pc > Chapter 7: Spells > Spell Lists > Spells`), the 35 rules (the 15
  sections, the 17 children, the introduction and the two boxes) and the entry total (738). The trait line names shipped traits except eight words the glossary has no definition for: the rarity word, two kinds of changes of form (`morph` and `polymorph`), `summon`, `sanctified`, `nonlethal` and the two luck traits.
  The 13 new tests in `DegreesSpellsTests` drive the header, the stat lines, the range of costs, the boxes, the lists as the count, the guard, the sections and the shared fixes with synthetic books.

**Shared fixes, and what they did to the earlier chapters (nothing: `--check` passes, the first 379 entries are byte-identical):**
- A table needs a row with two or more cells that is a body line: another header line with two cells under a header (two stat-block headers in a row whose second cell is far from the first) was taken for one. A page of the spells (two spells) was read as a table and its spells lost; 28 tables are rebuilt now, from 29.
- A whole-page sidebar whose title is split across the gutter: the first body line of the second column (the last word of the sentence that ran over) set the indent of the sidebar's text and ended it at once, so the rest of the page was read as the section's text. A single line at the top of a column that follows one that ended inside a sidebar no longer sets the indent. The page of the traits glossary between two pages of the targets section was the case.
- `runsToTitle` is a list of bookmark paths, not a flag of the row: only Heightened Spells, Innate Spells and Targets (and the four tradition lists) may run over a page break above the next title, and any other section still fails there (a test pins it), so a sidebar whose split fails cannot be absorbed silently into the section before it.
- A stray label of the page's art (a lone line in capitals set in from the column's edge) is skipped when the column has no spell header too: the edge is then where the column's other text starts. Seven such labels are skipped in the spells range (the import line prints the count); one had opened the text of a spell before.
- Ids are stable: an id that has shipped in the committed pack keeps its entry. The importer reads the ids of the pack folder it writes (`PackEmitter.ShippedIds`), and when two entries of different types share a slug, the one whose folder holds that id there keeps it and the other takes the suffix of its type (`-spell`, `-action`, or `-` and its type), so a
  later chapter's rule or action named like a shipped spell is the one suffixed (tested with a synthetic later-chapter clash); with nothing shipped under the id the earlier rule applies (the entry that is neither an action nor a spell keeps it).
- A hyphen after a number at a line end is the compound's hyphen and stays (three spells had lost a space after it); a suspended hyphen before `and`, `or` or `to` is not joined. A plural sidebar bookmark that says `and` (and no ampersand) gives titles that must match their headings exactly, not by a part of one, so a wrong split flags its page instead of dropping a heading.
- The description of the `spell` type's `actions` and `castTime` fields now says that the range of action costs of six spells is the cast time. The avatar spell's name is on the word denylist as a dropped entry's name, which no waiver can lift: the classes chapter's two feats that name it must be reviewed with it (they depend on it too); until then the word fails loudly, which is harmless.
- Own-text ranges skip a sidebar bookmark in front of the first child; `ownName` is optional (a row with several own paths names each by its last title). The level-count check counts a run of tag blocks as one trait line, and its header expressions take `spell|cantrip`.

**Redaction:** 25 new rows, so 88 in all (66 replace, 17 cut, 5 drop-entry), 0 unmatched. 15 remove a pointer to a page, a section of another book or a sidebar (10 `replace`, 5 `cut`; a pointer by section title that the pack has as a rule entry stays by name: Setting Triggers, Identifying Spells, Phantasmal Minion Statistics);
9 name a place or a deity of the setting (6 `replace`: the afterlife's keeper and its realm in the raise dead spell, three planes in the interplanar teleport, the shadow plane in two spells, and the forge of creation in one; 3 `cut`: the example sentences of the three summon spells that named a deity and kinds of beings the setting invented); and 1 `drop-entry`, the
avatar spell (`avatar`), whose mechanic is a list of battle forms named for each of the setting's deities (no row names a reserved word; the drop row names the spell, not a deity, because the word is an ordinary one). The sweep of the lowercase words the book writes fewer than three times (775 words of the new text, read as a list) found three coined kinds of beings, a weapon name and two ability
names, all inside the sentences and the spell that the rows remove; the pack says none now. The capitalized sweep is the allowlist gate (441 words flagged at first): every one was read, and the redactions above are what it found besides the names of the spells; the words of the spells' names are ordinary English words and game terms (no name of the setting among them).

**Pair guard (review finding):** two words that pass the word gates one by one can still be a setting's name together (a name made of an ordinary word and a game term). The review found one: the setting's product-line name, whose two words are a word of the title of a section of this chapter and a word of one spell's name, so both are
allowlisted and one is waived, because the pack's own names need them (no entry says the name). A committed `Data/degrees-pairs.sha256` (931 hashes of two lowercased words) now holds every pair of adjacent capitalized words in the text the take-list leaves out, except a pair with a word already on the word denylist, a pair in a take-list title, and the 88 reviewed pairs the waiver file names, each by name (the
names of actions, skills, defenses and statistics, the example Lore subjects of the skills chapter, and two words that meet across a label or a bullet; none names the setting). A pair is not left off because the taken text says it too, so the day a setting's name reaches a taken range its pair stays on the list. `degrees denylist` writes it beside the word list, the denylist test (`PackWordCheck.CheckDenylist`) checks the pack's text with it and reports a pair by its hash, and a test with invented words proves that a pair of unlisted words is caught and that game text does not hide a pair while titles, denied words and waived pairs are left off. A multi-word plane name or group name whose other
word is ordinary is caught the same way (three of them, whose one distinctive word was waived or allowlisted in this chapter, are on the list); the ordinary words that were the only hashable word of such a name are guarded by the pair list and the allowlist gate.

**Gates:** the denylist (862 hashes) and the common-word file (4,890) were deleted and regenerated with the real command, since the chapter is no longer planned (the focus spells and the rituals still are). Allowlist: 465 words, all game terms or the words of spells' names (the sizes, the animals and forms some spells offer, the abilities of a form, the abbreviations of the
statistics in the creature line of one box), except 12 ordinary words the gate itself needs (`a`, `i`, `if`, `the`, `with`, `you`, `once`, `p`, `non`, `name`, `furthermore`, `beating`: each opens text after a label, an action icon or an ability's name on the same line, or is a word of a spell's name that the book writes in lowercase fewer than three times). Waivers: 46 words and 88 pairs, 27 of the capitalized gate (the actions
dismiss, refocus and escape, two sizes, the eight statistic abbreviations, `furthermore` and `beating`, and eleven ordinary words of spells' names that the index or a left-out section capitalizes) and 19 of the lowercase gate: `beverages`, `brand`, `discrete`, `drain`, `expanse`, `improving`, `interacts`, `mantis`, `outer`, `pot`, `repeat`, `repetition`, `summer`, `swipe`, `therefore`, `transformation`, `vampire`, `vicious` and `web`. Each is an ordinary English word of a spell's text (a verb, a noun, a season, an animal or a thing a spell makes: the word in the sentence it stands in was read), which the book capitalizes in the index or a left-out section only as a name or a title; none is a coined or setting word.

**Not done:** the rules of the traits the spell sidebars hold (`morph`, `polymorph`, `summon`, `minion`, `summoned`, `spellshape`: no trait entry for them, and the sidebars are left out; the summon rule's table of creature levels is in the pack as a rule); the focus spells and the rituals (5b: their headers are `NAME [icon] FOCUS n` and the rituals have their own shape; the page of the first focus spells has a table the detector still rebuilds from a spell header that sits over a row of body text, so 5b must read the same shape); the spells' own traits that the glossary does not define
(8 words, above); the two letter marks of each line of the four lists (read past; they are not parsed); the heightened entries are not a field of the type; a spell that takes a range of actions has no action cost field (its `castTime` says it); a few insets inside a spell's text (a reaction a spell grants, an affliction's stat line, a creature's special abilities) are one paragraph with their lead line, not separate paragraphs, since the
layout has no hint of where they start; the three boxes' pages should be read again by the reviewer; and the table of another book's dragons is read past and hashed.

## 16. Chapter 5b (focus spells and rituals): the prerequisite and the run

The last two children of the spell lists: the focus spells of the six classes that have them, and the rituals with the rules of casting one (`planned:` had both; they are taken now, so `planned:` holds no section of Chapter 7).

**Prerequisite commit (hand-written):** four additions to the `spell` type in four-degrees `system.yaml`, each from a line the entries print and no existing field can hold.
- `kind` gains `ritual`. The header of a ritual sets the word before the rank as the others do (`NAME RITUAL n`), and the importer reads that word into the kind. A ritual is not a spell cast from a slot: it takes hours or days, has no action cost, and is cast with skill checks, so a filter by kind separates it; a flag or a second type would have been a second place to say the same thing.
- `focusGroup` (text): the heading the book sets a focus spell under. The trait line names the class, but not the domain, order, school, tier or hex group (a domain's name is not a trait), and the group is what a player picks from.
- `secondaryCasters`, `primaryCheck` and `secondaryChecks` (text): the labelled lines only a ritual prints. They sit between the requirements and the range in the type's order. The first is a count with a condition now and then (`1, must be ...`), so it is text.
Russian and Hebrew labels for the four fields (plain game terms, to be reviewed by a native speaker). Nothing else in `system.yaml` changed. The labels of the focus group's other lines (a bard's muse, a witch's lesson) stay in the description: they name a game term of one class, and a field per class would not be filtered by anyone.

**Built (the run):** `packs/rules-core/` now holds 911 entries (738 before, byte-identical: `--check` passes twice and `git diff` shows additions only). New: 147 `spell` entries of the focus spells and focus cantrips (131 with `kind: focus` and 16 with `kind: cantrip`, as the header says: the hex cantrips are focus cantrips and are `cantrip`), 19 `spell` entries with `kind: ritual`, and 7 `rule` entries of the category Spells.
- **Groups.** A new row option, `groups`, says that the heading of the section font (a class) and the heading of the entry-header font under it (a domain, an order, a school, a tier, the hex cantrips) name a group: each entry after it says it in `focusGroup`. The range starts at the first heading. Three headings hold no spell of their own (a class whose entries are all under its groups); 56 groups hold spells. A druid's one entry before its first order, a bard's compositions and a witch's hexes sit directly under the class heading, and are grouped by it. No group has any text of its own (the bookmarks of the six classes have spells and nothing else), so there are no group rules to take.
- **The shape the section 15 note named.** The first focus-spell page, and 7 more headers of this section (8 in all), were read as tables: a header whose second cell (the kind word and the rank) sits over a stat line of two cells is built as a table header by the detector, and the entry was lost. An entry's header (`NAME [icon]` then `SPELL|CANTRIP|FOCUS|RITUAL|FEAT n`) is never a table's header row now (`BlocksStage.EntryHeaderCell`); a test pins it with a synthetic header. The tables rebuilt in the whole book fall from 28 to 3 (the other 17 are headers of the same shape in sections no row takes yet); the 738 entries are the same bytes. Their lines are blocks of the left-out text now, and 11 more blocks fall in sidebars, so the denylist and the pair list gained words and pairs from them, which the review below read one by one.
- **Rituals.** `NAME RITUAL n`, a trait line (one word, the rarity), and the stat lines: `Cast`, `Cost`, `Requirements`, `Secondary Casters` and `Primary Check` and `Secondary Checks`, then the spell's `Range`, `Area`, `Targets`, `Duration`. The second and third labels of a line come after a semicolon, and the bold runs that mark them are sometimes cut by a line break or read as one word, so a stat part is split at a semicolon that comes before one of the three ritual labels, whatever the runs say (`BodyReader.SplitRitualLabels`; tested with both shapes). A header with `OR` between two action icons is a range of costs like `TO` (`heal companion`: castTime `1 or 2 actions`; five focus spells have a range, 2 of them to three actions).
- **Rules.** `Casting Rituals`: its own text, and its five children (`Learning Rituals`, `Cost`, `Secondary Casters`, `Checks`, `Effect`). The grandchild `Secondary Checks` has a bookmark and a heading in the block-header font, not the entry-header one, so a section of its own would not pass the heading count; it is a paragraph of `Checks` (`subheadings`), with its four degrees. The list of the rituals by rank sits in the text of `Casting Rituals`, one row to a line (`tableRows`), under its own heading as a paragraph. The box of costs by creature level among the rituals is the rule `Creature Creation Rituals` (`boxes`). Their ids are generic (`cost`, `effect`, `checks`), as the book's titles are; a later chapter that names a section so takes the suffix of its type.
- **Muse, lesson and patron.** Three class lines open some focus spells: a bard's muse, a witch's lesson (the theme of a hex) and a hex cantrip's patron. They are labelled paragraphs of the description (`**Muse.** ...`). The patron lines are cut (below).

**Counts, from the document:**
- **Groups:** the bookmarks below `Focus Spells` (6 classes, 37 domains, 4 orders, 4 tiers, the hex cantrips and 7 schools: 59) against the headings of the section font and the entry-header font in its range (59). A heading with no bookmark, or a bookmark with no heading, fails the run (`CheckEntryCounts` with `groups`; tested).
- **Headers, trait lines, markers:** 147 headers = 147 trait lines = 147 lines that end in the kind word and a rank, for the focus spells; 19 = 19 = 19 for the rituals (`CheckLevelCounts`). The spells of the four lists are counted on their own and against the lists as before (`CheckSpellCounts` takes the row whose level words include `spell` only: `TakeRow.OnTraditionLists`): 325 headers = 325 trait lines = 325 traditions lines, 723 (name, rank) pairs, unchanged. The focus spells print no traditions line, so there is no count of that kind for them, and no `countsDiffer` reason was needed: nothing differs.
- **An independent list.** The four tradition lists name none of the 166 focus spells and rituals (checked by name: none of them is on a list), and the class chapter (Chapter 3, which would list a class's focus spells) is still planned, so its pages are not taken: the focus spells have no independent list, and the three counts above are the document's own. The rituals have one: the book's table of the rituals by rank (19 rows, each a rank and a name) is read, and its set of (name, rank) pairs must equal the entries' (`CheckRankLists`, run by `listHeading`; tested with a synthetic list that misses one). They are equal: 3, 2, 3, 3, 4, 2, 1 and 1 of the ranks 2 to 8 and 10.
- **Degrees and heightened entries:** 512 degree sections and 349 heightened entries over all the spells rows (339 and 265 before: 101 and 73 in the focus spells, 72 and 11 in the rituals), counted from the bold labels at line starts against what the parser made (`CheckSpellDegrees`, unchanged).
- Pinned in `DegreesRulesPackTests`: 490 spells (324, 147 and 19), 131 focus spells and 16 focus cantrips, their rank distribution (64, 4, 5, 46, 6, 1, 1, 1, 1 and 2 of the ranks 1 to 10 for the focus ones; 10, 2, 2, 0, 1, 0 and 1 of the ranks 1 to 7 for the cantrips: the book prints a rank on each focus cantrip, unlike the cantrips of the lists), the class of each (19 bard, 74 cleric, 9 druid, 11 ranger, 20 witch, 14 wizard), the 56 groups and their sizes (37 domains of two spells, a rank 1 and a rank 4; 7 schools), the actions (62, 51, 1, 21, 7 and 5 with none: the five have a range of costs as their cast time), the counts of each labelled field, the 101 and 73, the nine trait words the glossary has no definition for (`composition`, `fortune`, `hex`, `morph`, `nonlethal`, `polymorph`, `sanctified`, `spellshape`, `uncommon`), the rituals' ranks (3, 2, 3, 3, 4, 2, 1, 0 and 1 of the ranks 2 to 10), their 72 and 11, the three ritual fields on every ritual, the 7 rules, the 78 rules and the total (911). The source pattern widens to the rituals' rules, four sections deep. `DegreesFocusSpellsTests` has 5 tests over synthetic blocks (the group of an entry, `or`, the labels of a ritual cut with split bold runs, the rank list, the heading count, the row rules).

**Stable ids:** no focus spell or ritual has a slug that is the id of another entry (a clash would have been suffixed, as section 15 says; the run fails on two entries of one type with one id). So no entry has a new suffix, and the six of section 15 are unchanged. The earlier 738 ids are untouched.

**Reserved material.** The risk of the chapter, read by hand:
- The 37 domain names, the orders, tiers, 7 school names and the hex group names are ordinary words or general terms (a domain is a theme: air, luck, travel; the schools are named for a discipline of magic), and none of them is on the denylist except as an ordinary word that a left-out section capitalizes (`initiate`, `mentalism`, `indulgence`, `secrecy`: waived, each named in the waiver file). No deity is named in a title, a group or an entry's name. The school names are not coined places or persons (`Ars Grammatica` is a Latin phrase for a discipline, `The Boundary` two ordinary words), so they stay.
- **Cut:** the patron line of the 7 hex cantrips (`Patron ...`): each names a patron the setting coined (one of them was a title on the denylist; the others are names of the same kind, a place, a being or a title). The mechanic of a cantrip does not depend on them; 7 `cut` rows. The lesson of a hex is an ordinary word and stays.
- **Replaced:** 5 sentences whose example named a deity or a place of the setting: two deities (consecrate; the gate flagged both words, as it did the others here), the realm of the dead and the same deity in the resurrect ritual (2 rows), the plane the planar displacement ritual names as an example, and the city of the pulse of civilization spell's Lore example. The sentence's mechanic is kept in plain words. 3 more rows remove a pointer to a page (charming touch, cloak of shadow, and the cost rule, which points to the creature creation table and says its title instead); the pointer by title that the pack has as a rule entry stays by name. Two more rows come from the review: an ordinary word the book capitalized as the setting's name for its cosmos is lowercased (`binding-circle`), and a misprint of an ordinary verb is corrected (`blight`). In all 17 new rows: 105 in all (76 replace, 24 cut, 5 drop-entry), 0 unmatched.
- **Sweeps.** The lowercase words of the new text that the book writes fewer than three times (465 words, read as a list and by context): none is coined; they are ordinary words (rare verbs and nouns of the new text, with the possessive and contracted forms the count makes of an apostrophe). The capitalized sweep is the allowlist gate (236 words flagged at first, 18 of them on the denylist): every one was read in its sentence. The 18 are the 6 above (two deities, a city, the realm of the dead, a plane and a patron's title: all redacted, none named here) and 12 ordinary words (waived; below); the rest are the words of the spells' names, the groups' names and the rituals' stat lines.
- **Allowlist:** 236 new words (1,152 in all), each a game term (a word of a focus spell's, ritual's or group's name, a trait or a stat line's word: `check`, `primary`, `secondary`, `casters`, `cost`, `ritual`) or an ordinary word the gate needs, which are these: `required`, `higher`, `lower` (the heading of the table of costs and the ranges of a degree), `its`, `this` and `into` (the word after a label or a bullet, and a small word that title case does not lower in two names). One misprint of an ordinary verb in the book (a word a sentence opened with) is corrected by a redaction row and is not on the list, and an ordinary word a sentence capitalized as the setting's name for its cosmos is lowercased by another. The ten waived capitalized ordinary words (`allegro`, `hustle`, `indulgence`, `initiate`, `mentalism`, `overture`, `restoration`, `secrecy`, `uplifting`, `whispering`) are on it too: musical terms, an exploration activity, domains, a tier and words of spells' names.
- **Waivers:** 37 new (252 in all): 19 words (the 12 above, and `alternatively`, `creations`, `prioritize`, `resentment`, `unbound`, `vault` and `wilding`: ordinary words of the new text that a left-out section writes only at a sentence start, or in an index, or that a cut line used together with a patron's name; `wilding` is a word of one cantrip's name and `resentment` an emotion a cantrip's text names) and 18 pairs, each read in its sentence or list in the pack (the review found that a pair of the older label of a saving throw hid a field the parser did not read; it is parsed now and its pair is not waived): two skills or terms that meet across a comma or a bullet, a Lore subject, the name of a spell or a form, a tier's heading, and four pairs of a label or a sentence's last word and the next sentence's first word. None names a person, place, deity or creature.
- **Gates:** `degrees denylist` now takes a waived word and a waived pair out of the committed files (it only ever merged before, so a waiver written after the hash was committed could not lift it without deleting the file). The lift is scoped: a waived word or pair is lifted only when this book's own left-out text would hash it without the waivers (a waiver reviewed against one book removes nothing another book's run put there), and a waiver that equals the name of a drop-entry row, or its plural, fails the command (`ImportPipeline.LiftedWaivers`; tested). Denylist 854 hashes (862), pairs 929 (931) and common words 4,923 (4,890).

**Not done:** the focus spells of the classes are not tied to the classes' own entries (the class chapter is planned); the group is text and a filter, not a link. The class lines (a bard's muse, a witch's lesson) are text, not fields. The patron of a hex cantrip is gone (above). The rules of the traits the spell sidebars hold are still not in the pack (section 15). The table of colors of one focus spell (`creative splash`) is read as one row to a line, and two of its rows wrap onto a second line. The ids of the rituals' rule sections (`cost`, `effect`, `checks`) are generic. A focus spell's `cantrip` kind has a printed rank like a spell's, where the cantrips of the four lists print none to read as the rank they are cast at; the rank of a focus cantrip is the printed number, and nothing here models how it is cast. The loader reads a quoted number (`secondaryCasters: "2"`) as a number, which a text field shows as its digits, so the field's values are of two types (the loader should keep strings for a text field). Two hex cantrips' patron names are made only of ordinary words (one is waived and common), so only the capitalized allowlist gate guards them; a phrase-hash list would, and the class chapter's import should add it. A hyphen at a line end in `lingering-composition` is lost, the three level bands of `fatal-aria` are plain lines, and the focus spells have a partial independent list on the class pages (some names of a bard's and a witch's), which a later run could compare.

## 17. Chapter 6a (armor, shields, weapons): the prerequisite and the run

The first half of Chapter 6: its rules sections, then the armor, the shields and the weapons, which are the items a character sheet uses. The gear, with all its children (adventuring, alchemical and assistive gear, formulas, services, cost of living, animals), is 6b; `planned:` now holds `Chapter 6: Equipment > Gear` in place of the whole chapter, so the gear stays untaken and unhashed as left-out text.

**Prerequisite commit (hand-written):** in four-degrees `system.yaml`, with Russian and Hebrew labels for each new field.
- `armor` is body armor and shields, one type: the sheet's inventory lists `armor`, and `SheetRules` tells a shield by `armorType: shield` (a flat `armorClassBonus` on any equipped item) and body armor by a numeric `armorBase`; a new `shield` type would have needed the inventory changed. New fields: `armorType` (a dropdown with the one value `shield`), `armorClassBonus`, `hardness`, `hp`, `brokenThreshold`, `checkPenalty`, `speedPenalty`, `strength`, `bulk`, `group`, `traits`, `price` and `level`. `armorCategory` gains `unarmored`, since the ranked armor list already had that key and the book's table of unarmored defense prints one item in it (clothing). `armorBase` is labelled "Base armor class" (en, ru, he; it was "Armor bonus", which a reader would take for the book's printed bonus) and says what it is: **10 plus the printed armor bonus**. That is the number the homebrew armor holds (the quilted vest, a bonus of 1, has 11), the fixture's `scale-coat`, and the sheet rule of `docs/CONTENT_PACKS.md` (Armor class: `armorBase` plus Dexterity up to the cap, then the rank in the category).
- `weapon` gains `damage` (the dice), `damageType`, `range` (feet), `reload`, `hands`, `bulk`, `group`, `price`, `level` and `ammunition`, and `weaponCategory` gains `advanced` (in the dropdown and in the ranked `weapons` list, beside `unarmed`, `simple` and `martial`, with its label `Advanced weapons` in the Russian and Hebrew value labels). Unarmed attacks need no change: `unarmed` was already a category and a ranked key, and the fist is an entry of it.
- Nothing else changed, and the smoke tests of the system and of the localization pass. `price` and `bulk` are the text the book prints (`2 sp`, `L`): the book's coins have their own rule, and a number of silver would have been a guess at its rates. A dash is no value, so the field is left off.

**Built (the run):** `packs/rules-core/` now holds 1,048 entries (911 before, byte-identical: `--check` passes twice, and `git diff` of the pack shows additions only). New: 16 `armor` entries (12 suits of armor and 4 shields), 83 `weapon` entries and 38 `rule` entries of the category Equipment. 9 take-list rows (26 in all, 101 ranges).
- **The table reader (`StatTables`, run by `BlocksStage` before the gutter search, only on the pages an `items` row names in `tables`).** The generic detector needs a header row in the block-header font and cuts cells at one gap, and these tables have a body-font header under a caption, columns that are centred or left aligned, and gaps between a name and a price that are smaller than the gap between two words of a trait line. The reader takes a header row to be a line whose words from `Price` on are exactly one of the committed column label lists (`tableColumns`; a label that is not the title of a bookmark of the chapter fails the run, `CheckTableLabels`), and the columns are the spans of the header's words. A data cell goes to the column whose label it overlaps most (a cell under no label, or under two equally, fails the run). Every row starts with a name and must have a cell in every column (the book prints a dash for none), or the run fails with the counts. A line with no name continues the row above it only in the last column (a trait list that wraps; a cell under any other label is a shifted value and fails the run, as does a footnote mark with no cell to its left and a rarity heading that no header follows), a lone line in parentheses continues the name (an item level), a name that starts with a digit is an ammunition line (a price and a Bulk only, joined to the weapon above), and a lone word is a rarity heading. A header again inside a table (a category band) reads the columns again. A footnote mark is a word set smaller than the text, joined to the cell on its left, and the note under the table is a row of its own. The tables are `stat-table` blocks, a role no parser but the items parser claims (the rules parser reads past one), and a page's text is cut into columns after they are carved out: page 274, a one-column page by the old reading, with a table across both columns over text in two, is two columns now (6 one-column pages are left, from 7). The 8 tables of the chapter: unarmored defense and armor (2 on one page, 4 category bands with the unarmored one), shields (1), unarmed attacks, simple melee weapons, martial melee weapons, advanced melee weapons and ranged weapons (5).
- **The items parser.** A description is a paragraph that opens with a bold name and a colon; its statistics are the row of a stat table with the same name (item level, footnote marks and star left off). A table row with no description, a description with no row, or two of either with one name fails the run, unless the row's `countsDiffer` says why and `exclude`, `undescribed` or `ruleDescriptions` names it. Three rows are made so: the row of wearing no armor (no item, no description), the alchemical bomb (a weapon of the gear chapter whose statistics are there, and the table row and the description are left to 6b), and the fist (a row of the unarmed attacks table with no paragraph of its own: its entry is its row, a trait line, and the unarmed attacks rule has the rest). The four kinds of ammunition have a description and no row (their prices sit under the weapons that fire them): they are one `rule`, `Kinds of Ammunition`, a labelled paragraph each, and the weapon's own `ammunition` field holds its quantity, price and Bulk as the table prints them. 11 weapons have one.
- **What an entry holds.** The statistics are properties from the table, written as the YAML the type's field takes (a bare number, a quoted string, a flow map: `FinalEntry.Raw`), and the text is the description, then a labelled `Traits` line (the traits as printed, with a damage letter written out), then the table's footnotes as `Note` lines (the tower shield's second bonus, and the one note that all shields share, whose page pointer is redacted). The entry's `traits` are the keys of the printed traits, their words up to the first parameter (`thrown 10 ft.` is `thrown`, `attached to shield` is `attached`), plus `uncommon` for a row under the rarity heading (23 weapons). Armor and shields carry no tag; every weapon carries `tags: [weapons]`, which the Strike action reads, and a `use` map of attack, abilities and damage as the homebrew weapons write it.
- **How the sheet reads them (and what it cannot).** Body armor: `armorBase` 10 plus the bonus, `armorDexCap` the printed cap. **The cap is a maximum of the bonus**, so a cap of 0 must still let a negative modifier through, which the engine's armor class rule did not do (it added no Dexterity either way). The one change to `src/` is the armor class rule's option `dexCapLimitsOnlyBonus` (default false, so the other systems read as before; four-degrees sets it true): with it a cap of 0 or less is `min(Dexterity, 0)`. `ArmorDexCapTests` pins both settings, and a full plate on a wearer with Dexterity 8 gives 15, not 16, in `FourDegreesEquipmentTests`, `armorCategory` from the band. The wearer's rank is the category's, and an unarmored suit (the clothing) uses the unarmored rank. A shield: `armorType: shield` and `armorClassBonus` the printed bonus, with the hardness, hit points and broken threshold; the tower shield's printed pair of bonuses is its first (raised) and the other is in the note. **The sheet adds a shield's bonus whenever it is equipped**, since it has no way to model raising one, which the book requires. A weapon: `use.abilities` is `[str]` for a melee weapon, `[str, dex]` for one with the finesse trait (the best counts) and `[dex]` for a ranged one, as the book's attack rules say (Weapons > Attack Rolls: a melee attack roll uses Strength, or Dexterity for a finesse weapon; a ranged one uses Dexterity); a thrown melee weapon is read as the melee one it is listed as, which the sheet cannot make a ranged attack of. `use.damage` is the damage dice (the sheet adds no ability to a damage roll, as before), and the blowgun's damage is the plain number 1. **The advanced weapons are untrained for every class** the pack's homebrew has, so a Strike with one adds the ability alone. The `agile` trait is not the homebrew's `featherlight`, so the sheet's multiple attack penalty variant does not read it: imported agile weapons use the plain penalties until the system names `agile` in `multipleAttack`. The 11 tests of `FourDegreesEquipmentTests` equip one suit of armor of each category, a shield with armor and without, a finesse weapon, a ranged weapon and the fist on a level 3 warden, and every one of the 83 weapons and 16 armors, and check the armor class, the Strike bonus and the damage against the numbers of the pack file, read in the test (and pinned from the yaml: 14 + 1 + 5 = 20 for the breastplate, 4 + 5 for a finesse weapon with a Dexterity of 18).
- **Rules.** The sections of the chapter are `rule` entries of the category Equipment: its own text, each section's own text before its children and each child (coins and currency, price, item level, carrying, bulk with its six children, item damage, shoddy items, items and sizes), the armor rules (armor class, donning and removing, specialization effects), the shields' own text and attacking with one, and the weapon rules (attack rolls and the multiple attack penalty, damage rolls, critical hits, unarmed attacks, improvised weapons, critical specialization). **The statistics sections** (`Armor Statistics` with 9 children, `Shield Statistics` with 3, `Weapon Statistics` with 8) are one rule each, the columns being paragraphs of it (`foldChildren`): three of the columns' titles are repeated in the other statistics sections (Bulk three times, Group twice, AC Bonus twice), so each as an entry of its own would clash on its id, and they are one explanation of one table. The count is by title and in order: bookmark children against the headings in the entry-header font in the range (9 = 9, 3 = 3, 8 = 8; `CheckFoldCounts`). **The two sections of traits.** The glossary of chapter 2 ships none of the weapon or armor traits (it ships 125 entries, among which are the six ancestries' that weapons carry, and no trait of a weapon), so `Weapon Traits` (the section after the tables) is a rule, with its definitions as paragraphs, and the definitions of the armor traits are the end of `Armor Statistics`; no trait entry was made from them, which would be a later step. Every weapon and armor trait key of the pack, except the rarity, has its definition in one of the two rules (pinned in `DegreesRulesPackTests`). Armor and critical specialization effects are one rule each, a paragraph for each group.
- **Item descriptions are kept whole.** A description is the book's own paragraph, so the lines about who makes or uses an item (some are tied to the ancestries chapter 2 ships as traits) stay with it: they name nothing reserved, and the flavor check of section 6 is for the openers and lore pages, not for an item's description. A formula in a box (the attack and damage roll formulas) is one line each, joined across its hanging indent (`proseBoxes`).
- **Boxes and art.** The two boxes of the item rules (the encumbered condition, the broken condition) and the boxes of the weapon rules on damage dice are paragraphs of their section (`subheadings`, and `proseBoxes` where a body box opens with a bold heading so it is read as prose and not as a table). A body line set in capitals alone is the label under a picture of an item: skipped, and counted (`artLabels`; 8 lines among the armor descriptions, 5 among the weapon descriptions, and the page of weapon pictures).

**Counts, from the document, independent of the parsers' text handling:**
- **Table rows against description headings.** Armor: 13 table rows (2 tables) against 12 descriptions, less the row for wearing none; shields 4 against 4; weapons 84 table rows (5 tables) against 87 descriptions, less the alchemical bomb on both sides, and the fist's row has no paragraph, and the four kinds of ammunition have no row. 13 - 1 = 12 = 12; 4 = 4; (84 - 1) = (87 - 1) - 4 + 1 = 83. The entries are named from the descriptions and joined by name, so the two counts are two readings of the page, by position and by bold name. The bookmark children of the sections have no entry per item, so the third count of a section 6 is the statistics columns: every label of the tables is the title of a bookmark of the chapter (`CheckTableLabels`; the book's `Dex Cap` is the short name in a bookmark title's parentheses), and the nine children of `Armor Statistics` are the category and the eight other columns.
- **Pinned in `DegreesRulesPackTests`:** 16 armor entries (12 body armor: unarmored 1, light 4, medium 4, heavy 3; groups chain 2, cloth 2, composite 2, leather 3, plate 3; every suit's base and cap; 4 shields with their four numbers), 83 weapons (advanced 4, martial 58, simple 20, unarmed 1; melee 68, ranged 15; 15 groups with their sizes; hands 1: 57, 1+: 4, 2: 22; reload 0: 5, 1: 6, 2: 1 and none 3 among the ranged; damage dice 1: 1, 1d4: 19, 1d6: 31, 1d8: 18, 1d10: 11, 1d12: 3; bludgeoning 24, piercing 34, slashing 25; 11 with ammunition; 19 with finesse; 23 uncommon), the five items that print a level (full plate 2; half plate, splint mail and the two composite bows 1), the 38 trait keys of the rows (six shipped as ancestry traits, one rarity, and the rest defined in the two rules), the 38 rules (among them the three statistics rules and the ammunition rule), the entry total (1,048) and that no id clashes with the homebrew pack in either direction (the existing test, over every type). 29 tests of `DegreesEquipmentTests` drive the table reader, the items join and the homebrew id check with invented words (a band, a wrapped cell, a wrapped name, an ammunition line, a footnote and its note, a row with a missing cell, a cell under no label, a row with no description and a description with no row, the exclusions, and a label that is no bookmark).

**Stable ids:** two entries took a suffix: the armor named Hide (`hide-armor`, since `hide` is an action's id that shipped) and the weapon named Staff (`staff-weapon`, since `staff` is a trait that shipped); the rule: an id that has shipped keeps its entry, and the other of the pair takes the suffix of its type, which `PackEmitter.ShippedIds` reads. **One source, always:** the pack folder a run names, else the committed rules pack (`PackEmitter.CommittedPackDir`, found by walking up from the program and the working folder), so `run`, `--check` and `denylist` give the same ids with or without `--out` and `--pack`. With no pack to read at all, the entry of the earlier take-list row keeps the slug (shipping order; within a row a rule or any other type keeps it against an action, and an action against a spell), which is what shipping gave. Tests drive both, and read the real pack for `hide` and `staff`. The 911 earlier ids are untouched; no armor or weapon id equals a homebrew one (the invented names are all unlike the book's).

**Reserved material.** The names of the book's items are ordinary words or the names of real weapons and armors (breastplate, katana, kama, nunchaku, longsword), and six are named for an ancestry that chapter 2 ships as a trait (an elf, a dwarf, a gnome, a goblin, a halfling, an orc), which is allowed. Nothing in a name needed `entryHash`. The review found three sentences that named something of the setting (none of them an item name):
- **Replaced:** an example in a bow's description that named a deity of the setting, a sentence of a saber's description that named a group of assassins as its users, and a sentence of the unarmed attacks rule that named a heritage the setting coined (3 rows, the mechanic kept in plain words). Three more are corrections of the extraction or the book: a word broken in two at a line end, a bracket the book leaves out, and a misprinted word (`reason` says so).
- **Pointers:** 23 rows (18 `replace`, 5 `cut`) remove a pointer to a page, to another book or to a sidebar (the page pointers inside brackets that the cleaner already took are not among them). A pointer by title that the pack has as a rule stays by name (Armor Specialization Effects, Item Damage, Weapon Traits, Carrying Items, Armor Statistics). In all 29 new rows: 134 in all (100 replace, 29 cut, 5 drop-entry), 0 unmatched.
- **Sweeps.** The lowercase words of the new text that the book writes fewer than three times (287, read as a list and by context): all ordinary words (the parts of armor and weapons, verbs of using them, kinds of material), except one, the coined heritage above, which a row replaces; the pack says none now. The capitalized sweep is the allowlist gate (161 words flagged at first): the names of the items and the words of the chapter's section titles, ordinary words that open a sentence after a label, and single letters (the table's Bulk of L, the damage letters B, P and S, and the shapes some descriptions give, a Y and an H); each was read in its sentence. The pair sweep is the pair list (16 pairs flagged over the two regenerations, all of them game terms: statistics that meet in a line, creature sizes down a column of a table and a box's heading with the sentence under it).
- **Allowlist:** 162 new words (1,314 in all), each a game term or an ordinary word the gate needs. **Waivers:** 29 new (281 in all: 159 words, 122 pairs): the words `breastplate` (a type of armor that an index capitalizes), `reactive` (a reaction named as an example), `gms`, `bt` (the abbreviation of a shield's broken threshold), `shades` and eight ordinary words of the item texts (`linked`, `cloth`, `armors`, `unit`, `rounding`, `grasping`, `sites`, `strand`), and 16 pairs (statistics that meet in a line, creature sizes down a column, and the like), each read in its sentence.
- **Gates:** `degrees denylist` has two new switches. `--fresh` writes the hash files from this run alone and not merged with what they hold (the earlier chapters regenerated by deleting the files, and the hash files are not to be deleted by a script), **and keeps section 16's rules**: it never drops the hash of a word a redaction row removes or of a dropped entry's name (the run fails); it refuses a take-list that has rows of another book, whose hashes it would drop; it never drops a pair that no waiver, take-list title or denied word explains (the run fails, as a regeneration must not silently remove a pair because a range has since been taken); and a redaction row whose entry is not found fails the denylist step (`RedactionStage.RowsWithoutEntry`) instead of leaving its words off. It prints, per file, what it writes, keeps and adds and what it removes, by reason (waived, left-out text since taken or common, pairs of a title or a denied word); `ImportPipeline.ReviewFresh` has a test for each rule. `--pack` names the pack folder whose ids have shipped (the committed pack is read otherwise). All three files were regenerated with it, and compared with the files before: the denylist is 846 hashes (854: 12 gone, which are the waived words that were on it, and 4 new, which are the words of the sentences the new rows remove: the heritage, the group of assassins and the two misprinted words), the pair list 932 (929: 14 gone, 17 new) and the common words 4,947 (4,923: 1 gone, 25 new). A table block's lines hold the cells and not the word for the row's kind, so the common words are not read from the reader's own bookkeeping (the six kind words are on neither file, or were already). **The homebrew check is part of `--out`:** the run reads the ids of the sibling `homebrew-core` folder (the three forms its files write: `- id: x`, `- id: "x"` and `- { id: x, ... }`; `PackEmitter.HomebrewIds`) and fails, naming the ids, if an imported id equals one in any type (the run prints that none of the 1,048 equals one of the 55); `DegreesRulesPackTests` still checks it from the loaded packs.

**Shared fixes, and what they did to the earlier chapters (nothing: the first 911 entries are byte-identical):**
- `ImportPipeline.CheckRuleSectionCounts` reads the font of a section's children from the first child's own heading (the equipment chapter sets a section's children in the section font under a chapter and in the entry-header font under a section), and skips a row that gives a `countsDiffer` reason (the chapter's own text, whose headings share a font with their children's).
- An entry's `source` line is normalized like the text (curly apostrophes made straight), so a title with one (`Estimating an Item's Bulk`) passes the source pattern; the pattern allows an apostrophe.
- `PackEmitter` writes `tags:` and a type's table properties (`Raw`), after the extracted ones.

**Not done:** the gear (6b), whose statistics the weapons table points to for the alchemical bomb. A trait entry (`trait` type) for each weapon and armor trait: the definitions are paragraphs of two rules. The classes' proficiencies in the new `advanced` category (the classes chapter). The shield's raising, which the sheet cannot model, and the cases it has no shape for: the tower shield's second bonus, a thrown melee weapon's ranged attack, the `agile` penalty variant, the reload and range of a thrown melee weapon. The price in silver (a text, as printed). The armor's strength: the field holds the modifier the book prints, and Four Degrees declares no proficiency rule (`SheetRules.Proficiencies`, whose `StrengthAttribute` has no default), so the sheet checks no Strength minimum; a later rule would read it. The item sizes (a weapon sized for a creature of another size). The weapons' `damage` bonus of Strength: the sheet has no `damageBonus` for the Strike. The rules of materials and damaging armor (a sidebar, not taken), and the quick equipment packages (a sidebar). Russian and Hebrew labels of the new fields are plain game terms, to be reviewed by a native speaker.
