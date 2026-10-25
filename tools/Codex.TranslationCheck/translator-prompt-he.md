# Task: translate D&D 5e SRD spells into Hebrew

Input: `BATCH/input.md` lists spells. Each has `## id:`, `name:`, the exact description between `<<<` and `>>>`, and `property key: value` lines.
Outputs, two files:

1. `BATCH/out/spell/spells.yaml`, a YAML list with one item per spell (rules text and name):

```yaml
- id: <same id>
  name: <Hebrew name>
  description: |-
    <Hebrew translation of the description>
  properties:
    castingTime: <translated>
    range: <translated>
    material: <translated, only if the input has it>
    duration: <translated>
    higherLevel: <translated, only if the input has it>
```

2. `BATCH/out-flavor/spell/flavor-01.yaml`, only if `BATCH/flavor.md` exists: the original English flavor text of the same spells (id + flavor), translated into Hebrew:

```yaml
- id: <same id>
  flavor: >-
    <Hebrew flavor, one paragraph>
```

Flavor is lore, not rules: keep its voice (dry, concrete, a little wry), keep every image and joke, add nothing, drop nothing, and do not translate word by word.

## Rules (a script checks the first four)
1. Translate ONLY `name`, `description` and the properties `castingTime`, `range`, `material`, `duration`, `higherLevel`. Output no other property (not damageType, not level, school, components, classes) and no other field.
2. Keep every number and dice expression exactly as in the English: `3d6`, `1d4`, `+4`, `30`, `20-foot`. Write dice as Latin `8d6` (never `ק6`, never reversed), signs before the number (`+4`), digits as in the input. Do NOT convert units: feet stay feet (`30 רגל`, `רדיוס של 20 רגל`), pounds stay pounds. Do not use metres or kilograms even if you have seen them in other Hebrew rules sources.
3. Reproduce the description's layout exactly: same lines in the same order, every blank line between paragraphs kept (a single empty line), the `**Label:**` header lines each ending with TWO trailing spaces as in the input, the italic header line, bullets, table rows, bold markers. Do not merge, split, add or drop paragraphs.
4. Translate the header as e.g. `*דרגה 2, הקסמה (כושף, מכשף)*` style: school and class names from `content/systems/srd5e/system.he.yaml` (schools and classes sections), spell level as `דרגה N`, cantrips as `לחשון <school>`.
5. Terminology (binding): read first `content/systems/srd5e/system.he.yaml` (schools, conditions, damage types, attributes, skills, sizes) and `docs/i18n-glossary.md`; use exactly those terms, and the "Decisions (applied)" section of `docs/i18n-he-terms.md`. Key terms: זריקת הצלה (saving throw), דרגת קושי (DC), נזק <סוג> (e.g. נזק אש), מוחץ/דוקר/חותך, כוח קסום (force), פעולה / פעולה נוספת / תגובה, "תא לחשים" for spell slot, לחשון (cantrip), ריכוז (concentration), טקס (ritual), יתרון/חיסרון (advantage/disadvantage), the Prone condition is שרוע. Dice and spell names inside prose stay as translated names. One term per concept; do not browse websites for terms.
6. Voice: rulebook Hebrew addressing the reader in the masculine singular ("אתה", "ביד שלך"), present tense for effects, like the published Hebrew D&D books. Use Hebrew punctuation: geresh `׳` and gershayim `״` for abbreviations (`ד״ק`), not ASCII quotes; ASCII `"` and `'` only where the YAML needs them. Do not use nikud.
7. Wording that went wrong in a pilot (do not repeat): the header label is `**רכיבים:**` (never מרכיבים) with `מילולי, תנועתי, חומרי`; "makes a saving throw" is `מבצע זריקת הצלה` plus the ability with ב (`בזריזות`), never "עושה"; the verb for a beam/ray/bolt is feminine/masculine to match its noun (`קרן ... נורית`); bat guano is `גואנו עטלפים`; the old *colleges* of wizardry are `מועצות` (never קולגות, which means colleagues); Instantaneous = `מיידי`; "creature" = `יצור`, "target" = `יעד`, "within range" = `בטווח`; "Hit Points" = `נקודות חיים`, "temporary hit points" = `נקודות חיים זמניות`; Armor Class = `דרגת שריון`. Read your Hebrew once as a native reader: if a sentence sounds like a word-for-word calque, rewrite it idiomatically.
8. Names: a natural Hebrew name (translated or a loan where players use it, e.g. a proper noun like "Tenser" is transliterated: טנסר). Keep it short, capitalised English names are not left in Latin letters.
9. Also required (a review of a first batch found a cheap model skipping these): creature types from `docs/i18n-he-terms.md` section 8 (שיקוץ, חיה, יצור מרום, יציר כפיים, יסודן, יצור פייה, בן-תופת, דמוי אדם, תפלץ, רפש, צמח, אל-מת); senses אל-ראייה / חוש-רעד / ראיית אמת / ראיית חושך; Difficult Terrain = `תוואי שטח קשה`; cover = `מחסה` (חצי / שלושה רבעים / מלא); Dodge = `חמיקה`, Dash = `מאוץ`, Disengage = `נסיגה`, Help = `עזרה לאחר`; Emanation (the 5.2 area shape) = `הילה` with the size ("הילה של 30 רגל"); Bane = `מארה`; a name must not duplicate another spell's Hebrew name (Confusion = בלבול, so pick another for Befuddlement); materials and gems must be real Hebrew words (agate = אחלמה); no "stat block" calques (`נתוני יצור`). Flavor must never sound garbled: if you cannot render a phrase naturally, restate its image in your own words.
10. Valid YAML only: `|-` block scalars for descriptions, `>-` for flavor, quote names containing a colon or starting with a special character.

## Check loop (required)
From /Users/michaelyarichuk/RiderProjects/TTRPG.Codex run:
`dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation BATCH/out`
and for flavor: `dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation BATCH/out-flavor`
Fix every ERROR and every `structure` or `terms` warning for your items, rerun, up to 3 rounds. Never run `dotnet build`. Do not edit anything outside BATCH/out and BATCH/out-flavor. Final reply: the two checker summary lines and the list of any warnings you chose to leave (with the reason), nothing else.

## Choices already made in earlier batches (stay consistent)
Study action = `פעולת עיון`; Shadowfell = `ארץ הצללים`; Feywild = `ארץ הפייה`; Upper Planes = `המישורים העליונים`; plural undead = `אל-מתים`; spell names already used: Counterspell = נטרול לחש, Dispel Magic = ביטול קסם, Divination = ניבוי (the school is חיזוי), Confusion = בלבול, Befuddlement = שיבוש מוחין, Bane = מארה, Hallow = קידוש, True Resurrection = תחיית אמת, Wish = משאלה. Gems and materials: agate = אחלמה. Before finalising, grep the other `BATCH/../batch-*/out/spell/spells.yaml` files for your names: no two spells may share a Hebrew name.
