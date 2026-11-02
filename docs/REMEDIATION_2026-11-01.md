# TTRPG.Codex — Remediation plan of 2026-11-01

*For the review of `main` @ `9777a54` (`REVIEW_2026-11-01`). Items marked **(✓)** were re-read in the code while
writing this plan; the rest keep the reviewer's line numbers and get a failing test first. Same rule as the
2026-10-30 plan: every fix lands with the test that would have caught it, and a phase is done when its tests and
the E2E for the screens it touches are green.*

Ids (S, D, R, U, T) are the review's.

---

## Phase 0 — Same day (one PR, small diffs)

**0.1 External sign-in (D1, ✓).** `Program.cs:382` indexes `Items["LoginProvider"]`, which throws. Replace with
`Items.TryGetValue(".AuthScheme", out var scheme)` falling back to `principal.Identities.FirstOrDefault()?.AuthenticationType`.
Test: build the `AuthenticationProperties` the way `RemoteAuthenticationHandler` does (only `.AuthScheme`) and drive
the callback endpoint through `WebApplicationFactory`; fix `OAuthTestFixtures.cs:38` so it stops writing `LoginProvider`.

**0.2 Data Protection keys under `dataHome` (D12, ✓).** `Program.cs:157`:
`AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataHome, "Keys"))).SetApplicationName("TTRPG.Codex")`.
Chain the AI-key protector's purpose with the user id (`CreateProtector("Codex.AiProviderKeys.v1", userId)`) — old
blobs then fail to decrypt, which `AiProviderService.cs:85-102` already handles; AI settings shows "re-enter this key"
for such a provider, and the release note says so. No migration of the old ring: `SetApplicationName` changes the
discriminator, so old cookies would not decrypt even with the old keys copied in. Everyone signs in once more.
Exclude `Keys/` from exports; README: the ring is plaintext at rest.

**0.3 Dirty tracking of lists (D4, ✓).** `ActorSheet.razor:1009`: `if (!YamlValues.SameValue(now, before)) count++;`.
Test: `DirtyCount` sees an added and a removed pick.

**0.4 Initiative rolled once (D7, ✓).** `CombatConsole.razor:569-571` rolls on the UI thread and the command rolls
again. New `RollInitiativeCommand(actorId, expression, bonus, tieBreak, userId)` whose `Apply` rolls once, appends the
roll entry and sets initiative from the same total. Test at runtime level: logged total == initiative.

**0.5 "End the table" (D6, ✓).** Smallest honest fix now: when an encounter is open, evict with
`completeSession: false` so `LoadOpenSessionsAsync` finds it again; keep the string (en/he/ru), which is then true.
Test: start → combat → end table → start → same encounter.

**0.6 Console exceptions (D8, ✓).** No `ErrorBoundary` exists in `Codex.Web`.
- `DiceExpressionParser.cs:49-80`: `int.TryParse` everywhere, overflow → `FormatException`.
- `CombatConsole`: one `SendAsync(IRuntimeCommand)` helper that catches, logs at Warning for `FormatException`, and
  flashes `L.Describe(ex)`; null `runtime` (evicted) flashes "the table was closed" instead of NRE. Route every
  `runtime!.EnqueueAsync` through it.
- Wrap `@Body` in `MainLayout` with an `ErrorBoundary` that shows a recoverable panel.
- Tests: `1d20+99999999999` is a `FormatException`; a bUnit test that a throwing command leaves the console alive.

**0.7 Sheet long-text secrets (S1, ✓).** `RavenActorRepository.Redact` (`:40-68`) never touches `Sheet`. For non-DM
access, run `SecretText.ForPlayers` (owner: `ForPlayers` with `owned`, like `Biography`) over every value whose field
in the system schema is `LongText`; on a player save, `KeepSecrets` against the stored value for the same fields.
Tests: Known NPC's Notes with `:::dm` hides it from a player; the owner's save keeps the DM block.

**0.8 Hidden combatants (S2, ✓).** `CombatConsole.razor:79` counts `encounter.Participants`. For non-DM viewers:
count over the visible list; `VisibleRollLog` drops entries whose `ActorId` is a hidden participant. Better at the
source: `RollDiceCommand.Apply` marks a roll `IsSecret` when its actor is `Hidden`. Test: player snapshot with a
Hidden goblin has no roll and "of N" over visible rows.

**0.9 Unit tests without a browser (T1).** Move `TemplateGrammarTests.TheEditorsParser_ReadsEveryCaseAsTheServerDoes`
into the `E2E` namespace (or `[Trait("Category","Browser")]`), so the unit filter runs clean.

**Phase 0 status (branch `fix/review-2026-11-01-phase0`).** 0.1–0.9 landed with tests, except: 0.1 tests
`ExternalLoginPolicy.ProviderOf` against the ticket the remote handler builds, not the endpoint through
`WebApplicationFactory` (see F1); 0.6's `SendAsync` and `ErrorBoundary` have no component test (no bUnit in the
repo), only the parser's overflow cases; 0.8 moved the visibility rules into `TableVisibility` so they are unit-tested.
0.5 also stops the stale-live-session check in `OpenSessionAsync` from completing a session whose fight is still open.

## Found during Phase 0

- **F1 (P3, tests).** `OAuthCallbackTests` re-implements the callback's logic inside each test instead of calling it,
  so it can't catch a bug in `Program.cs` (that is how D1 hid). Move the callback body into a testable
  `ExternalLoginCallback` service and test that; or drive `/login/external-callback` through `WebApplicationFactory`.
- **F2 (P2, secrets).** **Decided: keep the hint (decision 6).** A player's turn banner still reads "Someone you can't see" while a Hidden participant acts, which
  says a hidden creature is in the order (S2 now removes its count and rolls, not the banner). Decide with R1: show
  nothing (players see the previous visible turn until the next visible one) or keep the hint deliberately.
- **F3 (P2, UX/robustness).** `AuthLayout` (login, register) has no `ErrorBoundary`; `MainLayout` now does. **Landed in part 8.**
- **F4 (P3, maintainability).** `CombatConsole.ActorsById` (a new dictionary per access, review §5) now also feeds a
  `HashSet` per render for `TableVisibility`; cache both per snapshot when the console is split up. **Landed in part 9** (`VisibleActorIds`, built with `ActorsById`).
- **F5 (P3, tests).** No bUnit in the test project, so console handlers (`SendAsync`, `EndTableAsync`) and dirty
  tracking in `ActorSheet` can only be tested through E2E or by extracting logic (as 0.3 and 0.8 did). Adding bUnit
  would make U-items and D8-style fixes testable at component level.

## Found during Phase 1

- **F6 (P2, secrets).** `ISessionRepository.GetAsync` has no `CampaignAccess`: a player's session page gets the whole
  document (DM summary, secret notes, roll log) and filters in Razor. Nothing reaches the browser today (Blazor Server),
  but every new view has to remember to filter. Add `GetVisibleAsync(id, access)` that redacts as actors and notes do.
  **Landed:** `ISessionRepository.GetVisibleAsync` (no DM summary or secret notes, recap and notes redacted, log through
  `SessionEventRedactor`, no secret or hidden-actor rolls; the copy refuses `SaveAsync`); the session page uses it.
  `GetAllForCampaignAsync` still returns whole documents, but the list views read only titles, dates and event counts.
- **F7 (P3, UX).** Two characters with one name show as two identical "@Bram · PC" suggestions. The id is now written
  correctly, but the list should say which is which (owner, or the first line of their description). **Landed in part 9.**
- **F8 (P2, secrets).** The live table's notes drawer (`TableNotesDrawer`) shows non-secret table notes to players as
  typed, without `SecretText`; the session page now redacts the same notes. Redact them in the drawer too, or once in
  the runtime snapshot. **Landed:** one rule, `SessionNotes.TextFor` (others' notes redacted for a player, their own
  and the DM's whole), used by the drawer and by `GetVisibleAsync`.

## Found during Phase 2

- **F9 (P3, data loss in a narrow case).** `PlaceWikiSync.CreatePageAsync` builds a new `WikiPageDocument` with the
  place's existing `WikiPageId` when it has one and saves it whole. The Places page offers "Create page" only when it
  finds no such page, so this overwrites only from a stale list (another tab made or restored the page meanwhile); the
  copy was never handed out, so the version check does not apply. Create only when no page with that id exists. **Landed in part 9.**
- **F10 (P3, UX).** `WorldChat.Error.ApplyingElsewhere` reaches the DM through the generic "the proposal failed" path in
  `WorldChatSection`, which also logs it as an error. Show it as the stale-chat case is shown (reload, no log). **Landed in part 9.**
- **F11 (P3, UX).** A portrait changed while the table is live is not pushed to the runtime's cached actor, so the table
  shows the old one until the actor is hydrated again. Not checked in the console; noted from the code. **Confirmed in part 9, still open:** the table page reloads its actors only when the roster version moves, and every runtime command writes a session-log line, so pushing a portrait needs a command that is not logged.
- **F12 (P3, consistency).** `WorldChatModels.TrySaveAsync` still keeps its own change-vector table; move it onto
  `DocumentVersions` when the chat repository is next touched. **Landed in part 9.**

## Found during Phase 3

- **F13 (P2, access).** D15 covered the three write APIs the review named. Every other repository that takes a
  `CampaignAccess` (`TrySaveAsync` on actors, wiki, regions, homebrew, facts; `TrySetPortraitAsync`) still trusts the role
  the page resolved when it opened, so a demoted co-DM keeps those powers until they navigate. Image writes now refuse
  first, so a portrait flag can flip but no picture lands. The fix is the same: read the role from the stored campaign
  at write time (`CampaignAccessResolver.RoleOf`), once, in a shared guard. **Landed in part 8** (`NowAsync`).
- **F14 (P2, R13 remainder).** The server-wide AI key is still spendable by every account on a LAN server, with no budget
  and no attribution, and the AI settings page does not say so. **Landed in part 8** (`ServerAiUsage`).
- **F15 (P3, backups).** A backup's images and accounts have no restore path in the app (copied back by hand); campaigns
  restore through "restore it as a new campaign". A ServerAdmin restore page would close it.
- **F16 (P3, invites).** Invite codes made before D14 (8 hex characters) keep working until the DM makes a new link. The
  per-user and per-address limits make guessing one impractical, but a one-off upgrade (or a note on Settings) would end it. **Landed in part 9** (the one-off upgrade).
- **F17 (P3, maintainability).** `ReconnectModal.razor.js` is the template's copy of the reconnect logic and is never
  loaded; `app-startup.js` is the one that runs. Delete it. **Deleted** (staged with Phase 7).
- **F18 (P3, UX).** "Start table" still has no `Busy` state. Two clicks are now safe (R6), but the second one waits
  silently behind the first. **Landed in part 9.**
- **F19 (P3, robustness).** The login and invite attempt counters are in memory, so a restart forgets them. That buys a
  guesser a few more tries per restart, not a way round them.

## Found during Phase 4

- **F20 (P1, secrets).** Session-log events name actors with `runtime.GetActorName`, the real name, in both the DM text
  and the player text, and `SessionEventRedactor` shows players every event about an actor they can see, including an
  `Unknown` one. A stranger the DM gave a public name is named for real in the players' session log. Quick-added
  monsters are not affected (their public name is their real one); NPCs the DM masks are. Fix: log actors as terms
  (id plus name) and name them per viewer when rendering, as `MentionDirectory` does. A hidden actor named as a source
  ("damage from …") leaked the same way. **Landed:** `EventTerm.Actor`. The runtime logs every hydrated actor as a term
  (`CampaignRuntime.ActorTerm`). `SessionEventRedactor` renames each one to the name the audience knows: a stranger's
  public name, or "someone" (`EventTerm.UnseenKind`, localized as `Event.Someone`) for an actor they cannot see. It
  then rewrites `Description` from the renamed messages. Events logged before this change hold plain names and are
  not rewritten; they still name strangers for real until those sessions age out.
- **F21 (P3, UX).** Undo (R5) restores only the creature it was used on: effects that its change ended on others (a
  death ending what it concentrated on) do not come back. The confirm before "Mark dead" says so; undo itself does not.
- **F22 (P3, UX).** A player's table page now reloads its actor list whenever the roster changes (R1). That costs one
  actor query per open page per join, leave or reveal. Cheap at table sizes, but worth batching if tables grow.
- **F23 (P3, UX).** The undo button sits in the DM's command panel. U3's undo toast should replace it.

## Found during Phase 5

- **F24 (P2, R7 remainder).** Phase 5 took the five R7 items the plan named. The rest of R7 is still open:
  - The prepared-spell limit is pooled across classes.
  - A homebrew class without `hitDie` or `spellcasting` fails silently.
  - An attribute added to `system.yaml` later never reaches existing sheets.
  - The class skill-choice count is not enforced.
  - Equipment weight is never summed.
  Each needs a rules decision more than a code change. U4 (the level-up stepper) is the natural place for them.
  **All five landed in part 8 (decision 7).**
- **F25 (P2, R14 remainder).**
  - A chat that has run out of context is still dead after the "carry on in a new chat" recap, and there is no token budget.
  - Applying a proposal cannot be undone, and wiki pages have no revision history.
  - The chat document keeps up to four copies of each proposal and every full tool result, and is rewritten whole on each save.
  Each of these is its own piece of work. **Left for its own branch (decision 7).**
- **F26 (P3, sheet).** `SchemaForm.ParseNumber` means to store whole numbers as `long`, but its `? (long)parsed : parsed` ternary
  makes every number a `double`. Readers cope (`YamlValues`), so nothing is wrong on screen; the stored type is just not
  what the code says. **Landed in part 9.**
- **F27 (P3, wiki).** A player's single-page fetch (`GetVisibleBySlugAsync`) does not drop links to pages they cannot open.
  The list fetch does. The player's copy of the page reveals such a link only through its link count, but the two fetches
  should agree. **Landed in part 9.**

## Found during Phase 6

- **F28 (P2, deploy).** `App.razor` links `@Assets["app.css"]`, but `Program.cs` serves files with `UseStaticFiles`, not
  `MapStaticAssets`. Without the static-assets manifest, `Assets[...]` returns the bare name, so the stylesheet and
  scripts have no version in their URL. After an update a browser can keep the old `app.css` and draw new markup
  unstyled. The Phase 6 check met exactly this: the toast rendered as an ordinary banner until the CSS was reloaded.
  Fix: `app.MapStaticAssets()` (fingerprinted URLs and long cache), or at least `asp-append-version`.
  **Fixed (2026-11-01, uncommitted).** `UseStaticFiles` is replaced by `app.MapStaticAssets().AllowAnonymous()`; anonymous
  because the sign-in-required fallback policy now covers these endpoints, and the sign-in page needs its CSS.
  Bootstrap and `app-startup.js` now go through `@Assets[...]` too, so the page asks for `app.<hash>.css` and
  `js/app-startup.<hash>.js`. The service worker cache is bumped to `codex-shell-v22`, so browsers drop the shell they
  already hold. Checked: signed out, the login page links the hashed files and they return 200, and the bare names and
  fonts still work. A signed-in page picked up the new CSS with no forced reload. In Production the hashed files are
  sent as immutable; Development sends `no-cache`.
- **F29 (P2, every page).** `App.razor` rendered `<HeadOutlet />` without a render mode while the routes run interactive,
  so `<PageTitle>` reached the browser tab only on the server's first render. After any in-app navigation, the tab kept
  the first page's title. **Fixed with U2 (uncommitted):** `<HeadOutlet @rendermode="PageRenderMode" />`. Sign-in and
  register still render static, and their titles are unchanged.

## Phase 1 — Secrets (P0 remainder)

- **S3** `SessionMentions.ForEditing`: leave `[[actor:id]]` verbatim on `:::for` opener lines; in `Encode`, when a name
  matches several actors, prefer the id stored at that position, else refuse with a localized message naming both.
  Fix the glued `the[[actor:id]]` round trip. Failing test first (two "Bram"s), then the same in Wiki/Lore/World.
- **S4** `WorldSection` Describe: strip secret blocks before `DescribeAsync`, re-attach after (reuse the
  `WritePlayersVersionAsync` helper); as a backstop, refuse when `SecretText.LostSecrets(before, after)` is non-empty.
- **S5** Recaps and comments: run `SecretText.ForPlayers` in `SessionDetail` and in note rendering for players.
- **S6** `Mask` drops `Kind`; `ForPlayers` on regions clears `Connections`; wiki search over marker-free text; lens
  adds `?as=players` to `/preview/*`; `SessionEventRedactor.Project` fails closed (generic line when no
  `PlayerDescription`); `LogTerms.Source` checks visibility.
- **D16** `KeepSecrets` preserves stored `:::party`/`:::players` blocks with their openers, matched by inner text.

**Phase 1 status (same branch).**
- S3 landed: `MentionDirectory.ForEditing` keeps ids on marker lines, for shared names and glued mentions; `Encode` never
  guesses a shared name; the autocomplete writes the id for a shared name (`w` in the suggestion JSON).
- S4 landed in `AiFeatures.WithSecretsAside`, so it covers the homebrew wizard as well as places.
- S5 landed: comments are redacted in `RavenNoteRepository` (except the reader's own); the recap and table notes on
  the session page are redacted there (see F6, F8).
- S6 landed: a stranger's kind, place routes, wiki search, hover cards in the lens. **Deferred:** making
  `SessionEventRedactor` fail closed would hide roll totals players are meant to see, so it needs each event type to
  say whether its numbers are secret; `LogTerms.Source` leaks nothing today. Both are left for when events are next
  touched.
- D16 landed for blocks the player left as they were. A changed revealed block still becomes prose, and DM blocks
  still move to the end of the text. **Both closed in Phase 7 part 8.**

## Phase 2 — No whole-document write without a version (P0/P1)

One mechanism first, then apply it. `WorldChatModels.cs:263-316` already keeps a `ConditionalWeakTable` of change
vectors and compares on save; lift it into a `Versioned<T>` helper in `Codex.Persistence` (record the vector on load,
`session.StoreAsync(doc, expectedChangeVector, id)` on save, `ConcurrencyException` → `SaveResult.Changed`).

- **D2** wiki `TrySaveAsync` (used by the editor, "Share page", world-builder apply). On conflict the editor reloads,
  keeps the DM's text in the box, and says what changed.
- **D3** actors: same for the no-table path (`RavenActorRepository.cs:188`); one baseline in `ActorSheet`; HP merged by
  delta against live; slot/HP buttons go through runtime commands when a table is live.
- **D18** users via `RavenUpdate.RunAsync<UserDocument>` field patches.
- **D5** portrait actions patch `HasPortrait/PortraitVersion` only.
- **D10** `Saved(draft, "place", id)` inside `PlacesAsync`; an `Applying = epoch` marker in the guard save; `get_page`
  and `TooLong` measure the same string.

**Phase 2 status (same branch).**
- The mechanism is `DocumentVersions` (`Codex.Persistence`): the change vector each handed-out copy was loaded at, kept
  beside it; `Carry(copy, original)` for editors that work on a copy; `SaveIfUnchangedAsync` compares and then saves under
  optimistic concurrency; `SaveOutcome { Saved, Changed, Refused }`. A copy the store never handed out (a new document)
  is saved as before. `WorldChatModels` keeps its own table (not moved, to keep this diff small).
- D2 landed: `IWikiRepository.TrySaveAsync` returns `SaveOutcome`; DM loads are versioned. The editor keeps the DM's
  text on `Changed`, takes the stored page as the one it replaces and says so, so a second Save is a deliberate
  overwrite (it does not merge a reveal into the typed text). "Share page" shows the latest instead. World-builder edits
  map `Changed` to `WorldBuilder.Error.EditChanged`. A page deleted since it was opened is not recreated by saving.
- D3 landed: the no-table actor save throws `Actor.Sheet.ChangedMeanwhile` over a newer version; the sheet keeps its
  edits and, as for wiki, a second Save replaces knowingly. At a live table, `SheetCounters.MergeByDelta` adds the
  editor's change to the live value for every counter the table moves (resources, current HP, spell slots), so the
  30/30 → 12 → Con +5 case leaves 17/35, and a slot used on the sheet and one at the table leave two used. That also
  covers the slot-tracker overwrite, so the sheet's buttons were not rerouted through runtime commands (they still need
  Save while a table is live, which is UX, not data loss). **Two baselines kept on purpose:** `baselineSheet` (after the
  rules ran on load) decides what counts as unsaved; `loadedSheet` (as stored) is the merge baseline, so a value the
  rules recomputed on load is written and current HP moves with a new maximum. The doc comment says so.
- D5 landed: `IActorRepository.TrySetPortraitAsync` patches `HasPortrait`/`PortraitVersion` only; the sheet keeps its
  unsaved (and possibly rule-breaking) edits in the page, and stays saveable when it was the latest. The whole-sheet copy
  no longer carries the portrait fields (`CopyEditableFields`), so nothing else can revert them. A failed portrait save
  no longer reports success.
- D18 landed: `IUserRepository.UpdateUserAsync(userId, mutate)` over `RavenUpdate`; the whole-document overload is gone.
  Callers: language switch, login rehash, external-login linking (now also idempotent), AI settings.
- D10 landed: places are recorded as each is stored; `WorldChatDocument.ApplyingSince` is set by the guard save and
  cleared when the apply ends or fails, so a tab that opened the chat in between refuses with
  `WorldChat.Error.ApplyingElsewhere` (a mark older than 10 minutes, left by a crash, is ignored); `TooLong` measures
  `Editable(...)`, the text `get_page` shows, and `get_page` uses the shared `Cap`.
- Tests: wiki edit vs. reveal, actor DM vs. player between sessions, live HP/slot merge, portrait patch, two-tab user
  update, places failing half-way, the apply mark, `TooLong` with mentions. E2E not run (Playwright's browser is not
  installed on this machine).

## Phase 3 — Access and server hardening (P1)

- **D15** `CampaignAccess` parameter on `ICampaignRepository.UpdateAsync`, `IMediaStore.PutAsync`,
  `INoteRepository.DeleteNoteAsync`, re-resolved inside (as `TrySaveAsync` does).
- **D14** invite codes: 128-bit from `RandomNumberGenerator` (base32), optional expiry/one-shot, per-user attempt
  counter inside `JoinByInviteCodeAsync`.
- **R13** login throttling always on; first-admin under a compare-exchange; launcher "Host" waits for the DM account;
  drop `?error=` text (use codes); surface pack/plugin load errors in the Library; document the DLL trust boundary;
  `JsonReplyAsync` retries without `response_format` only on 400.
- **R6** runtime lifecycle: hydrate before `TryAdd`, `Lazy<Task<CampaignRuntime>>` per campaign, drain/fault the queue
  on dispose, subscribers count as activity.
- **D11** detect the licence refusal → "Backups: off" in About/launcher; scheduled JSON export of every campaign, with
  its images, plus users and AI settings (keys excluded) into `Backups/<timestamp>/`, with retention.
- **D13** export: remap ids on "restore as new", strip `InviteCode`, normalise every `JsonElement` map, rename to
  "merge", say "without images".
- **D9** draft mirroring to `sessionStorage`, longer `DisconnectedCircuitRetentionPeriod`, recovery view instead of
  `location.reload()`.

**Phase 3 status (same branch).**
- D15 landed: `ICampaignRepository.UpdateAsDmAsync(access, mutate)`, `IMediaStore.PutAsync/DeleteAsync(access, …)` and
  `INoteRepository.DeleteNoteAsync(id, access)` read the caller's role from the stored campaign when they write
  (`CampaignAccessResolver.RoleOf`). The media store also checks that the document is in that campaign, and lets a
  player write only their own character's portrait. A refusal is `Campaign.AccessChanged`;
  `CampaignContext.WithCurrentRoleAsync` says so and reloads the page, which resolves the role again. `ArtService`
  turns it into its usual `false`. Every page-side `UpdateAsync` now goes through `UpdateAsDmAsync`; the runtime
  manager keeps the plain one. The rest of the repositories are F13.
- D14 landed: `InviteCodes.New()` (128 bits, base32, 26 characters); the DM can make a new link that lasts until
  replaced, 7 days, 24 hours, or for one player (`InviteExpiresAt`, `InviteSingleUse`, `InviteUsedBy`; the player who
  used it can follow it again, because the join page prerenders). The join re-checks the code on the stored copy, so
  two players racing for a one-use link cannot both get in. `InviteAttempts` refuses a user's joins after 10 wrong codes
  in 15 minutes (`CampaignJoinResult.TooManyAttempts`). Sign-up on an exposed server applies the same expiry and use.
- R13 landed:
  - The HTTP limiter is always on, except for this computer (loopback). `LoginAttempts` allows 10 wrong passwords per
    account and address in 15 minutes.
  - The first ServerAdmin is claimed under a compare-exchange key, only from this computer, and only while no admin
    exists. A LAN player who signs up first can no longer take it, so the launcher's "Host" flow needs no change.
  - `?error=` takes codes only.
  - Pack problems (entries that do not parse, missing bases) are collected through `IContentPackLoader.TakeProblems`
    into About's warnings, and the Library tells the DM when there are any.
  - The README says plugin DLLs are programs.
  - JSON replies are retried without `response_format` only on 400, 422 or a client `NotSupportedException`.
  - The AI-key half of R13 is F14.
- R6 landed:
  - A start or end is serialized per campaign; two starts give one runtime and one session.
  - The runtime is built and hydrated before it is published, and a failed hydrate leaves nothing cached.
  - Ending the table finishes the commands already queued (with a 10-second cap; anything left over fails).
  - A table with pages subscribed is not idle-evicted.
  - The start button's `Busy` is F18.
- D13 landed:
  - Export leaves out the invite code.
  - `ImportAsNewAsync` gives every document a new id and rewrites each whole-id occurrence in the bundle's JSON: ids
    in other documents, ids inside ids, targets, and mentions in text. The file's campaign is never touched, and the
    importer becomes the owner.
  - Every `Dictionary<string, object>` in a bundle is normalised (world state, fact and place metadata, active
    effects).
  - The UI says "Merge from a file", and that the export comes without images or the invite link.
  - The plain `ImportAsync` stays the merge (and the restore under the same ids, for API callers).
- D11 landed:
  - `JsonBackups` writes `Backups/<yyyy-MM-dd_HHmmss>/` each day (and a minute after start when the last backup is
    older): every campaign's bundle, its images, the players' journals, and `users.json` (password hashes kept, AI
    keys removed).
  - Each backup is written under `.partial` and renamed when complete. It keeps the newest 7
    (`Codex:Backups:Keep`); `Codex:Backups:Enabled` turns it off.
  - The database's own backup is still requested. A licence refusal is detected and logged as information, not an
    error, so the smoke tests still pass.
  - About shows the ServerAdmin the folder, the last run, any failure, and whether the database backup is on.
  - The launcher's Backups button opens that folder, creating it if needed.
  - Restoring images and accounts is F15.
- D9 landed:
  - The circuit is kept 30 minutes after a disconnect.
  - A circuit the server no longer has shows "has to reload" with a Reload button, not a blind reload.
  - Every text box and textarea that has an id is copied to the tab's `sessionStorage` as the person types, unless
    it is a password box or marked `data-no-draft`.
  - After a reload the app asked for, boxes whose text differs from what was saved are offered back ("Put it back" or
    "Discard it"); the copies are dropped once offered, or after a day.
- Checked in the running app (in-app browser, throwaway data folder):
  - The About backup panel.
  - The first account becoming admin from this computer.
  - A draft surviving a forced reload and being saved by Create.
  - The invite options.
  - The 26-character code.
  - The first backup written a minute after start.
- Tests: 1068 unit tests pass; E2E not run (Playwright's browser is not installed on this machine).

## Phase 4 — Table rules (P1/P2)

R1 (per-row visibility toggle, quick-add defaults to `Unknown`), R2 (shared advance routine), R3
(`KillCommand`/`ReviveCommand` on `TableRules` + confirm), R4 (expire anchored effects on death/removal;
`Concentration` marker), R5 (per-actor undo of the last pool/effect change), R8 (multi-term dice, crit only on a d20
term, sheet uses the plugin roller), R9 (one `MaxKeyOf(resource)`; delete `CodexWorld.ApplyDamage`), D17 (soft-delete:
`Retired` flag the mention directory still names; mention count in the confirm).

**Phase 4 status (2026-11-01): done, uncommitted.**
- R1: the DM's command panel has a "Players see: Nothing / A stranger / Who it is" toggle, and the row shows a badge.
  Quick add makes the creature a stranger under its own name. `CampaignRuntimeManager.TrySetVisibilityAsync` stores
  the change and tells the live table (`RefreshActorCommand`). `RuntimeSnapshot.RosterVersion` makes every open table
  page reload who it may see when someone joins, leaves or is revealed. The player-side reload was not checked in a
  browser.
- R2: `CampaignRuntime.MoveTurnOn` is shared by "Next turn" and by removing whoever is acting: the dead are skipped and
  the next participant's turn begins.
- R3: `KillCommand` and `ReviveCommand` run on `TableRules.Kill` and `TableRules.Revive`. "Mark dead" asks to confirm.
  Revive clears down, stable and the death saves, and leaves the creature on at least 1 HP.
- R4: a death, or leaving the order or the table, ends the timed effects counted on that creature's turns.
  Concentration is in `vitals.concentration` (SRD: `Concentrating`). A condition can be "held by" a participant; a hit
  asks for a DC max(10, half) save; going down, dying or removing the condition ends what it held.
- R5: `IUndoableCommand` and `UndoLastChangeCommand`. The runtime keeps each creature's state from just before and
  after its last change, and undoes it only while nothing has changed the creature since.
- R8: the parser takes several terms (`1d8+1d6`, `2d6+3+2`, `1d20+5-1d4`). A natural 20 or 1 is read only from the one
  d20 check die (`CheckDieIndex`), so `1d200` is never a crit. The sheet rolls with the system plugin's roller.
- R9: `SystemDefinition.MaxKeyOf` is the one rule for a pool's maximum. `CodexWorld` takes it as a parameter, and
  `CodexWorld.ApplyDamage` is deleted.
- D17: `ActorDocument.Retired`. Cast retires instead of deleting and tells the DM how many texts mention the actor.
  There is a retired list with "Bring back". `CampaignContext.Actors` leaves retired actors out and `AllActors` keeps
  them, for names and mentions. Mentions still name retired actors, but the suggestions don't offer them. A retired
  actor is not loaded onto the table. `TryDeleteAsync` stays, only for the cast service's duplicate cleanup.
- Checked in the running app: the stranger badge and the visibility toggle; damage and undo; a held condition, the
  concentration save prompt, and removing concentration; retire with the mention count; bring back.
- Tests: 1091 unit tests pass. Three tests of the deleted `CodexWorld.ApplyDamage` were removed; 26 new tests were
  added. E2E not run.

## Phase 5 — Sheet and data edges (P2)

R7 (cached compiled formulas on `InterpreterOptions.None`; level cap; orphan picks removable; formula errors as
DM-facing warnings; numeric input validation), R10 (dates in the viewer's zone via `ViewerClock`; editable),
R11 (store links by page id; resolve on rename), R12 (`WaitForNonStaleResults` in every `DeleteAllForCampaignAsync`),
R14 (world-chat housekeeping).

**Phase 5 status (2026-11-01): done, uncommitted.**
- R7:
  - **Formulas.** Derived and armor-class formulas are compiled once and cached. They run on
    `Interpreter(InterpreterOptions.None)` with only `floor`, `ceil`, `max`, `min` and `mod`, so `new string(...)` no
    longer parses.
  - **Formula errors.** A broken formula is now a warning (`Actor.Rule.FormulaError`) that does not block a save and does
    not show the parser's text. Infinity and huge results count as errors and are no longer cast to `long.MinValue`.
  - **Formulas using formulas.** A derived formula can use one declared before it.
  - **Level cap.** Level up stops at the level field's `max` (20 in the SRD).
  - **Missing picks.** A pick whose Library entry is gone shows as a red chip with a remove button
    (`SheetEvaluation.ListMissing`). A reference field holding a removed entry lists it as "(not in the Library)" instead
    of showing "None".
  - **Number boxes.** Text that is not a number leaves the value as it was. Numbers are clamped to ±1e9. The armor-class
    boxes no longer write 0.
  - The rest of R7 is F24.
- R10:
  - `ViewerClock.DayToUtc` stores a picked day as noon of that day in the DM's zone, so the day reads the same for anyone
    within twelve hours of the DM.
  - The new-session date starts on the DM's today.
  - The session page has an editable date.
- R11:
  - `WikiLinks` decides which page a link points at: by the page's slug (its first name) or by its current title. Rendering,
    "Links here", link counts, a player's links and the lore about a page all use it, so a renamed page keeps its backlinks.
  - Stored data is unchanged, so nothing needs migrating.
- R12: every `DeleteAllForCampaignAsync` that queries an index waits for it.
- R14:
  - **"Model can't use tools" check.** It reads the error messages, not the stack trace.
  - **Proposal size.** The prompt and the schema now agree: about 8 entries, at most 30.
  - **Proposal errors.** They quote campaign names, so they are fenced.
  - **After applying.** Applying a proposal reloads the campaign every time.
  - The rest of R14 is F25.
- Not checked in a browser: the session date field, the missing-pick chips, and the level cap.
- Tests: 1109 unit tests pass (17 new). E2E not run.

## Phase 6 — UX programme (Owlcat yardstick)

In this order, each its own PR: U5 + U6 (one SaveBar/Ctrl+S model, flashes cleared on section change, inline
validation, `Busy` on every create) → U3 (undo toast on `Flash`; confirms for the listed actions) → U2 (sheet and
session as window routes inside `CampaignPage`) → U1 (pinnable, nestable preview card; tap bottom-sheet on phones;
links on the table) → U4 (level-up stepper from class `features`; per-field violations; skill/save breakdowns) →
U7, U8.

**Phase 6 status (2026-11-01): U5, U6, U3, U2, U1, U4, U7 and U8 done, uncommitted.**
- U5 (feedback where you are looking):
  - **Toast.** The page status (`StatusMessage Toast="true"`) floats over the top of the screen on the campaign, session,
    sheet, AI settings and home pages. A success or note goes after 6 seconds. An error, or a message with an action,
    stays until dismissed.
  - **Section change.** A message belongs to its section: moving to another section clears it.
  - **Inline errors.** Settings: a blank name and a bad cover image are said under their field (`FieldError`, with
    `aria-invalid`). AI settings: what stops a save is said above Save, with the fields marked.
  - **Combat.** Damage, healing or temporary HP with no amount, Set initiative with no total, and Add condition with no
    condition now say what is missing. Before, they did nothing; a blank initiative went in as 0. "0 or blank: until
    removed" is on screen under Rounds, not only in `title=`.
- U6 (one save model):
  - **Session page.** One sticky save bar (`session-save`, Ctrl+S) saves the details, recap and DM summary in one write,
    and the journal in its own. It replaces four Save buttons that shared one gate, where Ctrl+S could be dropped.
  - **Saving twice.** A double tap no longer saves or creates twice: `SaveGate` and `Busy` on Cast's Create, Lore's Add,
    Library's Add to cast, Settings' details and packs, and AI settings.
  - **One "Saved." string.** `Common.Saved` replaces the session, sheet, settings, journal, fact and template variants.
    The "Saved “{name}”." form stays where a name is given.
  - **Fact editor.** Lore's new-fact and edit boxes use the Markdown editor, with preview, the secret button and page
    links. They have a leave guard, and Cancel or opening another fact asks before dropping changes.
  - **Places art.** Art is stored at once. When the text is still unsaved, the message now says the art change is
    stored and the rest still needs Save, so a later "discard changes" is not read as discarding the art.
- Checked in the running app:
  - The session save bar, via a button and Ctrl+S from the recap.
  - The toast: solid, and gone after 6 seconds.
  - The inline Settings name error.
  - The message clearing on a section change.
  - The fact editor's toolbar and secret preview.
  - The leave guard on an unsaved fact.
- Not checked in the app: the combat messages (they need a running encounter), and AI settings.
- Tests: 1109 unit tests pass. One E2E test moved from "Save recap" to the save bar. E2E not run.
- U3 (undo and confirmations):
  - **Undo on the toast.** `Flash.Undoable(text, undo)` gives the toast an Undo button (`status-undo`). It stays 12
    seconds instead of 6. The undo reports itself ("Undone.").
  - **Undo, where the change can be put back:**
    - Wiki "Expand with AI" and the sheet's AI "Rewrite" put back the text that was in the box. If another page or
      actor is open by then, the toast says it is too late (`Common.UndoGone`).
    - Removing an AI provider puts it back where it was, and as the default if it was the default.
    - Lore's one-tap × (a character no longer knows a fact) gives the fact back at the level they had.
  - **Confirm, where it cannot:**
    - Removing a picture: every `ArtSlot` (Library, wiki, places, homebrew), the campaign cover, and an actor's
      portrait. The image is deleted, so there is nothing to put back.
    - Making a new invite link: the old link stops working at once.
    - Changing a member's role. It ran on the select's change, which a mis-scroll on a phone can fire. Promoting
      someone to DM is worded as the dangerous one. Cancelling redraws the select at the role it still has.
    - Removing a sheet list row (`SchemaForm`) that has something written in it. An empty row goes without asking.
  - "Mark dead" already asked first (R3). Reverting an applied world-chat proposal is still F25.
  - Checked in the app:
    - The invite-link confirm; Cancel keeps the link.
    - Forget, then Undo: the character is back at "Heard". Left alone, the toast goes at 12 seconds.
    - A double click on Create made one character.
  - Not checked in the app: the role confirm (the test campaign has no other member), the picture confirms, row
    removal, and the AI undos (no model configured).
  - Tests: 1109 unit tests pass. E2E not run, and none of them uses the flows that now ask first.
- U2 (windows, not pages):
  - **Window route.** `ActorSheet` and `SessionDetail` are no longer `@page`s. `CampaignPage` opens them for
    `actors/{id}` and `sessions/{id}` inside the workspace, so the side rail, the phone tab bar and the notes drawer
    stay.
  - **Section kept underneath.** The section the window was opened from stays mounted under it, hidden
    (`.ws-section[hidden]`). Closing finds it as it was: no skeleton, and scrolled where it was (`codex.windowOpened`
    and `codex.windowClosed`).
  - **Back.** The sheet's Back (`sheet-back`) and the session's new Back (`session-back`) say where they go ("Back to
    Table") and use the browser's Back, so Forward works too. A window opened from a link from outside, or a reload,
    goes back to Cast or Sessions. The rail marks the section underneath.
  - **Tab title.** The window names the tab: the campaign page drops its own `<PageTitle>` while a window is open
    (with F29).
  - **Place links.** `#place-ID` opens the place's description, marks the row for a moment and scrolls to it, once
    Places has loaded. This works from another section and from within Places.
  - **Phone detail panes.** `.phone-sheet` (a Library entry, the journal editor) stops above the tab bar, which is a
    second way out. Escape closes it, unless a box, a dialog or a menu has focus.
  - **Test ids.** The session page's Run button is now `session-run`; `run-session` is the session list's.
  - **Watch for:** E2E tests now find a hidden copy of the section under a window. None of the ids or texts they use
    is in both, but a test that matches loosely by text could see two.
  - Checked in the app:
    - Cast → sheet → Back: back on Cast at the same scroll (300 px), no skeleton.
    - Table → sheet: "Back to Table", returns to the Table, and the tab title follows.
    - A direct link to a sheet: Back goes to Cast, and browser Back reopens the sheet.
    - Sessions → session → Back.
    - A place link from Lore: Places with the description open and marked.
    - On a phone: a Library entry's pane ends at the tab bar, and Escape closes it.
  - Checked later, with U1: the notes drawer opens over a sheet window, takes a note, and links its mention.
  - E2E not run (no Playwright browsers here). Read instead: no test opens a window by clicking and then matches
    something the section underneath also has. Creating a character opens its sheet over Cast, and the tests that do it
    then use the sheet's own test ids and labels, which Cast does not have.
- U1 (the preview card):
  - **One card for more things.** `PreviewEndpoints` now also answers Library entries (`entry/{type}/{id}`: a condition's
    rules, a spell, an item, up to 600 characters) and sessions (`session/{id}`: the recap the viewer may read). Every
    answer carries where it opens (`href`) and what its text names (`links`): mentions as the viewer knows them, then
    page links to pages they may read. The links come from the same text the card shows, after the secret blocks are cut
    for a player.
  - **With a mouse** (`app-startup.js`):
    - The card can be reached. The pointer can move onto it.
    - Cards nest. A name on a card opens that name's card beside it, or under the name when neither side has room.
    - Cards can be pinned. The pin keeps a card open while you scroll, read on, or open the thing it names; up to four
      stay pinned.
    - Closing. Escape closes the newest card first, before a window or a phone pane gets it. Leaving the campaign
      closes its cards.
  - **On a touch screen:**
    - A tap on a mention, a page link or a name on the table opens its card as a sheet at the bottom, instead of
      following the link. Open goes there.
    - A name on the sheet opens its card in the same sheet; Back or Escape returns.
    - A link with no card to show is followed as before.
  - **Links on the table.** Each name on the table opens the character and shows its card: the participant's token,
    names in the slots panel, and who rolled in the roll log. A condition chip's name links to the condition's rules in
    the Library when the campaign's packs have them.
  - Words in en, he and ru (`Layout.Preview.*`), on the page as `#codex-preview-strings`. `sw.js` is `codex-shell-v23`.
  - **Not done:**
    - Phones still show no explanation for buttons with only an icon. The tap tip skips buttons on purpose, since a tap
      on a button presses it, and a long press would be its own piece of work. Closed in Phase 7 part 7.
    - A condition's rounds left still live only in the chip's `title`. On a phone the tap tip shows them when the tap
      lands beside the name, not on it. Closed in Phase 7 part 7 (a long press).
    - A session card's date is the server's, not the viewer's time zone. Closed in Phase 7 part 7.
  - Checked in the app:
    - Desktop:
      - Card → a name on it → nested card.
      - Pin, move away: the pinned card stays and the other closes.
      - Scroll, then a name on the pinned card: the sheet opens and the card stays. Escape closes it.
      - The table's token, roll log and Blinded chip link, and Blinded's card shows its rules.
    - Phone size:
      - A tap opens the sheet; a name on it nests, and Escape goes back.
      - Open goes to the page and closes the sheet.
  - Tests: 1109 unit tests pass. E2E not run. The existing hover test still finds the card by `hover-card` and closes it
    with Escape.
- U4 (level up, rule problems, sums):
  - **Level-up stepper.** "Set level to N" is gone. "Level up to N" opens a preview of the next level.
    - How it works: the sheet is evaluated as a copy at the next level, and `LevelUpPlan` (Core, tested) compares that
      with the sheet now. Nothing changes until "Take level N".
    - What the preview shows:
      - The class gaining the level (with multiclass rules).
      - The class and subclass features of that class level, from the entries' `features` rows, in the reader's
        language.
      - Hit points gained and the new maximum. In rolled mode there is a box for the roll and a button to roll the
        hit die.
      - The proficiency bonus when it changes.
      - What becomes choosable: a subclass when it is due, and every list whose limit grows ("1 more: Feats").
      - Any rule the new level breaks.
    - The preview follows edits made while it is open.
    - After the level is taken, a to-do list stays. Each item has "Go there", which opens the tab and focuses the
      field, and is ticked off once the sheet fills it.
  - **Rule problems where they are fixed.**
    - Each problem is said under its own field: a sheet field (through `SchemaForm`'s new `Problems`), the
      attributes, the score increases, the classes panel, each Picks list and the inventory.
    - Each tab shows how many problems it holds.
    - The summary at the top stays, and each line has "Show", which opens the right tab, scrolls to the place, focuses
      it and marks it for a moment (`codex.showRule`, `data-rule-key`).
    - An increase problem is filed under its feat's list by the engine but fixed on Attributes, so it is shown there.
  - **Sums.** "Show the sums" on the skills panel takes each number apart: a skill or save into its attribute modifier
    plus proficiency or expertise, a passive score into its base plus the skill, and the spell save DC into its parts.
    The derived values (proficiency bonus, initiative) show the system's own formula.
  - "Set level to N" (`Actor.Sheet.SetLevel`) is removed. The intro and the "level taken" message are reworded, and the
    new words are in en, he and ru. `sw.js` is `codex-shell-v24`.
  - **Not done:**
    - F24's rules questions: the pooled prepared-spell limit, the class skill-choice count, and equipment weight.
    - Features that a level gives only in a class's table (the Fighter's later ASI levels are in its rows; a
      table-only feature is not).
    - Initiative's sum shows the formula ("mod(dex)"), not the numbers. Closed in Phase 7 part 7.
  - Checked in the app, on a level 2 Fighter:
    - The preview showed "Fighter Subclass" and hit points 14 → 19, with a subclass to choose.
    - Taking the level left a to-do. Go there focused the subclass picker, and choosing Champion ticked it off.
    - Level 4 showed the Ability Score Improvement and "1 more: Feats". Go there opened Picks and focused the feat
      search.
    - Problems from a feat at level 1 and a subclass too early:
      - The tabs counted 2 on Attributes and 2 on Picks.
      - Each problem showed under its field.
      - Show on a feats problem opened Picks and focused and marked the feats list.
    - The sums: a save "STR −1 proficiency +2", a skill "STR −1", Passive Perception "10 + Perception −1", and the
      proficiency bonus's formula.
  - Tests: 1112 unit tests pass (3 new for `LevelUpPlan`). E2E not run. No E2E test used "Set level to N", and the test
    ids they read (`derived-proficiencyBonus`, `skill-*`, `save-*`) still hold the number alone.
- U7 (phone bar, Library address, deleting, offline language):
  - **Tab bar by role.** A player's phone bar is Table, Sessions, My journal, Places, as before. The DM's is Table,
    Sessions, Cast, Wiki. Everything else is under More, for both. `PhoneLayoutE2ETests` now expects the DM's bar and
    opens the journal through More.
  - **Library address.**
    - A type chip replaces the history entry instead of adding one, so Back leaves the Library.
    - The search is in the address (`?q=`). Opening an entry and closing it keep it, and so do a reload and a shared
      link.
  - **Deleting** (DM only, each behind a confirm):
    - **A place:** the places inside it move up a level, and connections to it go. Undo puts it back with both, and
      its art was never removed (`RemoveLocationAsync`, `RestoreLocationAsync`).
    - **A region:** goes with its places and their art. No undo, and the confirm says how many places go
      (`IRegionRepository.DeleteAsync`).
    - **A session:** goes with its notes, log, rolls and encounters. No undo. It is refused while the table runs that
      session (`ISessionRepository.DeleteAsync`; the window closes and the list says what was deleted).
    - What stays in all three cases:
      - Mentions of a deleted place read "somewhere".
      - Pages about a deleted place stay.
      - Journal entries keep their text and lose the link.
  - **Offline page language.** The language cookie is `HttpOnly`, so `app-startup.js` keeps the page's language in
    `localStorage` (`codex-lang`). `offline.html` reads that first, then the browser's language.
  - Two persistence tests are new: the place round trip, and who may delete a region or a session (with its
    encounters).
  - Not done:
    - A deleted place's art stays on the region document. That is what makes undo work, but nothing clears it later. Closed in
      Phase 7 part 7.
    - Mentions read "somewhere" in English in every language (`SessionMentions.UnknownPlace`; also true before). Closed in
      Phase 7 part 7.
  - Checked in the app:
    - Places: delete "Probe Keep", Undo brings it back. A new region deleted, with the "no places" confirm.
    - A session deleted from its window: the list refreshed and the toast named it. Delete was refused for the session
      the table was running.
    - Library: two chips and a search added no history entries. The address read `/library/spell?q=fire`. The entry
      link and Close kept `?q=`, and a reload filled the box.
    - With `codex-lang` set to `ru`, `offline.html` came up in Russian.
    - On a phone the DM's bar was Table, Sessions, Cast, Wiki. A player's bar was not checked in the app.
- U8 (one word for one thing):
  - **Places.** The screen is "Places" in its own title too (it said "World"; the rail already said "Places"). "Put on
    the map", "On the map" and "Put a first place on the map" say "Add to Places", "In Places" and "Add a first place",
    since there is no map. "World" stays the name of the rail's group. "Region" stays: it is the group of places on that
    screen.
  - **Delete and remove.** "Delete" destroys a thing; "Remove" takes something out of something else (from the order, a
    list, the campaign). A table note now says "Delete this note", like a wiki note and a journal entry. New
    `Common.Delete`.
  - **The table runs.** "No session is running" (the sheet's private roll, the overview) now says the table is not
    running, as the table and the rail do.
  - en, he and ru for all of it. `sw.js` is `codex-shell-v25`.
  - Done later, in Phase 7 part 6: "seven places to write prose" (U8's last line). Before it: The note boxes, the journal, the wiki, Lore and the sheet's
    history do not share one editor with one set of verbs and secret syntax. That is a component merge, not a wording
    pass, and belongs with Phase 7.
  - Tests: 1112 unit tests pass, and the persistence class with the two new tests passes (80). E2E not run. No E2E test
    reads the words changed here.

## Phase 7 — Maintainability

Split the god components along the review's seams as each is touched by an earlier phase, not as a separate big-bang.
One ability-modifier function; console reads `TableRules`; role constants; CSS tokens; delete the dead code listed.

**Phase 7 status (2026-11-01): done, uncommitted.** Done: dead code, the shared rules, role constants, the duplicates,
the fail-open defaults, the settings in the README, `Program.cs`'s endpoints, all the splits the review named, the CSS
tokens, the 5e words in the engine, and one rule for prose (part 6). The gaps those parts and Phase 6 left were closed in
part 7.

- **Dead code removed.**
  - `ReconnectModal.razor.js`, a stale copy of the live one in `app-startup.js`.
  - The `Routes.razor` loading gate, `LoadingScreen` and its string, and `PluginLoader.OnPluginsLoaded`. Systems load
    before the server takes a request, so the gate never showed.
  - `LocationDocument.Connections` and `LocationConnection`. Nothing set or showed them; the player redaction and the U7
    undo that carried them are gone with them. A stored region's old field is ignored on load.
  - `CodexWorld.ApplyDamage` was already gone.
  - **Observer** is no longer offered in Settings. Nothing checks it, so it is a player by another name. A member who
    has it keeps it, and its label stays.
- **One ability modifier.**
  - `AttributeDefinition.ModifierFor(score)` and `HalfMinusFive(score)`, with `HalfMinusFiveRule` for the name.
  - The sheet's skills and vitals, `TableRules.AttributeModifier`, the formulas' `mod()`, the sheet page, the monster
    card and the loader's check all call it.
- **The console reads the table's rules.**
  - `ActorStateSnapshot` carries `IsDead` and `IsDown`, worked out by `TableRules` when the snapshot is taken, and the
    console reads those.
  - The primary, temporary and maximum keys and their defaults come from `SystemDefinition.PrimaryKeyOf`,
    `TemporaryKeyOf` and `PoolMaxKey`. `TableRules`, the console, `ActorSheetSync`, the sheet and test seeding use them,
    so "HP" and "_Max" are written once.
  - The condition names fall back to `DownRules`' own defaults.
  - `ActorsById` is built when the parameters are set, not on every read.
- **Names instead of strings.**
  - `UserRoles.Player` and `UserRoles.ServerAdmin`; `ExposurePolicy.ServerAdminRole` points at the latter.
  - `UserPrincipal.For(user)` makes the cookie identity for sign-in, sign-up and an external sign-in. Sign-in and
    sign-up now carry the email claim when there is one, as the external path already did.
  - Lore's chips are an enum. The wiki's `Mode == "edit"` is `Editing`.
- **One copy each.**
  - `SecretText.PlayersVersionOf` replaces the wiki's own, which split only on `\n` after replacing `\r\n`. It now
    handles a lone `\r` too, and `SetPlayersVersion` shares its blank-line walk.
  - Cast's summary of a stranger reads mentions through `MentionDirectory` instead of its own regex. Before, places and
    items in that text showed as raw `[[place:…]]`, and a hidden character's mention showed their public name; now it
    is "someone".
  - The secret status pill is `SecretStatusPill`, used by the wiki's secrets panel and Lore.
  - A note's kind label is `NoteKindText.Label`, used by the session page, the notes drawer and the notes about a
    character. The `Session.Kind.*` strings are gone, and so is the unused `SessionNotes.KindNoun`.
- **Fail closed.**
  - `SessionEventRedactor` drops a Damage, Healing, temporary hit points or hit dice event that has no players' version,
    unless the reader owns everyone in it. Before, it showed such an event as written, exact numbers included. "No hit
    dice left" now writes a players' version, since it hides nothing.
  - The runtime's command queue is bounded at 256. A command past that is refused with a message instead of growing the
    queue.
  - A command that fails on a `FormatException` (a dice typo) is logged at Information, not Error.
- **Settings documented.** The README has a table of the `Codex:*` and `Authentication:EnableLocalLogin` settings,
  says which ones the launcher sets, and has a line on the unauthenticated loopback RavenDB.
- **`Program.cs` 538 → 329 lines.**
  - `AccountEndpoints.MapAccounts` takes logout, set-culture and the external sign-in pair.
  - `TestSeedEndpoints.MapTestSeeding` takes the Playwright seeding, still mapped only behind
    `Codex:EnableTestSeedEndpoint`.
- **Tests:** 1126 unit tests pass, 14 of them new:
  - `AttributeModifierTests`.
  - `PlayersVersionOf` with `\n`, `\r\n` and `\r`.
  - A number event with no players' version reaches only its owner.
  - The snapshot's down and dead verdicts after damage.

  E2E not run.
- **Checked in the app:**
  - Killing the goblin marked its row dead and offered Revive. Revive brought it back at 1 HP.
  - Lore's chips filter as before.
  - The wiki's secrets list shows the "DM only" pill.
  - `/set-culture` and `/login/external` redirect; `/logout` without a token is refused (400).
- **Left for part 2:**
  - **CSS tokens:** font sizes, spacing, colours outside `:root`, the z-index ladder and the breakpoints.
  - **Component splits:**
    - `ActorSheet`: attributes, classes, level up, portrait, visibility.
    - `CombatConsole`: initiative list, command panel, dice, quick add.
    - `WikiSection`: list, viewer, editor, secrets.
    - `WorldBuilderService` and `WorldChat`.
  - **5e words in the "system-agnostic" engine:**
    - The `die/2+1` average and full first-level die.
    - "Dexterity", "all armor" and `TrimEnd('s')`.
    - `SlotTracker`'s rest names.
    - The razor reading `level`, `speed` and `class` by name. Cast's summary still does.
  - **U8's one editor for prose:** the note boxes, journal, wiki, Lore and the sheet's history.
  - The launcher log rotates only at start.

**Part 2: the component splits.** Each piece the review named that could stand alone is now a component, with what it
shows as parameters and what it changes as callbacks. The parent keeps the state the page shares.

- **`CombatConsole`, 1271 → 938 lines.**
  - `TableCommandPanel` is the DM's panel under a selected row: amount, damage, healing, temporary hit points,
    conditions, death saves, revive, undo, what players see, the turn, rests and hit dice.
    - It sends through the console's `SendAsync`, so a failure is still a message (D8).
    - Removing from the order, the confirm before "Mark dead", the visibility change and rests go back to the console.
    - What is typed in the panel now belongs to the panel, so it goes when the panel closes. Before, an amount typed for
      one creature was still there after picking another and coming back.
  - `TableDicePanel` is the dice box and roll log. It gets the rolls this viewer may see and how to name each roller.
  - `QuickAddPanel` holds the creature search; the console makes the creature.
- **`ActorSheet`, 2064 → 1781 lines.**
  - `ActorVisibilityPanel` covers who sees the character, as what, its kind, and who plays it. It edits an
    `ActorVisibilityPanel.Draft` the sheet saves, and `OnChanged` keeps the sheet's count of unsaved edits right.
  - `ClassesPanel` covers the classes, their levels and subclasses, and adding a class. The sheet keeps the list.
  - `LevelUpStepper` and `LevelUpToDo` are U4's preview and to-do list. The sheet still plans and takes the level.
  - `RuleProblemList` replaces the sheet's `ProblemList` fragment, used by the sheet and `ClassesPanel`.
- **`WikiSection`, 1275 → 1054 lines.**
  - `WikiList` is the card list, with its search, sort and kind and tag filters.
  - `WikiSecretsPanel` is the DM's list of a page's secrets.
  - `WikiLinksPanel`, used twice, holds the links from and to a page.
  - `WikiPageText` holds the kind's word and icon, the tags shown, and the latest reveal, shared by the list and the
    page.
- **Left:** the wiki editor, the sheet's attributes and portrait, the console's initiative list, and the two AI services.
  Part 3 does them.
- **Tests:** 1126 unit tests pass. E2E not run; the test ids are unchanged.
- **Checked in the app:**
  - **Table:** the command panel's +5 then Heal took the goblin from 1 to 6. Adding Poisoned cleared the box. The dice box
    rolled into the log, with roller links. Quick add found and added a Dire Wolf, then cleared its search.
  - **Sheet:**
    - Changing who sees the character counted one unsaved change. On the first try it counted none: `OnChanged` wasn't
      passed. That is fixed.
    - The level-up preview for Fighter levels 2 and 3, and taking both.
    - The to-do list's Go there focused the subclass picker.
    - The classes panel listed Fighter as the first class. Adding a second class was not checked: the test character
      meets no class's prerequisites.
  - **Wiki:** search, a kind chip and sorting by update in the list. The secrets panel's Reveal opened the page's dialog.
    Both link panels showed.
  - **A slip, found and fixed:** renaming `plan` to `Plan` also turned the test id `levelup-plan` into `levelup-Plan`.
    No other new file has a changed id or class.

**Part 3: the rest of the splits.**

- **`CombatConsole`, 938 → 645 lines.**
  - `InitiativeRow` is one creature in the order: its token, roll, turn state, hit points (exact, or a band for players),
    death saves and conditions. The console decides what the viewer may see (`CanSeeExact`) and passes the DM's command
    panel in as the row's content. Rolling a death save and removing a condition still go through `SendAsync`.
  - `InitiativeAddPanel` is the picker under the order: roll for someone, or type their total. It keeps what is typed; the
    console adds the bonus and tie-break from the sheet.
- **`ActorSheet`, 1781 → 1397 lines.**
  - `AttributesPanel` holds the scores, the method, point buy, the standard array, origin bonuses, the feats' points and
    the derived numbers. It writes the sheet the page edits and says so through `Changed`; the page re-evaluates.
  - `PortraitUpload` (the camera) and `PortraitActions` (draw with AI, remove) store the picture and record only the
    portrait (D5). The drawing has its own stop; `OnBusyChanged` keeps the sheet's other AI buttons waiting meanwhile.
- **`WikiSection`, 1054 → 609 lines.** `WikiEditor` is the whole editor: the draft, the place panel, the picture,
  templates, the AI and Save and Delete, with the unsaved-changes guard. The section loads the pages, regions and mentions
  and hands them in; each load makes a new editor. `WikiPageText.PlacePath` is now shared by the page and the editor.
- **`WorldBuilderService`, 1349 lines → four partial files:** the main one (184: the service, its fields and shared
  helpers), `.Reading` (what the chat's model is told and its tools read), `.Checking` (the check of a proposal and the
  Library's stats) and `.Saving`. Code moved as it was.
- **`WorldChat`, 1126 lines → 457:** `WorldChatLive`, `ProposalInput` (with `AdditionInput` and `ProposalEntry`) and
  `WorldChatSessions` are their own files, and the chat is in four partial files: the turn (main), `.History`,
  `.Proposal` (apply and discard) and `.Tools`. Code moved as it was; unused usings were removed.
- **Tests:** 1126 unit tests pass. E2E not run; the test ids are unchanged.
- **Checked in the app:**
  - **Table:** the rows showed hit points, the bar and conditions. Set with no total said to type one, and 14 put the
    character in the order and cleared the picker. The command panel opened inside its row. Removing Poisoned from a
    row worked. Taking the character to 0 showed the death saves, and rolling one counted a failure. Healed back to 10.
  - **Sheet:** STR 14 counted 7 of 27 points; standard array set the scores. Drawing a portrait with no image model said
    so, and the button came back. Uploading a picture and removing it, after its confirm, both kept the sheet's 12
    unsaved edits.
  - **Wiki:** a new Place page showed the place panel; Save stored it, put it in Places and opened it. Editing it again
    filled the editor, left the page out of its own link chips, and Cancel asked before dropping a change. Delete,
    after its confirm, went to the list without asking again.
  - **World builder:** the page opens; the chat was not run (no AI model in the test data).
  - **A slip, found and fixed:** a perl step emptied `AttributesPanel.razor`, and the build still passed, because an
    empty component compiles. The sheet then failed when it opened. The file was rebuilt, and the sheet was checked again.
  - **Another, found in part 4:** `WikiEditor` kept the braces that wrapped the editor in `WikiSection`, so a `{` showed
    above its title. Removed.

**Part 4: CSS tokens.** `app.css` now draws its sizes, spacing, layers and its few fixed colours from tokens in `:root`.
Values were snapped to the nearest step, so some things moved by a pixel or two.

- **Type:** ten sizes, `--fs-2xs` (0.72rem) to `--fs-4xl` (2.5rem), plus `--fs-title` and `--fs-plate` for the two titles
  that grow with the window. 137 declarations use them; 36 sizes became 12. Sizes in `em` (relative to the parent) stay.
- **Spacing:** twelve steps, `--space-1` (0.1rem) to `--space-12` (3rem), for padding, margins and gaps: 698 values.
  Layout sizes (5rem, the tab bar) and the negative margins that pull a frame to an edge stay as they were.
- **Layers:** a named ladder, `--z-sticky` (900) to `--z-splash` (99999), with a comment for each step and where
  Bootstrap's own layers fall. The reconnect modal's draft offer is `--z-recovery` (1080). Small numbers (1, 2, 20) are
  left: they only order things inside one component.
- **Colours:** none left outside the theme blocks. New tokens for what looks the same in both themes: `--scrim`,
  `--scrim-light`, `--shadow-up` and `--shadow-up-lg` (bottom sheets), `--cover-*` (text and shades over a picture),
  `--brass` and `--on-brass` (the crit die), `--die-shadow`, the four `--rarity-*`, and `--font-mono`. The avatar's
  initials use `--text`, so the light theme needs no rule of its own.
- **Tokens that did not exist:** the crit die read `--brass`, the world builder's diff `--font-mono` and `--text-muted`,
  a card `--surface-sunken`, and template marks `--tf-bg`. None was defined, so each fell back to a literal. They are
  defined now or point to the real token, and fallbacks on tokens that always exist were dropped.
- **Breakpoints:** media queries cannot read custom properties, so the widths are listed in the file's header instead.
  Nine became five: 720px (phone), 991px and 992px (Bootstrap's lg), 1199px and 1699px. 640px became 720px (the wiki's
  side picture, the Save bar), and 899.98px, 900px and 1000px became 991px and 992px (the full-screen editor's two panes,
  the homebrew wizard). Height 820px stays.
- **`!important`:** `.pill.ms-1` overrode the app's own `.ms-1 !important` with another `!important`. The pills now use
  `ms-2`, and the rule is gone. The rest are Bootstrap's logical-margin fixes, reduced motion, and phone overrides.
- `ReconnectModal.razor.css` uses the tokens too. `sw.js` is `codex-shell-v26`, since the offline shell caches `app.css`.
- **Tests:** 1126 unit tests pass. E2E not run.
- **Checked in the app:**
  - The page loads the new `app.css`, and every token it reads is defined. The only exceptions are the ones a page sets
    inline (`--cover`, `--depth`, `--attr-count`).
  - The sheet in both themes; the avatar's initials read in each.
  - On a phone, the table's command panel sits under the tab bar (940 under 950), and the wiki editor's Save bar shows.
  - Not checked: every screen side by side with the old CSS, so other one- or two-pixel shifts may exist.

**Part 5: the 5e words in the engine.** What the engine and pages assumed about the SRD is now the system's YAML, and the
SRD's `system.yaml` says it. A system that leaves a setting out gets the old behaviour where there is an obvious default
(hit points), and nothing where there is none (all armor, plurals, field roles).

- **Hit points** (`sheetRules.hitPoints`): `firstLevel` (`full-die` or `average`) and `average` (`half-plus-one`, 6 for a
  d10, or `half`, 5). `HitPointRules.AverageOf` and `FirstLevelGain` replace `die / 2 + 1` and the full first die. The
  sheet's and level up's roll boxes show the level's own average instead of working it out again. The loader rejects
  other values.
- **Armor training** (`sheetRules.proficiencies`): `allArmor` is the training name that covers every armor but shields
  (the SRD's "All armor"), and `pluralSuffix` the ending dropped when a weapon is matched to a training by name (the
  SRD's "s": "Longswords" covers a Longsword). Before, both were the English words in the engine.
- **The armor class's Dexterity line** is named after the attribute `armorClass.dexAttribute` names, in the reader's
  language, not "Dexterity". The en, he and ru strings take the name as an argument.
- **Slots** (`slotPools[].unit`): the name of one slot ("spell slot"). The slot tracker and the session log name pools,
  slots and rests through the system's translation (`values:` in `system.he.yaml` and `system.ru.yaml`, which now
  hold "Spell slots", "Pact slots", "spell slot" and "pact slot"). They no longer switch on "Spell slots", "Pact slots",
  "Long Rest" and "Short Rest", and the eight resource strings that did are gone. The general Use, Regain and log wordings
  were rewritten in Hebrew and Russian to read as well as the special ones did.
- **Field roles** (`fieldRoles`, new): which actor fields are the level, class, subclass and speed, and which fields
  Cast's summary lists. The sheet's level up, the AI's level-up suggestion, Cast's summary, the plate's speed and the
  table's hit dice read them (and the class's entity type from the field) instead of `"level"`, `"class"`, `"subclass"`
  and `"speed"`. The engine's fallbacks for a system without multiclass or hit-point rules read them too. The loader
  checks each names an actor field. `multiclass.subclassClassProperty` names the subclass's link to its class.
- **A bug this fixed:** the plate stripped " (feet)" from the speed label in English only, so Hebrew read "מהירות (רגל)
  30 רגל". It now drops any trailing parenthesis.
- **Tests:** `SystemWordsTests` (14) runs a system with other names (rank, calling, agility) with each setting on and
  off, checks the loader's new errors, and that the shipped system names its roles and translations. 1140 unit tests
  pass. E2E not run.
- **Checked in the app:** Cast's summary ("Level 1 · 10/10 Hit Points"), the plate's speed and the level-up button in
  English, and the plate in Hebrew ("מהירות 30 רגל"). The armor-class line with armor on, and slots, were not checked
  in the app (the test character wears no armor and casts no spells); the unit tests and the Hebrew log test cover them.
- **Left:** the plate's "AC" label is English in every language (it was before), and "ft" is the app's unit for speed,
  not the system's.

**Part 6: one editor for prose (U8's last line).** Every box for prose is `MarkdownEditor` (or `AiTextField`, which wraps
it), and notes stay plain (decision 5 below). One rule says which boxes take secret blocks, and every place the DM reads such a text
shows its blocks marked.

- **The rule:** a box takes secret blocks (the toolbar's button and Ctrl+Shift+H) when the DM writes text that players
  read with the blocks taken out (the repositories already remove them). So:
  - **With secrets:** wiki pages, Lore, place descriptions, the history (the DM's), the public description (new), the
    session recap (new), and the DM's notes (new).
  - **Without:** text that only its writer reads, or that players read whole: the DM's notes on a character and on a
    session, journals, a player's own notes, and the campaign description, which players read as written.
- **Public description:** was a plain text box. It is `MarkdownEditor` now, with Preview and secrets. A stranger's sheet
  draws it through `SecretRender`, like the history.
- **Session recap:** takes secrets; its preview marks them, and players read it through `SecretRender`.
- **Notes** (a wiki page's, a session's, the table's quick note and its correction): plain one-box text with @-mentions,
  as before, for speed at the table. The DM's boxes take Ctrl+Shift+H and say so in their hint (`Common.Note.SecretHint`,
  en/he/ru). `MentionText` draws a DM's view of a note with each block marked in place ("DM only", "Party"...); a
  player's copy has none. The wiki, the session page, the table's notes and "In notes" pass it.
- **Verbs:** U8's pass already made them one set: "Add …" for a new text, "Save" (or "Save …") for an edit, "Delete" for
  destroying one. Nothing here needed changing.
- **Left:** `SchemaForm`'s long text fields. Closed in part 7.
- **Tests:** 1140 unit tests pass. E2E not run; the test ids the E2E tests fill (`actor-public-description`,
  `wiki-note`, `new-note`, `quick-note`) are on the same textareas.
- **Checked in the app:**
  - A wiki note: Ctrl+Shift+H on its second line wrapped it in `:::dm`. Added, the note showed the line marked "DM only".
  - The Dire Wolf's public description: the secret button and Ctrl+Shift+H. The preview showed "DM only". The edit
    counted as one unsaved change and saved, and came back after a reload.
  - A session: the recap has the secret button, the DM summary does not. The note box takes the shortcut and shows the
    hint.
  - Not checked: a player's view in the app; the repositories' redaction is covered by tests from earlier phases.
    Checked later, in part 7.

**Part 7: the gaps left by Phase 6 and parts 4–6 (2026-11-02, uncommitted).**
- **The plate's short words.** A field may set `abbreviation` and a number `unit` (`FieldDefinition`, and the same keys in
  a translation). `system.yaml` gives Armor Class `AC` and Speed `ft`; Russian `КБ` and `фт`; Hebrew the unit `רגל`. A
  translated label with no short form of its own drops the English one, so Hebrew shows "דרגת שריון". The app's
  `Actor.Sheet.AcShort` and `Actor.Sheet.Feet` are gone. `CONTENT_PACKS.md` says so.
- **Mentions of what a reader can't see** read "someone", "somewhere" and "something" in the page's language
  (`MentionWords`, `MentionDirectory.Words`, `Event.Somewhere`/`Event.Something` in en/he/ru). The AI's recap input stays
  English.
- **Derived values' sums** show the numbers: "DEX -1", "2 + (1 - 1) / 4" (`SheetEvaluation.DerivedInputs`,
  `FormulaWorking`), with the attribute's short name in the reader's language.
- **A session card's date** is the day in the viewer's zone: the card's request sends `X-Time-Zone`, and
  `ViewerClock.ForZone` reads it.
- **A deleted place's art** is kept an hour for Undo (`RegionDocument.RemovedArt`), then dropped by the region's next
  rename, place edit or delete. A restore after that comes back without art.
- **The launcher's log** rolls over to `server.log.1` at 10 MB while the server runs, as it does on each start.
- **Long text fields** (`SchemaForm`) are `MarkdownEditor`. The DM's sheet passes `Secrets` and the mention preview;
  homebrew gets the editor without secrets, since nothing strips them there. The box now reports each keystroke, so the
  sheet's rules run as the DM types.
- **Phones:** a long press (half a second) on a control shows its title or label in the tap tip, and the press doesn't
  also press it. A condition chip's rounds show when the press lands on its name. `sw.js` is `codex-shell-v28`.
- **Tests:** 1145 unit tests pass (`SystemWordsTests`, `SessionMentionsTests`, the place-art persistence test, the
  launcher log). E2E not run.
- **Checked in the app**, DM in the built-in browser and a test player (registered on the local app, joined by invite) in
  Chrome:
  - The plate in en ("AC 9", "Speed 30 ft"), ru ("КБ 9", "30 фт") and he ("דרגת שריון 9", "30 רגל"); the sums
    ("DEX -1", "ЛОВ -1", "זרז -1").
  - The PC's Notes: the DM's box has the DM-only button; a `:::dm` line saved and came back. The player read the note
    without that line, in a box with no DM-only button. The Dire Wolf's public description reached the player without
    its secret part.
  - The session card: "2 Nov 2026" with `Pacific/Kiritimati`, "1 Nov 2026" with `Pacific/Pago_Pago`.
  - Phone emulation: a long press on an icon button showed its label, swallowed that tap, and the next tap pressed it.
  - Not checked in the app: "somewhere" in another language (unit test), the place art's hour (persistence test).

**Part 8: the last P1 (D16) and the open P2 findings (2026-11-02, uncommitted).** F25 is left for its own branch (decision 7).
- **D16, the rest.** `SecretText.KeepSecrets` lines the player's text up with the text they were given, line by line (a
  longest common run, `Align`), instead of looking for each revealed block's exact words.
  - A `:::party` or `:::players` block keeps its opening line, stamp included, even when the player changed its words.
    The lines that came from it, and the lines the player wrote in their place, stay inside it; a new paragraph beside it
    stays outside.
  - A block every line of which the player rewrote takes what they wrote between its neighbours. A block they deleted
    is gone.
  - A DM block, or a block for some characters, goes back where it stood: after the nearest line before it that the
    player kept, or after the whole revealed block that line was in. It is kept as stored, nested blocks and all. A block
    whose neighbours were all rewritten goes at the end, as before.
  - Still idempotent, so the live table's double apply is unchanged.
- **F13.** `CampaignAccessResolver.NowAsync(store, access)` reads the caller's role from the stored campaign. All 33
  repository writes that take a `CampaignAccess` (actors, wiki, regions, facts, homebrew, notes, sessions, journals,
  world chat) start with it. A co-DM demoted while their page is open writes as the player they now are; someone
  removed from the campaign, or a preview's access, writes nothing, not even their own character or journal. Reads still
  use the role the page resolved (a demoted co-DM keeps seeing DM text until the page reloads).
- **F3.** `AuthLayout` has the same `ErrorBoundary` as `MainLayout`. Sign-in and sign-up render without a circuit, so its
  "try again" is a link that loads the page again.
- **F14.** `ServerAiUsage` counts each account's requests and pictures on the server's own key per UTC day. Each account
  may make `AI:DailyLimitPerUser` (200) requests and `AIImage:DailyLimitPerUser` (20) pictures a day; 0 is no limit, and
  the ServerAdmin has none. The server's chat client is handed out wrapped (`MeteredClient`), so each request, tool calls
  in the world chat included, is counted before it is sent, and refused with `Service.Ai.ServerAllowanceSpent` once the
  allowance is spent. Every use is logged with the user id. AI settings says the key is shared and how far the reader
  has got today; the ServerAdmin sees everyone's use by name. The counts are in memory, like F19's. The README lists
  the two settings.
- **F2 (decision 6).** The hint stays. `CombatConsole.CurrentTurnName` says why.
- **F24 (decision 7), all five:**
  - **Prepared spells per class.** With several classes, `CheckEachClassesShare` gives each pick to a class that allows
    it (those only one class allows first, then the class with the most room) and names a class left over its own
    table's share (`Actor.Rule.LimitForClass`): wizard 5 / cleric 1 may prepare 9 wizard and 4 cleric spells, not 10
    wizard ones. The constant and `fromField` parts of a limit are shared.
  - **Homebrew classes.** A class with no readable hit die is named in a warning (`Actor.Rule.NoHitDie`). One with a slot
    table but no spellcasting ability, or the reverse, is too (`NoSpellAbility`, `NoSpellTable`). No shipped class
    triggers any of them.
  - **Attributes added later.** `ApplyAttributeTotals` gives an attribute the sheet has never had its default, on sheets
    that keep base scores.
  - **Skill choices.** `SkillRules.ClassChoicesProperty` (`skillChoices`) is read as a number, or the first number in its
    text. Skills chosen beyond the background's, past that count, are a warning (`Actor.Rule.SkillChoices`), since a
    species or feat may give more.
  - **Weight.** `SheetEvaluation.CarriedWeight` adds up `weight` × quantity. `InventoryRules.Capacity` is a formula
    (SRD: `str * 15`) and carrying more is a warning (`Actor.Rule.OverCapacity`). The inventory tab shows "Carrying 61 lb
    of 120 lb"; `weightUnit` is a system word, so Hebrew and Russian translate it (`ליב׳`, `фнт.`).
  - `CONTENT_PACKS.md` documents the new keys.
- **Tests:** `KeepSecrets` (an edited revealed block, hidden blocks in place, a new paragraph, a deleted block, nested and
  unclosed DM blocks); every write under a stored role; the server allowance (per account, per kind, a new day, no
  limit, a refused request before sending); each F24 item against the shipped SRD or the demo system. Tests that
  made up a `CampaignAccess` for a campaign no one stored now store it first (`TestAccess.Member`), since F13 refuses
  such writes. 1156 unit tests pass. E2E not run.
- **Checked in the app:**
  - The inventory tab: a breastplate and a battleaxe read "Carrying 24 lb of 120 lb" at Strength 8, and in Russian
    "Ноша: 20 фнт. из 120 фнт.".
  - With a dummy server key and `AI:DailyLimitPerUser=3`: the DM (server admin) sees the shared-key note and "No one has
    used them today"; the test player sees the note and "You have used 0 requests and 0 pictures on them today".
  - Not checked in the app: a spent allowance (the player's sheet has no AI button; a unit test covers the refusal), the
    sign-in error panel, the per-class spell limit, and the D16 merge (unit tests).

**Part 9: the small P3 findings (2026-11-02, uncommitted).**
- **F26.** `SchemaForm.ParseNumber` returns a `long` for a whole number (the ternary now boxes each side), a `double` only
  for a fraction.
- **F27.** A player's single-page fetch keeps links only to party pages, as the list fetch does. A link to a page not
  written yet is dropped too, so it cannot be told from a link to a DM-only page; the DM's copy keeps both.
- **F9.** `PlaceWikiSync.CreatePageAsync` returns the page the place already links to when it exists, instead of saving a
  new one over it, and writes nothing for a player.
- **F10.** `WorldChat.IsStale` counts `ApplyingElsewhere`, so the DM's tab reloads the chat with the note as an info
  flash, and nothing is logged as a failed apply.
- **F12.** The world chat's versions are kept in `DocumentVersions`; its own table is gone.
- **F4.** `CombatConsole` builds the set of actors the viewer can see once, with `ActorsById`, not twice per render.
- **F18.** "Start table" shows the busy spinner while the table starts, and a second click does nothing.
- **F7.** When two characters share a name, each suggestion adds the first line of the description that viewer may read
  ("PC · The smith's son"): the DM's own text for the DM, the text without DM blocks for a player, a stranger's public
  description.
- **F16.** At startup, `RavenDbService.ReplaceShortInviteCodes` gives any campaign still holding a code shorter than 26
  characters a new 128-bit one, keeping its expiry and single-use rule. A link handed out before D14 stops working; the
  DM shares the new one from the campaign page.
- **F17** was already done (the file is deleted and staged).
- **F11** is confirmed, not fixed (see the finding).
- **The website.** Its guide still quoted the session page's "Save recap" and "Save journal", which U6 removed, so the
  site's build failed. The guide now describes the one Save bar, and catches up with the rest of this branch: invite
  link lifetimes, retiring and bringing back characters, per-class spell limits and carried weight, undo at the table,
  "Someone you can't see", deleting a session, AI keys tied to the account and the server's shared allowance. The
  install page described backups as unavailable; it now describes the daily backup. All in English, Russian and Hebrew.
  `SiteUiKeysTests` runs the site build's key check with the app's tests, so removing a key the site quotes fails there.

**Part 10: CI on the PR, and a second review (2026-11-02, uncommitted).**
- **CI.** The Test step runs the E2E suite too, which this branch had not run. Four failed locally as well:
  - Two read `status-message` and met two of them: since U2 a sheet opens inside the campaign page and each keeps its
    own toast region. `E2EHelpers.ExpectStatusAsync` reads the one that says the text.
  - One waited for a Library URL ending in the entry's path; the search now stays in the link (`?q=`), so it compares
    the path.
  - One was an app bug U2 brought in: a character made from Cast opens in a window without the campaign page reloading,
    so Lore's "Add lore" link, the mention lists and the cast did not know of them until a full reload. `CampaignPage`
    reloads its context when a window is left, and `LoreSection` loads again when the context is a new one, writing the
    "about" text again while the DM has not touched it.
  - The Website workflow failed on the guide's two removed keys (above). All 1180 tests, E2E included, passed after.
  - On Ubuntu one test more failed: `LauncherStartupProblemTests`, which runs only there (it needs a folder it can make
    read-only). D12's key folder (`Keys` under the data folder) is made while services are registered, before the
    writability check, so a read-only folder crashed the server with a stack trace instead of the launcher's "cannot
    write" line. `Program.cs` now checks the folder first. Checked on Windows with a data folder inside a file: the
    server prints `CODEX_STARTUP_ERROR: TTRPG.Codex cannot write to …` and exits 1.
- **Review (a second model, read-only), and what came of it:**
  - **Per-class prepared spells.** Each pick went to the class with most room at the time and stayed there, so with
    three casting classes a legal set could be reported over. `SheetRuleEngine.OverEachShare` matches picks to class
    seats (augmenting paths); a class is named only when no arrangement fits.
  - **Skill choices.** With a second class the check is off: a class taken later gives skills its entry does not
    list (a rogue, one). A count in text is read from ASCII digits only; `\d` took other scripts' digits, which
    `int.Parse` refuses.
  - **Level-up "what this level breaks"** compared messages, so a list already over its limit was new at every level
    (its number moves). It compares the rule's key and what it names other than numbers.
  - **F9, again.** The guard read the caller's copy of the place, so two tabs loaded before any page existed still
    made two. It reads the stored place's page first.
  - **F10, again.** A copy refused before any save was never dropped, so the reload handed back the same one.
    `WorldChatSessions.Forget` drops it first.
  - **F14 edges.** The server's key is never handed out for a session whose account is gone (it counted under ""), nor
    in place of the user's own key that can no longer be read (every key from before decision 4 would have spent the
    shared allowance silently, instead of sending the user to enter it again). Text and pictures alike.
  - **F13 at the live table.** A sheet saved while the table runs went to the runtime with the page's role. The role is
    read from the stored campaign first, so someone removed saves nothing and a demoted co-DM saves as a player.
- **Found by the review, not fixed:**
  - **F30 (P2, lost write).** At a live table a sheet's save copies its name, visibility, public name and description,
    DM notes and owner over the table's copy whole, with no version check (the table writes the actor on every hit
    point, so a plain version check would refuse every save in play). A DM's reveal, or the AI's edit to a history,
    made after the sheet was opened is undone by its save. The sheet needs to send what it loaded for those fields, as
    it does for the sheet (`baselineSheet`), so only what it changed is copied.
  - **F31 (P2, access).** Writes that take no `CampaignAccess` at all still trust the page's role: `CreateNoteAsync`
    (wiki and session notes) and `ISessionRepository.UpdateAsync` (recap, DM summary). Give them one, and `NowAsync`.
  - **F32 (P3, homebrew).** Part 5 dropped the `class` and `level` field names the engine fell back to. A homebrew
    system with spellcasting rules and neither `multiclass.classField` nor `fieldRoles.class` gets no spell DC and no
    class armour formula, without a word. `CONTENT_PACKS.md` says a role left out turns off what reads it; the loader
    could warn.
  - **F33 (P3, table).** Undo restores only its target: concentration a condition put on its holder stays.
  - **F34 (P3).** `KeepSecrets`: two adjacent revealed blocks both rewritten whole give the first every line, and the
    second loses its opener. Party text stays party text; no leak.
  - **F35 (P3).** Backup file names are flattened with `Safe()`, so "a/b" and "a_b" can collide in one backup.
  - **F36 (P3).** `WorldChatSessions` keeps its chats in a plain `Dictionary` that a turn's save can touch off the
    circuit's thread.
- **Tests:** class seats matched as a whole; no skill warning with two classes; a limit over before and after the level
  is not new; a stale list's second "Create page"; the server key refused for a missing account and for an unreadable
  key; a sheet saved at a live table by someone removed. 1167 unit tests and all 16 E2E tests pass (one E2E run lost a test to
  its server not starting in time; it passed alone).
- **Left:** F1, F5 (bUnit), F11, F15 (a restore page), F19, F21–F23 (undo and table UX), F25 (its own branch).
- **Tests:** a whole number stored as a long; a player's single page and list agree on links; a stale "Create page" keeps
  the page; applying-elsewhere is stale; same-named suggestions told apart without a DM line; a short invite code
  replaced once and the old link refused. One older test expected a player's link to a page not written yet; it now
  expects none (F27). 1161 unit tests pass. E2E not run, nothing checked in the app.

---

## Decisions (2026-11-01)

1. **D6** — ending the table while an encounter is open leaves the session open (0.5 as written); the string stays.
2. **D11** — the scheduled backup is a JSON export **plus images** (portraits, art) into `Backups/<timestamp>/`, with
   retention; users and AI settings go in too, AI keys excluded. Restore reuses the import path (after D13).
3. **D17** — actors are soft-deleted: a `Retired` flag hides them from lists and pickers, the mention directory still
   names them, `:::for` blocks keep working, and the DM can restore them from Cast.
4. **0.2** — AI keys are bound to the user (purpose chained with the user id); everyone re-enters their keys once, and
   the AI settings page says why when a stored key no longer decrypts. No compatibility fallback.
5. **U8** — notes (a wiki page's, a session's, the table's) stay plain one-box text with @-mentions, not Markdown, so the
   table's quick entry stays fast; they follow the same secret rule as the prose boxes. Every other box for prose is
   `MarkdownEditor`.
6. **F2** (2026-11-02) — players keep reading "Someone you can't see" while a hidden creature acts: a deliberate hint, like
   a DM pausing behind the screen. Its name, rolls and place in the count stay hidden.
7. **F24 / F25** (2026-11-02) — all five F24 rules gaps are closed now, the prepared-spell limit per class included; F25
   (world-chat recovery, undoing a proposal, wiki history, the chat's size) is left for a branch of its own.
