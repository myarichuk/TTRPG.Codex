# Task: translate D&D 5e SRD magic items and equipment into Hebrew

TYPE is `magic-item` or `equipment`; the caller says which. Input: `BATCH/input.md` lists entries. Each has `## id:`, `name:`, the exact description between `<<<` and `>>>`, and `property key: value` lines (read-only context).
`BATCH/flavor.md` (magic items only, if present) has the original English lore of the same items.
Outputs:

1. `BATCH/out/<TYPE>/<file>.yaml` where `<file>` is `magic-items.yaml` or `equipment.yaml`: a YAML list, one item per entry (rules text and name), nothing else:

```yaml
- id: <same id>
  name: <Hebrew name>
  description: |-
    <Hebrew translation of the description>
```
No `properties:` at all (category, cost, damage, mastery, armorClass stay canonical English data and are shown through the system's value labels).

2. `BATCH/out-flavor/magic-item/flavor-01.yaml`, only if `BATCH/flavor.md` exists:

```yaml
- id: <same id>
  flavor: >-
    <Hebrew flavor, one paragraph>
```
Flavor is lore, not rules: keep its voice (dry, concrete, a little wry), keep every image and joke, add nothing, drop nothing, do not translate word by word.

## Rules (a script checks the first three)
1. Translate ONLY `name` and `description`.
2. Every number and dice expression stays exactly: `3d6`, `+2`, `DC 17`→the number 17, `25`, `1d100` table cells like `01-10`. Dice are Latin `8d6`, signs before the number (`+2`). Do NOT convert units: feet stay feet (`30 רגל`), miles stay miles. Weight `3 lb.` → `3 פאונד`; cost `15 gp` → `15 מ״ז` (sp מ״כ, cp מ״נ, ep מ״א, pp מ״פ). No metres/kilograms.
3. Reproduce the layout exactly: same lines in the same order, every blank line, the `**Label:**` lines with TWO trailing spaces as in the input, the italic header line, bullets, table rows and separator rows, bold/italic markers. Translate the table header cells and the text cells; numbers and dice cells unchanged. Do not merge, split, add or drop paragraphs or rows.
4. Terminology (binding): `content/systems/srd5e/system.he.yaml`, `docs/i18n-glossary.md`, the "Decisions (applied)" and "Decisions (stat blocks, items)" sections of `docs/i18n-he-terms.md`, sections 6, 7 and 8 of that file (combat, equipment, creature types). Weapon properties and masteries: the Hebrew names (`name:`) of the entries `weapon-property-*` and `weapon-mastery-*` in `content/systems/srd5e/packs/srd-5.2.1-he/rule/rules.yaml`; skills, damage types, languages, conditions likewise. Spell names in prose = the Hebrew `name:` from `content/systems/srd5e/packs/srd-5.2.1-he/spell/spells.yaml`. Creature names in prose (e.g. "Aboleth", "Lich") = the Hebrew name from `content/systems/srd5e/packs/srd-5.2.1-he/monster/monsters.yaml` if it exists yet, else a natural name of your own, consistent inside the batch. One term per concept. Do not browse websites.
5. Magic item header line (e.g. `Armor (Any Medium or Heavy, Except Hide Armor)`, `Weapon (Any Sword)`, `Wondrous Item`, `Potion`, `Ring`, `Rod`, `Staff`, `Wand`, `Scroll`): `שריון (כל בינוני או כבד, פרט לשריון עור)`, `נשק (כל חרב)`, `חפץ פלאי`, `שיקוי`, `טבעת`, `מטה קצר`, `מטה`, `שרביט`, `מגילה`. Rarity words: נפוץ / לא נפוץ / נדיר / נדיר מאוד / אגדי / חפץ עוצמה; attunement: `דורש התאמה` (with the rest of the parenthesis translated). The word for the item class in prose: `חפץ קסום`. Armor types: שריון קל / בינוני / כבד; weapon categories נשקים פשוטים / צבאיים (Martial = `נשקי לחימה`), melee `קרב פנים אל פנים`, ranged `טווח`.
6. Voice: rulebook Hebrew, present tense, the reader is "אתה" (masculine singular), like the published Hebrew D&D books. Hebrew punctuation (geresh ׳, gershayim ״), no nikud. Read your Hebrew once as a native reader: rewrite word-for-word calques idiomatically.
7. Names: a natural Hebrew name; proper names (Heward's, Bigby's, Mordenkainen's, Tenser's, Otiluke's, Nystul's, Leomund's...) are transliterated, the item noun translated: `שריון אדמנטיום`, `טבעת הגנה`. No Latin letters in a name. No two entries in the whole pack may share a Hebrew name: grep `BATCH/../batch-*/out/<TYPE>/*.yaml` and `content/systems/srd5e/packs/srd-5.2.1-he/<TYPE>/` for your names before finalising. Items that come in +1/+2/+3 forms keep the "+1, +2 או +3" in the name.
8. Valid YAML only: `|-` block scalars for descriptions, `>-` for flavor, quote names containing a colon or starting with a special character.

## Check loop (required)
From /Users/michaelyarichuk/RiderProjects/TTRPG.Codex run:
`dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation BATCH/out`
and for flavor: `dotnet run --project tools/Codex.TranslationCheck --no-build -- --lang he --translation BATCH/out-flavor`
Fix every ERROR and every `structure` or `terms` warning for your items, rerun, up to 3 rounds. Never run `dotnet build`. Do not edit anything outside BATCH/out and BATCH/out-flavor. Final reply: the checker summary line(s) and the list of any warnings you chose to leave (with the reason), nothing else.

## Choices already made in earlier batches (stay consistent)
Study action = `פעולת עיון`; Shadowfell = `ארץ הצללים`; Feywild = `ארץ הפייה`; Upper Planes = `המישורים העליונים`; plural undead = `אל-מתים`; Far Realm = `המרחב הרחוק`; Dodge = `חמיקה`, Dash = `מאוץ`, Disengage = `נסיגה`, Help = `עזרה לאחר`; Difficult Terrain = `תוואי שטח קשה`.
Magic-item batch 1 choices: plain-name "Ammunition" = `תחמושת קסומה`, "Armor" = `שריון קסום`; `קמע` amulet; `פעולת שימוש` (Utilize); `אור בהיר` / `אור עמום`; `פעולת קסם`; `פגיעה קריטית`; `נזק כוח קסום`; `דרגת קושי לחילוץ` (escape DC); attack roll = `גלגול התקפה`, damage roll = `גלגול נזק` (as in the rules pack; never "הטלת תקיפה"); `מעבר מישורי`, `מישור פנימי` / `מישור חיצוני`, `המישור האסטרלי`; outer planes transliterated: ארבוריאה, ארקדיה, ארץ החיות, ביטופיה, אליסיום, מכאנוס, הר סלסטיה, יסגרד, התהום, אכרון, קרצרי, גהנום, הייד, לימבו, תשעת הגיהינומים, פנדמוניום; creature-type table plurals: שיקוצים, חיות, יצורי מרום, יצירי כפיים, דרקונים, יסודנים, דמויי אדם, יצורי פייה, בני-תופת, ענקים, תפלצים, רפשים, צמחים, אל-מתים.
Consistency across the pack: a weapon, armor, tool or gear item named in prose uses the Hebrew `name:` of the equipment entry with that id (look in `scratchpad/b/equipment/batch-*/out/equipment/equipment.yaml` of the batch directory's parent, and in `content/systems/srd5e/packs/srd-5.2.1-he/equipment/` if it exists); a creature named in prose uses its Hebrew name from `scratchpad/b/monster/batch-*/out/monster/monsters.yaml`. Cover = `מחסה`, never "כיסוי". Damage types are written `מוחץ / דוקר / חותך` (never "מחץ"). Elementals: `יסודן מים`, `יסודן אש`; bracers `מגני אמה`.
