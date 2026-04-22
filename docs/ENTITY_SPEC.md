# Core Entity Specification

This document defines the properties and relationships of the primary data entities in the Codex, optimized for **RavenDB** document storage.

## 1. Campaigns (Aggregate Root)
The overarching container for a game world. A campaign is the primary unit of persistence.

- **Properties**:
  - `Id`: `campaigns/{unique-id}`
  - `Name`: Title of the adventure.
  - `SystemId`: The TTRPG system (e.g., `dnd5e`, `swffg`).
  - `ActivePacks`: List of loaded Content Pack IDs.
  - `ParticipantIds`: List of User IDs in this campaign.

## 2. Characters & NPCs (Entity Blueprints)
Characters and NPCs are modeled as **Entities** in the ECS runtime, but persisted as JSON blueprints.

- **Standard Components**:
  - `IdentityComponent`: Name, Race, Class, Background.
  - `ResourcePoolComponent`: Dictionary of values (e.g., `{"HP": 20, "Shield": 5, "Mana": 10}`).
  - `StatModifierComponent`: List of active modifiers for rolls or static values.
  - `InventoryComponent`: List of item references and their states.
  - `AbilityComponent`: List of active/passive ability IDs from Content Packs.

## 3. Regions & Locations (Spatial Graph)
Locations are modeled as a **Spatial Graph** within a `RegionDocument`. We avoid deep nested trees; instead, we use a flat list of nodes and edges for O(1) document loading.

- **RegionDocument**:
  - `Id`: `regions/{region-id}`
  - `Locations`: List of nodes (Name, Description, SceneTemplateId).
  - `Paths`: List of edges (`From`, `To`, `Distance`, `TravelType`).

## 4. Lore & Facts (Knowledge Graph)
Knowledge is tracked via `FactDocument` entries. This allows the DM to track the spread of information across the campaign.

- **FactDocument**:
  - `Id`: `facts/{fact-id}`
  - `Summary`: What the secret or piece of lore is.
  - `KnownBy`: List of `EntityId`s (NPCs or PCs) and their level of knowledge (`Rumor`, `Actor`, `Full`).
  - **Query Pattern**: A Map-Reduce index on `KnownBy.EntityId` allows instant retrieval of everything a specific NPC knows.

## 5. Scenes (The Live Action)
A Scene is the aggregate root for a single play encounter (Combat or Social).

- **SceneDocument**:
  - `LocationId`: Where the scene is happening.
  - `Entities`: Snapshot of all NPCs, Monsters, and PCs present.
  - `Initiative`: Current turn order.
  - `ActiveEffects`: Environmental status effects (e.g., "Magical Darkness").
