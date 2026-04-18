# Core Entity Specification

This document defines the properties and relationships of the primary data entities in the Codex.

## 1. Sessions

Sessions are the heart of the play experience. They serve as the temporal containers for all events.

- **Properties**:
  - `Title`: Name of the session.
  - `Date`: Real-world play date.
  - `CampaignId`: Link to the campaign.
  - `Recap`: A DM-written or AI-generated summary of major events.
  - `Events`: A list of snapshots (e.g., "The party entered the cave", "Valerius died").
  - `Gallery`: (Future) Image uploads of maps/physical sketches.

## 2. Campaigns

The overarching container for a game world.

- **Sub-Entities**:
  - **NPCs**: Name, Race, Class, Alignment, Public/Private flag, History.
  - **Locations**: Descriptions, hidden secrets, map coordinates.
  - **Encounters**: Combat tracker data, loot tables.
  - **Timeline (Dual Layer)**:
    - **Master World Events (DM Only)**: A separate, comprehensive list of events independent of sessions (Cataclysms, Wars). Sessions are also injected into this view as chronological markers.
    - **Session-Only List (Players/DM)**: A focused view of chronological play dates, recaps, and shared events.
    - **Campaign Markers**: Key milestones marked with a unique icon to separate plot-critical beats from general history.

## 3. Characters

Owned by players, managed in the context of a campaign.

- **Features**:
  - **Stats**: Class, Level, HP, etc.
  - **Campaign Lore**: A list of lore entries this character "knows".
  - **Note Stack**: Append-only markdown notes.
    - Each note is tagged with a `SessionId` for chronological ordering.
    - Notes can be `Shared` or `Private`.

## 4. Lore (The Grimoire)

A global and campaign-specific knowledge base.

- **Categories**:
  - Spells / Artifacts / Bestiary / History.
- **AI Integration**: AI can "Generate Lore" based on session events to help the DM fill in blanks (e.g., "Generate a description for the Obsidian Scepter based on last night's session").

## 5. AI

AI is a first-class citizen in the Codex, used to enhance the DM's storytelling and world-building capabilities.

- **Properties**:
  - `Model`: The AI model to use (e.g., "gpt-4", "claude-3", "local-llama").
  - `ApiKey`: The API key for the AI model.
  - `SystemPrompt`: The system prompt to use for the AI model.
  - `UserPrompt`: The user prompt to use for the AI model.
  - `Response`: The response from the AI model.
  - `Timestamp`: The timestamp of the AI response.

- **Features**:
  - **Session Recap**: AI can generate a session recap based on the session events.
  - **Lore Generation**: AI can generate lore based on session events.
  - **NPC Generation**: AI can generate NPCs based on session events.
  - **Location Generation**: AI can generate locations based on session events.
  - **Encounter Generation**: AI can generate encounters based on session events.
