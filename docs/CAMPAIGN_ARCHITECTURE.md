# TTRPG.Codex: Campaign Architecture & Persistence

This document outlines the **Ayende-style** pragmatic architecture for campaign management, using **RavenDB** for persistence and **ECS** for live gameplay state.

## 1. Document Design & Aggregate Roots

We avoid the "Relational Trap." We don't normalize every piece of data. We store data based on how it is **accessed**, not how it is categorized.

### The Campaign Aggregate
The `CampaignDocument` is the boundary of consistency. It contains the core configuration, but it does **not** contain every NPC and Location as nested objects. Instead, it holds the references and high-level state.

### Entity Documents (PCs/NPCs)
Entities are stored as individual documents: `entities/{unique-id}`.
- **Why?** When a player edits their character, we only save that document. We don't save the whole campaign. This reduces IO and prevents write-conflicts.

## 2. Spatial Graph (Locations & Regions)

TTRPG maps are graphs. We model them as `RegionDocument` aggregates.

- **Storage**: All locations and their connections for a region live in one document.
- **Performance**: Loading the map is a single `Get` operation. Calculating a travel route happens in-memory.

## 3. Knowledge Graph (Lore & Facts)

"Who knows what?" is a common TTRPG query. We use **Map-Reduce** to make this O(1) at runtime.

### The Fact Document
```json
{
  "Id": "facts/the-vizier-is-evil",
  "Summary": "The Vizier is plotting to poison the King.",
  "KnownBy": [
    { "Id": "npc:maid", "Level": "Rumor" },
    { "Id": "npc:vizier", "Level": "Actor" }
  ]
}
```

### The Knowledge Index (RavenDB Map-Reduce)
We define an index that groups by `KnownBy.Id`.
```csharp
public class KnowledgeIndex : AbstractIndexCreationTask<FactDocument, KnowledgeEntry>
{
    public KnowledgeIndex()
    {
        Map = facts => from fact in facts
                       from knower in fact.KnownBy
                       select new { KnowerId = knower.Id, FactIds = new[] { fact.Id } };

        Reduce = results => from result in results
                            group result by result.KnowerId into g
                            select new { KnowerId = g.Key, FactIds = g.SelectMany(x => x.FactIds).Distinct() };
    }
}
```
**Result**: Querying `KnowledgeIndex` for `KnowerId == "npc:maid"` instantly returns every fact the maid knows.

## 4. Live Gameplay: The ECS Hand-off

1. **Hydration**: When a `Scene` starts, the engine loads the `SceneDocument` and the relevant `EntityDocument`s.
2. **ECS Promotion**: These documents are parsed into components and loaded into the `CodexWorld` (DefaultEcs).
3. **Flushing**: Every 30 seconds (or on manual save), the ECS state is serialized back into JSON and written to RavenDB.
4. **Pragmatic Persistence**: We only flush if the state has actually changed (Dirty Flag pattern).

## 5. Content Packs as Rule Providers

Content Packs (Abilities, Items, Templates) are **Immutable** at runtime. They are loaded into a global `Registry` and referenced by ID.
- **Dangling References**: If a pack is uninstalled, the `EntityDocument` will still have the `AbilityId`. The UI handles this by showing a "Missing Content" warning instead of crashing.
