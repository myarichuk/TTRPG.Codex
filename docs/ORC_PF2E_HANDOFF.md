# Handoff: Pathfinder 2e Remaster pack under the ORC - licensing evaluation and next steps

Written 2026-11-13 by a cloud session, for a local session that can read the purchased PDFs (too large to upload to the cloud
session, and kept out of it on purpose). Scope is **Pathfinder Second Edition Remaster only**; Starfinder was set aside.
This is research, not legal advice; the AxE itself says it is not legal advice.

## 1. Goal

Add a PF2e Remaster rules pack to TTRPG.Codex: transcribed from Paizo's ORC-licensed core books, released under the ORC, with
the ORC Notice and Paizo's requested attribution, Reserved Material left out. The app and its code stay under PolyForm
Noncommercial. A commercial edition may come later. An email to Paizo licensing (dataset question, credit wording) was sent and
has not been answered; nothing below depends on a reply.

## 2. Bottom line

The mechanics in Paizo's Remaster core books are licensed to anyone, irrevocably, with no non-commercial limit, in all media
including software and AI uses. The legal basis is the ORC grant that the book's own notice invokes. So a pack of **verbatim
mechanics, with Reserved Material removed, released under the ORC with attribution, is a supported path**. It is not
"fair game" unconditionally; the conditions in section 5 are what make it safe.

Primary sources read in full (all in the session's uploads, or quoted by the user): ORC License Final (5 pp), the ORC AxE
(9 pp), the Player Core ORC Notice (p. 463, second printing 2026, screenshot), Paizo Compatibility License 1.0 (5 pp), Paizo
Terms of Use (pasted text), Foundry pf2e README and `src/module/data.ts`, and this repo's `LEGAL.md`.

## 3. Findings

**ORC mechanics**
- Licensed Material (License section I.e) is "expressions reasonably necessary to convey functional ideas and methods of
  operation of a game": rules, statblocks, traits, classes, spells, equipment, conditions and the like. Grant (II.a): worldwide,
  royalty-free, non-sublicensable, non-exclusive, irrevocable, "in all media and formats". Neither the license nor the AxE
  mentions commercial restrictions. The AxE plain-English page lists "printed books, video games, podcasts, AI".
- Reserved Material is defined by the **license text** (I.h): trademarks, trade dress, art, "clearly expressed and sufficiently
  delineated characters, character organizations, dialogue, settings, locations, worlds, plots, or storylines, including proper
  nouns and the adjectives, names, and titles derived from proper nouns". A book's notice can only **widen** the grant (I.e(1),
  "Expressly Designated Licensed Material"); its reserved list is non-exhaustive. The denylist therefore comes from the
  definition, with the notice as a hint. (An earlier draft of this analysis got this backwards.)
- AxE p5: verbatim use of Licensed Material is allowed. Non-essential colorful description is outside the grant; a trivial
  phrase ("beam of sickly green light") is de minimis and licensed. AxE p6: remove proper nouns by deleting or renaming
  ("Bimbol's Bursting Bunion" becomes "Bursting Bunion"); descriptions about the named person are not Licensed Material.
- II.b: adapted Licensed Material goes onward under the ORC with no different terms and no Effective Technological Measures.
  II.c: the database/data structure counts as Adapted Licensed Material. So everything mechanics-bearing in the pack, including
  a `system.yaml` that restates the rules, is ORC.
- I.c(ii) and AxE p5: for a product with sections for several non-interoperable systems, only the portion derived from the
  Licensed Material is the Derivative Work. The separate-pack architecture fits. AxE p8-9: no source-code obligation; only the
  new game mechanics you developed are given back, never code.
- AxE p8: ORC content may include CC-BY content, but ORC material cannot go into a CC-BY product. Keep this pack out of the
  srd5e and fitd-srd packs, and do not let those packs `inherits` from its entries.
- III: required notice = ORC Notice + Attribution naming each licensor/creator (III.b.i) + a statement of how **you** wish to
  be credited for your adaptation (III.b.ii) + your own Reserved Material notice if any (III.c). V.a: breach terminates, with a
  60-day cure; only the licensor and upstream licensors can enforce.

**The Player Core notice (p. 463, second printing 2026)**
- "Expressly Designated Licensed Material: This product contains no Expressly Designated Licensed Material." Nothing Reserved
  has been opted in.
- Reserved Material includes "proper nouns (characters, deities, locations, etc., as well as all adjectives, names, titles,
  and descriptive terms derived from proper nouns), artworks, characters, dialogue, locations, organizations, plots,
  storylines, and trade dress". **Deities are explicitly reserved.**
- Attribution: "This product is original game content and is not based on a licensed game system", then "If you use our
  Licensed Material in your own published work, please credit us in your product as follows: *Pathfinder Player Core* (c) 2023
  Paizo Inc., Authors: ..." That is Paizo's requested credit wording. There is no upstream licensor to add. **Copy the credit
  line verbatim, with the full authors list, from the PDF** (do not retype it from a screenshot).
- Trademarks (Paizo, the golem, Pathfinder) remain Paizo's. The title "Pathfinder Player Core" appears only inside the
  attribution.

**Sources, and why Foundry is not one**
- Foundry's README says game mechanics there are under the **OGL 1.0a**, used "with permission granted as part of the
  partnership agreement between Foundry Gaming LLC and Paizo". `PublicationData` has `title`, `authors`,
  `license: "ORC" | "OGL"`, `remaster: boolean`, so the ORC/OGL split is filterable, but the repo's own terms are not ORC.
  ORC IV ties the irrevocable offer to "publication of Required Notice in a Licensor Work", i.e. the book. Paizo's Community
  Use Policy is free-only, non-commercial, revocable and non-transferable. Archives of Nethys sits under that policy.
  Conclusion (matches `LEGAL.md`): transcribe from the purchased books; use Foundry or AoN only as a cross-check, never as the
  source or in the pack.

**Contracts that are not the ORC**
- Compatibility License 1.0: covers the Compatibility Logos and Pathfinder-Icons font only, for "printed books and electronic
  books". Terminable at Paizo's sole discretion, with an indemnity, Washington law and King County courts. **Do not use or
  accept it.** It confirms naming rules: no "Pathfinder"/"Starfinder" in titles; refer to "Pathfinder Second Edition", never
  "PF2e".
- Paizo Terms of Use (probably the purchase contract; they can change them at any time): personal-use license to Content,
  no reproduction or derivatives "unless otherwise authorized by our Policies" (Policies includes Paizo's "public licenses",
  and Paizo's licenses page discusses the ORC), no AI training, no removing notices, no circumventing access controls.
  Position: the ORC is the irrevocable copyright grant for Licensed Material; the Terms can at most be a contract claim and are
  a managed risk. Keep a **dated snapshot** of the Terms beside the notice.

## 4. Design decisions already taken

- Source: the purchased PDFs, run **locally**; never committed, never uploaded, never fed to an AI for training.
- Deterministic extraction and Reserved Material removal. **No LLM rewriting** of rules text (it can silently change a
  mechanic and is not reproducible; the AxE recommends deleting or renaming proper nouns).
- Deities: dropped. Whether Player Core ties domains and divine font to named deities must be checked in the book. If the cleric
  can choose domains directly, nothing is lost. If not, ship anonymous bundles with no name, flavor or sanctification text,
  or leave the choice free-form. Do not ship a named deity table.
- The system is not named "Pathfinder"; the name appears only inside the attribution. Use "Pathfinder Second Edition" in full
  where compatibility must be stated; avoid "PF2e" in user-facing strings. Write our own non-endorsement line; do not copy the
  Compatibility License's text.
- Distribution: as its own archive, not in `content/systems/bundled.txt`, so it can be withdrawn without a release.

## 5. Conditions for the pack (checklist)

1. Pack folder carries its own license block: ORC, `https://paizo.com/orclicense`, the ORC Notice, the attribution, and a
   statement of how this project wishes to be credited for the adaptation.
2. No Reserved Material: art, characters, deities, locations, organizations, settings, plots, storylines, dialogue, trade
   dress, and proper nouns plus derived adjectives, names and titles. Non-essential flavor text is dropped.
3. No restrictions on recipients: no encryption, license keys, or extra terms on the pack.
4. Code and the app stay PolyForm Noncommercial; mechanics-bearing data stays ORC. Prefer data-only `system.yaml` over a
   compiled plugin (open question below).
5. Only verified-ORC books. Verified: Player Core (notice read). Likely ORC but **not yet verified from the book's notice**:
   GM Core, Monster Core, NPC Core, Player Core 2, Monster Core 2. Read each book's notice before importing from it. Exclude
   legacy OGL books (Core Rulebook 2019, Bestiaries).
6. Provenance on every entry: book, printing (this is the second printing, 2026), chapter and section; page optional (Paizo's
   own reference form avoids page numbers). Record what the importer dropped.
7. No PDF, page images, layout, art or personalized watermark text in the pack or the repo (add to `.gitignore`).

## 6. Suggested first tasks for the local session

1. Read the purchased PDFs' front matter and notices; confirm which books you actually hold and their printings.
2. Spike an importer in `tools/Codex.ContentImport` (per `docs/CONTENT_PACKS.md`, "Importing the SRD"; PdfPig is already a
   dependency) taking a PDF path as an argument. Start with a mechanics-only chapter such as the conditions appendix.
3. Define the entity types in a new system (separate folder under `content/systems/`) and a `manifest.yaml` with the license
   block (fields: name, url, attribution, sourceUrl, modified, notices).
4. Add a smoke test in the style of `tests/Codex.Tests/SrdPackSmokeTests.cs`: counts per type (the book states them) and no
   entry with empty required fields; plus a denylist test that fails on known proper nouns.
5. When the pack exists, replace the "Pathfinder Second Edition Remaster" paragraph in `LEGAL.md` ("What is deliberately not
   included") with the pack's notice, and add a `LEGAL.md` for the pack folder like the other packs.

## 7. Open items for a lawyer (one hour is cheap)

- The Terms of Use vs the ORC grant for purchased PDFs (personal use, no derivatives, no AI training).
- Whether a compiled PF2e plugin (DLL) counts as "mechanics" to give back, or as code.
- Deity handling and the line between flavor and mechanics in feats and spells.
- Whether "compatible with Pathfinder Second Edition" wording is safe without the Compatibility License (AxE p7,
  nominative fair use, is guidance, not a license).

## 8. Facts not verified

- Whether the Paizo PDFs carry a personalized watermark (forum claim only).
- The ORC notices of every book other than Player Core.
- How Player Core ties deities to domains and divine font.
