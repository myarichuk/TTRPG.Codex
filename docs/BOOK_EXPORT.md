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
- **PDF engine: undecided, see 8.** Playwright's driver bundles a Node runtime per platform (tens of MB each, so
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

- Markdown engine: vendor `marked-hbfm` (MIT, exact dialect, JS in page) or write our own .NET subset renderer? Note the
  closed-network rule (JS off in the print browser) favours a server-side .NET renderer or running marked-hbfm in a
  server-side JS step, not in the print page.
- PDF engine: Playwright vs PuppeteerSharp vs WebView2 (see 3). Playwright is attractive only because e2e already uses it; the
  review found its shipped-product costs larger than assumed.
- Prices: the Anthropic client exists (`AIClientFactory`), so "OpenAI-compatible" is not the whole story for usage reporting.
- Installer impact and signing if we ever bundle Chromium instead of using an installed browser.
- Headless launch under root/containers (`--no-sandbox`), Windows/macOS channel discovery.
- Parallelism of section calls vs provider rate limits and the server allowance.
- Where a build's state lives (a `BookBuildDocument` like `WorldChatDocument`?) and whether it is part of the campaign
  export (the world chats are not).
