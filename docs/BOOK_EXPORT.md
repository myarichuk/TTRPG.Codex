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
  no `@import`, `@font-face`, `url()` other than `data:image/`, `expression`, `behavior`, `image-set`, no `<`, 20 KB cap.
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

## 26. First run against a real model (claude-haiku-5-5), and what it changed

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
budget overflow. **Not checked by it, and still open**: a wiki link `[[Some Page]]` to a DM-only page is passed to the model (and printed by the
deterministic book) as the link text.

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
and 120 dpi for the preview (`BookArtQuality`; the route chooses by `forPrint`). Boxes: cover 140x165 mm (contained), portrait and homebrew 62x52 mm,
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
1. **Anchoring is still by heading text.** A body line `## Vera` inside an earlier entry (a wiki page "History" with a `## Vera` subsection) still takes Vera's picture,
   and the real heading gets none. Always a picture the party may see, so a misplacement, not a leak. A proper fix needs a per-entry marker emitted by `Compile`.
2. **The decoder runs on player-uploaded portraits**, in-process (see the correction above). Options: decode out of process, scale only DM-uploaded media, or decode in the printing browser.
3. **One global decoder queue of two**, shared by all users: one DM's first build of many large pictures delays everyone's preview and PDF, and the PDF slot is held meanwhile.
   Needs a per-user limit or a time budget that falls back to unscaled bytes.
4. **Preview and print can differ**: the 64 MB cap counts scaled bytes and preview files are about six times smaller, so a book of roughly 11-64 MB of print-size art
   shows every picture in the preview and drops some from the PDF ("limit"). The skips are not shown in the panel.
5. Animated WebP/APNG that need scaling lose all but one frame (untested which); the cache's byte count can drift by one entry under a race; the column and drop-cap tests are
   string checks on the CSS, not on a render.
