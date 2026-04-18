# UI Specification

The Codex uses a **Fluid Dark Mode** aesthetic, inspired by Microsoft's Fluent 2.0 and modern "glassmorphism" trends.

## Core Design Principles
1. **Vertical Alignment**: All labels and inputs must align to a strict baseline.
2. **Contextual Intelligence**: Screens should look different based on the active Role (DM vs Player).
3. **Immersive feedback**: Use animations (fade-ins, sparkles) to indicate AI interaction or data synchronization.

## Screen Breakdown

### 1. Dashboard (`/`)
- **DM**: View active campaigns, quick-start a session, and see recent DM notes.
- **Player**: View their characters, the very last session recap for each campaign, and unread DM messages.

### 2. Campaign Detail (`/campaigns/{id}`)
- **Header**: Large art preview, current session status (Online/Offline).
- **Tabs**:
  - **Timeline**: Vertical chronological list of world events.
  - **Codex (NPCs/Locations)**: Toggled view between list and grid.
  - **Sessions**: History of play dates with expand/collapse recaps.

### 3. Character Page (`/characters/{id}`)
- **Left Panel**: Stats and "Vitals".
- **Center Panel**: The Note Stack.
  - Interactive "New Note" box that auto-links to the *Current Session* if one is active.
- **Right Panel**: Inventory and Lore Reference.

### 4. Grimoire (`/grimoire`)
- **Search-First Design**: Large omni-search bar.
- **Categories**: Filter chips for Spells, Bestiary, etc.
- **Grid Layout**: Fluid cards with hover effects.

## Consistency Guidelines
- **Buttons**: All buttons must use `var(--radius-md)`. Primary buttons have `filter: brightness(1.1)` on hover.
- **Inputs**: All text boxes must have a 1px solid `var(--fluent-border)` and 10px-14px padding.
- **Alignment**:
  - Headers must be `padding: 4rem 3rem`.
  - Grid gutters must be exactly `1.5rem`.
