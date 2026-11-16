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

**Built (step 3, Ch1b):**
- `packs/rules-core/` holds 42 conditions and 2 rules (44 entries; 46 bookmark children minus 4 sidebars, and 42 entry headers, agree on the 42). Its manifest is
  `four-degrees-rules`, with the attribution and ORC Notice copied from the system and a Reserved Material notice that designates none of its own.
- 11 redaction rows, all `replace`: 5 page pointers (the report's `pageReferencesLeft` is 0), and 6 sentences reworded only so the word gate stops flagging an ordinary
  capital. The gate takes a stop after a contraction's last letter (`wouldn't. If`) for an abbreviation and so reads two sentences as one, and it flags a sentence-opening
  word whose lowercase form the pack never uses; none of these was a game-term problem, so the allowlist took only game terms (condition names, attributes, saves, actions).
- The CI gate test pins `rules-core` in `ExpectedGeneratedPacks`, and now lists every flagged word (entry number, field path) rather than the first five.
- Not done: `degrees-denylist.sha256` is still empty; it is regenerated once every chapter's rows are on the take-list. A few `armor .` style spaces before a stop remain in
  one entry (a parser gap, to be fixed in the entries stage with its own test).
