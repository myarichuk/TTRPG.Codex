# Hebrew terminology comparison: Codex vs the community Hebrew 5e SRD

## Decisions (applied)

Applied to `system.he.yaml`, `codex-flavor-he`, `Strings*.he.resx` and the glossary. The tables below record the comparison that led here.

- Classes: sorcerer כושף, bard פייטן, paladin פלאדין (warlock מכשף, wizard אשף unchanged). Subclass: מועצת הידע, פטרון בן-תופת.
- Species: the field is גזע/גזעים; dragonborn דם דרקון, gnome ננס, halfling זוטון, human בן-אנוש (pl. בני-אנוש), tiefling זד; dwarf גמד, elf אלף, goliath גוליית, orc אורק stay.
- Abilities: Intelligence תבונה (תבנ), Charisma כריזמה (כרז).
- Core: saving throw זריקת הצלה, death save זריקת הצלה ממוות, spell save DC דרגת קושי להצלה נגד לחשים; proficiency שליטה, proficiency bonus תוסף שליטה, weapon mastery אומנות נשק; feat כשרון, origin feat כשרון מוצא; ability score improvement העלאת ערך תכונה.
- Level is always דרגה, class is always מקצוע, ability modifier מתאם, bonus action פעולה נוספת, cantrip לחשון. Skills keep מיומנות.
- Skills: Arcana מאגיה, Perception הבחנה (passive הבחנה קבועה), Sleight of Hand זריזות ידיים, Survival השרדות, Stealth התגנבות.
- Conditions: עיוור, חירש, מבוהל, אחוז, מרוסן, שרוע, חסר הכרה; the rest unchanged (חסר יכולת פעולה stays).
- Damage: מוחץ, דוקר, חותך, כוח קסום; vulnerability רגישות (resistance עמידות, immunity חסינות stay).
- Schools: מגננה, יצירה, חיזוי, הקסמה, תעצומה, אשליה, תאובה, תהפוכה.
- Sizes: קטנטן, קטן, בינוני, גדול, עצום (Huge), אדיר (Gargantuan).
- Creature types, senses, combat actions and weapon properties follow the SRD site terms listed in section 3 onward; they are not in the Hebrew files yet.
- Kept: שליט המבוך (שה״מ), קמפיין, מפגש, נקודות חיים, דרגת שריון, סבב, תא לחשים, פגיעה קריטית.

---

Purpose: decide which Hebrew terms Codex should use. This is a terminology reference only.

- Reference source: the community Hebrew D&D 5e SRD at <https://5esrd.co.il> (translator Yuval Sapir, edited by a translation team; OGL 1.0a, SRD 5.1). Read in the 2026 snapshot. Only short terms are recorded here, no sentences.
- "Ours today" comes from three places, and they do not always agree with each other:
  - `content/systems/srd5e/system.he.yaml` (labels) and `packs/codex-flavor-he/**` (names), called **yaml/pack** below.
  - `src/Codex.Localization/Resources/Strings.*.he.resx` (interface), called **resx** below.
  - `docs/i18n-glossary.md`.
- The SRD site is SRD 5.1 (2014 rules), Codex content is SRD 5.2.1 (2024 rules). Terms for things that exist only in 5.2.1 (species instead of races, Goliath, Orc, origin feats, weapon mastery, the 5.2 conditions) have no SRD-site equivalent. "n/a" means exactly that.
- SRD abbreviations appear in the text as typed (logical order). See section 9 for number quirks.

**Correction to the brief.** The SRD site has warlock = **מכשף** and sorcerer = **כושף** (verified on the class pages: the מכשף page has d8, "פטרון מעולם אחר", "קסם ברית", the כושף page has d6, "שושלת דרקונים", "מקור כושף"). Codex also has warlock = מכשף, so that one agrees. The real collision is Codex's **sorcerer = קוסם**, which the SRD does not use (it uses כושף), and "קוסם" is also the ordinary Hebrew word for any magician/mage.

## Top disagreements (summary)

1. Sorcerer: ours קוסם, SRD כושף. Warlock agrees (מכשף). Bard: ours בארד, SRD פייטן. Paladin: ours פלדין, SRD פלאדין (spelling).
2. Species (races): ours transliterates Gnome, Halfling, Tiefling, Human, Dragonborn as גנום / הלפלינג / טיפלינג / אדם / יליד דרקון. The SRD has ננס / זוטון / זד / בן-אנוש / דם דרקון. Dwarf = גמד in both.
3. Intelligence: ours אינטליגנציה (abbr. אינט), SRD תבונה (abbr. תבנ). Charisma: ours קסם, SRD כריזמה (abbr. כרז). Our "קסם" also means magic.
4. Skills: ours Arcana = כישוף (same word as our Enchantment school), SRD מאגיה. Perception: ours תפיסה, SRD הבחנה. Survival: ours הישרדות, SRD השרדות. Sleight of Hand: ours תחבולה, SRD זריזות ידיים. Deception / Intimidation: ours הונאה / הפחדה, SRD תרמית / איום. Passive perception: ours תפיסה פסיבית, SRD הבחנה קבועה.
5. Core rules words: HP ours נקודות חיים, SRD נקודות פגיעה (נק״פ). AC ours דרגת שריון, SRD דירוג שריון / דרג״ש. Proficiency bonus ours בונוס מיומנות, SRD תוסף שליטה. Saving throw: yaml הצלות / resx הטלות מילוט / "סיכון מילוט", SRD זריקת הצלה. Spell slot ours תא לחשים, SRD יחידת לחש. Cantrip ours לחש בסיס, SRD לחשון.
6. Our own sets conflict: level (דרגה in yaml, רמה in resx; the SRD uses דרגה for class level and דרג for spell level), class (מקצוע almost everywhere but 3 resx strings say מחלקה), feat (תכונה מיוחדת in yaml, כישרון in a resx hint, SRD כשרון), conditions (yaml `conditions:` values differ from the flavor-pack names, see section 4).
7. Conditions: ours מוכה עיוורון / מוכה חירשות / מפוחד / נתפס / אסור / שרוע vs SRD עיוור / חירש / מבוהל / אחוז / מרוסן / שרוע. Incapacitated: ours חסר יכולת פעולה, SRD חסר אונים.
8. Damage types: ours מחץ / דקירה / חיתוך / נקרוטי / נפשי / כוח, SRD מוחץ / דוקר / חותך / תאובה / תודעה / כוח קסום. Our "כוח" for force collides with Strength.
9. Sizes: ours Huge = ענק, Gargantuan = עצום, Tiny = זעיר. SRD Tiny = קטנטן, Huge = עצום, Gargantuan = אדיר. Our עצום means Gargantuan, the SRD's means Huge.
10. Schools: ours הגנה / זימון / ניבוי / כישוף / שחרור / אשליה / נקרומנטיקה / שינוי צורה, SRD מגננה / יצירה / חיזוי / הקסמה / תעצומה / אשליה / תאובה / תהפוכה. Dungeon Master: ours שליט המבוך (שה״מ), SRD only writes the abbreviation שה״ם (final mem).

---

## 1. Role words

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Dungeon Master | שליט המבוך (UI, resx), glossary also lists שה״מ | שה״ם (abbreviation only; the full form is not written out in the pages read) | The SRD abbreviation ends in final mem (ם), which suggests "שליט המבוכים" (plural); ours is "שליט המבוך" with abbreviation שה״מ. he.wikipedia and hamichlol use "שליט המבוך (שה״מ)" (see Other sources). **Recommendation: keep ours.** Confirmed by two Hebrew encyclopedia sources; consider accepting שה״ם as a search alias. |
| Player | שחקן | שחקן | Same. |
| Player character (PC) | דמות שחקן | דמות שחקן | Same. |
| NPC | דמות לא-שחקנית (NPC) | דמות ללא שחקן (section title "דמויות ללא שחקן") | Both are fine. SRD form is shorter in tables. |
| Monster | מפלצת / יצור | מפלצת; creature = יצור | Same. |
| Character | דמות | דמות | Same. |
| Campaign | קמפיין | not in the SRD text (the word "הרפתקה" = adventure is used) | Transliteration is common among players. Keep. |
| Session | מפגש | not in the SRD text | Keep. |
| Encounter | מפגש קרב | התקלות (as in "התקלות קרבית") | Our "מפגש" is both session and encounter, which is ambiguous in the combat UI ("מפגש קרב" is clear enough). |
| Adventure | n/a | הרפתקה | |
| Round / Turn | סבב / תור | סיבוב / תור | Round differs: ours סבב, SRD סיבוב. Both understood. **Recommendation: adopt סיבוב** only if the rest of the UI follows it; low priority. |
| Level (character) | דרגה (yaml), רמה (resx) | דרגה (e.g. "דרגה 1 עד דרגה 20"); also "רמת הדרואיד" in prose | Ours is inconsistent. **Recommendation: adopt דרגה for class/character level** (published usage); keep רמה only if a UI decision is made to use it everywhere. |
| Spell level | דרגה / רמה | דרג (e.g. "לחש תעצומה מדרג 3"); "בדרגים גבוהים יותר" | SRD uses דרג for spell level and exhaustion level. |
| Library / homebrew / wiki / journal | ספרייה / תוכן ביתי / ויקי / יומן | n/a | Codex product words; no SRD equivalent. |

## 2. Classes, subclasses, species, backgrounds

### Classes

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Class | מקצוע (yaml, mostly resx; 3 resx strings say מחלקה) | מקצוע | Agree. Fix the 3 stray מחלקה strings. |
| Barbarian | ברברי | ברברי | Same. |
| Bard | בארד | **פייטן** | Disagree. Players often say "בארד" aloud; the published SRD says פייטן. Hebrew class names elsewhere are Hebrew words (לוחם, נוכל, כוהן), so פייטן fits. **Recommendation: adopt פייטן** (matches the SRD and published text); keep בארד as a search alias. |
| Cleric | כוהן | כוהן | Same. |
| Druid | דרואיד | דרואיד | Same. |
| Fighter | לוחם | לוחם | Same. |
| Monk | נזיר | נזיר | Same. |
| Paladin | פלדין | פלאדין | Spelling only. **Recommendation: adopt פלאדין** (the SRD spelling). |
| Ranger | סייר | סייר | Same. |
| Rogue | נוכל | נוכל | Same. |
| Sorcerer | קוסם | **כושף** | Disagree, and ours is ambiguous: "קוסם" is also the generic word for any magic-user and sounds like wizard. **Recommendation: adopt כושף.** It is the SRD term, it does not collide with the other casters, and Hebrew players of the SRD know it. |
| Warlock | מכשף | מכשף | Agree. Note "מכשף" in everyday Hebrew is a generic sorcerer/witch, so it reads as the wrong class to someone who has not seen the SRD; but this is the published term and it is consistent with ours. **Keep.** |
| Wizard | אשף | אשף | Same. |

### Subclasses (5.2.1 SRD subclass names vs SRD 5.1 site)

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Subclass | תת-מקצוע | per-class names: נתיב קדמוני (barbarian), מועצת פייטנים (bard), תחום השפעה קדוש (cleric), מעגל דרואידי (druid), מסורת נזירית (monk), שבועת מסירות (paladin), פטרון מעולם אחר (warlock), מקור כושף (sorcerer), מסורת מאגית (wizard) | The SRD has no single word; each class has its own heading. Ours is a clear generic UI word. Keep. |
| Champion (fighter) | אלוף | אלוף | Same. |
| Path of the Berserker | דרך הברסרקר | הנתיב של הברזרקר | Spelling of the loan (ברסרקר vs ברזרקר) and דרך vs נתיב. Adopt SRD ("נתיב ... הברזרקר") if Barbarian "Primal Path" = "נתיב קדמוני" is adopted. |
| College of Lore | קולג׳ הידע | מועצת הידע (college = מועצה) | Disagree. SRD "מועצה" is a real Hebrew word, ours is a transliteration. **Recommendation: adopt מועצת הידע.** |
| Circle of the Land | מעגל האדמה | מעגל האדמה | Same. |
| Draconic Sorcery (5.2.1) / Draconic Bloodline (5.1) | קסם דרקוני | שושלת דרקונים | Rename differs in English too (5.2.1 renamed it). Keep ours, or adopt שושלת דרקונים for the 5.1 name if both are shown. |
| Evoker (wizard, 5.2.1) / School of Evocation (5.1) | מטיל כוחות | מסורת תעצומה | Ours is a loose rendering. SRD ties it to the school name תעצומה. **Recommendation: adopt "מסורת תעצומה"** only if the schools in section 5 are also adopted; otherwise keep. |
| Fiend Patron | פטרון שטן | פטרון: אדון האופל ("The Fiend"); fiend (creature type) = בן-תופת | Disagree. "אדון האופל" is the SRD's Fiend patron name. |
| Hunter (ranger) | צייד | צייד | Same. |
| Life Domain | תחום החיים | תחום החיים | Same. |
| Oath of Devotion | שבועת המסירות | שבועת מסירות | Same (article only). |
| Thief | גנב | גנב | Same. |
| Warrior of the Open Hand (5.2.1) / Way of the Open Hand (5.1) | לוחם היד הפתוחה | משעול היד הפתוחה | Rename in English. Keep ours for 5.2.1. |

### Species / races

5.2.1 says "species", 5.1 and the SRD site say "race" (גזע). Ours: מין (field label "מין", plural "מינים"). "מין" in Hebrew also means sex/gender and kind, so it is ambiguous out of context.

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Species / Race | מין | גזע | Disagree. Players know "גזע". "מין" can be misread as sex. **Recommendation: adopt גזע** (sound but dated); alternatives such as "תרבות" or "יליד" are not used anywhere. Needs a human decision. |
| Dragonborn | יליד דרקון | דם דרקון | **Recommendation: adopt דם דרקון** (SRD). |
| Dwarf | גמד | גמד | Agree. Note: everyday Hebrew "ננס" and "גמד" both mean dwarf/gnome loosely; the SRD uses גמד for dwarf. |
| Elf | אלף | אלף | Same. |
| Gnome | גנום | **ננס** | Disagree. The SRD uses ננס for gnome (verified: +2 Intelligence "תבונה", rock gnome = ננסי אבן). Caution: in common Hebrew "ננס" also means dwarf, so ננס and גמד are easily confused. **Recommendation: adopt ננס** to match the published text (accept גנום as an alias). |
| Goliath | גוליית | n/a (not in 5.1 SRD) | Keep ours. |
| Halfling | הלפלינג | זוטון | **Recommendation: adopt זוטון** (SRD). |
| Human | אדם | בן-אנוש (plural בני-אנוש) | Ours "אדם" is ambiguous in a species list; בן-אנוש is the SRD and standard. **Recommendation: adopt בן-אנוש.** |
| Orc | אורק | אורק (the monster page uses אורק; the playable race is חצי-אורק = half-orc) | The 5.1 playable race is Half-Orc. Keep אורק. |
| Tiefling | טיפלינג | זד | **Recommendation: adopt זד** (SRD), accept טיפלינג as alias. Note זד also means "arrogant" in Hebrew; some players prefer the loan. Real choice. |
| Half-elf / Half-orc | n/a | חצי-אלף / חצי-אורק | Not in 5.2.1. |
| Hill Dwarf / Rock Gnome | n/a | גמד גבעות / ננסי אבן | |

### Backgrounds

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Background | רקע | רקע | Same. |
| Acolyte | משרת מקדש | משרת בקודש | **Recommendation: keep ours or adopt SRD** (minor). |
| Criminal | פושע | n/a (5.1 SRD has only Acolyte) | Keep. |
| Sage | חכם | n/a | Keep. "חכם" is a plain word; consider "חכמן" or "מלומד" if it reads as an adjective. |
| Soldier | חייל | n/a | Keep. |
| Feat | תכונה מיוחדת (yaml), "כישרונות" (a resx hint) | כשרון (plural כשרונות) | Disagree and ours is inconsistent. **Recommendation: adopt כשרון.** |
| Origin feat / General feat / Epic boon | תכונת מוצא / כללית / מתנה אפית | n/a | 5.2.1 only. Keep. |
| Feature (class feature) | יכולת / מאפיין | מאפיין (e.g. "מאפיין מסורת נזירית") | Ours uses "יכולות המקצוע"; SRD uses מאפיין. Keep ours (clear). |
| Fighting style | סגנון לחימה | n/a in the pages read | |
| Ability Score Improvement | שיפור ערכי יכולת (flavor pack) | העלאת ערך תכונה | Ours uses "יכולת" for ability, everywhere else "תכונה". **Recommendation: use העלאת ערך תכונה.** |

## 3. Abilities, skills and core mechanics

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Ability (score) | תכונה / ציוני תכונות; "יכולת" in the feat name | תכונה / ערך תכונה | Agree on תכונה; avoid "יכולת" (also means ability/feature). |
| Ability modifier | no Hebrew label found in the files read | מתאם (מתאם תכונה) | Worth adopting מתאם. |
| Strength | כוח (abbr. כוח) | כוח (abbr. כוח) | Same. |
| Dexterity | זריזות (זרז) | זריזות (זרז) | Same. |
| Constitution | חוסן (חוס) | חוסן (חוסן) | Same. |
| Intelligence | אינטליגנציה (אינט) | **תבונה** (תבנ) | Disagree. **Recommendation: adopt תבונה** (short, fits the abbreviation column). |
| Wisdom | חוכמה (חכמ) | חוכמה (חכמ) | Same. |
| Charisma | **קסם** (קסם) | **כריזמה** (כרז) | Disagree. "קסם" collides with "magic" and "spell" family words. **Recommendation: adopt כריזמה.** |
| Ability check | בדיקת {0} | בדיקת תכונה | Same. |
| Saving throw | הצלות (yaml), הטלות מילוט (resx), הטלת הצלה (a resx hint) | **זריקת הצלה** (plural זריקות הצלה) | Disagree and ours is three-way inconsistent. **Recommendation: adopt זריקת הצלה.** Death save: ours הטלות הצלה מפני מוות, SRD זריקת הצלה ממוות. |
| Spell save DC | סיכון מילוט מלחשים | ד״ק (דרגת קושי) הצלה; see section 5 | "סיכון מילוט" is not a known term. **Recommendation: use דרגת קושי (ד״ק).** |
| Difficulty Class (DC) | דרגת קושי / ד״ק (not seen in files) | ד״ק (דרגת קושי) | |
| Proficiency | מיומנות / אימון / שליטה (the class fields use all three) | שליטה | Ours collides: "מיומנות" is both skill and proficiency in the yaml (e.g. מיומנות בנשק vs skillChoices). **Recommendation: adopt שליטה for proficiency, מיומנות only for skills.** |
| Proficiency bonus | בונוס מיומנות | תוסף שליטה | **Recommendation: adopt תוסף שליטה.** |
| Expertise | מומחיות | מומחיות | Same. |
| Advantage / Disadvantage | יתרון / חיסרון (resx), (yaml n/a) | יתרון / חסרון (the text also contains חיסרון in a few places) | Same word. Spelling: SRD mostly חסרון, ours חיסרון. Either; pick one. |
| Initiative | יוזמה | יוזמה | Same. |
| Hit points (HP) | נקודות חיים | **נקודות פגיעה** (נק״פ) | Disagree. "נקודות חיים" is the more natural everyday term and what online communities say. Hit Dice: ours קוביות חיים, SRD קוביות פגיעה. Temporary HP: ours נקודות חיים זמניות, SRD נקודות פגיעה זמניות. **Recommendation: real choice.** Keep ours for UI clarity, or adopt the SRD set as a whole; do not mix. |
| Armor Class (AC) | דרגת שריון | דירוג שריון (monsters), דרגת שריון (דרג״ש) (armor table) | The SRD uses both. Ours agrees with the armor table. Keep. |
| Speed | מהירות | מהירות | Same. |
| Passive Perception | תפיסה פסיבית | הבחנה קבועה (e.g. "הבחנה קבועה 14") | Disagree. |
| Inspiration | n/a in the files read | השראה | |
| Heroic Inspiration (5.2.1) | n/a | n/a | |
| Level | see section 1 | דרגה | |
| Experience points | ניסיון (monster field) | נקודות ניסיון (נק״נ) | |

### Skills

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Skill | מיומנות | מיומנות | Same. |
| Acrobatics | אקרובטיקה | אקרובטיקה | Same. |
| Animal Handling | טיפול בחיות | טיפול בחיות | Same. |
| Arcana | **כישוף** | **מאגיה** | Disagree, and ours collides with our Enchantment school (כישוף). **Recommendation: adopt מאגיה.** |
| Athletics | אתלטיקה | אתלטיקה | Same. |
| Deception | הונאה | תרמית | Either. Ours is fine plain Hebrew; SRD is the published term. |
| History | היסטוריה | היסטוריה | Same. |
| Insight | תובנה | תובנה | Same. Note: it is the same word as nothing else in the SRD (Int is תבונה), so no collision. |
| Intimidation | הפחדה | איום | Either. |
| Investigation | חקירה | חקירה | Same. |
| Medicine | רפואה | רפואה | Same. |
| Nature | טבע | טבע | Same. |
| Perception | תפיסה | **הבחנה** | Disagree. "הבחנה" is also what the SRD uses for the Perception check inside monster stat blocks. **Recommendation: adopt הבחנה.** |
| Performance | הופעה | הופעה | Same. |
| Persuasion | שכנוע | שכנוע | Same. |
| Religion | דת | דת | Same. |
| Sleight of Hand | תחבולה | זריזות ידיים | Disagree. **Recommendation: adopt זריזות ידיים.** |
| Stealth | התגנבות | התגנבות | Same. |
| Survival | הישרדות | השרדות | Spelling. |

## 4. Conditions

Ours is inconsistent: `system.he.yaml` `conditions:` has one set, the flavor-pack names (`condition-001.yaml`) another. Both are listed.

| English | Ours: yaml `conditions:` | Ours: flavor pack name | 5esrd.co.il | Notes |
|---|---|---|---|---|
| Blinded | מוכה עיוורון | עיוור | עיוור | **Adopt עיוור** (everywhere). |
| Charmed | מוקסם | מוקסם | מוקסם | Same. |
| Deafened | מוכה חירשות | חרש | חירש | Spelling of the pack name (חרש vs חירש); yaml form is clumsy. **Adopt חירש.** |
| Exhaustion | תשישות | תשישות | תשישות | Same. Levels: SRD writes "דרג" + number (table of effects 1 to 6, "מוות" at 6). |
| Frightened | מפוחד | מבוהל | מבוהל (the text also says "מפוחד" in a couple of places) | **Adopt מבוהל.** |
| Grappled | נתפס | תפוס | **אחוז** | Disagree. The SRD condition is "אחוז", the action is "אחיזת יצור". Ours is two different words. **Recommendation: adopt אחוז**; "תפוס" can be misread as "occupied". |
| Incapacitated | חסר יכולת פעולה | חסר יכולת פעולה | **חסר אונים** | "חסר אונים" is the published term but in everyday Hebrew it means "helpless". Ours is more literal. Real choice; **recommend keeping ours** unless full SRD matching is a goal. |
| Invisible | בלתי נראה | בלתי נראה | בלתי נראה | Same. |
| Paralyzed | משותק | משותק | משותק | Same. |
| Petrified | מאובן | מאובן | מאובן | Same. |
| Poisoned | מורעל | מורעל | מורעל | Same. |
| Prone | שרוע | מוטל על הקרקע | שרוע | **Adopt שרוע.** |
| Restrained | אסור | מרוסן | מרוסן | **Adopt מרוסן** ("אסור" also means "forbidden"). |
| Stunned | המום | המום | המום | Same. |
| Unconscious | חסר הכרה | מחוסר הכרה | חסר הכרה | Either; yaml form equals SRD. |
| Dead / Stable | מת / יציב | n/a | stabilize = יצוב יצור; "unconscious/dying" wording around death saves | |

## 5. Damage types, schools and spellcasting

### Damage types

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Damage | נזק | נזק | Same. SRD writes "נזק <type>" (e.g. נזק אש). |
| Acid | חומצה | חומצה | Same. |
| Bludgeoning | מחץ | מוחץ | Ours is the noun; SRD the adjective ("נזק מוחץ"). **Adopt SRD forms** so "נזק X" reads naturally. |
| Cold | קור | קור | Same. |
| Fire | אש | אש | Same. |
| Force | כוח | **כוח קסום** | Ours collides with Strength (כוח). **Adopt כוח קסום.** |
| Lightning | ברק | ברק | Same. |
| Necrotic | נקרוטי | **תאובה** | Disagree. "תאובה" is an obscure word (usually "desire"), but it is the published term; ours is a loan. Real choice: **keep נקרוטי** for clarity, or adopt תאובה for SRD parity. |
| Piercing | דקירה | דוקר | Adopt SRD (adjective). |
| Poison | רעל | רעל | Same. |
| Psychic | נפשי | **תודעה** | Disagree. "נזק נפשי" reads naturally, "נזק תודעה" is the published term. Real choice; I would keep ours. |
| Radiant | קורן | קורן | Same. |
| Slashing | חיתוך | חותך | Adopt SRD (adjective). |
| Thunder | רעם | רעם | Same. |
| Resistance / Immunity / Vulnerability | עמידות / חסינות / פגיעות | עמידות / חסינות / **רגישות** | "פגיעות" is fine plain Hebrew; the SRD uses "רגישות" (the column header reads "רגישות, עמידות וחסינות"). **Recommendation: adopt רגישות.** "פגיעות" (vulnerability) can also read as "hits". |

### Spell schools (the SRD calls them "מסורות הקסם" = traditions of magic)

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| School | אסכולה | מסורת (קסם) | Heading "מסורות הקסם". |
| Abjuration | הגנה | מגננה | Verified on the Shield spell page. |
| Conjuration | זימון | יצירה | Verified on Misty Step. Ours "זימון" is more descriptive. |
| Divination | ניבוי | חיזוי | Verified on Detect Magic. |
| Enchantment | **כישוף** | הקסמה | Ours collides with the Arcana skill (also כישוף). **Adopt הקסמה** (or a new word). |
| Evocation | שחרור | תעצומה | Verified on Fireball and Cure Wounds. |
| Illusion | אשליה | אשליה | Same. |
| Necromancy | נקרומנטיקה | תאובה | The same word is used for necrotic damage. |
| Transmutation | שינוי צורה | תהפוכה | Ours means "shapechange", too narrow. |

The SRD's school words are unusual and largely unknown outside the translation, so this is a real choice. Whatever is chosen, change כישוף (enchantment) because of the Arcana collision.

### Spellcasting vocabulary

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Spell | לחש | לחש | Same. |
| Cantrip | לחש בסיס / לחשי בסיס | **לחשון** (plural לחשונים) | Disagree. **Recommendation: adopt לחשון** (single word, in the SRD and in class tables). |
| Spell slot | תא לחשים / תאי לחשים; Pact slots: תאי ברית | **יחידת לחש** (plural יחידות לחש) | Disagree. "תא" is natural to UI ("תאים"). Real choice; keep ours if the sheet is a UI grid, adopt SRD if text must match books. |
| Spell level | דרגה / רמה | דרג ("לחש תעצומה מדרג 3") | |
| Casting time | זמן הטלה | זמן הטלה | Same. |
| Range | טווח | טווח | Same. |
| Components | רכיבים | רכיבים | Same. V / S / M: SRD ק / ת / ח (קולי / תנועתי / חומרי); material component text in parentheses. |
| Verbal / Somatic / Material | n/a | קולי (ק) / תנועתי (ת) / חומרי (ח) | Ours has no abbreviations for components in the files read. |
| Duration | משך | משך | Same. Instantaneous: SRD מיידי (also written מידי); until dispelled etc. not read. |
| Concentration | ריכוז | ריכוז | Same. |
| Ritual | טקס | טקס | Same (written "(טקס)" next to the level). |
| Touch / Self | n/a (not in the files read) | מגע / עצמי | |
| At higher levels | שימוש בתא לחשים בדרגה גבוהה יותר | בדרגים גבוהים יותר | |
| Spell attack | n/a | התקפת לחש | |
| Spell list | n/a | רשימת לחשים ("לחשי כושף" etc.) | |
| Prepared spells | לחשים מוכנים | n/a (5.1 uses "מוכנים" in the Wizard page for prepared) | |
| Eldritch Invocations / Eldritch Blast | n/a | קריאות קמאיות / פרץ קמאי | Warlock-specific. |
| Pact Magic | תאי ברית | קסם ברית | |

## 6. Combat vocabulary

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Combat / encounter | קרב / מפגש קרב | קרב / התקלות קרבית | |
| Action | פעולה | פעולה | Same. |
| Bonus action | n/a (monster field "פעולות בונוס") | **פעולה נוספת** | Disagree. **Recommendation: adopt פעולה נוספת** (SRD text). |
| Reaction | תגובה / תגובות | פעולת תגובה (plural פעולות תגובה) | |
| Attack roll | n/a | גלגול התקפה | |
| Attack | n/a | התקפה | |
| Opportunity attack | n/a | התקפה מזדמנת | |
| Melee | n/a | קפא״פ (קרב פנים אל פנים), e.g. "התקפת נשק קפא״פ" | |
| Ranged | n/a | התקפת טווח / נשק טווח | |
| Cover | n/a | מחסה: חצי מחסה / שלושה רבעים (מחסה) / מחסה מלא | |
| Grapple | n/a | אחיזת יצור (verb: לאחוז; condition: אחוז) | |
| Shove | n/a | דחיפת יצור | |
| Dash | n/a | **מאוץ** | |
| Disengage | n/a | **נסיגה** | |
| Dodge | n/a | **חמיקה** | |
| Help | n/a | **עזרה לאחר** | |
| Hide | n/a | **התחבאות** | |
| Ready | "מתכונן לפעולה" (resx) | הכנת פעולה | |
| Search | n/a | **חיפוש** | |
| Use an Object | n/a | **שימוש בחפץ** | |
| Cast a Spell (action) | n/a | הטלת לחש | |
| Critical hit | פגיעה קריטית | פגיעה חמורה | Disagree. "קריטית" is the common loan; SRD uses "חמורה". Real choice; both understood. |
| Death saving throws | הטלות הצלה מפני מוות | זריקת הצלה ממוות | Aligned wording = זריקת הצלה. |
| Short rest / Long rest | מנוחה קצרה / מנוחה ארוכה | מנוחה קצרה / מנוחה ארוכה | Same. |
| Surprise | n/a | הפתעה | |
| Difficult terrain | n/a | תוואי שטח קשה | |
| Stabilize | n/a | יצוב יצור | |
| Temporary HP | נקודות חיים זמניות | נקודות פגיעה זמניות | See section 3. |
| Weapon mastery (5.2) | שליטה בנשק | n/a (5.2 only) | Note: "שליטה" = proficiency in the SRD, so "שליטה בנשק" would read as weapon proficiency. **Recommendation: rename to something else**, e.g. "מומחיות בנשק" is taken by expertise; "אומנות נשק" is a candidate. Needs a human decision. |
| Carrying/encumbrance | n/a | עומס | |

## 7. Equipment

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Equipment | ציוד | ציוד | Same. |
| Armor | שריון | שריון | Same. |
| Light / Medium / Heavy armor | שריון קל / בינוני / כבד | שריון קל / בינוני / כבד | Same. |
| Shield | מגן / מגנים | מגן | Same. |
| Stealth disadvantage | חיסרון בחמקנות | "התגנבות: חסרון" column | Ours says חמקנות; the Stealth skill is התגנבות in both. **Use התגנבות.** |
| Strength requirement | כוח מינימלי | "כוח 13" etc. | |
| Weapons | נשק | כלי נשק | Ours uses נשק; SRD כלי נשק (headings) and נשק (singular). |
| Simple / Martial weapons | n/a | נשקים פשוטים; (martial) נשקי ... קרביים; Simple ranged = נשקי טווח פשוטים | |
| Weapon properties | תכונות | מאפייני כלי נשק | "מאפיין" = feature in the SRD, used for weapon properties too. |
| Ammunition | n/a | תחמושת | |
| Finesse | n/a | מעודן | |
| Heavy | n/a | כבד | |
| Light | n/a | קל | |
| Loading | n/a | טעינה | |
| Range (weapon) | n/a | טווח | |
| Reach | n/a | הישג | |
| Special | n/a | מיוחד | |
| Thrown | n/a | הטלה | |
| Two-handed | n/a | דו-ידני | |
| Versatile | n/a | רב שימושי | |
| Adventuring gear | n/a | ציוד הרפתקנות | |
| Tools | מיומנות בכלי | כלים; Thieves' tools = כלי גנבים | |
| Mounts and vehicles | n/a | רמכים וכלי רכב | |
| Trade goods | n/a | סחורות | |
| Magic item | פריט קסום / פריטים קסומים | חפצי קסם (singular חפץ קסם) | Disagree: ours "פריט" vs SRD "חפץ". Either. |
| Rarity | נדירות: נפוץ / לא נפוץ / נדיר / נדיר מאוד / אגדי / חפץ עוצמה | n/a (rarity words not read) | |
| Attunement | דורש התאמה | n/a (not read) | |
| Currency | n/a | מ״ז (gold), מ״כ (silver), מ״נ (copper) | Prices like "5 מ״ז". The lifestyle table also uses פ״ז / פ״כ. |

## 8. Monster vocabulary

| English | Ours today (he) | 5esrd.co.il (he) | Notes |
|---|---|---|---|
| Creature type | סוג יצור | סוג | |
| Aberration | n/a | שיקוץ | Verified (Aboleth). |
| Beast | n/a | חיה | |
| Celestial | n/a | יצור מרום | |
| Construct | n/a | יציר כפיים | |
| Dragon | n/a | דרקון | |
| Elemental | n/a | יסודן | |
| Fey | n/a | יצור פייה | |
| Fiend | n/a | בן-תופת | |
| Giant | n/a | ענק | |
| Humanoid | n/a | דמוי אדם | |
| Monstrosity | n/a | תפלץ | |
| Ooze | n/a | רפש | |
| Plant | n/a | צמח | |
| Undead | n/a | אל-מת | |
| Swarm | n/a | נחיל | |
| Size | גודל | גודל | |
| Tiny | זעיר | קטנטן | |
| Small | קטן | קטן | |
| Medium | בינוני | בינוני | |
| Large | גדול | גדול | |
| Huge | **ענק** | **עצום** | Same words, shifted. |
| Gargantuan | **עצום** | **אדיר** | Ours "עצום" = SRD Huge. **Adopt SRD sizes** or mapping becomes confusing for readers. "ענק" in the SRD means Giant (creature type). |
| Alignment | נטייה | נטייה | |
| Lawful Good (LG) | טוב וחוקי | סדר טוב (ס״ט) | Disagree in word order and vocabulary. SRD uses סדר / תוהו for lawful / chaotic. |
| Neutral Good | טוב וניטרלי | ניטראלי טוב | |
| Chaotic Good | טוב וכאוטי | תוהו טוב | |
| Lawful Neutral | ניטרלי וחוקי | סדר ניטראלי | |
| Neutral | ניטרלי | ניטראלי | Spelling (ניטרלי vs ניטראלי). |
| Chaotic Neutral | ניטרלי וכאוטי | תוהו ניטראלי | |
| Lawful Evil | רע וחוקי | סדר רוע | |
| Neutral Evil | רע וניטרלי | ניטראלי רוע | |
| Chaotic Evil | רע וכאוטי | תוהו רוע | |
| Unaligned | ללא נטייה | חסר נטייה / חסרת נטייה | Either. |
| Challenge rating | דרגת אתגר | דירוג אתגר / אתגר (stat block line "אתגר 10 (5,900 נק״נ)") | |
| XP | ניסיון | נקודות ניסיון (נק״נ) | |
| Hit Dice | קוביות חיים | קוביות פגיעה | |
| Legendary actions | פעולות אגדיות | פעולות אגדתיות (stat blocks use "פעולות אגדיות" in the Aboleth page) | Both spellings appear in the SRD. |
| Lair / lair actions | n/a | מאורה / פעולות מאורה; regional effects = השפעות אזוריות | |
| Reactions / Multiattack | תגובות | פעולות תגובה / התקפות מרובות | |
| Traits (monster) | תכונות | מאפיינים מיוחדים | |
| Senses | חושים | חושים | |
| Darkvision | ראיית חושך | ראיית חושך | Same. |
| Blindsight | n/a | אל-ראייה | |
| Tremorsense | n/a | חוש-רעד | |
| Truesight | n/a | ראיית אמת | |
| Languages | שפות | שפות | |
| Telepathy | n/a | טלפתיה | |
| Speed types: burrow / climb / fly / swim | n/a | חפירה / טיפוס / מעוף / שחייה | |
| Resistances / Immunities / Vulnerabilities | עמידויות / חסינויות / פגיעויות | עמידות / חסינות / רגישות | See section 5. |
| Skills (monster) | מיומנויות | מיומנויות | |
| Gear | ציוד | ציוד | |

## 9. Dice, units and number formatting (exact SRD conventions)

| English | Ours today | 5esrd.co.il | Notes |
|---|---|---|---|
| Dice notation | the interface shows "1d10 + 3" in a placeholder (resx) | **ק** + number, no space: `1ק6`, `2ק6`, `8ק6`, `ק20`, `5ק12 + 10` | "ק" for "קובייה". Formula spacing: only around + (e.g. `5ק12 + 10`). Average then dice: `7 (2ק6)`. |
| Modifier sign | n/a | Trailing sign: `4+`, `2-`, `0+`, `5-`; table "ערך 18: 4+". In stat blocks: `19 (4+)`, skill bonus `הבחנה +4` and saves `חוסן 6+` (both orders occur) | Signs are written after the number in tables because of RTL. When text is stored it is in logical order, so `+4` and `4+` are different strings. Avoid copying either blindly; Codex should generate signs itself. |
| Feet to metres | Codex shows feet ("מהירות (רגל)", "ראיית חושך (רגל)") | metres (מ׳), at 5 ft = 1.5 m: 5 ft = `½1 מ׳`, 10 ft = `3 מ׳`, 15 ft = `½4 מ׳`, 25 ft = `½7 מ׳`, 30 ft = `9 מ׳`, 60 ft = `18 מ׳`, 120 ft = `36 מ׳`, 150 ft = `45 מ׳`; Tiny space (2.5 ft) = `75 ס״מ` | The SRD uses 0.3 m per foot with rounding to halves. |
| Fractions | n/a | `½` is typed BEFORE the whole number: `½1 מ׳` is displayed as 1½, `½4` as 4½, `½22 ק״ג` as 22½, bare half = `½` | Logical order is "fraction, then integer". This will confuse copy-paste or parsing. Codex should store numbers numerically. |
| Weapon range (normal/long) | n/a | `(טווח 18/6)` for a 20/60 ft weapon, i.e. long/normal in logical order (RTL reading gives 6 then 18). `(טווח 96/24)` for 80/320 ft | Easy to read backwards. State the order in UI copy. |
| Weight | משקל (ליברה) | ק״ג (kg) at 1 lb = 0.5 kg: 1 lb = `½ ק״ג`, 2 lb = `1 ק״ג`, 8 lb = `4 ק״ג`, 55 lb = `½27 ק״ג`; small items in grams: `100 ג׳` | |
| Distance (travel) | n/a | ק״מ, speeds `קמ״ש` | |
| Cost | מחיר | `5 מ״ז`, `2 מ״כ`, `1 מ״נ` | gold / silver / copper abbreviations. |
| Thousands | n/a | comma (`1,500 מ״ז`, `5,900 נק״נ`); one table has digits without comma (`1000`) | |
| Ranges in time | n/a | `1 דקה`, `10 דקות`, `1 פעולה`, `1 תגובה`, `1 פעולה נוספת`, `1 סיבוב`, `ריכוז, עד 10 דקות` | |
| DC | n/a | `בד״ק 13` ("at DC 13") | |
| Ability abbreviations | כוח / זרז / חוס / אינט / חכמ / קסם | כוח / זרז / חוסן / תבנ / חכמ / כרז | |
| Negative numbers / minus | n/a | hyphen after the number (`2-`, `5-`) | |

## Other sources

- Hamichlol (Hebrew wiki about D&D), search result for "שליט המבוך": the entry is reached by a redirect from "שליט המבוך" to the D&D article <https://www.hamichlol.org.il/%D7%9E%D7%91%D7%95%D7%9B%D7%99%D7%9D_%D7%95%D7%93%D7%A8%D7%A7%D7%95%D7%A0%D7%99%D7%9D>; the search summary reports the term "שליט המבוך (שה״מ)". When I fetched the page, the extract instead showed the generic phrase "מנחה המשחק" in prose for the host role, and "דמויות שחקן" for player characters. So Hamichlol is not consistent. The redirect page is <https://www.hamichlol.org.il/%D7%A9%D7%9C%D7%99%D7%98_%D7%94%D7%9E%D7%91%D7%95%D7%9A>.
- he.wikipedia.org, article "מבוכים ודרקונים" <https://he.wikipedia.org/wiki/מבוכים_ודרקונים>: fetch result gave **שליט המבוך (שה״מ)** for Dungeon Master. Its class links listed only Rogue = נוכל and Fighter = לוחם (the "bard" link label looked wrong in the fetch and is ignored).
- Wikipedia's general tabletop-RPG article calls the role "שליט המשחק" (GM), via the search summary of Hamichlol (not verified at the primary source).
- pundak.co.il: the SRD site itself names the Pundak product page of the Hebrew SRD translation (<https://www.pundak.co.il/dungeons-dragons/sourcebooks/Supplements/dnd5-hebrew-srd/detail>, from the 5esrd.co.il about text) and a DriveThruRPG/DMs Guild listing (<https://www.dmsguild.com/product/225851/SRD–Hebrew-translation>). WebSearch found only the forum thread <https://www.pundak.co.il/forums/threads/fizbans-treasury-of-dragons-%D7%94%D7%A1%D7%A4%D7%A8-%D7%94%D7%97%D7%93%D7%A9-%D7%A9%D7%9C-%D7%9E%D7%91%D7%95%D7%9B%D7%99%D7%9D-%D7%95%D7%93%D7%A8%D7%A7%D7%95%D7%A0%D7%99%D7%9D-5.44821/> and nothing on the DM term or class names. Not verified.
- Bard: a search for "בארד" / "פייטן" returned no usable source (only film pages, ynet <https://www.ynet.co.il/blazer/article/rkbqqbako>, which lists class examples without bard). The only Hebrew class-name evidence for bard is the SRD's פייטן and Codex's בארד, so that decision cannot be backed by an independent source here. Treat my recommendation as based on the SRD only.
- Not found: any independent source for sorcerer/warlock Hebrew names. Note that the Hamichlol and Wikipedia searches mostly returned Harry Potter hits for "מכשף" / "כושף".
