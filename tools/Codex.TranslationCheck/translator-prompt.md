# Task: translate D&D 5e SRD spells into Russian

Input: `BATCH/input.md` lists spells. Each has `## id:`, `name:`, the exact description between `<<<` and `>>>`, and `property key: value` lines.
Output: write ONE file `BATCH/out/spell/spells.yaml`, a YAML list with one item per spell:

```yaml
- id: <same id>
  name: <Russian name>
  description: |-
    <Russian translation of the description>
  properties:
    castingTime: <translated>
    range: <translated>
    material: <translated, only if the input has it>
    duration: <translated>
    higherLevel: <translated, only if the input has it>
```

## Rules (a script checks them)
1. Translate ONLY `name`, `description` and the properties `castingTime`, `range`, `material`, `duration`, `higherLevel`. Output no other property (not damageType, not level, school, components, classes) and no other field.
2. Keep every number and dice expression exactly (`3d6`, `1d4`); never write dice as `к6`. Translate unit words (feet -> фут/фута/футов).
3. Reproduce the description's layout exactly: same lines in the same order, every blank line between paragraphs kept (a single empty line), the `**Label:**` header lines each ending with TWO trailing spaces as in the input, the italic header line, bullets, table rows, bold markers. Do not merge, split, add or drop paragraphs.
4. Translate the header as e.g. `*2-й уровень, Воплощение (Волшебник)*` and `*Заговор Воплощения (Чародей, Волшебник)*`.
5. Read first: `content/systems/srd5e/system.ru.yaml` (conditions, damage types, schools, attributes, skills) and `docs/i18n-glossary.md`. Use exactly those terms, inflected as Russian needs; one term per concept. Action types: Действие, Бонусное действие, Реакция. Attributes: Сила, Ловкость, Телосложение, Интеллект, Мудрость, Харизма; "Спасбросок Ловкости" style. Force damage: "силовое поле".
6. Style: clear neutral rulebook Russian, "вы", like published Russian 5e books.
7. Valid YAML only: `|-` block scalars for descriptions, quote names containing a colon.

## Check loop (required)
From /Users/michaelyarichuk/RiderProjects/TTRPG.Codex run:
`dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang ru --translation BATCH/out`
Fix every ERROR and every `structure` or `terms` warning for your items, rerun, up to 3 rounds. Never run `dotnet build`. Do not edit anything outside BATCH/out. Final reply: the checker's summary line and the list of any warnings you chose to leave (with the reason), nothing else.
