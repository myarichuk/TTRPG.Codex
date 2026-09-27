# Rules Content: SRD 5.1 + PF2e Remastered Packs

How the full rules datasets get into Codex as YAML, what the schema means,
what executes today vs. what is reference-only, and the license posture.
(6.1: full SRD/ORC rules data.)

## Packs

| Pack | System | Contents | Source |
|---|---|---|---|
| `packs/srd51-full` | DnD5e | 319 spells + 334 monsters + 24 classes + 13 ancestries + 237 equipment, generated | SRD 5.1 via dnd5eapi, CC-BY-4.0 |
| `packs/pf2e-remastered` | Pf2e | 373 spells + 1807 feats + 16 classes + 10 ancestries + 100 heritages + 56 backgrounds + 7 patrons + 551 equipment, generated | Player Core / Player Core 2 via Archives of Nethys, ORC |

Packs carry priorities (`srd51-full`: 10), so a homebrew pack with a higher
priority deterministically overrides shared ids - pinned by `HomebrewOverrideTests`,
which builds its override pack inline in a temp dir rather than checking one in.

## Regenerating

```bash
python3 scripts/import_srd51.py        # needs network: www.dnd5eapi.co
python3 scripts/import_pf2e.py        # needs network: elasticsearch.aonprd.com
python3 scripts/import_srd51.py --limit 5 --output /tmp/smoke --no-install  # smoke test
```

Both scripts are stdlib-only (no pip install). Each writes `packs/<id>/`
(`manifest.json`, `ATTRIBUTION.md`, one YAML file per entry) and mirrors it to
`plugins/<id>/`, the directory the dev-time `PluginLoader` reads. Pass
`--no-install` to skip the mirror. Any fetch or mapping failure exits nonzero
with the offending entry ids - a partial pack is never written silently.

Packs are generated, not checked in: a fresh clone has no `packs/` directory,
so CI regenerates both before testing and the release workflow regenerates both
before packaging (the Docker image regenerates them in-build too).

## Schema

Spells and feats are abilities (`spells/`, `feats/` subfolders load into the
ability registry; `metadata.kind` is `spell` or `feat`). Monsters are actors.
Every generated file is a one-item YAML list with a source header comment.

**5e spell** (`acid-arrow.yaml` shape): `triggers: [{type: Cast}]`,
`requires`/`costs` on `Slots{level}` for levelled spells, plus `metadata`
with `level`, `school`, `classes`, `castingTime`, `range`, `area`, `components`,
`material`, `ritual`, `concentration`, `duration`, `attackType`, `save`,
`damageAtSlotLevel` / `damageAtCharacterLevel`, `healAtSlotLevel`.

**5e monster** (`goblin.yaml` shape): `tags`, `resources` (`HP`), `properties`
with `armorClass`, `hitDice`, `speed`, six scores, `savingThrows`, `skills`,
`senses`, `languages`, `challengeRating`, `xp`, `damage*` lists,
`specialAbilities`, `actions` / `legendaryActions` / `reactions` (each with
`attackBonus`, `damage` dice, `save`, `usage` where the API provides them).

**PF2e spell**: metadata-only (`rank`, `traditions`, `traits`, `classes`,
`actions`, `components`, `range`, `area`, `savingThrow`, `school`, `heighten*`,
`sourceBook`). No TRCE: AoN's structured fields carry no damage/effect
formulas. The engine itself can now express saves, attacks, and areas (see
"What executes today"), so a future importer that sources PF2e damage formulas
gets execution without engine work.

**PF2e feat**: metadata-only (`level`, `traits`, `classes`, `featType`,
`actions`, `prerequisites`, `frequency`, `trigger`, `cost`, `sourceBook`).
Prerequisite/frequency/trigger/cost *names* are regex-extracted from the entry
text at import time; the text itself is never copied.

## What executes today

- Single-damage 5e spells → `Damage` effect with base-slot dice (63 spells).
- 5e healing → `Heal` effect (10 spells); flat amounts (aid) ride `amount`.
- Saves halve or negate (`Requires` save gates); attack rolls gate damage
  (`acid-arrow` shape); areas fan one shared damage roll out to every target.
- Multi-damage-type spells (ice storm) and everything PF2e: metadata-only,
  deliberately - half-expressing them would silently drop mechanics.

Engine gaps for later slices: durations/concentration, upcasting, heightened
ranks, class/level prerequisites.

## Importer validation

Regeneration fails loudly rather than writing holes: unmapped equipment-option
shapes raise, and any `?` placeholder left anywhere in class properties fails
`validate_mapping` (the monk's nested tool-proficiency choice and the cleric's
crossbow bundle both regressed this way once). `RulesPackContentTests` re-pins
the same guarantees on the checked-in YAML: every class equipment option is
wizard-renderable and no `?` leaks through.

## License posture: mechanics-only

Generated YAML carries game facts (names, numbers, enums, short factual
labels) and no copyrightable expression. Importers drop `desc`,
`higher_level`, `text`, `summary`, and `markdown` at the mapping boundary:

- 5e scope is SRD 5.1, which dnd5eapi mirrors in full (CC-BY-4.0, attributed
  per file header + manifest + `ATTRIBUTION.md`).
- PF2e scope is ORC-licensed Remastered core only: the AoN query filters to
  `primary_source` Player Core / Player Core 2 + common rarity, so legacy OGL
  content (Core Rulebook, APG, ...) cannot leak in.
- `RulesPackContentTests` asserts the guard on every entry: `Description`
  null, no banned metadata keys.

## PF2e system shell

`src/Codex.Systems.Pf2e` is intentionally thin: `SystemId`, shared core
components, and Actor/Ability sheet schemas. All rules are YAML in
`pf2e-remastered`. Like DnD5e, the web project never references it (5.2c);
only the test project does, for discovery tests.

Adding another system (Star Wars FFG or otherwise) follows the same shape -
see `docs/ADDING_A_SYSTEM.md` for the seams, the worked FFG example, and the
legal caveats.
