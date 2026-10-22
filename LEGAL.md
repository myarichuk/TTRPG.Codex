# Legal notices

## Software

TTRPG.Codex is source-available under the PolyForm Noncommercial License 1.0.0 (see `LICENSE`):
free for any noncommercial use, not for commercial use. It is not an OSI-approved open source
licence. Versions published before this change were released under the MIT License, and copies of
those versions stay MIT.

Bundled third-party software:

- Bootstrap 5.3.3 - MIT License - `src/Codex.Web/wwwroot/lib/bootstrap/LICENSE`
- Lucide icons - ISC License - `src/Codex.Web/wwwroot/lib/lucide/LICENSE`

Bundled typefaces, self-hosted under `src/Codex.Web/wwwroot/fonts/` (the app makes no request to a
font CDN). Both are licensed under the SIL Open Font License 1.1, whose text sits beside the font
files as `OFL.txt`; the Latin subsets were taken unmodified from the Fontsource packages
(`@fontsource-variable/cinzel` 5.3.0, `@fontsource/alegreya-sans` 5.3.0):

- Cinzel (variable weight 400-900), Copyright 2020 The Cinzel Project Authors
  (https://github.com/NDISCOVER/Cinzel) - used for headings, the brand, ability modifiers and the
  HP/AC numerals - `src/Codex.Web/wwwroot/fonts/cinzel/OFL.txt`
- Alegreya Sans (regular, medium, bold, regular italic), Copyright 2013 The Alegreya Sans Project
  Authors (https://github.com/huertatipografica/Alegreya-Sans) - the body face -
  `src/Codex.Web/wwwroot/fonts/alegreya-sans/OFL.txt`

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
