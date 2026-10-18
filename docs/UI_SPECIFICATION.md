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
| `/` | The campaigns you own or play in. New campaign dialog (name, system, description). |
| `/campaigns/{id}` | Overview: description, your characters (or the cast), table status, members, invite link. |
| `/campaigns/{id}/cast` | Player characters, NPCs and monsters. Players create their own character; the DM reveals/hides. |
| `/campaigns/{id}/world` | Regions and places, reveal to party, add places from setting packs. |
| `/campaigns/{id}/lore` | Facts with visibility: DM only, revealed to a character, or the whole party. |
| `/campaigns/{id}/sessions` | Planned, live and completed sessions. |
| `/campaigns/{id}/sessions/{sid}` | Recap (optional AI draft), notes (private or party), event log. |
| `/campaigns/{id}/table` | The live table: initiative, hit points, conditions, dice, quick notes, quick-add creatures. |
| `/campaigns/{id}/library[/{type}[/{entry}]]` | Reference content for the system plus homebrew; DM authors homebrew here. |
| `/campaigns/{id}/settings` | Name, system, active packs, members and roles, invite link, backup, delete. |
| `/campaigns/{id}/actors/{aid}` | The character sheet, rendered from the system's `system.yaml`. |
| `/library[/{system}[/{type}[/{entry}]]]` | Read-only browser across loaded systems. |
| `/about` | Loaded systems and packs with their licence attribution; content load warnings. |
| `/login`, `/register`, `/campaigns/join/{code}` | Authentication and invites. |

## Test ids

Playwright scenarios rely on `data-testid` attributes rather than copy; keep them when restyling
(`new-campaign`, `campaign-title`, `section-*`, `start-table`, `participant-row`, `hp-badge`,
`library-search`, `library-item`, `entry-attribution`, `create-character`, `sheet-save`, ...).
The initiative heading's text (`Initiative - Round N`) is asserted verbatim.

## Mobile

The layout works on phones today; a native or hybrid companion needs an API and a table hub first.
See [MOBILE.md](MOBILE.md).
