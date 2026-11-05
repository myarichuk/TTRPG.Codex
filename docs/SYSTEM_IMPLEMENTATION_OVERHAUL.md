# System implementation overhaul

Status: proposal (2026-11-01). Nothing here is built yet.

## Why

A game system today is a `system.yaml` (plus content packs), wrapped by `DataDrivenSystemPlugin`; a compiled
`ICodexSystemPlugin` DLL is the only way past what the YAML can say. The SRD 5e ruleset runs as about 500 lines of YAML,
so the data model is strong. It is not yet "drag and drop":

1. **No in-app install.** The Library imports content-pack zips; a system can only be copied into the "My content"
   folder on the server machine.
2. **Restart required.** `PluginLoader.LoadAndInitializeAsync` returns early once loaded; no reload, no watcher.
3. **Errors are invisible.** `SystemDefinitionLoader` validates well, but `PluginLoader.Warn` has no callers and
   `LoadException` no reader (review R13): a broken system just isn't there.
4. **The vocabulary is d20-shaped.**
   - Attribute modifiers are `none` or `half-minus-five` only; formulas get a built-in `mod()` meaning `(score - 10) / 2`.
   - Many keys default to 5e names (`str`, `dex`, `con`, `proficiencyBonus`, `hitDie`, `spellcastingAbility`).
   - `ArmorClass`, `Spellcasting`, `Proficiencies`, `SlotPools`, `Multiclass` and `DeathSaves` are 5e concepts made
     configurable.
   - The dice grammar has `NdM±k`, keep-highest/lowest and `adv`/`dis`, but no success counting, exploding dice,
     roll-under or result bands.
   - `DiceResult.razor` detects crits by finding "d20" in the expression; `SpellCard` and `SchemaForm` look for fields
     named `level` and `classes`.
   - Result: Blades in the Dark, PbtA, Call of Cthulhu and dice-pool games can describe their content but not their
     core roll, except through a DLL.
5. **DLLs are fully trusted.** Every `*.dll` under a content root is `Assembly.LoadFrom`'d, with no sandbox, signature
   or statement in the docs.
6. **No author's reference** for `system.yaml` (`CONTENT_PACKS.md` covers packs only).
7. **No versioning.** A campaign stores a system id only; a changed or removed system silently mismatches every sheet,
   and `EnsureKnownSystem` refuses every save once the id is gone.

## The idea: one set of rule hooks, three suppliers

The sheet engine, the table, the AI features and the tests call a single set of hooks. `system.yaml` stays where a
system is described, and names, hook by hook, who answers it.

```csharp
// Codex.Plugin.Abstractions
public interface IRuleHooks
{
    RollResult? Roll(RollRequest request, IDiceSource dice);            // null: the built-in roller
    int? Modifier(string attribute, int score);
    IReadOnlyDictionary<string, object>? Derive(SheetView sheet);
    IReadOnlyList<RuleViolation> Validate(SheetView sheet);
    VitalsChange? OnDamage(VitalsView vitals, int amount, string? damageType);
}
```

```yaml
rules:
  modifier: half-minus-five                                   # built-in
  roll:     { script: rules.js#rollCheck }                    # Jint
  derive:   { type: "Acme.Shadowrun.Derive, Acme.Shadowrun" } # compiled
  # validate / onDamage omitted: built-in behaviour
```

| Supplier | What it is | Trust | Installed by |
|---|---|---|---|
| Built-in | today's `SheetRuleEngine` / `TableRules`, as the default `IRuleHooks` | none needed | ships with the app |
| Script | a JS function run by Jint (`ScriptRuleHooks`) | sandboxed | GM or admin, in the app |
| Compiled | a type in a DLL implementing the hook | full | server owner, by copying files on the server machine |

### Why this shape

- **One contract.** Callers never know which supplier answered.
- **Mix per hook.** A homebrew can be 95% YAML with one scripted roll; a large system can ship a DLL for derived values
  and keep YAML for everything else.
- **A gradual path.** An author starts in YAML, moves one rule to JS when YAML runs out, and to C# only for speed or size.
  Nothing else in the file changes.
- **One test suite.** The same contract tests run against all three suppliers ("dice add up to the total", "derived
  values are deterministic", "violations name a field").

## Rules of the contract

1. **Plain data in, plain data out, for every supplier.** Hooks get read-only views (`SheetView`, `VitalsView`,
   `RollRequest`) and return results. No live table objects, no ECS `World`, no repositories, for DLLs as well as scripts.
   The host checks the result and applies it through the existing runtime commands, so undo, the session log and secrecy
   keep working.
2. **One supplier per hook, named in the YAML.** No hidden overrides (a DLL silently replacing a script). Absent means
   built-in; that is the only fallback.
3. **The host owns randomness.** Every supplier rolls through `IDiceSource` (`ctx.d(6)` in JS, never `Math.random`), so
   tests can seed it and the log records the dice actually rolled.
4. **The same failure handling.** A timeout, exception or invalid result produces a GM-facing message naming system,
   hook, supplier and (for scripts) line, and falls back to the built-in behaviour. Jint enforces time and memory limits;
   DLL calls are timed and slow ones logged.
5. **Install rights.**
   - YAML and scripts can be uploaded in the app by a GM or admin. A server setting can turn scripts off.
   - DLLs are never uploaded; they are installed only by copying them in on the server machine.
   - The Library labels a system that uses one "contains compiled code".
6. **Versioned.** A campaign records the system version and a hash of the YAML, each script and each DLL it uses, and
   says so when any of them changes.

## Script supplier (Jint)

- **No .NET access.** `AllowClr` stays off.
- **Limits on every call:** `TimeoutInterval`, `MaxStatements`, `LimitMemory`, `LimitRecursion` and a cancellation
  token, all set low.
- **Parse once, run on cheap engines.** Each script is parsed once (`Engine.PrepareScript`) and run on a cheap engine
  per call or a small pool per system, because a Jint engine is not thread-safe.
- **Cache results.** Hook results are cached like formula results (see review R7), never called per render.
- **Keep DynamicExpresso.** It stays for one-line `derived` formulas. Jint is for rules that need loops, tables or
  branches. The existing formulas are not rewritten.
- **A "try this hook" panel** with a sample sheet, so non-programmers can test a script before using it.

## Compiled supplier (DLL)

- **Same loader.** Uses the existing `PluginLoader` assembly loading. The named type is looked up and checked against
  the hook it claims; a mismatch is a load error shown in the Library.
- **Whole plugins keep working.** A whole `ICodexSystemPlugin` stays supported for behaviour beyond the hooks (custom
  components, a narrative dice roller). New work should prefer hooks.
- **Document the trust boundary.** The docs say plainly that a DLL is code running with the server's rights
  (closes R13's documentation item).

## Alongside the hooks

- **Load errors in the Library.** A system or pack that failed to load is listed with the validator's messages (R13).
- **Author's reference and JSON schema.** A `system.yaml` reference and a JSON schema generated from
  `SystemDefinition`, so editors autocomplete and validate.
- **Install and reload from the app.**
  - Upload a system zip, validate it, write it to the content folder and reload.
  - Reloading needs a reloadable `PluginLoader`; live tables keep the system they started with until they end.
- **Less 5e in the defaults.**
  - Require the keys that now default to 5e names.
  - Crit/fumble marking comes from the dice section (or the roll hook's `outcome`), not from finding "d20" in text.
  - Spell and level display becomes configurable.
- **Removing a system.** Refuse to remove a system that campaigns still use, or open those campaigns read-only with
  an explanation.

## Order

1. **Built-in supplier.** Define `IRuleHooks` and its views; wrap today's behaviour as the built-in supplier. No change
   in results; the existing tests prove it. This is the bulk of the work: extracting the hooks from `SheetRuleEngine`
   (about 1,500 lines) and `TableRules`.
2. **Errors.** Load errors in the Library (can land before step 1; it is part of remediation Phase 3).
3. **`rules:` section.** Add it to `system.yaml` and pick a supplier per hook.
4. **Jint.** Add the script supplier, `roll` and `modifier` hooks only. These two cover the core mechanic of PbtA,
   Blades, Call of Cthulhu and dice pools.
5. **DLLs.** Add the compiled supplier for the same two hooks; contract tests across all three.
6. **Install and version.** In-app system install with reload; campaign system versioning.
7. **The rest.** `derive`, `validate` and `onDamage` hooks when a real system needs them; reference docs and JSON schema
   alongside.

## Open questions

- Should a GM (not only a server admin) be able to install a script system on an internet-facing server? The proposal
  says yes, with low limits and a server switch; confirm.
- Does a hook see other actors (for example, the roll hook wanting the target's defence)? Starting position: no, only
  the roller's sheet and the request; add a narrow `target` view if a real system needs it.
- Do scripts get to localize their own messages (violations, roll outcomes)? Likely via keys resolved against the
  system's translation files.
