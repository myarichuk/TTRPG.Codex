# Legal notices

## Software

TTRPG.Codex is source-available under the PolyForm Noncommercial License 1.0.0 (see `LICENSE`):
free for any noncommercial use, not for commercial use. It is not an OSI-approved open source
licence. Versions published before this change were released under the MIT License, and copies of
those versions stay MIT.

Bundled third-party software:

- Bootstrap 5.3.3 - MIT License - `src/Codex.Web/wwwroot/lib/bootstrap/LICENSE`
- Lucide icons - ISC License - `src/Codex.Web/wwwroot/lib/lucide/LICENSE`
- Avalonia 12 (the tray launcher, `src/Codex.Launcher`) and its SkiaSharp and HarfBuzzSharp
  dependencies - MIT License - https://github.com/AvaloniaUI/Avalonia
- QRCoder (QR codes in the launcher) - MIT License - https://github.com/codebude/QRCoder

### RavenDB

Campaign data is stored in RavenDB, run as the unmodified `RavenDB.Embedded` server in a separate
process that the app talks to over HTTP. The client library (`RavenDB.Client`) is MIT licensed.
The server is Copyright RavenDB Ltd and is used under the GNU Affero General Public License v3
(`licenses/AGPL-3.0.txt`, shipped as `AGPL-3.0.txt` next to the packaged app), as RavenDB's own
licence file for the package states. RavenDB is also available under a commercial licence.

- The packaged downloads include the RavenDB server binaries as published by RavenDB Ltd, without
  changes. Its complete corresponding source is at https://github.com/ravendb/ravendb (the
  release matching the `RavenDB.Embedded` version in `src/Codex.Persistence/Codex.Persistence.csproj`).
- TTRPG.Codex's own code is a separate program, not a modification of or linked into the server, so
  the AGPL covers the RavenDB server only; the PolyForm licence above covers this project's code.
- If you modify the RavenDB server and let others use it, the AGPL requires you to offer them your
  modified source.

Bundled typefaces, self-hosted under `src/Codex.Web/wwwroot/fonts/` (the app makes no request to a
font CDN). Both are licensed under the SIL Open Font License 1.1, whose text sits beside the font
files as `OFL.txt`; the Latin subsets were taken unmodified from the Fontsource packages
(`@fontsource-variable/cinzel` 5.3.0, `@fontsource/alegreya-sans` 5.3.0):

- Cinzel (variable weight 400-900), Copyright 2020 The Cinzel Project Authors
  (https://github.com/NDISCOVER/Cinzel) - used for headings, the brand, ability modifiers and the
  HP/AC numerals - `src/Codex.Web/wwwroot/fonts/cinzel/OFL.txt`
- Alegreya Sans (regular, medium, bold, regular italic), Copyright 2013 The Alegreya Sans Project
  Authors (https://github.com/huertatipografica/Alegreya-Sans) - the body face -
  `src/Codex.Web/wwwroot/fonts/alegreya-sans/OFL.txt` (the bundled files also include its Cyrillic subset)
- Forum, Copyright (c) 2011 Denis Masharov (https://fonts.google.com/specimen/Forum) - Cyrillic headings beside
  Cinzel - `src/Codex.Web/wwwroot/fonts/forum/OFL.txt`
- Frank Ruhl Libre, Copyright 2015 The Frank Ruhl Libre Project Authors (https://github.com/fontef/frankruhllibre) -
  Hebrew headings beside Cinzel - `src/Codex.Web/wwwroot/fonts/frank-ruhl-libre/OFL.txt`
- Assistant, Copyright 2020 The Assistant Project Authors (https://github.com/hafontia/Assistant) - Hebrew body
  text beside Alegreya Sans - `src/Codex.Web/wwwroot/fonts/assistant/OFL.txt`

### Launcher artwork

The background of the tray launcher window (`src/Codex.Launcher/Assets/background.jpg`) is an image
the project author generated with Google's Gemini image model. It contains no third-party artwork.
Like the rest of the project's own files it is not licensed for commercial use. (Images made by an
AI model may not be protected by copyright in some countries, so others may be free to reuse
similar ones.)

## Game content

Game content is shipped as data under `content/`, each pack with its own licence in its
`manifest.yaml`. The application shows every pack's attribution on the **About & licences** page and
under each entry in the Library.

### System Reference Document 5.2.1 (`content/systems/srd5e/packs/srd-5.2.1`)

This work includes material from the System Reference Document 5.2.1 (“SRD 5.2.1”) by Wizards of the
Coast LLC, available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative
Commons Attribution 4.0 International License, available at
https://creativecommons.org/licenses/by/4.0/legalcode.

- Modified from the original: restructured into YAML entries, layout artefacts removed, structured
  fields added. No rules were changed.
- The text was read from the JSON transcription in https://github.com/5e-bits/5e-srd-api
  (`packages/5e-database`, MIT License, Copyright (c) 2018-2020 Adrian Padua, Christopher Ward).
- The system is named "Fifth Edition SRD (5.2.1)" and describes itself as "5E compatible", the
  wording the SRD's legal page permits. TTRPG.Codex is not affiliated with or endorsed by Wizards of
  the Coast, and no trademark is used as a product name.

### Example content (`content/examples/`)

The `simple-d6` example system and the `homebrew-world` example setting are released to the public
domain under CC0 1.0 by the TTRPG.Codex project.

### What is deliberately not included

- Non-SRD fifth-edition material (other subclasses, most feats, setting lore): proprietary.
- Pathfinder Second Edition Remaster: its rules text is under the ORC License, but no ORC-licensed
  machine-readable dataset exists, and the community datasets (Foundry VTT, Pf2ools, Archives of
  Nethys) are distributed under Paizo's Community Use Policy, which cannot be re-licensed into this
  project. A PF2E-shaped system can still be defined in `system.yaml` and filled from a local,
  ORC-compliant transcription that is not redistributed here.
