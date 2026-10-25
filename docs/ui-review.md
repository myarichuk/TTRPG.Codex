# UI review: usability, cross-links, phone screens

Reviewed on 2026-10-25, branch `feat/srd-translation-tooling`. I walked the running app as the DM and as a player, at 1024px and at 375px (phone). The code audit references are relative to `src/Codex.Web/Components`.

The yardstick is Owlcat's Pathfinder games (Kingmaker, Wrath of the Righteous), not corporate design guidelines. Three things from those games apply here:
- **Every term is a link to an explanation**, shown as a tooltip card you can pin and open further links from (the encyclopedia).
- **Big screens are windows over the game**: the character sheet, inventory and journal open over the play area, and closing one returns you exactly where you were.
- **Dense, scannable stat blocks**: portraits in an initiative bar, icons for conditions, and a hover breakdown of every roll.

The app has the right look already (the frames, plaques and token avatars). What it lacks is the behaviour. Nearly every action navigates to another page, and the phone layout is a squeezed desktop.

---

## 1. Bugs found during the walk (fix regardless of redesign)

**Status (2026-10-25): all ten are fixed.**
- **Header (B2, B8, B9):** on wide screens the language is a dropdown; at ≤720px the language, theme and sign-out options move into one menu.
- **Unsaved text (B5):** the new `UnsavedChangesGuard` component covers it. It asks before in-app navigation and before Cancel/Escape inside the page, and the browser prompts on tab close.
- **Regression test:** `PhoneLayoutE2ETests` checks the main pages at 375px.

| # | What | Where | Effect |
|---|---|---|---|
| B1 | **Combat HP bars always look full.** `.hp-bar > span` also matches the `.hp-over` number overlay, so the label gets the fill colour at 100% width. | `app.css:778` and `CombatConsole.razor:676` | A goblin at 0/7 and Mara at 45/45 show identical full red bars. |
| B2 | **The header does not fit on a phone.** The language buttons, theme toggle, user chip and sign-out never collapse. | `AppHeader.razor`, `app.css:854` | The page is 684px wide on a 375px phone, so every page scrolls sideways. At 1024px it is 1037px wide, which also scrolls. |
| B3 | **Hidden actors leak their id in the dice log.** `ResolveRollerName` falls back to `entry.ActorId`. | `CombatConsole.razor:1081` | A player sees `a-mara` rolling, which names a hidden NPC. |
| B4 | **Wiki page delete has no confirmation.** Neither does wiki note delete. | `WikiSection.razor:60`, handler at `:429`, note at `:164` | One click removes a page for good. |
| B5 | **Unsaved text is lost silently** in the wiki editor, session recap/summary/journal, journal and location dialog. Only the actor sheet has a `NavigationLock`. | various | I typed into the wiki editor, navigated away, and the edit was gone without a prompt. |
| B6 | **"Played by" shows user ids** (`u-dm`, `u-player`) instead of usernames. | `ActorSheet.razor:786`, `CastSection.razor:102` | |
| B7 | **Missing space in quotes**: "Aria Vell**to** Mara of the Tides". The leading space inside `<span> @L[...]` is trimmed. | `SessionDetail.razor:164`, `TableNotesDrawer.razor:208` | |
| B8 | **Header icon links lose their names at ≤720px.** The `NavLink`s have no `aria-label`, and the text span is `display:none`. | `AppHeader.razor:13-23` | Screen readers announce "link". |
| B9 | **The language buttons are all named "Change language"** (also "החלפת שפה"). | language picker | Screen readers can't tell the three apart. |
| B10 | **"Mara of the Tides's turn"**: the possessive is built by appending `'s`. | combat turn label | |

## 2. Phone screens (375px)

The phone layout is the weakest area. A player at the table holds a phone, so this matters more than desktop polish.

> **Status:** items 1–9 are addressed, along with the P7 shell (without P2's window routes):
> - **Sections:** campaign sections are a bottom tab bar (Table, Sessions, Journal, Places, More), and the campaign title is one line inside a section.
> - **Sheets:** Library entries and the journal editor open as full-screen sheets with a Back bar.
> - **Actor sheet:** tabs scroll in one row, abilities are a 3×2 grid, and Save sticks to the bottom when there are unsaved changes.
> - **Combat:** the command panel is a bottom sheet with +1/+5/+10 amount buttons.
> - **Touch:** targets are at least 40px on touch screens, and note actions no longer wait for a hover.
> - **Tooltips:** tapping anything with a `title` shows it as a bubble.
> - **Tables:** wide tables scroll on their own.
>
> `PhoneLayoutE2ETests` covers the tab bar, the sheets and the command panel.

1. **Horizontal page scroll everywhere** (B2).
2. **Chrome eats the screen.**
   - On campaign pages, the campaign title (4 lines in display font), badges and the section strip come before the content.
   - On the table page the first initiative row starts about 1.5 screens down.
   - Once scrolled, the sticky header, section strip and initiative controls take about 300 of 812px.
3. **The Library is unusable on a phone.** Tapping an entry renders its detail below the 150-row list, about 18,000px down the page. Nothing visibly happens. The same happens in the Journal, where the editor renders under all entries and starting an edit neither scrolls nor moves focus.
4. **The actor sheet hero fills the whole first screen.**
   - The breadcrumb takes 3 lines and the buttons are stacked.
   - The tab bar wraps to 4 rows.
   - Six ability cards take about 3 screens in a 2-column grid.
5. **The combat command panel expands inline** under the selected row: amount, type, crit, Damage/Heal/Temp HP, condition, rounds. On a phone this pushes the rest of the order off screen. It wants to be a bottom sheet.
6. **Touch targets are too small.**
   - Under 40px: note edit/delete 28px, condition chip remove 26px, language buttons 28px, chips about 32px.
   - The participant select button has 0 padding, so only the name text can be tapped.
   - Note actions sit at opacity .55 until hover, and hover doesn't exist on a phone.
7. **Information is hidden in tooltips.** Condition rules, effect expiry, exhaustion and initiative explanations live only in `title=`, which phones never show.
8. **Number inputs have no steppers on phones.** Only one field sets `inputmode="numeric"`. HP damage and heal have no quick buttons.
9. **Tables have no overflow wrapper**: the HP rules table (6 columns, in a sidebar), monster `.ref-scores` (nowrap) and markdown `.rules-table`.

## 3. Links: names behave differently in each place

These links work:
- Mentions (`@actor`, `#place`, `$item`) are rendered as links in the recap, table notes, wiki comments, journal, biography (read-only), "Said" lines and backlink panels.
- Cast, Overview and Bestiary link actor names.
- On the actor sheet, picked spells/feats, inventory, class and "From the Library" link to the Library.

These don't:
- **The live table has no links at all.** It has 0 `<a>` tags. Combatant, turn banner, quick-add creature (no stat-block preview), slot tracker and roll-log names are all plain text. There is no link to the running session either.
- **Speaker and author names are plain everywhere**: notes drawer, session notes, wiki notes, "Notes mentioning".
- **Mentions can't be typed in many fields**:
  - wiki body (it has its own `[[Page]]` syntax, which links wiki pages only)
  - location description, lore details, campaign description, sheet long-text fields (backstory and the like), bestiary note and session event log.

  So there are two link languages (`@ # $` versus `[[ ]]`). Neither has autocomplete in the wiki, and a wiki page can't be @-mentioned anywhere.
- **Authors never see their own links.** The DM's recap, DM summary, biography (for whoever can edit it) and DM notes are only ever a textarea. Links render for readers only, and there is no preview.
- **Library entries have no internal links.** A monster's spells, a class's subclasses and condition names inside rules text are all plain.
- **On the sheet**, the subclass, increase grants and reference select fields don't link.
- **Every link is a full page navigation.**
  - From the table, a mention lands on the actor sheet. That is a separate route, so it has no notes drawer and no section rail, which contradicts the intent stated in `TableNotesHost.razor:7-9`.
  - From a dirty actor sheet, a spell link triggers the "leave page?" prompt.
- **Places link to `/world#place-…`.** The description there is inside a collapsed `<details>`, so the link lands on a closed row.

## 4. Edit boxes and save patterns

- **No textarea autosizes.**
  - Long-form fields are small: lore details 3 rows, sheet long text (backstory, notes) 4, DM notes 6.
  - The session journal has 7 rows in a 258px-wide side column.
  - Adding a note has 2 rows (inflated by `min-height: 6rem`).
  - The wiki body (18 rows, monospace markdown) is the only roomy field, and it has no toolbar or preview.
- **Odd or inconsistent sizes.**
  - Ability score inputs are 58×30 px with no border.
  - Combat inputs are `width:auto`.
  - Inline widths are scattered across the code: 4.5rem, 5rem, 6rem, 9rem, 14rem and 68px.
  - The quick-add creature search is too narrow to show its own placeholder ("Search creatures in th").
- **Saving works differently on every page.**
  - Actor sheet: one Save button with a dirty count and a leave guard. Portrait changes save at once.
  - Session detail: four separate Save buttons, no guard.
  - Bestiary: autosave.
  - Settings: two forms plus role changes that save on select.
  - World: a dialog with Save, except art, which saves at once.

  Pick one model: autosave with a "Saved" status on long text, and explicit Save only where a change is a decision.
- **Destructive actions without confirmation**:
  - wiki page and note delete (B4)
  - cover, portrait and art remove
  - AI provider remove
  - invite regenerate
  - role change on select
  - combat remove and Mark dead

  An undo toast is better than a confirm dialog for most of these.

## 5. Proposals

### P1. Peek cards for every name (Owlcat encyclopedia tooltips)

One component, `<Ref Kind Id>`, used for every actor, place, wiki page, session, library entry and condition name in the app, including inside Library rules text.

| Platform | Click / tap | Other gestures |
|---|---|---|
| Desktop | Opens a pinned card next to the name. The card holds a portrait or icon, key stats, and the first paragraph or stat block. | Cards can be pinned and nested, like Owlcat's tooltip chain. Ctrl/middle-click opens the full page. |
| Phone | Opens a bottom sheet with the same card and an "Open" button. | |

Replace the tooltip-only `title=` info (conditions, expiry, initiative, roll results) with the same card. This fixes the table having no links, the hidden rules text, losing your place on navigation, and the drawer disappearing. It is also where the rest of the app's links come from.

### P2. Windows over the campaign instead of separate pages

Turn the actor sheet, a Library entry, a wiki page, a place and a session into **window routes**. They stay URLs, but they render over the current section as a large modal (desktop) or a full-screen sheet (phone). Close returns you to exactly where you were: the table, scroll position and drawer intact.

Add keyboard shortcuts like the games have:

| Key | Opens |
|---|---|
| C | My character |
| I | Inventory |
| J | Journal |
| L | Library |
| N | Notes (exists) |

Native `<dialog>` + `codex.showModal` already exists (`ConfirmDialog`, `HomebrewWizard`, Cast create). Extend it with:
- a `guardModal`-style unsaved-changes check
- backdrop-click close
- a single `WindowHost` in `CampaignPage`

### P3. Combat console rebuilt around an initiative bar

- **Initiative bar.** A horizontal, sticky strip of round portrait tokens: current turn highlighted, an HP ring around each token, condition icons under it. On a phone this one strip replaces the turn banner and the round controls. Next/Previous turn becomes a floating button.
- **Command panel.** Selecting a token opens the existing command panel:
  - on desktop, as a docked action bar at the bottom
  - on a phone, as a bottom sheet

  Add quick damage buttons (−1 −5 −10, ±custom) and condition icons instead of a text box.
- **Roll log entries** show a breakdown card on tap: dice, modifiers, advantage, target.
- **Adding monsters.** A single "Add to encounter" window replaces the two separate paths (quick-add and the bestiary shortlist). It has search with a stat-block preview, a count, and an option to roll initiative.

### P4. "My character" dock for players at the table

The player's table view shows nothing about their own character. Add a compact card at the bottom with:
- HP, with damage and heal
- AC and conditions
- spell slots
- roll buttons for saves and skills

Tapping it opens the full sheet as a window (P2).

### P5. Actor sheet, denser and in Owlcat's paper-doll shape

| Area | Desktop | Phone |
|---|---|---|
| Header | Left column: portrait, name, chips, and the HP/AC/initiative/speed plaques. Not sticky. | One row of name and HP, plus a compact strip of stat plaques. |
| Abilities | The six abilities as a 6×1 (desktop) or 3×2 (phone) strip. | Same. |
| Skills | Two columns with the proficiency dot inline. | Same. |
| Tabs | Vertical tabs along the side. | A horizontal scroll strip, not wrapped rows. |
| Save | Autosave with a status line. The Save button only appears for decisions. | Same. |
| HP | One component instead of the three HP editors (Sheet field, HP panel, header plaque). | Same. |

### P6. A text editor that links

One `RichNoteField` replaces every long-form textarea. It:
- autosizes, with a sensible minimum per field
- has `@ # $` autocomplete everywhere: wiki body, places, lore, backstory, campaign text
- adds `[[` wiki pages (and `@`-able wiki pages) to the same picker, so there is one link language
- has a Preview/Edit toggle, so authors see their links
- has a light toolbar (bold, list, heading, link) for markdown

### P7. Phone shell

- **Header**: brand, a section title, and a ☰ or avatar menu that holds the language, theme and sign-out options.
- **Campaign section strip** becomes a bottom tab bar with 5 items: Table, Sessions, Journal, World, More.
- **Campaign title** shrinks to one line in the header on inner pages.
- **Lists that open a detail** (Library, Journal, Wiki, Cast) open the detail as a sheet (P2), never below the list.
- **Touch targets** at least 40px, and no hover-only actions.

## 6. Suggested order

1. **Bugs B1–B10.** Small and independent. B1, B2 and B3 are the urgent ones.
2. **P7 phone shell and P2 window host.** These are structural, and the rest builds on them.
3. **P1 peek cards.** Biggest gain in "links work everywhere".
4. **P3 combat console and P4 player dock.**
5. **P6 linking editor and P5 sheet density.**
6. **Unify save patterns and destructive-action handling** (undo toasts).
