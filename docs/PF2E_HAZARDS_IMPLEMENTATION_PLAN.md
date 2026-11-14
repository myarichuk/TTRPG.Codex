# PF2e pack and hazards: implementation plan and delegation

Written 2026-11-14 on `claude/exciting-keller-1af6u9`, after merging main at `a3da1af`: #297 (grants, pools, ranks), #299 (sheet-kind
feature sources and moments), #301 (join review edits), #302 (srd5e grants import, entry choices, free castings), #303 (ru/he drafts).
It plans two tracks: [HAZARDS_DESIGN.md](HAZARDS_DESIGN.md) and the pack surveyed in [ORC_PF2E_IMPORT_SURVEY.md](ORC_PF2E_IMPORT_SURVEY.md).
It also says which model does each step and how Serena is used. Nothing here is built.

## 1. What the merges changed for these tracks

Checked in code on 2026-11-14.

| Survey item | Now |
|---|---|
| Moments (daily preparations, refocus, session start) | **Built.** `moments:` in `system.yaml` (`MomentDefinition`, `SystemMoments`), `MomentCommand`, `recovers.on` and `changesOn` take declared moments. PF2e declares its own; no engine work |
| Features on a sheet kind | **Built**, for `list` sources only: counters, spenders, row choices, keyed choices (`EvaluateKeyedChoices`), modifiers into `SheetModel`. No `field`/`byField` sources, so no class rows gated by level on a kind |
| Entry choices ("one cantrip from a tradition's list") | **Built on `sheetRules` only.** `ResolveEntryChoices` and the check read `SheetRules.Lists`, and skip `OnKind` features |
| Free castings (innate spells) | **Built on `sheetRules` only** (`FeatureAdd.Free`, cost in `EvaluateActions`) |
| Actions panel (`count`, `critical`) | **Built on `sheetRules` only** |
| General ranked lists (Perception, weapon and armor categories, class DC, spell per tradition) | **Still a gap.** Ranks exist for `skills` and `saves` only (`SkillRules.Ranks`, `SaveRules.Ranks`) |
| Per-type penalties (untyped penalties stack, typed take the worst) | **Still a gap.** `ModifierTypesDefinition.Penalties` is one rule for all types |
| Valued conditions (frightened 2) | **Still a gap.** Only `ExhaustionRules` has a level |
| Boosts, MAP, cooldowns, server-side check rolls, item rune slots, linked companion sheets | **Still gaps** |

**Engine path: the hybrid still stands, for a new reason.** Going kinds-only is possible now, but a kind has no ranks, no entry choices, no
free castings, no actions and no slot pools, which is what a PF2e character is built from. So `sheetRules` builds the character and one
sheet kind carries the play-time numbers (four degrees, valued conditions as tracks, condition effects). No system declares both today.
On that path a kind's features are collected by the instance `CollectFeatures` after the `sheetRules` sources, and also by `SheetModel`
through the static collector. Their modifiers go only to the model (`!OnKind` filter), but nothing tests that. **The pf2 reference
fixture (P1) is therefore the gate for every PF2e engine step.**

## 2. Step 0, before anything is delegated

| # | Step | Who |
|---|---|---|
| 0a | Green baseline on the merged branch: `dotnet build`, unit tests without E2E and print. Clean orphaned RavenDB test data first if anything flakes | Opus (this session) |
| 0b | **Serena canary.** `activate_project` now takes a `session` id. That may mean activation is per session, but on 2026-11-13 edits from parallel agents landed in the wrong worktree. Test: two Haiku subagents in two worktrees, each calls `initial_instructions` for its own session id, activates its own worktree path, makes a one-line edit to a scratch file, and reports the absolute path the edit landed in. Run them at the same time, twice | Opus runs it, Haiku agents |
| 0c | Push the branch and open a PR for the two docs and this plan, so tracks branch from a merged main | User approves the push |

If 0b fails, the rule stays as it is: **one Serena writer at a time across all worktrees**. Parallel then means read-only agents beside a
single writer, or writers in one worktree on disjoint files.

## 3. Track H: hazards (srd5e first)

From [HAZARDS_DESIGN.md](HAZARDS_DESIGN.md) section 9. It works on srd5e alone and needs nothing from track P.

| # | Step | Files (main ones) | Who |
|---|---|---|---|
| H1 | `HazardDefinition` on `ContentTypeDefinition`, `hazards.scaling` on `SystemDefinition`, loader checks, schema docs | `SystemDefinition.cs`, `SystemDefinitionLoader.cs`, `docs/schema/system.schema.json`, `SYSTEM_YAML_REFERENCE.md` | Sonnet; Opus reviews the type shape |
| H2 | srd5e `hazard` type and scaling from the SRD traps; a PDF step in `Srd5eImporter` for the 8 traps; 2 homebrew hazards; golden updated | `content/systems/srd5e/system.yaml`, `tools/Codex.ContentImport/*`, golden | Sonnet (rules judgment, not Haiku) |
| H3 | `PlaceFeature.Hazard` with state; place page link and picker | `Codex.Persistence/Models.cs`, place pages | Sonnet |
| H4a | Server-side check service: skill, save, passive or AC from an actor's evaluation, rolled with `dice.check`; a typed bonus for printed creatures | new `Codex.Persistence/Runtime/CheckService.cs`, `CampaignRuntime.cs` | **Opus designs the interface, Sonnet builds, Opus reviews** |
| H4b | `HazardCommand` (notice, disable, trigger) on H4a; live state in `EncounterDocument.ActiveEffects`; Hazards panel; log | `RuntimeCommands.cs`, `CombatConsole.razor`, new `HazardsPanel.razor` | Sonnet; Opus reviews |
| H5 | Complex hazards in initiative: registration gate, `ToActorBlueprint` overload, `QuickAddPanel` split | `YamlContentPackLoader.cs`, `QuickAddPanel.razor` | Sonnet |
| H6 | World builder: proposal link, homebrew hazards, scaling check, prompt line | `Codex.Core/AI/*`, world builder services | Sonnet |
| H7 | Homebrew wizard hazard mode; book stat block | wizard and book components | Sonnet |
| H8 | Strings en/ru/he; accessibility pass | `Strings.*.resx` | Haiku for strings; Sonnet for accessibility |

H1 to H4b, in order, is the first shippable piece. H5 to H7 can follow in any order, but each touches different files, so they run one
writer at a time unless 0b passes.

## 4. Track P: the PF2e pack

The engine steps use our own numbers and invented entries only. The PDFs are not touched until P8.

| # | Step | Files (main ones) | Who |
|---|---|---|---|
| P1 | **Reference fixture** `tests/Codex.Tests/Fixtures/reference/pf2/`: `system.yaml` (hybrid: `sheetRules` plus one kind), a tiny pack, and `spec.yaml` in the harness's shape (pass, fail, unsupported, `requires`). Expectations: ranks worth level + 2/4/6/8; four degrees with the natural 20/1 shift; typed bonuses; untyped penalties stacking while typed take the worst; frightened 2 lowering checks and DCs; HP from ancestry + class × level; AC with an armor rank; MAP as a roll input; a kind feature's modifier counted once on the hybrid. Unsupported ones name the P step meant to make them pass | fixture folder, `progress.txt` | **Opus writes the spec** (it is the design); Sonnet fills the pack |
| P2 | Per-type penalty rule (`penalties` per type, untyped exempt) | `SheetKinds.cs` (`ModifierTypesDefinition`), `ModifierStacking.cs` | Sonnet; Opus reviews |
| P3 | General ranked lists (a rank set per key list, reachable by grants and choices like `skills`) | `SheetRules.cs`, `SheetRuleEngine.Ranks.cs`, `FeatureGrantCheck.cs` | **Opus designs the diff**, Sonnet builds; the srd5e golden must stay byte-identical |
| P4 | Valued conditions (first-class level, read by `conditionEffects` amounts) | `SystemDefinition.cs`, `SheetModel.cs`, condition picker, `TableRules.cs` | Opus designs; Sonnet builds. **After H1** (both edit `SystemDefinition.cs`) |
| P5 | Boost rules (free boosts, flaws, four different, partial boost above +4) | `SheetRules.cs` (`AttributeRules`), `SheetRuleEngine.cs` | Sonnet; Opus reviews. After P3 (same files) |
| P6 | Degree function and MAP as a roll input on the hybrid; the Actions panel passes attack number | `SheetRolls.cs`, `ActionsPanel.razor` | Sonnet |
| P7 | The system skeleton under `content/systems/` with homebrew-only content: ORC licence block in its manifest, no "Pathfinder" in the name, `moments:` (daily preparations, refocus, session start), the faith shape with no names | new system folder | Sonnet; Opus reviews the licence block |
| P8 | `pf2` importer command, local only: bookmarks as the spine, font histogram, tables by word boxes, sidebars split out. The PDFs are gitignored and never read by CI | `tools/Codex.ContentImport/Pf2/*`, `.gitignore` | Sonnet, one book chapter per run; **Opus reviews every generated diff** |
| P9 | Redaction table (phrase-level, fails on mismatch) and the denylist test from the glossary allowlist | `tools/Codex.ContentImport/Pf2/Redactions.cs`, a test | Sonnet builds; **Opus reviews the table itself** |
| P10 | Homebrew until GM Core: about 12 magic items, fundamental runes, generic property runes (flaming, icy). Each marked homebrew in the library | pack folder | Sonnet; Opus checks none is copied from GM Core |
| P11 | Companions and familiars as their own sheet kind | system yaml, sheet kind | Opus designs (linked sheets are new); later |
| P12 | Hazards on the pack: `notice.rank`, `disable.successes`, `critFail`, degrees; the pack's homebrew hazards | pack, hazard UI | Sonnet. After H4b and P6 |

Order: P1 first. P2 and P3 can run in parallel with each other only if 0b passes (disjoint files). P4 waits for H1. P5 after P3. P7 can
start after P1. P8 and P9 go together, after P7. P10 any time after P7.

## 5. What runs at the same time

| Pair | Safe? | Why |
|---|---|---|
| H1 to H4b, and P1 | Yes | Disjoint files: P1 is a fixture folder and `progress.txt` |
| H1, and P2 or P3 | Yes | `SystemDefinition.cs` against `SheetKinds.cs`/`ModifierStacking.cs` or `SheetRules.cs`/`Ranks.cs` |
| H1, and P4 | **No** | Both edit `SystemDefinition.cs` and its loader |
| P3, and P5 | **No** | Both edit `SheetRules.cs` and the engine's main file |
| H4b, and P6 | Check first | Both may touch the Actions panel; list the files at the start of the later one |

"Safe" assumes 0b passed. Otherwise only one track writes at a time, and the other is limited to read-only work (sweeps, spec drafting).

## 6. How work is delegated

**Sessions or subagents.** Each track is its own session in its own worktree, because each ends in its own PR (H1 to H4b is one PR, P1 to
P3 another). Inside a session, a step is a subagent, or the session's own work. The step's result comes back before the next step starts.

**Models.** This follows the "Who" column of `ENTRY_CHOICES_DESIGN.md` and `SHEETKINDS_FEATURES_DESIGN.md`:
- **Opus:** engine shape and review. That means:
  - the P1 spec;
  - the P3 and P4 diff design;
  - the H4a interface;
  - review of every engine diff, the licence block and the redaction table;
  - review of every importer diff before it is committed.
- **Sonnet:** implementation, tests, importer passes, homebrew content, rules judgment.
- **Haiku:**
  - read-only sweeps, for example "every reader of `Conditions`" before P4, or "every writer of `PlaceFeature`" before H3;
  - en/ru/he strings from an English list;
  - never PDF work, rules judgment or engine edits.

**Review gate.** An Opus reviewer gets the diff and the design section, not the implementer's summary. A step is done when its tests and
the golden pass, and the reviewer found nothing it would block on.

**Every brief starts with these lines:**

```
FIRST, AND THROUGHOUT: use Serena for both reading and writing code and files (.cs, .razor, .resx, .yaml, .css, .md).
Bash only for dotnet build and dotnet test. Call initial_instructions, then activate_project with YOUR worktree's absolute path
and your own session id. After every edit, check the path in the result is inside your worktree. Say in your report that you used Serena.
```

The brief then names:
- the design section;
- the files it may touch;
- the files it must not touch;
- what done looks like (tests and goldens).

**Briefs that touch the PDFs (P8, P9) add:**
- Never commit, upload or paste PDF text, statblocks or tables into the repo, a PR or a report. Only generated pack yaml goes in, after
  the redaction table and the denylist test pass.
- No deity names, or any other Reserved Material. Provenance is by chapter and section only, never a page quote.
- The PDFs stay outside the repo and are gitignored.
- Nothing personal about the purchase goes in any file or report.

## 7. Not decided yet

1. Whether track P waits for the hazards PR, or starts P1 at once beside H1 (recommended: at once; section 5 says it is safe).
2. The outcome of 0b decides whether H and P write at the same time.
3. P11 companions, and GM Core when it is bought, each get their own design.
