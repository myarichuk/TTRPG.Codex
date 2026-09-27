# Player vs. DM Modes

TTRPG.Codex supports two primary user roles: **Dungeon Master (DM)** and **Player** (plus **Observer** for watch-only seats). The system uses role-based access to determine what data is displayed and which actions are available.

## Role Selection

- **Joining**: an invite link grants Player (or Observer, if requested) - never DM. After
  joining, the join page lets you switch your own seat between Player and Observer.
- **Promotion**: an existing DM promotes members between DM/Player/Observer from the
  campaign's Members list. Self-promotion to DM is always denied.
- **Leaving/removal**: a DM can remove any non-owner member; anyone can leave. The campaign
  owner is structurally DM and can only exit by deleting the campaign.
- **Screens**: the dashboard splits into "Campaigns you run" (DM view) and "Campaigns you
  play in" (player view with per-campaign character creation), plus a "My Characters"
  table of every character you own across campaigns.

## Visibility Logic

The core concept is the **Public/Private Toggle**. Every entity (NPC, Item, Location, Event) has a visibility flag.

```mermaid
graph TD
    Data[Entity Data] --> DM{User Role?}
    DM -- DM --> Full[Full Visibility + Edit]
    DM -- Player --> Status{Public Flag?}
    Status -- Yes --> Reveal[Render to User]
    Status -- No --> Hide[Hide or Show Placeholder]
    Hide -- Manual --> Reveal
```

## DM Mode (The Architect)
The DM is the world-builder. Their interface includes:
- **Campaign Dashboard**: Full overview of all active modules.
- **Hidden Entities**: NPCs not yet met, secret artifacts, and impending encounters.
- **Timeline Control**: The ability to add "Future" events or "Hidden" historical lore.
*   **Encounter Manager**: Full stat-blocks and tactical overview.

## Player Mode (The Adventurer)
Players experience the world through their characters.
- **Public Grimoire**: Lore discovered by the party.
- **Character Dashboard**: 
  - Manage stats and bio.
  - **Session Notes**: Append-only notepad linked to specific sessions.
  - **Shared Visibility**: Notes toggled to "Public" are viewable by all party members; otherwise, they are private to the creator and DM.
  - **DM Shares**: Specific notes or insights shared by the DM with that player.

## NPC Visibility States
NPCs use a tiered visibility system to maintain immersion:
1. **Hidden**: Players have never encountered the NPC. They are completely omitted from the player's Codex.
2. **Unknown**: Players have seen the NPC but do not know their identity. They appear as "Unknown NPC" with a generic placeholder icon/description.
3. **Known (Revealed)**: The DM has revealed their identity. Full name, race, and public bio are rendered.

## Interaction Table

| Feature | DM Permission | Player Permission |
| :--- | :--- | :--- |
| **NPCs** | Create, Edit, Toggle Public | View (if Public) |
| **Encounters** | Full Access | No Access (until triggered) |
| **Locations** | Full Map/Details | Public Locations only |
| **Timeline** | All Events | History + Public Milestones |
| **Sessions** | Create Recap, Log Events | View Recap, Add Personal Notes |
