# Task: translate D&D 5e SRD creatures (stat blocks) into Hebrew

Input: `BATCH/input.md` lists creatures. Each has `## id:`, `name:`, the exact stat block between `<<<` and `>>>`, and `property key: value` lines.
`BATCH/flavor.md` (if present) has the original English lore of the same creatures (`flavor`, `habitat`, `behavior`).
Outputs, two files:

1. `BATCH/out/monster/monsters.yaml`, a YAML list, one item per creature (rules text and name):

```yaml
- id: <same id>
  name: <Hebrew name>
  description: |-
    <Hebrew translation of the whole stat block>
  properties:
    speed: <translated>
    skills: <translated, only if the input has it>
    senses: <translated>
    languages: <translated>
    savingThrows: <translated, only if the input has it>
    gear: <translated, only if the input has it>
```

2. `BATCH/out-flavor/monster/flavor-01.yaml`, only if `BATCH/flavor.md` exists: the English lore of the same creatures translated into Hebrew:

```yaml
- id: <same id>
  flavor: >-
    <Hebrew flavor, one paragraph>
  habitat: >-
    <only if the English has it>
  behavior: >-
    <only if the English has it>
```

Flavor is lore, not rules: keep its voice (dry, concrete, a little wry), keep every image and joke, add nothing, drop nothing, do not translate word by word.

## Rules (a script checks the first four)
1. Translate ONLY `name`, `description` and the properties `speed`, `skills`, `senses`, `languages`, `savingThrows`, `gear` (only those the input lists). Never output size, creatureType, alignment, hitDice, challengeRating or any other property or field.
2. Every number and dice expression stays exactly as in the English: `2d6 + 5`, `+9`, `DC 14`→the number 14, `5900`. Write dice as Latin `8d6`, signs before the number (`+9`, `-1`), digits as in the input, no thousands commas. Do NOT convert units: feet stay feet (`10 רגל`), miles stay miles, pounds stay pounds. Do not use metres or kilograms even if you have seen them in other Hebrew sources.
3. Reproduce the layout exactly: the same lines in the same order, blank lines, trailing double spaces on the `**Label** value  ` lines, `###` headings, the ability table (same columns, same cells; translate only the header abbreviations), bullets, bold/italic markers. Do not merge, split, add or drop paragraphs, traits or actions: every trait/action/reaction appears, in the same order.
4. Terminology (binding): `content/systems/srd5e/system.he.yaml` (attributes and their abbreviations, skills, conditions, damage types, sizes, alignments), `docs/i18n-glossary.md`, the "Decisions (applied)" and "Decisions (stat blocks, items)" sections of `docs/i18n-he-terms.md`, section 8 (creature types, senses). Language names: use exactly the Hebrew names in `content/systems/srd5e/packs/srd-5.2.1-he/rule/rules.yaml` (entries about languages). Spell names mentioned in prose use the Hebrew name from `content/systems/srd5e/packs/srd-5.2.1-he/spell/spells.yaml` (grep the id's `name:`), never a fresh invention. One term per concept. Do not browse websites.
5. Voice: rulebook Hebrew, present tense for effects, the reader is "אתה"; creature actions are in third person ("היצור"/the creature's name used as in the English: "האבולת"). Hebrew punctuation (geresh ׳, gershayim ״), no nikud. Read your Hebrew once as a native reader: rewrite word-for-word calques idiomatically.
6. Names: a natural Hebrew name (translated, or a loan/transliteration where players use it: טרול, גובלין, פיקסי; proper names transliterated). No Latin letters in a name. No two creatures in the whole pack may share a Hebrew name: before finalising, grep `BATCH/../batch-*/out/monster/monsters.yaml` and `content/systems/srd5e/packs/srd-5.2.1-he/monster/` for your names.
7. Valid YAML only: `|-` block scalars for descriptions, `>-` for flavor, quote names containing a colon or starting with a special character; `speed`/`senses` values that contain a colon or start with a special character must be quoted.

## Stat-block vocabulary (binding)
- Header line `*Large Aberration, Lawful Evil*` → `*שיקוץ גדול, רע וחוקי*` (noun then size; alignment words from system.he.yaml; "Humanoid (Any Race)" → `דמוי אדם (כל גזע)`; "Dragon (Chromatic)" → `דרקון (צבעוני)`, `(Metallic)` → `(מתכתי)`).
- `**AC** 17` → `**דרגת שריון** 17`; `**HP** 150 (20d10 + 40)` → `**נקודות חיים** 150 (20d10 + 40)`; `**Speed**` → `**מהירות**`; `**Initiative** +14 (24)` → `**יוזמה** +14 (24)`; `**CR** 10 (XP 5900; PB +4)` → `**אתגר** 10 (נק״נ 5900; תוסף שליטה +4)` (keep `in Lair` forms: `או 6500 במאורה`).
- Lines: `**Saving Throws**` → `**זריקות הצלה**` with the Hebrew ability abbreviations (STR כוח, DEX זרז, CON חוס, INT תבנ, WIS חכמ, CHA כרז) and the sign before the number (`זרז +3`); `**Skills**` → `**מיומנויות**` (`התגנבות +9`); `**Resistances**` → `**עמידות**`; `**Immunities**` → `**חסינות**` (damage types and conditions both); `**Vulnerabilities**` → `**רגישות**`; `**Senses**` → `**חושים**`; `**Languages**` → `**שפות**`; `**Gear**` → `**ציוד**`.
- Table header `| STR | DEX | CON | INT | WIS | CHA |` → `| כוח | זרז | חוס | תבנ | חכמ | כרז |` (cells with numbers and modifiers unchanged).
- Section headings: `### Traits` → `### מאפיינים`; `### Actions` → `### פעולות`; `### Bonus Actions` → `### פעולות נוספות`; `### Reactions` → `### תגובות`; `### Legendary Actions` → `### פעולות אגדיות`; `### Lair Actions` → `### פעולות מאורה`.
- Movement: `burrow` חפירה, `climb` טיפוס, `fly` מעוף, `swim` שחייה, `hover` ריחוף: `10 רגל, שחייה 40 רגל`, `30 רגל, מעוף 60 רגל (ריחוף)`.
- Senses: `darkvision` ראיית חושך, `blindsight` אל-ראייה, `tremorsense` חוש-רעד, `truesight` ראיית אמת, `passive Perception 20` → `הבחנה קבועה 20`; `telepathy 120 ft.` → `טלפתיה 120 רגל`. Languages: `understands X but can't speak` → `מבין X אך אינו יכול לדבר`.
- Attacks: `Melee Attack Roll: +9, reach 15 ft.` → `התקפה בקרב פנים אל פנים: +9, הישג 15 רגל.`; `Ranged Attack Roll: +5, range 80/320 ft.` → `התקפה מרחוק: +5, טווח 80/320 רגל.`; `Melee or Ranged Attack Roll` → `התקפה בקרב פנים אל פנים או מרחוק` (in running prose the same words, e.g. `גלגול התקפה בקרב פנים אל פנים`; weapon masteries/properties use the names in `rules.yaml` ids `weapon-mastery-*`, `weapon-property-*`); `Hit:` → `פגיעה:`; `Hit or Miss:` → `פגיעה או החטאה:`; `Miss:` → `החטאה:`; `one target` → `יעד אחד`; `Failure:` → `כישלון:`; `Success:` → `הצלחה:`; `Failure or Success:` → `כישלון או הצלחה:`; `Half damage` → `מחצית מהנזק`; `Constitution Saving Throw: DC 14` → `זריקת הצלה בחוסן: דרגת קושי 14` (attribute with ב); `Strength (Athletics) check` → `בדיקת כוח (אתלטיקה)`; `Multiattack` → `התקפות מרובות`; `Legendary Resistance (3/Day)` → `עמידות אגדית (3/יום)`; `Recharge 5–6` → `טעינה מחדש 5–6`; `(3/Day)` → `(3/יום)`; `Spellcasting` → `הטלת לחשים`; `Innate Spellcasting` → `הטלת לחשים מולדת`; `At Will:` → `כרצונו:`; `1/Day each:` → `1/יום לכל לחש:`; `Emanation` → `הילה`; `Cone` חרוט, `Line` קו, `Sphere` כדור, `Cube` קובייה, `Cylinder` גליל; `Difficult Terrain` → `תוואי שטח קשה`; `Grappled` etc. from the conditions in system.he.yaml; damage `Bludgeoning damage` → `נזק מוחץ` (damage types from system.he.yaml, "נזק <סוג>"); `Hit Points` → `נקודות חיים`; `Temporary Hit Points` → `נקודות חיים זמניות`; `Proficiency Bonus` → `תוסף שליטה`; `Long Rest` → `מנוחה ארוכה`; `Short Rest` → `מנוחה קצרה`; `GM` → `שליט המבוך`; `Magic action` → `פעולת קסם`; `Bonus Action` → `פעולה נוספת`; `Reaction` → `תגובה`.
- Trait/action names in bold (`**Amphibious.**`) are translated and keep their trailing period and bold markers.

## Check loop (required)
From /Users/michaelyarichuk/RiderProjects/TTRPG.Codex run:
`dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation BATCH/out`
and for flavor: `dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation BATCH/out-flavor`
Fix every ERROR and every `structure` or `terms` warning for your items, rerun, up to 3 rounds. Never run `dotnet build`. Do not edit anything outside BATCH/out and BATCH/out-flavor. Final reply: the two checker summary lines and the list of any warnings you chose to leave (with the reason), nothing else.

## Choices already made in earlier batches (stay consistent)
Study action = `פעולת עיון`; Shadowfell = `ארץ הצללים`; Feywild = `ארץ הפייה`; Upper Planes = `המישורים העליונים`; plural undead = `אל-מתים`; Far Realm = `המרחב הרחוק`; Difficult Terrain = `תוואי שטח קשה`; Dodge = `חמיקה`, Dash = `מאוץ`, Disengage = `נסיגה`, Help = `עזרה לאחר`.

Monster batch 1 choices: escape DC = `דרגת קושי לחילוץ` (`אחוז (דרגת קושי לחילוץ 14)`); "Large or smaller" = `גדול או קטן יותר`; (A)/(B) options in Multiattack = `(א)`/`(ב)`; empty spellcasting lists stay empty as in the English; adult dragons: `דרקון <צבע> בוגר` with colors שחור, כחול, ירוק, אדום, לבן and metals פליז, ארד, נחושת, זהב, כסף; young `צעיר`, wyrmling `גור דרקון <צבע>` for wyrmling, ancient `קדמון`; a creature's Hebrew name must not collide with a magic item or spell name where avoidable.

## More choices from batches 2-12 (binding)
Write "1 דקה", "1 שעה" with digits (the checker counts digits; never "דקה אחת"). A `terms` warning that only reflects Hebrew gender/number agreement (e.g. `חיה קטנה`, `חיה עצומה` for Small/Huge) may be left with that reason. Take size, creature type and alignment in the italic header from the `property size/creatureType/alignment` lines of the input; the checker cannot verify alignment, so double-check each one (chaotic evil = רע וכאוטי, lawful evil = רע וחוקי, etc., exactly the labels in system.he.yaml). Spell-level versions: `(גרסת דרגה N)`. Gear: chain shirt חולצת שרשראות, hide armor שריון עור, javelin רומח, morningstar כוכב בוקר, light hammer פטיש קל, breastplate שריון חזה, longbow קשת ארוכה, battleaxe גרזן קרב. Bloodied = פצוע; Nine Hells = תשעת הגיהינומים; "None" = אין; Opportunity Attack = התקפה מזדמנת; Flyby = מעוף חולף; Engulf = בליעה (engulfed = בלוע); cover = מחסה (חצי / שלושה רבעים / מלא), never "כיסוי"; "Common plus one other language" = שפה משותפת ועוד שפה אחת; Cultist = חסיד כת, Cultist Fanatic = קנאי כת, Commoner = אזרח פשוט; Deception = הונאה, Survival = השרדות; Air Elemental = יסודן אוויר, Fire Elemental = יסודן אש, Earth Elemental = יסודן אדמה; Giant types: ענק אש, ענק כפור, ענק ענן; Ghoul = גהול.
Names already taken (do not reuse): אבולת, גלימן, בולט, חייל קנטאור, כימרה, צ׳ול, גולם חימר, אטין, גולם בשר, נחש מעופף, צפרדע, גרגויל, קובייה ג׳לטינית, גאסט, דרואיד, דריאדה, מפיט אבק, עיט, אפריט, פיל, אייל, אריניס, אטרקאפ, חסיד כת, קנאי כת, אזרח פשוט, דארקמנטל, באגבר, שטן עצמות, נץ דם, שטן שרשראות.
