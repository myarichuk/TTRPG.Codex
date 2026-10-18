# UI specification

The web UI is a Blazor Server app with a token-based stylesheet (`src/Codex.Web/wwwroot/app.css`)
over a vendored Bootstrap. Light and dark themes follow the OS preference and can be toggled from
the header; every colour pair meets WCAG AA. Nothing loads from the internet at runtime.

## Principles

1. **Plain words.** Sign in, Password, Delete campaign. No flavour copy on controls.
2. **Every control is named.** Labels (or `aria-label`) on every input, select, textarea and
   icon button; routed sections use `aria-current`; status messages are a `role="status"` live
   region; confirmations use the native `<dialog>`; `prefers-reduced-motion` is honoured.
3. **One place per thing.** One campaign list, one campaign workspace, one Library.
4. **Deep links everywhere.** Sections, Library entries, sheets and sessions are URLs.
5. **Motion explains, never decorates.** Panels and rows rise in when they appear, the current
   turn pulses, hit-point bars slide and change colour, dialogs scale in. Every animation is under
   half a second, eased out, triggered by the element entering the DOM (no JavaScript timers), and
   switched off under `prefers-reduced-motion`.

## Shell

- **Header**: translucent, blurred, sticky. Brand, top-level nav (Campaigns, Library, About),
  theme toggle, user chip, sign out. Nav labels hide below 720px.
- **Campaign workspace** (`/campaigns/{id}/...`): a sticky side rail on wide screens holding the
  campaign name, system and role pills, and the eight section links; a "Live" badge appears on
  Table while the runtime is running. Below 992px the rail becomes a horizontal, scroll-snapping
  strip that sticks under the header; the content stacks beneath it.
- **Loading**: skeleton rows (`LoadingSkeleton`) rather than spinners, so layout doesn't jump.
- **Sign in / Register** render as static server pages (`[ExcludeFromInteractiveRouting]`) so the
  form always posts with the antiforgery token and an `HttpContext`, no matter how slowly the user
  types.

## Tokens

Colours (`--bg`, `--surface`, `--surface-2/3`, `--text`, `--text-2/3`, `--accent`, `--accent-2`,
`--danger`, `--success`, `--warning`, `--hp`, `--hp-mid`, `--hp-high`), radii (`--radius-sm/md/lg/xl`),
shadows (`--shadow-1/2/3`), motion (`--ease-out`, `--ease-spring`, `--dur-fast`, `--dur`,
`--dur-slow`) and layout (`--header-h`, `--content-max`, `--gutter`) all live on `:root`, with the
dark overrides under `:root[data-theme="dark"]`.

## Screens

| Route | What |
|---|---|
| `/` | The campaigns you own or play in, as cards with cover art. New campaign dialog (name, system, description). |
| `/campaigns/{id}` | Overview: description, your characters (or the cast), table status, members, invite link. |
| `/campaigns/{id}/cast` | Player characters, NPCs and monsters with portraits. Players create (by hand or from an AI concept), edit and retire their own character; the DM reveals/hides and deletes anything. |
| `/campaigns/{id}/bestiary` | DM: a shortlist of Library creatures for upcoming encounters (note, count, "to cast"), plus monsters in play. Players: the creatures they have met. |
| `/campaigns/{id}/world` | Regions and places, reveal to party, add places from setting packs. |
| `/campaigns/{id}/wiki[/{slug}[/edit]]` | The world wiki: DM-written pages linked with `[[Page title]]`, each DM-only or shared with the party; backlinks; player notes per page; "Expand with AI". |
| `/campaigns/{id}/lore` | Facts with visibility: DM only, revealed to a character, or the whole party. |
| `/campaigns/{id}/sessions` | Planned, live and completed sessions. |
| `/campaigns/{id}/sessions/{sid}` | Player recap and DM-only summary (both with AI drafts), table notes (private or party), the reader's private journal for the session, event log. |
| `/campaigns/{id}/journal` | The reader's private notebook for the campaign: one entry per session plus free entries. Never visible to anyone else, the DM included. |
| `/campaigns/{id}/table` | The live table: initiative, hit points, conditions, dice, quick notes, quick-add creatures. |
| `/campaigns/{id}/library[/{type}[/{entry}]]` | Reference content for the system plus homebrew; the DM authors homebrew, clones a pack entry as a variant, and imports homebrew from other campaigns they run. |
| `/campaigns/{id}/settings` | Name, system, cover art, active packs, members and roles, invite link, backup, delete. |
| `/campaigns/{id}/actors/{aid}` | The character sheet, rendered from the system's `system.yaml`; portrait, history (AI rewrite), DM notes (DM only, AI draft), level-up advice. |
| `/settings/ai` | The reader's AI providers: presets (OpenRouter first), key, model; test connection; which one is the default. |
| `/library[/{system}[/{type}[/{entry}]]]` | Read-only browser across loaded systems. |
| `/about` | Loaded systems and packs with their licence attribution; content load warnings. |
| `/login`, `/register`, `/campaigns/join/{code}` | Authentication and invites. |

## Test ids

Playwright scenarios rely on `data-testid` attributes rather than copy; keep them when restyling
(`new-campaign`, `campaign-title`, `section-*`, `start-table`, `participant-row`, `hp-badge`,
`library-search`, `library-item`, `entry-attribution`, `create-character`, `sheet-save`, ...).
The initiative heading's text (`Initiative - Round N`) is asserted verbatim.

## Visibility rules the UI relies on

- **DM notes** on an actor are stripped by the repository before a non-DM receives the document;
  a player's save never writes them.
- **DM summary** on a session is only bound into the DM's editor; players get the recap.
- **Wiki** pages are DM-only until shared; a `[[link]]` to a page the reader can't see renders as
  plain text (or a "create it" link for the DM).
- **Journals** are filtered by owner inside the query; they are absent from exports and AI digests.
- **Player AI drafts** (recap, history) are built from `SessionDigest`/sheet facts that exclude
  secret rolls, private notes and DM notes.
- **Art** is served through `/media/...` endpoints that resolve `CampaignAccess` first, so a hidden
  NPC's portrait is not reachable by URL.

## Art

Covers (campaign) and portraits (actors) are RavenDB attachments on the owning document, so they
back up, export-delete and cascade with it. PNG, JPEG, WebP, GIF and AVIF up to 6 MB; SVG is
refused. URLs carry a version query so browsers cache them. Actors without a portrait get initials
on a hue derived from the name (`ActorAvatar`).

## Installable app

`manifest.webmanifest`, icons under `wwwroot/icons` and `sw.js` make the site installable. The
service worker caches only the static shell (styles, scripts, icons) and an offline page; pages,
`/media` and the Blazor circuit are never cached. Standalone mode respects the safe-area insets.

## Mobile

The layout works on phones and installs as an app; a native or hybrid companion needs an API and a
table hub first. See [MOBILE.md](MOBILE.md).
