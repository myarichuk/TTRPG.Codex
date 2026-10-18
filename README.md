# TTRPG.Codex

> ⚠️ **Work in progress.** Features and file formats may still change.

**A self-hosted companion for running tabletop role-playing campaigns.** One DM, any number of
players, one browser tab each: a live table with initiative, hit points and conditions; campaign
prep (cast with portraits, a bestiary, places, a linked world wiki, lore with per-character
reveal); session recaps, table notes and each player's private journal; a Library of rules content
that the DM extends with homebrew (equipment, classes, subclasses, feats, spells, creatures) and
carries between campaigns; and optional AI drafting on your own key.

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

## AI features

Every "Draft with AI" button talks to a chat model you choose under **AI settings** (click your
name in the header). Providers are per user and OpenAI-compatible: OpenRouter is pre-selected
(one key, most current models), with presets for OpenAI, Groq, Mistral, a local Ollama or LM
Studio, or any custom `/v1/chat/completions` endpoint. API keys are stored encrypted with ASP.NET
Data Protection under the server's data directory. The `AI` section of `appsettings.json` is an
optional server-wide fallback for users who set nothing up. Nothing is sent to a model until
someone clicks an AI button.

What the AI does today:

- **Session recaps**: a player-facing recap from the event log, party notes and open rolls, and a
  separate DM summary that also sees secret rolls, private notes and the previous summary.
  Player drafts are built only from player-visible material.
- **Histories and bios**: write or rewrite a character's history (player-visible) or the DM's
  private notes on an NPC, without contradicting the sheet.
- **Level-up advice**: what the next level brings from the Library's class and subclass entries,
  the choices it forces, and picks that fit the sheet and story.
- **Characters from a concept**: one line of concept becomes a draft sheet using only classes,
  species and backgrounds loaded in the campaign's Library, plus a short history.
- **Wiki pages**: expand rough notes into a linked page.

## Install on a phone

The web app is installable (a PWA): open it in Safari or Chrome on a phone and choose "Add to Home
Screen". It runs full-screen with the app icon; the live table still needs a connection, so
offline you get a plain "you're offline" page rather than stale data. A native companion app is
assessed in [docs/MOBILE.md](docs/MOBILE.md).

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
