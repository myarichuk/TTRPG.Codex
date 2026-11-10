# TTRPG.Codex

> ⚠️ **Work in progress.** Features and file formats may still change.

**A self-hosted companion for running tabletop role-playing campaigns.** One GM, any number of
players, one browser tab each: a live table with initiative, hit points and conditions; campaign
prep (cast with portraits, a bestiary, places, a linked world wiki, lore with per-character
reveal); session recaps, table notes and each player's private journal; a Library of rules content
that the GM extends with homebrew (equipment, classes, subclasses, feats, spells, creatures) and
carries between campaigns; and optional AI drafting on your own key.

Game systems are **data**: a `system.yaml` describes attributes, the character sheet and the kinds
of content a system has; packs of YAML files supply the content. The repository ships the
**Fifth Edition SRD (5.2.1)** under CC-BY-4.0 and two small examples (a homebrew system, a setting
pack). See [docs/CONTENT_PACKS.md](docs/CONTENT_PACKS.md) for the format and
[LEGAL.md](LEGAL.md) for licences.

The project website, with screenshots, an install guide and a usage guide in English, Russian and Hebrew, lives in
[`site/`](site/README.md) and is published to GitHub Pages: <https://myarichuk.github.io/TTRPG.Codex/>.

## Run it

**Download:** the [latest release](https://github.com/myarichuk/TTRPG.Codex/releases) has an
installer for Windows, macOS and Linux. Start **TTRPG.Codex** and it sits in your system tray (menu
bar on macOS): it starts the server, opens your browser, and its menu starts or stops the server,
copies the link for your players and opens the log. Tick "Let players on my network join" in its
window to host a table from your laptop. Players outside your network need a VPN or tunnel such as
Tailscale. Nothing else needs installing.

If you have [Tailscale](https://tailscale.com) installed and signed in, "Open to the internet (Tailscale
Funnel)" gives players one `https://` link that works from anywhere, with nothing for them to install. The
first time, Tailscale asks the owner of your Tailscale network to approve Funnel; the launcher opens that
page. While it is on, anyone who finds the link can see the sign-in page, so new accounts need a campaign
invite, only the server admin can use the AI key set in the server's own configuration, and the switch turns
itself off whenever the launcher closes. Create your own account before turning it on.

**From source:**

```sh
dotnet run --project src/Codex.Web
```

Open http://localhost:5183, create an account (the first account made on the server's own computer becomes
server admin; set `Codex:AdminUsername` to name one from elsewhere), create a campaign, and share its invite
link with players. Data lives in an embedded RavenDB in your per-user folder, the same one the launchers use
(`~/Library/Application Support/TTRPG.Codex/RavenData` on macOS, `%LOCALAPPDATA%\TTRPG.Codex\RavenData` on Windows,
`~/.local/share/ttrpg-codex/RavenData` on Linux; `Codex:DataDirectory` or `CODEX_DATA_HOME` moves it). So a source
run and the installed app see the same campaigns; run only one at a time. The key ring that protects sign-ins and stored
AI keys is the `Keys` folder beside `RavenData`: move or back up the two together, or stored AI keys need entering
again. On first start the server moves data an older build left under `bin/` or `src/Codex.Web/RavenData` into these
folders. Nothing is fetched from the internet while the app runs.

**Backups.** Every day (and a minute after start, when the last one is older) the server writes a folder under
`<data>/Backups/`: each campaign as the same JSON **Export campaign** makes, with its images and the players'
journals beside it, and the accounts with their AI settings but no API keys. It keeps the newest 7
(`Codex:Backups:Keep`; `Codex:Backups:Enabled` turns it off). To bring a campaign back, tick "restore it as a new
campaign" in any campaign's Settings and merge its `campaign.json`; images and accounts are files to copy back by
hand for now. `users.json` holds the accounts' password hashes, so keep the folder as private as the data folder.
The About page shows the server admin when the last backup ran. The database's own backup is
also requested, but RavenDB's free licence refuses it, and About says so.

Content is loaded from the folders in `Codex:ContentDirectories` (`appsettings.json`), plus
`content/` and `plugins/` next to a published executable, plus the folder systems installed from the app live in. Problems
with a pack (an entry that does not parse, a missing base it inherits from) are listed on the About page, and the campaign
Library says when there are any.

**Installing a system.** The admin installs, replaces or removes a system from the About page: a zip of its folder, checked
before anything changes, never with compiled code (see [docs/CONTENT_PACKS.md](docs/CONTENT_PACKS.md#installing-reloading-and-versions)).
*Reload systems* there picks up a change made on disk without a restart; a table that is running keeps its system until it
ends. A campaign records the system's version and file hashes, and its GM is told what changed. The keys of system.yaml are
in the generated [reference](docs/SYSTEM_YAML_REFERENCE.md) and [JSON schema](docs/schema/system.schema.json). A release
ships srd5e inside the app; any other system under `content/systems/` is its own download.

**Plugins are programs.** Packs (YAML, and `.zip`/`.cdx` archives of it) are data. A `.dll` in any content
folder, including the GM's "My content" folder the launcher opens, is loaded into the server and runs with
everything the server can reach: every campaign, every account, the AI keys. That holds for a DLL beside a
`system.yaml` that names a type in it (a compiled function, event handler or sheet widget) as much as for a
whole plugin; About marks such a system "contains compiled code". Put a DLL there only if you would run it as
a program on that computer.

**Settings.** Each goes in `appsettings.json`, or in an environment variable with `__` for `:`
(`Codex__DataDirectory`). The tray launcher sets the ones marked *launcher* itself.

| Setting | What it does |
| --- | --- |
| `Codex:DataDirectory` | Where the database, images and backups live. Default: `RavenData` in the per-user folder; a relative path is taken from the app's folder. |
| `Codex:KeysDirectory` | Where the Data Protection key ring lives. Default: `Keys` beside the data folder. |
| `Codex:ContentDirectories` | Folders of systems, packs and plugins to load (a list). `Codex:PluginsDirectory` is the older single folder. |
| `Codex:Systems:InstallDirectory` | Where systems installed from the app live. Default: `Systems` beside the data folder. |
| `Codex:Systems:GmInstall` | `true` lets anyone signed in install and remove systems, not only the server admin; never while the server is open to the internet. |
| `Codex:Systems:Watch` | `true` reloads the systems by itself when a file under a content root changes. Default: on in Development, off otherwise. |
| `Codex:Systems:Scripts` | `false` turns system scripts off: a system that names one does not load, and an upload with one is refused. |
| `Codex:AdminUsername` | The account that is server admin, by name. Without it, the first account made on the server's own computer is. |
| `Authentication:EnableLocalLogin` | `false` hides the name-and-password sign-in, leaving only the external providers configured under `Authentication:`. |
| `Codex:DefaultCulture` | The language for a visitor who has not picked one (`en`, `he`, `ru`); without it the browser's language decides. *Launcher.* |
| `Codex:PublicUrl` | The address in the invite link a GM copies, when the host knows a better one than the browser's. *Launcher.* |
| `Codex:RemoteUrl` | A second invite link, for players who reach the host another way (over Tailscale, say). *Launcher.* |
| `Codex:PublicExposure` | `true` while the server is open to the internet: sign-up needs an invite, only the server admin may use the server's AI key, and nobody can claim server admin. *Launcher*, with Funnel on. |
| `Codex:StopOnStdinClose` | Stop the server when its standard input closes: how the launcher stops it. *Launcher.* |
| `Codex:Backups:Enabled`, `Codex:Backups:Keep` | The daily backup, above. |
| `AI:DailyLimitPerUser`, `AIImage:DailyLimitPerUser` | How many requests (200) and pictures (20) each account may make a day on the server's own AI key, when it has no model of its own; 0 for no limit. The server admin has none. The counts are kept in memory, so a restart starts the day over. **AI settings** says the key is shared, and shows the admin who used it today. |
| `Codex:EnableTestSeedEndpoint` | The end-to-end tests' seeding endpoints, which write accounts and campaigns straight into the database for anyone who calls them. Off by default; never on outside the tests. |

The embedded RavenDB server listens on a random port on `127.0.0.1` with no authentication. Only programs on the
same computer can reach it, which is fine for a desktop app, but anything running there can read every campaign.

## Layout

| Project | What |
|---|---|
| `src/Codex.Plugin.Abstractions` | The contracts: `SystemDefinition`, `ContentEntry`, `PackManifest`, `ICodexSystemPlugin`, dice. |
| `src/Codex.Core` | Loading systems and packs, the content registry and search, the ECS world, scripting, text formatting. |
| `src/Codex.Persistence` | RavenDB documents and repositories; the per-campaign live runtime (single-writer command loop). |
| `src/Codex.Web` | The Blazor Server UI. |
| `src/Codex.Launcher` | The Avalonia tray app that starts and stops the server on a GM's computer. |
| `tools/Codex.ContentImport` | Converts open rules data (5e-bits SRD JSON) into packs. |
| `content/` | Shipped systems and packs. |
| `tests/Codex.Tests` | xUnit unit tests and Playwright end-to-end scenarios. |

## AI features

Every "Draft with AI" button talks to a chat model you choose under **AI settings** (click your
name in the header). Providers are per user and OpenAI-compatible: OpenRouter is pre-selected
(one key, most current models), with presets for OpenAI, Groq, Mistral, a local Ollama or LM
Studio, or any custom `/v1/chat/completions` endpoint. API keys are stored encrypted with ASP.NET
Data Protection, each under its own user; the key ring is in `Keys/` inside the server's data
directory, so keep that folder with the data when you move or back it up. The ring itself is not
encrypted at rest, so anyone who can read the data directory can read the keys. The `AI` section of `appsettings.json` is an
optional server-wide fallback for users who set nothing up. Every account on a LAN server shares it, each within a daily allowance (`AI:DailyLimitPerUser`, above). Nothing is sent to a model until
someone clicks an AI button.

What the AI does today:

- **Session recaps**: a player-facing recap from the event log, party notes and open rolls, and a
  separate GM summary that also sees secret rolls, private notes and the previous summary.
  Player drafts are built only from player-visible material.
- **Histories and bios**: write or rewrite a character's history (player-visible) or the GM's
  private notes on an NPC, without contradicting the sheet.
- **Level-up advice**: what the next level brings from the Library's class and subclass entries,
  the choices it forces, and picks that fit the sheet and story.
- **Characters from a concept**: one line of concept becomes a draft sheet using only classes,
  species and backgrounds loaded in the campaign's Library, plus a short history.
- **Wiki pages**: expand rough notes into a linked page.
- **World builder**: named chats with your model, kept per campaign, that read the campaign and the
  Library and propose new places, people, factions, quests, secrets, spells, magic items, equipment
  and creatures (or variants of the Library's), changes to existing pages, places, histories and
  homebrew (reviewed as a word-by-word diff), Bestiary picks, and tags. Nothing is saved until you
  apply it, and everything starts hidden. Each tool call the model makes can be opened, down to the
  raw JSON. Tags then let the Library export one set of homebrew as a pack to share.

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

Run **Actions > Release > Run workflow** (optionally with a version, pre-release and draft flags), or
push a tag such as `v0.2.0`. Each platform is built and smoke-tested on its own runner:

| Platform | Installer | Portable |
| --- | --- | --- |
| Windows x64 | Inno Setup `-setup.exe` (per-user, no admin) | `.zip` |
| macOS Apple silicon | `.pkg` (installs to Applications) | `.tar.gz` |
| Linux x64 | `.deb` (`/opt/ttrpg-codex`, `ttrpg-codex` on the PATH) | `.tar.gz` |

Every package carries the .NET and ASP.NET Core runtimes and a launcher that keeps campaign data in
the user's folder. The embedded RavenDB server runs as its own `dotnet Raven.Server.dll` process and
needs those full shared runtimes, so the app runs on the same runtime (framework-dependent,
ReadyToRun-compiled) instead of shipping a second, trimmed copy, and RavenDB's native libraries
are trimmed to the target platform (that alone halves the download); Native AOT is ruled out by Blazor
Server, RavenDB, YamlDotNet and DynamicExpresso. Build the same packages locally with
`packaging/package.sh <rid> <version>` or `pwsh packaging/package.ps1 -Version <version>`, and check
one with `packaging/smoke-test.sh dist/<rid>/app`.

Without an explicit version, conventional commit titles (`feat:`, `fix:`, `chore:`…) drive the
version number through GitVersion.
