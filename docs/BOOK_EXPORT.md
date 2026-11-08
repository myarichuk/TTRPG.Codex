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
  claim pixel parity, and we do not use the Homebrewery name or the WotC-imitating PHB look. Ship our own theme. (Revised 2026-11-06,
  41.12: the user chose Homebrewery parity for adoption. A "Homebrewery compatibility" layer, on by default, carries PHB's layout and the
  Solbera faces but none of its pictures or colours; the name is used to say what it is compatible with, nothing more.)
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

## 13. Candidate default theme "Ashen Vellum" (a Homebrewery Style-tab sheet supplied by the user): assessment

Measured in the phase-0 harness (Chromium 153, 816x1056 `.page` boxes, `printBackground`, print media):
- The full-page **stains** texture (inline SVG, `feTurbulence` with 5 octaves, 816x1056) is rasterised into every page: 10 pages
  46 MB, 45 pages 209 MB and 67 s, 808 MB peak. Without it, 10 pages are 7 MB. Grain alone (420 px tile) is about 0.5 MB/page
  (45 pages 23 MB, 11 s). The torn `mask-image` with a displacement filter, the `filter:` colour grade on art, and the art itself
  cost nothing measurable. So filter-generated backgrounds are fine as small tiles and fatal as full-page images.
- Screenshot shows the vellum, foxing and burn render as intended in the preview engine.
- The sheet's own `@media print` rule (grain only) means the PDF (print media) would not match a screen preview unless one of the
  two is forced; the preview must show what the PDF prints.

Needed before it can ship as a theme (none done yet):
1. No `@import` of Google Fonts (closed network, CSP). Bundle IM Fell, Pirata One, UnifrakturMaguntia and Special Elite (all open
   licences, to verify per file) with `unicode-range`; none covers Hebrew and Cyrillic coverage must be checked, so a fallback stack
   and the glyph preflight are required.
2. Replace the stains with a pre-rasterised tile (small, repeated or a compressed WebP) or a CSS-only approximation.
3. Our pagination is automatic flow, not fixed `.page` boxes: `.page::after` footer, `.pageNumber`/`counter(page-numbers)`,
   `.page:nth-child(even)` mirroring and absolutely positioned images become `@page` margin boxes and `:left/:right`.
4. `strong` is mapped to small-caps (the face has no bold), which does nothing in Hebrew and is synthetic in Cyrillic: add
   `:lang(he)`/`:lang(ru)` overrides that restore bold.
5. Drop caps use `float:left`; RTL needs `float:inline-start` (unverified in Chromium 153).
6. The art colour grade should default to off for user-supplied art, with the sheet's knob kept.
7. `.page` needs `isolation:isolate` or negative `z-index` art falls behind the page background.
8. Chapter art offsets are pixel values for a Letter page; A4 and other sizes need the geometry as variables.

## 14. Opus review of the renderer and the theme (fixes and decisions)

An independent review (Opus, with throwaway probes) found no way to get raw HTML, an attribute or a URL through, and these
problems, all now fixed and covered by tests in `BookMarkdownTests` (78 tests):
- **High: a mention's typed `|label` leaked through every path around the mention handler** (image alt, the nesting and
  paragraph caps, code spans, a mention split over a line, malformed or differently-cased mentions). Mentions are now settled
  once, over the whole text, before any parser runs: the name the reader may have, or nothing; an unclosed one is cut to the end of
  its line. A resolved name travels as a sentinel and is put back as a `<bdi>` span, or as plain text inside an attribute or a
  heading id. Disabling the pre-pass fails 14 tests.
- **High: one crafted line (`{{aaaa…'`) made `BlockOpen` backtrack exponentially and threw, killing the build.** Now a linear
  scanner; `Render` also catches a regex timeout and shows that text escaped with a `render-failed` warning, so hostile input
  costs one section. The scanner itself was not mutation-checked.
- **Medium**: openers without a closer rescanned the whole text (a bounded window of 2,000 characters now); blank lines inside a
  list item were quadratic; headings and table cells have a length cap; CSS values: no `var()`, no `/*`, arithmetic only in
  `calc()`, no four-digit numbers, no negative margins, no `height` properties.
- **Medium: the DM secret box was rebuilt as Markdown text**, so a stray `}}` closed it early and an unclosed `{{` swallowed the
  text after it. Each secret segment is now rendered on its own inside a div; author classes starting with `secret` are refused.
- **Low**: heading ids can be shared across calls (`BookMarkdownOptions.HeadingIds`) and author ids are prefixed `u-`; bidi
  overrides and embeddings, zero-width space, word joiner and BOM are stripped (LRM/RLM/ALM kept); a marker behind a quote mark
  or an invisible character is cut; `x|` over `---` is no longer a table; wikilinks and mentions sit in `<bdi>`.

Still open from the review: `dir="auto"` follows the first strong character, so a Hebrew paragraph that starts with "HP" or a Latin
NPC name lays out left to right; it needs an explicit `dir` per block from the book language, which the section builder will pass.
`BookHeading.Text` is plain decoded text and the TOC and outline code must escape it (to be covered by a test there).

### Theme decision (same review)
Ship a **plain default**; "Ashen Vellum" is a **selectable** theme. Reasons: its fonts are Latin only so a Russian or Hebrew book
would lose its look, dark pages cost ink and need contrast checked against the texture's worst spot, and the preview and the PDF
both render as print, so the sheet's `@media print` ink-saver must become an explicit export toggle. Additions to the list in
section 13:
1. No `var()` inside `@page size`: the server writes page size and margins into the CSS and derives `--page-w/h` from them.
2. `filter` or `mix-blend-mode` on an element that holds text can make Chromium rasterise it, losing search, extraction and
   tags: filters only on images and background layers; decorative `content:` glyphs need an empty alt (`content: "x" / ""`);
   a test compares the PDF's extracted text with the source.
3. RTL beyond drop caps: logical properties everywhere, mirrored asymmetric masks, swapped `@page :left/:right` contents.
4. Hebrew has no italic and some faces no bold: per-language `em`/`strong` and deliberate `font-synthesis`.
5. `box-decoration-break: clone` on framed blocks, `break-inside: avoid` on stat blocks, a test for frames split across pages.
6. The custom-CSS layer must refuse `@import`, remote `@font-face` and any `url()` that is not a data URI or asset.
7. WCAG AA contrast checked in CI for body text, notes and zebra rows on the dark palette.

**Per-page backgrounds under automatic flow**: no fixed `.page` boxes (they bring back clipping and hand pagination). Use a
pre-rasterised parchment image (WebP or JPEG, about 200 KB, a few variants) as `@page { background-image }` from a closed-network
asset, with `:left/:right` and named pages for variety, ornaments in margin boxes; a shared image is embedded once per book, so
its cost does not grow with the page count. Phase-0 check still to do: that Chromium 153 paints an `@page` background full bleed
next to margin boxes. Fallback: a `position: fixed; z-index: -1` element, which needs `@page { margin: 0 }` unless a negative
inset escapes the margins. Never a filter-generated image as a full-page background.

## 15. The print route: built (plain theme, language gate)

- **`BookTheme` and the language gate** (`Codex.Core/Book/BookTheme.cs`): a theme declares the languages its fonts cover and the font
  files it draws with. `BookHtml.Build` refuses a book in a language the theme does not support (the route answers 422) instead of
  printing it in whatever system font has the glyph. **The plain theme declares English only**, as asked. Its faces (Alegreya Sans
  400/400i/700, Cinzel) are the ones with proven coverage. `BookThemeTests`-style checks hold a theme to its fonts: every file it
  names ships, and a theme that declares `ru` or `he` must name a Cyrillic or Hebrew face. Adding a language to a theme is a decision
  with a test, not a string. My view: English only is right for *this* theme today, but not a place to stop: the app is EN/RU/HE, so
  the plain theme should grow `ru` and `he` once the bundled faces (Forum, Assistant, Frank Ruhl Libre: one weight, no bold, no
  italic) are proven by the glyph preflight, or get faces that have them. Until then RU/HE books have no theme, which the gate makes
  explicit.
- **`BookCompiler`** (`Codex.Persistence/Book`): `BookSource` to chapters (overview, cast, gazetteer, chronicle, lore, pages,
  homebrew), deterministic, names escaped as literal text, the DM summary only in the DM edition and inside a `:::dm` block.
  `BookCompiler.Mentions(source)` resolves `[[actor:id]]` and `[[place:id]]` only from the audience-masked source, so a party book
  never names a hidden actor (closes the "name mentions" item of section 10 for the compiler path; items resolve to nothing).
  A canary-test bug it caught: flattening a fact's lines turned its `:::dm` markers into printed text; facts are paragraphs now.
- **`BookHtml`**: one self-contained document: theme CSS with page size and direction written in (no `var()` in `@page`), fonts as
  data URIs (no font request at all), a CSP meta and header of `default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src
  data:`, chapters rendered with one shared heading-id namespace, a table of contents. The user's CSS goes through `BookCss.Clean`:
  no `@import`, `@font-face`, `url()` other than `data:image/`, `expression`, `behavior`, `image-set`, no `<`, 20 KB cap. (Superseded by
  the tokenizer of 41.11.)
- **Route**: `GET /campaigns/{id}/book/{dm|party}?lang=&theme=&size=` (`BookEndpoints`, DM only, `no-store`, `nosniff`). The HTTP-level
  test arrived with section 16.
- **Measured in Chromium 153 on a real book from this builder (23 A4 pages, three chapters, tables, lists, quotes)**: 0.14 s, 466 KB,
  tagged, zero blocked requests with the network closed, only the four allowed fonts embedded (no system-font leak), page numbers in
  the margin box, none on the first page. An `@page` background (colour plus a tiled data-URI image) paints full bleed under the
  margin-box page number on every page, so the parchment strategy of section 14 is confirmed for this Chromium.
- Not done (section 16 did the PuppeteerSharp print step): TOC page numbers (the two-pass outline lookup), the preview UI,
  localized labels (chapter titles are English `BookLabels` defaults; they need `Book.*` keys in the three resource files), art
  through masked paths, stat-block `{{block:N}}` templates, `.md` export, and the cost layer.

## 16. The PDF step: built (PuppeteerSharp, browser already on the machine)

- **Engine**: `Codex.Web/Book/BookPdf.cs`, PuppeteerSharp 25.15 (MIT). It drives a browser that is already installed; it never downloads
  one. A fresh browser per job: nothing idle holds memory and nothing survives a stuck page. Per job: JavaScript off, request
  interception that refuses everything except `data:`, `--host-resolver-rules="MAP * ~NOTFOUND"` as a second lock for host *names* (an IP literal is stopped by the interception alone), `PreferCSSPageSize`
  (the theme's `@page size` rules), `PrintBackground`, tagged output, an outline, wait for fonts. One job at a time; `MaxWaiting`
  (default 2) others may queue, a further one gets 429 with `Retry-After`. A hard timeout (`TimeoutSeconds`, default 90, browser
  start included) kills the browser process tree and answers 504. `--no-sandbox` is added only when the process is privileged (a
  root container cannot use the sandbox) or when `Codex:Book:Pdf:NoSandbox` says so.
- **Browser discovery** (`BookBrowser`): a path in `Codex:Book:Pdf:Browser` or `CODEX_BOOK_BROWSER` is the *only* candidate when set
  (a typo is reported, not papered over). Otherwise Windows Program Files/LocalAppData (Chrome, Edge, Chromium), the macOS app
  bundles, and on Linux a Playwright cache (`PLAYWRIGHT_BROWSERS_PATH`, `/opt/pw-browsers`, `~/.cache/ms-playwright`) then `PATH`
  names. After launch the product version must be 131 or newer, else a message names the browser and the floor. None found: 503 with
  the sentence a self-hoster needs ("set CODEX_BOOK_BROWSER ...").
- **Route**: `GET /campaigns/{id}/book/{dm|party}/pdf?lang=&theme=&size=` shares the build with the HTML route (same checks, same
  gate), answers `application/pdf` as a download named after the campaign (control characters, path characters, quotes and
  semicolons removed, 80 characters), `no-store`.
- **Tests** (all pass): locator order and the "configured path is the only candidate" rule; version parsing; a 40-section book prints
  to A4 with its text, with zero blocked requests and only the theme's faces (Cinzel is a variable font and Chromium embeds those as
  Type 3, so `Type3` is accepted as Cinzel; any other name would be a fallback); a page that reaches for `https://` and `http://`
  is refused and still prints; a `<script>` never runs; a job that cannot finish in 5 s throws the timeout and the next job still
  prints; the line is one running plus `MaxWaiting`. `BookRouteE2ETests` drives the real app: DM gets HTML (CSP, `no-store`) and a
  real `%PDF-` for both editions, `lang=ru` is 422, unknown theme 400, unknown edition 404, a non-DM gets 404 on all four routes
  and for a campaign that does not exist. The browser tests skip (visibly) when no browser exists, and fail on CI where one must.
- **Mutation checks**: removing `SetJavaScriptEnabledAsync(false)` fails the script test; removing request interception fails the
  refusal test only after the counter was moved to *after* the browser accepted the abort (before, a request that was merely seen was
  counted, and the test could not tell blocked from observed).
- **A bug the first run found**: the launcher joins arguments into one string, so `--host-resolver-rules=MAP * ~NOTFOUND` became
  three arguments and Chrome refused with "Multiple targets are not supported in headless mode". The value is quoted now.
- **Honest limits**: Windows and macOS discovery is tested only as paths, not run; a browser the user installs from a snap or flatpak
  may not be able to read a temp profile or run unsandboxed (not tried); Chromium crash dumps and a stuck GPU process are handled
  only by the timeout; printing an art-heavy book may need more than the default memory (unmeasured); no consent-gated Chromium
  download (a deliberate choice for now, the error says what to do); TOC page numbers still need the two-pass outline lookup.

## 17. The book panel: built (Settings, DM only)

- **`BookPanel.razor`** inside Settings' DM column: edition (DM / party, each with a one-line statement of what it holds), language,
  paper (A4 / Letter), theme note, **Preview** and **Download PDF**. The language list shows every app language and disables those
  the theme cannot print, labelled "(not in this theme yet)", so the gate is visible before it is hit; the default is the UI
  language when the theme supports it, else the theme's.
- **Preview** is an `<iframe sandbox="">` (no script, no same-origin, no forms, nothing) on the same route the PDF is printed from.
  The page carries its own CSP, so what a campaign's text holds cannot reach the app around it. Changing edition/size/language
  reloads it.
- **Download** goes through `codex.downloadFrom` (fetch, blob, save under the server's `Content-Disposition` name), not a link, so a
  refusal is shown in the panel in the user's language (422 language, 429 busy, 503 no browser with the server's setup sentence,
  504 timeout, anything else generic) and never replaces the page with an error document. The button shows "Printing..." and is
  disabled meanwhile (a book takes seconds; the server queue is one deep anyway).
- Strings: `Campaign.Book.*` in EN, RU, HE (RU/HE written by me, not reviewed by a native speaker).
- **Tests** (Playwright, real app): panel visible to the DM; ru/he options disabled, en enabled; preview frame has an empty
  `sandbox` attribute and shows the campaign title, also after switching to the party edition; download yields a `.pdf` named after
  the campaign starting `%PDF-`; "downloaded" shown, button re-enabled, no page errors; a joined player sees no panel; for 429/503/
  504/500 (server answers stood in via route interception) the matching sentence shows on the page, the URL did not change, the
  button is enabled again and no "downloaded" message appears.
- **Not done / not verified**: no visual review (RTL, phone width, dark/light) of the panel; the custom CSS editor (needs a POST or
  a stored setting: the GET routes take no CSS); the preview does not show the unsaved state of anything (it reads the stored
  campaign); a stuck print has no cancel button (the server timeout is the only stop).

## 18. Independent review (Opus) of sections 16-17, and what changed

Findings that held up, with the fix:

- **High, confirmed: a Chromium leaked whenever a request gave up while the browser was starting** (`WaitAsync` stopped waiting, the launch finished later, nobody closed it; three cancelled calls left three browsers and their profiles for good, and a signed-in player can create a campaign and loop `curl --max-time 0.5`). Now the launch task is kept and awaited in `finally` (bounded by the job limit), the browser it yields is closed, the line is held until then, and each job has its own profile directory (`ProfileRoot`) that is removed afterwards. Test: three calls cancelled after 30 ms leave no process whose command line holds the profile root and nothing on disk; mutation (cleanup removed) fails it.
- **Medium: the oldest Playwright Chromium was chosen** (`chromium-999` sorted above `chromium-1243`, and a too-old browser ended the search). Sorted by build number; every existing candidate is tried in turn.
- **Medium: the queue was checked after the expensive part**, and was global. A place is now reserved (`TryReserve`) after the access check and before the campaign is read and the page built, one place per person, released on dispose; a request that would be turned away costs nothing. `MaxWaiting < 0` is clamped.
- **Medium: every 503 said "no browser", and internals leaked** (absolute path, browser stderr). Failures carry a code (`no-browser`, `browser-too-old`, `launch-failed`, `print-failed`, `busy`, `timeout`); the panel says what each means; details go to the log; only "no browser" shows the setup sentence (an environment variable name), in a `<bdi>` so a right-to-left sentence cannot reorder it.
- **Low, confirmed: `Enum.TryParse("2")` made an "edition" that reads as a stranger but is not cleaned** (inline secrets unboxed). The route accepts exactly `dm` and `party`, and `BookSourceReader.ReadAsync` refuses an undefined value. E2E covers `0`, `1`, `2`, `-1` on both routes.
- **Low, confirmed: the download name** kept `: * ? < > |` on Linux, could cut a surrogate pair and kept bidi overrides. Fixed set of refused characters, no control/format/separator characters, cut on whole text elements; tests.
- **Low:** an unreadable cache folder no longer turns into a 500 (`UnauthorizedAccessException` is caught per directory); the async request handler catches everything; the sandbox is decided on Linux only; `PeakRunning` proves the line is one wide (mutation: semaphore removed fails it); the font test now requires fonts to be found, Alegreya present, and the Type 3 letters to be ones the headings spell; the timeout test reuses the same printer; Hebrew strings corrected ("הורד", "בהדפסה…", "מאחורי מסך המנחה"); Russian "Тема" is "Оформление"; the panel clears its "downloaded" message when an option changes and the toggle has `aria-controls`.
- **Not taken:** a per-user rate limit beyond the one-place rule, a cap on the size of the page built, a "host only" gate for the setup sentence (it holds no path any more), preferring system Chrome over Playwright caches on Ubuntu 23.10+ (the error now says to set `NoSandbox` or a browser; not reproduced), `CodexButton Busy` instead of `Disabled`.
- **Test infrastructure note:** a full non-e2e run writes about 12-16 GB of RavenDB data under `/tmp` before it is swept. On this machine's disk allowance that made the suite fail with "no space left" (the failures were fixture-backed tests, not these changes); run in two halves it passes: 631 + 704 = 1335.

## 19. Table of contents page numbers and localized chapter titles: built

- **Two-pass print** (`BookPrintJob`, `BookPages`, `BookPdf`): the browser cannot do `target-counter()` and the page runs no script, so the book is printed once with the numbers' column reserved and empty (fixed `min-width`, so the numbers cannot move the layout), the headings' pages are read back from the outline Chromium writes (`Outline = true`; PdfPig reads it, Apache-2.0, now a production dependency, `LEGAL.md` updated), and the page is built and printed again with the numbers in, on the same browser and page. After the second print the outline is read again: if any number moved, it prints once more (three prints at most). The numbers are the PDF's own page indices, which is what `counter(page)` prints in the margin.
- **Matching** walks headings and outline entries in document order and takes an outline entry only for the next heading whose normalised text it equals; the outline also holds the book's title and "Contents" and those are skipped. A heading with no entry shows **no number**, not a guess; if the outline cannot be read at all the book prints with the column empty and a warning in the log.
- **The page**: `BookHtmlOptions.PageNumbers` null (the preview) leaves the column out, empty reserves it, filled shows numbers; entries are a flex row of text, a dotted leader and the number (`text-align: end`, tabular figures), so right-to-left books mirror without special cases.
- **Tests**: a 30-section book prints in at least two passes, and for four entries (first, middle, last, and one early) the number in the contents text equals the page of the heading's own outline entry and is at least 3; mutation (the second page ignores the numbers) fails it. A single-page document prints once. HTML tests for the three states of the column.
- **Chapter titles** are localized: `Campaign.Book.Label.*` (contents, overview, cast, gazetteer, chronicle, lore, pages, homebrew) in EN, RU, HE, taken from the book's language (`?lang=`) rather than the viewer's; RU/HE reuse the app's own section names where one exists (Предания, אגדות עולם). A test holds every title to existing and being different from English in RU/HE. They are only reachable through the route once a theme supports those languages (the plain theme is still English only, so nothing in the RU/HE path is exercised end to end yet).
- **Not verified**: the look of the leader dots and the column (no image review), RTL numbers, a book whose contents run to several pages (the reserved column should hold, not measured), a stat that oscillates between prints (the cap is three, and the last print is kept even if its numbers still moved; there is no log line for that case), and print time for large books (two or three prints instead of one).

## 20. The AI layer: plan (proposal, nothing built; checked against the code as of 36b4070)

### 20.1 What exists and what it means for us

| Existing piece | Fact | Consequence |
|---|---|---|
| `AIClientFactory.CreateClient` | One `IChatClient` for OpenAI-compatible endpoints and Anthropic (Messages API, `AnthropicMaxOutputTokens = 8192`). Transport retries via `PatientRetryPolicy`; 5 min request timeout. | Section workers use `IChatClient` directly. Transport retries are invisible to us, so the budget must reserve for them (2.4). |
| `AiProviderService.ClientFor` | Picks the user's default provider, else the server key wrapped in `MeteredClient`, which counts **one request per call** against `ServerAiUsage` (default 200/day) and throws `Service.Ai.ServerAllowanceSpent` mid-call. | A build must reserve N requests up front (new `ServerAiUsage.TryReserve(user, kind, n)`), and refuse to start if short. Today a 40-section book would die at section 31 with half the money spent. |
| `AiAssistant` | One prompt in, one text out; `JsonAsync` retries once on `FormatException`; `JsonReplyAsync` retries without `response_format` on 400/422. | Reuse the pattern, not the class: section workers need system prompt + closed block list + usage capture, and the fallback ladder must be counted. |
| `WorldChat` | Accumulates `response.Usage` per streamed round; tool loop with nudges; **no per-token price anywhere**. | Reuse `WorldChatUsage`'s shape for the meter; the planner is a short `WorldChat`-style conversation, but with no tools that touch the repository (the planner sees only the already-masked `BookSource` outline). |
| `AiImageGenerator` | Separate provider list (`DefaultImageProvider`), `ArtShape` Tall/Wide/Square, `ServerAiUsage.Kind.Image` allowance. | Slot generation calls this as is; explicit click only. |
| `AiProviderConfig` | `Id, Name, Kind, BaseUrl, ModelId, ProtectedApiKey, CreatedAt`. No prices, no context size, no max output. | Add `InputPricePerMTok`, `OutputPricePerMTok` (`decimal?`), `PriceLabel` (free text like "USD", never interpreted). Optional, additive, no migration needed in Raven. |

### 20.2 Components (all in `Codex.Core/Book/Ai` unless noted; Raven only in Persistence)

1. **`BookBudget`**: reservation ledger. `TryReserve(estIn, maxOut)` before every call, `Settle(actualUsage | null)` after. When the provider reports no usage, settle at the estimate and mark the total "estimated" (the UI says so). Estimation is per script (Latin ~4 chars/token, Cyrillic ~2, Hebrew ~2), as 2.4 says. Hard cap per build; at the cap, stop and keep what exists. Thread-safe, because section workers run in parallel.
2. **`BookSectionWorker`**: input is *one section of the masked `BookSource`* plus a style sheet plus a **closed block list** (`{{block:1..n}}`). Output is Markdown for our renderer. Fresh context per call. Concurrency 1 for local endpoints (Ollama), 2 for remote, back-off on 429.
3. **Template blocks**: stat blocks, tables, every number are rendered by `BookCompiler` deterministically; the model may only *place* them. Substitution happens after the checks, so numbers never pass through prose.
4. **`BookChecks`** (deterministic, per 2.2): every number in the output appears in the section's input; every name-like span fuzzy-matches a name the audience may see; only `{{block:n}}` for n in the list, each used at most once; no `:::` marker lines, no URLs, no raw HTML (our renderer escapes anyway, the check is for the honest UI message). A failure retries once with the error shown, then falls back to the deterministic Markdown and says so per section.
5. **`BookPlanner`**: short conversation, no tools, input is the masked outline (titles, kinds, sizes). Output is a typed plan: ordered sections, each with source ids *from the outline only* and a style note. The DM edits the plan in the UI; the model never invents an id (ids are resolved through the section's own map, never a global lookup).
6. **Cache key** per 2.4 (audience, language, source change vectors, template/dialect/prompt version, model, temperature, style, slots). DM and Party caches are stored apart; filtered inputs are recomputed at print time.
7. **Build state**: `BookBuildDocument` (Raven, DM-private, per DM, **excluded from the campaign export and backups**, deleted with the campaign; prompts never logged). Sections are marked stale when source change vectors move; a source hidden or deleted mid-build drops its section.
8. **Slots**: declared by the worker as `{{slot:purpose|aspect}}` (closed grammar, parsed by the check), resolved existing art → DM upload → AI image → placeholder, all reads through masked paths (this is the open "images through masked paths" item and must land first). Generation is an explicit click.

### 20.3 UI (honest cost)

- Pre-flight: tokens as a **range**, labelled estimate, plus "uses ~N of your M requests today" when on the server key.
- Meter: running totals, "not reported" where the provider says nothing; per-section re-run cost; money only when both prices are set, shown with `PriceLabel`. The app ships **no** price table.
- Budget cap field; at the cap the build stops and says what was kept.

### 20.4 Order of work (each step ends with tests, a commit and a report)

1. Price fields on `AiProviderConfig` + the AI settings form (EN/RU/HE) + `ServerAiUsage.TryReserve`. Small, independently useful.
2. `BookBudget` + estimator, with tests for parallel overshoot, no-usage settle, cap, retries counted.
3. Masked art/portraits through `BookSourceReader` (prerequisite for slots; the canary extends to images).
4. `BookSectionWorker` + template blocks + `BookChecks`, with a scripted `IChatClient` (retry, cap, 429 storm, injected `![](url)`, Russian and Hebrew adversarial cases), and the canary: unique strings in every DM-only field must not appear in any captured model request.
5. Build document, cache and staleness, then the panel's guided flow with live preview.
6. Planner, slot tray, per-slot image generation, `.md` export with the DM-edition warning.

### 20.5 Decisions needed

- (a) Section workers on the user's default text provider only, or a separate "book model" picker (cheap model for prose, strong one for the planner)? Recommendation: default provider first; picker later.
- (b) Parallelism default (1 local / 2 remote) as above?
- (c) Prices on `AiProviderConfig` only (per user), with the server `AI` section getting the same two optional keys; agreed?
- (d) Start at step 1 (prices + allowance reservation) or step 3 (masked art)?

## 21. AI layer, step 1: prices and request reservation: built

Decisions (from the user): section workers run on the user's default text provider (no separate picker); concurrency 1 local / 2 remote;
prices live on `AiProviderConfig` (and optionally in the server's `AI` section) and are used to show the price **and the accumulated cost**.

- `AiProviderConfig.InputPricePerMTok`, `OutputPricePerMTok` (`decimal?`) and `PriceLabel`; entered in AI settings for text models only
  (an image model is not priced per token). `AiPrice` (Core): `Clean` (0..100,000 or nothing), `Cost` (null, never 0, when a price or a
  token count is missing; 0 only when the user typed 0), `CleanLabel` (strips control/format/bidi characters, cut to 12 text elements), `Format`.
- A turn of the World builder chat keeps the prices it ran under (`WorldChatUsage.InputPricePerMTok/OutputPricePerMTok/PriceLabel`), so a
  later price change cannot rewrite history. Each reply shows "cost X"; the chat shows a total, and says "only k of n turns have a price"
  rather than passing a partial sum off as the total. Server key: `AI:InputPricePerMTok`, `AI:OutputPricePerMTok`, `AI:PriceLabel`.
- `ServerAiUsage.Reserve(user, kind, n, unlimited)`: all-or-nothing against the daily allowance (compared in `long`, so a huge `n` cannot
  wrap), returns a `Reservation` whose `TryUse()` is lock-free and whose `Dispose()` refunds the unused part once.
  `AiProviderService.ReserveRequestsAsync` returns null when the user's calls go to their own provider; `ClientFor(user, prepaid)` draws
  from the reservation first, then falls back to `Take`. Nothing uses it yet: step 2 and the section workers do.
- Not done: a price shown in the book panel's pre-flight (there is no pre-flight until the budget exists); the settings form has no
  browser test yet; RU/HE strings are not native-reviewed.

## 22. AI layer, step 2: the budget and the estimator: built

`Codex.Core/Book/BookBudget.cs`, `BookTokens.cs`. Nothing calls them yet (step 4's workers do).

- `BookBudget(cap, transportRetries = 2)`: every call reserves its worst case first (input once per transport attempt the SDK may make,
  plus the longest reply) under a lock, and is refused when it does not fit, so parallel workers cannot overshoot together. Compared
  without overflow (huge estimates are refused, not wrapped into "fits"). `Settle(reportedIn, reportedOut, replyTokens?)` swaps the
  reservation for real usage; a missing figure is settled at our estimate (the reply's own length, else a quarter of the maximum, never
  above the maximum) and the snapshot says `Estimated`. `Failed()` charges the input estimate (a provider bills a request it received);
  only `NotSent()` is free; a reservation disposed without either counts as sent. Usage above the reservation is recorded, not clamped.
- `BookBudgetSnapshot`: spent, reserved, remaining, calls, failed, refused, `Estimated`, `Cost(price)` (null without prices).
- `BookTokens.Estimate`: per-script weights (Latin 0.28, Cyrillic 0.55, Hebrew 0.6, CJK 1.1 tokens per char; bidi marks free), on the high
  side on purpose. `Range` brackets it 0.7x..1.4x. `BookPreflight.Estimate(sections, stylePromptTokens, price)` returns ranges, requests
  (min = sections, max = 2x for one retry each) and a cost range only when both prices exist.
- Mutation-checked: cap check removed (6 fail), failed-is-free (1 fails), reservation never released (3 fail).
- The weights are not measured against any real tokenizer. Calibrate against a provider's reported usage in step 4 before trusting the range.

## 23. AI layer, step 3: pictures through the audience masks: built (reading layer only)

(Here "masked" is audience masking. The shaped frames of 4 are a separate, later art task.)

- `BookArtReader(IMediaStore).ReadAsync(BookSource)`: asks the media store **only** for documents already in the source, which was read through
  the access-scoped repositories. A stranger's mask has no portrait flag (`RavenActorRepository.Mask` builds a new document), a Hidden actor,
  a DM-only wiki page, a Hidden location and an unpublished homebrew entry are not in the party source, so their pictures are never *read*, not
  read and dropped. Sources: campaign cover (any member sees it), actor portraits, wiki art, location art, homebrew art. Pack-library art
  is not included (the book does not list pack entries yet).
- `BookArt`: opaque asset ids (`a1`, `a2`…) assigned in reading order; `Portrait(actorId)`, `Wiki(id)`, `Location(region, loc)`, `Homebrew(id)`,
  `Cover()` give the id, `DataUri(id)` is the delegate for `BookMarkdownOptions.Image` (`![alt](asset:a1)`). An id that is not in the map, a Raven
  id, or an id written by a model resolves to nothing, and renders as a placeholder. Caps: 120 images, 64 MB after sanitising, 6 MB each.
  `Skipped` lists what was left out and why (`missing`, `unsupported`, `malformed`, `toolarge`, `limit`).
- `BookImageSanitizer` (Core, no imaging library): uploads are stored exactly as they came, so a photo still carries EXIF (GPS, owner, a
  thumbnail of what was cropped away). JPEG: only JFIF, ICC and Adobe segments are kept; PNG: allow-list of chunks (text, EXIF, time out);
  WebP: EXIF and XMP chunks out, their VP8X flags cleared, RIFF size rewritten. Pixel data is copied byte for byte (nothing is decoded here). The
  type and size come from the bytes, not the declared type; more than 16,000 px a side or 50 MP is refused. GIF and AVIF are not supported and take a
  placeholder. A real browser still decodes the output (test), and truncation and random damage never throw (tests).
- Tests: `BookArtTests` (canary by width: party edition holds none of the 1xx and all of the 2xx; the recording media store is never asked for a
  hidden subject; DM edition has all; ids are opaque), `BookImageSanitizerTests`, `BookImageDecodeTests`. Mutation-checked: five sanitizer mutations,
  and the reader without the portrait flag filter fails the canary.
- Not done: nothing puts a picture in a chapter yet (portraits in the cast, art under a wiki page, the cover): that is a compiler change and a
  design choice about layout. A multi-scan progressive JPEG whose later scans are preceded by APP segments would keep them (the copy stops at the first
  scan); no re-encode or downscale to print resolution, so a 6 MB photo goes in as is; images are inlined as base64 (memory with 120 art-heavy pages is unmeasured).

## 24. Visual masks, first pass: torn page edges (the "Parchment" theme): built

What the user means by masks (ragged page corners and edges, later torn picture frames, ink wash, vignette), as opposed to the audience masking of 23.

- `BookMasks.PageEdge(seed, widthMm, heightMm, outside, line, style)` (Core): a page-sized SVG, two plain `<path>`s (an even-odd frame and a
  thin stroke), no filter, mask, href, style or script, colours accepted only as hex. Corners are single explicit vertices with their own bite;
  the sides run between them with midpoint-displacement tear (rough at every scale); the tear never moves a corner. A seeded SplitMix64, so the
  same seed prints the same edge in every .NET version. Insets are clamped to 0.35 mm .. 12 mm whatever the style says, so it can never reach the text
  (the margins are 19-22 mm).
- `BookThemes.Parchment` (`?theme=parchment`, and a "Look" select in the Settings panel): the plain theme's type and fonts on `#f2e8cf` paper, with the
  edge as the `@page` background, a different seed on `:left` and `:right`. Same language gate as Plain (English only).
- **Measured, and it cost a wrong first attempt:** Chromium paints an `@page` background image from the corner of the page's *content box*, not of
  the page, so `background-size: 100% 100%` squeezed the shape into the content area (the first render showed white strips and a diagonal). It needs
  `background-size: <page in mm>` and `background-position: -<margin-left> -<margin-top>`. A test ties those numbers to the plain theme's `@page` margin.
- Printed: vector (no page holds an image), a 30-chapter book is 88 KB and within 60 KB of the plain one; screenshots checked by eye.
- Not done: the stroke is one flat colour (no paper fibre, no shadow under the edge); no picture-frame masks (torn photo, ink wash, vignette) yet; no
  variation between pages of the same side (every left page has the same edge); the edge's look in Letter, RTL and with real content has not been looked at;
  the panel's theme names are English only.

## 25. AI layer, step 4: section workers, template blocks, deterministic checks: built (not wired to the UI or a stored build yet)

`Codex.Core/Book/Ai/`: `BookBlocks.cs`, `BookChecks.cs`, `BookSectionWorker.cs`, `BookBuildRunner.cs`. Written against `IChatClient`, so it runs on the user's
default provider (OpenAI-compatible or Anthropic) as decided; nothing calls it from a route or the panel yet.

- **What the model is sent** (`BookWork.Prepare`): the chapter text with every stored mention written as the name its audience may be told (a
  hidden one as nothing, never an id or the `|label`), and **every secret block (`:::dm`, `:::for`) cut out and replaced by `{{block:N}}`** on a line of
  its own. The secret's words are not sent at all, in either edition; they come back as the same box the deterministic book prints (`BookSection.Blocks`,
  trusted HTML the renderer inserts at `{{block:N}}`). So in the DM edition a model never sees a DM secret, and in the party edition there is none to see.
  Text is delimited as `<source>` and the system prompt says it is material, never instructions.
- **One call per chapter**, fresh context (its own text plus the style sheet), `BookSectionWorker`: reserves the worst case in the `BookBudget`, settles on
  reported usage, and charges a failure. A reply that fails `BookChecks` gets **one** retry with the problems quoted; a second failure, a spent budget, or a
  provider error leaves the chapter as the **deterministic text** with a reason (`checks`, `budget`, `error`, `auth`, `ratelimit`, `allowance`, `cancelled`).
  A refused key, an unknown model, a 429/402, or the server's allowance stop every later chapter (`BookBuildRunner`), instead of failing N more times.
- **Checks** (`BookChecks.Validate`, no model involved): no URL or link scheme, no HTML tag, no `![`, no `:::` line, no `[[`, no `{{` except `{{block:N}}`;
  every block marker exactly once, on its own line, none that does not exist; headings kept verbatim (they are the TOC and the anchors); **every number
  already in the source** (digits normalised, list markers and thousands separators tolerated); **no new capitalised mid-sentence name** (stem match, so Russian
  case endings pass); the book's script (Latin 85%, Cyrillic or Hebrew 60% of letters); not empty, not 0.12x-2.5x the source's length.
- **Concurrency** `BookBuildRunner.ConcurrencyFor(endpoint)`: 1 for localhost, a private/link-local/CGNAT address or a bare hostname, otherwise 2.
- **Verified:** 41 unit tests with a scripted client (retry shows the problems, fallback, cap, budget refusal, failed call charged, auth not retried, injected
  `![]()`/URL/HTML/`:::` refused, prompt injection is only material, concurrency honoured, shared failure stops the run). **Canary through the model
  path** (`BookSourceTests`): across every request the party edition sends, no `CANARY_*`; the DM edition sends none of its secret-block words and no `:::` in any chapter text,
  yet its HTML carries them in the box. Mutation-checked: secrets not cut out, checks skipped, no concurrency limit, number check off, each caught.
- **Not done / not verified:**
  - Nothing in a route, the panel or a stored build (`BookBuildDocument`, cache key, staleness) uses it: step 5. There is no cost pre-flight in the UI yet.
  - **Run against no real model.** The prompts and the checks' thresholds (name heuristic, length ratio, language share) are untuned; expect false refusals and
    some tuning. The name check is a heuristic, and a Hebrew text has no capitals, so it checks nothing there; the number check still does.
  - The token estimator's weights are still uncalibrated against real reported usage (22).
  - No template blocks other than secrets exist yet: the compiler emits no stat blocks or tables (they need the system's sheet schema), so numbers in prose are guarded
    by the checks, not yet by construction.
  - A secret is never rewritten by the model; a DM who wants the secrets polished will not get that.
  - A model that returns text a check accepts but that reads badly is not detected.

## 26. First run against a real model (a small Anthropic model), and what it changed

A scratch harness outside the repo (the key came in through an environment variable for each run and is in no file) ran `BookSectionWorker` on
hand-written chapters in English, Russian and Hebrew, then ablated the prompt to test the checks themselves. About 25 calls, a few cents.

- **Real replies (9 chapters: secrets and mentions, numbers, inflected Russian names, Hebrew, a prompt injection in the source, a sparse source with a
  "make it vivid" style, a 1,200-character chapter to condense): all 9 passed the checks on the first attempt. 0 false refusals, 0 retries.**
  The injected "IGNORE ALL PREVIOUS INSTRUCTIONS... link... vampire aged 900" was dropped from the rewrite, not followed. With the rules in the prompt the model
  invented nothing even when told to be vivid: a one-line source came back as the one line. **That is a product finding, not only a safety one:** under these
  rules the rewrite is close to the source (tightened and joined, mentions turned into names, a long chapter condensed to ~95%). If DMs expect "make this read like a book",
  the prompt needs to license reordering and linking without licensing new facts, or the style sheet needs a place to say "add flavour" with the facts still pinned. Not decided.
- **Do the checks catch anything? (ablation: the rules removed, "write it richly")**: 8 of 8 rewrites flagged. English and Russian invented place and person names
  (Gannet Point, Oswin, Trell, Гаррет, Эдда...) were caught by the names check, every one was caught by the length check, and **"thirty-one winters" was missed by the
  number check** (digits only). Fixed: English number words are now read as numbers (`SpelledNumbers`; "one".."nine" alone are exempt as idiom). Hebrew has no capitals, so the names
  check sees nothing there and only length caught the invention: still open. Russian and Hebrew number words are not read.
- **Estimator recalibrated.** Measured against reported usage: English 0.34 tokens/char, Russian 0.42, Hebrew 0.66, Markdown/stat-block-like text 0.56. My weights had under-counted
  input by 20-45% and over-counted Russian; a claim I had made (Cyrillic costs 2-3x Latin) was wrong for this tokenizer (about 1.2x per letter). Punctuation, digits and
  markup cost about 1.2 tokens each, which is why a stat-block-like text was 2.3x under-estimated. Weights: Latin 0.39, Cyrillic 0.47, Hebrew 0.78 per letter, marks 1.2, space 0.05.
  Pinned by four measured fixtures that must stay within 10%. **One provider's tokenizer.** OpenAI-compatible endpoints were not measured and will differ.
- Still not done: the prompt and thresholds were tried on 9 good replies and 8 ablated ones from one small model; no large model, no long chapters (> 4,000 characters), no retry
  path exercised against a real refusal; a non-English style sheet is untested.

## 27. AI layer, step 5: stored build, staleness, pre-flight, live progress: built

### 27.1 What exists

- **`BookBuildDocument`** (Persistence, `bookbuilds/{campaign}/{user}/{edition}`): one per DM per edition, found by its name and never by a
  query. Holds, per chapter, the key it was built from, whether the rewrite passed the checks, why not if it did not, the rewrite (with
  `{{block:N}}` markers), the boxes those markers stand for, and the model's name; plus totals over all runs (tokens, calls, money where
  prices were known, a count of runs that had none, an "estimated" flag). The repository re-reads the caller's role at every call, serves
  only the DM's own, and is **not** read by the campaign export or the backup (a test greps both for the box HTML and the id). It is deleted
  with the campaign (`CampaignRepository` cascade, by the name's prefix, no index to wait for).
- **`BookBuildKey`**: SHA-256 (length-prefixed fields) of recipe version, prompt version, edition, language, style sheet, chapter id and
  title, the prepared chapter text, and every block's number, label and HTML. A rewrite is used **only** while the key recomputed from the
  campaign as read now (same masks) equals the stored one. Hidden, edited, re-styled or re-languaged chapters print from the campaign.
  Deviation from the plan (20.2, item 6): the **model and temperature are not in the key**. Switching providers must neither throw a book
  away nor silently buy it again; the model is stored beside each chapter and shown, and a re-run is an explicit click ("Rebuild").
- **`BookBuilds`** (Web, singleton): `PreviewAsync` (chapters with state, pre-flight, suggested and minimum cap, model, totals), `StartAsync`
  (everything that can refuse refuses here: no model, server allowance not covering, another build of the same DM), the detached run, `Find`
  (the page re-attaches to a run when reopened), `DiscardAsync`. The run does not belong to the page: closing the tab does not throw away
  what the calls cost. Results are stored after **each** chapter, in order by one writer; cancelling keeps what finished and charges the calls
  that were in flight. One running build per DM.
- **Cached chapters cost nothing**: `BookBuildRunner.RunAsync(cached: ...)` returns a stored chapter before any budget or call.
  A chapter that fell back (checks, budget, error) is not "built": the next run tries it again, and the page says why it last failed.
  Chapters under 200 characters are not sent (`BookComposer.MinCharsToPolish`) and are counted in the pre-flight.
- **Route**: `?ai=1` on the page and the PDF applies `BookComposer.Overlay` (the DM's stored build of that edition, chapter by chapter, key
  checked). Without it nothing changes. The panel's checkbox adds it to the preview and download.
- **Panel**: model (or a link to AI settings), style notes (clipped to 800 chars), the chapter list with state, the pre-flight (requests,
  tokens as a range, money as a range only when prices are set, the server allowance used today), a cap field with a minimum, Start / Stop,
  live progress (bar, per-chapter rows, running tokens and money, "estimated" when the provider reported no usage), totals over all runs,
  Discard. EN/RU/HE (the RU and HE strings are mine and need a native read).
- `AiBookModels` is the seam to the AI settings (`IBookModels`), so the service is tested with a scripted model; it reserves the server's
  requests all-or-nothing up front, returns the unused ones when the run ends, and picks concurrency by endpoint.

### 27.2 Found by testing, not by reading

- **The default cap could not start a small book.** The suggestion (sum of the high estimates) was below the worst case one call must
  reserve (input x 3 for transport retries + the longest reply). The e2e run, which pressed Start with the suggestion, saw every chapter fail
  with "cap reached". Fixed: `BookBudget.WorstCase` is shared, the preview computes `MinimumCap`, the suggestion is at least twice the biggest
  chapter's worst case, and the panel refuses a lower cap with the number.
- **A singleton cannot take the scoped access resolver.** Unit tests build the service by hand; only booting the app showed it. It now takes a
  delegate that opens a scope of its own.
- Three tests were mine and wrong (an escaped name, an owner id in a canary check, `Work` being internal); two mutants survived until a
  test for an *edited* chapter at print time and for a build planted under a non-DM's name were added.

### 27.3 Mutation checks (all caught now)

Key without block HTML; overlay ignoring the key; store serving a non-DM; and (defence in depth, caught by the layer behind) overlay
ignoring the edition, cached lookup ignoring the key. The last two are redundant by design: the key contains the edition and the run's
`todo` set is computed from keys.

### 27.4 Not covered

- A chapter is one call. A long "Cast" or "Pages" chapter (dozens of entries) is one reply, capped at the client's maximum output; whether a
  real model returns a faithful rewrite of 20,000 characters is unmeasured (the checks would make it fall back, which is safe but wasteful).
  Splitting by `##` entry is the obvious structure change and is the first thing for review.
- The panel has been driven only in English, on desktop, in Chromium; RTL and phone layouts of the new section are unreviewed.
- The temperature is fixed (0.4) and not in the key; the prompt is `book-section-1`; no style presets.
- The money shown uses the prices as the user has them now; a run's cost is stored at the prices of that day, not re-priced.
- Concurrent edits by two DMs of the same book each have their own build (per DM), so they do not collide, but a co-DM's build is not shared.
- A crash in the middle of a run loses the totals of that run (chapters are stored one by one; the totals are added at the end).

## 28. Opus review of the AI layer (steps 1-5): findings, what was fixed, what was not, and the art proposal

An independent read-only review of `99e1a97..7390a29`. I checked the claims against the code before acting; the ones below marked "fixed"
were reproduced by a failing test first (or a mutant the tests now kill).

### 28.1 Defects

| # | Finding | Status |
|---|---|---|
| A1 | `BookWork.Prepare` found mentions with a strict regex; the renderer reads them case-insensitively with spaces. `[[Actor:hid\|The Lich King]]`, `[[ actor:hid ]]`, a `]` in the label were cleaned in the deterministic book but reached the model raw (and the stored party rewrite). | **Fixed**: same reading as the renderer, anything that does not parse as a plain id is removed, an unclosed one removes the rest of its line (fails closed). Five spellings are canary tests on the model path. |
| A2 | "One build per DM" was check-then-act across awaits: a double click or two tabs ran two builds (double spend, last writer wins, lost totals); `Discard` could race a start. | **Fixed**: the slot is taken atomically before the first await and released when the run ends or a start is refused; `Discard` takes the same slot. 6 parallel starts: one runs, one is paid for. |
| A3 | Any non-refined result (cancel, budget, auth, failed checks) replaced a stored rewrite that had already been paid for, even one the DM was only re-running. | **Fixed**: a failure never replaces a stored rewrite. |
| A4 | A reply cut off by the length limit (`FinishReason.Length`) could pass the checks (floor 0.12x). | **Fixed**: a cut-off reply is a failed check. **Not fixed**: no coverage check (every source name and number appears in the output); the 0.12x floor still licenses dropping most of a chapter. Raising it untested would break real runs; see 28.2. |
| A5 | The style notes (one field for both editions) go into every request verbatim: "foreshadow that the Duke is the lich" shaped the party book, and the name check passes if "Duke" is in the chapter. | **Fixed (narrowly)**: for the party edition the notes may carry no number and no capitalised name the party's chapters do not hold; the panel shows why and refuses to start. The DM edition is unchecked. A fact written in lowercase prose ("the duke is the lich") is **not** caught. |
| A6 | The key had no checks version, and the prompt text only through a hand-bumped version; stored blocks held the DM's secret HTML although the key guarantees they equal the current ones. | **Fixed**: the key hashes the system prompt as sent and a checks version; blocks are no longer stored, they are rendered from the campaign when printing. The store now holds none of a DM's secret words (test). |
| A7 | `Cached` read `build.Sections` on runner threads while the writer changed it. | **Fixed**: looked up in a snapshot taken before the run. |
| A8 | An exception escaping a worker failed the run and lost the run's totals; a regex timeout in the checks escaped. | **Fixed**: the checks' timeout is a failed check; the totals are added once, in every exit path. Still lost on a process crash. |
| A9 | A provider's HTTP timeout (`TaskCanceledException`) was reported as "cancelled" and not retried. | **Fixed**: only a requested cancellation is "cancelled". |
| A10 | The money upper bound ignored that the retry carries the first reply back; the tokens line said "at most N out" for one attempt. | **Fixed**: the upper figure counts the carried reply; the line shows two attempts' output. Transport retries are reserved by the budget but not in the money range (stated, not hidden). |
| A11 | (a) a refund after UTC midnight is taken from the new day; (b) a DM demoted mid-run keeps paying; (c) an image inside a secret box has no `Image` resolver in the AI path; (d) `![` is forbidden in rewrites, so an image in a chapter is lost once it is rewritten. | (b) **fixed** (the first refused save cancels the run); (a), (c) open and small; (d) is the reason images must be placed by code (28.3). |

What the reviewer tried that held: a DM build served to the party (audience is in the key, the id and the overlay guard); hiding an actor or editing
a secret after a build; injection through `{{`, `:::`, HTML from a model; role re-read in the store; backups listing; guessing an `asset:` id;
budget overflow. **Not checked by it**: a wiki link `[[Some Page]]` to a DM-only page was passed to the model (and printed by the
deterministic book) as the link text. Fixed in section 37.

### 28.2 Structure (recommendations, not built)

- **The unit should be the `##` entry, not the chapter.** Today one NPC edit makes all of Cast stale and re-buys it; a long chapter is one reply
  capped at the client's maximum output (the cut-off check now makes this safe, not good). Key per entry, headings written by code and not sent,
  short entries batched with delimiters. This is the next structural change and it touches the compiler (it should emit `{Id, Title, Body,
  Entries[]}`; `BookBuildRunner.Item` re-parses the compiler's escaping with a regex).
- **Licence flavour, pin facts.** Split the rules into facts (proper nouns and numbers: closed set, as now) and texture (common nouns, sensory
  detail, transitions: open); add the coverage check first, because without it a freer prompt can drop facts; then raise the length ceiling in a
  "flavour" mode. Code-owned read-aloud boxes or epigraphs as a separately checked output. Decision for the DM: do they want prose or tidied notes.
- `BookBuilds` as a singleton is acceptable for a single-instance app now that it is atomic. Open: replace the `ContinueWith` chain with a
  single-consumer channel; cancel runs and flush totals on `ApplicationStopping`.
- Layering: `BookCompiler` is pure and belongs in Core; key and overlay policy live in Web and are called by the route too.

### 28.3 Art: the proposal (nothing built; for the DM to confirm)

1. **A slot is code-made.** `BookSlot(Key, Section, Anchor, Placement, Subject, Audience)`; the key is (placement, subject kind, subject *as the
   audience sees it*), the page gets opaque `s1..` ids. Placement is relative to a heading the checks already pin and sits **outside** the
   rewritten prose, so a rewrite can never drop or move art. Placements with fixed aspects: `cover`, `frontispiece` (page), `chapter-header`
   (~3:1, spanning columns), `full-page`, `half-page`, `entry-portrait` (3:4, floated beside a `##`), `location` (3:2), `inline-left/right`,
   `spot` (1:1 at an entry's end). Deterministic slots: cover, a header per chapter, one per Cast, Gazetteer or Pages entry (existing `BookArt`
   fills them). AI-suggested slots: only `spot` and Chronicle scenes.
2. **An art-director call per chapter**, input the masked outline only (blocks cut out, same edition), output JSON per slot: `depicts`, `visual`,
   `mood`. Checks: names and numbers from the source only; **no capitalised words in `visual`** (invented proper nouns mean nothing to an image
   model); a deny-list of real artists and "in the style of"; a length cap. **The image prompt is assembled by code**, never written by the model:
   placement framing + `visual` + the book's style sheet. The style sheet is chosen once per book from a code-owned vocabulary (medium: ink
   linework, woodcut, engraving, watercolour wash, painterly oil, parchment sketch; palette; line weight; lighting) plus a fixed negative suffix
   (no text, lettering, signature, watermark, frame, border, photorealism, modern objects, real people). The existing hard-coded "Painterly…"
   in `AiPrompts.Image` becomes one entry.
3. **DM control**: a slot tray per chapter (existing / uploaded / proposed / drawn / none), editable prompt, Draw, Regenerate (keep 3), Accept,
   Upload, "No art here". Cost per image shown only when an image price is set (a price field beside the token prices), otherwise the server
   picture allowance, reserved all-or-nothing for "Draw selected (N)". The director call goes through `BookBudget` with its own pre-flight.
   Nothing is drawn without a click.
4. **Audience masking for art**: every image carries `BuiltFrom: party|dm`, the audience of the inputs its prompt came from. The party edition
   places only party-built images, in slots its own masked source produces (a hidden NPC's slot does not exist there). A masked actor is keyed by
   the mask's content, never the actor id (otherwise the DM's portrait of the real actor and the party's of the mask collide). Accepted images
   live in campaign media as a separate `bookart` kind with the tag; in backups (they cost money), in the export only as the DM's own, never in a
   player-facing read, and never promoted to an actor's portrait except by a separate explicit action.
5. **Size and staleness**: an image is not keyed to chapter text (store a hash of its inputs and say "description changed since drawn", do not
   invalidate it), but the audience gate is hard and checked at render time. **Re-encoding is required**: generated PNGs are 2-3 MB, thirty break
   the 64 MB cap. Downscale only, to 300 DPI x the placement's mm, JPEG about q85; a 120-DPI variant for the live preview; measure Chromium's
   memory at 120 images first. SkiaSharp is already in the test output tree; check its licence and the app's dependency before choosing.
6. **Build order**: (1) slots from the compiler, placement CSS, existing `BookArt` placed, with the party canary (no slot for a hidden subject);
   (2) re-encode and downscale; (3) DM upload into a slot through the sanitizer with the tag; (4) style vocabulary and the code prompt assembler
   (golden tests: no proper nouns, negatives present); (5) the art-director call; (6) per-slot generation with allowance and price;
   (7) suggested spot and scene slots. **Not to build**: model-chosen placement inside prose, batch generation without a click, text inside
   images, generated maps (upload only), identity-consistent characters through reference images, per-player `:::for` art.

### 28.4 What stands between this and a real Homebrewery page, other than art

The theme is a single column (no `column-count`; two balanced columns with spanning headers is the main thing); drop caps; the typographic
palette (deep red serif headers with a gold rule, tan note boxes, green-grey read-aloud boxes, zebra tables); a running footer band with the
chapter name; part and title pages; front and back matter; keep-with-next across column breaks. On content: code-owned sidebars, read-aloud and
stat blocks. Without them even good chapters read as a tidied wiki in a nicer font.

## 29. Two columns (done) and what rendering it showed

A chapter's text now runs in two balanced columns (`BookLayout.Columns`, `BookHtmlOptions.Columns`, `?cols=2`, a "Columns" select in the panel, default two).
The chapter title (`.chapter > h1`) and anything marked `.wide` span both; the title page and the contents stay one column. Anything other than `2` is one,
so a hand-typed `cols=7` cannot produce a third. Boxes, tables and pictures keep `break-inside: avoid`. Parchment gains a three-line drop cap on the first
paragraph after a chapter title (`::first-letter`, floated to the start side, so it follows the writing direction); Plain stays plain.

Rendered in the real Chromium (two chapters, a table, a quote, headings) and looked at: the title spans, text flows in two columns across the page break,
the last page balances, the table stays inside its column. The drop cap first spanned two lines and sat badly; it was tuned until it spans three.

Not looked at: Letter, RTL (the theme is English only, so an RTL book cannot reach it yet), a chapter whose first block is not a paragraph (no drop cap
then, by design), a very long table (it cannot be split by a column and is kept whole), narrow-column hyphenation (`hyphens: manual`, so ragged lines
show on long names), a `.secret` box taller than a column, and the per-page footer band with the chapter name (still not built).

## 30. Art step 1: the pictures the campaign already has, placed (done)

What is built (§28.3 order step 1, minus the slot object model, which waits for the pictures that are not there yet):
- `BookCompiler.ArtAnchors(source, art)` lists, beside `Compile` and in its order, where each existing picture goes: after the heading of its cast
  entry (portrait), place (location art), wiki page or homebrew entry, as `(section, heading line, which repeat of that line)`. The repeat index keeps two
  characters called "Twin" apart; a mutant that ignored it survived the first version of the test (it counted pictures instead of checking whose) and is now caught.
- `BookComposer.PlaceArt` runs on the finished chapters, so it works on a rewritten chapter too and **a model never sees a picture**: the stored-build key
  does not change when art does, and a rewrite cannot move or drop one. A rewritten chapter whose heading no longer reads as the entry's name gets no
  picture (silently; a count is returned and unused so far).
- The cover goes on the title page (`BookDocument.CoverAsset`; only a `data:image/` URI without quotes, brackets or spaces is ever written into the page).
- `BookArtReader` (existing, already audience-scoped and sanitising) is registered and used by the preview and the PDF; `pics=0` and a panel checkbox
  leave pictures out. The party canary (`BookArtTests`): a hidden, unknown, retired or masked subject has no picture, no heading and no name in the page,
  and the media store is never asked for it.
- Look: Chromium rendered, looked at. A portrait first floated beside the text; a float that does not fit the end of a column moved to the next column
  **without its heading**, and Chromium ignored `break-after: avoid` before a float. So a portrait is a block (34% wide) that keeps with its heading, and a
  place picture is full column width. The text no longer wraps around a portrait; that is the price of not separating a name from its face.

Not done, in addition to §27's list: chapter-header and spot art (nothing to place yet), re-encoding and downscaling (a 64 MB book in a preview iframe is
heavy, only `pics=0` mitigates it), an Image resolver for the DM's secret boxes, the art slot tray and everything AI about art (§28.3 steps 2-7), pictures
in a hand-written chapter that is not one of the compiler's, and a look at RTL or Letter with pictures.

## 31. Art step 2: re-encoding and downscaling (done)

`BookImageScaler` (Codex.Persistence, SkiaSharp 3.119.4, MIT; `SkiaSharp.NativeAssets.Linux.NoDependencies` so a Linux host needs no fontconfig; Windows and macOS
natives come with SkiaSharp itself) sits between `BookImageSanitizer` and the book. A picture is scaled down, never up, to its box at 300 dpi for the PDF
and 120 dpi for the preview (`BookArtQuality`; the route chooses by `forPrint`). Boxes: cover 140x165 mm (contained), portrait and homebrew 62x52 mm (now 42x56, 3:4: see §39),
place, wiki and location 88x48 mm (filled, so both sides must be covered). Opaque pictures are written as JPEG q85, pictures with any transparency as PNG;
a result that is not smaller than the source is discarded. `BookArtReader.MaxTotalBytes` now counts what is placed, not what was uploaded.

**The security posture, because this is the first decoder in the book path.** Until now no picture was ever decoded by our code (the browser was the only
decoder, with its network closed). Now a native decoder (libjpeg-turbo, libpng, libwebp inside Skia) reads user uploads. What limits that:
- only bytes that passed the structural sanitizer arrive (known container, pixels at most 50 million, metadata gone);
- a picture already small enough (within 15% of its target) is **not decoded at all**;
- whole decodes are limited to 30 megapixels; a larger JPEG is decoded reduced, straight from its blocks; a larger PNG or WebP is kept as it is;
- decoding runs two at a time, off the request thread; any decode failure or exception returns the sanitizer's bytes (a refusal costs bytes, never a picture);
- results are remembered by a hash of the bytes and the target size, in memory, up to 96 MB, then dropped whole.
This does not make a decoder CVE harmless: Skia's decoders are in-process native code, and the sanitizer is not a decoder-level defence. **Correction of an earlier draft of this paragraph, found by review:** it is not only the DM. A player may upload the portrait of their own character
(`MediaStore`: `role == DM || actor.OwnerUserId == userId`), and that file reaches the decoder when the DM builds either edition. A crafted file that crashes the native
decoder takes the whole process down (RavenDB embedded included); the managed `catch` cannot stop a native fault. If that is not acceptable, the alternative is decoding in the printing browser (a canvas), which keeps
the old guarantee at the cost of needing a browser for the preview and a round trip per picture; I did not build that.

Colours are converted to sRGB while decoding, because the re-encoded file carries no profile (a mutant that skipped this is caught by a Display P3 test). Other mutants
killed: scaling pictures that are small enough, writing transparency as JPEG.

Measured (synthetic 2048 x 2048 PNGs of drawn circles, which compress better than real art, so real savings are probably larger): 30 pictures, 22.7 MB sanitized,
3.4 MB after; one went 787 KB to 195 KB at 1654 x 1654. I did **not** measure Chromium's memory or time with many pictures, only the byte sizes.

Known and not fixed: the sanitizer drops a JPEG's EXIF, including its orientation, so a phone photo taken sideways prints sideways (it did before this step too);
a picture with a wide-gamut profile is converted, one with a CMYK profile is whatever Skia makes of it; animated WebP and APNG are **untested**: Skia will most likely write only the first frame
when one is big enough to be scaled (otherwise it is kept whole); the print/preview split means the preview is softer than the PDF by design.

### 31.1 Opus review of §29-31: fixed, and left open

No way was found for a picture, name or heading the party may not see to reach the party book (the party's art comes from the party's masked source; the `asset:` delegate
only reaches its own edition's map; the block class is a constant, the asset id `a<digits>`, the alt text escaped). The review's other findings:

Fixed: placement now ignores fenced code, trailing spaces and `\r`, and takes the picture flag from the audience's own source as well as from the art map (a test hands
the party document the DM's whole map; the first version of that test showed seven pictures where five were right, which is why the flag check exists). A JPEG over the
decode limit was kept unscaled whenever its needed reduction fell between 1/8 and 1/4 (my bug, found by writing the test the reviewer asked for): the reduction is now chosen by asking
for 1/8, 1/4, 1/2 in turn. The decoder wait honours the request's cancellation, the cache is checked again after waiting for a place, and a picture that will not shrink is
remembered as itself. The place box is 172 mm in one column (it printed at half the intended resolution). The cover's height depends on the paper (Letter spilled the title
page onto a second page). `.art` has a 120 mm height limit. Two dead CSS rules are gone. Vacuous test assertions replaced; mutants (fence, trim, flag, the 15% shortcut) now die.

Open, in the order I would take them:
1. **(Fixed in section 32.)** ~~Anchoring is still by heading text.~~ A body line `## Vera` inside an earlier entry (a wiki page "History" with a `## Vera` subsection) still takes Vera's picture,
   and the real heading gets none. Always a picture the party may see, so a misplacement, not a leak. A proper fix needs a per-entry marker emitted by `Compile`.
2. **(Fixed in section 33.)** ~~The decoder runs on player-uploaded portraits, in-process (see the correction above).~~ Options: decode out of process, scale only DM-uploaded media, or decode in the printing browser.
3. **One global decoder queue of two**, shared by all users: one DM's first build of many large pictures delays everyone's preview and PDF, and the PDF slot is held meanwhile.
   Needs a per-user limit or a time budget that falls back to unscaled bytes.
4. **Preview and print can differ**: the 64 MB cap counts scaled bytes and preview files are about six times smaller, so a book of roughly 11-64 MB of print-size art
   shows every picture in the preview and drops some from the PDF ("limit"). The skips are not shown in the panel.
5. Animated WebP/APNG that need scaling lose all but one frame (untested which); the cache's byte count can drift by one entry under a race; the column and drop-cap tests are
   string checks on the CSS, not on a render.

## 32. Placing a picture by position, not by text (done)

`BookCompiler.Compile(..., out entries)` records, as it writes each entry heading, the line of the chapter's Markdown it wrote it on (`BookEntryPosition`: section,
heading, line, kind, owner, sub, alt, placement, and the picture flag from the audience's source). `ArtAnchors` now takes those entries and the art map; the old
re-derivation of the compiler's sort order is gone.

`BookComposer.PlaceArt(document, compiled, entries, art)` places by what is known about each chapter:
- **A chapter still exactly as the compiler wrote it** is placed by the recorded line (and the line must still read as the heading and be outside a code fence). A body
  line equal to a heading, in an earlier entry or in a fence, cannot take the picture: the test has a wiki page "History" with a `## Vera` subsection and a page "Vera" with art.
- **A chapter a model rewrote** is placed only when its heading is unambiguous: exactly one entry in the chapter has that heading text and exactly one non-fenced line reads
  that way. The checks keep only the *set* of heading texts, so a rewrite may reorder, repeat or drop headings; in those cases there is no picture, which is the safe failure.
  Consequence: two characters with one name get their pictures in the compiler's text, and **neither** in a rewritten chapter. A rewritten chapter whose entries are
  renamed also loses its pictures.
- Lines are compared trimmed and written back as they were, because two trailing spaces are a line break in this dialect (a first version trimmed them and would have
  removed hard breaks; a test pins that).

No marker is written into the text: a marker would be seen by the model, could be dropped or edited by it, and would put a pseudo-token into headings, the contents and the
PDF outline. Mutants killed: placing by first text match, accepting ambiguous headings, writing trimmed lines back.

Not done: a rewrite that keeps its headings but moves entries around is placed by heading text and is therefore right only in that each picture follows *its own* heading; a
`## Name` line in the **body** of a rewritten entry that equals an entry heading makes that heading ambiguous, and its picture is left out.

## 33. The decoder runs in its own process (done)

`src/Codex.ImageWorker` is a small executable (references `Codex.Persistence` for SkiaSharp; built and copied beside the server because `Codex.Web` references it; a
`dotnet publish` of the server was checked to contain it, its runtimeconfig and `libSkiaSharp.so`, and a worker run from that publish directory scaled a picture).
`BookImageEngine` (the former private `Scale`) is the only code that calls the native decoder, and it is called only there. The server's `BookImageWorkerPool` starts at most two
workers on first use with the same dotnet host the server runs in (the launcher starts the server with the bundled one), talks to them over stdin/stdout in a length-prefixed
format (`BookImageWire`; sizes validated on both ends), and:
- kills and replaces a worker that exits, does not answer within 30 s, or whose request is cancelled mid-flight (the pipe is mid-message); a hung worker does not outlive its timeout;
- answers "keep the picture as it is" for any failure (it cannot start, died, timed out, spoke nonsense), counted in `Failures`; the scaler remembers that answer, so a file that kills
  a worker costs one worker per server run and not one per request;
- keeps pictures as they are when the worker file is missing (a host that lacks it loses the saving, nothing else);
- is told nothing but bytes and a size, and gets no secrets, ids or network: it has no database and no access to the campaign.

The engine also refuses to scale up on its own account now (a request at or above the picture's own size returns the picture).

What this does and does not buy: a crash, a stack overflow, an out-of-memory kill or a hang in libjpeg/libpng/libwebp/Skia now ends a child process; the server, RavenDB embedded and the
other users' requests continue. It is **not a sandbox**: the worker runs with the server's user and file access, so an attacker who gets code execution in the decoder (not
merely a crash) has what the server's user has. Real isolation would be a container, seccomp or an unprivileged user for the worker; none of that is done. The worker's memory is
not capped either (a decompression bomb inside the 30-megapixel limit can use a few hundred megabytes, twice at a time); the timeout and the kill are the only bounds.

Tests (`BookImageWorkerTests`, with the real worker process and `sh` stand-ins): the real worker scales like the in-process engine and is reused; the engine's "keep" comes back as nothing;
a worker that dies at once, one that never answers, and one that dies on its first start but not later; a cancelled request leaves nothing running; no worker file; a poison picture is not
resent. Mutants killed: failures thrown instead of absorbed, the failure answer not remembered, a hung or cancelled worker not killed (found by the first run: a leak that no assertion
could see, so the pool now reports `Alive`). Not tested: a real native crash on a real malformed file (the stand-ins exit with a crash code but never touch libjpeg), Windows and macOS hosts.

Still open from section 31.1: the shared two-worker queue is per server, not per user, and the PDF slot is still held while art is read; preview and print can differ near the size cap.

### 33.1 The website (done)

The guide has a section "Print your campaign as a book" (with the optional AI step as a subsection), the home page tour has a row for it, and the install page's troubleshooting
names the missing-browser case and `CODEX_BOOK_BROWSER`; all in English, Russian and Hebrew. The Russian and Hebrew text is mine and needs a native read. Two new screenshots, `book-panel`
and `book-pages`, are captured by `site/tools/capture-screenshots.mjs` from the real app (English only: the themes print English, so the other languages fall back to the English shot).
The capture uploads a cover and three portraits through the app's own upload controls, prints the party edition through `/book/party/pdf` (so the worker process and the printing
browser both ran), and `site/tools/book-pages.py` lays cover, Cast and a content page side by side. The pictures are **stand-ins drawn by `site/tools/demo-art.py`** (flat shapes made
with Pillow, not art); the screenshots show how pictures are placed, not what good art looks like. A render of that capture found the drop cap landing on the italic tagline paragraph;
the theme now skips a paragraph that is all italic. Not shown on the site: the AI build (no model is configured in the capture), the one-column layout, Letter paper, the GM edition.

## 34. Art step 3: slots, and the GM's own pictures in them (done)

The slot model that section 30 put off. `BookSlots.For(source, compiled, entries)` (Persistence) lists an edition's slots: the cover, a header per chapter,
and one per entry the compiler recorded. All of them come from that edition's own compile, so a hidden subject has no slot in the party book. A slot's
key is `s` + 24 hex digits of SHA-256 over the subject *as this audience sees it*. The compiler now stamps every `BookEntryPosition` with its `Subject`
and `Audience`. For an actor, the subject is `actor:<id>`, except in the party edition when the party knows the actor only as a stranger: then it is
`mask:<id>`.

**Deviation from 28.3, item 4:** the mask is keyed by the actor's id with a `mask:` prefix, not by the mask's content.
- The prefix is enough to keep the GM's picture of the real character apart from the party's picture of the stranger.
- Keying by id also keeps two strangers with the same public name apart.
- When the GM reveals the character, the key changes, so the stranger's picture does not follow it onto the real character's entry. That is correct.

**Storage.** `bookart/{campaign}` (`BookArtDocument`, `RavenBookArtRepository`) holds one document per campaign, not per GM, because pictures are part of
the campaign. Each picture is an attachment named `book-<edition>-<key>` and tagged `BuiltFrom: party|dm` with the edition whose tray it was put in.
- Reads and writes re-check the caller's GM role.
- Writes use optimistic concurrency.
- There are at most 600 pictures.
- Deleting the campaign deletes the document.
- The backup copies the pictures; the campaign export leaves them out.
- No media route serves the document: the routes ask for fixed names (`cover`, `portrait`, `art`), and a test checks this.

**The gate has three layers, and a mutation test showed each one is caught alone:**
- `BookSlots.Choose` gives the party edition only party pictures. The GM edition gets its own picture, or the party's where it has none.
- `BookArtReader` refuses a GM picture handed to it for a party source.
- `BookArt.Slot(key, audience)` gives a party entry nothing made for the GM, even from a map read for the GM.

On upload, `BookArtService` rebuilds the slots from the edition's source and refuses a key that is not among them. It does not trust a key posted from
the page. A picture in a slot replaces the subject's own picture in the book only; that picture is then not read at all. A chapter header is placed
after the title line, which a rewrite also has because code writes it. A chapter that does not start with its title gets no header. The scaler's
header box is 172 x 62 mm. The Parchment drop cap also finds `h1 + .art-header + p`. A Chromium render showed the header spanning both columns and the
drop cap in place.

**The tray.** `BookArtTray.razor`, under "Pictures for this book" in the panel, lists the cover, each chapter's header and its entries. Each row shows
its state: your picture, the party edition's, its own picture, or none. Each row has upload and remove buttons and a status line read out to screen
readers. Entries with the same name, such as two strangers, are numbered "(1)", "(2)" in the tray only; the numbers come from that edition's labels.
The e2e test `TheGmPutsAPictureInTheCoverSlot...` drives the real tray in the real app:
- it uploads to the party cover and finds it on the party page;
- the GM tray then shows it as the party edition's picture;
- removing it takes it out of the page;
- the file input takes keyboard focus, and its label draws a focus ring.

Mutants caught: GM pictures accepted for a party slot in `Choose`, in the reader and in the lookup; the mask key replaced by the actor key; the
service's slot check skipped; the header placed without the title check.

**Not done:**
- No thumbnails in the tray; it shows each slot's state only.
- There is no picture for a slot that no longer exists (it stays stored and the tray does not show it).
- The Russian and Hebrew strings are written by the model.
- Restoring a backup does not bring book pictures back. No backup image comes back automatically; a backup only copies them.

## 35. Art step 4: the style vocabulary and the code-built prompt (done)

`Codex.Core/Book/BookArtStyle.cs`:
- `BookArtStyles` holds the vocabulary: seven media, six palettes, three line weights and five lights, plus framing per slot kind and a fixed negative
  suffix.
  - The media are painterly (the existing house phrase), oil, watercolour, ink linework, woodcut, engraving and parchment sketch.
  - The framing covers cover, header, portrait, homebrew, location, wiki, spot and scene.
  - The negative suffix says: no text, letters or numbers, no signature, watermark, frame or border, not a photograph, no modern objects, no real
    people.
- `BookImagePrompt.Assemble(slotKind, visual, style, names)` builds the prompt from framing, visual, the four style phrases and the negative suffix.
  - It takes no subject name, and a test pins its signature.
  - The visual is the only free text. It is refused, not repaired, when it has any of: a capitalised word that does not start a sentence (an all-caps
    word counts), one of the campaign's names in any case, a real artist from a deny-list, "in the style of" and similar phrases, a link, empty text,
    or more than 400 characters.
  - An unknown style id falls back to the default, so a style saved from an older vocabulary still draws.

The style is not stored or chosen anywhere yet; it arrives with drawing (step 6). `AiPrompts.Image`, the entity art path, is unchanged.

**Limits:**
- The capital check works for Latin and Cyrillic scripts. Hebrew has no capitals, so a Hebrew visual is checked only against the names list.
- A German visual is refused, because German capitalises nouns.
- The art director (step 5) therefore has to write visuals in English, whatever the book's language.

## 36. Art step 5: the art director (done; nothing is drawn yet)

`BookArtDirector` (Core/Book/Ai) asks a model, one chapter at a time, what each picture slot should show. `BookArtDirection` (Web) plans, prices and
runs the requests. The suggestions show in the tray under each slot.

**What the model is shown.** `BookArtOutline` builds one outline per chapter: each slot's heading, plus an excerpt of at most 700 characters from its
entry as that edition reads it. The cover is asked about with the overview.
- The outline is cut from the chapter after `BookWork.Prepare` has run on the whole chapter, which removes secrets and spells names out.
- Before that, a marker line replaces each recorded heading line, and the prepared text is then split on those markers.
- A GM block that is never closed runs to the end of the chapter, so it swallows the next entry's marker. That entry is then sent with its heading
  and no text. The test seeds a biography with an unclosed `:::dm` and checks that the next entry's text is not sent.

Each request covers at most 16 slots (20 before section 39), numbered 1..N within that request. The model never sees a slot key. The names list used for checks is never sent.

**What is kept from the reply.**
- The reply must be JSON `{slots:[{slot, depicts, visual, mood}]}`. A code fence or a preface is tolerated: everything from the first `{` to the last
  `}` is read. The reply is capped at 64 KB, with a JSON depth of 8.
- A slot number that was not sent is ignored. A number given twice drops both entries. A reply with more entries than slots drops everything.
- Each field is checked on its own, and a slot that fails is dropped alone:
  - `visual` (English) goes through `BookImagePrompt.Check` against the edition's names. A stranger's mask is a description, not a name, so it is not
    on the list.
  - `mood` must be an id from `BookArtStyles.Moods` (ten of them); it maps to a fixed phrase.
  - `depicts` is for the GM's eyes only, in the book's language. It is checked with `BookChecks.StyleProblems` against that slot's own text: no
    invented names or numbers, no markup, at most 160 characters.
- The request is asked again only when the reply cannot be read or nothing in it passed, and only once, with the reasons.

`BookImagePrompt.Assemble` now takes the mood id, and the signature test was updated on purpose.

**Tags, the new invariant 10.** A suggestion carries the tag of the edition whose source was read. The repository is given only that tag. Each
tray shows only its own edition's suggestions. The GM's outline of Cast includes hidden characters, so a GM suggestion for Vera's slot (which has the
same key in both editions) can describe what the party may not know. Anything drawn from a suggestion in step 6 must inherit the suggestion's tag,
not the tag of the tray it was clicked in.

**Money and runs.**
- The pre-flight is `BookPreflight.Estimate` over the requests, and the panel shows it: about N tokens, never more than the cap, and a cost range or
  "no price".
- The cap is fixed: every request twice at the high estimate, plus room for two in flight. The budget enforces it, and the GM does not set it.
- The server allowance is reserved up front for requests × 2.
- A refused key, a rate limit or the allowance stops the requests not yet sent.
- The spend is added once to `BookArtDocument`'s director totals.
- A refused suggestion write (the user is no longer GM) cancels the run.
- `BookGate` is now one per-GM gate shared by chapter builds, discards and director runs, so they cannot overlap; a test checks both directions.

**Staleness.** Each suggestion stores a hash of what was sent for its slot. A slot is asked about again only when that hash changes, unless "Suggest
again" is ticked. Editing one biography re-asks that slot and at most its chapter header, not the whole of Cast. The tray marks an old suggestion as
"the text has changed".

Mutants caught: sending the outline without `Prepare`; accepting a slot number that was not sent; skipping the visual check; accepting a free-text
mood; tagging by something other than the source read; a gate of its own; a tray that shows the other edition's suggestions.

**Not done:**
- No real model has been run on this; only scripted replies.
- The prompt has not been tuned.
- The GM cannot edit a suggestion yet (step 6).
- The tray shows the mood id in English.
- There is no e2e test of the director button.
- Russian and Hebrew strings are written by the model.
- `depicts` in Hebrew gets only the number check, because the name check relies on capitals.

Fixed after review:
- A party wiki body with `[[Some GM page]]` sent that page's title in the party outline, and the `depicts` check let it through because the
  text was in the slot's own text. Fixed in section 37.
- The mood is matched in any case, and the vocabulary's id is stored.
- A quoted slot number (`"1"`) is read.
- A slot number that was not sent is listed in the reasons given for the retry.
- A save drops the edition's suggestions for slots that are gone, so the list stays bounded.

## 37. Wiki links in the party edition (done)

A `[[Page]]` link printed its target's title in every edition. In the party edition that named GM-only pages: in the printed book, in the chapter
text a model is sent, and in the art director's outline, where the `depicts` check could not catch it because the title was in the slot's own
text.

The compiler now rewrites page links in every free-text paragraph, per edition (`BookCompiler.PageLinks`):
- The GM edition keeps every link as written.
- The party edition keeps a link whose page is in the party source, found the way the wiki finds it (`WikiLinks.TargetOfTitle`: by slug or by
  title, any case).
- Any other link prints its `|shown` words as escaped plain text, or nothing when it has none (an empty label counts as none). The title is never
  printed.
- Anything either reader takes for a stored mention is left to the mention resolvers, which fail closed. `BookWork.Prepare` reads a mention in
  any case and with spaces before the colon, so the check here does too. A first version matched only lowercase `actor:` and printed the
  label of `[[Actor:x|The Lich King]]` for a hidden actor; `BookBuildsTests.EveryWayOfWritingAMention_IsClosedToTheModel_WhenItsActorIsHidden`
  caught it.

The rewrite is done once, in the compiler, because the book, `BookWork.Prepare` and `BookArtOutline` all read the compiled text. The pattern
matches anything the renderer reads as `[[…]]`, and longer ones too.

Side effects:
- The party source cannot tell a page the party may not see from one that was never written, so a link to an unwritten page also prints
  nothing in the party edition.
- A party chapter whose text changed this way gets a new build key, so a stored rewrite that carried the title is no longer printed.

Test: `BookSourceTests.PartyEdition_NeverPrintsTheTitleOfAPageThePartyMayNotOpen_ThroughALink` covers the title, the slug, another case, an empty
label and a real label, in the book, the model requests and the outline, and checks that the GM edition keeps links, including one to an
unwritten page. It also has mentions in the same paragraph: a known actor, a stranger shown by their mask, and a hidden actor written
`[[Actor:…|label]]`. Mutants caught: no rewrite; an unlabelled link keeping its title; the label dropped; a visible link dropped; the GM edition
rewritten; mentions not skipped; mentions matched in lowercase only.

The app's own wiki had the same leak: `WikiRender` showed a link a player may not open as its text (`missingAsText`), and for an unlabelled
link that text is the hidden page's title. Fixed with the same rule in `WikiText.ForReader`, used by `WikiRender.ToHtml` and
`WikiRender.Summary` for every non-GM reader. That covers page bodies, the GM's "view as players", lore details, map descriptions, the wiki
list's cards, link previews (`PreviewEndpoints`) and a stranger's line in the cast list. The cast list loads no pages, so there a page link
always shows only its label. The app rule also drops labelled or other-case text in a mention's form (`[[actor:x|Name]]`), which no reader
names. Tests: `WikiPlayerLinksTests`, mutation-checked (eight mutants).

Still not covered: recaps and notes go through `MentionLinks`/`TextFormatting`, which do not read page links at all. A `[[Page]]` typed there
prints literally, brackets included, to everyone. Those editors do not offer page links.

## 38. Art step 6: drawing the suggested pictures (done)

The GM draws pictures from the art director's suggestions, one ticked place at a time, in the book's one style. Nothing is drawn without a
click, and the cost (or the server's allowance) is shown before the click.

**Where it lives:**
- `Web/Book/BookArtDrawing.cs`: preview and run.
- `Web/Book/BookImages.cs`: `IBookImages`, the picture model for one run.
- `AiProviderService`: `ImagePriceFor`, `ReserveImagesAsync`, and a `GenerateImageAsync` overload that draws from a reservation.
- `BookArtStore.cs`: style, drawing totals, and what a drawn picture was drawn from.
- The tray: style pickers, a Draw tick and an editable description per suggestion, the draw line, progress and Stop.
- AI settings: a price per picture for image models, and `AIImage:PricePerImage` / `AIImage:PriceLabel` for the server's key.

**The rules:**
- **The server checks every pick again when the run starts.** The client sends only slot keys and descriptions. For each pick:
  - the slot must be one this edition's source makes now;
  - the suggestion must be this edition's (`BuiltFrom` equal to the edition's tag);
  - the suggestion must have been made from the slot's current text, so one stored before section 37's fix is "stale" and cannot be drawn;
  - the description, edited or not, must pass `BookImagePrompt.Check` against the edition's names.

  A pick that fails is listed with its reason ("slot", "suggestion", "stale", "visual", "repeat", "many") and is not drawn.
- **The prompt is the code's.** `BookImagePrompt.Assemble` combines:
  - the framing for the slot's kind;
  - the description;
  - the suggestion's mood (from the vocabulary);
  - the book's style;
  - the fixed negatives.

  The header's framing now keeps the subject in a band, since its 172×62 mm box crops a wide picture.
- **The tag is the edition's**, the one the suggestion was checked against. The request carries no tag.
- **Shapes:** cover, portrait and homebrew are Tall; spot is Square; everything else is Wide.
- **Money and allowance:**
  - On the server's key the run sets aside N pictures, all or nothing.
  - Each picture drawn spends one, never counted again (`GenerateImageAsync` with the reservation never falls back to `Take`).
  - The handle refuses a draw past N.
  - What was not drawn goes back.
  - The pre-flight shows N × the price per picture when one is set; on the server's key, the pictures left today; otherwise "no price set".
- **Pictures:**
  - Drawn one at a time.
  - Never retried, since a lost reply may still have been paid for.
  - A refused key or model, or a rate limit, stops the rest.
  - The returned bytes go through `BookImageSanitizer` like an upload; a refused picture is counted as drawn but not stored.
- **Storing:**
  - A drawn picture replaces the slot's picture for that edition; the pre-flight names every place where it would replace one.
  - It is stored with origin "drawn", its description, and a hash of kind, description, mood and style.
  - The tray says "drawn from an older description or style" when the slot's text, the mood or the style changed since. The picture is kept.
- **One paid run per GM:** a run takes the shared `BookGate`.
- **At most 12 pictures a run.**
- **A tick is dropped when its suggestion changes.** The tray keeps, beside each tick, the suggestion it was ticked from, and drops the tick
  when that suggestion is gone, out of date or replaced. Otherwise an old description could be sent under a new suggestion; the server
  would accept it as the GM's own edit.
- **OpenRouter picture errors keep their HTTP status**, as an inner exception, so a refused key or spent credit stops the run. Before
  this they were read as an unknown failure, and the next picture was tried.

**Tests:**
- `BookImagesTests`: a run counts each picture once, cannot draw past N, gives back the rest, and is refused whole when it does not fit.
- `BookArtDrawingTests`: the code's prompt and the edition's tag; refusals for the other edition's slot or suggestion, a stale suggestion and a
  named description; a refused picture and a refused key; the gate.
- The e2e `TheGmTicksASuggestedPlace_SeesThePriceFirst_AndThePartyBookPrintsThePictureDrawn`: through the real settings page and tray, with a
  fake text and picture server in the test process.

Mutants caught: counted twice; no cap; wrong tag; stale drawn; other edition's suggestion drawn; edited description ignored; no gate; no stop
on a refused key; foreign slot accepted.

**Not done:**
- **No real picture model has been run on this.** Only a fake server has.
- **Regenerate and Accept from up to three versions (28.3 item 3) are not built.** Drawing replaces the slot's picture, and the earlier one,
  paid for, is gone; the pre-flight says which places would be replaced.
- The tray's tick dropping is not covered by an automated test. The server's checks, which are covered, are the guard that counts.
- `WikiSecretsE2ETests` failed once in a run of 759 tests during this step, cause not captured, and passed on two reruns (alone, and with the
  28 e2e tests). It failed again in the full run of section 39, and that time the cause was read: see section 39.
- A description check failure is shown in English.
- The mood is shown as its English id.
- The drawing totals are lost if the process dies mid-run, like the director's.
- Russian and Hebrew strings are written by the model.
- Style names are translated in the tray, but the prompt is always English, on purpose.


## 39. First real run of the art director and drawing, and what it changed

The first real run of steps 5 and 6 used a small Anthropic text model and a fast picture model through OpenRouter. It ran in the preview app with a
throwaway database. The campaign had one chapter (Cast) and two characters with short biographies, in the party edition and the default style.
The GM entered both keys. Pictures drawn: three (the cover, the Cast header, one portrait). Text: about 5,100 tokens in and 4,500 out over seven
requests, counting the failed ones and the reruns after each fix.

**What it found, and what changed:**
- **The director's replies were cut off.** The output limit was 160 tokens per place. The model's JSON for three places stopped mid-string, twice,
  and the tray only said "0 of 1 requests gave suggestions". The model then used about 370 tokens per place. Now:
  - The limit is 480 per place (`BookArtDirector.OutputPerSlot`). A request now asks about at most 16 places (`MaxSlotsPerCall`, from the
    largest output limit), not 20: twenty would have had about 400 tokens apiece.
  - A reply that stops at the limit (the provider says so, or it used the whole limit) is asked again, told to be brief (`BookArtDirector.CutOff`).
  - When it still fails, the reason is `cutoff`, not `unreadable`.
- **A chapter header's "depicts" was refused for naming the chapter's people.** The header's own text is only its title, so "Vera Holloway" was
  "a name the book does not hold". The names are now checked against the whole request (the chapter and every place in it). That text is the
  edition's own, and "depicts" is only shown to the GM. A name in none of it is still refused.
- **Every drawn picture showed "drawn from an older description or style" at once.** The stored description was cut at 200 characters (a label's
  length), and the tray hashed the cut text. The model's descriptions run to 300-400 characters. It is now kept up to `BookImagePrompt.MaxVisualChars`.
  The three pictures of this run keep the old, cut text, so they stay marked until they are drawn again.
- **A portrait lost its head.** The portrait box was 62x52 mm (wider than tall), cropped from the middle, and portraits are tall. On screen
  it was 335x197 px. Now:
  - The box is 3:4, at most 42 mm wide (34% of a column), cropped from 20% down (`.art-portrait`, `BookImageScaler.BoxFor`).
  - This applies to uploaded portraits too (same box; not looked at with a real upload).
- **The tray said why a request gave nothing only as a count.** It now lists each request's reason (cut off, unreadable, the checks, budget, key,
  rate limit, stopped). It also shows "Some results could not be stored" for both runs, which it never did before.
- **The server log** now has one line per director request whose replies were refused: the checks' own words, never the reply.
- **Plurals:** "1 requests" is gone. The director's summary, progress and the token meter use plural forms (en, ru, he).

**What went right:**
- No name reached a picture prompt.
- The pre-flight's estimates were close.
- The cover and the portrait were good.
- Drawing stopped at three, and the tray said "drew 3 of 3".
- The pictures were stored under the party tag and printed in the party book.

**Not changed, worth knowing:**
- In the header the model drew a figure whose head touches the top edge, despite the framing's "nothing important near the top or bottom
  edge". The crop is from the middle.
- The cover's "depicts" was vague ("a coastline scene named by the chapter title"). It is only for the GM, and the visual was good.
- With no price set for either model, no cost was shown anywhere. That is by design, but a GM may not notice that no price was set.

**Tests:**
- `BookArtDirectorReplyTests.AReplyCutOffAtTheLimit_IsAskedAgainMoreBriefly_AndSaysSoWhenItGivesUp` and `AHeaderMayNameThePeopleOfItsChapter`.
- `BookArtDrawingTests.APictureDrawnFromALongVisual_IsCurrentInTheTray_UntilTheStyleChanges`.
- The scaler test was updated to the new box.
- Mutants caught: cut-off not detected; the 200-character clip.

**`WikiSecretsE2ETests`, the failure of section 38, explained (likely).** In the full run (1,647 tests) it failed again: both secrets were shown to the
player, but only one of the two was marked new (count 1, not 0: a "seen" time between the two reveals, as below). A player's "last seen" time for a wiki page was stamped when the page's first interactive render ran its script,
not when the page was read. A reveal made in that gap (the test reveals right after the player opens the page, and the full suite is slow) was counted
as seen, though it was never shown. That is a real bug, if a small one. `WikiSection` now stamps the time the page was read (`loadedAt`). It has not
been reproduced on demand; the wiki tests then passed three runs in a row. It has nothing to do with section 37.

Full run after these changes: 1,646 of 1,647 passed, the one failure being the test above, before its fix.

## 40. Spike: Homebrewery's engine with an editor and AI editing (nothing in the app; for the GM to decide)

The question was whether to reuse Homebrewery's system: Markdown and CSS on the left, a live preview on the right, AI editing in a dialog, and an
export that prints a PDF looking like the preview. The spike lives in a session scratchpad, not in the repo. It is a bundled `marked-hbfm` with a
small renderer, an editor page and a print page, run on the test campaign of section 39 with its three drawn pictures.

**What was found:**
- **The renderer is a separate MIT package**, `marked-hbfm` 1.0.3 (Homebrewery Flavored Markdown on `marked` 15). It bundles for the browser
  (about 350 KB, from its ES module entry) and runs in Node too. The preview is the same as Homebrewery's: each `\page` becomes a fixed-size
  `.page` div with a `.columnWrapper`, styled by the theme CSS and the GM's CSS. Its PDF is the browser printing that page, so the PDF matches
  the preview. This was shown with Chrome to Chrome, for normal and long text, in Letter and A4.
- **Grimdark works on Blank's layout alone.** The PHB theme is not needed (its look copies the official 5e book, and its pictures and fonts
  have no clear licence). Blank's `style.less` still imports the 5e fonts and `assets.less`; only its layout CSS is reusable, and each font and
  picture needs its own licence check.
- **A4 works.** The pages reflow; the ribbon and the torn chapter art held at A4.
- **Size:** grimdark printed 17.8 MB for 4 pages (the full-page stains texture, as section 13 measured); the PHB theme was 1 MB.

**Findings that shape a design:**
1. **Markdown and theme are coupled.** The same Markdown under the PHB theme was a mess (full-size pictures over the text). Grimdark places every
   `.page img` absolutely, so a portrait on an overflow page covered the text and one on a chapter page joined the banner. A theme system on this
   engine needs a contract: the generator emits a fixed set of blocks (chapter art, portrait, place picture, note, read-aloud, stat block,
   table), and every theme styles each one. A rule for every image breaks that.
2. **Homebrewery never breaks a page by itself;** text past a page is clipped. The spike's fit pass moved spilled blocks onto new pages, and
   worked. But a block taller than a page stays clipped, without a warning, and moving rendered blocks means the stored Markdown is not what
   prints. Better: a "Fit pages" command that writes `\page` and `\column` into the Markdown, an overflow marker in the preview, and a warning or
   refusal at print time.
3. **The preview and the PDF come from different browsers in the app.** The preview runs in the GM's browser (maybe Safari or Firefox); the PDF in
   the server's Chromium. Line breaks can differ, so a page that fits in the preview may overflow in the PDF. The overflow check must also run in
   the print browser and report what spilled. Untested.
4. **An edited brew stops following the campaign.** That is the product decision. These do not carry over to a hand-edited copy: the stored
   build and its staleness, drawn pictures' inputs, "the party book prints only party-tagged pictures", and the no-secrets guarantee. Options:
   (a) content stays generated live, and the GM edits the theme, CSS and per-block options; (b) a generated brew the GM edits freely, with a
   per-chapter "changed since you edited" notice and a regenerate button.
5. **AI editing**, either way: it must not send `:::dm` blocks unmasked; it should return a patch to a selected range, shown as a diff to accept;
   and it should show the cost first, like the rest of the AI layer. Not tried against a real model.
6. **Security:** the print path has JavaScript off today. This needs either JS on in the print browser, limited by CSP to our own bundled script,
   with no inline scripts and the network closed; or HTML rendered and cleaned on the server before printing. The editor preview must stay in a
   sandboxed iframe with `default-src 'none'`, which blocks grimdark's Google Fonts import: the fonts must be bundled for the preview too.

## 41. The editable book: design (chosen by the GM 2026-11-05; phases 1 to 6 built, see 41.9, 41.10, 41.15, 41.16, 41.18 and 41.19; layout 41.17)

The GM chose an editable copy: the book becomes Markdown, generated from the campaign, that the GM edits freely, with AI help. Each chapter says
when the campaign has changed since it was edited, with Regenerate and "Bring up to date with AI". The engine is Homebrewery's (section 40).

### 41.1 What is stored
- **A draft** per campaign, edition (party or GM) and language: the theme, the GM's CSS, and an ordered list of chapters. Changing language
  means a new draft, because chapter titles are translated. Size-capped like the art store; saved with RavenDB optimistic concurrency, so a second
  tab cannot silently overwrite the first.
- **A chapter** is its own record and is edited one at a time; the preview shows the whole book. It holds:
  - the GM's Markdown;
  - **the base**: the generated Markdown the GM's edits started from (the text, not only a hash: a three-way merge needs it);
  - the base's inputs key (as `BookBuildKey` does for rewrites), to tell whether the campaign changed;
  - the previous version, so a Regenerate or an accepted merge can be undone.
- **Entries carry stable ids.** The generator puts the entry's id on each `##` entry (a Cast member, a place, a page), so the stale notice and
  merges work per entry, not per chapter. Handoff §7 item 2 already found chapter units too coarse.

### 41.2 The generator and the theme contract
- The generator writes Homebrewery-flavoured Markdown from the same masked source the book reads today (`BookSourceReader`, `BookComposer`),
  with polished text from "Polish with AI" as an optional input, and the picture tray's pictures.
- It uses only a fixed set of blocks, and **every theme must style each one**: chapter opening (title, chapter art), entry, portrait, place
  picture, note, read-aloud, secret (GM draft only), stat block, table, cover, contents. Section 40 showed why: the same Markdown under another
  theme's rules was a mess, and grimdark's rule for every picture put a portrait over the text. A theme that styles only some blocks is not a theme.
- **Pictures go in only as `art:` references** (`![](art:<slot>)`), resolved when the page is drawn, and only to pictures tagged for the draft's
  edition. No outside links or `data:` pictures in a draft. The GM can still pick any of the edition's slots or upload into one.
- Themes: Plain first (on Blank's layout CSS, without its 5e fonts and pictures), then Ashen Vellum (the grimdark sheet, fonts bundled, stains as
  a small tile, positions as variables), then Parchment. No theme copies a published book's look.

### 41.3 When the campaign changes
For each chapter (and inside it, each entry), the base's key is compared with the key recomputed now.
- **A chapter the GM never edited** (its Markdown equals its base) is simply regenerated: one click, or automatically, with no AI.
- **An edited chapter** shows "The campaign changed since you edited this", with:
  - **Regenerate**: replaces it with the new generated text; the old one is kept as the previous version.
  - **Bring up to date**: a three-way merge of base, GM's text and new generated text, **block by block, by the code first**:
    - a block the GM did not touch takes the new generated text;
    - a block the GM edited, whose source did not change, keeps the GM's text;
    - only a block where both changed goes to the model, with the base, the GM's text and the new text, and returns one merged block.
    The pre-flight shows the cost before anything is sent; with no conflicts there is no AI call at all.
  - **Keep mine**: dismisses the notice until the campaign changes again.
- **Page breaks are layout, not edits.** `\page` and `\column` lines (from "Fit pages" or typed) are left out of edit detection and out of the
  merge, and pages are fitted again after a merge. Otherwise every fitted chapter would look edited.

### 41.4 The review overlay (for merges and for AI edits)
- Shown on the Markdown, one change at a time: inside prose, word-level red strikethrough for what goes and green for what comes; for a table or
  stat block, the whole block replaced (word diffs on table syntax are unreadable). Accept or reject each change, or accept all. The preview shows
  the result as accepted so far.
- **AI edits** use the same overlay: the GM selects a passage (or a block, or a chapter) and asks ("make this a read-aloud box", "shorter",
  "in the voice of a ship's log"). The model returns a replacement for that range only; nothing changes until it is accepted.
- **Secrets:** in the GM draft every `{{secret` block (drafts never use `:::dm`, invariant 12) is swapped for a placeholder before sending and put back after; the model never sees a secret
  and cannot change one. In the party draft the model sees only the party draft and the party-masked source.

### 41.5 What changes for the party guarantee
Once the GM can type freely, "the party book never shows a secret" is no longer something the code can guarantee: the GM can paste one in. What
the code still guarantees: the party draft is generated from the party source only; its pictures resolve only to party-tagged pictures; its AI calls
see only party text. What it adds: **before export, the party draft is checked for GM-only text** (secret blocks' text, hidden characters' names,
titles of GM-only pages) and the GM is warned, with each hit shown. A warning, not a refusal: it is the GM's book.

### 41.6 Printing
- The renderer (`marked-hbfm`, bundled) runs in the GM's browser for the preview, in a sandboxed iframe with `default-src 'none'` and only bundled
  fonts; and in the print browser for the PDF, with only that script allowed by CSP and the network still closed. Its HTML is cleaned in both.
- **The fit check runs at print time too**, because the preview and the PDF come from different browsers (the GM's may be Safari or Firefox). A
  block that does not fit, even on its own page, is reported before the PDF is given out, never silently clipped. "Fit pages" in the editor
  writes `\page`/`\column` into the Markdown, so what is stored is what prints.

### 41.7 What happens to the current book
The HTML route, "Polish with AI" and the picture tray become inputs to the generator. The current route stays only until the draft reaches parity,
so that two book paths do not drift apart.

### 41.8 Phases
1. **The draft and the editor:** draft store, generator with the block contract, editor dialog (CodeMirror with Markdown and CSS, sandboxed live
   preview), the Plain theme, export through the print browser, the per-chapter stale notice with Regenerate (and silent update of unedited chapters).
2. **Ashen Vellum:** the grimdark port; fonts bundled after a licence check of each file; theme picker.
3. **Fit pages** and the print-time overflow report. Built (41.15).
4. **AI edits** on a selection, with the review overlay. Built (41.16).
5. **Bring up to date:** the block merge, AI only for conflicts, the same overlay. Built (41.18).
6. **The party leak check** before export. Built (41.19).

### 41.9 Phase 1: built

The GM asked for `marked-hbfm` as planned (not the app's own renderer, which was offered as the safer option). What exists:

- **Store** (`Persistence/Book/BookDraftStore.cs`):
  - One `BookDraftDocument` per campaign, edition and language: `bookdrafts/{campaign}/{edition}/{language}`, holding theme, paper, GM CSS and
    chapter order. Each chapter is its own `BookDraftChapter` (`.../chapters/{section}`) with the GM's Markdown, the base, the base's key, the
    previous version (Markdown, base and key, so an undo brings the notice back), "keep mine", and a version number.
  - A save names the version it was made from; any other is refused (`Conflict`), and optimistic concurrency covers the gap between load and save.
  - Edition, language (`en`, `ru`, `he`) and section are checked before they reach an id. Caps: 400,000 characters a chapter, 512,000 of CSS (20,000 before 41.11),
    40 chapters.
  - `FollowAsync`: a chapter whose text is still its base takes the new generated text silently; a new chapter is added in place; a chapter the
    campaign no longer has is removed if never edited. `\page`/`\column` lines are not edits (`BookDraftText.Content`).
  - Deleted with the campaign; written to the daily backup (`book-drafts.json`); left out of the export, like the book art.
- **Generator** (`Web/Book/BookDraftWriter.cs`): from `BookComposer.Compose` of the edition's masked source.
  - Blocks: `{{cover`, `{{toc`, `{{chapter,#ch-ID}}`, `{{chapter-art`, `{{entry,#e-SLOT}}`, `{{portrait`, `{{place`, `{{secret` (GM draft only).
    (Phase 2: `{{frontCover}}` with `{{cover-art`, `{{toc,wide`, and `{{chapter,#ch-ID` around the title; 41.10.)
  - **Entry ids are slot keys**: a stranger in the party draft is `mask:`-keyed, so not even the hidden actor's id is in the party's text.
  - **Secrets are `{{secret` blocks, never `:::dm`**: `marked-hbfm` reads `:::` as a definition list and would print the secret as text.
  - The campaign's free text: mentions become the edition's names (never the typed label); wiki links become their words; pictures in free text
    become their captions (a loss against the current book, which resolves `asset:` pictures in wiki bodies); `<` and `&` are escaped.
  - Key: a hash of the generator version and the generated text. Bumping `BookDraftWriter.Version` makes every chapter "moved on"; unedited
    ones follow silently.
- **Renderer** (`tools/book-editor/src/render.js`, built into `wwwroot/lib/book/book-render.js` and `book-editor.js`, output committed,
  licences in `THIRD-PARTY-NOTICES.txt`):
  - `marked-hbfm` 1.0.3 pages (`\page` splits, Homebrewery's filler column).
  - The **cleaner**: parsed inert in a `<template>`, then a tag and attribute allowlist. Links only to `#ids`. No style with `url(`, image
    functions or escapes. A picture only as `art:<slot key>`, resolved to a plain data image from the edition's map; any other picture is removed.
  - **Fitting at draw time**: blocks that spill go onto a continuation page; each fragment of a split block is measured. A block too tall even
    for a page of its own is reported (preview line, `X-Book-Overflow` header on the PDF, a log line), not silently lost.
  - **Contents page numbers** read from the laid-out pages.
  - **`$[...]` maths is Homebrewery's, without `expr-eval`.** `marked-hbfm`'s variables extension evaluates it with `expr-eval`, whose
    every version has critical advisories (code execution, prototype pollution; `npm audit`: no fix). The build aliases it to
    `src/safe-math.js` (41.13): numbers, `+ - * / ^`, brackets and a fixed list of functions, nothing that reads a member or a name that is
    not a listed function; and the build fails if `expr-eval` gets into a bundle another way. `test.mjs` runs the shipped bundle with hostile
    expressions.
  - **A trust boundary:** the editor runs `marked-hbfm` in the app's own page, on a draft that holds text players wrote (a character's
    biography reaches Cast). Only cleaned output enters the frame, but a parser bug in `marked` or its extensions would run in the GM's
    signed-in session. The print path is contained (a separate browser, every request refused, printed with script off). The proper fix is to
    render in a frame with script and an opaque origin (`sandbox="allow-scripts"`, no same-origin), passing strings in and out. Not done.
  - This replaces phase 3's "Fit pages" command for now: what prints is a pure function of the stored Markdown, but it is not the stored
    Markdown. Phase 3 can still write the breaks into the text.
- **Printing** (`BookPdf`, `BookPrintLayout`):
  - The print browser opens the shell in a page with script on and every request refused, evaluates the bundled renderer (the shell's CSP
    forbids every script element; DevTools evaluation is not governed by it), and lays the book out.
  - The resulting page is then printed in a page with script off, as every book is.
  - Tests (`BookDraftPdfTests`, real Chrome): nothing fetched; hostile markup neither runs nor fetches; long text flows and the contents line
    says where; a too-tall block is reported; a picture resolves only to its slot.
- **Plain theme** (`Core/Book/BookDraftHtml.cs`, `BookDraftThemes.Plain`): our own CSS for Homebrewery's page structure and every block of the
  contract, with the plain book's faces. It has the same language gate: **English only**. (Phase 2 put it on Homebrewery's Blank layout, 41.10.)
- **Editor** (`Components/Campaign/BookDraftEditor.razor`, opened from the book panel):
  - Chapter list, CodeMirror (Markdown and a stylesheet tab), and the whole book drawn in a frame. The frame is `sandbox="allow-same-origin"`
    (no script; same-origin only so the editor can draw into it and measure), shell CSP `default-src 'none'`, and is scaled to fit by a transform.
  - Autosave about a second after typing stops. Text the editor is given (showing a chapter, a Regenerate) is not typing and is never saved
    back; `BookDraftE2ETests.ATabThatOnlyLooksAtAChapter...` holds it (without the guard, a tab that only looked turned another tab's edit
    into a conflict). The text goes to the server as a stream (no message-size limit).
  - A conflict stops saving from that tab and says so; Close then warns once before closing over text that could not be saved.
  - The stale notice names the changed entries; it offers Regenerate, Keep mine, and Undo after a Regenerate.
  - Start over (with a confirmation) and Download PDF.
- **Routes**: `GET /campaigns/{id}/book/{edition}/draft/{lang}/art` (the edition's preview pictures by slot, GM only, no-store) and `/pdf`.

**Tests:** `BookDraftTests` (10: party canaries including the masked id, GM secrets, the text conversion, version conflicts, follow and stale
naming the entry, Regenerate and Undo, keep mine, breaks are not edits, access and posted values, two editions and deletion);
`BookDraftPdfTests` (5); `tools/book-editor/test.mjs` (the shipped bundle against hostile drafts, under jsdom); `BookDraftE2ETests` (2: the
dialog, with typing, autosave, preview, stale notice, Regenerate and PDF; and a tab that only looks never saving); the backup test. Mutants caught: entry id written raw; secrets kept in the party draft;
version check removed; breaks counted as edits; the `expr-eval` stub left out (`test.mjs`); text the editor is given counted as typing (the
two-tab e2e).

**Not done, or not verified:**
- ~~**Deviations from 41.2:** Plain is our own CSS for Homebrewery's page structure, not Blank's layout CSS, and `{{cover` is our block
  name.~~ Done in phase 2 (41.10): every theme sits on Blank, and the generator writes Homebrewery's `{{frontCover}}`, `{{toc,wide` and
  `{{chapter` where they exist.
- The editor's renderer runs in the app's page (the trust boundary above).
- ~~The overflow report reaches the GM only in the preview.~~ Done in phase 3 (41.15): Fit pages reports it from the print browser, and the
  editor reads the PDF's header.
- **The preview was seen only in Chrome.** Safari and Firefox were not tried (section 40, finding 3).
- The editor was not looked at on a phone, in RTL, or with a screen reader.
- Russian and Hebrew drafts are made and edited but cannot be printed (the language gate).
- There is no per-chapter preview: the whole book is drawn again on each pause in typing. Fine for the test campaign (3 pages); not measured
  on a large one.
- Pictures in free text (`asset:`) are dropped to their captions (see Generator above).
- Chapter order cannot be changed, and the GM cannot add a chapter of their own; they can write anything inside one.
- The old book route still exists beside the draft (41.7). "Polish with AI" output is not yet an input to the generator.

### 41.10 Phase 2: built (Blank layout, Ashen Vellum, theme picker)

The GM asked for option 1 of the phase-1 report: themes on Homebrewery's Blank layout, so CSS written for Homebrewery works here as there.

- **Blank** (`tools/book-editor/themes/blank.less`): Homebrewery's `themes/V3/Blank/style.less` at commit `3e600a0` (MIT, notice in
  `THIRD-PARTY-NOTICES.txt` and `LEGAL.md`), with only removals: its `@import`s (the 5e and Journal fonts, Homebrewery's picture assets, icon
  fonts) and its watercolor, image-mask and credits blocks (they draw Homebrewery's pictures from `/assets` URLs). The build compiles it with
  `less` into `src/Codex.Core/Book/Themes/blank.css`, embedded. Every theme's page is Blank, then the book's page (paper size over Blank's Letter,
  every page laid out rather than only those on screen, the screen around the pages), then the theme (`BookDraftThemes.PageCss`).
- **Homebrewery's blocks where it has them:** the generator writes `{{frontCover}}` (with our `{{cover-art` for its picture), `{{toc,wide`
  and `{{chapter,#ch-ID` around the title. `{{chapter-art`, `{{entry`, `{{portrait`, `{{place` and `{{secret` are ours (Homebrewery has none).
  `BookDraftWriter.Version` is `book-draft/v2`, so unedited chapters followed by themselves (seen in the preview); an edited front chapter
  shows the stale notice.
- **Page numbers and contents as Homebrewery's:** the renderer gives each page Homebrewery's `<div class="pageNumber auto">` (beside the
  columns, so fitting never moves it; none on a cover or where the text placed one), counted as Blank counts (`skipCounting`, `resetCounting`),
  and writes contents lines as two spans, which Blank's `.toc` rules join with dot leaders.
- **A break in campaign text is escaped** (`BookDraftWriter`): a `\page` or `\column` line in a biography or wiki page prints as its words,
  so player-written text cannot break, hide or restyle a page now that `\page {…}` styles one. Breaks are the GM's to write.
  Homebrewery's inline `{…}` and `{{…}}` syntax in campaign text is still read (since phase 1): a player can style or box their own words, with
  styles cleaned as everywhere; not escaped yet.
- **`\page {classes,style}`** is read as Homebrewery reads it (marked-hbfm's own inject parser on the `\page` line): plain class names go on
  the page, a style goes through the cleaner. A styled break is now an edit (`BookDraftText.IsBreak`), so a chapter with `\page {reliquary}`
  is not replaced silently when the campaign changes; plain breaks are still layout.
- **Ashen Vellum** (`src/Codex.Core/Book/Themes/ashen-vellum.css`; its header lists every change from the GM's sheet):
  - Faces bundled from Fontsource 5.3.0 with their licences: IM Fell English, English SC, DW Pica, DW Pica SC, Pirata One (OFL), Special Elite
    (Apache 2.0). UnifrakturMaguntia was named by the sheet but drew nothing, so it is not shipped. English only (Latin faces).
  - **The paper is pictures**, made once by `tools/book-editor/themes/ashen-vellum/make-tiles.sh` from the sheet's own CSS and SVG: page-sized
    JPEGs (odd, even, soot, reliquary, cover; burnt edges, stains, tint) and WebP tiles for grain and foxing. Measured: with the sheet's soft
    transparent gradients Chrome wrote five page-sized pictures into **every** page (44 pages, 6.6 MB, 150 KB a page); with the baked paper,
    1.9 MB (43 KB a page; Plain is 12 KB a page). The stains are softer and fewer than the sheet's generated ones.
  - **No text-shadow and no text stroke:** Chrome writes each shadow and each stroke into the PDF as another copy of the words, so the
    cover's title was read back as "SSSaaalllttt..." and every chapter title as "LLoorree". The cover's murk keeps the title legible instead.
  - **Pictures:** only the generator's `{{chapter-art` is the torn banner (the sheet masked every picture on a chapter page, which put
    portraits over the text, section 40 finding 1). Portraits float in a ragged frame, place pictures span the column torn along the foot, the
    cover art fills the cover under the murk. A picture the GM writes is in the flow. A chapter with no art gets the plain heading by itself.
  - What PHB used to supply is written in (the footer rule's box, the footnote's place, paragraph spacing, the column gap). The sheet's
    vocabulary stays (`.note .descriptive .monster .quote .tome .credits .artist .frontcredit`, `\page {reliquary}`, `\page {soot}`); `{{secret`
    is the GM's dossier box in Special Elite with the red frame.
  - The sheet's `@media print` had no ink-saver left (only `print-color-adjust`), so preview and PDF are the same page.
- **Theme picker** in the editor bar: what was typed is saved first, the theme is stored (`SaveSettingsAsync` refuses an id the book does not
  have, and touches no chapter), and the preview is drawn again in the new theme's page. The PDF prints with the stored theme.

**Tests:** `BookDraftPdfTests.EveryTheme_SitsOnBlank...` (Blank first, then the paper size, nothing loaded); `EveryBlock_UnderEveryTheme_KeepsPicturesOffTheText`
(each theme in Letter and A4, real Chrome: a chapter with art, a portrait on the chapter page, text onto the next page, a place picture there,
the boxed blocks and a table; no printed word may sit on a portrait or place picture; the chapter's title over its banner is the one
exception). Putting the sheet's old "every picture on a chapter page" rule back fails it for Ashen Vellum. `ALongBook_UnderEveryTheme_StaysSmall_AndItsTextStaysText`
(30+ pages; under 60 KB a page; the cover's title read back once; every 37th paragraph found as text). The gradient paper failed it (150 KB a
page), and so did the shadows and strokes. `BookDraftTests.ATheme_IsStoredWithTheDraft...`, the styled-break line in `PageAndColumnBreaks...`, `test.mjs`
(page numbers counted as Blank counts, the contents' two spans, `\page` classes and hostile page styles), and the editor e2e test now switches the theme
straight after typing (the typed line is saved and drawn in the new theme).

**Not done, or not verified:**
- ~~CSS written for the PHB theme will not look the same.~~ Done (41.12): the Homebrewery compatibility layer, with a parity test.
- ~~The GM's stylesheet cleaner strips every backslash, and its cap is 20,000 characters.~~ Done (41.11): `BookCss.Clean` reads the sheet
  as CSS Syntax Level 3 tokens and writes back only what it checked; escapes survive; the cap is 512,000 characters.
- No `url()` in the GM's CSS but `data:image/` (and `data:font/` inside `@font-face`); Homebrewery CSS that loads fonts or pictures from the
  web will not load them (on purpose).
- Contrast on Ashen Vellum's paper (WCAG AA for body text, notes and table rows, section 14 item 7) is not measured. The dark pages and the
  cover were only looked at.
- RTL: not applicable yet (English only); the sheet's `float: left` drop caps and left-side ribbons would need logical properties first.
- The preview redraw time was not measured on a large book in the GM's browser; the print browser laid out and printed 44 Ashen Vellum pages
  in about 2.5 s (browser start included).
- Read back from the PDF, Ashen Vellum's drop cap splits a chapter's first word ("P UBLIC") and its small-caps lead-in and bold (small caps,
  the faces have no bold) come back in capitals ("T HE TIDE"). Search and copy see that; the long-book test steps around the first paragraph.
- Plain looks different from phase 1: Blank's page padding (1.4/1.9/1.7 cm), Blank's page number in the corner (switching sides on even
  pages), Homebrewery's cover and wide contents.
- Small soft gradients (heading rules, the footer rule, the monster frame, the cover murk) are still written as small pictures each time they
  are drawn (about 4 KB a page).
- Still from 41.9: Safari and Firefox, ~~the overflow report in the panel~~ (41.15), phone, screen reader, `asset:` pictures in free text, the trust
  boundary of rendering in the GM's page.

### 41.11 The GM's stylesheet, read as CSS (built 2026-11-06)

The regex cleaner removed every backslash to be safe, which broke Homebrewery CSS such as `content: '\2014'`, and its 20,000-character cap
could not hold a theme. `BookCss.Clean` (`src/Codex.Core/Book/BookCss.cs`) now:

- **Reads the sheet as the browser does**: CSS Syntax Level 3's tokenizer (section 4), escapes decoded before any name is looked at, so
  `\75rl(`, `@\69mport` and `e\78pression(` are seen as what they are.
- **Checks tokens, not text**: `@import`, `@namespace` and `@charset` rules are dropped (nested ones too; since 41.14 an `@import` the
  server has a copy of is that sheet, cleaned the same way); `url()` keeps only `data:image/` and `#fragment` (and `data:font/` inside
  `@font-face`) or the server's copy of a web address (41.14), anything else becomes `none`; a `@font-face` with any other source is dropped
  whole; `image()`, `image-set()`, `src()`, `element()`, `paint()` and `expression()` calls become `none`; `behavior` and `-moz-binding`
  declarations are dropped. Bad strings, bad URLs, `<!--`, `-->`, a stray `<` and a stray backslash are dropped.
- **Writes back only the tokens**: a token with no escape as it was written, the rest re-escaped (CSSOM's serialize-an-identifier); every
  string is re-escaped with `<`, `>` and `&` as hex escapes, so no `<` is ever written and nothing closes the style element. Where a
  comment or a dropped token stood between two tokens, `/**/` keeps them apart (`ur<l(` must not become `url(`).
- **Cap: 512,000 characters**, in the cleaner and the store (`BookDraftDocument.MaxCssChars`): a whole Homebrewery theme and a design on top.

Tests: `BookCssTests` (escape, comment and join bypasses, the style element, broken sheets, what Homebrewery's sheets write kept token for
token, cleaning twice changes nothing). Mutants each fail it: ignoring the join guard, a raw `<` in a re-escaped name, names read without
decoding escapes, strings written raw, and a fetching `@font-face` kept.


### 41.12 The Homebrewery compatibility layer (built 2026-11-06)

Most shared Homebrewery CSS is written on top of its 5e PHB theme, not Blank, so on Blank alone it lands in the wrong places. The user
chose parity with Homebrewery (people bring their CSS with them) but no PHB look: Homebrewery's PHB is a fan recreation of the official
book's look, and its pictures have no traceable licence (its issue #723 left that open; its watercolour stains came from imgur links, its
cover placeholders from an image generator).

- **The layer** (`src/Codex.Core/Book/Themes/homebrewery-5e.css`): Homebrewery's `themes/V3/5ePHB/style.less` (MIT, commit 3e600a0,
  vendored unchanged as `tools/book-editor/themes/homebrewery-5e.less`) compiled by `build.mjs` and stripped by rule: every picture (its
  asset variables compile to `none`; `background-image`, `border-image`, masks dropped), every colour turned to the grey of its lightness
  (so `--HB_Color_*` keep their names), gradients, shadows, text stroke and filters dropped (they cost a raster or a second copy of the
  text in the PDF), `@font-face` dropped (the server adds the faces). A frame that was a picture keeps its width, transparent: width is
  layout. The build fails if a `url(`, gradient, shadow, stroke or mask is left.
- **Its faces** (`wwwroot/fonts/homebrewery-5e`, see its README): Homebrewery's own copies of Solbera's fonts (CC BY-SA 4.0), Overpass
  (OFL) and Walter Turncoat (Apache 2.0). Homebrewery's copies, not Solbera's originals: Scaly Sans, Scaly Sans Caps and Nodesto differ
  from the originals in widths and line metrics (measured with fontTools), and only Homebrewery's lay a page out as it does. The exception,
  chosen by the user: the drop-cap face is Solbera Imitation 1.60 from Solbera's release (CC BY-SA in its own metadata), not Homebrewery's
  tweaked 1.30, whose metadata reads "All Rights Reserved" with no licence. So drop caps differ in size from Homebrewery's, and text beside
  one wraps differently; the parity test uses the same faces on both sides and cannot see it.
  Davek, Iokharic and Rellanic (no known source) and Martel Sans (unused) are left out. About 350 KB, embedded in the shell only when the
  layer is on; a PDF keeps only the glyphs it uses.
- **Order**: Blank, the layer, the book's page rules, the theme, the GM's CSS. **A setting on the draft** (`Homebrewery`, on by default; a
  draft saved before it existed reads as on), a checkbox in the editor's bar, read by the preview shell and the print route alike. The page's
  body gets `homebrewery-layer` when it is on.
- **A third theme, "Homebrewery (layout only)"** (`homebrewery`): the layer with black ink on white, thin lines for the frames (drawn as
  outlines, which take no room), and the generator's own blocks. It sets no size, margin or face of the layer's: it is the theme a
  Homebrewery brew's CSS matches; it sets colours through the `--HB_Color_*` variables, so a brew's CSS that sets them is obeyed. Plain and
  Ashen Vellum sit on the layer too, keep their faces (Plain names its own wherever the layer names one; Ashen Vellum already did) and the
  cover title's case, and take the layer's spacing where they set none. That changes their looks, measured by laying the same chapter out
  with the layer and without: Plain moved 35 of 41 marker words (paragraph indents, PHB's line height and spacing, box padding, drop caps
  in Cinzel); the user chose that over Plain off the layer. Ashen Vellum moved 16, by 1.5 to 13 pt, all in its notes, table and stat
  block. On any theme the cover's tagline (the paragraph after its title) gets no drop cap.

Tests (`BookDraftPdfTests`):
- `UnderTheHomebreweryLayer_ABrewLaysOutAsUnderHomebrewerysOwnTheme`, Letter and A4: a brew of everything PHB styles (headings, drop cap,
  lists, quote, note, descriptive, table, stat block, column break, wide block, contents; since 41.13 a quote with its attribution and a
  footnote too) printed under the layer and under Homebrewery's whole theme compiled (test fixture `Fixtures/homebrewery-5e-reference.css`,
  written by `build.mjs`, never shipped) pasted as the GM's CSS over no layer, with the same faces: all 80 marker words on the same page
  within 1 pt. Mutants each fail it: paragraph spacing removed,
  the h3 size changed, the note's padding changed, the descriptive frame's width changed.
- The overlap and long-book tests run every theme with the layer at Letter and A4 and without it at Letter, and a theme with faces of its
  own must print in them alone (Plain failed it, with notes in Scaly Sans, before its font rules).
- `BookDraftTests`: the setting's default for an old draft, and turning it off. The editor's end-to-end test turns it off.

Not done, or not verified:
- **Parity is measured for one brew**, in Chrome, against Homebrewery's themes compiled here; not against Homebrewery's live site, and not
  for its Journal and Unearthed Arcana themes or its snippets' markup beyond what the brew uses. (DMG and a community theme: 41.13.)
- **Pictures a brew's CSS loads from the web** (imgur, a CDN): the server fetches a copy since 41.14. Homebrewery's own `/assets/...`
  addresses are relative to its site and still load nothing (there is no address to fetch); uploads are not built.
- **Markdown parity** beyond variables (41.13): icon shortcodes, definition-list and spacing syntax are not audited.
- Russian and Hebrew: the layer's faces are Latin; the layout-only theme is English only, like the others.
- The layer's greys and the layout-only theme's contrast were looked at, not measured; Plain on the layer was looked at for one chapter.

### 41.13 Homebrewery's variables, its DMG theme and a community theme (checked 2026-11-06)

- **Markdown variables** (`[name]: value`, `$[name]`, hoisting, cross-page, page numbers, links and pictures by variable): Homebrewery's
  own tests (`tests/markdown/variables.test.js`, commit 3e600a0, MIT) are vendored unchanged in `tools/book-editor/homebrewery-tests` and
  run on the shipped bundle by `test-homebrewery.mjs` (part of `npm test`). Before: the 32 tests without maths passed and the 24 with maths
  failed (maths was switched off). Now all 56 pass:
  - **Maths** is `src/safe-math.js`: what Homebrewery's parser allows (`+ - * / ^`, brackets, `round floor ceil abs`, `min max pow hypot
    roundTo`, and the extension's own `sign signed toRomans toChar toWords` and their cases). A name is only ever looked up among those
    functions (an object with no prototype); there is no member access, string or assignment; 500 characters and 40 levels at most. Anything
    else throws, and the extension leaves the `$[...]` as written. `random` is left out (the preview and the PDF would differ).
  - **A second render pass** when the brew has a `[`: `marked-hbfm` learns a variable set on a later page only by rendering that page, and
    Homebrewery's editor, rendering on every keystroke, always has. The print browser renders once, so before this a variable used before
    its page printed as `$[name]` in the PDF and worked in the preview.
  - Like Homebrewery, a deleted definition is remembered until the preview is reloaded (`marked-variables` keeps its list for the page's
    life); the PDF never has it. Not fixed: the package has no way to clear it.
- **Colour variables** (`--HB_Color_*`), set in a brew's CSS as Homebrewery's users set them on `:root`: measured in the PDF (heading
  letters' colour, the fills of table stripes, stat block and page). The layout-only theme obeys them. **Plain did not**: on the layer its
  table stripes and stat block printed in the layer's greys instead of its own beige (a change 41.12 missed), and an override recoloured
  those but not its headings. Plain now sets the variables on `:root` to its own colours and its rules read them, so it looks as it did and
  obeys an override, on the layer or off it. Ashen Vellum keeps its own palette and ignores an override, by design. Test:
  `HomebrewerysColourVariables_SetInABrewsCss_ColourTheBook` (an override under the layout-only theme and Plain, and Plain's own colours
  with none, no greys); taking Plain's mapping out fails it.
- **The 5e DMG theme** (`themes/V3/5eDMG/style.less`, vendored as `tools/book-editor/themes/homebrewery-5e-dmg.less`, compiled by
  `build.mjs` to the fixture `Fixtures/homebrewery-5e-dmg.css`): it sits on PHB and changes only colours, pictures and three things of
  layout (a quote's first line, the footer's height, the footnote's place). Pasted as the brew's CSS on the layer, the parity brew lays out
  as under Homebrewery's PHB and DMG whole: 80 of 80 words. Against PHB alone DMG moves 2 of them, and the test fails if our side leaves the
  DMG CSS out. In Homebrewery DMG is picked from a menu, not pasted; there is no such choice here.
- **A community theme**: "Tasha's Style" (KaiburrKathHound, a theme brew in Homebrewery's Vault, about 10,500 views; share id
  `5CxliV-7LFuV`). It has no stated licence, so it was measured once and is not in the repository. It moves 22 of 80 words against PHB
  alone. With its fonts supplied, the layer matches Homebrewery word for word (77 of 77; its h5s are letter-spaced and read as letters on
  both sides). **As a GM would paste it**, its fonts come by `@import` from a CDN, which the cleaner drops: the layout still matches (1 word
  moved by 9 pt, the quote's attribution) but its Cormorant Garamond text prints in a fallback face (Times). Its two font files are data
  fonts (allowed in `@font-face`), but they total 1.26 MB, over the 512 KB cap; the faces it uses come to 388 KB. Its page texture and note
  frames are imgur pictures and do not load. **Since 41.14** the server fetches all of it, and Tasha as pasted matches Homebrewery word for
  word (77 of 77), pictures and all.

### 41.14 The web addresses a brew's CSS names: fetched once by the server (built 2026-11-06)

A brew's CSS loads its fonts and pictures from the web (`@import` of a font sheet or Google Fonts, `url()` of an imgur texture); the page
fetches nothing (its policy, and the cleaner), so before this Tasha's Style printed in Times with no texture. Now the **server** fetches each
https address the GM's stylesheet names, once, keeps a copy with the draft, and the cleaner writes the copy in place of the address. The page
still fetches nothing.

- **What is fetched is what the cleaner asked for.** `BookCss.Clean(css, out warnings, web)` takes the copies (`BookCssWeb`,
  `src/Codex.Core/Book/BookCssWeb.cs`): a font or a picture is written as a data URL, an `@import` as the imported sheet's own rules where
  the rule was, cleaned by the same code (its relative addresses are relative to it; wrapped in `@media` when the rule had a media list;
  dropped when it has a `layer` or `supports()` condition, comes after another rule, imports itself, or is more than 4 imports deep). While
  it cleans it notes every address it was asked for and the ones it had no copy of, and that list is what the server fetches
  (`BookDraftWebs.RefreshAsync`, `src/Codex.Web/Book/BookDraftWebs.cs`), round after round until the sheets fetched import nothing new. So
  the server never fetches what the page would not have used, nor misses what it would. An imported sheet cannot reach past itself: what it
  leaves open is closed at its end, a closer with no opener is not written, and its strings are escaped like every string.
- **Only https, only the public internet** (`BookWebFetcher`): the server's own client (`UseProxy=false`, no cookies, no redirects of its
  own) checks the address when it connects, so a name that answers differently a second time gains nothing; every address the name gives
  must be public (not loopback, private, link-local such as a cloud's metadata service, Tailscale's 100.64/10, multicast, documentation, or
  the IPv6 forms that carry an IPv4 address: IPv4-mapped, 6to4, Teredo, NAT64). Redirects are followed by hand, 4 at most, each checked
  again. It sends a browser's User-Agent (Google Fonts answers a browser with woff2) and nothing about the server or the GM.
- **Bounds**: a sheet 3 MB, a font 4 MB, a picture 6 MB, read to the bound whatever the site says its length is; 16 MB and 120 copies a
  draft; 20 seconds an address. The 512 KB cap stays on the GM's own text.
- **A copy is what its bytes say**, not what the site says (raw GitHub serves fonts as `text/plain`, imgur can answer a picture with a
  page): woff2, woff, TrueType, OpenType, a collection or EOT for a face's source; PNG, JPEG, GIF, WebP or AVIF for a picture (not SVG, as
  uploads do not take it); for a sheet, text with no control characters that is not an HTML page.
- **Stored** as attachments on `bookdrafts/{campaign}/{edition}/{language}/web` (`BookDraftWeb`), under the draft's id: they go with
  "start over" and with the campaign. Only the campaign's GM reads or writes them. A copy whose address the sheet no longer names is let go
  at the next fetch.
- **When**: after the stylesheet saves (outside the save, so chapters keep saving), and when the editor opens; only addresses with no copy
  are fetched, a failure is not remembered (the next save tries again), and a newer fetch for the draft cancels an older one. The preview is
  restyled with the copies when a fetch ends. Printing never fetches: the PDF is printed from the copies. The stylesheet pane says what is
  kept and lists each address that could not be fetched, with why.
- **The older one-shot export** (`BookHtml`, its custom CSS) does not fetch: it has no draft to keep copies with.
- **Fixed on the way**: an empty stylesheet or chapter (the GM deleted it all) could not be saved: Blazor takes no stream of no bytes. The
  editor now reads the length first.

Measured: Tasha's Style as pasted (two `@import`ed font sheets of 0.35 and 0.91 MB, 2 more faces, 16 imgur pictures): 20 copies, 5.4 MB,
about 7 to 10 seconds from here; the parity brew then matches Homebrewery word for word (77 of 77), with Tasha's fonts, texture and frames.
In the editor the preview takes the copies when the fetch ends, and they are there at once when the editor is opened again. Google Fonts
(checked once, live, with Pirata One): the sheet is kept as CSS, both subsets come back as woff2, and the heading prints at the width the
face sets when written into the sheet by hand.

Tests: `BookCssWebTests` (addresses; an imported sheet written in place with its relative addresses as copies; what has no copy noted and
dropped; a copy of the wrong kind not used; media lists; conditions, late imports, cycles and depth; an imported sheet that tries to reach
past itself; sniffing; the public-address list; the server's registered fetcher refusing this computer by name and number; redirects,
errors, pages in place of pictures, and the bounds, against a site on this computer); `BookDraftTests` (copies kept, not fetched twice,
printed from, let go, GM only, gone with "start over", through the real fetcher over a fixture site);
`AnImportedThemesFaceAndPictures_Print_FromTheServersCopies` (an imported theme's face, set at the width it sets by hand, and its relative
picture, in the PDF; neither without the copies). Mutants each fail them: the public check always true, no check at connect, any bytes
taken as a picture, no balancing of an imported sheet, imports anywhere, and no depth or cycle guard (a stack overflow).

Not done, or not verified:
- A proxy set for the server is not used by this client, on purpose; a server that can reach the web only through a proxy fetches nothing.
- No retry and no "fetch again" button: a copy is kept until the sheet stops naming it (a changed file at the same address keeps the old
  copy until the address leaves the sheet and comes back).
- Each fetch reads the copies from the database and the preview gets them whole over the circuit (8 MB of stylesheet for Tasha): fine here,
  not measured on a slow link or a phone.
- Fonts a GM's CSS names are under their own licences; they are the GM's to use.
- Fetching is open to any campaign's GM, also while the server is open to the internet (decided 2026-11-07): unlike the server's AI key
  (`ExposurePolicy.MayUseServerAi`) it costs no money, and the bounds above hold. A stranger who signs up could still make the server fetch
  public addresses, up to 16 MB a save.
- Typing in the stylesheet pane with a large set of copies (Tasha: 8 MB of stylesheet in the preview) was not timed. A fetch that changes
  nothing does not restyle the preview a second time.

### 41.15 Phase 3: Fit pages and the overflow report (built 2026-11-08)

- **Fit pages** (the editor bar; `BookDrafts.FitAsync`, `BookPdf.FitAsync`, `render.js` `fitText`): what was typed is saved first, then the
  draft is laid out **in the print browser**, with the PDF's pictures, stylesheet, web copies and paper, so the breaks come from the browser
  that prints and not from the GM's (which may be Safari or Firefox and break lines differently). Wherever the drawing would move a block
  onto a page of its own, a `\page {fitted}` line is written before it in that chapter's Markdown. Laid out again until nothing moves (six
  rounds at most). Each chapter it changed is saved at the version it was read at; one saved meanwhile (another tab) is not overwritten: the
  run stops there and the editor shows the conflict. The changed chapters are put back in the editor as text the editor is given, not as
  typing. It takes a place in the printing line (one per GM), like a PDF.
- **Decided: the breaks it writes say so.** `\page {fitted}` (after a page with classes, `\page {reliquary,fitted}`, as the drawing's own
  continued page copies them; the page does not get the `fitted` class). A second run takes out its own breaks first and fits again, so
  after an edit the breaks move instead of piling up, and a plain `\page` the GM typed stays where it is. `\page {fitted}` is layout, not an
  edit (`BookDraftText.IsBreak`), so a fitted chapter is still "unedited" and still follows the campaign by itself; following it gives the
  chapter the new generated text, without the breaks, and the drawing continues the pages by itself again until the next Fit pages.
- **Decided: only `\page` is written, never `\column`.** The drawing moves blocks onto pages, not columns; a column break the GM writes is kept.
- **Where a block's Markdown starts** (`marking`): for the run only, marked-hbfm's top-level tokens are rendered one at a time (as its parser
  takes them anyway) and the first element of each gets a `data-fm` tag naming the line its token started on. Nothing is added to the
  Markdown or between blocks, so rules such as `h1 + p` see the same page (a test draws hostile, table, list, block, column, variable and
  styled-page brews with and without the tags: the same HTML). A tag is kept only with this run's random prefix; one typed into a draft is
  dropped. A block whose line cannot be found (a line the variables changed) takes the nearest block above it on its page; when there is
  none, no break is written and the report lists it.
- **A heading never ends a page:** the drawing (preview, PDF and Fit pages) moves a run of headings just above a block that spills along with
  it, unless that would move the page's first block.
- **The overflow report:**
  - Fit pages says how many fitted breaks the book has and how many chapters changed (or that nothing changed), lists each block too tall
    even for a page of its own (page, chapter, first words), and each break it could not place. This is the report **before** the PDF, from
    the browser that prints it.
  - The preview names each cut-off page's chapter, and says how many pages run on past where the text breaks them (Fit pages would write
    those breaks).
  - The PDF route's `X-Book-Overflow` header is now `7:cast,9:lore` (page numbers and section ids, ASCII only); the editor reads it after the
    download and says which pages printed cut off, with their chapters. The PDF is still given out: a warning, not a refusal.
  - Page numbers in the reports count every page from the cover (as a PDF viewer shows them), not the numbers printed on the pages.
- **The print browser takes the chapters** (`BookPrintLayout.Chapters`) rather than the joined brew, so it knows each page's chapter;
  the renderer joins them as the editor does.

Tests: `test.mjs` (marking leaves the HTML the same; tags point at their lines; a typed or foreign tag is dropped; the chapters' places in
the brew and each page's chapter; `\page {fitted}` written and taken out exactly, fitting again writes the same text, the GM's breaks stay);
`BookDraftPdfTests.FitPages_WritesTheBreaks...` (real Chrome: breaks written, no page continued after them, no page ending in a heading,
every word printed on the same number of pages, a second run the same, an edit moves the breaks and keeps the GM's own) and
`FitPages_KeepsAStyledPagesClasses...` (the styled page's classes, a too-tall note named with its chapter and words, by Fit pages and by the
PDF's header); `FitPages_NeverLeavesAHeadingAtTheFootOfAPage` (40 chapters whose heading lands lower and lower: some break
falls right before a heading, none right after one); `BookDraftTests` (`\page {fitted}` is layout and `{reliquary,fitted}` is not; Fit pages saves only the chapters it changed,
at their versions, leaves them unedited, stops at a conflict, and lays nothing out for a player; the header read back);
`BookDraftE2ETests.TheGmFitsPages...` (the run-on line, Fit pages, the breaks in the editor and saved, a second run changing nothing, the
too-tall block named before and after the PDF). Mutants, each failing them: headings not kept with their text (survived the first
long-chapter test, which had no heading at a page's foot; the 40-chapter test was written for it), earlier breaks not taken out, the
styled page's classes dropped, no block found in the Markdown, a mark kept whatever its prefix (`test.mjs` was too weak for it at first),
a continued page losing its chapter, fitted breaks counted as edits, a conflict ignored, unchanged chapters saved, the header without
sections, the editor not reading the header, and the editor not taking the fitted text. One survived: the guard against a break before the
first line of a page's text, which the drawing never reaches (it never moves a page's first block); kept as a guard.
Seen in the app (preview, the real-run campaign's Ashen Vellum GM draft, 3 pages): Fit pages ran in the print browser and said every page
already fits; no server errors.

Not done, or not verified:
- Safari and Firefox: still not tried (only Chromium is installed for the tests). Fit pages makes them matter less: the breaks come from the
  print browser, and the drawing still continues pages by itself where the GM's browser differs.
- A theme or stylesheet change after Fit pages can make the breaks wrong (pages half empty or running on); run it again. Nothing says so yet
  beyond the run-on line.
- Keep-together for a short block after a heading in the next column (only page ends are handled).
- Large books: one Fit pages run on the 44-page Ashen Vellum book was not timed; each round lays the whole book out again.
- The Russian and Hebrew text of the new strings (`Campaign.Book.Draft.Fit*`, `RunOn`, `PrintedOverflow`) is machine-drafted.

### 41.16 Phase 4: AI edits on a selection (built 2026-11-08)

- **Asking** (the "Ask AI" bar above a chapter's Markdown; `BookDraftAi`, `BookDraftEdits`, `BookEditWorker`, `editor.js` `selection`/`review`/`finish`):
  the GM selects a passage, or nothing for the whole chapter, and presses a preset (As the book, Shorter, Read-aloud box, Tidy;
  `BookEditPrompts.Presets`) or types a request (600 characters at most). What was typed is saved first; the server reads the chapter **as
  stored**, at the version the editor had, and refuses (Stale) unless the selected text is exactly what is stored there. The page's
  offsets are trusted only that far.
- **The pre-flight sends nothing:** it names the model, whether the selection or the whole chapter goes, whether the selection was widened, how many
  pieces are kept back, the token range and the cost at the GM's prices (or that no prices are set), and the server allowance. The editor
  is locked from here until the review ends, so the text stays what the server checked. Send, or Cancel.
- **What the model never sees** (`BookDraftEdits.Cut`, found in the whole chapter before the selection is looked at, so a selection that starts or
  ends inside a piece takes the whole piece): every `{{secret` block (nested, inline, with other classes, any case; one never closed runs to
  the end of the chapter), every HTML element with a `secret` class (unclosed: to the end), pictures (`![…](…)`, `{{portrait`, `{{place`,
  `{{chapter-art`, `{{cover-art`), the generator's markers (`{{chapter,#ch-…`, `{{entry,#e-…}}`, the cover, the contents, any block with an
  `#id`), and page and column breaks. Inside the passage each is `{{block:N}}` with a label; in the text before (1,500 characters) and after
  (600) it is left out. The model therefore cannot change a secret, move or drop a picture, lose an entry's id (which phase 5's merge reads),
  or touch the fitted breaks.
- **The reply** must keep every block exactly once, on a line of its own where it stood on one, and carry no secret, entry, chapter or picture
  block, link, web address, picture, HTML (a `\<` the draft escaped is not HTML), `[[…]]`, `\page`/`\column` or `:::`. A reply cut off by the
  length limit is refused even if it passes, because a whole-chapter edit cut short would drop the chapter's end. One retry with the problems,
  then nothing changes. Pieces go back where their markers are, and the passage keeps its own blank lines at either end.
- **The book's shape comes from the game system** (the user's decision, handoff §7 item 2): `ai.bookShape` in `system.yaml`
  (`AiVocabulary.BookShape`) finishes "the book should read like this game's own published books, which are …". srd5e describes
  fifth-edition adventures' structure and furniture (an overview, keyed places, read-aloud boxes in the second person, sidebars, stat blocks,
  checks by ability and DC) with no title, trademark or sentence of theirs; a system that says nothing gets neutral words. The prompt also
  names the draft's own blocks (`{{note`, `{{descriptive`, `{{monster`, `{{wide`), so "make this a read-aloud box" can be done.
- **Money:** the per-GM `BookGate` before anything is awaited (Busy otherwise), the server's requests set aside up front (two: the try and
  its retry), a `BookBudget` capped at both attempts' worst case, settled on real usage; what was used is shown after. Not stored anywhere
  (the build's spend totals do not include edits).
- **The review overlay:** the reply opens in a read-only CodeMirror view in the chapter editor's place (`@codemirror/merge`'s unified view,
  pinned 6.12.3; the same author's package as the editor), each change with Accept and Reject, deleted text struck through and new text
  marked word by word inside changed lines. Alt+Up and Alt+Down go from change to change; Ctrl+Enter accepts and Ctrl+Backspace rejects the one at the
  cursor. The bar says how many changes are left and offers Keep what I accepted, Accept the rest, Discard. **The preview draws the chapter
  with only the changes accepted so far.** What is kept goes into the chapter as one change, as if typed: it is saved by the ordinary save
  (with its conflict check), and Ctrl+Z in the editor takes it back. While the review is open, typing, the chapter list, the theme, Fit pages,
  Regenerate and Start over are off; closing the editor discards it.

Tests: `BookDraftEditTests` (a selection starting or ending inside a secret takes all of it; a secret beside the passage is left out of the
text around it; eight spellings of a secret, each cut at every 3rd/5th character pair, never reach the prompt and always restore to the same
chapter; pictures, markers and breaks kept back; a reply missing, doubling, inlining or inventing a block, or carrying any forbidden thing, is
refused; a cut-off reply is never offered; a retry carries the problem; srd5e's shape reaches the prompt with no trademark in it, simple-d6
gets the neutral words); `BookDraftTests.AnAiEdit_*` (the GM draft's secret never sent and put back, the shape sent, nothing saved; stale
version, moved text, no request, a too-long request, a player, a busy gate (left held), no model: refused before sending; the whole chapter
when nothing is selected; the gate let go of after a run); `BookDraftE2ETests.TheGmAsksAiToEditASelection...` (real browser, a local fake
model: select a line, the cost shown and nothing sent, the review with the editor hidden, the preview unchanged until Accept and changed
after, kept text saved and marked edited, a whole-chapter ask in the GM's words discarded with nothing changed, the GM edition's secret never sent).
Mutants, each failing them: no widening at the start, a block closed at its first "}}", HTML secrets not found, a missing block not flagged,
the own-line rule dropped, cut-off ignored, secrets left in the text after, blank lines lost on restore, the stored-text check dropped, the
gate never let go of, no gate, no shape sent, the preview drawing the unaccepted text, Discard keeping the reply. One survived at first: only
the first class read as `secret` — harmless, because `secret` was also in the list of kept blocks; that second path was removed so the secret
test is the one path, and the mutant now fails two tests.
Seen in the app (preview, the real-run campaign's GM draft, Cast): a selection across Tomas's paragraph and Vera's entry marker; the pre-flight
named the GM's model, one piece kept back, the token range and that no prices are set; the editor was locked, and after Cancel unlocked
with nothing sent or changed. No request was sent (a real key is stored there).

**A real model, 2026-11-08** (the GM's own key, the provider's smallest current model, "As the book" on the whole Cast chapter: two
entries, chapter art, a portrait, five pieces kept back). What it found, each fixed and tested:
- **The reply's room was too small.** Twice the passage (at least 800) cut both tries off: shaping notes into a book's prose makes them
  several times longer. Now four times the passage plus 1,200, at least `MinOutputTokens` (2,000), at most the client's ceiling.
- **It made things up.** The shape asks for each NPC's wants and manner and for GM sidebars; the notes had neither, and the model wrote
  them ("nobody has taken him seriously since", a sidebar saying how she answers the characters), despite the rule against invented facts.
  The preset now says to use only what the passage says and to leave out a part of the shape the notes do not fill (`book-edit-2`). On the
  next run nothing was invented: an overview, a read-aloud box per character, then the notes' facts.
- **A sidebar opened with its title on the `{{note` line** prints as text, braces and all (marked-hbfm reads a block only from its name
  alone on the line). The prompt now says a title is a `#####` heading inside, and `Check` refuses a block opened with words after its
  name unless it closes on that line (an inline span).
- **After Keep or Accept the rest the kept text was selected with the focus in the editor**, so the next key replaced the whole passage
  (the whole chapter, after a whole-chapter edit). The cursor now goes after it; the browser test types straight after Keep.
Both runs kept all five pieces, in place, on the first try. Accept one change, then Accept the rest (with two still open) put all three in;
the preview showed only the accepted one in between; Ctrl+Z took the whole edit back. A key the provider refuses is reported as such with
nothing changed, though the "used about" line then counts the input as sent (the budget's rule for any failed call).

The other asks, same model and chapter, each discarded after reading: **Shorter** (Vera's paragraph) kept every fact, barely shorter
because the notes were already terse. **Tidy** (whole chapter) changed only a blank line after each entry marker, which renders the same.
**A typed request** ("as an entry in a ship's log, written by a passing captain") kept every fact and invented only the framing it asked
for. **Read-aloud box** (Tomas's paragraph) wrote a good box but then gave the GM a clue, a motive, a village-elders scene and a DC 12
Insight check that the notes never had; the preset now limits the box to what the passage describes and the GM's part to the passage's
facts, and the general rule names what may not be invented (what someone knows, wants or did, events, clues, checks and DCs;
`book-edit-3`). Run again, it added nothing but a mild "more worried than he lets on".

Not done, or not verified:
- One model, one chapter, English only.
- The AI bar takes room above the Markdown in a narrow window; the merge view's colours are its own defaults (not checked in dark mode).
- A request is a single call the dialog waits on (Stop cancels it); edits are not in the build's spend totals.
- The Russian and Hebrew text of `Campaign.Book.Draft.Ai.*` is machine-drafted.

### 41.17 The book's layout, like an IDE (built 2026-11-08, asked for by the user)

The user found the book's UI convoluted and asked for Visual Studio's collapsible tool windows as the model.
- **The editor** (`BookDraftEditor.razor`):
  - **The toolbar** is one row: theme, the Homebrewery layout, Fit pages, Download PDF (the primary button), and More (Start over, still
    confirmed). Close is an X on the title's row; in a narrow window the tools wrap below the title.
  - **Three panes**, Chapters | Markdown | Preview, each with a header. Chapters and Preview fold to a thin strip with their name and open
    again on a click. The Markdown pane's header names the chapter, its Edited or Stale badge, and Undo.
  - **A folded preview is still drawn**, off the screen, so the run-on and cut-off lines (now in the preview's header area) keep measuring it.
  - **A panel below** has tabs: Ask AI (phase 4's bar, the pre-flight and the review), Problems (Fit pages' report, pages printed cut off,
    stylesheet addresses not fetched, with a count on the tab) and Blocks (the block reference). Clicking the open tab folds the panel.
    A report opens it on Problems, and asking the AI opens it on Ask AI.
  - **An AI request's pre-flight (Send, Cancel) and its review (Keep, Accept the rest, Discard) sit on the Markdown pane**, above the code
    they lock, not in the panel: switching or folding the panel, or a print report opening Problems, never hides the way out of a locked
    chapter (found in review; the AI browser test switches and folds the panel with the review open).
  - **What is open is kept** in this browser (`localStorage`, `codex.bookEditor.layout`). The tabs take the arrow keys, Home and End.
  - **CodeMirror on the app's colours:** gutters, the active line, the cursor and the selection use the app's variables, and so does a
    highlight style in the bundle (headings brass, links and `art:` references verdigris). The editor is dark or light as the app is;
    before, it was a white box in a dark dialog.
- **The book panel** in campaign settings: the edition picker stays on top, and the rest is in tabs: Print (look, language, paper,
  columns, pictures, preview, download), Pictures (the picture tray, which opens and reads its slots the first time its tab is shown), Edit
  (the editable book) and AI polish. A player sees only Print, with no tab strip. Hidden tabs are hidden, not removed, so what was typed in
  one is still there. The editor dialog sits outside the tabs.
- Icons the layout uses that the app's set lacked (`more-horizontal`, `panel-left-close`/`-open`, `chevron-up`, `printer`, `blocks`, and
  `palette`, which the Stylesheet entry had always asked for and drawn as a circle) were added from Lucide.

Tests: the book browser tests click the tab they need; `TheEditorsPanesFold...` (both panes fold and open; typing still draws the folded
preview; the panel's tabs move with the arrow keys; the layout is the same after the page is loaded again; Start over behind More, still
asking); the Fit test now also checks that the report opens the Problems tab with a count of 1. Mutants: the layout never saved (caught by the
reload); the report not opening Problems (it survived at first, because the test read the report's text, which a hidden tab still has; the
visibility check was added for it).
Seen in the app (preview, the real-run campaign): the settings panel's tabs; the editor at 1400 px with both panes open, both folded, and
the panel folded; Fit pages opening the folded panel on Problems; at 820 px (stacked, Close on the title row); and under the light theme.

Not done: the panes cannot be resized by dragging (fixed proportions), and the panel's height is fixed; the folded preview's off-screen
drawing has no test that it is measured rather than only drawn.

### 41.18 Phase 5: Bring up to date (built 2026-11-08)

The stale notice of an edited chapter now offers **Bring up to date** beside Regenerate and Keep mine (`BookDraftMerge`, `BookMergePrompts`,
`BookDraftMerges`, `editor.js` `take`).

- **The unit is the entry.** A chapter splits, losslessly, into its head (title, chapter picture, anything before the first entry) and one unit
  per `{{entry,#e-…}}` marker. Base, mine and the campaign's version are matched **by entry id**, never by a line diff: this is what phase 4
  kept the markers safe for. An id that is in a version twice (a copied entry) refuses the merge with a message; Regenerate and Keep mine still work.
- **Units are compared without page or column breaks, blank lines or trailing spaces**, so Fit pages and a model's tidying (a blank line after an
  entry marker, seen in the real Tidy run) never make a unit "edited", and never make a paid conflict. A unit the merge keeps is the GM's text
  exactly, breaks included, so the review is not full of deleted `\page {fitted}` lines; after a merge that had fitted breaks the GM is told to
  fit pages again.
- **Per unit, by the code:** the campaign did not change it, the GM's; the GM did not, the campaign's; both made the same change, the GM's;
  otherwise a **conflict**. An entry new in the campaign goes in after the entry the campaign puts before it (or after the head); one the
  campaign no longer has (hidden or deleted) is offered for removal; one the GM deleted stays deleted unless the campaign changed it since,
  when it is offered back. The GM's order is kept.
- **Only a conflict goes to a model**, one call each, in turn, under the per-GM `BookGate` and one `BookBudget` capped at every call's worst
  case, with the server's requests set aside up front. The model gets the base and the campaign's version with every piece left out
  (`BookDraftEdits.Bare`) and the GM's version cut like an AI edit, pieces as markers. **The pieces are settled by the code**: matched by kind
  and order, the campaign's piece where the GM left it as it was, the GM's otherwise. When the GM's and the campaign's pieces do not line up
  (the campaign added a secret to an entry the GM rewrote), no model is asked. Page and column breaks are not lined up with anything (the
  generated versions have none, and after Fit pages nearly every entry holds one): they stay markers the model must keep, and come back
  where the GM had them. A review found that, before it was fixed, a fitted book's conflicts would almost all have skipped the model. The reply passes the AI edit's checks (`BookEditWorker`,
  one retry); the prompt says to keep the GM's wording, order and additions, apply every change of fact, and invent nothing.
- **A conflict with no answer is shown as the campaign's text** (no model, pieces that do not line up, a reply that failed its checks, or
  the GM pressing "Without AI"), which the GM accepts or rejects in the review: rejecting keeps their own. So Bring up to date works with no
  model at all, and one failed conflict does not sink the merge. With no conflicts nothing could be sent, and the review opens at once.
- **The pre-flight** (only when there are conflicts) says how many entries come from the campaign, stay the GM's, are new, are offered for
  removal or back, and conflict; how many of those go to which model, the token range and the cost. Merge with AI, Without AI, Cancel.
- **The review** is phase 4's overlay over the whole chapter. **Keeping it is not typing**: `take` ends the review without writing into the
  editor, the server saves the kept text **with the campaign's chapter as its new base** (`BookDraftStore.MergeAsync`; the previous text,
  base and key are remembered), and the editor then takes the saved text. So the notice goes, the next merge starts from here, and **Undo**
  (the existing button, now "Undo the last update") takes back text and base together, which brings the notice back; Ctrl+Z in the editor
  cannot undo the text while leaving the new base. The campaign is read again on save, never taken from the page: if it moved on since the
  merge was made, nothing is saved and the GM is told to bring it up to date again. Discard saves nothing. Partly accepted is still a merge:
  what was rejected stays as the GM's edit against the new base. The stale notice is hidden while a merge is open.

Tests: `BookDraftMergeTests` (lossless split, marker lines only, duplicates refused; the per-unit table; blank lines, trailing spaces and
breaks not edits, a kept unit keeping its breaks; new entries placed, gone ones offered for removal, a GM deletion kept or offered back,
the GM's order; a fitted conflict still sendable with its breaks back in place; eight spellings of a secret in all three versions never in the prompt, and the campaign's changed secret put back; a piece both
changed stays the GM's; pieces that do not line up keep the model out; a reply retried once, then left to the campaign's text);
`BookDraftTests.AMerge*` (no conflict: nothing sent, the campaign's and the GM's units, nothing saved until kept, an old version refused,
keeping moves the base, Undo brings back text and notice; a conflict alone sent, no secret, the campaign moving again refuses the save;
a player, an old version, nothing to bring in, another run, no model and a failing model each handled, the gate let go of);
`BookDraftE2ETests.TheGmBringsAnEditedChapterUpToDate...` (real browser, a fake model: two GM edits, the campaign changes one entry and adds
one; the pre-flight with the model and the cost and nothing sent; only the conflict sent, without its secret; Accept the rest; the notice
gone, the GM's other edit kept, the new entry in the editor and the preview; typing works; Undo brings the notice back). Mutants, each
failing them (15 of 15): breaks lined up as pieces (fails the fitted-conflict tests), the campaign's unit taken as the GM's, blank lines counted, the campaign's piece ignored, pieces not lined up,
gone entries kept, new entries at the end, GM deletions undone, a moved campaign saved, the merge not undoable, the base not moved, the gate
never let go of, the model's reply dropped, every entry sent; in the page, a kept review not saved. And phase 4's opener check now refuses
only a `{{note Title` the reply brought, not one the GM typed (tested, and its mutant fails).
Seen in the app (preview, a throwaway campaign as the real-run GM, deleted after): two GM edits, the campaign changing one and adding one;
the pre-flight named the GM's model, one conflict and the token range; Without AI opened the review with the new entry and the campaign's
line; Accept the rest kept the GM's other edit, added the entry, cleared the notice; Undo brought it back; Discard left the chapter as it was.
No model was sent anything.

**A real model, 2026-11-08** (the GM's own key, the provider's smallest current model, a throwaway campaign deleted after): the GM had
rewritten Vera's entry in their own words with a bold "Habits." paragraph; the campaign then changed "thirty winters" to "forty" and added
a limp. One conflict, one call, about 1,500 tokens, first try: the GM's wording and paragraph kept, "forty" and the limp put in where they
fit, the secret back as it was, nothing invented. Accept, then Keep: saved, the notice gone, the preview updated.

Not done, or not verified:
- One real merge, one model, English only; a conflict whose entry holds a fitted break has met only the test model.
- Entries are the unit: two edits of one entry by both sides are one conflict, however far apart in it; prose before the first entry is
  the head, one unit.
- An entry the GM moved into another chapter is, to the merge, deleted from one and unknown in the other.
- The Russian and Hebrew text of `Campaign.Book.Draft.Merge.*` is machine-drafted.

### 41.19 Phase 6: the party leak check (built 2026-11-08)

Download in the party edition first looks in the party draft for what only the GM edition has (`BookLeakCheck`, `BookDrafts.LeaksAsync`).
With a hit nothing is printed: the Problems tab opens on a list of each hit, with its chapter (a button that opens it), what it is, and its
first words, and **Download anyway** prints the book as it is. A warning, not a refusal (41.5). The GM edition is not checked.

- **What only the GM edition has is found by comparing the two editions' sources**, both read as the GM may read the campaign now, never by a
  visibility rule of the check's own (it would drift from the repositories', as `BookSourceReader` warns of a second filter):
  - a character the party source does not have (its name), or has under another name (a stranger: the real name, shown with the name they go
    by); the history of either;
  - a page, place, session or homebrew entry the party source does not have: its title and its text; a fact it does not have: its words;
  - every session's GM summary;
  - every secret block of the GM edition, as the generator prints it (mentions as the names the GM edition gives), and every `{{secret` block
    of the GM's own GM draft in that language, so a secret the GM typed into the GM book, which the campaign never had, is found too.
- **Anything the generated party draft says is allowed.** It is made from the party source alone, so a phrase in it is not a leak, even when a
  secret repeats it or a hidden character's name is a word the party's text uses. This is what keeps the check quiet on a draft nobody pasted
  into: the generated party draft has no hit (tested on the canary campaign).
- **Compared as words**: letters and digits, lower case, accents and Hebrew points dropped, anything else a gap. The generator's escapes,
  Markdown emphasis, line breaks and case do not hide a hit, and a name matches only whole words ("Ash" is not in "ashes"). A name or title
  is looked for whole; a secret or other text by **any six words in a row**, so half of one pasted is still found; a text of fewer than three
  words, or a name under three letters, is not looked for. The party draft's stylesheet is looked in too (a `content:` prints).
- One hit per thing and chapter; the first 20 are listed, the rest counted.

Tests: `BookLeakCheckTests` (a secret found escaped, re-cased, re-wrapped, with emphasis, or half of it; names whole words only; what the
party draft says never a hit, window by window; short ones not looked for; Hebrew points and Russian case; the stylesheet; the cap; secret
blocks read with their entry, nested blocks inside; the needles: a stranger by their real name and not their alias, a hidden character and
their history with mentions as names, a GM-only page's title and body, nothing the party has; a GM session summary, of a session the
party has too, and a planned session's title and recap; a secret in two drafts one needle);
`BookDraftTests.TheLeakCheck_...` (the canary campaign: the party draft as generated has no hit, a player gets nothing, and every canary
pasted in, escaped or not, is found with its kind, a GM-typed secret of the GM draft and the stylesheet included, and nothing public);
`BookDraftE2ETests.ASecretPastedIntoThePartyDraft_...` (real browser: a secret typed into Cast, Download straight after; the warning with
the chapter, "words of a secret in Vera" and the secret's words, on the Problems tab; nothing downloaded; the hit opens Cast; Download
anyway gives the PDF). Mutants, each
failing them (13 of 13): what the party draft says not subtracted, case kept, points kept, secrets matched only whole, a stranger's real name
not looked for, the GM draft's secrets not read, no text of GM-only things, short ones looked for, the list not capped, nested blocks ending
a secret, a stranger's history not looked for, a known character's history looked for (its public words reported), a GM session summary not looked for. One that survived
showed that taking the generator's markup out before comparing changed nothing, so the check does not.
Seen in the app (preview, a throwaway campaign deleted after): a known character with a GM secret, a hidden one and a stranger; a line typed
into the party Cast with the secret's words in other case, the hidden name and the stranger's real name, then Download straight away. The
Problems tab opened with three hits, each with Cast, what it is and its words (the secret named with Vera's entry); nothing about the
public text; Download anyway printed the PDF.

Not done, or not verified:
- A secret paraphrased, a name declined (Вера, Веру) or with a Hebrew prefix letter (לוורה) is not found; nor a secret translated.
- What the GM typed into the GM draft is looked for only inside its `{{secret` blocks: GM-only prose written outside one is the GM's to judge.
- A GM-only page's or character's picture pasted as an `art:` reference is not checked here; pictures still resolve only to the party's (41.5).
- Encounters are not looked for (their names are generic, and the whole of them is the GM's).
- A generic name is noisy: a hidden actor called "Goblin" or a planned session called "Session 6" is reported wherever the party draft
  says the word. It is a warning, with Download anyway; no filter is built.
- The Russian and Hebrew text of `Campaign.Book.Draft.Leak.*` is machine-drafted.
