# Codex: Architectural Walkthrough & Reasoning

This document explains the **why** behind the Codex architecture.
## 1. The Runtime: Entity Component System (ECS)

We use **DefaultEcs** for the live game state. Why?

- **Separation of Concerns**: In a TTRPG, a "Character" isn't a class. It's a collection of data that changes. ECS treats data (Components) as distinct from logic (Systems).
- **Cache Locality**: Systems process arrays of components. This is friendly to the CPU cache. Iterating over 1,000 entities to decrement a duration is an `O(N)` operation that is blazingly fast.
- **Flexibility**: Want to add a "Burning" effect? You don't subclass `Character`. You just slap a `StatusEffectComponent` and a `DurationComponent` on the entity. The systems don't care what the entity "is"; they only care what data it "has."

## 2. Persistence: RavenDB (The Pragmatic Choice)

We don't use SQL. TTRPG data is hierarchical, nested, and sparse. SQL's "Impedance Mismatch" would kill us with joins and migrations.

- **Document Model**: A Character or a Campaign is a JSON document. We load the whole thing in one shot. No joins. No ceremony.
- **Aggregate Roots**: We define consistency boundaries. A `Campaign` is a root. A `Note` is a separate document to prevent the Campaign document from growing to 50MB and causing IO bottlenecks.
- **Map-Reduce for Relationships**: We don't use "Bridge Tables" for things like knowledge ("Who knows what?"). We use RavenDB's Map-Reduce indexes. We compute the "knower-to-fact" mapping at write-time, making lookups `O(1)` at runtime.

## 3. Content Packs: Data-Driven Rules

The engine shouldn't know what a "Fireball" is. If you hardcode your rules in C#, you've already lost.

- **YAML for Declarative Data**: Most TTRPG content is just data. YAML is human-readable and easy for content authors to edit.
- **TRCE Model**: Abilities are defined by four pillars:
  - **Triggers**: *When* it happens (e.g., OnHit, Action).
  - **Requirements**: *Must be true* (e.g., Range < 30ft, Has Level 3 Spell Slot).
  - **Costs**: *What is spent* (e.g., 2 Mana, 1 Action).
  - **Effects**: *What happens* (e.g., Deal 2d6 Fire damage).
- **DynamicExpresso for Logic**: When you need "real" logic, we use a safe scripting sandbox.
- **Schema Patching (The Homebrew Secret)**: The Authoring UI is not hardcoded. Content packs can provide `schema.patch.yaml` to extend the UI. If a homebrew pack adds "Sanity" as a resource, the Authoring App automatically adds "Sanity" to all relevant dropdowns.

## 4. The "No-Nonsense" Rule

Every line of code in the Codex must justify its existence.

- **Logic vs. Data**: C# Plugins provide the *logic* (how to roll dice, how to apply damage types). YAML packs provide the *data* (the name of the spell, the damage value).
- **No Repository-per-Entity**: We have generic persistence patterns where they make sense.
- **Pragmatic Extensibility**: We don't use complex inheritance. We use composition (Components) and data-bags (Dictionaries) to ensure homebrew can always add "one more field" without a recompile.
