# A mobile companion: what it would take

This is an assessment, not a plan that has been started. It answers two questions: how do we get
TTRPG.Codex onto phones, and what does the server need before any mobile client can talk to it.

## Where we are

- The whole UI is **Blazor Server**. Components call repositories, `CampaignAccess` and the live
  `CampaignRuntime` directly through DI. There is no HTTP API, and nothing is serialised for a
  client: the browser only ever receives rendered DOM diffs over Blazor's own SignalR circuit.
- Authentication is a **cookie** (local accounts plus optional Google, Apple, Discord, Twitch and
  Microsoft). Nothing issues a bearer token.
- The live table is **server-authoritative**: every mutation is an `IRuntimeCommand` record pushed
  through one single-writer loop per campaign, and `CampaignRuntime.StateChanged` re-renders every
  connected circuit. That design is exactly what a mobile client needs; it just isn't exposed.
- The stylesheet is responsive down to phone widths (this pass added the sticky section strip,
  larger touch targets and single-column layouts), so the web app already *works* on a phone.

## Three ways to get on a phone

| Option | What it is | Reuse | Cost | What it buys |
|---|---|---|---|---|
| **A. Installable web app (PWA)** — *done* | A `manifest.webmanifest`, icons and a minimal service worker for the app shell (`wwwroot/sw.js`). | Everything | Days | Home-screen icon, full-screen, correct status-bar colour, an offline page. No offline data, because the UI is a live circuit. |
| **B. .NET MAUI Blazor Hybrid** | A native shell (iOS, Android, macOS, Windows) hosting the *same Razor components* in a WebView, talking to the server over HTTP and SignalR. | All Razor markup and most component logic, once components stop depending on server-only services | Weeks | Native install and updates, push notifications, biometrics, camera for pack import, an offline character sheet, a battery-friendly table that survives backgrounding. |
| **C. Native UI** (MAUI XAML, SwiftUI, Compose) | A second UI written against the same API as B. | Server and API only | Months | The best feel, at the price of two UIs to keep in sync. |

**Recommendation.** Do the server work in the next section once, because B and C both need it and
nothing mobile is possible without it. Ship A immediately on top of the responsive web UI, then build
B. C is not worth it while the product is still changing weekly.

## The server work (the real prerequisite)

### 1. Tokens for non-browser clients
Add a bearer scheme beside the cookie one. `POST /api/auth/token` exchanges username and password
for a short-lived JWT plus a refresh token stored on the `UserDocument`; external providers use the
standard OAuth code flow with PKCE from the app and finish at the same callback. Mobile WebViews
can't share the browser's cookie jar reliably, and iOS requires HTTPS for app traffic, so the server
needs a real certificate (reverse proxy with Let's Encrypt, or a Tailscale/Cloudflare tunnel for a
home server). Document that in the README when this lands.

### 2. A thin read API
Minimal-API endpoints under `/api`, returning DTOs rather than RavenDB documents, every one resolved
through `ICampaignAccessResolver` so visibility rules (hidden NPCs, DM-only lore, HP bands for
hostiles) are applied once, server-side, exactly as the Razor pages do today:

- `GET /api/campaigns`, `GET /api/campaigns/{id}` (members, role, invite link for the DM)
- `GET /api/campaigns/{id}/actors`, `GET|PUT .../actors/{aid}` (the sheet; PUT goes through `ActorSheetSync`)
- `GET /api/campaigns/{id}/sessions`, `.../sessions/{sid}` (recap, notes, events)
- `GET /api/campaigns/{id}/library?type=&q=` and `.../library/{type}/{entry}` (packs plus homebrew)
- `GET /api/campaigns/{id}/world`, `.../lore` (already visibility-filtered by the repositories)
- `POST /api/campaigns/join/{code}`

Writes other than the sheet and session notes can wait; the DM's prep work stays on the desktop web
UI for the first release.

### 3. The live table over a SignalR hub
Add `TableHub` with one group per campaign. It is a thin adapter over what exists:

- **Client to server:** one hub method per existing command record, with the same arguments:
  `ApplyDamage`, `ApplyHealing`, `AddStatusEffect`, `RemoveStatusEffect`, `SetInitiative`,
  `NextTurn`, `PreviousTurn`, `SetTurnState`, `RollDice`, `AddSessionNote`. Each handler resolves
  `CampaignAccess` for the caller, checks the DM-only ones, and calls `runtime.EnqueueAsync`. The
  single-writer loop is untouched.
- **Server to client:** `TableSnapshot(snapshot)` on every `CampaignRuntime.StateChanged`, where the
  snapshot is a DTO (round, turn index, participants with initiative and turn state, HP exact or
  banded, active effects, the roll log and notes the caller may see). Snapshots are built **per
  role**, so a player connection never receives a hidden actor or an exact hostile HP, the same rule
  `CombatConsole` enforces in Razor today. Two snapshot shapes (DM, player) cached per state change
  are enough.
- **Join and resume:** `JoinTable(campaignId)` adds the connection to the group and replies with the
  current snapshot, so a phone coming back from the background re-syncs with one call. `RuntimeChanged`
  on the manager becomes a `TableStarted`/`TableEnded` message to the group.
- Commands carry a client-generated idempotency key, so a retry after a dropped mobile connection
  can't apply damage twice.

Once the hub exists, the web `CombatConsole` can switch to the same snapshot model, which also
removes its direct dependency on `CampaignRuntime` and makes it reusable in the hybrid app.

### 4. gRPC, or not?
Not for this. SignalR is already in the stack (Blazor runs on it), gives server push, groups,
automatic reconnect with resume, works through ordinary HTTPS proxies and has first-party clients
for .NET (MAUI), JavaScript, Java and Swift. gRPC would add a second transport, HTTP/2 end-to-end
(gRPC-Web for browsers), and a `.proto` contract that duplicates the C# command records. The one
case where gRPC earns its place is option C with a non-.NET client that wants generated typed stubs;
even then, keep the push side on SignalR (or gRPC server streaming) and use unary calls only for
commands. Default answer: REST for reads, SignalR for the table, no gRPC.

### 5. Offline (later)
A phone at the table loses signal in basements and pubs. Phase two caches the player's own sheet and
the campaign's library locally in the hybrid app and queues sheet edits; last-write-wins on
`UpdatedAt` is fine for a single player's own character. The live table itself is never offline.

## The client work for option B

New projects, in order:

1. `Codex.Client.Abstractions`: the DTOs above and an `ICodexClient` interface (campaigns, actors,
   library, sessions) plus `ITableClient` (join, commands, `SnapshotReceived` event).
2. `Codex.Client.Remote`: `HttpClient` and `HubConnection` implementations, token handling, retry.
3. `Codex.UI`: a Razor Class Library the components move into once they depend on `ICodexClient`
   instead of repositories. Start with `CombatConsole`, `ActorSheet` and `LibraryBrowser`; the web
   app registers an in-process `ICodexClient` that calls the repositories directly, so nothing
   changes for the desktop.
4. `Codex.Mobile`: the MAUI Blazor Hybrid shell: sign in, campaign list, table, sheet, library.
   Native touches that matter at a table: keep-screen-on while the table is live, haptics on your
   turn, a push notification when the DM starts the table.

## Rough order of work

| Phase | Scope | Size |
|---|---|---|
| 1 | Bearer auth, read API, `TableHub` with role-filtered snapshots, end-to-end tests that drive the hub from a second client | 2 to 3 weeks |
| 2 | ~~PWA manifest and icons~~ (shipped); switch web `CombatConsole` to the snapshot model | A few days |
| 3 | Extract `Codex.UI` and `ICodexClient`; web app keeps the in-process implementation | 1 to 2 weeks |
| 4 | MAUI Blazor Hybrid app: sign in, campaigns, table, sheet; TestFlight and internal Android track | 3 to 4 weeks |
| 5 | Offline sheet and library cache, push notifications | Later |
