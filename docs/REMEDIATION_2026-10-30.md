# TTRPG.Codex — Remediation plan of 2026-10-30

*For the review of `main` @ `e4834d1` (the 52 commits `c7016ca..e4834d1`: wiki secrets, world-builder chat,
templates and editor shortcuts, AI plumbing, cast and rules, the website). At review time `dotnet build` gave 0
errors and `dotnet test` without E2E passed 900/900. Line numbers are from `e4834d1`. Items marked **(✓)** were
re-read in the code after the area review; the rest are the area reviewer's reading and get a failing test first.*

Every fix lands with the test that would have caught it. "Done" for a phase means its tests are in and green, and
E2E is green for the screens it touches.

---

## Phase 1 — Secrets that reach players (P0)

**1.1 Strip DM blocks from fact Details on the server (✓).** `RavenFactRepository.GetVisibleForCampaignAsync`
(`RavenFactRepository.cs:20`) gives players the raw `Details`. `RelatedEntityIds` is also built from the full text
(`:42`), so a public fact shows under "Lore about" for an actor named only inside a `:::dm` block, and Lore search
(`LoreSection.razor:293`) matches words inside DM blocks.
- For non-DM access, set `Details = SecretText.ForPlayersMarked(Details, ownedActorIds)` and recompute
  `RelatedEntityIds = FactLinks.Of(Summary, Details)` on the returned copy. Mirror `RavenWikiRepository.RedactForPlayer`,
  including the `ConditionalWeakTable` guard, so a redacted copy is never saved back.
- Search and `LoreAbout` then work on redacted text with no change of their own.
- Tests:
  - A repository test: a public fact with `:::dm [[actor:x]] :::` returns no DM text and no `actor:x` to a player.
  - A `FactLinks.About` test: that fact is not "about" x for a player.

**1.2 Refuse AI edits that drop a secret block (✓).** `WorldBuilderService.ApplyEditAsync` (`:851`) replaces the whole
text, and nothing checks `:::dm` blocks.
- In `CheckAsync` (`:328`), for each edit compare `SecretText.SecretBlocks(before)` with the edited text. Any block
  that is missing or changed in audience is a check **error** naming the block's first words. The model then has to
  re-propose (same loop as the existing check errors).
- Run the same comparison again in `ApplyEditAsync` against the stored text. It is defence in depth: a refused edit
  throws the same `EditChanged`-style localized error.
- `WorldProposalPanel`: render marker lines (`:::dm`, `:::for`, `:::party`, `:::`) in the diff as whole-line tokens
  with the secret styling, not as single struck-out words.
- Tests: check rejects an edit that drops, moves out of, or re-audiences a block. Apply refuses even if the check was
  bypassed. An edit that keeps blocks verbatim passes.

**1.3 Bare `\r` line endings.** `SecretText.cs:391` splits on `\n` only. Normalise `\r\n` and lone `\r` to `\n` at
the top of `Parse` (and wherever bodies are imported). Extend the randomized test in `SecretTextTests.cs:304` with
`\r`.

**1.4 `:::for` reveals moving to a same-named character (plausible).** Editing turns `[[actor:id]]` into `@Name`
(`WikiSection.razor:736`), and the round trip back (`:1031`, `SessionMentions.cs:132-150`) matches by name.
- Write the failing test first: two PCs named "Bram", an unrelated edit, check the `:::for` id survives.
- Fix: inside `:::for` opener lines, keep the `[[actor:id]]` form in the editor (it is a DM-only line anyway). In body
  text, when a name is ambiguous, prefer the id that was at that position in the stored text.

**1.5 Hover cards under "view as players" (low, DM-only).** Have the lens add `?as=players` to `/preview/*`
(`PreviewEndpoints.cs:68,83,97`). For a DM, the endpoint then renders with the same player access the lens uses.
Test: DM text is absent from a lens preview.

**1.6 Small leftovers.**
- "Revealed to me" filter is always empty (`LoreSection.razor:320`): list blocks whose `:::party` stamp came from a
  `:::for` naming one of the reader's characters, or drop the option.
- Player search matching `:::party` stamp lines (`WikiSection.razor:673`): search the plain text with markers removed.

## Phase 2 — Saves from stale copies (P0/P1)

One cause behind several bugs: a whole document is written back from a copy loaded earlier, with no version check, so
the last writer wins. The codebase already has the pattern for this: `RavenUpdate.RunAsync` (audit O1), which loads
fresh, applies a small change, and saves under optimistic concurrency. Each item below moves onto that pattern.
Where a whole document really must be replaced, it gets an expected-version check: add
`RavenUpdate.TryReplaceAsync(id, expectedChangeVector, …)`, which returns "changed since you loaded it".

**2.1 World chat (✓).** `RavenWorldChatRepository.TrySaveAsync` (`WorldChatModels.cs:251`) evicts and stores
blindly, and each tab holds its own `WorldChat`.
- Keep the change vector from the load on `WorldChatDocument` (not persisted). Save through `TryReplaceAsync`. On
  conflict, reload, tell the DM "this chat changed in another tab; it has been reloaded", and drop the stale in-memory
  proposal.
- **Make apply resumable and idempotent.** After each entity is stored, persist that draft's `SavedAs` immediately
  through `RavenUpdate` (a field patch, not a whole save). Store `AppliedEpoch` once apply finishes. `ApplyAsync`
  refuses an epoch that is already applied and skips drafts with a stored `SavedAs`.
- A deleted chat must not come back: `TryReplaceAsync` on a missing document returns "gone", and the tab closes the
  chat.

**2.2 Fresh proposal after a partial apply (✓).** `ProposeAsync(fresh: true)` (`WorldChat.cs:705`) drops `SavedAs`.
Carry `SavedAs` over by `Ref` and `Kind`, exactly as the `add_entries` merge does (`:676-690`). Factor that merge
into one helper used by both.

**2.3 `add_entries` resets the lost-update guard.** The merge re-runs `FillEditsAsync`, which takes a new
`BeforeStamp` and `Before`. Keep the original stamp for edits already in the proposal; only new edits get a fresh
one.

**2.4 Promote to cast (✓).** `CastService.PromotePageAsync` (`CastService.cs:185-187`) saves the whole page from the
dialog's copy and ignores the result.
- Add `IWikiRepository.TryLinkActorAsync(pageId, actorId, kind, access)`, a `RavenUpdate` patch of `Kind` and
  `ActorId` only.
- If it fails, delete the just-created actor and return a localized error, so a retry cannot make a duplicate NPC.

**2.5 Fact saves overwrite `KnownBy`.** `RavenFactRepository.SaveAsync` (`:52`) copies `KnownBy` and `Visibility`
wholesale. Edits from the fact editor should change Summary, Details and Metadata only. Knowers and visibility change
only through `AddKnowerAsync`/`RemoveKnowerAsync` and an explicit visibility setter, all through `RavenUpdate`.

**2.6 A player's save loses a block the DM just revealed (✓).** `SecretText.KeepSecrets` (`SecretText.cs:337`) keeps
only the blocks the player can't read *now*.
- Recommended: the player's editor keeps the change vector it loaded. `RavenActorRepository` (`:182`) saves player
  edits with `TryReplaceAsync`. If the DM changed the history meanwhile, refuse with "the DM changed this while you
  were editing; your text is kept below, reload to merge".
- Also keep revealed blocks' `:::party` / `:::players` markers in place on a player save, rather than flattening
  them to plain text and moving DM blocks to the end. Otherwise the DM can no longer hide what was revealed.
- **Decision needed** (see end): refuse-on-conflict (simple, safe) vs. a three-way merge of the player-visible
  segments (nicer, much more work).

**Tests for phase 2** (Raven test store):
- Two `WorldChat` instances on one document: the second apply is refused, nothing is duplicated, and history isn't
  lost.
- Apply fails on entry 4, then a fresh `propose_changes`, then apply: entries 1–3 are not written twice.
- An `add_entries` after an outside edit still fails the stamp check.
- Promote races a reveal: the reveal survives, and a failed link leaves no orphan NPC.
- A fact edit racing `AddKnowerAsync` keeps the knower.
- A player save racing a DM reveal is refused, and the block survives.

## Phase 3 — Editor and keyboard (P1)

**3.1 Ctrl+S (✓)** (`app-startup.js:678`).
- **Placeholder names.** Before `button.click()`, end any active template inside the button's scope. Expose
  `codexTemplates.stopWithin(scopeEl)` and call it. Do not rely on the form `submit` listener (`:1058`): the
  `OnClick` Save buttons on `ActorSheet.razor:142` and `SessionDetail.razor:134` are not in forms.
- **Non-Latin layouts.** Match `e.code === "KeyS"` (and `"KeyH"` for Ctrl+Shift+H at `:658`), not `e.key`.
- **Repeats.** Return early on `e.repeat`. Mark the button `data-saving` until the next Blazor render clears it. On
  the server, give `WikiSection.SaveAsync`, `TemplatesSection.SaveAsync`, `ActorSheet.SaveAsync` and
  `SessionDetail` save a `saving` guard, so a second click while the first awaits is a no-op. Set `template.Id`
  before the await.
- Tests (E2E): Ctrl+S with a template active on the sheet biography and on the session summary saves no placeholder
  names. A held Ctrl+S on a new template creates one template. A unit-level JS test that `KeyS` with `key: "ы"`
  saves.

**3.2 Template keyboard.**
- Shift+Tab on the first field ends the template and moves focus backwards (`app-startup.js:1073`).
- **Decision needed:** Tab after a word that equals an abbreviation (e.g. "npc" in prose) currently expands. Options:
  (a) expand only when the abbreviation is the whole line or follows a line start or list marker; (b) expand on Tab
  but let Escape immediately after undo it back to the word plus a Tab-move; (c) a distinct trigger (e.g. `;npc`).
  Recommended: (a) + (b).

**3.3 One template grammar, two parsers.**
- Use `Object.hasOwn(defaults, name)` at `app-startup.js:744`.
- Add a shared fixture file (`tests/fixtures/templates.json`: input → tokens and fields). xUnit runs it against
  `LiveTemplate`, and a small Node test (or the existing Playwright fixture evaluating the module) runs it against the
  JS parser. This catches the two drifting apart, which today only a comment guards against.

**3.4 `TextDiff` performance.** `TextDiff.cs:108` concatenates strings per token (quadratic), and the DP table is up
to 8 MB.
- Accumulate with `StringBuilder` per piece. Trim the common prefix and suffix before the DP.
- Above a size cap, fall back to a line-level diff.
- In `WorldProposalPanel.razor:500`, compute the diff once per proposal epoch and cache it, not per render.
- Make the "Current" view render `flatBefore`, the text the diff actually compares.
- Test: a one-word change in a 100 KB page finishes under a set budget.

**3.5 Smaller items.**
- Run emphasis before links in `TextFormatting` (or skip emphasis inside link text) so tags nest properly, and keep
  code spans out of `href`.
- Cap the hover-preview cache (`KEEP` map) at a fixed LRU size, and don't cache failures for 60 s.
- Templates: give each refusal its own message (too long / invalid abbreviation). Store a hidden built-in template as
  a `Hidden` flag on its key, not a full copy in the current language.

## Phase 4 — AI robustness (P1)

**4.1 Equipment is optional, for real (✓).** At `AiFeatures.cs:259` and `PromoteToCastDialog.razor:154`, change the
filter to `when (!ct.IsCancellationRequested)` so a provider timeout keeps the finished draft. Test: a fake client
that times out on the equipment call still returns the character.

**4.2 Reuse chat clients (✓).** `AiProviderService.ClientFor` (`:83`) builds a new client, and a new
`TemperatureFallbackChatClient`, every call, so the "temperature rejected" lesson is lost each time.
- Add a singleton `AiClientCache` keyed by (provider kind, endpoint, model, hash of key). Invalidate an entry when the
  user saves AI settings.
- Test: two calls through one cached client send `temperature` once after the first rejection.

**4.3 World chat cost and lifetime.**
- **History budget.** After N turns, replace old tool results in the history sent to the model with a one-line stub
  ("page X, 5,800 characters, read at turn 3"). Keep the full text in storage for the DM. Set `MaxOutputTokens`.
  Detect a context-length failure and offer "start a new chat from a summary" instead of failing forever.
- **Cancellation.** Pass a `CancellationTokenSource` owned by `WorldChatSection` into every turn. Cancel it on
  `Dispose` and on a Stop button.
- **Busy guard.** Set the `IsBusy` claim synchronously at the top of a turn (`WorldChat.cs:187-194`), before any
  await. `ApplyAsync` and `DiscardAsync` check it on the server.
- **Access.** Re-resolve `CampaignAccess` at apply time rather than trusting the cached one (`WorldChat.cs:835`).
- **Shared state.** Marshal tool-callback mutations of `Document` / `open` onto one lock or the dispatcher (`:864`).
  Plausible; confirm with a stress test first.

**4.4 Equipment picks during apply.** Today apply makes one unreviewed, uncancellable call per new NPC
(`WorldBuilderService.cs:739`). Pick equipment when the proposal is checked, so the picks show in the review panel and
are part of what the DM approves. Apply then makes no model calls.

**4.5 Honest notes to the model.**
- "Applied" (`WorldChat.cs:505`) should say which entries are visible to players.
- Discard after a partial apply (`:544`) should list what was already saved, with links.

**4.6 Prompt injection.**
- Wrap every tool result in clear data delimiters.
- Add a line to `AiPrompts.WorldChat` that text inside tool results is campaign data, never instructions.
- Send the app's own notes as a distinct, unforgeable marker (or a system-role message where the provider supports
  it), not user text starting "(A note from the app…".
- With 1.2 in place, the worst case of a steered model is an edit the DM still has to approve.

**4.7 Error text.**
- Use `AiFailureText.For` in `PromoteToCastDialog.razor:211`.
- `AiFailure.Shorten` (`AiFailure.cs:114`): keep the provider's reason line, not just "HTTP 400 (…)".
- Map 408 to Timeout.

## Phase 5 — System neutrality and rules (P2)

- Move the armor and AC wording out of core: "Dexterity" (`SheetRuleEngine.Vitals.cs:207`), "Strength or Dexterity…
  can't cast spells" and "Speed drops by 10 feet" (`SheetRuleEngine.Skills.cs:117,124`), and the matching
  `Actor.Ac.Dex` / `Actor.Rule.Armor*` resx keys.
  - Use the configured attribute's own label (`DexAttribute` / `StrengthAttribute` → `AttributeDefinition` label).
  - Penalty sentences come from `sheetRules` text in `system.yaml` (and its `.ru` / `.he` translations), with a
    neutral core fallback.
  - Extend `SystemNeutralityTests` to fail on these words in `src/Codex.Core/Rules` and core resx.
- `ActorSheet.Describe` (`:1309`): translate only arguments that are value words, not ids or labels. Default engine
  messages come from core resx, not from every system's yaml.
- AI-made player characters (`CastService.cs:112-121`): keep the campaign's generation method and validate it. Use
  `manual` only for NPCs or when the DM chooses it.

## Phase 6 — Performance (P2)

- **Portraits.** For `MediaEndpoints.cs:55`, load the one actor and check visibility directly. Don't run the
  `OwnedActors` index query with `WaitForNonStaleResults` per image. Where `OwnedActors` is still needed, cache it for
  the request and drop the non-stale wait for read-only display.
- **Wiki art.** Have the `/media/wiki/{pageId}/art` endpoint load one page and check it, instead of
  `GetVisibleForCampaignAsync`.
- **Sheet.** Have `ActorSheet.razor:1152` load the linked page by id through a visible-by-id repository method.

## Phase 7 — Website CI (P2)

- `.github/workflows/pages.yml:24-26`: give pull-request builds their own concurrency group (or put `concurrency`
  only on the deploy job), so a PR can never cancel a queued deploy.
- Pin third-party actions to commit SHAs.

## Phase 8 — Structure (P3, after phases 2–4 land in these files)

- **`WorldChat.cs` (873 lines)** splits into `WorldChatTurn` (loop, nudges, undo), `WorldChatHistory` (mapping to and
  from storage), `WorldChatTools` (tool definitions), and `WorldChatSessions` in its own file.
- **`WorldBuilderService.cs` (1,247 lines)** splits into `WorldReadTools` (57–325), `ProposalChecker` (328–600) and
  `ProposalApplier` (637–1117).
- **`app-startup.js` (1,206 lines)** becomes ES modules loaded by one small entry:
  - `templates.js` (about 380 lines, with the fixture test from 3.3);
  - `suggest.js`;
  - `hover.js`;
  - `save-shortcut.js`;
  - `editor.js`.
  Bump the service-worker cache version.
- **`ActorSheet.razor`** nullability warnings: copy `access` to a local after the guard at `:1125`.

Pure moves, one PR each, no behaviour change. Do them after the fixes above so those PRs don't conflict with
churn.

## Phase 9 — Missing features (P3, each its own design note first)

- **Undo an applied proposal.** Store each apply's `before` snapshots (text and stamp) with the chat. Offer "Revert
  this apply" while no target has changed since, using the same stamp check. Created entries are deleted, and edits
  are restored.
- **Per-chat budget.** Show total tokens used per chat. Add an optional per-campaign cap after which a turn asks
  before sending.
- **Visible-to-players edits** get a second confirmation in the panel ("this changes text players can read").

## Test coverage to add (tracked against the phases)

| Gap | Phase |
|---|---|
| `/preview/page`, `/preview/place` leave out DM text for a player | 1.5 (add even before the lens change) |
| Fact Details redaction and "Lore about" association | 1.1 |
| Edit keeps / drops `:::dm` blocks | 1.2 |
| `:::for` with duplicate names | 1.4 |
| Two tabs on one chat; propose after partial apply; discard after partial apply; double send | 2.1–2.3, 4.3 |
| Promote races a reveal; failed link | 2.4 |
| Save races a reveal (facts, player histories) | 2.5, 2.6 |
| Ctrl+S on `OnClick` saves, non-Latin layouts, held key | 3.1 |
| JS and C# template parsers agree | 3.3 |
| `TextDiff` with a long shared prefix | 3.4 |
| `WorldProposalPanel` / `WorldChatSection` E2E: review, apply, discard | 2.1, 4.3 |
| `PromotePageAsync`, `OwnedActors`, `ActorSheet.Describe` | 2.4, 5, 6 |

## Order and parallelism

1. **PR A — Phase 1.1–1.3** (secret leaks). Small, independent; ship first.
2. **PR B — Phase 2** (stale saves). Touches persistence and `WorldChat`. Needs the 2.6 decision. Can run alongside A.
3. **PR C — Phase 3.1–3.3** (keyboard). JS and editor components only, so it runs in parallel with A and B.
4. **PR D — Phase 4** (AI). Touches `WorldChat` and `WorldBuilderService`; start after B merges.
5. **PR E — Phases 1.4–1.6, 3.4–3.5, 5, 6, 7.** Independent small items; any order.
6. **Phase 8 refactors**, then **Phase 9 features**.

## Decisions needed

1. **Player save conflicting with a DM reveal (2.6):** refuse and ask to reload (recommended), or a three-way merge.
2. **Template expansion on Tab (3.2):** line-start only plus Escape-to-undo (recommended), or a prefix trigger.
3. **Equipment picks (4.4):** move into the reviewed proposal (recommended), or keep at apply but make them
   cancellable and parallel.
4. **Undo of an applied proposal (Phase 9):** build it, or rely on the stamp-checked review alone.
