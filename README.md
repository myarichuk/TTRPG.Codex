# TTRPG.Codex

> ⚠️ **Work in progress.** Features and file formats may still change.

**A self-hosted companion for running tabletop role-playing campaigns.** One DM, any number of
players, one browser tab each: a live table with initiative, hit points and conditions; campaign
prep (cast, places, lore with per-character reveal); session recaps and notes; and a Library of rules
content that the DM can extend with homebrew.

Game systems are **data**: a `system.yaml` describes attributes, the character sheet and the kinds
of content a system has; packs of YAML files supply the content. The repository ships the
**Fifth Edition SRD (5.2.1)** under CC-BY-4.0 and two small examples (a homebrew system, a setting
pack). See [docs/CONTENT_PACKS.md](docs/CONTENT_PACKS.md) for the format and
[LEGAL.md](LEGAL.md) for licences.

## Run it

```sh
dotnet run --project src/Codex.Web
```

Open http://localhost:5183, create an account (the first account becomes server admin), create a
campaign, and share its invite link with players. Data lives in an embedded RavenDB under
`src/Codex.Web/RavenData` (configurable via `Codex:DataDirectory`); a daily backup is written to
`<data>/Backups`. Nothing is fetched from the internet while the app runs.

Content is loaded from the folders in `Codex:ContentDirectories` (`appsettings.json`), plus
`content/` and `plugins/` next to a published executable.

## Layout

| Project | What |
|---|---|
| `src/Codex.Plugin.Abstractions` | The contracts: `SystemDefinition`, `ContentEntry`, `PackManifest`, `ICodexSystemPlugin`, dice. |
| `src/Codex.Core` | Loading systems and packs, the content registry and search, the ECS world, scripting, text formatting. |
| `src/Codex.Persistence` | RavenDB documents and repositories; the per-campaign live runtime (single-writer command loop). |
| `src/Codex.Web` | The Blazor Server UI. |
| `tools/Codex.ContentImport` | Converts open rules data (5e-bits SRD JSON) into packs. |
| `content/` | Shipped systems and packs. |
| `tests/Codex.Tests` | xUnit unit tests and Playwright end-to-end scenarios. |

## Tests

```sh
dotnet test
```

The Playwright scenarios under `tests/Codex.Tests/E2E` boot the real app and drive it with headless
Chromium; install the browser once with `pwsh tests/Codex.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`
(CI does this). Run only the unit tests with `dotnet test --filter "FullyQualifiedName!~E2E"`.

## Regenerating the SRD pack

```sh
dotnet run --project tools/Codex.ContentImport -- srd5e --download --source /tmp/srd-json --out content/systems/srd5e/packs/srd-5.2.1
```

## Releases

Pushing a tag such as `v0.2.0` builds single-file executables for Windows, Linux and macOS with the
`content/` folder beside them. Conventional commit titles (`feat:`, `fix:`, `chore:`…) drive the
version number through GitVersion.
