# Encounter builder: design

Status: designed 2026-11-15, open questions decided the same day (the recommendations; see section 12). Building from 2026-11-23,
step by step (section 10). What it depended on is on main: the hazards PR (`SetEncounterPlaceCommand`, `HazardsTable`, `PlacedHazard`),
H5 (complex hazards in initiative, `ActorDocument.Hazard`) and H6 (world builder hazards), so the "after H5" parts can be built in their
steps rather than waited for. It reuses the world builder chat (WORLD_BUILDER.md) for its AI part. An MCP server is a later phase of this
design (section 8).

**The party** (the readout's characters, and any count of them) is the campaign's player characters whose sheet kind is a party member:
`SheetLayout.IsPartyMember(system, sheet)`, since P11 (COMPANIONS_DESIGN.md) a companion or familiar is a player-character actor whose
kind says `partyMember: false`, and it does not count.

## What it is

Today a fight is put together at the table: the GM finds creatures in Quick Add (`QuickAddPanel`), each one becomes a cast member
(`CombatConsole.QuickAddMonsterAsync`), and the first initiative roll creates the live `EncounterDocument`
(`CampaignRuntime.GetOrCreateEncounter`). Nothing sets `EncounterDocument.LocationId`, so the Hazards panel's place picker starts on
"All places". The Bestiary has a shortlist "planned for upcoming encounters" with counts and notes, but nothing turns it into a fight.

The encounter builder lets the GM prepare encounters before the session:

- **Prepare** one by hand: a name, a place, the creatures (Library, homebrew or existing cast members) with counts, notes, and a
  difficulty readout when the system declares how to work one out.
- **Prepare** one with AI, in the world builder chat: the model reads the place, its hazards and the Library, proposes an encounter,
  and the GM reviews it before it is saved.
- **See** the prepared encounters of the campaign, by place and by status.
- **Start** one at the table. Its creatures join the cast and roll initiative, and the encounter's place is set, so the Hazards panel
  shows that place's hazards straight away. The picker stays as the manual override (the user's decision in HAZARDS_DESIGN.md 11, H4b).

Everything about a prepared encounter is GM only. Players see nothing of it until it starts, and then only what the table shows them
today (creatures by what they are, as Quick Add does).

## 1. Principles

1. **A prepared encounter is a plan, not a fight.** It is its own document. The live `EncounterDocument` stays what it is: the state of
   one fight in one session.
2. **Hazards are not copied.** A hazard's state belongs to its placement (`PlaceFeature.Hazard`). A prepared encounter names a place,
   and reads that place's hazards live. Placing a hazard is done on the place (the Places form, or the world builder per H6).
3. **No game numbers in C#.** The difficulty readout is data in `system.yaml`. A system that declares nothing gets no readout.
4. **The model proposes, the GM applies.** The AI writes through the world builder's one review and save path.
5. **Plain, accessible copy; en, ru, he.** No trademark names.

## 2. Data

```csharp
namespace Codex.Persistence;

/// <summary>An encounter the GM prepared ahead of the session. GM only. Starting it at the table makes the live EncounterDocument.</summary>
public class PreparedEncounterDocument
{
    public string Id { get; set; } = string.Empty;              // preparedencounters/<guid>
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;            // "Ambush in the cellar"; can spoil a secret, so GM only everywhere
    public string? RegionId { get; set; }                       // the place, as the hazard keys name it (hazard:{regionId}/{locationId})
    public string? PlaceId { get; set; }                        // a LocationDocument id; null: no place (the panel shows "All places")
    public List<PreparedLine> Lines { get; set; } = new();
    public string? Notes { get; set; }                          // markdown, GM only: tactics, terrain, what sets it off, the way out
    public List<string> Tags { get; set; } = new();
    public PreparedStatus Status { get; set; }                  // Draft, Ready, Ran
    public List<string> RunEncounterIds { get; set; } = new();  // the live encounters started from it, newest last
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string? FromChatId { get; set; }                     // the world builder chat that proposed it, if any
}

/// <summary>One line of the lineup: a Library or homebrew creature with a count (the BestiaryEntry key shape), or one cast member.</summary>
public class PreparedLine
{
    public PreparedLineKind Kind { get; set; }                  // Creature, CastMember, Hazard (after H5: the initiative tick of a complex hazard placed at the place)
    public string EntryType { get; set; } = "monster";          // Creature, Hazard: the Library type
    public string EntryId { get; set; } = string.Empty;
    public bool IsHomebrew { get; set; }
    public string? ActorId { get; set; }                        // CastMember: an existing actor (a named villain already on the cast)
    public string Name { get; set; } = string.Empty;            // as it was when added, for a line whose entry is gone
    public int Count { get; set; } = 1;                         // Creature: 1 to 20, as the Bestiary clamps it; CastMember and Hazard: 1
    public string? Note { get; set; }                           // "waits behind the door"
    public bool StartsHidden { get; set; }                      // true: Hidden (players do not see it in the order); false: Unknown (seen by what it is)
}
```

- A creature line uses the same key as `BestiaryEntry.Key` (`pack|homebrew : type : id`), so the builder can take lines from the
  Bestiary shortlist in one click, and a pack and a homebrew entry with the same id stay two different choices.
- A **hazard line** would be a complex hazard picked from the search's Hazards group. A hazard's state has one home, its placement
  (HAZARDS_DESIGN 4.1, H4b: live state under `hazard:{regionId}/{locationId}`), so a line with no place would get no Notice, no Disable
  and no write-back. **Recommended:** choosing a complex hazard in the editor places it on the encounter's place (a feature of that
  place, made when the plan is saved), and the line is then only the "joins initiative" tick of that placement; with no place chosen,
  the Hazards group is disabled with "Choose a place first". Open question 10. Before H5 the Hazards group is not offered.
- **What the encounter shows of its place**: the place's hazards and those of its child places, read live through the same walk the
  Hazards panel uses (`HazardsTable.Chain`, moved to a shared helper), with their state and, after H5, whether each joins initiative.
  A disabled or triggered hazard is shown but does not join.
- **The live encounter** gains one field: `EncounterDocument.PreparedEncounterId` (string?), so the table can show the plan's notes to
  the GM and the prepared encounter can say when it ran.

### 2.1 The difficulty readout (optional, data only)

A system may declare how hard a lineup is for the party. Two shapes cover the systems in view, and the block says which it uses:

```yaml
# system.yaml
encounters:
  difficulty:
    # Shape 1, "sum": each creature is worth a field of its entry; the party's budget is a per-character row by level.
    shape: sum
    creatureValue: xp                 # a Number field of an actorBlueprint type (a Number wherever a type has it)
    unit: XP                          # optional; the word after the total ("250 XP"); translated
    partyLevel: level                 # optional: the sheet field the budget reads per player character; default fieldRoles.level
    pick: atMost                      # optional, default atMost: a budget is the most a band allows (atLeast: what a band starts at)
    bands:                            # in order, easiest first; keys never translate, labels do (system.<locale>.yaml)
      - { key: low, label: Low }
      - { key: moderate, label: Moderate }
      - { key: high, label: High }
    budget:                           # one row per character level, one whole number per band (per character)
      - { level: 1, low: ..., moderate: ..., high: ... }
    # Shape 2, "byLevel": each creature is worth a value looked up by (its level - the party's level); the budget is per party size.
    # shape: byLevel
    # creatureLevel: level            # a Number field, like creatureValue
    # values: [ { delta: -2, value: ... }, { delta: -1, value: ... } ]    # deltas go up by exactly one, no gaps
    # partySize: 4                    # the size the band budgets are written for
    # bands:                          # under byLevel each band carries its own budget (no separate budget list)
    #   - { key: low, label: Low, value: ..., perCharacter: ... }   # perCharacter: added (or taken) for each character above (or below) partySize
```

As built, see section 16.

- The readout says the party (the campaign's player characters and their levels), the lineup's total, and the band it falls in, in
  words ("Moderate for 4 characters of level 3"), never only a colour. A line whose entry has no value says so; the readout does not
  guess.
- The numbers are written in their own build step from the system's licensed text, as the SRD traps were (a reviewed data file, a test
  that compares it). **None are written from memory**: the first hazard draft from memory was wrong almost everywhere (HAZARDS_DESIGN 11,
  H2). srd5e takes them from the SRD 5.2.1 document (CC BY 4.0): it has "Combat Encounter Difficulty" with an "XP Budget per Character" table (Gameplay Toolbox, "Combat Encounters", printed p. 202; checked 2026-11-15), which is shape 1 (sum of creature XP against a budget per character and difficulty). The degrees system declares shape 2 when its pack exists (track P).
  Forged in the Dark declares none: a fight there is a clock and a consequence, not a budget.
- `SystemDefinitionLoader` checks the block: a known shape, fields that exist with the right kind, whole numbers, rows in level order,
  no repeated level. Schema and `SYSTEM_YAML_REFERENCE.md` regenerated.
- Complex hazards count only when the system says how (a `hazardValue` field); otherwise the readout lists them apart as "not counted".

## 3. Storage

- `IPreparedEncounterRepository` / `RavenPreparedEncounterRepository`, with an index `PreparedEncountersByCampaignIndex`.
  Methods: `GetAsync(id, access)`, `ListAsync(access)`, `SaveAsync(doc, access)` (returns false when refused), `DeleteAsync(id, access)`,
  `DeleteAllForCampaignAsync(campaignId)`, and `MarkRunAsync(id, encounterId)` (one patch: adds the run and sets `Ran`, so a GM editing the
  plan at the same time is not overwritten; `RavenUpdate.RunAsync` as the place patch does).
- **Every read and write takes `CampaignAccess` and refuses a non-GM at the repository**, not only in the UI. Writes also refuse a party
  preview and `SystemMissing`, as other repositories do. A co-GM reads and edits them like the GM (unlike world chats, which are private
  to the GM who started them): a prepared fight is shared table prep.
- **Separate from `EncounterDocument` on purpose.** `IEncounterRepository.GetAllForCampaignAsync` is read by the campaign export and by
  `BookSourceReader` (the GM edition's fights), and `CampaignRuntimeManager.FindOpenEncounterAsync` restores any encounter with no end. A
  plan stored there would show up as a fight in the book and could be "restored" at the table.
- Joins the cascade delete in `CampaignRepository` beside `encounterRepository.DeleteAllForCampaignAsync`.
- **Export**: included in the campaign export, which is GM only (`CampaignExportService.ExportAsync` refuses a non-GM), as prep like the Bestiary shortlist, restored on import with new ids and its place
  and actor ids mapped as the export maps the others. Never in the book, either edition.
- Deleting a place leaves the prepared encounter with no place (the page says "its place was deleted"); deleting a cast member drops
  that line, shown once as "no longer in the cast". Neither deletes the plan.

## 4. Services and the engine

### 4.1 `PreparedEncounterService` (Codex.Web/Services)

The one place prepared encounters are read, checked, saved and started. The pages, the world builder and later the MCP server all
call it.

| Method | What it does |
|---|---|
| `ListAsync(campaign, access)` | Prepared encounters with their place's name and path, line count, status, last run |
| `ViewAsync(campaign, access, id)` | One in full: lines resolved against the Library (entry gone: kept with its stored name and a warning), the place's hazards read live, the readout |
| `RateAsync(campaign, access, lines)` | The difficulty readout (2.1) for a lineup and the campaign's party, or null when the system has none |
| `CheckAsync(...)` | Errors and warnings for a draft: unknown entry, a cast-member line naming a player character (an NPC such as one the world builder cast is fine), count out of range, a place that does not exist, a complex hazard line before H5 |
| `SaveAsync(campaign, access, doc)` | Through the repository; stamps `UpdatedAt` |
| `StartAsync(campaign, access, id, runtime)` | Starts it at the table (4.2) |

### 4.2 Starting a prepared encounter

Two halves, because saving actors happens off the runtime loop and the initiative order changes only on it.

1. **Off the loop** (`PreparedEncounterService.StartAsync`):
   - Refuses unless the table is running for the campaign (`CampaignRuntimeManager.TryGet`) and the caller is a GM.
   - Refuses when an encounter is already running with participants ("End the current encounter first", see Open question 2).
   - Creates the creatures through `CastService.AddToCastAsync` (the Bestiary's path: seeded from the blueprint, sheet kind applied,
     numbered "Bandit 1, Bandit 2" when the count is more than one). `AddToCastAsync` gains a `visibility` argument that **defaults to
     `Hidden`**, so the Bestiary's *Add to cast* is unchanged; the start passes `Unknown`, as Quick Add does (players see "a bandit joins
     the fight"), or `Hidden` for a line ticked `StartsHidden`. Cast-member lines reuse their actor and keep its visibility. `TryCreateActorAsync` enqueues `HydrateActorCommand` for each, and the loop is first in, first out, so they are
     on the table before step 2 runs.
   - Today `InitiativeBonus` and `TieBreak` live in `CombatConsole.razor`. They move to a shared pure helper (`TableInitiative`) that
     the console calls, and the command calls on the loop (below), so a creature rolls the same either way.
2. **On the loop**: one `StartPreparedEncounterCommand(PreparedId, Name, RegionId?, PlaceId?, PlaceLabel, ActorIds, RollerUserId)`,
   with the GM's user id as the roller. **Recommended shape:** the command works out each creature's initiative expression and tie-break
   itself, from `runtime.LiveSheetOf(actor)` through `SheetRuleEngine.EvaluateLive`, as H4a's check service does on the loop. The
   service then needs neither the page's engine nor a snapshot of actors that were only just hydrated. (Opus settles this in B3.) It:
   - Refuses (no change) if an encounter with participants is running by the time it runs.
   - `GetOrCreateEncounter()`, then sets `LocationId` and `PreparedEncounterId`. This is needed because `SetEncounterPlaceCommand`
     does nothing without an active encounter.
   - Applies one `RollInitiativeCommand` per creature, so the roll log, the refill of counters on entering the order and the redaction of
     a Hidden actor's roll are exactly today's.
   - Writes one event: "Encounter started: {name} at {place}", both names `EventTerm.GmOnly`, plus the initiative lines as today.
   - Then the service calls `MarkRunAsync` with the new encounter id.
   - **Player characters** are not rolled: the order opens with the existing `InitiativeAddPanel` listing the party, and players roll their
     own as today (Open question 3).
   - Undo: the start is not one undoable step. The GM ends the encounter or removes a participant, as today. The creatures stay on the
     cast, as Quick Add's do.

**The Hazards panel.** `HazardsTable` reads `Snapshot.Encounter?.LocationId` only on its first load. It must also take it again when
the running encounter's id changes (it already reloads then, through `Under()`), so a panel that was open before the start moves to the
new place. That is the one change to the panel. The picker still overrides it, through `SetEncounterPlaceCommand`.

**After H5.** A complex hazard placed at the place or under it, and not disabled or triggered, joins initiative: the service makes its
actor through H5's `ToActorBlueprint` overload, and its roll uses the hazard's own initiative field (the system's hazard block names it).
(A complex hazard added in the editor was placed on the place when the plan was saved, so it is one of these.) Each placed complex hazard has a tick on the prepared encounter, "Joins the
initiative order", on by default; the GM can untick one (a rolling stone that has not been set off yet).

## 5. UI

### 5.1 The Encounters section

A new GM-only section, **Encounters** (`/campaigns/{id}/encounters`, key `encounters`, icon `swords`), in the World group after
Bestiary. Hidden from players the way `build` and `book` are in `CampaignPage`'s section filter.

**The list.** Each prepared encounter as a row: name, place (with its region), creatures in one line ("3 bandits, Wendel Tarr"),
the readout's band in words, status (Draft, Ready, Ran on {date}). Filter by place and by status, search by name. Buttons: *New
encounter*, *Build with AI* (opens the world builder with a starter, as the wiki's *Build with AI* menu does). An empty state says what
an encounter is for in one sentence and offers both buttons.

**The editor** (`/encounters/{id}`, and `?new=1`):
- *Name*, *Place* (a picker of the campaign's places as a tree, with "No place"), *Notes* (the markdown editor the wiki uses).
- *Creatures*: the lineup, one row per line with a count field (1 to 20), a note, a "Starts hidden" check box and a remove button.
  Under it an add search over the Library's creature types and homebrew (`CampaignLibrary`, as the Bestiary searches), a second tab for
  the cast, and *Add from the Bestiary shortlist*.
- *Hazards at this place* (read only): each hazard at the place and under it, with its state in words and a link to the place. After
  H5, complex ones carry the "Joins the initiative order" tick. When the place has none: "No hazards are placed here. Place one on the
  place's page." with a link.
- *Difficulty* (only when the system declares it): the readout as a sentence in a polite live region, updated as lines change.
- *Start at the table*: enabled only while the table is running; otherwise its text says "Start the table to run this encounter" and
  links to the table. Starting goes to the table.
- *Save* and *Delete* (delete asks first; it cannot be undone).

**The search for creatures is a new reader of the actor registry** (when it lists pack creatures through `ContentRegistry`), so it splits
by the `hazard` tag as HAZARDS_DESIGN 3.3 requires: creatures in one group, complex hazards in their own "Hazards" group, never mixed
(HAZARDS_DESIGN section 7 already reserves this row for the encounter builder). A test pins it.

### 5.2 At the table

- In the encounter bar beside *Start encounter*: *Start a prepared encounter*, a menu of the Ready ones, those at the place last used
  first. Choosing one asks once: "Start {name}? {n} creatures join the cast and roll initiative." It calls the same `StartAsync`.
- While an encounter started from a plan runs, the GM column shows its notes in a folded panel, *Encounter notes*, with a link to the
  plan. Never drawn for a player.

### 5.3 Accessibility and strings

- Every count field, check box and remove button has a label naming the creature ("How many bandits", "Remove Bandit"). The lineup is a
  list; the add search announces how many results it has in a polite region. Everything works by keyboard. The readout and every
  warning are text. Check RTL in Hebrew (the count field and the remove button change sides).
- Strings in a new prefix `Encounters.` with its own `Strings.Encounters{,.ru,.he}.resx` (a new prefix needs its own files, or the keys
  fall back to `Strings.resx`). Table strings under `Combat.Prepared.*` in `Strings.Combat`, the log line under `Event.Encounter.Started`
  in `Strings.Event`. Section label `Page.Campaign.Section.Encounters`. Russian and Hebrew need native review, as all the others do.

## 6. AI tools (in-app, the world builder chat)

The encounter tools join the **existing** world builder chat. It is the same chat, the same access, the same fenced results and the same
review panel. The chat does not get a second write tool: an encounter is a new **proposal kind** in `propose_changes`, so one proposal
can make a cellar, place a homebrew trap on its strongbox (H6), make a homebrew creature and the encounter that uses all three, linked by
refs, and the GM reviews it once.

### 6.1 Read tools (added to `WorldChat.Tools`)

All are GM only (`DmOnly(access)` in `WorldBuilderService`), return short capped text, and are fenced as campaign data.

| Tool | Returns |
|---|---|
| `list_encounters(place?)` | Prepared encounters, one line each with key `existing:encounter:<id>`, place, lineup in short, status |
| `get_encounter(encounter)` | One in full: lines with their `type/id` or `existing:actor:<id>`, notes, the place's hazards, the readout |
| `place_hazards(place)` | The hazards at a place (`existing:place:<id>`) and under it: entry `type/id`, name, state, the place it is on, complex or not. Feature notes that name a creature ("a mimic") are listed too |
| `rate_encounter(creatures)` | The readout (2.1) for a draft lineup, or "this system declares no difficulty", so the model can try before it proposes |

`search_library(query, type)` already searches any type, so `type: "hazard"` finds hazards with no change. The system prompt gains the
party (count and levels, which the readout needs anyway) and two lines on encounters: build on what the place already has (its hazards,
its feature creatures), say in the notes what the creatures want and how the fight can end other than by killing.

### 6.2 The proposal kind

```json
{"ref": "f", "kind": "encounter", "name": "Ambush in the cellar", "place": "c | existing:place:<id>",
 "creatures": [ {"entry": "monster/bandit", "count": 3, "note": "behind the casks"},
                {"entry": "h"},
                {"entry": "existing:actor:<id>", "hidden": true} ],
 "text": "notes, markdown", "tags": ["grey-coast"]}

{"ref": "g", "kind": "edit", "target": "existing:encounter:<id>", "rename": "...", "place": "...", "creatures": [...], "text": "..."}
```

- `WorldBundleParser` gains `DraftKinds.Encounter`: `place` resolves like a place's parent (a place ref in this proposal, or an existing
  place); a creature `entry` is a Library `type/id` (as Bestiary picks are found), a homebrew ref in this proposal whose type is a creature
  type, or an existing cast member. A hazard in `creatures` is refused with a pointer: "place it on the place's feature (`feature.hazard`)",
  (after H5, a complex one is placed through the place too and then ticked to join). Counts 1 to 20.
- An edit of `existing:encounter:<id>` replaces what it names (lines in full when `creatures` is given), with the review's stamp check
  as other edits have: refused at saving if the plan changed since the review.
- `CheckAsync` adds the readout to "ready for review" when the system has one, as information, never an error.
- The review panel shows an encounter entry as its place, its lineup (count and name, each linked), its notes and the readout. Leaving out
  a place leaves out an encounter set in it (as leaving out a place leaves out what is inside it).
- `SaveAsync` saves encounters **last** among new things (after places, homebrew, the Bestiary and the cast, before edits), so every ref
  is settled. Status `Draft`, `FromChatId` set. Saved as GM only, like everything the builder saves; starting is never done by the AI.
- The encounters page's *Build with AI* starter: "Prepare an encounter at {place}" when opened from a place, else a numbered starter in
  the chat ("An encounter for the next session").

### 6.3 Secrecy

- Nothing in this design reaches a player: the repository refuses them; the section, the table's menu and notes panel are drawn for GMs
  only; the start's log line has its names as `GmOnly`; creatures join as `Unknown` or `Hidden`, which the table already redacts.
- Tool results are campaign text, so they are fenced with the per-result tag as all world builder reads are; a plan's notes that say
  "ignore your instructions" are read as notes.
- The model never starts an encounter, never reveals anything, and never applies its own proposal.

## 7. How it meets H5 and H6

| Step | What this design needs from it | What it gives it |
|---|---|---|
| **H5** (complex hazards in initiative) | The per-entry actor registration and the `ToActorBlueprint` overload, so a complex hazard can be made an actor at start. A link from the hazard's actor back to its placement (Open question 5), so the Hazards panel and the initiative row are one hazard | The "Hazards" group in the builder's search (HAZARDS_DESIGN 7), the "Joins the initiative order" tick, the start step rolling the hazard's initiative. The builder's search is a second reader of `GetActorsBySystem`, covered by the same split-by-tag test H5 writes for `QuickAddPanel` |
| **H6** (world builder hazards) | `ProposalFeature.hazard` and homebrew hazards, so a proposal can place a hazard on a place it also makes | The `encounter` proposal kind, which links to that place by ref; `place_hazards` to read what is already placed. Hazards stay on places: the encounter kind never carries one; after H5 it only ticks a placed complex one to join initiative |

Order: this design's B1 to B4 need neither. B5 (AI) works before H6 (it reads placed hazards; it cannot place one). B7 is after H5.

## 8. Phase 2: MCP server (later)

**What it exposes.** The same read tools (6.1) plus the world builder's (WORLD_BUILDER 3), and `propose_changes` / `add_entries`
with the encounter kind. They are plain methods on `WorldBuilderService` and `PreparedEncounterService`, so the MCP server
(`ModelContextProtocol.AspNetCore`, not yet in the solution) wraps them; no logic of its own. A proposal from an outside client lands in a
world builder chat of the campaign, titled with the token's own name and the date, where the GM reviews and applies it in the app as in WORLD_BUILDER 2.
An outside client can **build** (propose) and **view** (list, get, rate) encounters. It cannot save or start one.

**Sign-in and permission.**
- One auth model for the whole MCP endpoint, the one WORLD_BUILDER 7 sets out: a per-user token the GM creates on their settings page,
  scoped to **one campaign**, GM only (checked at every call through `CampaignAccessResolver`, so a GM demoted to player loses it at once),
  revocable, with an expiry, shown once, stored hashed. Off by default for the server; the admin turns it on.
- While the server is open to the internet (`ExposurePolicy`), the endpoint is refused unless the admin allows it there separately.
- Each call is logged (token name, tool, time) where the GM can see it, beside the token.

**Never exposed.** Table commands of any kind (start, initiative, damage, hazard actions, reveals); applying a proposal or any direct
write; visibility changes; players' journals and private notes; other GMs' chats; other campaigns; AI provider keys and settings; the
vault; accounts and the admin log; the raw database.

**Why it waits.**
1. The world builder's own MCP phase is not built, and there should be one endpoint and one token model, not one per feature.
2. Tokens, the settings page and the exposure rule are security work that deserves its own review.
3. The in-app chat proves the tools and the proposal kind first; the MCP layer then adds no new behaviour.

## 9. Tests

| Test | File |
|---|---|
| A prepared encounter round-trips; a player's access is refused on read, list, save and delete; party preview and `SystemMissing` refused on write; cascade delete removes them; export and import keep them with ids mapped; the book reader never sees them | `PersistenceTest`, `CampaignExportTests`, `BookSourceReaderTests` |
| `MarkRunAsync` patches only the run fields while another save of the plan is in between | `PersistenceTest` |
| Start: creatures created with the right count, names and visibility (`Unknown`, `Hidden` when ticked); a cast-member line reuses its actor; each rolls with the same expression and tie-break the console would give; `LocationId` and `PreparedEncounterId` set; the log line's names are GM only in the players' view | new `PreparedEncounterStartTests` (runtime with a scripted `IDiceRoller`) |
| Start refused with no table, with a running encounter (both in the service and in the command), for a player | same |
| `TableInitiative` gives the console's old numbers (moved code, same result) | `TableInitiativeTests` |
| `HazardsTable` follows a new encounter's `LocationId` when it was already loaded; the picker still overrides | `HazardsTableTests` (bUnit) |
| Readout: loader checks (unknown shape, missing field, rows out of order); sum and byLevel worked out on a fixture system with invented numbers; no block, no readout; a line with no value is named | `SystemDefinitionLoaderTests`, new `EncounterDifficultyTests` |
| The srd5e budget data matches its reviewed data file | srd5e content test |
| The builder's creature search never lists a complex hazard among creatures | bUnit, beside H5's `QuickAddPanel` test |
| World builder: an `encounter` entry with a new place, a new homebrew creature and a Library creature parses and saves in order; a hazard in `creatures` is refused with the pointer; an edit of an existing plan is refused when it changed since the review; leaving out its place leaves it out; the tool loop with a scripted model reads `place_hazards` then proposes | `WorldBundleParserTests`, `WorldBuilderTests` |
| Strings: key parity en/ru/he; every `L["..."]` exists | `LocalizationTests` |

## 10. Build order

One writer at a time across worktrees (the Serena canary failed, PF2E_HAZARDS_IMPLEMENTATION_PLAN 2). B1 and B2 touch no hazard code
and can start at once; B3 waits for the hazards PR (`SetEncounterPlaceCommand`, `HazardsTable`) to be merged.

| # | Step | Files (main ones) | Who |
|---|---|---|---|
| B0 | Review this design; settle the open questions | this doc | Opus with the user |
| B1 | `PreparedEncounterDocument`, repository, index, cascade, export; `EncounterDocument.PreparedEncounterId` | `Codex.Persistence` | Sonnet; Opus reviews the access checks |
| B2 | `PreparedEncounterService` (list, view, check, save); the Encounters section, list and editor (no readout, no start) | `Codex.Web/Services`, `Components/Campaign` | Sonnet |
| B3 | Starting: `TableInitiative` helper moved out of the console, `AddToCastAsync` visibility, `StartPreparedEncounterCommand`, `StartAsync`, the table's menu and notes panel, `HazardsTable` following `LocationId` | `RuntimeCommands.cs` (or a new `PreparedEncounterCommands.cs`), `CastService.cs`, `CombatConsole.razor`, `HazardsTable.razor` | **Opus designs the command and the helper's signature**, Sonnet builds, Opus reviews |
| B4 | Difficulty: the `encounters:` block, loader checks, schema docs, the readout in the editor and `RateAsync`; tested on a fixture system with invented numbers | `SystemDefinition.cs`, `SystemDefinitionLoader.cs`, schema | Sonnet; Opus reviews the block's shape |
| B4b | srd5e's budget, shape 1, from the SRD 5.2.1 "XP Budget per Character" table (Gameplay Toolbox, "Combat Encounters", printed p. 202; confirmed present 2026-11-15), read from the SRD text, never from memory: a reviewed data file and its test | `content/systems/srd5e`, a data file | Sonnet (rules judgment, not Haiku); Opus checks the numbers against the source |
| B5 | AI: the four read tools, `DraftKinds.Encounter` in the parser, check, review panel and save; prompt lines; *Build with AI* starter | `WorldChat.Tools.cs`, `WorldBundle.cs`, `WorldBuilderService*.cs`, `WorldProposalPanel`, `AiPrompts.cs` | Sonnet; Opus reviews the parser and the save order |
| B6 | Strings en/ru/he; accessibility pass (labels, live regions, RTL) | `Strings.Encounters*.resx`, `Strings.Combat*`, `Strings.Event*` | Haiku for the strings only; Sonnet for accessibility |
| B7 | After H5: complex hazards join on start, the "Joins the initiative order" tick, hazard lines, the search's Hazards group | the service, the editor, B3's command | Sonnet; Opus reviews |
| B8 | Later: the MCP server (section 8), together with the world builder's MCP phase | new endpoint, settings page | **Opus designs auth**, Sonnet builds, Opus reviews; security review before merge |

B1 to B3 give a working feature on any system. B4 can come before or after B5.

## 11. Not in this design

- Random or wandering encounter tables (roll on a table to pick a plan). The campaign's tables could hold plan names later.
- Encounter rewards and treasure: the place feature's `contents` and the Items section already carry them.
- A map or token positions.
- Adding a plan's creatures to a fight already running (Open question 2 recommends it as a follow-up).
- The AI starting, revealing or running anything at the table.
- Automatic re-arming of hazards between runs (HAZARDS_DESIGN 10.3).
- Sharing a world builder chat between co-GMs (WORLD_BUILDER 8); prepared encounters themselves are shared.

## 12. Decisions (2026-11-15)

The user agreed with every recommendation below. Two are settled by what H5 built:

- Question 5: H5 links a complex hazard's actor to its placement with `ActorDocument.Hazard` (on `feat/hazards-h5`); this design reads that link.
- Question 10: H5 lets a hazard taken from the Library with no place keep its state in the encounter only. In a prepared encounter, adding a complex hazard still places it on the encounter's place, so a plan never holds an unplaced hazard line.


1. **Status values.** Draft, Ready, Ran, or only Ready and Ran? **Recommended:** all three. The AI saves Draft; the table's menu lists
   Ready only, so a half-made plan never shows up mid-session. A plan that ran can be run again ("Ran 2 times").
2. **Starting while an encounter runs.** Refuse, or add the plan's creatures to it? **Recommended:** refuse in the first version with a
   plain message; add "Add to the current fight" as a follow-up that adds and rolls but keeps the running place.
3. **Player characters on start.** Roll for them too, or leave them to the existing panel? **Recommended:** leave them, so players keep
   rolling their own; a "Roll for the party too" check box later if GMs ask.
4. **Visibility on start.** **Recommended:** `Unknown` by default (as Quick Add), `Hidden` per line for ambushers; the GM reveals from
   the row as today.
5. **How a complex hazard's actor links to its placement (H5).** **Recommended:** H5 stores the placement key
   (`{regionId}/{locationId}`) on the actor's metadata, so the panel shows its turn and the actor's damage reaches the placement's live
   state; this design only reads it. Decide in H5's design, not here.
6. **Creatures made at start, or at prep time?** **Recommended:** at start. Making them early fills the cast with creatures that may
   never fight; cast-member lines cover the named ones.
7. **Export.** **Recommended:** include prepared encounters in the campaign export (GM prep, like the Bestiary), never in the book.
8. **One chat or a separate encounter chat.** **Recommended:** the same world builder chat with a new proposal kind, so one proposal can
   make the place, its hazard, a creature and the fight together.
9. **Difficulty for systems with no rule.** **Recommended:** no readout at all, and no "unknown" badge; the section reads the same without it.
10. **Where a complex hazard added to a plan keeps its state.** **Recommended:** adding one from the Hazards group places it on the
    encounter's place, so its state has the one home H4b gives it; no unplaced hazard lines. Settle with H5's design.

## 13. B1 as built (2026-11-23)

Storage only: no service, no UI, no start.

**Files** (`Codex.Persistence`): `PreparedEncounterModels.cs` (document, line, the two enums), `IPreparedEncounterRepository.cs`,
`RavenPreparedEncounterRepository.cs`, `PreparedEncountersByCampaignIndex` in `CampaignScopedIndexes.cs`. `EncounterDocument.PreparedEncounterId`
is added. Tests: `PreparedEncounterRepositoryTests.cs` (26).

**Wired in**
- `RavenDbService`: the index is created with the others.
- `Program.cs`: `IPreparedEncounterRepository` as a singleton.
- `CampaignRepository`: optional `preparedEncounterRepository`; the cascade delete calls it beside the encounters. Optional, like the newer
  repositories, so no test or caller had to change.
- `CampaignExportService`: optional `preparedEncounters`; `CampaignBundle.PreparedEncounters` (GM only, as the whole export is). Import
  restores them under their stored ids. Import as a copy adds their ids to the ids it renames, so the plan ids, and the region and actor ids
  inside the lines, follow. A place keeps its id in the copy (as `EncounterDocument.LocationId` does), so `PlaceId` still points at it.
  `FromChatId` is cleared in a copy (the chat is not in the bundle). **Copied plans have bare-GUID ids, not the `preparedencounters/` prefix**,
  so no route or check may rely on the prefix.
- JSON backups use the same bundle, so they carry the plans with no extra code.
- Not wired, on purpose: the book (`BookSourceReader` never reads the repository), `IEncounterRepository` and the table's restore (a plan
  is in no session's `EncounterIds`), account deletion (a plan belongs to the campaign, which the hand-over keeps), place and cast-member
  deletion (B2's page reports it), `DocumentVersions` (see below).

**Access, at the repository**
- Reads (`GetAsync`, `ListAsync`) need a GM access whose role the campaign still stores as GM, read again on each call, so a demoted co-GM
  reads nothing more. A missing system still reads. A party preview is a player's access, so it reads nothing.
- Writes (`SaveAsync`, `DeleteAsync`, `MarkRunAsync`) need the same, and also refuse `SystemMissing`. `MarkRunAsync` takes the access like the
  others, so a start path cannot mark another campaign's plan.
- Every path checks the plan's `CampaignId` against the access's campaign, so a GM of campaign A cannot read, save over, delete or mark B's
  plan by id.
- A plan is looked up in its own collection only (a query on the id), so another kind of document's id (a session's, a world chat's) is never
  deserialized as a plan: Get, Delete, MarkRun and Save answer null or false for it.
- A co-GM reads, edits and deletes like the GM.
- `GetAllForCampaignAsync` and `RestoreAsync` take no access: the export service has already required a GM. `RestoreAsync` writes over a
  stored plan of the same campaign, or at an id nothing is stored at that a plan can have (the prefix, or a bare GUID). It writes nothing for
  an id stored as anything else, or for a per-campaign id such as `ConsistencyDismissals/{id}` or `CampaignObjects/{id}` (a plan there would
  break that campaign's later saves). The import has no pre-check stage, so a crafted plan is skipped silently while the rest of the file is
  imported; nothing is written for it and nothing throws.

**Decisions**
- Counts are not clamped or normalised in the repository. It stores what it is given; B2's `CheckAsync` checks 1 to 20 and 1.
- One rule for `Ran`: only `MarkRunAsync` sets it. `SaveAsync` keeps the stored run list and creator whatever the copy carries. A stored plan
  with runs stays `Ran` whatever the copy says; one with none is never `Ran` (a copy saying `Ran` is saved as `Ready`). It is based on the
  stored runs only, so the editor need not carry `RunEncounterIds`. The plan's other fields are last-writer-wins, as the design says;
  `SaveAsync` returns a bool, not a `SaveOutcome`, so it does not tell the editor that the plan changed under it.
- `MarkRunAsync` loads, adds the run id once, sets `Ran` and saves under optimistic concurrency, retrying up to eight times (the
  `RavenUpdate.RunAsync` pattern, written out because it also looks the plan up in its own collection).
- The repository gives a new plan its id (`preparedencounters/<guid>`), always takes `CreatedBy` from the access's user, and stamps
  `UpdatedAt` on every `SaveAsync` and `MarkRunAsync`. `RestoreAsync` keeps the file's `UpdatedAt`.

**For B2:** a plan whose `Id` is set but not stored is refused by `SaveAsync` (create with an empty id); use `DocumentVersions` and a
`SaveOutcome` if the editor must warn about a plan changed in another tab; list is by `UpdatedAt` newest first, with no paging.

## 14. B2 as built (2026-11-23)

The service, the Encounters section, the list and the editor. No start, no readout, no AI.

**Files**
- `Codex.Web/Services/PreparedEncounterService.cs` (the service and its result records), registered as a singleton in `Program.cs`.
- `Codex.Web/Components/Campaign/EncountersSection.razor` (the GM gate and the switch between list and editor), `EncounterList.razor`, `EncounterEditor.razor`.
- `Codex.Persistence/PlaceTree.cs` gains `Chain(region, place)`, the one walk from a place up through the places it is inside. `HazardsTable` and `HazardOffers`
  call it and their private copies are gone; behaviour is unchanged (their tests pass untouched).
- `Strings.Encounters{,.ru,.he}.resx` (the area is found by file name; no registration), and `Page.Campaign.Section.Encounters` in `Strings.Page*.resx`.
- Tests: `PreparedEncounterServiceTests` (23), `PreparedEncounterServiceRavenTests` (7), `EncounterCreatureSearchTests` (2), `PlaceChainTests` (2),
  `EncountersSectionTests` (component, 25 with its theory rows), helpers in `EncounterTestKit.cs` (an in-memory plans repository and a small campaign).

**Wired in** (`CampaignPage.razor`): a Groups entry after bestiary (`encounters`, icon `swords`), `IsDmOnly` includes it (the rail and the phone More menu both filter by it,
and `IssuesSectionTests` still pins that), a `SectionLabel` case, and a section case passing `Arg1`. Nothing else enumerates sections: the phone tab bar is a fixed list,
and `WorldChatView`'s section links name the sections a chat result points at (an encounter kind comes with B5).

**Route.** `/campaigns/{id}/encounters/new` for a new plan and `/campaigns/{id}/encounters/{guid}` for a stored one, both through the router's existing
`{Section}/{Arg1}` page, so no `?new=1` and no new `@page`. A plan id can never be `new`. The URL carries the part of the id after `preparedencounters/`
(`PreparedEncounterService.RouteOf`). Looking a plan up from a URL tries `preparedencounters/{arg}` and then `{arg}`: a copied campaign's plans have bare GUIDs. This is
safe because the repository looks an id up in the plans' collection only; a test passes the campaign's own id and gets nothing.

**Service.** `ListAsync`, `ViewAsync`, `CheckAsync`, `SaveAsync`, `DeleteAsync`, and the editor's helpers: `PlacesAsync` (the picker's tree), `HazardsAtAsync`,
`SearchCreaturesAsync`, `CastChoicesAsync`, and `LineFrom`/`KeyOf` (the Bestiary key shape). Every method answers nothing to a player, and the repository refuses them besides.
- Issues are structured (`EncounterIssue(Key, Args)`), so the page words them in the reader's language and a test names them. Errors: no name, a creature count outside 1 to 20,
  a cast-member count other than 1, a cast member who is a player-character actor (a companion or familiar is one too; an NPC is fine), any hazard line (the text says hazards are
  added on the place). Warnings: an entry gone from the Library, a cast member gone, a place gone.
- A line whose entry, actor or place is gone is kept with its stored name and a warning, never dropped.
- `SaveAsync` trims, clears the region with no place, checks, refuses on errors, then calls the repository, which writes the new id back on the document;
  `EncounterSave.Id` says it so the editor can open the plan. A refusal from the repository is reported as "not saved", not hidden. There is no "edited in another tab" warning,
  because `SaveAsync` answers a bool. Only Draft and Ready are offered; a Ran plan's status is read-only text, and the repository keeps `Ran` as B1 decided.
- The list shows how many times a plan ran (`EncounterRow.RunCount`, the length of `RunEncounterIds`): "Ran once", "Ran 3 times", or a plain "Ran" for a `Ran` plan with no recorded run.
  There is no run date: the plan's `UpdatedAt` moves with every edit, and the live encounter carries no start time (B3 may add one).
- **The check keeps hazards out of lines.** A creature line is an error unless its type is one of the system's creature types (`ActorBlueprint`, no `Hazard` block) and its entry is not
  tagged `hazard` or held by the registry as a hazard actor; a missing entry is only a warning. A cast line naming an actor with a hazard link is an error too (both use `Encounters.Check.HazardLine`).
  Any actor that is not a player character (a companion or familiar is one) and not a hazard may be a cast line: NPCs, and the monster actors that casting a Library creature makes.
  The cast tab lists them, NPCs first.
- **One read per call.** `CheckAsync` and `ViewAsync` read the regions, the cast and the homebrew once (`CampaignLibrary.LookupAsync`), not once per line. Names are looked up in the
  reader's language (`culture` on `ViewAsync`, `HazardsAtAsync` and `CampaignLibrary.GetAsync(..., isHomebrew, culture)`).
- B1's restore now takes a prefixed id only when the suffix is a GUID (`preparedencounters/new` is refused).
- The one-line summary is "Bandit ×3, Wendel Tarr" (the entry's name and a count), not a pluralised name: a name cannot be pluralised in three languages.

**Creature search.** It reads the system's `actorBlueprint` types through `CampaignLibrary.SearchAsync`, as the Bestiary does, and drops anything that is a complex hazard:
an entry tagged `hazard`, or one the registry holds as a `hazard`-tagged actor of that id and entry type. A hazard type is not an `actorBlueprint` type, so this is a second line of defence
against a hazard filed under a creature type; `EncounterCreatureSearchTests` pins it (and breaking the filter fails it). The cast tab is described above.

**Editor.** Name, place (a select; regions as groups when there is more than one, the tree indented), status, notes (`MarkdownEditor`), the lineup as a list (count labelled "How many Bandit",
a note, "Starts hidden", "Remove Bandit"), the add panel (Library tab, Cast tab, *Add from the Bestiary shortlist*, a polite live region for the result count, a second one for
what was added or removed), the hazards at the place (read live, state in words, link to the place), the check's errors and warnings as text, Save and Delete (asks first, says it cannot be undone).
A creature added twice raises its count; the shortlist adds each entry once. The page edits a copy and guards unsaved changes. There is no *Start at the table*, no difficulty readout and no *Build with AI*.
Layout uses the app's logical classes. The one new rule, `.line-check` in `app.css`, lays out "Starts hidden" with flex and `gap` instead of
Bootstrap's `.form-check`, which pads and floats on the left and breaks in right-to-left.

**Left for later**
- B3: start, `MarkRunAsync` caller, the table's menu and notes; a real run date.
- B4: the readout in the editor and the list. B5: *Build with AI* and the proposal kind. B7: hazard lines and the "joins initiative" tick (the check refuses hazard lines until then).
- Deleting a place or a cast member leaves the plan with a warning, as designed; nothing reports it outside the plan's own page and the list's "place was deleted".
- Russian and Hebrew need native review, as all the others do. Hebrew "encounter" is «מפגש קרב», as in the Combat strings (the start button there says «עימות»; worth settling).
- The list has no paging, as the repository has none.

## 15. B3 as built (2026-11-23)

B3 starts a prepared encounter at the table: from the console's menu, or from the plan's editor. It does not cover complex hazards joining initiative (B7) or the difficulty
readout (B4).

**Files**
- `Codex.Persistence/Runtime/TableInitiative.cs` (new): the die, the bonus, the tie-break and the expression, moved out of `CombatConsole` so the console and the start work a roll out
  in one place. Pure; the numbers are the console's old ones (`TableInitiativeTests` keeps the old bodies as an oracle and breaking the helper fails it).
- `Codex.Persistence/Runtime/PreparedEncounterCommands.cs` (new): `StartPreparedEncounterCommand` and `PreparedStartRefusal`.
- `IRuntimeCommand.cs`: the internal `IFollowedByEvents`. `CampaignRuntime.cs`: `AppendSessionEventAsync` writes those events after the command's own, in the same session write;
  `PublishSnapshot` carries the plan. `RuntimeSnapshot.cs`: `EncounterSnapshot.PreparedEncounterId` (optional, trailing).
- `PreparedEncounterModels.cs`, `RavenPreparedEncounterRepository.cs`, `IPreparedEncounterRepository.cs`: `LastRunAt`, stamped by `MarkRunAsync` and by nothing else.
- `Codex.Web/Services/CastService.cs`: `AddToCastAsync(..., visibility, numberFrom)`, both optional and trailing.
- `Codex.Web/Services/PreparedEncounterService.cs`: `StartAsync`, `StartChoicesAsync`, `GetAsync`, `CombatantsOf`, `EncounterStart`, `EncounterStartProblem`.
- Components: `PreparedEncounterPicker` and `EncounterNotesPanel` (new, `Components/Shared`); `CombatConsole`, `HazardsTable`, `EncounterEditor`, `EncounterList` changed.
- Strings: `Combat.Prepared.*`, `Event.Encounter.*`, `Encounters.Start*` and `Encounters.RanOn`, in en, ru and he.

**The wiring**
- The console's *Start a prepared encounter* opens the picker beside *Start encounter* (one of the two is open at a time). It lists Ready and Ran plans, those at the place last used
  first (the live encounter's place, else the place of the plan started most recently), then by name. A click asks first ("N creatures join the fight and roll initiative").
- `StartAsync` runs on the page's circuit; the command runs on the loop. The service refuses early and cheaply (not a GM, no table, no plan, an invalid plan, an encounter
  running), then takes one gate per campaign (a second start is `Busy` at once), makes the creatures with `CastService` (Unknown, or Hidden for a line that starts hidden; cast-member
  lines reuse the actor and keep its visibility), and enqueues the command with the actor ids. The command sets the encounter's place and plan and rolls each actor through
  `RollInitiativeCommand`, so the roll log, the counter refill and every initiative rule are what a roll at the console does. Then `MarkRunAsync` records the run.
- Player characters are not rolled: the order opens with the existing add panel, which lists who is not in it (decision 12.3).
- The console shows the plan's notes (`EncounterNotesPanel`, folded, first in the right column) from the snapshot's plan id, GM only. The Hazards panel follows the encounter's place:
  a new encounter with a place, or a plan started in the empty one, moves it; one with no place keeps what the GM picked.
- The editor's *Start at the table* shows while a table runs (it listens to `RuntimeChanged`), is off while the form has unsaved changes (the start reads the stored plan), and says
  why beside the button. With no table it links to the table. Any status may be started from there, a Draft too.

**Decisions, and where the code differed from the design**
- *One event per creature.* The design said the start applies one `RollInitiativeCommand` per creature, so the log is today's. The loop calls `Describe` once per queued command,
  so an inner command's own line would never be written, and one bundled event would carry every creature's id and be dropped for a player who cannot see any one of them (one
  Hidden ambusher would hide everyone's roll). `IFollowedByEvents` lets the command add each inner roll's own "Initiative" event after its own, with that creature's actor ids.
- *No ordering race.* `TryCreateActorAsync` awaits the hydrate, so creatures are on the table before the command. The real gap is that it swallows a closed table or a full
  queue, leaving a saved actor that is not on the table. The command skips an actor the table does not hold, and the service names it as not joined.
- *`RegionId` is not on the command.* The encounter stores a place id only, as `SetEncounterPlaceCommand` does.
- *Ready and Ran in the menu.* Nothing sets `Ran` back, and a plan that ran can be run again (decision 12.1), so a Ready-only menu would hide every plan after its first run.
  Drafts are left out of the menu; `StartAsync` itself does not filter by status.
- *The panel follows a plan, not only an encounter id.* A live encounter with no participants is reused, so its id does not change when a plan starts in it. The snapshot carries
  `PreparedEncounterId`, and the Hazards panel follows `Encounter.Id + "|" + PreparedEncounterId`, checked before its early return.
- *`StartAsync` takes no runtime.* It looks the table up twice (before it makes anything, and just before it enqueues), since the table can end during any await.
- *Numbers give nothing away.* A visible copy's number is its public name, so the numbering is per creature and counts the visible copies first, and only when there is more than one
  of them: a lone visible Bandit next to a hidden Bandit line stays "Bandit", and two visible ones are "Bandit 1" and "Bandit 2" whatever order the lines are in. The hidden
  copies take the numbers after (their names are the GM's alone).
- *Failure handling.* The loop reports any failure after a command (a persist, the session write) as a failed enqueue, though the table has changed. So once the command has started
  the encounter (`EncounterId` is set) the start is treated as done: the creatures stay, the plan is marked, a warning is logged. Only when nothing started are this start's creatures
  removed, and then a closed table (`InvalidOperationException`) is `TableClosed` and anything else is `Failed`. A plan that cannot be marked (a database fault) still starts, and the page
  says the run was not recorded. `AddToCastAsync` returns the copies it made even when one fails midway (a copy that was saved before the failure counts), so cleanup finds them.
  A command whose follow-up lines cannot be worked out loses only those lines: its own line is written once with the rest.
- *Rolls.* The sheet and linked sheets are built inside the helper's guard, as the console always did, so a sheet that throws gives no bonus instead of ending the circuit. An actor
  whose numbers or roll fail is skipped and named; if nobody rolled, the start is refused (`NobodyAtTable`) and no plan, place or encounter is set.
- *Focus and notes.* When the order opens, focus moves to the order's heading (the picker it was in is gone). The notes panel's heading stands outside its fold, and the plan is read
  again each time the fold is opened or closed. The picker and the editor draw their busy state at once (the dialog's answer arrives outside the click).
- *Creatures of a start that fails are removed.* After a start that is refused or whose table closed, the actors this start made are deleted; reused cast members are never touched.
  After a start that worked they stay on the cast, as Quick Add's do.
- *A run date.* `LastRunAt` is the minimum (one field, one writer): the list says "Ran on 20 Nov 2026" or "Ran 3 times, last on 20 Nov 2026", and a plan run before B3 keeps "Ran once".
- *Systems.* Four-degrees declares no initiative bonus and fitd-srd has no `dice:` block, so both roll the bare `1d20` with no bonus and tie-break 0, as their consoles do. Perception-based
  initiative for four-degrees would be data (`initiativeBonus: <a derived field>`), not B3. fitd-srd has no creature type, so its plans hold only cast members.
- *A small fix on the way.* The expression is built with the invariant culture. Under ICU the Hebrew culture's minus sign carries a direction mark, so a negative bonus used to give
  `1d20‎-1` there. En and ru are byte for byte what they were.
- *Strings.* Russian keeps «столкновение» and uses «мастер» only for the GM; Hebrew follows the Encounters strings («מפגש קרב»; the Combat strings' start button still says «עימות», so the
  word is worth settling). The ru and he texts are drafts for native review, like the others.

**Known limits**
- Duplicate numbers across runs: running "Bandits" twice makes a second "Bandit 1" on the cast, as the Bestiary's *Add to cast* does today.
- After a start that is refused on the loop, or whose table closes, players have already seen each Unknown creature's "joined" line, and the clean-up then writes "left". They read
  "a bandit joined... left". Recorded, not redesigned.
- A creature whose roll fails (a die expression the system wrote badly) is named as not joined and stays on the cast.
- `PreparedStartUiTests` runs the picker and the editor's button against a real table (embedded database), so the messages, the busy buttons and the button's follow-the-table
  behaviour are covered at component level.

**Left for later**
- B4: the readout in the editor and the list, and in the picker. B5: *Build with AI* and the proposal kind. B6: the in-app tools. B7: hazard lines and the "joins initiative" tick, which
  will add a region id to the command if the hazards need one.

## 16. B4 as built (2026-11-24)

B4 builds the difficulty readout: the system block and its loader checks, the pure rating, the service's `RateAsync`, and the editor, list and picker. It adds **no numbers to any bundled
system**: srd5e's budget is B4b (from the SRD text), the degrees system's comes later, so on main no bundled system shows a readout (decision 12.9: no block, no readout, no "unknown" badge).
Everything is tested on two fixture systems with invented numbers.

**Files**
- `Codex.Plugin.Abstractions/EncounterRules.cs` (new): `EncounterRules`, `DifficultyRules`, `DifficultyBand`, `DifficultyValue`, and the translation shapes `EncounterTranslation` and
  `DifficultyTranslation`. `SystemDefinition.Encounters` (null: none); `SystemTranslation.Encounters`; `SystemDefinition.Localize` words the band labels and the unit in a new copy and shares the rest.
- `Codex.Core/Systems/EncounterChecks.cs` (new), called from `SystemDefinitionLoader.Validate`: every check of the brief, each a sentence naming the path (`encounters.difficulty.budget[1] ...`).
- `Codex.Core/Rules/EncounterDifficulty.cs` (new): `EncounterDifficulty.Rate(rules, partyLevels, lines)` -> `DifficultyRating`; pure, no language (reasons are a key and arguments).
- `Codex.Web/Services/PreparedEncounterService.cs`: `RateAsync`, `PartyOverride`, `PartySource`, `EncounterRating`; `EncounterRow.Rating` (optional, trailing); `ListAsync` and
  `StartChoicesAsync` take an optional culture. `Codex.Web/Services/DifficultyText.cs` (new): the words, shared by the list, the picker and the editor.
- `EncounterEditor`, `EncounterList`, `PreparedEncounterPicker` changed. Strings `Encounters.Difficulty.*` in `Strings.Encounters{,.ru,.he}.resx` (the picker's too, so no new resx).
- Generated, not hand-edited: `docs/schema/system.schema.json` and `docs/SYSTEM_YAML_REFERENCE.md` (`dotnet run --project tools/Codex.SystemCheck -- --schema docs`).
- Tests: `SystemDefinitionLoaderTests` (new, the loader's checks), `EncounterDifficultyTests` (pure), `PreparedEncounterRatingTests` (service), `Components/EncounterDifficultyUiTests`;
  fixtures `Fixtures/difficulty-sum` and `Fixtures/difficulty-bylevel`; `EncounterWorld` takes an optional system.

**The wiring**
- `budget` is one YAML key with two row shapes, so the model reads it as a list of string maps (as `hazards.scaling` does) and the loader and the rating parse it per shape.
  A field of the other shape (`values` or `partySize` under `sum`; `creatureValue` or `unit` under `byLevel`) is an error, so a half-edited block is caught.
- The party is the campaign's player-character actors that are not retired and for which `SheetLayout.IsPartyMember` is true (a companion or familiar does not count), each at the level
  the block's `partyLevel` (default `fieldRoles.level`) names on the sheet. A character with no whole-number level makes the readout unavailable and is named; "Rate for a different party" still works.
- A creature line reads its Library or homebrew entry's field times its count. A cast-member line reads the same key on the actor's sheet (a creature made from a stat block has its
  properties there), else the Library actor it was made from (`BlueprintId`, pack entries only), else it has no value. A line with no value, a delta the table lacks, or a level that is
  not a whole number is named with its reason and not counted; the band is given from the rest and the readout says "N lines not counted". A hazard line is listed apart (none can exist
  until B7).
- The editor rates its unsaved copy on every change (add, remove, count, shortlist, the party form), so it follows the lineup without a save. The summary line is also in one polite live
  region that waits 600 ms after the last change, so adding three creatures in a row is one sentence. The readout is a section at the top of the right column (beside the lineup it follows).
- The list and the picker show the band word ("Moderate", "Above High", "Below Easy") as a pill beside the plan, with the section's name as hidden text, for the campaign's party. The list
  is rated in one pass: the cast and the Library are read once, not once per plan. Nothing is shown when the system has no block, the campaign has no characters, a level is unknown,
  or nothing is counted yet.

**Decisions, and where the code differed from the brief**
- *`RateAsync` takes the `CampaignDocument`* (not an id), like every other method, and the plan to rate (the saved one or the page's copy), a `PartyOverride?` and a culture.
- *Band words are worded in the reader's language* by rating with `SystemDefinition.Localize(culture)`; the rating keeps the keys.
- *Levels between budget rows.* A character level with no row of its own but inside the rows' range takes the row just below it; only a level below the first or above the last row makes the
  readout unavailable.
- *Total 0 says "Nothing counted yet"* whether there are no lines or all are worth 0; any total above 0 under `atMost` is at least the first band (pinned in the tests, both).
- *`unit` is `sum` only*, as the brief lists it; under `byLevel` it is a loader error.
- *Review changes (2026-11-24).* Under `byLevel` the budget is on each band (`value`, `perCharacter`, both `int?` and required there, forbidden under `sum`); there is no `budget` list
  there, and `budget` is `sum` only, so a band's budget can no longer sit out of band order. The loader checks that the bands never cross for a party of 1 and of 20 (a budget is linear in
  the size before its floor at zero, so the two ends are enough), and `ByLevelBudgets` keeps a running maximum as a second guard. `level` is reserved as a band key under `sum`.
  `DifficultyValue.Delta` and `Value` are `int?` and required. An empty or blank `partyLevel` falls back to `fieldRoles.level`, in the loader and the service. A negative value on an entry or
  sheet is "no value" with a reason, not summed. `SystemDefinition.Localize` copies the whole `EncounterRules` and replaces only `Difficulty`. A fault while rating the list is logged and the
  list comes without ratings. Hebrew "below" no longer glues the band label to a prefix.
- *The "different party" level range.* `sum`: the first and last budget row. `byLevel` has no level rows, so the level field's own `min` and `max` (1 to 20 when it has none). The form
  opens with the party the readout is for now; with no characters it opens by itself, filled with 4 (or `partySize`) characters of the lowest level, and says "rating for ...".
- *Mixed levels in `byLevel`:* the party level is the level most characters have, the lower on a tie, and the readout says the levels vary. This and the "Rate for a different party" form
  are our own decisions, not from any rule text.
- *No numbers for no characters in the list.* With no party characters the list and the picker show no band (a stand-in party's band would claim something about nobody); the editor shows it, and says so.
- *The old `SystemDefinitionLoaderTests`* did not exist (the loader's tests are `SystemDefinitionTests`); the brief's name is a new class that holds the block's checks.
- *Strings.* Russian «столкновение» and «мастер» only for the GM, Hebrew «מפגש קרב»; the ru and he texts are drafts for native review.

**Known limits**
- Shape `sum` is checked against the SRD rule text in B4b and may gain a field there (for example a multiplier for the number of creatures, if the text has one). `byLevel` is provisional
  until a system declares it. The first real numbers are B4b's.
- A player character's level is read from the sheet's field only; a level a sheet formula works out is whatever the stored sheet holds.
- The editor reads the cast and the Library again on every change (a few small reads); the list reads them once. Caching them for the page's lifetime is left for later: it needs a reload when
  the cast changes, and the reads are small.

**Left for later**
- B4b: srd5e's `sum` budget from the SRD 5.2.1 text, with a test that compares the data to it. The degrees system's `byLevel` block when its pack has the numbers. Hazard values (`hazardValue`)
  with B7.
