# Book export (Homebrewery-style PDF)

Status: **design proposal, nothing built.** Written for review before any code.

Goal: from a campaign's data, produce a print-quality, Homebrewery-looking PDF "book" (DM edition or player
edition), optionally refined by the DM's own AI model, built in a guided session that shows live progress, live
preview, what it costs in tokens, and asks the DM for illustrations where it wants them.

## 1. Principles

1. **Works with no AI.** The deterministic book (stat blocks, gazetteer, NPC roster, session recaps) is a complete
   feature. AI only improves layout and prose.
2. **The model never owns facts.** Names, numbers, ids and stat blocks come from data through templates. The model
   handles prose, order and emphasis, with facts passed as opaque tokens that a validator checks on the way back.
3. **Filter before the model, not after.** Secret blocks (`SecretText`: `:::dm`, `:::party`, `:::players`, `:::for`) are
   resolved for the chosen audience *before* any text is sent to the model, as `AiAssistant` already requires of callers.
4. **The model proposes, the DM applies.** Same stance as the World builder: nothing in the campaign is modified by an
   export. The export reads a snapshot and writes a file.
5. **Honest cost.** Estimate before, meter during, cap always. Tokens are the truth; money only if the DM tells us a price.
6. **No new network dependency at run time.** Everything the renderer needs (CSS, fonts, masks) is bundled.

## 2. Pipeline

```
Repository reads as a synthetic reader (see 2.3)  -- NOT CampaignBundle
  -> BookPlan   (outline: ordered typed sections, each with its source ids)
  -> Sections   (deterministic Markdown + optional AI-refined Markdown, per section)
  -> Slots      (image slots resolved: existing art -> upload -> AI image -> masked placeholder)
  -> HTML + CSS (our renderer, Homebrewery-flavoured Markdown dialect, escaping by default)
  -> preview and PDF paginated by ONE engine (see 3)
```

### 2.1 Why not one long chat

`WorldChat` resends history on each round (it stubs old tool results to limit growth). A 40-page book in one
conversation makes cost unpredictable and failure expensive. Instead:

- **Planner**: a short conversation producing the outline; the DM steers it.
- **Section workers**: one call per section, fresh context = that section's source material + a style sheet. Bounded
  input, independent retries, parallelisable (subject to provider limits).
- **Content hash per section** (inputs + style + model + prompt version): an unchanged section is not regenerated.

### 2.2 Facts never pass through prose (revised after review)

Prose tokens such as `⟦ref:id⟧` break in Russian (case endings), Hebrew (attached prefixes, bidi-mirrored brackets) and
number agreement, and they forbid the condensing the model is there to do. So:

- Stat blocks, tables and every number are **template blocks**. The model may only *place* them, as `{{block:3}}` references
  to a closed list for that section. It never writes their content.
- Names go to the model in clear text so it can inflect them.
- After generation, deterministic checks: every number in the output occurs in that section's input; every name-like span
  fuzzy-matches a name the audience may see; ids resolve only through the section's own map, never a global lookup (Raven ids
  are guessable); no `:::` marker lines; no URLs, no raw HTML. A failing section is retried once with the error shown, then
  falls back to the deterministic Markdown and says so in the UI.

### 2.3 Audience (revised after review: this was the top blocker)

The DM bundle is raw by design (`CampaignExportService.cs:73-84`: `GetAll...`, unredacted events, `DmNotes`). Redaction lives
in about ten repository and helper paths (`RavenActorRepository.Redact/Mask`, `SessionEventRedactor`, the region, fact and
note repositories, `MentionDirectory`). A second filter in book code would drift the first time `ActorDocument` gains a field.

- The **player edition is built only from repository reads** with a synthetic `CampaignAccess(campaignId, "book-reader:<guid>",
  Player)`: a user id that belongs to no account and owns no character, so the "own content comes whole" carve-outs
  (`RavenActorRepository.cs:53`) never apply. The book builder's input type cannot hold a `CampaignBundle`.
- v1 editions are **DM** and **Party** (no owned characters). Per-player `:::for` editions are out of scope.
- Existing art is read through the same masked reads, not raw attachments by id, so Unknown and Hidden actors' faces cannot leak.
- A canary test seeds a unique string in every DM-only field and asserts it appears in none of: captured model requests (fake
  `IChatClient`), the stored build, the HTML, the `.md`, the text extracted from the PDF, the PDF title and metadata.

### 2.4 Cost control (revised)

Usage is often not reported (streaming on many compatible servers), parallel calls overshoot a cap, and retries stack
invisibly (transport retries, temperature fallback, `response_format` fallback, parse retry, plus our validator retry;
`WorldChat` counts only the final response). So:

- **Reserve before each call** (estimated input + `MaxOutputTokens`); fall back to a character estimate when no usage is
  reported, and label it. chars/4 under-counts Cyrillic and Hebrew by 2-3x, so use a per-script factor.
- **Reserve server-allowance requests up front** and refuse the build if they are not there, instead of failing mid-book on
  `ServerAllowanceSpent`.
- Default **concurrency 1 for local endpoints** (Ollama queues into the 5-minute timeout), 2 for remote, back off on 429.
  Batch small sections into one call. Keep a stable prompt prefix so provider prompt caching applies.
- The **cache key** includes: audience, book language, each source document's change vector, template and dialect version,
  pack and system versions, endpoint, model, temperature, prompt version, style options, slot assignments. DM and player caches
  are stored apart, and filtered inputs are recomputed at print time, not taken from the build-start snapshot (an actor hidden
  mid-build must not appear, and cached prose naming someone later made Unknown must not survive).

## 3. Rendering (revised after review)

- **Dialect**: a Homebrewery-compatible subset (see 6 and 8 for the engine choice). The `.md` is *compatible markup*, we do not
  claim pixel parity, and we do not use the Homebrewery name or the WotC-imitating PHB look. Ship our own theme.
- **Closed network**: images are referenced only by slot or attachment id; no URL survives rendering. The renderer escapes by
  default (reuse the `WikiText` approach, there is no sanitiser package in the repo). The preview iframe is `sandbox`ed with
  no scripts and a CSP of `default-src 'none'; img-src data: 'self'`. The print browser uses `SetContent`, JavaScript off, and
  routes everything except in-memory assets to abort. Reason: player-written text reaches section workers, so a markdown
  image carrying a DM secret in its URL is a real exfiltration path, and server-side Chromium is otherwise an SSRF and
  `file://` primitive.
- **Pagination**: Homebrewery pages are fixed boxes with hand-placed `\page`, which clip overflow; generated content needs
  automatic pagination. Chromium lacks `target-counter()`, `string-set` and footnotes, so a TOC with page numbers needs a
  measure-then-render pass or paged.js (MIT). Paginate in **one engine** and preview what that engine produced; a Firefox or
  Safari iframe will paginate differently from the Chromium PDF. Pass `Outline` and `Tagged` for the PDF.
- **PDF engine: PuppeteerSharp (decided).** All Chromium paths share `Page.printToPDF`, so fidelity is equal; PuppeteerSharp wins on friction (MIT per its repo, no Node driver, no download unless code calls the fetcher, `ExecutablePath` to an installed Chrome/Edge). We write browser discovery ourselves (Edge/Chrome standard paths per OS, PATH on Linux), check the browser version at launch (page-number margin boxes need Chrome 131+), and with no browser found offer the print-stylesheet page or a consent-gated, size-labelled Chromium download. CI passes the Chromium Playwright already installs for e2e as `ExecutablePath`. Rejected: Playwright (per-platform Node driver in the NuGet needs pruning in `package.sh`, same output), WebView2 (Windows only; macOS `WKWebView` is not paged), WeasyPrint/Prince/QuestPDF (Python+GTK, commercial, code layout). Raw `--print-to-pdf` is an emergency fallback only. Original reasoning kept below for the process notes: Playwright's driver bundles a Node runtime per platform (tens of MB each, so
  `package.sh` would need pruning as it does for RavenDB), installed Chrome/Edge auto-update past the pinned driver, and
  Linux needs system libraries and `--no-sandbox` as root. Candidates: PuppeteerSharp (pure .NET over DevTools to an
  installed browser), WebView2 `PrintToPdfAsync` on Windows, Playwright anyway. Whichever: one lazily started browser, closed
  when idle; a global render queue of one with a hard timeout that kills the process; per-user rate limit, admin-only on an
  exposed server; budget 0.5-1.5 GB peak next to embedded RavenDB. Any Chromium download stays an explicit consent step.
- **Fonts**: the bundled OFL fonts are thinner than first claimed (`wwwroot/fonts`): Cinzel is Latin only, Forum Cyrillic 400
  only, Frank Ruhl Libre and Assistant Hebrew only, and there is no bold-italic. The book declares explicit `@font-face` with
  `unicode-range` and a full fallback stack (Assistant to Alegreya Sans, and so on), mirrors `@page :left/:right` for RTL, and
  a test asserts which fonts are embedded in the PDF, so a missing glyph never silently falls to a system font.
- **Masks/ornaments**: our own SVG masks applied with CSS `mask-image` (torn edge, ink wash, vignette). See 6.

## 4. Illustration slots

A **slot** = `{ id, purpose, aspect, mask, subject ref, state }`. The AI declares slots while writing; it does not draw.
Resolution order, first hit wins, automatically (all reads go through the audience-masked paths of 2.3):

1. Art that already exists for the subject: actor portrait, place picture, wiki art, pack art, campaign cover.
2. A DM upload into the slot.
3. AI image generation, when an image provider is set (separate per-user image provider list already exists).
4. A placeholder: mask-shaped, labelled with the purpose.

Requests are **non-blocking**: the AI keeps writing; unresolved slots collect in a tray ("I'd like art for: Geography
divider, Captain Vale, the Drowned Chapel"). Filling one re-renders only the pages it touches. Uploaded and generated images are re-encoded and downscaled (about 300 DPI at
printed size), which also strips EXIF/GPS, and are credited as such on an art-credits page. Alt text and filenames never
reach the player edition. Generation of images is
always an explicit click, never automatic, because it costs money and takes time.

## 5. Cost transparency

- **Pre-flight**: estimated input/output tokens as a **range**, from section count x source size x style prompt size, for the
  selected model. Labelled as an estimate (reasoning tokens are unpredictable).
- **Meter**: running totals from the same per-turn usage `WorldChatUsage` records (model, in/out tokens, rounds); where the
  provider reports no usage, say "not reported" rather than guess.
- **Cap**: hard token cap per build; at the cap, stop and keep what exists. Each section shows its own re-run cost.
- **Price**: optional `InputPricePerMTok` / `OutputPricePerMTok` (decimal, currency-agnostic label) stored on
  `AiProviderConfig` next to model id and key, entered in the same settings form. With a price, show money; without one,
  tokens only. Server-wide fallback config (`AI` section) gets the same two optional keys. The app never ships a
  price table (prices change and providers are arbitrary OpenAI-compatible endpoints).
- **Server allowance**: the server key already has a daily *request* allowance (`AI:DailyLimitPerUser`, default 200,
  `ServerAiUsage`). A section-per-call build may spend dozens. Pre-flight shows "uses ~N of your M requests today".

## 6. Licensing (to confirm)

TTRPG.Codex is PolyForm Noncommercial 1.0.0. Anything vendored needs a licence compatible with redistribution inside that
and an entry in LEGAL.md. Findings from reading naturalcrit/homebrewery (a research pass, partly unverified):

- **Code is MIT** (`license`, "Copyright (c) 2016 Scott Tolksdorf"; `package.json` says MIT). Vendorable with notice.
- **The V3 Markdown dialect is not in that repo.** It is the separate MIT npm package `marked-hbfm`
  (github.com/naturalcrit/marked-hbfm). It defines `{{class,k:v ...}}` spans/divs, `{width:..}` attribute injection on
  images, hard breaks. `\page` and `\column` are string-split in `brewRenderer.jsx`, not Markdown tokens.
- **Assets and fonts have no stated licence.** No per-directory LICENSE/README/NOTICE exists. The 5e theme ships Bookinsanity,
  Scaly Sans and Mr Eaves "Remakes" (WotC-lookalikes, probably derived from Solbera's fonts, which are CC-BY-SA-4.0 by
  upstream, unconfirmed for these files), plus PHB/DMG-named backgrounds and frames (`PHB_footerAccent`, `DMG_background`,
  `partCoverHeader*`, `monsterBorder*`, parchment) with no provenance. The README only says the MIT covers the project and
  that sellers need rights to images they use. The watercolor masks (`watercolor1..12.png`, `waterColorMasks/edge/*.webp`)
  have no provenance either.
- **Decision: vendor no Homebrewery image, mask or font.** Bundled OFL fonts only (see §3). Ornaments, frames, parchment and
  masks are our own SVG/CSS, drawn for this project, so there is nothing to attribute or to be wrong about. WotC-styled
  assets are specifically avoided.
- **Optional: vendor `marked-hbfm`** (MIT) as the Markdown engine in the preview/print page. That would make the `.md`
  download genuinely Homebrewery-compatible by construction, at the price of a JS dependency in the page (bundled, no network).
  Alternative is our own small .NET renderer for the subset in §3. Needs a decision; see §8.
- **Homebrewery's HTTP API** (`POST /api`) is undocumented, needs a login, and rejects writes unless a `Homebrewery-Version`
  header equals its exact `package.json` version (HTTP 412). Do not build on it. Download or clipboard only.
- The name "Homebrewery" is NaturalCrit's. Say "compatible with Homebrewery markup", never imply affiliation.

The attribution page is built from the `license.attribution` of every pack whose entries the book uses, not a hard-coded SRD
page: the ru/he SRD packs are translations (say so), flavor packs are MIT, user-imported packs carry their own terms.
CC-BY-4.0 for the SRD grants no trade dress, which is one more reason for our own theme.

## 7. Phasing (revised: phase 1 was not small)

0. **Risk spike, no new dependency**: a print-stylesheet route plus the browser's own Save as PDF. Render one gazetteer, one
   stat block and one Hebrew page; measure overflow and font fallback. Add server-side PDF only once this looks right.
1. Deterministic DM and Party books -> paginated HTML -> PDF, audience built per 2.3, closed network per 3, canary test.
   Slots filled from existing art, placeholders otherwise. No AI, no `.md` parity promise yet.
2. Section workers with template blocks and deterministic checks (2.2), reservation-based budget and cache key (2.4),
   token counts and the optional price fields, live preview.
3. Planner conversation, the slot tray, AI image generation per slot, `.md` export (warn that a DM-edition `.md` pasted into a
   third-party site publishes secrets; offer the Party edition `.md` from the same filtered source).

Cut from early phases: money prices beyond the stored fields, per-player editions, AI images, the planner chat.

## 7a. Build state and staleness

A build is a DM-private document like a world chat: deleted with the campaign, **left out of the campaign export and
backups** (it holds DM secrets in prompts and outputs), PDFs are never stored as Raven attachments, prompts are never logged.
A section is marked stale when its sources' change vectors move (same idea as the World builder's stamp-on-review); a source
deleted or hidden mid-build fails closed (the section is dropped). Builds are per-DM, so two DMs do not collide.

## 7b. Testing

Pure unit tests for the dialect, validator, numeric/name checks and cache key, with Russian and Hebrew adversarial cases.
Scripted `IChatClient` tests as in `WorldBuilderTests` (retries, cap and allowance exhaustion, a 429 storm, an injected
`![](url)`). The canary test of 2.3 against `RavenDbFixture`. PDF tests on the existing CI Chromium (`ci.yml`): page count,
extracted text, embedded fonts, an empty external-request log. No cross-OS pixel snapshots.

## 8. Open questions

- DECIDED: our own .NET renderer for the Markdown subset (no JS in the print page, no marked-hbfm version coupling, one parser
  for preview and PDF). The headless browser is still needed for pagination and PDF; this only makes its input static HTML.
- DECIDED: PuppeteerSharp (see 3).
- UNVERIFIED, test in phase 0 on the real Chromium: does CSS `mask-image` stay vector in the PDF (else use SVG clip-path or
  inline SVG); is `target-counter()` unsupported (then two-pass TOC or paged.js); PDF size and memory ceiling for large books;
  Tagged/Outline behaviour.
- Prices: the Anthropic client exists (`AIClientFactory`), so "OpenAI-compatible" is not the whole story for usage reporting.
- Installer impact and signing if we ever bundle Chromium instead of using an installed browser.
- Headless launch under root/containers (`--no-sandbox`), Windows/macOS channel discovery.
- Parallelism of section calls vs provider rate limits and the server allowance.
- Where a build's state lives (a `BookBuildDocument` like `WorldChatDocument`?) and whether it is part of the campaign
  export (the world chats are not).

## 9. Phase 0 results (measured, Chromium 153.0.8010.12, Linux)

Harness: `tools/book-spike/` (synthetic book with the app's real fonts, closed network, JS off). One browser, one machine:
re-verify on Windows and macOS Edge/Chrome before relying on any of it.

| Question | Result | Consequence |
|---|---|---|
| Closed network works? | Yes. JS disabled, 8 fonts served from a route, every other request aborted, none attempted. | Keep the rule from section 3. |
| `mask-image` stays vector? | **Yes.** CSS `mask-image` (SVG mask) and SVG `clip-path` both render; no raster image is added to the PDF. | Masks are viable as CSS. |
| Masked illustration? | A JPEG under a mask is embedded once at source resolution (shared across uses, mask as soft mask), not re-rasterised. | Downscale uploads ourselves (section 4); no quality loss from masking. |
| `@page` margin boxes with `counter(page)` / `counter(pages)`? | **Yes**, including separate `:left` / `:right` boxes and `:first`. | Page numbers and running footers need no header/footer template. |
| `target-counter()`? | **No.** The declaration is dropped; the TOC lines get no page numbers. | Two-pass TOC: render, read each heading's page from the PDF outline (`outline: true` gives a page per heading, verified), render again with the numbers. Leader width fixed so pass two paginates like pass one. |
| RTL page progression | Follows `html dir`: in RTL the first page is `:left`, in LTR `:right`. | Hebrew books need their own mirrored `@page` rules and `dir` on the root. |
| Tagged PDF | `tagged: true` yields a tagged PDF. | Cheap accessibility win; keep on. |
| Font embedding | All embedded and subset. Static fonts (Alegreya Sans) become CID TrueType; **variable fonts (Cinzel, Assistant, Frank Ruhl Libre, Forum) become Type 3** (vector, searchable, but unhinted). | Acceptable for print. If it matters later, build static instances (OFL allows it, mind reserved font names). |
| Missing glyphs | **System fonts leak in** (Liberation Serif, WenQuanYi, DejaVu appeared for CJK, Arabic, dingbats, symbols, and for Hebrew when the stack lacked a Hebrew face). | Campaign text will contain emoji and symbols. Required: full `font-family` stacks, a test that every embedded font is in an allow-list, and a preflight that lists glyphs we cannot cover so the DM sees them before printing. Not reproducible across OSes otherwise. |
| Scale | 45 pages: 0.9 s, 1.3 MB, ~510 MB peak Chromium RSS. 114 pages: 5.0 s, 3.2 MB, ~830 MB. Text only, no large images. | Time grows faster than page count. Budget about 1 GB; keep the single render queue and timeout from section 3. Images will add to both: re-measure with a real art-heavy book. |

Not measured: Windows/macOS Chrome and Edge; fonts under a different Chromium; art-heavy books; nested multicol with page
breaks (reported historically fragile); copy/search behaviour of extracted Hebrew text.

Not yet built: the in-app print-stylesheet route over real campaign data (it needs the audience-masked reads of 2.3 first).

## 10. Audience-masked reads: built (2.3)

`src/Codex.Persistence/Book/` (`BookSource`, `BookSourceReader`) and `tests/Codex.Tests/BookSourceTests.cs`.

- `BookSource` has an internal constructor: nothing but the reader can make one, so the builder cannot be handed a
  `CampaignBundle`. It holds a projected `BookCampaign` (no owner, members, invite code or world state).
- Only a DM can read, for either edition. The party edition reads every collection through the repositories' own access-scoped
  calls as a stranger whose id is made up per read (not `PartyPreview()`: see below). The one unscoped read is the session-id
  list, which never leaves a private method. Encounters are DM edition only. Journals and world chats are never read.
- Party edition: completed sessions only; no retired actors; public table notes only (both editions); secret-block markers
  stripped from every text, and the read **throws** if a marker line survives.
- **Canary test**: every DM-only field of a seeded campaign holds a `CANARY_*` string, including every account id; the party
  source is serialised whole and none may appear, with `PUBLIC_*` positive controls so an empty source cannot pass. Verified
  to fail under two sabotages (DM access for the party read: 26 canaries leak; empty user id: the empty-owner actor leaks).

What the canary found beyond the repositories' redaction (which held for everything else seeded):

1. `FactDocument.Metadata` is handed to players as stored. The reader clears it for the party edition.
2. Account ids ride along on wiki and homebrew `AuthorUserId`, session note and note authors, roll log and actor owners. The
   party edition clears them all, so it holds no account id. (Crediting players later needs an explicit projection.)
3. `PartyPreview()` uses an empty user id, which matches an actor stored with `OwnerUserId == ""`, skipping its Unknown mask.
   Nothing in app code stores that today, and it only affects the DM's preview, so it is queued as a separate fix; the book
   reader avoids it with a synthetic id.

Not covered yet: images (portraits and art are read through masked paths in a later step), `:::for` per-player editions, name
mentions (`[[actor:id|Name]]` resolving to a hidden actor's name), and re-checking the DM's role at read time (export does not
either).

## 11. Theming and custom CSS (proposal)

- **Do not link Homebrewery's CSS at runtime.** It would break the closed-network rule and the preview CSP, tie output to a moving
  upstream, and its 5e theme depends on assets and fonts with no stated licence (section 6).
- **Ship our own default theme** built on CSS variables (palette, fonts, page size, margins, columns, frame and ornament slots).
- **A custom-CSS layer** applied after the default, scoped to the book, size-capped, shown in the live preview (a sandboxed
  iframe, no scripts, CSP `default-src 'none'`; `url()` allowed only for `data:` and slot assets). The print browser still has JS
  off and every non-asset route aborted, so CSS cannot exfiltrate. A "reset to default" button; a theme is data (CSS plus assets)
  stored with the campaign or the user and exportable.
- **Homebrewery-compatible class names** where cheap (`.page`, `.monster`, `.note`, `.wide`, `.toc`), so CSS written for
  Homebrewery mostly works when pasted. Its `url()` assets will not load; the user supplies their own through slots.
- **Optional later**: vendor Homebrewery's MIT structural CSS (no assets, no fonts) as a selectable theme, after a per-file review.

## 12. The Markdown renderer: built

`src/Codex.Core/Book/` (`BookMarkdown.Render(text, options)` returns HTML, the headings for a TOC and the PDF outline, and warning
codes). Pure: no repository, no I/O, so it cannot reopen the masking question of section 10. 38 tests in `BookMarkdownTests`.

- **Dialect**: headings, paragraphs (hard breaks), bold/italic/strike/code, nested lists, quotes, pipe tables with alignment,
  fences, rules, `\page`, `\column`, `{{class,#id,prop:value}}` as block (opening line holds only the attributes, `}}` closes) and
  as inline span, `{{block:N}}`, image slots, `[[wiki]]` and stored mentions. Every block and cell carries `dir="auto"`, so a
  Russian paragraph beside a Hebrew one each take their own direction.
- **Refused on purpose, each reported as a warning code**: raw HTML (everything is escaped, every tag is written by the
  renderer); any URL (a link keeps its words, an image must be `slot:id` or `asset:id`, and art arrives only as a `data:image/`
  URI from the caller); CSS beyond a property allow-list with values that cannot hold a function other than colour or
  arithmetic (`url(`, `image-set(`, `expression(` all fail); class and id names that are not plain identifiers.
- **Mentions fail closed**: the name comes only from the caller's resolver, and a mention's own `|label` is never used as a
  fallback, since that is where a typed hidden name would ride along. No resolver means no name.
- **Secrets, a second time**: the renderer settles `:::` blocks itself from `SecretText.Parse` before any Markdown is read. The
  party edition drops what a stranger may not read even if the source was not cleaned; the DM edition boxes it
  (`.secret.secret-dm`, `.secret.secret-for`). A marker never reaches the output. This is defence in depth under section 10, not
  a substitute for it.
- **Bounded**: text length cap, paragraph cap, block and inline nesting caps, regex timeouts, and an unmatched-delimiter memo so
  200,000 stars cost one scan. A hostile-input test (stars, brackets, braces, quotes, backticks) runs under a time bound.
- **Mutation checked**: secrets always boxed (2 tests fail) and mention label fallback (1 test fails); both reverted.

Known limits, none verified against Homebrewery's own output:
- "Compatible" is by intent, not by test. There is no conformance suite against `marked-hbfm` yet; it needs real Homebrewery
  documents run through both. No setext headings, reference links, footnotes, HTML, `{{pageNumber}}`/`{{footnote}}`-style
  special spans, or `{{ }}` blocks whose values contain a space unless quoted (`font-size:"calc(1em + 2px)"`).
- Loose/tight list detection and lazy continuation follow the common cases, not the full CommonMark algorithm.
- A `}}` line inside a code fence inside a block still closes the block.
- Mentions resolve only through a caller-supplied function; the resolver that applies the audience (so a party book never names a
  hidden actor) is not written yet. That is the "name mentions" item under "Not covered yet" in section 10.
