# TTRPG.Codex Design Specification

**Modular tabletop RPG campaign and character management system.**

A high-performance digital companion for tabletop play. Built on an **Entity Component System (ECS)** for live state and **RavenDB** for pragmatic, document-oriented persistence.

---

## Architectural Philosophy (The Ayende Way)
- **Pragmatism over Purity**: We don't build complex class hierarchies. We build data-driven systems that are fast and maintainable.
- **Data Locality**: We store related data together in documents to minimize IO and maximize cache efficiency.
- **ECS for Live State**: Characters aren't "objects" with methods; they are collections of components processed by high-performance systems.

---

## High-Level Architecture

```
Client Devices (Phones / Tablets / Desktop)
          ↓
   Blazor Server Application (SignalR)
          ↓
     Codex Core Engine (Business Logic)
          ↓
   ECS Runtime (DefaultEcs - In-memory state)
          ↓
   Content Packs (YAML/Scripts - Data-driven rules)
          ↓
 Persistence (RavenDB Embedded - Document Store)
```

---

## Core Engine: Entity Component System (ECS)

Entities are IDs with attached components. We avoid hardcoded components for specific stats to keep the engine system-agnostic and flexible.

### Generic Components
- **ResourcePoolComponent**: `Dictionary<string, int>`. Tracks dynamic values like "HP", "Mana", "Shield", "Strain".
- **StatModifierComponent**: `List<Modifier>`. Tracks temporary or permanent adjustments (e.g., `+2 AC from Shield spell`).
- **StatusEffectComponent**: Tracks active conditions (e.g., "Poisoned", "Stunned") tied to a `PackId`.
- **DurationComponent**: Tracks remaining rounds for effects.

### Systems
- **DurationSystem**: Ticks every round, decrements durations, and removes expired components.
- **DamageSystem**: Processes `DamageEvent` entities, accounting for modifiers and resource pools (e.g., draining "Shield" before "HP").
- **PeriodicEffectSystem**: Listens for turn events to trigger "ticks" (DOTs/HOTs) defined in scripts.

---

## Content Pack & Ability System

Content (spells, NPCs, items) is defined in **Content Packs** (YAML + Scripts), not C# code.

### Pack Structure
- `manifest.json`: Metadata (Id, SystemId, Priority, Dependencies).
- `abilities/`: YAML definitions for spells and feats.
- `entities/`: Blueprints for NPCs and Monsters.
- `scripts/`: Complex logic evaluated via **DynamicExpresso**.

### Inheritance & Overrides
Abilities support an `inherits` field. The loader performs a deep-merge of the base ability, allowing "Homebrew" packs to easily override "SRD" content by having a higher priority.

---

## Scene Management

A **Scene** is a temporal and spatial boundary. It is the aggregate root for live gameplay.

- **SceneDocument (RavenDB)**: Persistent state of the scene (Location, Participants, Initiative).
- **CodexWorld (ECS)**: When a scene starts, we load only the relevant entities into a fresh ECS world.
- **Contextual Execution**: Scripts have access to the `AbilityContext` containing the `Caster`, `Target`, and the current `World`.

---

## Persistence: RavenDB

We use RavenDB for its superior handling of complex, nested data and its ability to scale without the "impedance mismatch" of relational databases.

- **CharacterDocument**: Fully serialized ECS state.
- **CampaignDocument**: The container for all scenes, lore, and history.
- **Map-Reduce Indexes**: Used for fast lookups (e.g., "What facts does this NPC know?").

---

## Interaction Model

1. **Player/DM Action**: Triggered via UI (e.g., clicking "Use Fireball").
2. **Registry Lookup**: Engine finds the `IAbilityDefinition` in the `AbilityRegistry`.
3. **Script Execution**: `ScriptEvaluator` runs the associated script within the `AbilityContext`.
4. **ECS Update**: Components are modified (e.g., `AddStatus` or `DealDamage`).
5. **Real-time Sync**: Blazor/SignalR pushes the updated state to all connected clients.
