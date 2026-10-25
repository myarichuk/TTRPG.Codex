# TTRPG.Codex Specification

Welcome to the central grimoire of the Codex project. This documentation defines the architecture, user experience, and roadmap for the ultimate TTRPG management tool.

## Philosophy
The Codex is designed to be **invisible but omnipresent**. It provides the DM with the tools to manage complex worlds without breaking the flow of narration, and gives players a window into a living, breathing landscape.

## Documentation Index

1. [**Player & DM Modes**](PLAYER_DM_MODES.md)
   - Understanding Role-Based Access and Visibility.
2. [**Core Entities**](ENTITY_SPEC.md)
   - Deep dive into Sessions, Campaigns, Characters, and Lore.
3. [**UI/UX Specification**](UI_SPECIFICATION.md)
   - Screen-by-screen breakdown and design principles.
4. [**Content packs & systems**](CONTENT_PACKS.md)
   - The data format: `system.yaml`, `manifest.yaml`, entries, homebrew, the SRD importer.
5. [**Localization**](LOCALIZATION.md)
   - English, Russian and Hebrew (RTL): UI resources, system label overlays, translation packs, the glossary.
6. [**Audit of 2026-10-18**](AUDIT_2026-10-18.md)
   - Findings (incomplete features, UI/accessibility, licensing) and what was changed in response.
7. [**Review & Remediation Plan (2026-09-25)**](REVIEW_AND_REMEDIATION_PLAN.md)
   - The earlier review; phases 0-4 landed, the rest is superseded by the audit above.

## Getting Started for Developers
- The UI is Blazor (Interactive Server) with a small token-based stylesheet (light and dark themes) over a vendored Bootstrap.
- Persistence is an embedded RavenDB.
- Game systems and content are YAML under `content/`; see CONTENT_PACKS.md.
