# TTRPG.Codex

**Modular tabletop RPG campaign and character management system.**

A lightweight digital companion for tabletop play that replaces paper sheets while preserving the tabletop feel.  
Supports multiple TTRPG systems via plugins and uses an **Entity Component System (ECS)** runtime to model living character state.

---

## Goals

### Primary Goals
- Replace paper character sheets with synchronized digital state
- Provide a real-time DM dashboard for party overview
- Support multiple TTRPG systems (and homebrew) via plugins
- Enable user-defined rules without code changes
- Work seamlessly on phones (players) and tablets/laptops (DM)
- Run fully locally (LAN) or self-hosted in the cloud

### Non-Goals
- Virtual tabletop (maps, tokens, grids)
- Full automation of every single rule
- Official rulebook replacement or content distribution

---

## High-Level Architecture

```
Client Devices (Phones / Tablets / Desktop)
          ↓
   Blazor Server Application
          ↓
     Codex Core Engine
          ↓
   ECS Runtime (DefaultEcs)
          ↓
      System Plugins
          ↓
 Persistence (RavenDB Embedded)
```

All state lives on the server; clients are thin real-time views.

---

## Technology Stack

**Backend**  
- ASP.NET Core + Blazor Server + SignalR

**Runtime Model**  
- DefaultEcs (fast, lightweight .NET ECS)

**Persistence**  
- RavenDB Embedded (document DB, zero-config)

**Deployment**  
- Self-hosted (Windows/Mac/Linux)  
- Local LAN (zero internet)  
- Optional Docker/cloud

---

## Core Concepts

### Entity
A game object (Character, NPC, Creature, Effect, Item, etc.).

### Component
Pure data. No behavior.

**Examples:**
```csharp
NameComponent { string Name; }
HealthComponent { int Current, int Maximum; }
ConditionComponent { List<ActiveCondition> Conditions; }
InventoryComponent { List<ItemEntry> Items; }
PlayerOwnerComponent { string PlayerId; }
```

### System
Processes entities that match a component query. Runs on update cycles or events.

**Examples:**
- `DamageSystem`
- `ConditionExpirationSystem`
- `ResourceConsumptionSystem`

---

## ECS Runtime Model

Entities are just IDs + attached components.

**Example Character:**
```
CharacterEntity
├── NameComponent
├── HealthComponent
├── AbilityScoresComponent
├── ConditionComponent
├── InventoryComponent
└── (any plugin-specific components)
```

**Query example:**
```csharp
var query = world.GetEntities()
    .With<HealthComponent>()
    .With<ConditionComponent>()
    .AsEnumerable();
```

Used by the DM dashboard for live party status.

---

## Persistence Model

RavenDB document-per-campaign.  
ECS state is **derived** on load (hydrate) and periodically flushed back.

```
RavenDB
   ↓
Campaign Document (JSON)
   ↓
Hydrate → ECS World
   ↓
Live gameplay (DamageSystem etc.)
   ↓
Periodic save
```

---

## Campaign Document Example
```json
{
  "campaignId": "abc123",
  "name": "Curse of the Black Tower",
  "system": "DnD5e",
  "characters": [ ... ],
  "sessions": [ ... ],
  "notes": [ ... ],
  "worldState": { ... }  // any extra plugin data
}
```

---

## Plugin Architecture

Everything system-specific lives in plugins.

```
Codex.Core
├── Codex.Systems.DnD5e
├── Codex.Systems.SWFFG
└── (your homebrew plugin)
```

Plugins register components, systems, templates, and UI schemas at startup.

---

## Plugin Interface
```csharp
public interface ICodexSystemPlugin
{
    string SystemId { get; }
    void RegisterComponents(ComponentRegistry registry);
    void RegisterSystems(World world);
    CharacterTemplate GetDefaultCharacterTemplate();
    // Optional: UI schemas, dice parsers, etc.
}
```

---

## Example Plugin: DnD 5e (SRD-only)

**Components**
- `AbilityScoresComponent`
- `HitPointsComponent`
- `SpellSlotsComponent`
- `ConditionComponent`
- `ProficiencyComponent`
- `ConcentrationComponent` (new in v1.1 — see recommendation below)

**Systems**
- `DamageSystem` → `CurrentHP -= Damage`
- `ConcentrationSystem` → triggers on damage events
- `SpellSlotSystem` → consume on cast

---

## Example Plugin: Star Wars FFG (mechanics only)

**Components**
- `CharacteristicsComponent`
- `WoundComponent`
- `StrainComponent`
- `ObligationComponent`
- `DicePoolComponent`

**Systems**
- `WoundSystem`
- `StrainRecoverySystem`
- `DicePoolSystem` (builds pools from characteristics + skills)

---

## UI Design

**Player View (Mobile)**
- Clean sheet: HP bar, conditions, resources, abilities, notes
- Big tap targets, portrait-first

**DM Dashboard (Tablet/Laptop)**
- Party overview (HP bars + color coding)
- Active conditions + concentration flags
- Initiative list (sortable)
- Real-time updates via SignalR

---

## Networking Model

Blazor Server + SignalR = single source of truth.

```
Player taps "Take 8 damage" (phone)
          ↓
Server ECS updated instantly
          ↓
DM dashboard + all other clients refresh
```

Reconnects automatically with full state.

---

## Plugin Loading
Plugins dropped in `/plugins` folder.  
Loaded via reflection at startup:

```csharp
foreach (var plugin in LoadAssemblies("plugins"))
    plugin.Register(...);
```

Zero rebuild needed.

---

## Security
- Campaign owner = DM (full control)
- Roles: DM / Player / Observer
- Players can only edit their own entities
- SignalR authentication via simple tokens

---

## Known Risks & Mitigations

- **Connection drop mid-session** → Add localStorage cache + "Reconnect" button (5 lines of JS interop).
- **Too many entities** → DefaultEcs handles 1000+ easily; add a soft limit warning at 200.
- **Plugin conflicts** → Namespace components (e.g. `Dnd5e.HealthComponent`).
- **Forgetting to save** → Auto-save every 30 s + manual "Backup Campaign" zip button.

## Design Philosophy

TTRPG.Codex models characters as **evolving state machines**, exactly like the table does.  
ECS + plugins = a flexible engine that grows with your campaigns instead of fighting them.
