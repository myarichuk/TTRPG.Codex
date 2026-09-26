# Adding a Game System (and: How We'd Do Star Wars FFG)

This answers "what if I add Star Wars FFG or some other system - how flexible is what we
have now?" Short answer: a new system is a thin C# shell plus YAML data. The Pf2e plugin
(`src/Codex.Systems.Pf2e/Pf2ePlugin.cs`, ~67 lines) is the template - every rule lives in a
content pack, never in C#.

## The Seams (What Each Layer Gives You for Free)

| Seam | Location | What a new system gets |
|---|---|---|
| System shell | `ICodexSystemPlugin` | `SystemId`, component registration, sheet schemas (`UISchema`), optional dice roller |
| Content packs | `packs/<id>/` + `YamlContentPackLoader` | versioned YAML datasets; abilities (`spells/`, `feats/`) + rules entries (`classes/`, `ancestries/`, `equipment`, ...) with priority override |
| Rules entries | `IRulesEntryDefinition`, `GetRulesEntries(systemId, kind?)` | free-form `kind` string - new terms need no core-type changes |
| Execution | TRCE effects + `BuiltInHandlers` | saves, attacks, areas, damage already executable; new effect types register a handler |
| Dice | `IDiceRoller`, resolved per campaign (`CampaignRuntimeManager`) | default polyhedral roller; override for exotic dice |
| Persistence | `CampaignDocument.SystemId` + components | campaigns, actors, sessions are system-agnostic; components snapshot to documents |
| Creation UX | `CreateCharacter.razor` steps | step list + term queries per system (see below) |
| i18n | `IStringLocalizer` + `Resources/` | new pages/keys follow the wizard's pattern; Hebrew RTL already handled |

## Worked Example: Star Wars FFG

FFG is the stress case: narrative symbol dice, no classes/levels, talent trees, obligation.
Walking it through each seam:

1. **Shell** - new `Codex.Systems.Swffg` project with `SystemId => "Swffg"`, a strain/wounds
   component (reuse `ResourcePoolComponent`), and `UISchema`s for characteristics/career.
   Copy `Pf2ePlugin`; expect < 150 lines.
2. **Narrative dice** - implement `IDiceRoller` parsing pool expressions (e.g. `2a 1p 1d`
   for 2 ability + proficiency + difficulty) and returning symbol counts
   (success/advantage/triumph/despair) in `DiceRollResult`. `GetDiceRoller()` returns it;
   the campaign runtime picks it up automatically. This is exactly the case the seam
   exists for (see the doc comment on `ICodexSystemPlugin.GetDiceRoller`).
3. **Data as YAML** - new pack `packs/swffg-core/` with folders like `species/`,
   `careers/`, `specializations/`, `talents/`, `equipment/`. Two loader additions in
   `YamlContentPackLoader` (folder -> kind mapping exists in two places: bulk load and
   single-file load) register the new kinds; `GetRulesEntries("Swffg", "talent")` then
   just works. No core model changes - `kind` is a string and `properties` a bag.
4. **Talent-tree mechanics** - static tree layout is plain YAML (`properties:
   {row, column, links}`). Ranked-talent effects reuse TRCE (`bonus`/`heal`-style
   effects); genuinely new mechanics (e.g. "upgrade next check") register one handler in
   the effect-handler map.
5. **Creation wizard** - add a third branch next to `Is5e`/Pf2e: species -> career ->
   specialization -> obligation/duty -> XP spend -> equipment. The step engine
   (`WizardStep` list + `VisibleSteps`) is data-driven; only genuinely FFG-shaped steps
   (talent-tree picker) need new markup.
6. **Importer** - `scripts/import_swffg.py` following `import_pf2e.py`'s shape. See the
   legal note below before writing one.

## Legal Posture (Read Before Importing FFG)

- SRD 5.1 is CC-BY-4.0 and PF2e Remastered is under the ORC - both allow mechanics + names
  with attribution (see `src/Codex.Systems.DnD5e/LEGAL.md` and `docs/RULES_CONTENT.md`).
  Our importers additionally keep to mechanics-only data, no prose.
- **FFG Star Wars has no open license.** There is no legal bulk source to import. An FFG
  pack must be hand/sourced-community data in our own words (mechanics + names only, no
  FFG prose), or user-supplied. Do not scrape FFG books/Adversaries PDFs into YAML.

## Checklist for Any New System

- [ ] Shell project + `SystemId` + solution wiring + `LEGAL.md`
- [ ] Pack `packs/<id>/` + `manifest.json` + importer script (mechanics-only)
- [ ] Loader folder mappings (both load paths) for any new term kinds
- [ ] `IDiceRoller` only if dice aren't polyhedral
- [ ] Effect handlers only for mechanics TRCE can't express
- [ ] Creation-wizard branch + e2e walkthrough (see `CharacterCreationTests`)
- [ ] `.he.resx` coverage for new UI strings (`LocalizationCoverageTests` enforces it)
- [ ] Pack content tests (counts + known-good values, see `RulesPackContentTests`)

## Honest Limits

- The creation wizard branches per system (`Is5e`/Pf2e `if`s). A third system is fine;
  a sixth should graduate to per-system step providers.
- TRCE effects cover d20-style resolution well; deeply procedural subsystems (FFG
  starship combat, crafting) stay manual (dice + notes) until someone models them.
