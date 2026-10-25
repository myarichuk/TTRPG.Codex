# Task: translate D&D 5e SRD character options and rule entries into Hebrew

You translate ONE type per run into a translation pack file. TYPE and the output path come from the caller, e.g. TYPE = `class`,
output `OUT/class/classes.yaml`.

## Source and shape
- English source: the entries of that type in `content/systems/srd5e/packs/srd-5.2.1/` (folders `classes`, `subclasses`, `species`,
  `backgrounds`, `feats`, `conditions`, `rules`; one YAML file per entry or per group). Translate every entry of the type, in id order.
- Output shape: exactly the layout of the Russian pack file for the same type, `content/systems/srd5e/packs/srd-5.2.1-ru/<type>/<file>.yaml`
  (a YAML list; per item `id`, `name`, `description`, and `properties:` with only the translated text properties and `features:` rows
  `name` / `level` / `description` where the English has them). Look at the Russian file for the SHAPE only (which keys, which rows); never translate from it.
- The 69 character-option entries (classes, subclasses, species, backgrounds, feats, conditions) already have Hebrew names in
  `content/systems/srd5e/packs/codex-flavor-he/<type>/*.yaml`: use exactly that name for the entry; do not output `flavor`.
- Row counts must match the English: every feature row translated, in the same order, same `level` numbers; do not drop or merge rows.

## Rules (a script checks them)
1. Translate ONLY `name`, `description`, feature row `name`/`description`, and text properties the Russian file also translates. No other fields.
2. Every number, dice expression, sign and unit stays exactly as in the English: `1d12`, `+2`, `30 feet`. Dice are Latin `8d6`, never `ק6`.
   Do NOT convert units: feet stay `רגל`, pounds stay pounds, no metres/kg.
3. Keep the markdown layout identical: same lines/paragraphs, blank lines, `**Label:**` headers with two trailing spaces where the English has them, bullets, bold/italic markers, table rows.
4. Terminology (binding): `content/systems/srd5e/system.he.yaml` (attributes, skills, conditions, damage types, schools, sizes, class/species labels), `docs/i18n-glossary.md`,
   and the "Decisions (applied)" section of `docs/i18n-he-terms.md`. Examples: זריקת הצלה, תוסף שליטה, שליטה (proficiency), כשרון (feat), מקצוע (class),
   דרגה (level), פעולה / פעולה נוספת / תגובה, נקודות חיים, דרגת שריון, לחשון, תא לחשים, ריכוז, רכיבים, יתרון / חיסרון. One term per concept. Do not browse websites for terms.
5. Voice: rulebook Hebrew addressing the reader in the masculine singular ("אתה"), like the published Hebrew D&D books. Hebrew punctuation (geresh ׳, gershayim ״), no nikud.
   Read your Hebrew once as a native reader: rewrite word-for-word calques idiomatically.
6. Valid YAML only: `|-` block scalars for descriptions; quote names that contain a colon.

## Check loop (required)
From /Users/michaelyarichuk/RiderProjects/TTRPG.Codex run
`dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation OUT`
Fix every ERROR and every `structure` or `terms` warning, rerun, up to 3 rounds. Never run `dotnet build`. Do not edit anything outside OUT.
Final reply: the checker's summary line and any warnings you chose to leave (with the reason), nothing else.
