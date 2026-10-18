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
