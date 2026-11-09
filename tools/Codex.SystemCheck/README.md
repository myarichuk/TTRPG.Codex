# Codex.SystemCheck

Checks a data-driven system the way the server loads it, for the person writing one.

```bash
dotnet run --project tools/Codex.SystemCheck -- path/to/my-system --types
```

- Loads every `system.yaml` under the folder, with its scripts and packs, and prints what failed to load (`FAILED`) and every
  problem the loader reports (`PROBLEM`): unknown keys, handlers, rolls, layouts, campaign objects, scripts that are missing,
  do not parse, or do not export what `system.yaml` names.
- When the folder has a `spec.yaml`, runs each expectation and prints `PASS`, `FAIL` (with what came out instead) or `CANNOT`
  (something this version does not do). The format is the reference fixtures'
  ([tests/Codex.Tests/Fixtures/reference/README.md](../../tests/Codex.Tests/Fixtures/reference/README.md)).
- `--types` writes `codex.d.ts` beside `system.yaml`: TypeScript declarations of what a script is handed and may return, with
  the system's own track, list, function, roll and event names. A script stays JavaScript and opts in with `// @ts-check` and
  JSDoc (`/** @param {import("./codex").Sheet} sheet */`); the street fixture's `rules.js` shows how. Write it again when
  `system.yaml` changes.
- `--quiet` prints only what is wrong.

Exit code 0 when the system loads and no expectation fails, 1 otherwise, 2 for a wrong command line. Loader problems alone do
not fail the check, since the server loads such a system too.
