---
title: בונים מערכת משלכם
lead: מערכת משחק ב-TTRPG.Codex היא תיקייה של קובצי טקסט פשוטים. במדריך הזה בונים מערכת קטנה מאפס, שלב אחר שלב, וטוענים ובודקים אותה.
description: מדריך למפתחים לכתיבת מערכת משחק ב-TTRPG.Codex: קובץ system.yaml, שדות ופריסה של דף הדמות, ערכים מחושבים, רשימות, יכולות עם מתאמים, שימושים ובחירות, רגעים, תרגומים, בדיקה, טעינה וטעויות נפוצות.
---

> [!NOTE]
> טיוטת תרגום מכונה: דובר שפת אם עדיין לא קרא את הדף. בלוקי הקוד זהים לגרסה האנגלית, והטקסט האנגלי הוא הקובע.

אין צורך לכתוב קוד. אם אתם יכולים לקרוא קובץ YAML, אתם יכולים לתאר משחק: המספרים בדף הדמות, מה שהשחקנים בוחרים ממנו, ומה עושה כל בחירה. האפליקציה קוראת את הקבצים האלה ובונה מהם את הדף, את הספרייה ואת השולחן.

זהו מדריך צעד אחר צעד. לכל מפתח, עם הסוג וברירת המחדל שלו, יש את [המפרט המלא](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/SYSTEM_YAML_REFERENCE.md). הוא נוצר מהאפליקציה עצמה, ולכן הוא תמיד מדויק.

## מהי מערכת {#what}

מערכת היא תיקייה תחת `content/systems/`. בה קובץ `system.yaml` אחד, ולצדו כל מספר של **חבילות תוכן**.

| חלק | מה זה |
| --- | --- |
| `system.yaml` | החוקים כנתונים: מה דף הדמות מכיל, איך מחשבים את המספרים שבו, אילו סוגי רשומות יש בספרייה, ואילו רגעים חשובים למשחק. |
| `system.ru.yaml`, `system.he.yaml` | אופציונלי. המילים של אותה מערכת בשפה אחרת. |
| `packs/<שם>/` | רשומות הספרייה: מקצועות, כשרונות, לחשים, מפלצות, כל מה שיש במערכת שלכם. כל רשומה היא קובץ YAML קטן. |
| `spec.yaml` | אופציונלי. בדיקות שאתם כותבים, למשל "הדף הזה צריך להסתכם ב-8". |

באפליקציה יש שתי מערכות שאפשר לקרוא תוך כדי עבודה. הן הדוגמאות הטובות ביותר, כי הן אמיתיות:

- מערכת SRD 5.2.1: `content/systems/srd5e/system.yaml`
- מערכת Forged in the Dark SRD: `content/systems/fitd-srd/system.yaml`

הקטעים שלהלן לקוחים מהן כשזה מתאים, וממשחק קטן ומומצא בשם **Lantern Run** שאנחנו בונים יחד.

> [!NOTE]
> מערכת מתארת חוקים, לא ספר. כתבו במילים שלכם, ואל תעתיקו טקסט, שמות או עולם של מו״ל אחר. אם המערכת שלכם מבוססת על מסמך מורשה, ציינו זאת בבלוק `license`, כפי שעושות המערכות המובנות.

## מתחילים בתיקייה {#folder}

צרו את התיקייה הזו ליד המערכות המובנות:

```text
content/systems/lantern-run/
  system.yaml
  packs/
    core/
      manifest.yaml
      talents/
        deep-pockets.yaml
```

כל מה שהטוען צריך נמצא ב-`system.yaml`. מתחילים בלומר מי המערכת:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/myarichuk/TTRPG.Codex/main/docs/schema/system.schema.json
id: lantern-run
name: Lantern Run
version: 0.1.0
description: >-
  A small dice game about delving into old tunnels with a single lantern between you.
license:
  name: CC0-1.0
  url: https://creativecommons.org/publicdomain/zero/1.0/
```

השורה הראשונה היא הערה, וכדאי לשמור עליה. עורך עם שרת שפה של YAML (VS Code עם תוסף YAML, או עורך של JetBrains) קורא אותה, משלים מפתחות תוך כדי הקלדה ומסמן בקו תחתון מפתח עם שגיאת כתיב.

רק `id` ו-`name` הם חובה. ה-`id` הוא מפתח המערכת: אותיות קטנות, ספרות, `-` או `_`. קמפיינים זוכרים אותו, אז בחרו פעם אחת ואל תשנו לעולם. את `version` צריך לשנות: כשהחוקים משתנים, העלו אותה, ושליט המבוך של כל קמפיין יראה מה השתנה.

## קובעים אילו רשומות יש {#types}

חבילות מכילות רשומות ספרייה, ו-`entityTypes` קובע אילו סוגים יכולים להיות. ב-Lantern Run יש סוג אחד, הכשרון:

```yaml
entityTypes:
  - { type: talent, label: Talent, pluralLabel: Talents, folder: talents, playerFacing: true }

conditions: [Shaken, Lost]
```

`folder` הוא התיקייה בתוך החבילה שבה נמצאות רשומות מהסוג הזה. `playerFacing: true` מאפשר לשחקנים לראות אותן בבוררים שלהם. `conditions` ממלא את בורר המצבים בשולחן החי.

מערכת Forged in the Dark SRD עושה אותו דבר, ולחלק מהסוגים יש שדות נוספים:

```yaml
entityTypes:
  - { type: crew-upgrade, label: Crew upgrade, pluralLabel: Crew upgrades, folder: crew-upgrades, fields: [{ key: cost, label: Upgrade boxes, type: Number }] }
```

## מוסיפים דף דמות {#sheet}

**סוג דף** הוא סוג אחד של דף דמות. במערכת יכולים להיות כמה: במערכת Forged in the Dark SRD יש דף נוכל ודף צוות. אצלנו יש אחד, הצוללן. כל מה שקשור לדף נמצא תחתיו.

```yaml
sheetKinds:
  - kind: delver
    label: Delver
    role: player
```

### קבוצה של מיומנויות {#skills}

**רשימה ממופתחת** היא קבוצה קבועה של דברים בעלי שם, ולכל אחד שדות משלו. מיומנויות מתאימות לזה היטב. כאן לכל מיומנות יש דירוג מ-0 עד 3, שמצויר בנקודות, ו-`group` שנשתמש בו עוד מעט:

```yaml
    keyedLists:
      - key: skills
        label: Skills
        itemFields: [{ key: rating, type: Number, min: 0, max: 3, display: dots }]
        keys:
          - { key: climb,  label: Climb,  group: body }
          - { key: fight,  label: Fight,  group: body }
          - { key: sneak,  label: Sneak,  group: wits }
          - { key: tinker, label: Tinker, group: wits }
```

במערכת Forged in the Dark SRD יש שתים-עשרה פעולות בדיוק בצורה הזו.

### ערך מחושב {#derived}

**ערך** הוא מספר בדף. תנו לו `formula` והאפליקציה תחשב אותו מחדש בכל שינוי. נוסחאות קוראות ערכים אחרים, רשימות ממופתחות ורשימות. זו סופרת את המיומנויות שיש בהן לפחות נקודה אחת:

```yaml
    values:
      - { key: trained, label: Trained skills, formula: "count(skills, s => s.rating >= 1)" }
      - { key: packMax, label: Pack slots, formula: "4 + trained", modifiers: packMax }
```

`packMax` מתחיל ב-4 ומוסיף אחד על כל מיומנות מאומנת. השורה `modifiers: packMax` אומרת שכל דבר אחר בדף שמכוון אל `packMax` נוסף מעליו. נשתמש בזה עוד מעט.

במערכת SRD 5.2.1 אותו רעיון כתוב כנוסחה של שורה אחת:

```yaml
    - { key: proficiencyBonus, label: Proficiency Bonus, formula: "2 + (level - 1) / 4", category: Combat }
```

החשבון הוא במספרים שלמים. `floor`, `ceil`, `max` ו-`min` זמינים כשצריך.

### דברים שעולים ויורדים {#tracks}

**מד** הוא מספר שמשתנה במשחק ויש לו גבולות: נקודות חיים, לחץ, שמן בפנס.

```yaml
    tracks:
      - { key: lantern, label: Lantern oil, max: 6, start: max, display: boxes }
      - { key: nerve, label: Nerve, max: 5, display: boxes }
```

מד יכול לעשות יותר מלספור. במערכת Forged in the Dark SRD מד לחץ מלא מתאפס ומוסיף טראומה אחת, והשחקן בוחר איזו:

```yaml
      - key: stress
        label: Stress
        max: 9
        display: boxes
        overflow: { into: trauma, by: 1, then: reset, ask: { who: player, choices: [Cold, Haunted, Obsessed, Paranoid, Reckless, Soft, Unstable, Vicious] } }
```

### פורסים את הדף {#layout}

בלי `layout` האפליקציה מציגה כל ערך, מד ורשימה בזה אחר זה. פריסה מחלקת אותם ללשוניות, כל אחת מקטע עם וידג׳טים:

```yaml
    layout:
      - { section: Skills, widgets: [{ widget: table, key: skills, groupBy: group, columns: [rating] }, { widget: values, keys: [trained, packMax] }] }
      - { section: Condition, widgets: [{ widget: track, key: lantern }, { widget: track, key: nerve }, { widget: moments }] }
      - { section: Talents, widgets: [{ widget: list, key: talents }, { widget: features }] }
```

הווידג׳טים שכאן הם `table`, `values`, `track`, `list`, `moments` ו-`features`. בכל מקטע צריך לפחות וידג׳ט אחד. השאר מפורטים במפרט.

## נותנים לדף משהו לבחור {#features}

**רשימה** מכילה בחירות מהספרייה: מקצועות, כשרונות, לחשים. **מקור יכולות** אומר שלרשומות שנבחרו ברשימה יש חוקים משלהן. הוסיפו את שניהם לצוללן:

```yaml
    features:
      - { list: talents }

    lists:
      - key: talents
        label: Talents
        type: talent
```

עכשיו החוקים לא נמצאים ב-`system.yaml` בכלל. הם נמצאים בכל כשרון, בקובץ משלו תחת `properties`. יכולה לעשות שלושה דברים.

### לשנות מספר: `modifiers` {#modifiers}

`packs/core/talents/deep-pockets.yaml`:

```yaml
id: deep-pockets
type: talent
name: Deep pockets
description: You always find room for one more thing.
properties:
  modifiers:
    - target: packMax
      amount: 2
```

למי שבוחר את הכשרון הזה יש שני מקומות נוספים בתרמיל. ה-`target` הוא מפתח של ערך. ה-`amount` הוא מספר או נוסחה. היכולת Mule במערכת Forged in the Dark SRD היא אותו קובץ עם יעד אחר ובונוס קטן יותר:

```yaml
properties:
  modifiers:
  - target: LoadMax
    amount: 2
```

### לתת משהו להוציא: `uses` {#uses}

**שימוש** הוא מונה בדף שיכולת נותנת, כמו "פעם אחת בכל חניה". החלק `recovers` אומר מתי הוא מתמלא מחדש:

```yaml
id: second-wind
type: talent
name: Second wind
description: Once per camp, shake off a scare.
properties:
  uses:
    key: second-wind
    max: 1
    recovers:
      - on: camp
```

`camp` אינו מובנה. נצהיר עליו בשלב הבא. גם מנוחה, `initiative` ו-`dawn` עובדים ב-`on`. בזעם במערכת SRD 5.2.1 חלק מהשימושים חוזרים אחרי מנוחה אחת וכולם אחרי אחרת, והמקסימום הוא נוסחה של דרגת הדמות:

```yaml
    uses:
      key: rage
      max: 'classLevel >= 17 ? 6 : classLevel >= 12 ? 5 : classLevel >= 6 ? 4 : classLevel >= 3 ? 3 : 2'
      recovers:
      - on: Short Rest
        amount: 1
      - on: Long Rest
```

### נותנים לשחקן לבחור: `adds` {#choice}

שורת `adds` נותנת משהו לדף, וכשיש בה `choice`, השחקן מחליט מה. הכשרון הזה נותן לשחקן לבחור מיומנות אחת:

```yaml
id: specialist
type: talent
name: Specialist
description: Pick one skill. You are known for it.
properties:
  adds:
    - to: skills
      choice: specialty
      choose: 1
```

היכולת Expertise במערכת Forged in the Dark SRD כתובה בדיוק כך. ה-`choice` הוא שם משלכם לבחירה. הדף שומר אותו, מציג בורר, ואומר לשחקן כל עוד הבחירה פתוחה.

בחירה יכולה גם לבחור רשומות ספרייה ולקרוא בחירה קודמת. זה הכשרון Magic Initiate ממערכת SRD 5.2.1, מקוצר: קודם בוחרים רשימת לחשים, ואז שני לחשי יסוד ממנה.

```yaml
  adds:
  - choice: spell-list
    choose: 1
    from:
    - Cleric
    - Druid
    - Wizard
  - to: grantedSpells
    choice: cantrips
    choose: 2
    where:
    - property: level
      equals: 0
    - property: classes
      containsChoice: spell-list
```

לשורה הראשונה אין `to`, ולכן היא לא מוסיפה דבר לדף: היא רק ערך ששורות אחרות יכולות לקרוא. כתבו אותה לפני השורות שתלויות בה.

## מוסיפים רגע {#moments}

**רגע** הוא זמן בעל שם שבו מונים חוזרים. מנוחות, `initiative` ו-`dawn` כבר קיימים. הצהירו על רגע משלכם ב-`system.yaml`:

```yaml
moments:
  - { key: camp, label: Camp }
```

זה מספיק כדי ש-`second-wind` יתמלא מחדש. הווידג׳ט `moments` בפריסה שלנו נותן לדף כפתור **Camp**, והלחיצה עליו ממלאת מחדש כל מונה עם `on: camp`.

רגע יכול גם להופעל מעצמו כשערך משתנה. רגע הטעינה במערכת Forged in the Dark SRD מופעל כשהשחקן קובע את הערך `load` של הדף:

```yaml
moments:
  - { key: downtime, label: Downtime }
  - { key: loadChosen, label: Load chosen, onSet: load }
```

## המניפסט של החבילה {#manifest}

לכל חבילה יש `manifest.yaml` שאומר לאיזו מערכת היא שייכת:

```yaml
id: lantern-core
name: Lantern Run talents
version: 0.1.0
systemId: lantern-run
kind: rules
license:
  name: CC0-1.0
  url: https://creativecommons.org/publicdomain/zero/1.0/
```

`systemId` חייב להתאים ל-`id` של המערכת. בלעדיו אין לחבילה לאן ללכת.

## מתרגמים {#translations}

תרגום לעולם לא משנה את החוקים. מפתחות, מזהים ונוסחאות נשארים באנגלית, ורק המילים שאנשים קוראים מתורגמות.

שימו את המילים של המערכת בקובץ אח על שם השפה, `system.ru.yaml`. הוא משתמש באותם מפתחות, והתוויות של הדף עצמו (הסוג, המיומנויות, המדדים והמקטעים) הולכות תחת `values`, ונמצאות לפי המילה האנגלית שלהן:

```yaml
name: Забег с фонарём
description: Небольшая игра на кубах о спуске в старые туннели с одним фонарём на всех.
entityTypes:
  talent:
    label: Талант
    pluralLabel: Таланты
conditions:
  Shaken: Напуган
  Lost: Заблудился
values:
  Delver: Исследователь
  Skills: Навыки
  Climb: Лазание
  Lantern oil: Масло в фонаре
  Camp: Привал
```

הרשומות מקבלות **חבילת תרגום**, תיקיית אח של החבילה שהיא מתרגמת. יש בה אותן תיקיות סוגים, וכל קובץ מונה רשומות לפי `id` ורק את הטקסט להצגה:

```yaml
id: lantern-core-ru
name: Lantern Run talents, Русский
version: 0.1.0
systemId: lantern-run
kind: translation
locale: ru
translationOf: lantern-core
dependencies: [lantern-core]
```

`packs/core-ru/talent/talent-01.yaml`:

```yaml
- id: deep-pockets
  name: Глубокие карманы
  description: В вашем снаряжении всегда найдётся место ещё для одной вещи.
```

עברית היא אותו דבר עם `system.he.yaml`. האפליקציה מציגה את הדף מימין לשמאל מעצמה.

> [!TIP]
> תרגום מכונה הוא טיוטה ראשונה טובה. כתבו זאת בתיאור החבילה, ובקשו מדובר שפת אם לקרוא אותו לפני ששיתפתם.

## בודקים {#check}

אין צורך להפעיל את האפליקציה כדי לגלות אם מערכת נטענת. הבודק טוען אותה כמו השרת ומדפיס מה לא בסדר. נדרשים .NET SDK ועותק של המאגר:

```bash
dotnet run --project tools/Codex.SystemCheck -- \
  content/systems/lantern-run
```

הרצה טובה מדפיסה `LOADED`. כל דבר שלא מוצא חן בעיניו הוא שורת `PROBLEM`: מפתח שהאפליקציה לא קוראת, סקריפט חסר, רשימה שלא קיימת. תקנו כל אחת. מפתח עם שגיאת כתיב פשוט מתעלמים ממנו, והוא אף פעם לא שגיאה, ולכן כך מוצאים אותו.

אפשר גם לכתוב ציפיות, בדיקות קטנות ב-`spec.yaml` שהבודק מריץ בשבילכם:

```yaml
system: lantern-run
sheets:
  mara:
    kind: delver
    lists:
      skills: { climb: { rating: 2 }, sneak: { rating: 1 } }
      talents: [{ id: deep-pockets }]
expectations:
  - id: lantern.trained-counts-skills-with-dots
    sheet: mara
    expect: { values: { trained: 2 } }
  - id: lantern.deep-pockets-adds-two-slots
    sheet: mara
    expect: { values: { packMax: 8 } }
```

למארה יש שתי מיומנויות מאומנות, ולכן התרמיל שלה הוא 4 + 2, ו-Deep pockets מוסיף עוד 2. הבודק מדפיס `PASS` על כל ציפייה שמתקיימת, ו-`FAIL` עם מה שיצא במקום, על כל ציפייה שלא.

## טוענים באפליקציה {#load}

יש שתי דרכים, לפי האופן שבו מפעילים את האפליקציה.

**מקוד המקור.** האפליקציה קוראת את התיקייה `content/` של המאגר, ולכן תיקייה ששמתם שם היא מערכת. בפיתוח האפליקציה עוקבת אחרי התיקייה וטוענת מחדש מעצמה כמה שניות אחרי השמירה. אם זה לא קרה, פתחו את [[ui:Page.About.Title]] ולחצו על [[ui:Page.About.Reload]].

**אפליקציה מותקנת.** צרו קובץ zip של תיקיית המערכת, ואז ב-[[ui:Page.About.Title]] השתמשו ב-[[ui:Page.About.Install]] ובחרו את הקובץ. הבודק יוצר את ה-zip בשבילכם:

```bash
dotnet run --project tools/Codex.SystemCheck -- \
  content/systems/lantern-run --pack lantern-run.zip
```

הדף אומר מה ה-zip מחליף ומה השתנה עוד לפני שמשהו מותקן, והתקנה שלא נטענת מוחזרת למצבה הקודם. כברירת מחדל רק מנהל השרת יכול להתקין.

אחר כך לחצו על [[ui:Page.Home.New]], תנו שם לקמפיין ובחרו את המערכת שלכם. פתחו דף ונסו:

1. תנו לצוללן כמה נקודות במיומנויות וראו איך "Trained skills" ו-"Pack slots" משתנים.
2. הוסיפו את הכשרון "Deep pockets". מקומות התרמיל עולים בשניים.
3. הוסיפו את "Specialist" ובחרו מיומנות כשהדף מבקש.
4. הוסיפו את "Second wind", השתמשו בו, ואז לחצו על **Camp** וראו אותו חוזר.

כשאתם משנים את החוקים, העלו את `version`. שליט המבוך של הקמפיין יראה מה השתנה בפעם הבאה שיפתח אותו.

## טעויות נפוצות {#mistakes}

- **מפתח עם שגיאת כתיב מתעלמים ממנו בשקט.** `moddifiers` לא נכשל, הוא פשוט לא עושה כלום. הריצו את הבודק אחרי כל שינוי, והשאירו את הערת הסכמה כדי שהעורך יתפוס אותה קודם.
- **שינוי ה-`id`.** קמפיינים מוצאים את המערכת שלהם לפיו. את `name` אפשר לשנות כרצונכם, את `id` לעולם לא.
- **שכחתם להעלות את `version`.** שחקנים ושליטי מבוך לא יכולים לדעת שהחוקים השתנו.
- **מתאם שאף דבר לא קורא.** ה-`target` של יכולת חייב להיות מפתח של ערך, וערך קולט מתאמים רק כשהוא מציין את המפתח של עצמו ב-`modifiers`, כמו `packMax` למעלה. בלי השורה הזו הכשרון נטען כרגיל ולא משנה דבר.
- **מצהירים על רגע ולא משתמשים בו, או משתמשים באחד שלא הוצהר.** ל-`recovers: [{ on: camp }]` צריך את `camp` ב-`moments` (או מנוחה בשם הזה).
- **מקטע פריסה בלי וידג׳טים.** בכל מקטע צריך לפחות אחד.
- **`choice` שקורא בחירה מאוחרת יותר.** כתבו קודם את הבחירה שאחרות קוראות.
- **מתרגמים מפתחות.** ב-`system.ru.yaml` ובחבילת תרגום, מזהים, מפתחות, נוסחאות ו-`target` נשארים באנגלית. רק טקסט להצגה משתנה. ורשימת שורות מתורגמת חייבת להכיל כל שורה, באותו סדר.
- **חבילת תרגום בתוך החבילה שהיא מתרגמת.** שימו אותה בתיקיית אח.
- **מעתיקים טקסט של מו״ל.** חוקים אינם המילים שמישהו כתב עליהם. כתבו במילים שלכם ושמרו על בלוק הרישיון ישר.

## לאן הלאה {#next}

- [המפרט המלא של `system.yaml`](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/SYSTEM_YAML_REFERENCE.md), כל מפתח וסוג.
- [מערכות וחבילות תוכן](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/CONTENT_PACKS.md), פורמט החבילה ואיך עובדות הטעינה והגרסאות.
- שתי המערכות המובנות, שמשתמשות כמעט בכל מה שתואר כאן.
- [דיווח חדש](https://github.com/myarichuk/TTRPG.Codex/issues) אם חוק שאתם רוצים עדיין אי אפשר לכתוב כנתונים.
