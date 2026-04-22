# Plan: Redesign Codex.Authoring App

## Background & Motivation
The current `Codex.Authoring` application features a basic graph editor. However, to fulfill its role as a comprehensive TTRPG content authoring tool, it needs to support convenient, intuitive, and themed (light/dark) creation of Abilities, Actors (NPCs, PCs), and Locations. Since Codex supports multiple systems (e.g., D&D 5e, SWFFG), the UI must dynamically adapt to the specific data requirements of each system without hardcoding. 

Crucially, we've identified that many data types (not just Locations) are best edited via a graph-based interface, including:
- **Story Flows**: Narratives and quest lines.
- **Skill Trees**: Progression paths for characters.
- **Relationship Maps**: Connections between NPCs and factions.
- **World Maps**: Spatial locations and regions.

## Scope & Impact
This redesign affects the `Codex.Authoring` project. It will introduce a new navigation structure, a theme toggler, and a dynamic form and graph generation system based on UI schemas defined by the installed system plugins. It will interact with the abstractions in `Codex.Plugin.Abstractions`.

## Proposed Solution: Schema-Driven Extensible UI
We will build a dual-mode content editor. Each TTRPG system plugin will define a `UISchema` that describes:
1.  **Properties**: Fields for the standard form-based view (e.g., Strength, Dexterity).
2.  **Display Hints**: Whether the entity type prefers a `FormView`, a `GraphView`, or both.
3.  **Graph Rules**: If a `GraphView` is preferred, define the allowed node types, edge types, and connections.

## Implementation Plan

### Phase 1: Core Navigation and Theming [DONE]
1. Update `MainWindow.axaml` to include a global Theme Toggler (Light/Dark mode) leveraging `FluentAvalonia`.
2. Expand the `NavigationView` to include sections for:
   - **Dashboard** (Overview of loaded packs)
   - **Entity Browser** (Hierarchical view: System -> Entity Type -> Entity List)
   - **System Editor** (Specific tools for the selected TTRPG system)
   - **Graph Editor** (Visual data editing)

### Phase 2: Schema Abstractions (Metadata-Rich) [DONE]
1. Introduce a `UISchema` definition into `Codex.Plugin.Abstractions`.
2. Add a `PreferredEditor` enum (Form, Graph) to the schema.
3. Add `GraphMetadata` (NodeShape, EdgeTypes, NodeCategories) to support the Generic Graph Engine.
4. Update `ICodexSystemPlugin` to expose these schemas.

### Phase 3: Dynamic Form Engine [DONE]
1. Create `DynamicFormView` using Avalonia's `ItemsControl` to generate input elements (TextBox, NumericUpDown, ComboBox) from the `UISchema`.
2. Bind to a `GenericEntityViewModel` that proxies the entity's underlying dictionary properties.

### Phase 4: Generic Graph Engine (The "Visual Logic" Mode) [DONE]
1. Refactor the existing `GraphEditorView` into a `SchemaDrivenGraphEditor`.
2. Use `Avalonia.Controls.PanAndZoom` for a high-performance, zoomable canvas.
3. Implement node templates that dynamically adapt their visuals (icons, colors, ports) based on the `UISchema` for that node type.
4. Support diverse graph types (e.g., hierarchical for skill trees, spatial for maps).

### Phase 5: Integration & Persistence [DONE]
1. Implement a `MultiModeContentEditor` that automatically swaps between `DynamicFormView` and `SchemaDrivenGraphEditor` based on the selected entity's `PreferredEditor`.
2. Wire save/load logic to serialize both form data and graph metadata (node positions, connections) into the content pack's YAML.
3. Updated `YamlContentPackLoader` to support folder-per-type YAML structures and ZIP (.cdx) packaging.
4. Added `ContentPackExporter` to synchronize authoring tool format with game engine loader.

## Verification
- **Theme Check**: Verify Theme Toggler swaps Mica/Acrylic effects and control styles without layout artifacts.
- **Form Check**: Create a mock 5e "Ability" schema and verify the form correctly renders and validates.
- **Graph Check**: Create a mock "Skill Tree" schema and verify the graph engine allows creating and connecting nodes with appropriate visual hints.
- **Persistence Check**: Verify changes made in both the form and graph views are correctly saved to YAML.

## Migration & Rollback
- The changes are additive to the UI. The legacy `GraphEditorView` code will be refactored but its core logic (Pan and Zoom) will be reused.
- Standard Git mechanisms for rollback if the dynamic engine becomes overly complex.