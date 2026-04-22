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
  - `ResourcePoolComponent`: Dictionary of values (e.g., `{"HP": 20, "Mana": 10}`).
  - `AbilityComponent`: List of active/passive `AbilityId`s.
  - `Properties`: A generic `Dictionary<string, object>` used for system-specific attributes (e.g., `Strength`, `Agility`). This ensures homebrew can add new attributes without schema migrations.

## 3. Abilities (The TRCE Format)
Abilities are defined by their logic and resource requirements. This is a **Compositional Model**.

- **Structure**:
  - `Id`: `abilities/{system-id}/{ability-id}`
  - `Triggers`: `List<TypedComponent>` (e.g., `Type: "Action"`, `Type: "OnKill"`)
  - `Requires`: `List<TypedComponent>` (e.g., `Type: "Range"`, `Params: { "Value": 30 }`)
  - `Costs`: `List<TypedComponent>` (e.g., `Type: "Resource"`, `Params: { "Id": "Mana", "Amount": 5 }`)
  - `Effects`: `List<TypedComponent>` (e.g., `Type: "Damage"`, `Params: { "Dice": "2d6" }`)

- **TypedComponent**: A simple structure: `{ string Type, Dictionary<string, object> Params }`.
- **Reasoning**: This format allows the **Authoring App** to render dynamic builders (e.g., a "Cost" picker that pulls from all known resources) while keeping the engine logic isolated in C# plugins.

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
