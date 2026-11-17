# Legal notices

## Software

TTRPG.Codex is source-available under the PolyForm Noncommercial License 1.0.0 (see `LICENSE`):
free for any noncommercial use, not for commercial use. It is not an OSI-approved open source
licence. Versions published before this change were released under the MIT License, and copies of
those versions stay MIT.

Bundled third-party software:

- Bootstrap 5.3.3 - MIT License - `src/Codex.Web/wwwroot/lib/bootstrap/LICENSE`
- Lucide icons - ISC License - `src/Codex.Web/wwwroot/lib/lucide/LICENSE`
- @github/combobox-nav 3.0.2 (the keyboard and ARIA behaviour of the model picker's list, bundled unmodified into `src/Codex.Web/wwwroot/lib/model-combobox/model-combobox.js`) - MIT License, Copyright (c) 2018 GitHub - `src/Codex.Web/wwwroot/lib/model-combobox/LICENSE`
- Avalonia 12 (the tray launcher, `src/Codex.Launcher`) and its SkiaSharp and HarfBuzzSharp
  dependencies - MIT License - https://github.com/AvaloniaUI/Avalonia
- QRCoder (QR codes in the launcher) - MIT License - https://github.com/codebude/QRCoder
- PuppeteerSharp (prints campaign books to PDF with a browser already on the machine; it downloads none) - MIT License -
  https://github.com/hardkoded/puppeteer-sharp
- PdfPig (reads the outline of a printed book to number its table of contents; also used by tests) - Apache License 2.0 - https://github.com/UglyToad/PdfPig
- The editable book's browser bundles (`src/Codex.Web/wwwroot/lib/book`, built by `tools/book-editor`): marked-hbfm, marked,
  CodeMirror and their dependencies, each under its own licence, all collected in
  `src/Codex.Web/wwwroot/lib/book/THIRD-PARTY-NOTICES.txt`.
- Homebrewery's "Blank" theme layout (`tools/book-editor/themes/blank.less`, compiled into
  `src/Codex.Core/Book/Themes/blank.css`), trimmed of its fonts and pictures - MIT License, Copyright (c) 2016 Scott Tolksdorf -
  https://github.com/naturalcrit/homebrewery (the notice is in the same `THIRD-PARTY-NOTICES.txt`)
- Homebrewery's "5e PHB" theme, as the book's Homebrewery compatibility layer (`tools/book-editor/themes/homebrewery-5e.less`,
  compiled into `src/Codex.Core/Book/Themes/homebrewery-5e.css` with its pictures, colours, shadows and gradients taken out: its
  layout only) - MIT License, the same notice. None of Homebrewery's pictures is shipped.

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

The Ashen Vellum book theme's typefaces, Latin subsets taken unmodified from their Fontsource packages (5.3.0), each with its
licence beside it:

- IM Fell English and IM Fell English SC, IM Fell DW Pica and IM Fell DW Pica SC, by Igino Marini
  (https://iginomarini.com/fell/) - SIL Open Font License 1.1 - `src/Codex.Web/wwwroot/fonts/im-fell-*/OFL.txt`
- Pirata One, Copyright (c) 2012 Rodrigo Fuenzalida, Nicolas Massi, Reserved Font Name "Pirata" - SIL Open Font License 1.1 -
  `src/Codex.Web/wwwroot/fonts/pirata-one/OFL.txt`
- Special Elite, by Astigmatic - Apache License 2.0 - `src/Codex.Web/wwwroot/fonts/special-elite/LICENSE.txt`

Its stand-ins for Russian and Hebrew books (the faces above have only Latin letters), also unmodified Fontsource 5.3.0 subsets, each with
its licence beside it; a book embeds only those of its own language:

- Old Standard TT (Cyrillic regular and italic; and its latin-ext file, used only for the obelisk the theme's lists and page numbers
  print), Copyright 2011 The Old Standard Project Authors - SIL Open Font License 1.1 - `src/Codex.Web/wwwroot/fonts/old-standard-tt/OFL.txt`
- Ruslan Display (Cyrillic), Copyright 2011 Oleg Snarsky, Denis Masharov, Vladimir Rabdu - SIL Open Font License 1.1 -
  `src/Codex.Web/wwwroot/fonts/ruslan-display/OFL.txt`
- Cousine (Cyrillic), Copyright The Cousine Project Authors (https://github.com/googlefonts/cousine) - SIL Open Font License 1.1 -
  `src/Codex.Web/wwwroot/fonts/cousine/OFL.txt`
- David Libre (Hebrew), Copyright 2016 The David Libre Project Authors (https://github.com/meirsadan/david-libre) - SIL Open Font
  License 1.1 - `src/Codex.Web/wwwroot/fonts/david-libre/OFL.txt`
- Frank Ruhl Libre Black (Hebrew, weight 900), from the same project as the Frank Ruhl Libre above - `src/Codex.Web/wwwroot/fonts/frank-ruhl-libre/OFL.txt`

For the stand-ins the theme wizard offers (docs/BOOK_EXPORT.md 41.22), the bold Cyrillic of Old Standard TT and Cousine and the bold Hebrew of
David Libre, from the same unmodified Fontsource 5.3.0 packages and under the same licences as above.

The book's pages are made with Homebrewery (https://homebrewery.naturalcrit.com/, by naturalcrit, MIT): its notice is in
`src/Codex.Web/wwwroot/lib/book/THIRD-PARTY-NOTICES.txt`, and the book editor, the theme wizard and the About page credit it visibly, by the
maintainer's wish, though MIT asks only for the notice.

The test fixtures `tests/Codex.Tests/Fixtures/Fonts/ruslan-display-cyrillic.{ttf,woff}` are the Ruslan Display file above written out
as TrueType and WOFF by fontTools, to test the font reader; same licence, beside them.

The faces Homebrewery's 5e layout names, for the book's Homebrewery compatibility layer: Homebrewery's own copies (commit 3e600a0),
unmodified, except the drop-cap face, Solbera Imitation 1.60 from Solbera's own release converted to WOFF2, in
`src/Codex.Web/wwwroot/fonts/homebrewery-5e` (its `README.txt` says what each is and where it came from):

- Bookinsanity, Scaly Sans, Scaly Sans Caps, Mr Eaves Small Caps, Nodesto Caps Condensed, Nodesto Caps Wide and Solbera Imitation,
  by Solbera, with fixes and additions by Ryrok, Ners and LUCASTUCIOUS and changes by Homebrewery's contributors - Creative Commons
  Attribution-ShareAlike 4.0 International - `CC-BY-SA-4.0.txt` there. Sharing them on under the same licence is what it asks; the
  book only embeds them in a PDF, which does not make the book itself ShareAlike.
- Overpass, Copyright 2021 The Overpass Project Authors - SIL Open Font License 1.1 - `overpass-OFL.txt` there
- Walter Turncoat, Copyright (c) 2010 Font Diner, Inc DBA Sideshow - Apache License 2.0 - `walter-turncoat-LICENSE.txt` there

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
  fields added. No rules were intentionally changed.
- The text was read from the JSON transcription in https://github.com/5e-bits/5e-srd-api
  (`packages/5e-database`, MIT License, Copyright (c) 2018-2020 Adrian Padua, Christopher Ward).
- Eleven spell descriptions that the transcription ran together or spliced were replaced with the wording of the
  SRD 5.2.1 itself (`tools/Codex.ContentImport/SpellTextFixes.cs`). The class feature tables' numbers were read from
  the Markdown transcription at https://github.com/downfallx/dnd-5e-srd-markdown (CC-BY-4.0): numbers only, no rules text.
- The system is named "Fifth Edition SRD (5.2.1)" and describes itself as "5E compatible", the
  wording the SRD's legal page permits. TTRPG.Codex is not affiliated with or endorsed by Wizards of
  the Coast, and no trademark is used as a product name.

### Forged in the Dark SRD (`content/systems/fitd-srd`, pack `packs/srd-fitd-2017`)

This work is based on Blades in the Dark (found at http://www.bladesinthedark.com/), product of One
Seven Design, developed and authored by John Harper, and licensed for our use under the Creative
Commons Attribution 3.0 Unported license (http://creativecommons.org/licenses/by/3.0/).

- That is the publisher's own attribution sentence (https://bladesinthedark.com/node/33, checked
  2026-11-11), copied exactly. The SRD's licence is CC-BY 3.0 Unported and is not share-alike: the
  app's own licence is unaffected.
- Modified from the original: the SRD's text is split into one entry per section, its lists become
  typed entries, and layout artefacts are cleaned. No rules were intentionally changed. The system
  definition (`system.yaml`) restates the SRD's mechanics as data.
- The text was read from the Markdown transcription at
  https://github.com/amazingrando/blades-in-the-dark-srd-content (commit
  `f141ed2c4ed8061b6833a57e37a314a03e4166ce`), which the publisher's site links as the SRD's text.
  It carries no copyright notice of its own beyond the attribution sentence above, and neither does
  the publisher's SRD page.
- Only the SRD is covered. The Blades in the Dark book, its setting (Duskwall and the Shattered
  Isles), NPCs, artwork, maps and logo are not included, and translations of the book are a matter
  for the author. Where the SRD's own examples name something from that setting, it is SRD text and
  is kept as written; nothing beyond it is added.
- The system is named "Forged in the Dark SRD" and never uses "Blades in the Dark" in a title; the
  name appears only inside the attribution. TTRPG.Codex is not affiliated with or endorsed by One
  Seven Design. The Forged in the Dark logo is not used (it would need the trademark notice the
  publisher gives).
- Not shipped inside the app: like every system other than the 5E one it is released as its own
  archive (`content/systems/bundled.txt`), so it can be withdrawn without a new release.

### Four Degrees (`content/systems/four-degrees`, packs `packs/homebrew-core` and `packs/rules-core`)

An adaptation of ORC-licensed rules (the data structure restates their mechanics, which the ORC treats as Adapted Material). The
`homebrew-core` pack is written in this project's own words with invented names, text and creatures; no text, table or stat block from any
book is copied into it. The `rules-core` pack holds ORC Licensed Material from the player book of the upstream game (so far the
conditions and the rules for their values and overrides): it is generated by the local book importer from the book's own text, with flavor,
names and cross references removed or reworded, and every entry says in its `source` line which chapter and section of the book it
comes from. The book file is never committed. Book text enters the repository in the pack, and in a redacted form in the replacement texts
of the redaction file named below; nowhere else. The pack is not in `bundled.txt`.

- The system and its pack are released under the ORC License. This folder's data is under the ORC; the code of this repository stays under
  its own licence, since the ORC grants no rights to code.
- The manifests carry the ORC Notice and the upstream Attribution (credit line and authors list), both copied verbatim from the upstream
  book's own ORC Notice, plus this product's Reserved Material and Expressly Designated Licensed Material statements.
- The system is not named after the upstream game and uses no publisher, setting or deity name. The publisher and product are named only
  in the attribution line, which the ORC requires.
- Not shipped inside the app: like every system other than the 5E one it is released as its own archive, so it can be withdrawn without a
  new release.
- `tools/Codex.ContentImport/Data/degrees-redactions.yaml` (the replacement texts of the local book importer's redaction step),
  `degrees-allowlist.txt` (game terms), `degrees-denylist-waivers.txt` (reviewed game terms) and `degrees-chapters.yaml` (bookmark titles, which are
  game terms) are ORC data, released with the packs the importer generates, as `Data/srd521-traps.yaml` is CC BY data. A redaction row stores
  only the hash and length of the span it changes, never the span, but a `replace` row's text is a redacted form of a sentence of the book: it is
  redacted Licensed Material (as the design of the importer says) and carries the same notices as the pack. `degrees-denylist.sha256` and
  `degrees-common.sha256` are hashes only. None of these holds a name.
- Reserved Material: the name Four Degrees and every name invented for it (ancestries, backgrounds, classes, creatures, items, runes,
  hazards and places), with their descriptive and flavour text. The rules and numbers are not reserved. The product contains no Expressly
  Designated Licensed Material. The `rules-core` manifest designates no Reserved Material of its own (ORC section II.b).

### Example content (`content/examples/`)

The `simple-d6` example system and the `homebrew-world` example setting are released to the public
domain under CC0 1.0 by the TTRPG.Codex project.

### What is deliberately not included

- Non-SRD fifth-edition material (other subclasses, most feats, setting lore): proprietary.
- Material that reaches this project only through terms that can be withdrawn or do not pass to it: community data sets, fan-content
  policies and licences between third parties. The ORC grants a perpetual, irrevocable licence to the rules of the books published under
  it, so packs are generated from those books themselves, never from a site or data set that carries the same text under other terms.
  Such a pack is released under the ORC with an ORC Notice and the upstream attribution (this project's code stays under its own licence,
  since the ORC grants no rights to code), and it leaves out Reserved Material: trademarks and trade dress, art, settings, deities, named
  characters and places, and narrative text, as designated by the ORC Notice in each book. The book files themselves are never
  committed, and neither is any text of them except the generated pack.
- Material published under an older open licence that carries its own notice and Product Identity obligations: not used, to keep to a
  single licence per system.
- Games without an open licence: no content from them is included. Where a publisher offers only a
  fan or homebrew policy, a system definition a user writes for such a game stays limited to
  attributes, resources and formulas, with no rules prose, stat blocks, trademarks, art or trade
  dress, and anything beyond that comes from the user's own copy of the books. The reference
  fixtures under `tests/Codex.Tests/Fixtures/reference` use this project's own words and numbers:
  `street` is original, `d35` follows the shape of the d20 System Reference Document 3.5 (Open Game
  License 1.0a), `crew` follows the Forged in the Dark SRD (CC BY 3.0), and `degrees` is a d20
  four-degree system with this project's own names and numbers, released under CC0 like the other fixtures.
