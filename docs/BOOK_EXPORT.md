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
CampaignBundle (snapshot)
  -> audience filter (SecretText, per-actor visibility, Retired, Hidden)
  -> BookPlan   (outline: ordered typed sections, each with its source ids)
  -> Sections   (deterministic Markdown + optional AI-refined Markdown, per section)
  -> Slots      (image slots resolved: existing art -> upload -> AI image -> masked placeholder)
  -> HTML + CSS (our renderer, Homebrewery-flavoured Markdown dialect)
  -> live preview (iframe) ... and finally ... PDF (headless Chromium via Playwright)
```

### 2.1 Why not one long chat

`WorldChat` resends history on each round (it stubs old tool results to limit growth). A 40-page book in one
conversation makes cost unpredictable and failure expensive. Instead:

- **Planner**: a short conversation producing the outline; the DM steers it.
- **Section workers**: one call per section, fresh context = that section's source material + a style sheet. Bounded
  input, independent retries, parallelisable (subject to provider limits).
- **Content hash per section** (inputs + style + model + prompt version): an unchanged section is not regenerated.

### 2.2 Fact tokens

Source facts that must survive a refine are replaced by tokens before the call (`⟦stat:actor-id⟧`, `⟦ref:place-id⟧`,
`⟦n:15⟧`-style for bare numbers in prose where practical) and substituted back afterwards. The validator rejects output
that drops, duplicates or invents a token, or contains `:::` marker lines, `{{` unbalanced, or raw HTML outside an
allow-list. A rejected section is retried once with the error shown to the model (as `JsonAsync` does), then falls back to
the deterministic Markdown for that section and says so in the UI.

## 3. Rendering

- **Dialect**: a Homebrewery-compatible subset we implement ourselves: `\page`, `\column`, `{{class,...}}` blocks,
  `{{note}}`, `{{monster,frame}}`, `{{wide}}`, `{{toc}}`, `{{cover}}`, `{{pageNumber,auto}}`. The `.md` download is pasteable
  into homebrewery.naturalcrit.com; the PDF is rendered by us. We do **not** claim pixel parity with Homebrewery.
- **Live preview** is plain HTML+CSS in an iframe, re-rendered as sections land. No browser automation involved.
- **PDF** uses Playwright (`Microsoft.Playwright`), launched through, in order: installed Edge (`Channel="msedge"`),
  installed Chrome (`Channel="chrome"`), then a one-click, size-labelled download of Playwright's Chromium (consent
  required; the app otherwise fetches nothing). Print via `page.PdfAsync` with `PreferCSSPageSize`.
- **Fonts**: the app already bundles Cinzel, Alegreya Sans, Forum, Frank Ruhl Libre and Assistant (all OFL; see LEGAL.md),
  which cover Latin, Cyrillic and Hebrew. The book uses those so it works in all three UI languages including RTL.
- **Masks/ornaments**: see §6 (licence-driven; to be decided after the licence review).

## 4. Illustration slots

A **slot** = `{ id, purpose, aspect, mask, subject ref, state }`. The AI declares slots while writing; it does not draw.
Resolution order, first hit wins, automatically:

1. Art that already exists for the subject: actor portrait, place picture, wiki art, pack art, campaign cover.
2. A DM upload into the slot.
3. AI image generation, when an image provider is set (separate per-user image provider list already exists).
4. A placeholder: mask-shaped, labelled with the purpose.

Requests are **non-blocking**: the AI keeps writing; unresolved slots collect in a tray ("I'd like art for: Geography
divider, Captain Vale, the Drowned Chapel"). Filling one re-renders only the pages it touches. Generation of images is
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

TTRPG.Codex is PolyForm Noncommercial 1.0.0. Anything vendored from Homebrewery (CSS, masks, images, fonts) needs a licence
compatible with redistribution inside that and an entry in LEGAL.md. Pending: Homebrewery's repository licence and the
per-asset licences of its backgrounds, frames and fonts. Fallback: our own CSS and our own SVG masks, which removes the
question.

SRD content in a book needs the CC-BY-4.0 attribution (`LEGAL.md`), added automatically as a final page.

## 7. Phasing

1. Deterministic book -> HTML -> PDF, slots filled from existing art, placeholders otherwise. No AI.
2. Pre-flight/meter/cap, section workers with fact tokens, live preview, price fields.
3. Planner conversation and the slot tray; AI image generation per slot.

## 8. Open questions

- Installer impact and signing if we ever bundle Chromium instead of using an installed browser.
- Headless launch under root/containers (`--no-sandbox`), Windows/macOS channel discovery.
- Parallelism of section calls vs provider rate limits and the server allowance.
- Where a build's state lives (a `BookBuildDocument` like `WorldChatDocument`?) and whether it is part of the campaign
  export (the world chats are not).
