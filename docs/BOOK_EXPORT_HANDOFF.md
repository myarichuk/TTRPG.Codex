# Campaign book export: handoff to the next session

Written at the end of a long session and updated in session 2 (2026-11-05). Read this first, then `docs/BOOK_EXPORT.md`. Sections 27-38 are
the newest; sections 1-26 are the design and earlier steps and are partly superseded (see "What to distrust" below). Session 1's branch was
merged into `main`. Work continues on **`book/next`**, which is local only and not pushed; see section 9 and `git log` for its tip. No pull
request should be opened unless the user asks.

## 1. What this feature is

TTRPG.Codex is a self-hosted .NET 10 / Blazor / RavenDB TTRPG companion (EN, RU, HE with RTL; bring-your-own-key AI). The feature exports a
campaign as a Homebrewery-styled PDF book:

campaign -> audience-masked source (`BookSourceReader`) -> `BookCompiler` (Markdown chapters) -> optional AI polish (`Ai/*`, stored build)
-> `BookComposer.PlaceArt` (pictures) -> `BookHtml` (self-contained page, CSP, theme) -> sandboxed preview iframe and PuppeteerSharp PDF.

Two editions: `dm` (GM; secrets boxed) and `party` (exactly what a player who owns no character may read; masked strangers, no hidden
characters, no DM-only pages, secrets removed *before* compile). The GM-only panel is `Components/Campaign/BookPanel.razor`, shown in Settings.
Routes: `BookEndpoints.cs` (`/campaigns/{id}/book/{edition}` and `/pdf`; query: `lang theme size cols pics ai`).

## 2. Where things are

| Area | Files |
|---|---|
| Masked reads | `src/Codex.Persistence/Book/BookSource*.cs` (the only input of the compiler; its constructor is internal) |
| Compiler | `Persistence/Book/BookCompiler.cs` (chapters; `BookEntryPosition` records each entry heading's line; `ArtAnchors`) |
| Renderer | `Core/Book/BookMarkdown*.cs` (own Markdown subset; escaped by default; no URLs; `{{block:N}}`; `asset:`/`slot:` images) |
| Page, themes, CSS | `Core/Book/BookHtml.cs`, `BookTheme.cs` (Plain, Parchment; English only by the language gate), `BookMasks.cs` (torn-edge SVG) |
| PDF | `Web/Book/BookPdf.cs`, `BookBrowser.cs` (finds Chrome/Edge/Chromium; `CODEX_BOOK_BROWSER`; one job at a time) |
| Composition | `Web/Book/BookComposer.cs` (labels, `Items`, `Overlay` of a stored rewrite, `PlaceArt`) |
| AI layer | `Core/Book/Ai/*` (blocks, checks, section worker, runner, build key), `Core/Book/BookBudget.cs`, `BookTokens.cs`, `Web/Book/BookBuilds.cs` (stored build, background runs), `BookModels.cs`, `Persistence/Book/BookBuildModels.cs` |
| Pictures | `Persistence/Book/BookArt.cs` (reader, caps), `Core/Book/BookImages.cs` (structural sanitizer, never decodes), `BookImageScaler.cs`, `BookImageWorker.cs` |
| Book art slots | `Persistence/Book/BookSlots.cs` (slots and keys, `Choose`), `BookArtStore.cs` (`bookart/{campaign}`, tagged party/dm), `Web/Book/BookArtService.cs` (upload checked against the edition's slots), `Components/Campaign/BookArtTray.razor` |
| Art director | `Core/Book/Ai/BookArtDirector.cs` (prompt, reply reading, per-field checks), `Web/Book/BookArtOutline.cs`, `BookArtDirection.cs` (plan, pre-flight, run), `BookGate.cs` |
| Drawn-art prompt | `Core/Book/BookArtStyle.cs` (style vocabulary, `BookImagePrompt.Assemble` and its checks) |
| Drawing | `Web/Book/BookArtDrawing.cs` (picks checked again, run), `BookImages.cs` (picture model per run, reserved allowance), image price in `AiSettings.razor` and `AiProviderService` |
| Decoder process | `src/Codex.ImageWorker` (only caller of SkiaSharp; talks stdin/stdout via `BookImageWire`) |
| Strings | `Localization/Resources/Strings.Campaign{,.ru,.he}.resx`, keys `Campaign.Book.*` |
| Website | `site/` (see `site/README.md`), guide section `#book`, tour row, screenshots `book-panel` and `book-pages`, tools `capture-screenshots.mjs`, `demo-art.py`, `book-pages.py` |
| Tests | `tests/Codex.Tests/Book*Tests.cs`, `E2E/BookRouteE2ETests.cs` |

## 3. State: built, verified, and not

Built: everything in `docs/BOOK_EXPORT.md` sections 10-38. Sections 34-38 (book art slots and the GM's own pictures, the style vocabulary
and prompt, the art director, wiki links, drawing) are on `book/next`, not pushed. The rest: masked reads, renderer, plain and parchment themes, PDF with TOC page numbers,
panel, AI layer steps 1-5 (prices, budget, estimator, section workers, checks, stored build, pre-flight, live progress), two columns, existing pictures
placed by recorded line, re-encoding/downscaling, out-of-process decoding, website.

Verified: unit and integration tests (759 book, localization, AI, backup, campaign and wiki tests in session 2 at step 6, one failure in
`WikiSecretsE2ETests` that passed on two reruns, see section 38; all 28 e2e tests passed after it), the book e2e tests (9) at the commit that added
the worker, a real-browser look at columns, drop caps, picture placement, panel and pages, a real `dotnet publish` containing the worker and a
worker run from that directory, and one run against a real model (a small Anthropic model, section 26, short chapters only).

**Not verified, say so if you rely on any of it:**
- Drawing (section 38) has only been run against a fake picture server, never a real picture model; Regenerate/Accept is not built.
- The art director (section 36) has only been run against scripted replies, never a real model. The tray's director section has only been
  drawn in a browser in its no-model state: the pre-flight, Suggest, progress and suggestion lines have not been looked at.
- The full e2e suite has not been rerun since step 5 of the AI layer; only the book e2e tests were.
- No real-model run of anything after section 26: the stored build, the panel's AI controls and cost display were exercised only with a scripted fake
  chat client and an in-process fake OpenAI server.
- The Russian and Hebrew strings (`Campaign.Book.*`) and the RU/HE website text are written by the model, never read by a native speaker.
- Windows and macOS hosts (browser discovery, worker start, native Skia), snap/flatpak browsers, Letter paper with pictures, RTL with pictures
  or columns (the themes are English only, so RTL books cannot be produced yet).
- Worker crash handling is tested with `sh` stand-ins that exit or hang, not with a real malformed file.
- Chromium's memory and time with many pictures; only byte sizes were measured.

## 4. Invariants that must never break (the review checklist starts here)

1. **Audience masking.** The party book is built from the party source only; nothing from the DM source or a DM art map may reach it. Placement
   re-checks the picture flag from the audience's own source (`BookCompiler.ArtAnchors`). A `[[Page]]` link to a page the party source does
   not hold prints only its own `|shown` words, never the title (`BookCompiler.PageLinks`). Canary tests use strings like `CANARY_*` and `PUBLIC_*`.
2. **Secrets never reach a model.** `BookWork.Prepare` cuts `:::dm` and `:::for` into `{{block:N}}`; boxes are rendered from the current campaign at
   print time. A stored build holds no secret text (`BookBuildSection.Blocks` was removed on purpose). Mentions are read the way the renderer reads
   them, fail closed (`BookBlocks.Mention`).
3. **No URL in a page.** CSP is `default-src 'none'; img-src data:`; only a `data:image/` URI with no quote, bracket or space is ever written to an
   attribute (renderer and cover).
4. **A stored rewrite prints only while its key matches** the key recomputed from the campaign as read now (`BookBuildKey`); the key covers recipe,
   prompt and checks versions, the rules text as sent, edition, language, style, chapter and block bodies. Model and temperature are deliberately out.
5. **A failure never replaces a paid rewrite; money is reserved at the worst case** (`BookBudget.WorstCase`) and settled on real usage.
6. **One paid run per GM**: chapter builds and director runs take the same `BookGate` before any await.
7. **A model never sees a picture and cannot move or drop one**; `PlaceArt` runs after `Overlay`.
8. **The decoder is not in the server process.** Any worker failure returns "keep the picture"; the server never throws on it.
9. **The party book places only pictures made for the party, in the party's own slots.** The tag is checked in three places (`BookSlots.Choose`,
   the tag check in `BookArtReader`, and `BookArt.Slot(key, audience)`), and an upload's key must be one of the slots that edition's source makes
   (`BookArtService`). A stranger's slot is `mask:<id>`, never the actor's.
10. **A suggestion, and anything drawn from it, carries the tag of the edition whose source was read** (`BookArtDirection` passes only
   `p.Tag`), and each tray shows only its own edition's suggestions. The director is shown only `BookArtOutline` (cut after `BookWork.Prepare`
   of the whole chapter), under per-request numbers, never slot keys. Chapter builds and director runs share one per-GM `BookGate`.
11. **Nothing is drawn but what the GM ticked, and every pick is checked again on the server** (`BookArtDrawing.PrepareAsync`). The slot
   must be the edition's own, with the edition's own current suggestion, and the description must pass the checks. The picture is sanitised,
   stored under the edition's tag, and counted once against pictures set aside up front (`IBookImages`).

## 5. What to distrust

- `docs/BOOK_EXPORT.md` sections 1-26 predate several decisions (for example the plan's phasing, the "no image library on purpose" claim in
  `BookImages.cs` comments is now only true of the sanitizer). Sections 27-33 are current but still written by the same author as the code:
  one claim in section 31 ("a DM can upload only into their own campaign") was **wrong** and was found by an independent review. Verify claims
  against the code.
- Subagent reports (including earlier "Opus" reviews) are model output, not authority; two earlier findings needed re-checking and one test they
  suggested was vacuous. Reproduce a finding before fixing it.
- Test names that say "mutation": the mutants were run by hand from backup copies and are not in the repo. Re-run them if you touch that code.

## 6. Review instructions for the next session

Do these before building anything new. Report findings with file:line, a concrete failing input, and whether you reproduced it.

**A. Run what exists**
```bash
dotnet build src/Codex.Web
dotnet test tests/Codex.Tests --filter "(FullyQualifiedName~Book|FullyQualifiedName~Localization)"        # includes the 9 book e2e tests
```
The full suite is large; run it in two halves with namespace-prefix filters (A-M, N-Z) plus `&FullyQualifiedName!~E2E`, then the e2e suite
separately. Playwright and Chromium are preinstalled (`/opt/pw-browsers`); do not run `playwright install`.

**B. Mutation checks (this is the most useful review; do it with backup copies, never `git checkout` an uncommitted file)**
For each, copy the file, apply the change, run the named tests, expect a failure, restore from the copy.
- `BookComposer.PlaceArt`: ignore `inFence`; place by first text match instead of the recorded `Line`; accept ambiguous headings in a rewritten chapter;
  write trimmed lines back (hard breaks).
- `BookCompiler.ArtAnchors`: drop the `HasPortrait`/`HasArt` flag checks (test: `ArtReadForTheDm_NeverReachesAPartyDocument...`).
- `BookImageScaler`: remove the "already small" shortcut or widen 1.15; stop caching "keep"; skip the sRGB conversion; always JPEG.
- `BookImageWorkerPool`: do not kill on failure/timeout/cancel (tests report `Alive`); rethrow instead of returning null.
- `BookBlocks.Mention` and `BookSectionWorker`: break the mention parity with the renderer; accept a length-cut reply; let a failed retry overwrite a refined chapter.
- `BookBuildKey`: drop the rules text or checks version from the key.
- `BookBuilds`: take the busy slot after an await (test: 6 parallel starts).
- Book art (`BookArtTests.Slots.cs`): let a GM picture through in `BookSlots.Choose`, in the reader's tag check, or in `BookArt.Slot`; key a
  stranger as `actor:`; skip the slot check in `BookArtService.UploadAsync`; place a header without checking the title line. All six were caught in
  session 2.
- Art director (`BookArtDirectorTests.cs`): outline without `Prepare`; a slot number that was not sent accepted; visual check skipped; mood as
  free text; tag not from the source; a gate of its own; tray showing the other edition's suggestions. All seven were caught in session 2.
Anything that survives is a missing test; add it.

**C. Questions to answer by reading code, not docs**
1. Can any path put a picture, name or heading the party may not see into the party book? Try: a rewritten chapter that reorders same-named entries;
   a masked stranger whose public name equals a known character's name; a wiki page whose body contains `## <other page title>`; `![](asset:aN)` in a
   model-written chapter (the checks refuse `![`, confirm that is applied on the AI path, `BookChecks.cs`).
2. Does `BookImageWire` bound every length it reads on both sides? Can a worker reply (or a worker that is not ours) make the server allocate
   unboundedly? `BookImageWorkerLaunch.Beside` runs a file found beside the server through `dotnet`: is that path trustworthy on every install layout
   (launcher bundle, `dotnet run`, tests)?
3. `BookBuilds`: every exit path of a run (error, cancel, budget, process stop) must add totals once and release `_busy`. `ApplicationStopping` does not
   cancel runs or flush totals (known); decide whether that matters.
4. The preview and the PDF must be the same page. `BookArtReader.MaxTotalBytes` counts scaled bytes, and preview files are ~6x smaller than print files,
   so near the cap the preview can show pictures the PDF drops (known, section 31.1 item 4). Reproduce it.
5. CSS in multi-column print: `h1 + p::first-letter` drop cap with a first paragraph that begins with a digit, quote, inline element or non-Latin letter;
   `.secret` boxes and tables taller than a column; headings at the bottom of a column; one-column place art width (the scaler assumes 88 mm in two
   columns, 172 mm in one).
6. Localization: `Strings.Campaign*.resx` keys exist in all three files; the localization tests enforce it, but read the RU/HE wording for sense, and
   check that "GM" (not "DM") is used in user-facing strings (a project-wide change made by another commit; the site guide still says "DM" in prose,
   which is an existing inconsistency).
7. Things I did not look at: `BookPdf` temp-profile cleanup under crash, the panel on a phone and in light theme, the panel in RTL, accessibility of the
   panel controls, memory of base64 pictures in the preview iframe with 100+ images.

**D. Housekeeping to check**
- `git status` clean; nothing under `site/.capture*`, `/tmp` scratch, or any API key is committed. The Anthropic key pasted into an earlier
  conversation was a three-hour key and has been revoked (user, session 2). Still: never write a key to a file.
- No model identifier appears in any committed artifact (code, docs, commit messages, PR text); this is an explicit rule.

## 7. Open work, in the order I would take it (the user decides)

1. **AI-suggested art** (`docs/BOOK_EXPORT.md` 28.3, steps 3-7). **Steps 3-6 are done (sections 34-36, 38); 7 remains, and step 6's
   Regenerate/Accept (keep 3).** Next: one run against a real text and picture model (the user offered short-lived keys, entered by them in AI settings). DM upload into a slot; style vocabulary and a code-assembled image prompt (the model never
   writes the prompt; deny proper nouns and "in the style of"); the art-director call per chapter (masked outline in, `{depicts, visual, mood}` out);
   per-slot generation with cost shown first and nothing drawn without a click; suggested spot and scene slots. Audience-tag every image
   (`BuiltFrom: party|dm`); the party book places only party-built images. The user asked for painterly/linework style choices.
2. **Decision pending from the user: prose or tidied notes.** The rules say "invent nothing", so the model returns text close to the source. Real
   book prose needs pinned facts (names, numbers) separated from open texture, a coverage check (a rewrite can still drop most of a chapter; the 0.12x
   length floor is untuned) and splitting the unit from chapter to `##` entry (a long Cast chapter is re-bought whole after one NPC edit).
3. **Russian and Hebrew books**: both themes are English-only. Needs proven coverage (the bundled Forum, Assistant and Frank Ruhl Libre have one
   weight, no bold, no italic) and a preflight.
4. Review leftovers: per-user decoder fairness (the queue of two is global and the PDF slot is held while art is read); preview/print cap mismatch and
   showing skipped pictures in the panel; a per-entry marker is NOT needed (section 32 chose position over markers).
5. Layout and format: stat-block and table blocks, running footer with the chapter name, picture-frame masks, paper fibre on the page edge, `.md` export with
   a GM-edition warning, custom CSS editor, the Ashen Vellum theme (measured in section 13, not built), `:::for` per-player editions.
6. ~~The `PartyPreview` empty-user-id bug~~ fixed (session 2): `CampaignAccess.PartyPreview()` now reads as `CampaignAccess.Nobody`, a made-up id per
   preview, the same one the book reader uses; test `PartyPreview_OwnsNoActor_EvenOneStoredWithAnEmptyOwner`, mutation-checked.
7. ~~A wiki link to a GM-only page prints its title in the party book and outline~~ fixed (BOOK_EXPORT section 37). The app's own wiki had the
   same leak; fixed with `WikiText.ForReader` (same section).
   **Step 6 rule from this:** party suggestions stored before this fix may hold such a title; they now show as stale. Draw only from a suggestion
   whose inputs are current.
8. Allowance refund after UTC midnight is subtracted from the new day; transport retries are not in the money range; totals are lost on a process crash. All are in section 28.

## 8. Practical notes

- Conventions: commit and push often on the branch; commit messages end with the attribution lines the harness gives; no PRs unless asked; the Stop hook
  forces a commit and push of uncommitted work. Tone in discussing code with this user: thorough, technical, blunt about what is unverified.
- Tests: `ScriptedChat` is the fake `IChatClient`; canary strings `CANARY_*` (must never appear) and `PUBLIC_*` (must appear); e2e uses an in-test
  `HttpListener` fake OpenAI server and the real AI settings UI.
- Running the app for screenshots: use a `dotnet publish` output, not `dotnet run --no-build` (stale static-asset manifest), with
  `Codex__DataDirectory` fresh each time, `Codex__EnableTestSeedEndpoint=true`, `Codex__ContentDirectories__0=<repo>/content`,
  `Codex__Book__Pdf__Browser=/opt/pw-browsers/chromium`, `ASPNETCORE_URLS=http://127.0.0.1:5199`. Then
  `node site/tools/capture-screenshots.mjs --base http://127.0.0.1:5199 --langs en` (needs `cd site && npm ci`). To add shots without churning the
  others, encode only the new PNGs with `site/tools/optimize-screenshots.py` into a temp directory and merge `manifest.json` by hand.
- Serena (an MCP code-navigation server) was used and disconnected repeatedly; it is optional.
- Scratch files lived under `/tmp/claude-0/...` and are gone with the container; nothing needed is only there.

## 9. Session 2 (2026-11-05, macOS host): review results

Branch `claude/nice-brahmagupta-3ugesy` was merged into `main` (pull requests 289 and 290); work continues on `book/next`.

- **A. Run what exists**: build clean; book and localization tests 378 of 379 on the first run. The one failure,
  `BookPdfTests.AJobThatCannotFinishInTimeIsStopped...`, also fails on clean `main` on macOS: Chrome's font there sets "fi" as one ligature glyph,
  so the PDF's text held `ﬁne`. The test now compares NFKC-normalized words. Watch for the same in any new PDF text assertion.
- Fixed: the `PartyPreview` empty id (open work 6); `BookBuilds.ExecuteAsync` did the build document's preparation before its `try`, so a throw
  there would have left the run "Running" forever, the model handle open and the GM's build slot taken; it is inside the `try` now.
- Housekeeping: `src/Codex.Web/RavenServer/Logs/server.log` was tracked (since an old HTTPS commit) and changed on every run; untracked and
  ignored. Model identifiers removed from `docs/` and the `BookTokens` comment (they stay where code needs them: the AI client factory,
  settings defaults, the price table and their tests; old commit messages cannot be changed).
- C1 read: `BookChecks.Validate` (refuses `![`, `[[`, `{{`, tags, URLs, markers) is applied on the AI path in `BookSectionWorker`; the renderer
  needs `![` adjacent, so the check and the renderer agree. A deterministic wiki body may contain `![x](asset:aN)`; the asset map is the
  audience's own, so at worst it shows another picture that audience may see (wrong place, not a leak).
- C2 read: `BookImageWire` caps a picture at 64 MiB and the content type at 64 bytes on both directions.
- macOS: the PDF tests find the installed Chrome; Playwright browsers are not at `/opt/pw-browsers` here.
- Test runs leave RavenDB data in `$TMPDIR/TestRavenData_*` (18 GB found at the start); clean after each run.
