# UI specification

The web UI is a Blazor Server app with a token-based stylesheet (`src/Codex.Web/wwwroot/app.css`)
over a vendored Bootstrap. The look is dark stone, leather and brass; dark is the default whatever
the OS prefers, and the opt-in light theme, *parchment*, is toggled from the header. Every colour
pair meets WCAG AA. Nothing loads from the internet at runtime, fonts included.

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

- **Header**: leather with a brass rule, sticky. Brand, top-level nav (Campaigns, Library, About),
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

Colours (`--bg` stone; `--surface`, `--surface-2/3` leather; `--text`, `--text-2/3` bone;
`--accent` brass and `--accent-2` bone; `--arcane` verdigris for magic and the AI helpers;
`--danger`, `--success`, `--warning`; `--hp`, `--hp-mid`, `--hp-high`, `--hp-track` blood), borders
(`--border` and `--border-strong` brass, `--rule` the dark divider between rows, `--frame` the
inner dark line that completes the two-tone border), radii (`--radius-sm/md/lg/xl`, 2 to 4px),
shadows (`--shadow-1/2/3`), type (`--font-display`, `--font-body`), motion (`--ease-out`,
`--ease-spring`, `--dur-fast`, `--dur`, `--dur-slow`) and layout (`--header-h`, `--content-max`,
`--gutter`) all live on `:root`, which is the dark theme. Parchment overrides them under
`:root[data-theme="light"]`; the inline script in `App.razor` applies the saved choice before first
paint and falls back to dark. There are no gradients, blurs or `backdrop-filter` anywhere in the
stylesheet except the scrims that fade a cover image into its card and the loading-skeleton shimmer.

### Type

Two OFL typefaces, self-hosted under `wwwroot/fonts` and recorded in `LEGAL.md`: **Cinzel**
(engraved Roman capitals; its lower case is true small capitals) for `h1`-`h3`, `.brand`,
`.attr-mod` and the `.stat-numeral` HP/AC figures, set with positive tracking; **Alegreya Sans** for
everything else. Labels (field-group legends, table heads, the side-rail group names, ability names,
feature levels, the type line above a Library entry) share one style, `.label-caps`: body face,
bold, `font-variant-caps: all-small-caps`, 0.08em tracking, `--text-3`. Do not add new uppercase
eyebrows or inline `letter-spacing`.

### Contrast

Checked with the WCAG relative-luminance formula; text needs 4.5:1 and borders of controls, focus
rings and the HP bar's fill against its track need 3:1.

| Pair | Need | Dark | Parchment |
|---|---|---|---|
| `--text` on `--surface` | 4.5 | 14.9 | 13.7 |
| `--text` on `--bg` | 4.5 | 15.9 | 11.7 |
| `--text-2` on `--surface-2` | 4.5 | 9.8 | 8.0 |
| `--text-3` on `--surface-2` (muted text, placeholders) | 4.5 | 6.3 | 5.5 |
| `--text-3` on `--surface-3` | 4.5 | 5.6 | 4.7 |
| `--accent` on `--surface` (links, labels) | 4.5 | 8.4 | 6.4 |
| `--accent-hover` on `--surface` | 4.5 | 10.7 | 8.9 |
| `--on-accent` on `--accent` (primary buttons, active chips) | 4.5 | 8.5 | 6.9 |
| `--on-accent` on `--accent-hover` | 4.5 | 10.8 | 9.5 |
| `--arcane` on `--surface` / `--bg` | 4.5 | 8.5 / 9.1 | 5.9 / 5.0 |
| `--danger` on its tint over `--surface-2` | 4.5 | 5.5 | 4.6 |
| `--success` on its tint over `--surface-2` | 4.5 | 7.1 | 4.8 |
| `--warning` on its tint over `--surface-2` | 4.5 | 6.8 | 4.5 |
| `--border-strong` on `--surface` / `--bg` | 3 | 4.8 / 5.1 | 4.9 / 4.2 |
| `--hp-high` / `--hp` fill on `--hp-track` | 3 | 3.3 / 4.6 | 4.2 / 3.3 |

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

## Components and frames

Controls are owned by the app, not Bootstrap. Every page uses these; Bootstrap's grid and spacing utilities remain, but no `btn-*`, `form-control`, `form-select` or `form-check` classes are left. The exceptions are the icon buttons in the header (theme, sign out) and the reconnect dialog, which have their own styles. A button-shaped link is `<a class="cx-btn cx-plaque">` (or `cx-etched`, `cx-sm`).

| Component | Use |
|---|---|
| `CodexButton` | One button. `Variant` is `Plaque` (brass call to action), `Etched` (default), `Blood` (destructive) or `Rune` (icon alone: pass `Label`, it becomes the accessible name). `Small` for dense rows, `Class="cx-arcane"` for the AI helpers. Extra attributes (`data-testid`, `aria-*`, `title`) go to the `<button>`. Normal height is 44px, small 38px. |
| `CodexField` | A label, the control and an optional hint tied together. The control is the caller's, with `class="field-input"` or `"field-select"` (add `field-sm` for compact) and `id="@id"`, the generated id passed to the template. |
| `CodexCheck` | A checkbox with its label. Plain markup can use `div.field-check` with `input.field-box`. |

Frames: every `.panel`, `.campaign-card`, `.participant`, `.list-picker` and dialog has the two-tone brass border, corner ornaments (one SVG mask, `--corner`, coloured by `--border-strong`) and, for panels, a titled header band. `.pill` is a chamfered plaque. `.divider-orn` is the ornamental rule.

## The sheet

A sticky plate sits under the header: portrait, name, kind, class-and-level and species plaques, then a stat strip (hit points with bar, AC on a shield, then each derived number such as proficiency bonus and initiative, and speed). The plate also holds Save, with a count of unsaved changes next to it. Below it, rule violations and warnings, then tabs: Attributes (attributes, skills, sheet fields, classes, linked Library entries), Picks, Inventory, History, and DM notes (the DM only). The "From the Library" list sits at the foot of Attributes. A tab appears only when the system or the viewer has something to put in it. The tabs are a navigation of buttons (`aria-current="page"`), not an ARIA tablist, so they are reached with Tab and activated with Enter or Space. Hit points, armor class, slots, level-up and visibility stay in the right column on every tab.

Ability plates show the modifier large and the score in a medallion; with the point-buy or standard-array method the medallion is the input.

## The table

Above the order, a sticky banner names whose turn it is (and "Turn n of m"). Each row is a token (portrait or initials, ringed in the colour of the kind: brass for characters, verdigris for NPCs, blood for monsters), the name, an initiative plaque, state badges, an HP bar with the numbers on it, and a strip of condition chips (icon, name, rounds left, and a remove button for the DM). Players see the bar only for characters they can see exactly; hostiles keep the coarse band text.

## Dice

`DiceResult` draws one roll: what it was for, each die as a face (dropped dice dashed and struck
through), the modifier, and the total in display type. A natural 20 or 1 on a single d20 is marked in
words for screen readers as well as colour. The table's roll log uses the compact form.

The sheet has roll buttons beside each ability, save, skill and spell attack, and a Dice panel with a
Normal / Advantage / Disadvantage choice. The latest roll also shows in the sticky plate so it stays
in view while scrolling. During a live session the roll goes into the table's shared log under the
character; with no session it is rolled and shown to that user only, and the panel says so.

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
