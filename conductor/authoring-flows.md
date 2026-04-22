# Plan: Content Pack Authoring Flows

## Background & Motivation
The authoring app needs comprehensive flows to create and edit content packages. Rather than expecting authors to build ZIP files and YAML manifests by hand, the UI should provide an intuitive "New Project" wizard. This wizard will inspect the available TTRPG systems, allow the user to select one, and input package metadata (Name, Description, Thumbnail) before generating the boilerplate.

## Scope & Impact
This updates the Authoring App's Dashboard and the core Plugin Abstractions (`PackManifest`, `ICodexSystemPlugin`) to support richer metadata for visual presentation.

## Proposed Solution: The "New Pack" Wizard
1. **Rich System Metadata**: `ICodexSystemPlugin` needs an official `Name`, `Description`, and `ThumbnailPath` so the Authoring App can present a visually appealing list of available game systems.
2. **Rich Pack Metadata**: `PackManifest` needs `Description` and `ThumbnailPath` to make user-created packs visually distinct on the Dashboard.
3. **Dashboard Enhancements**: The Dashboard will feature "Create New Pack" and "Open Existing Pack" actions.
4. **Wizard UI**: A multi-step or single-page dialog where authors:
   - Select a target system (e.g., D&D 5e).
   - Enter Pack Name, Description, and select a Thumbnail.
   - Click "Create," which generates an empty workspace and transitions to the Entity Browser.

## Implementation Plan

### Phase 1: Metadata Abstraction Updates
1. Add `Description` and `ThumbnailPath` to `PackManifest` record in `Codex.Plugin.Abstractions`.
2. Add `Name`, `Description`, and `ThumbnailPath` to `ICodexSystemPlugin`.
3. Update `DnD5ePlugin` and `SwffgPlugin` to supply this metadata (e.g., Placeholder text and default icons).

### Phase 2: Dashboard UI Enhancements
1. Update `DashboardView.axaml` to replace the "Load Pack" placeholder with two prominent buttons: "Create New Pack" and "Open Pack".
2. Create a new Avalonia Window or UserControl called `NewPackDialogView.axaml`.

### Phase 3: The Wizard & Workspace Initialization
1. Implement the `NewPackDialogViewModel` that queries the `PluginLoader` for available systems.
2. Build the UI for the wizard, featuring a list or grid of available systems (with thumbnails).
3. On completion, generate an empty `PackManifest` object in-memory, set it as the "Active Context," and navigate the user to the `Entity Browser`.

## Verification
- Run the Authoring App and open the "Create New Pack" dialog.
- Verify that D&D 5e and SWFFG appear as selectable options with their descriptions.
- Verify that finishing the wizard successfully initializes an empty pack session in the app's state.

## Migration & Rollback
- Changes to `PackManifest` add optional fields, which is backward compatible with existing JSON serialization.
- Legacy plugins will need to be updated to support the new `ICodexSystemPlugin` properties.