# TTRPG.Codex — Code Review, Gap Analysis & Remediation Plan

*Snapshot of `main` @ `d735f00`, reviewed 2026-09-25.*

## 0. Method

"It looks wrong" isn't a finding. Every claim below comes from one of these:

- **Read**: all ~6k lines of C#/Razor/AXAML under `src/` and `tests/`, plus every doc in `docs/` and `conductor/`.
- **Built and tested**: `dotnet build` gives 0 errors and 17 warnings. `dotnet test` passes 68/68.
- **Probed**: I wrote throwaway xUnit probes against the real types to confirm or reject suspected bugs. Results are marked **[confirmed]**. One suspicion, that `DurationSystem` skips entities when it removes components mid-iteration, turned out to be **wrong**. DefaultEcs handles that case, so it is not listed.
- **Ran it**: I started `Codex.Web` and drove registration and login with headless Chromium (Playwright). Results are marked **[verified live]**.

Severity scale: **Critical** means a security issue or data loss. **High** means a feature is broken end to end. **Medium** means incorrect behavior with a workaround. **Low** means hygiene.

---

## 1. Bugs

### Critical

**B1. Anyone can become DM by registering the username `dm`.**
`Register.razor:314`: `if (Model.Username.ToLower() == "dm") user.Roles.Add("DM");`. Whoever registers first gets the global DM role. The `DM` role is also **global**, not per campaign. There is no model in which Alice runs campaign A and plays in campaign B. The `"DM"` authorization policy (`Program.cs:86`) is declared and never used.

**B2. No data authorization at all.**
`CampaignDocument` has no owner, members, or roles (`Models.cs:3`). `CampaignRepository.GetAllAsync()` returns *every* campaign in the database to *every* authenticated user. Any user can delete any campaign from `/` (`Home.razor:172`) or `/campaigns` (`CampaignList.razor:528`). The Player/DM visibility model in `docs/PLAYER_DM_MODES.md` has no representation in code: no visibility fields and no query filtering. `RavenNoteRepository.GetNotesForTargetAsync` takes an `isDm` flag from the caller, so it trusts the UI to say who is a DM.

**B3. The script "sandbox" can kill the world for every connected user. [confirmed]**
`ScriptEvaluator` exposes `CodexWorld`, which exposes `InnerWorld` (the raw DefaultEcs `World`). Content-pack script `world.InnerWorld.Dispose()` runs, and afterwards `Entity.IsAlive` throws `NullReferenceException`. In the web app `CodexWorld` is a **singleton** (`Program.cs:110`), so one bad pack takes the whole server down. DynamicExpresso blocks reflection by default, so this is DoS and state corruption, not remote code execution. It is still not a sandbox.

**B4. The script interpreter is a shared, stateful singleton. [confirmed]**
`ScriptEvaluator` holds one `Interpreter` and calls `SetVariable` on every run (`ScriptEvaluator.cs:44-50`):
- **Stale target.** `target` is set only when `context.Target.HasValue`. A cast with no target runs against the *previous* caller's target. The probe cast "burn" on B, then a no-target "frozen", and B ended up frozen.
- **Cross-circuit race.** The interpreter is a DI singleton shared by all Blazor circuits. Two concurrent executions overwrite each other's `caster`, `target`, and `world`.
- Errors go to `Console.WriteLine` and are swallowed. Authors and the DM never see them.

### High

**B5. Packs exported by the Authoring app load zero content. [confirmed]**
`ContentPackExporter` uses `ZipFile.CreateFromDirectory`, which writes entries as `abilities/fireball.yaml`. `YamlContentPackLoader.LoadFromZipAsync` matches on `entry.FullName.Contains("/abilities/")`, with a leading slash, so it never matches. The probe exported `fireball` and got `GetAbility("dnd5e:fireball") == null`. Separately, `PluginLoader.LoadContentPacksAsync` only looks for `manifest.json` in directories, so `.cdx` and `.zip` files are never discovered anyway. **The authoring → runtime pipeline does not work at either end.**

**B6. An entity can have only one status effect. [confirmed]**
`CodexWorld.AddStatus` does `entity.Set(new StatusEffectComponent(...))`, and ECS allows one component per type per entity. In the probe, Poisoned (10 rounds) followed by Stunned (1 round) left only Stunned. When Stunned expired, `DurationSystem` removed the status component, so the target was no longer poisoned either. Concurrent conditions are the most basic thing a campaign tool has to track.

**B7. Damage-as-component loses hits and doesn't clamp. [confirmed]**
`DamageEvent` is a component, so two hits in the same tick overwrite each other. HP 10 took 3 + 4 and ended at 6. There is no clamping either: HP 2 took 50 and ended at −48. There is no temp HP, no "Shield before HP" (which `DESIGN.md` claims), and no damage types. `SwffgWoundSystem` has the same event-overwrite problem.

**B8. One global ECS world for the whole server.**
`CodexWorld` is a singleton shared by every user, campaign, and circuit. DefaultEcs `World` is **not thread-safe**, and Blazor Server circuits call into it concurrently. `/demo` (`CharacterDemo.razor`) creates entities in the global world, never destroys them, and calls `World.Tick(0.016f)`. That advances **every** duration on the server by 0.016 "rounds". `Tick(float deltaTime)` mixes up wall-clock time and game rounds. Durations in a TTRPG are integer rounds that expire at the start or end of a specific creature's turn.

**B9. Ability inheritance duplicates instead of overriding. [confirmed]**
`AbilityDefinition.MergeFrom` *appends* the base's `Effects`, `Costs`, `Requires`, and `Triggers` to the child's. A homebrew Fireball overriding `8d6` with `10d6` resolves to **`10d6 + 8d6`**. That contradicts the "override" semantics in `DESIGN.md`. There are more problems in the same area:
- Base `TypedComponent` instances are shared by reference, so mutating a child mutates the base.
- The base is resolved only if it is already registered. Resolution depends on filesystem enumeration order. `PackManifest.Dependencies` is ignored, and a missing base is silently skipped.
- Priority ties (`priority >= existing`) are decided by `Directory.GetFiles` order, which is nondeterministic across OSes.

**B10. The release artifacts are almost certainly non-functional.**
The first three issues are certain from the code. The fourth is high-risk and was not tested on a clean machine.
- `release.yml` publishes with `PublishTrimmed=true`. Trimming breaks reflection-loaded plugins, YamlDotNet, DynamicExpresso, and RavenDB. Plugins reference `Codex.Core` members that the trimmer can't see being used.
- The `plugins/` folder isn't part of the publish output, so the release zip ships with **no game systems**.
- `Codex:PluginsDirectory` is `../../plugins` relative to the content root, which points outside the published folder.
- Embedded RavenDB runs as a child process, `dotnet .../RavenDBServer/Raven.Server.dll`, as seen in the live run. The "self-contained" build therefore still needs a .NET runtime installed on the host.

**B11. Linking external logins by email is an account-takeover vector.**
`Program.cs:211-218`: if an external login's email matches an existing user, the provider is attached to that account. The code never checks `email_verified`. Any provider or configuration that issues unverified emails lets an attacker take over a local account. Also in `Program.cs`:
- `/login/external?provider=<anything>` is passed straight to `Challenge`. An unknown scheme gives a 500.
- An Apple PEM key passed inline is written to `/tmp/apple_key_<guid>.p8` and **never deleted** (`Program.cs:73`).
- `/logout` is a GET, so any cross-site image tag can log a user out. This one is low severity.

### Medium

**B12. RavenDB read-your-writes and uniqueness.**
Every list query runs against an auto-index without `WaitForNonStaleResults`. After create or delete, `LoadData()` can show the old list. Username uniqueness is "query, then insert" (`Register.razor:299`, `Program.cs:232`), which is a race and also runs against a possibly-stale index, so duplicate usernames are possible. Use compare-exchange (cluster-wide transactions) for uniqueness.

**B13. Deleting a campaign doesn't cascade.** Characters, sessions, notes, and facts with that `CampaignId` are orphaned forever.

**B14. System identity is inconsistent.** New campaigns are hardcoded to `System = "D&D 5E"` (`CampaignList.razor:546`), but the plugin's `SystemId` is `"DnD5e"`. The campaign is never linked to a loaded plugin. The UI branches on `campaign.System.Contains("DnD")` and hardcoded Unsplash URLs.

**B15. The Home page "Oracle" calls Gemini with a hardcoded empty API key** (`Home.razor:189-190`). It bypasses the configured `IChatClient`/`LoreGenerator` and always fails. If someone fills the key in, it goes into the query string and ends up in logs.

**B16. `PluginLoader` failure handling.**
- `IsLoaded = true` even when loading threw, and there is no retry.
- Plugins are loaded with `Assembly.LoadFrom` into the default load context, with no isolation or unloading.
- `Codex.Web` *also* project-references `Codex.Systems.DnD5e`, so there are two copies (bin and `plugins/`), and which one wins depends on load order.
- `ICodexSystemPlugin.RegisterSystems(dynamic world)` uses `dynamic` to get around a layering problem. The fix is to move the world abstraction into `Plugin.Abstractions`.

### Low

- **B17. Signing in while already authenticated fails [verified live].** Visiting `/login` with a live circuit and submitting shows *"HttpContext is not available"*. A fresh, unauthenticated login and registration both work.
- **B18.** The startup log says `http://localhost:5000` (`Program.cs:136`, also in the README), but `launchSettings.json` binds `5183`.
- **B19. Dependency hygiene.** There are 2 high-severity transitive CVEs: `Tmds.DBus.Protocol` 0.21.2 via Avalonia, and `SSH.NET` 2025.1.0 via Testcontainers. `System.Diagnostics.PerformanceCounter Version="*"` makes builds non-reproducible. `NoteDocument.TargetId` has a nullability warning.
- **B20. The UI depends on the internet.** It loads Bootstrap, `lucide@latest` (unpinned, no SRI), Google Fonts, Unsplash, and Gravatar from CDNs. A local-first tool breaks at a table with bad Wi-Fi. Gravatar also sends users' email hashes to a third party.
- **B21. Dead chrome.** Every search box, "Manage", "New Session", "Add Player", "Add Entry", "Generate", "Analyze Party", the filter chips on `/grimoire`, and "Create Adventure" is a no-op. `/players` and `/grimoire` render hardcoded empty lists.

### Docs vs. reality

`conductor/plan.md` marks phases 3–5 **[DONE]**. In reality:
- `DynamicFormView` is not reachable from navigation. Only the Dashboard and Graph nav items do anything.
- Nothing saves or loads.
- `ContentPackExporter` is never called.
- "Load Pack" has no handler.
- The graph editor can't create edges or persist anything.

`DESIGN.md` and `CAMPAIGN_ARCHITECTURE.md` describe several things that **do not exist**: scene hydration and flushing, dirty flags, `PeriodicEffectSystem`, Shield-before-HP, "Missing Content" warnings, `schema.patch.yaml`, and `RegionDocument`/`SceneDocument`.

### Unwired code (exists, never used at runtime)

- `ISessionRepository`
- `INoteRepository`
- `FactDocument` + `KnowledgeIndex`: nothing writes facts, nothing queries the index.
- `LoreGenerator`
- The `"DM"` policy
- `ContentRegistry.ExecuteAbilityAsync`
- `ICodexSystemPlugin.GetUISchemas()`: nothing in the web app reads it.
- `ComponentRegistry`: written, never read.
- `SessionDto`: a duplicate of `SessionDocument`.
- `SystemRegistration`: an empty class.
- `AbilityScoresComponent`, `ConditionComponent`, `CharacteristicsComponent`, `StrainComponent`: no system reads them.

### What's actually good

This isn't a teardown. The bones are reasonable:
- Embedded RavenDB with per-aggregate documents is the right call for this domain.
- The content-registry priority/override idea is right, even though the merge is wrong.
- The plugin boundary exists.
- Password hashing uses `PasswordHasher<T>`.
- There is a fallback "require authenticated" policy.
- Open-redirect protection (`UrlHelper.IsLocalUrl`) is present and tested.
- The test suite runs an in-memory RavenDB, which is exactly the harness the rest of this plan needs.

---

## 2. Gap analysis: current state vs. running a campaign

A GM uses a campaign tool in four loops. The table compares what each loop needs with what exists today.

| Loop | What a GM/player actually needs | Today |
|---|---|---|
| **Setup** | Create a campaign with a real system; invite players; per-campaign DM/player roles | Creates "Unnamed Legend" hardcoded to "D&D 5E". Can't rename, open, or invite. Roles are global, and one of them is up for grabs (B1). |
| **Prep** | NPCs, locations, factions, secrets (with visibility), encounters, prepared notes; pull monsters/spells from content packs | Nothing. There is no `/campaigns/{id}` page. Pack content is loaded into the registry but **never shown in any UI**. `/grimoire` is a static empty list. |
| **Characters** | Player-owned sheets driven by the system (stats, HP, conditions, abilities, inventory); DM sees all, players edit only their own | `CharacterDocument { Name, State: Dictionary }`. No create/edit UI. The plugin `UISchema`s exist but the web app never renders them. No link between saved characters and the ECS. |
| **At the table** (the core job) | Start a session; initiative tracker; HP/damage/heal/temp HP; **multiple** conditions with round-based expiry; dice roller with a shared log and secret DM rolls; quick rules/monster lookup; DM changes pushed live to players with visibility filtering; quick notes | None of it. The ECS is one global, thread-unsafe world (B8) that can hold one status per entity (B6) and loses simultaneous damage (B7). No dice engine. No initiative. No per-campaign live state. No broadcast between DM and player circuits: Blazor Server re-renders only the circuit that made the change. |
| **Rules** | Use an ability: check requirements → pay costs → apply effects → fire triggers | `ExecuteAbilityAsync` runs only `Script` effects and ignores Requires, Costs, and Triggers. It is unwired and unsafe (B3/B4). |
| **After the session** | Recap (optionally AI-drafted from the event log), XP/loot, timeline, "what does the party know now" | `SessionDocument` and `LoreGenerator` exist but nothing uses them. Nothing records events, so there is nothing to recap. |
| **Between sessions** | Players read what they have discovered (visibility-filtered lore, known NPCs), add personal notes, DM shares | The knowledge index exists, but no facts are ever written. The notes repo exists with no UI. There is no visibility model on entities. |
| **Content** | Ship at least one usable system pack (SRD); homebrew; import/export | No packs ship. ZIP import is broken (B5). The Authoring app can't save. |
| **Ops** | Runs on a laptop at the table, offline; backups; one-click release | The UI needs CDNs (B20). No backup or export. Release artifacts are broken (B10). |

**Bottom line:** the only working vertical slice today is *register → log in → create a nameless campaign → delete it*. All the infrastructure pieces exist, but nothing connects them into a workflow a GM would use at the table. The next milestone has to be one real vertical slice, not more breadth.

---

## 3. Remediation plan

Principles:
1. **Correctness and security first.** A tool that leaks DM secrets or lets anyone delete campaigns isn't worth adding features to.
2. **Build one vertical slice end to end:** *"A DM runs a combat encounter with two remote players watching live."* Everything else is parked until that works.
3. **Enforce visibility on the server.** Players' circuits must never *receive* hidden data. Hiding it in markup is not enough.
4. **Every phase ships with tests that would have caught its bugs.**

Sizes: **S** ≈ ≤1 day, **M** ≈ 2–4 days, **L** ≈ 1–2 weeks.

### Phase 0 — Stop the bleeding (security & correctness)

Independent, small PRs. Each one lands with a regression test reproducing the bug above.

| # | Fix | Size |
|---|---|---|
| 0.1 | Remove the `dm` username → DM role grant (B1). Keep only a `ServerAdmin` role for the first user or a config-seeded admin. DM becomes a **per-campaign** role (Phase 1). | S |
| 0.2 | Temporary guard until Phase 1 membership exists: only the creator can delete a campaign. Store `OwnerId` now (B2). | S |
| 0.3 | External login: link by email only if the provider asserts `email_verified`. Otherwise create a separate account, and link only from an authenticated session. Whitelist `provider` against registered schemes. Remove the temp-file PEM (use an in-memory `IFileInfo` or a configured path). Make logout a POST with an antiforgery token (B11). | M |
| 0.4 | Username uniqueness via compare-exchange `usernames/{normalized}` inside a cluster-wide transaction (B12). | S |
| 0.5 | Rewrite `ScriptEvaluator` (B3/B4). Compile each script once with `Interpreter.Parse(script, typed Parameters)` and cache the `Lambda` by script text. Invoke with explicit args: no shared mutable variables, thread-safe, faster. Expose a narrow `IScriptApi` facade (e.g. `Damage(target, n)`, `AddStatus(...)`, `Roll("2d6")`) instead of `CodexWorld`/`InnerWorld`. Errors go to `ILogger` and back to the caller as a result object. | M |
| 0.6 | Zip loader: normalize entry paths and dispatch on the **first path segment**. `PluginLoader` discovers `*.cdx`/`*.zip`. Add an exporter → loader round-trip test (B5). | S |
| 0.7 | Inheritance: define the semantics. A child list **replaces** the base list unless the YAML says `merge: append`. Deep-clone the base. Load packs in topological order over `Dependencies` and `inherits`. A missing base is a load error that names the pack. Deterministic tie-break on `(priority, packId)` (B9). | M |
| 0.8 | Cascade delete via `DeleteByQuery` on a `ByCampaign` static index, or archive instead of delete (B13). | S |
| 0.9 | Release: drop `PublishTrimmed`. Copy `plugins/` into the publish dir. Resolve the plugins dir from `AppContext.BaseDirectory`. Add a CI job that runs the published artifact, waits for `/login` → 200, and checks the log for "Loaded plugin". Decide the RavenDB-runtime story (framework-dependent publish, or bundle the runtime for Raven) (B10). | M |
| 0.10 | Delete the hardcoded Gemini call and route "Oracle" through `LoreGenerator`, or remove it (B15). Fix the port log/README (B18). Pin `PerformanceCounter`. Bump or override the vulnerable transitive packages (B19). | S |
| 0.11 | Update `conductor/plan.md` and `docs/*` to mark what is *specified* vs *implemented*. Docs that lie are worse than no docs. | S |

**Exit:** all of B1–B16 have a failing-then-passing test or a CI check.

### Phase 1 — A domain model that can hold a campaign (persistence before UI)

| # | Work | Size |
|---|---|---|
| 1.1 | `CampaignDocument` gains `OwnerId`, `SystemId` (validated against loaded plugins), `ActivePacks`, `Members: [{UserId, Role: DM\|Player\|Observer}]`, `InviteCode`, `CurrentSessionId`. Rename `System` → `SystemId` with a one-off patch migration. | M |
| 1.2 | **Actor instances** (PC/NPC/Monster) as `actors/{id}`: `CampaignId`, `Kind`, `OwnerUserId` (null for NPCs), `BlueprintId` (a pack actor reference, so a missing pack shows "Missing Content" instead of crashing), `Visibility: Hidden\|Unknown\|Known` + `PublicName`/`PublicDescription`, `State` (a component snapshot, see 1.5). Replaces the bare `CharacterDocument`. | M |
| 1.3 | `LocationDocument`/`RegionDocument` (the graph lives in one doc per region, as the architecture doc says), `FactDocument` + `Visibility`, `NoteDocument` (fix `TargetId`), `SessionDocument` + `Status: Planned\|Live\|Completed` + `Encounters`. `EncounterDocument`: participants, initiative order, round, turn pointer, active effects. Delete `SessionDto`. | M |
| 1.4 | **The access layer is the security boundary.** Every repository call takes a `CampaignAccess(userId, role)` resolved from membership. Player queries filter `Visibility`/ownership **in the Raven query**, never in Razor. Static indexes: `Actors_ByCampaign`, `Notes_ByTarget`, `Sessions_ByCampaign` (ordered by date), `Facts_ByKnower` (the existing `KnowledgeIndex`, scoped by campaign). Authorization tests: a player cannot load a hidden NPC by id, cannot delete anything, and cannot see another player's private notes. | L |
| 1.5 | Give `ComponentRegistry` its real job: a stable **type-name ↔ Type map for (de)serializing component snapshots**, so `State` round-trips into ECS components and back. Test: serialize → deserialize → equal. | M |

### Phase 2 — The prep loop (DM UI)

| # | Work | Size |
|---|---|---|
| 2.1 | `/campaigns/{id}` with tabs: Overview · Cast · Locations · Lore · Sessions. Rename/edit the campaign. Pick the system from loaded plugins at creation. | M |
| 2.2 | Schema-driven Blazor forms from `ICodexSystemPlugin.GetUISchemas()`. This is the web renderer of the same `UISchema` the Authoring app uses: one schema, two renderers. | M |
| 2.3 | Content browser: search the registry (abilities, actors) for the campaign's system and packs. "Add to campaign" instantiates a blueprint into an actor instance. Wire up the existing search boxes. | M |
| 2.4 | Invites: an invite link or code that adds a member as Player. `/players` becomes the campaign's member list. | S |
| 2.5 | Visibility controls on actors, locations, and facts, plus a "Reveal to party" action. | S |

### Phase 3 — The table loop

This is the phase that makes it a campaign tool.

| # | Work | Size |
|---|---|---|
| 3.1 | **`CampaignRuntime`, one per live campaign**, replacing the global `CodexWorld` singleton (B8). It owns its own `CodexWorld`. All mutations go through a `Channel<ICommand>` drained by a **single loop**. DefaultEcs isn't thread-safe, and a single writer means no locks, no races, and deterministic replay. Hydrate from `EncounterDocument` + participants on start. After each command, persist dirty actors in one Raven session. Command rate at a table is ~1/s, so write-through is cheap and simpler than a 30-second flusher. Evict the runtime when idle. | L |
| 3.2 | **The command log is the session log.** Each applied command appends a `SessionEvent`. That gives the recap raw material, an audit trail, and undo (inverse commands) for free. | M |
| 3.3 | **Live broadcast.** The runtime publishes `StateChanged(campaignId, affectedIds)` to an in-process per-campaign hub. Each circuit subscribes, re-projects through **its own** `CampaignAccess` (DM: exact HP; players: "Bloodied" or hidden, per campaign setting), and calls `InvokeAsync(StateHasChanged)`. | M |
| 3.4 | **Fix the ECS model (B6/B7).** `StatusEffectsComponent { List<ActiveEffect(EffectId, SourceId, RoundsRemaining, Expiry: StartOfTurn\|EndOfTurn, AnchorActorId)> }`. Damage and healing are **commands** applied immediately with clamping, temp HP first, and a plugin hook for resistances. No event-components. Durations tick on **turn advance** (integer rounds), not `Tick(float)`. | M |
| 3.5 | Initiative tracker: add participants, roll or enter initiative, handle ties, next/previous turn, round counter, delay/ready. | M |
| 3.6 | Dice: an expression parser (`2d6+3`, `4d6kh3`, advantage/disadvantage), plus a plugin extension point `IDiceRoller` so SWFFG narrative dice fit. Shared roll log with DM secret rolls. | M |
| 3.7 | DM combat console and player view: HP, damage/heal, apply/remove conditions, quick-add monsters from packs, quick notes auto-linked to the live session. | L |

**Exit, as an automated Playwright end-to-end test:**
1. The DM creates a campaign and invites two players.
2. The DM starts a session and runs three rounds against three goblins.
3. Each player sees their own HP change live.
4. Players never receive goblin HP numbers or the hidden NPC. This is asserted on the rendered DOM **and** the circuit payload.
5. Poisoned + Stunned coexist and expire independently.
6. The session log contains every command.

### Phase 4 — After and between sessions

| # | Work | Size |
|---|---|---|
| 4.1 | Session recap editor, pre-filled from the event log. Optional AI draft via `LoreGenerator.SummarizeSessionAsync`, with explicit opt-in. Nothing is sent to an LLM by default. | M |
| 4.2 | Knowledge: record who learned which facts, and the reveal flow. The player-facing Grimoire shows only facts known to their PC, via the (finally used) `KnowledgeIndex`. | M |
| 4.3 | Player character sheets: schema-driven, editable only by the owner and the DM. Personal and shared notes per `PLAYER_DM_MODES.md`. | M |
| 4.4 | Campaign export/import (a JSON bundle) and scheduled RavenDB backups to the data dir. | M |

### Phase 5 — Rules depth

Start this only after Phase 3 is in real use.

| # | Work | Size |
|---|---|---|
| 5.1 | A TRCE pipeline:<br>• `Requires`: predicate handlers.<br>• `Costs`: resource deduction, **atomic with the effects**. That comes naturally from the single-writer command loop.<br>• `Effects`: typed `IEffectHandler`s registered by plugins, keyed by `Type`, with `Script` as the fallback.<br>• `Triggers`: a runtime event bus (`OnTurnStart`, `OnHit`, …). | L |
| 5.2 | Plugin API cleanup: replace `RegisterSystems(dynamic)` with an interface in `Plugin.Abstractions`. Load plugins in a collectible `AssemblyLoadContext` that shares the abstractions assembly. Remove the direct DnD5e project reference from `Codex.Web`. | M |
| 5.3 | Ship a sample SRD 5.1 pack (CC-BY-4.0, see `Systems.DnD5e/LEGAL.md`) as the reference pack and a test fixture. | M |

### Phase 6 — Authoring app

This decision is up to you. My recommendation is to **park it** until Phase 3 ships. In-browser editing (Phase 2.2) covers homebrew for most tables. If it continues, the minimum to make it real is:
- Wire `DynamicFormView` into navigation.
- Add workspace save/load.
- Hook up export through the fixed exporter (0.6).
- Add import of existing packs.
- Persist graph edges.

### Cross-cutting (continuous)

- **Offline:** vendor Bootstrap, Lucide (pinned), and fonts into `wwwroot`. Make Gravatar opt-in (B20).
- **Tests:** bUnit for components. Authorization tests on every repository method. The Playwright end-to-end test from Phase 3 runs in CI.
- **Observability:** `ILogger` everywhere (no `Console.WriteLine`). Surface pack-load and script errors to the DM in the UI.
- **Cut or hide dead chrome** (B21) until the feature behind it exists. A button that does nothing teaches users the tool is unreliable.

### Suggested order

`0.x` (parallel small PRs) → `1.1, 1.4` → `1.2, 1.3, 1.5` → `2.1–2.5` → `3.1 + 3.4` (the runtime is the risk, so do it first) → `3.2, 3.3` → `3.5–3.7` → end-to-end gate → Phase 4 → Phase 5.
