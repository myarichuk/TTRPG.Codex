# Encounter builder: Russian and Hebrew review list

The Russian and Hebrew words the encounter builder added (ENCOUNTER_BUILDER_DESIGN.md, sections 13 to 20), for a native speaker to confirm or replace. All of it is machine-drafted. Each row is a resource key, the
English, the Russian and the Hebrew; the files are `src/Codex.Localization/Resources/Strings.<Area>.ru.resx` and `.he.resx`, the area being the key's first part (`Encounters.` is `Strings.Encounters*.resx`).
`{0}`, `{1}` stand for a name, a number or a date the app puts in; `{0, plural, one{...} other{...}}` picks a form by the number (Russian has one, few, many and other; Hebrew one, two and other, and `#` is the number).

Terms used throughout (see [i18n-glossary.md](i18n-glossary.md) and [i18n-he-terms.md](i18n-he-terms.md)):

| English | Russian | Hebrew | To confirm |
|---|---|---|---|
| encounter | столкновение | מפגש קרב | Hebrew «מפגש» alone is a session, so the encounter is always «מפגש קרב». The table's «התחלת עימות» was changed to «התחלת מפגש קרב». |
| hazard | опасность | סכנה | |
| place | место | מקום | |
| band (Low, Moderate, High) | порог (Низкая, Умеренная, Высокая) | טווח (נמוכה, בינונית, גבוהה) | The Russian labels are feminine, agreeing with «сложность»; the Hebrew with «רמת קושי». |
| level | уровень | דרגה | The sheet's own word. «רמת קושי» (difficulty) and «דרגת קושי» (a DC) are kept apart on purpose. |
| party | отряд | קבוצה | |
| Library | Библиотека | ספרייה | |
| the GM | мастер | מנחה המשחק | «мастер» only for the GM, never for a builder or wizard. |

Where a Hebrew sentence names a place the player typed, it says «במקום {0}» (or «מתוך {0}») rather than gluing a prefix letter to the name, so a Latin name reads right in a right-to-left line. A date takes «ב-{1}».

## Hazard names

The names of the hazards the Library shows in Russian and Hebrew: `codex-hazards-ru` and `-he` (our own two, MIT; also their descriptions, triggers, effects, resets, durations and the words of their ways to find, disarm and handle them,
in `content/systems/srd5e/packs/codex-hazards-<lang>/hazard/hazards.yaml`) and the eight SRD hazards, named only, in `srd-5.2.1-ru` and `-he` (`hazard/hazards.yaml`). Checks, DCs, dice, conditions and ids are the English ones.

| Id | English | Russian | Hebrew |
|---|---|---|---|
| `homebrew-cursed-doorway` | Cursed Doorway | Проклятый дверной проём | פתח מקולל |
| `homebrew-flooding-room` | Flooding Room | Затопляемая комната | חדר מוצף |
| `collapsing-roof` | Collapsing Roof | Обрушивающийся потолок | תקרה קורסת |
| `falling-net` | Falling Net | Падающая сеть | רשת נופלת |
| `fire-casting-statue` | Fire-Casting Statue | Статуя, мечущая огонь | פסל מטיל אש |
| `hidden-pit` | Hidden Pit | Скрытая яма | בור נסתר |
| `poisoned-darts` | Poisoned Darts | Отравленные дротики | חיצי זריקה מורעלים |
| `poisoned-needle` | Poisoned Needle | Отравленная игла | מחט מורעלת |
| `rolling-stone` | Rolling Stone | Катящийся камень | אבן מתגלגלת |
| `spiked-pit` | Spiked Pit | Яма с шипами | בור עם דוקרנים |

«חיצי זריקה» follows the Hebrew pack's name for a dart («חץ זריקה»). The longer texts of the two homebrew hazards are in their pack files for review.

## The encounter list

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Encounters.AllPlaces` | All places | Все места | כל המקומות |
| `Encounters.AllStatuses` | All statuses | Все статусы | כל המצבים |
| `Encounters.BuildWithAi` | Build with AI | Собрать с помощью ИИ | בנייה באמצעות AI |
| `Encounters.Count` | {0, plural, one{# encounter} other{# encounters}} | {0, plural, one{# столкновение} few{# столкновения} many{# столкновений} other{# столкновения}} | {0, plural, one{מפגש קרב אחד} two{שני מפגשי קרב} other{# מפגשי קרב}} |
| `Encounters.DmOnly` | Only the GM prepares encounters. | Столкновения готовит только мастер. | רק מנחה המשחק מכין מפגשי קרב. |
| `Encounters.Empty.Body` | An encounter is a fight you prepare before the session: where it happens, which creatures are in it, and what to remember when it starts. | Столкновение — это бой, который вы готовите до сессии: где он происходит, какие существа в нём участвуют и что не забыть в его начале. | מפגש קרב הוא קרב שמכינים לפני המפגש: איפה הוא מתרחש, אילו יצורים משתתפים בו ומה כדאי לזכור כשהוא מתחיל. |
| `Encounters.Empty.Title` | No encounters yet | Столкновений пока нет | אין עדיין מפגשי קרב |
| `Encounters.Error.Load` | The encounters could not be loaded. Try again. | Не удалось загрузить столкновения. Попробуйте ещё раз. | לא ניתן היה לטעון את מפגשי הקרב. נסו שוב. |
| `Encounters.FilterPlace` | Filter by place | Фильтр по месту | סינון לפי מקום |
| `Encounters.FilterStatus` | Filter by status | Фильтр по статусу | סינון לפי מצב |
| `Encounters.New` | New encounter | Новое столкновение | מפגש קרב חדש |
| `Encounters.NewTitle` | New encounter | Новое столкновение | מפגש קרב חדש |
| `Encounters.NoCreatures` | No creatures yet | Существ пока нет | אין עדיין יצורים |
| `Encounters.NoMatch` | No encounter matches. | Ни одно столкновение не подходит. | אין מפגש קרב מתאים. |
| `Encounters.NoPlace` | No place | Без места | ללא מקום |
| `Encounters.PlaceGone` | Its place was deleted | Его место удалено | המקום שלו נמחק |
| `Encounters.RanOn` | {0, plural, one{Ran on {1}} other{Ran # times, last on {1}}} | {0, plural, one{Проведено # раз, последний раз {1}} few{Проведено # раза, последний раз {1}} many{Проведено # раз, последний раз {1}} other{Проведено # раза, последний раз {1}}} | {0, plural, one{התקיים ב-{1}} two{התקיים פעמיים, לאחרונה ב-{1}} other{התקיים # פעמים, לאחרונה ב-{1}}} |
| `Encounters.RanTimes` | {0, plural, one{Ran once} other{Ran # times}} | {0, plural, one{Проведено # раз} few{Проведено # раза} many{Проведено # раз} other{Проведено # раза}} | {0, plural, one{התקיים פעם אחת} two{התקיים פעמיים} other{התקיים # פעמים}} |
| `Encounters.Search` | Search encounters by name | Поиск столкновений по названию | חיפוש מפגשי קרב לפי שם |
| `Encounters.SearchPlaceholder` | Search by name | Поиск по названию | חיפוש לפי שם |
| `Encounters.Status.Draft` | Draft | Черновик | טיוטה |
| `Encounters.Status.Ran` | Ran | Проведено | התקיים |
| `Encounters.Status.Ready` | Ready | Готово | מוכן |
| `Encounters.Subtitle` | Fights you prepare before the session. | Бои, которые вы готовите до сессии. | קרבות שמכינים לפני המפגש. |
| `Encounters.Title` | Encounters | Столкновения | מפגשי קרב |

## The editor: details, saving, deleting, checks

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Encounters.BackToList` | Back to encounters | К столкновениям | חזרה למפגשי הקרב |
| `Encounters.Breadcrumb` | Breadcrumb | Путь по разделам | נתיב ניווט |
| `Encounters.Cancel` | Cancel | Отмена | ביטול |
| `Encounters.Check.CastCount` | {0} is one person, so the count must be 1. | {0} — один персонаж, поэтому число должно быть 1. | {0} הוא דמות אחת, ולכן הכמות חייבת להיות 1. |
| `Encounters.Check.CastIsPlayer` | {0} is a player character or a companion. Only characters the GM runs can be in an encounter. | {0}: персонаж игрока или его спутник. В столкновении могут быть только персонажи, которыми управляет мастер. | {0}: דמות של שחקן או בן לוויה שלה. רק דמויות שמנחה המשחק מנהל יכולות להיות במפגש קרב. |
| `Encounters.Check.CountRange` | {0}: the count must be from 1 to {1}. | {0}: число должно быть от 1 до {1}. | {0}: הכמות צריכה להיות בין 1 ל-{1}. |
| `Encounters.Check.Errors` | Fix these before saving | Исправьте это перед сохранением | יש לתקן לפני השמירה |
| `Encounters.Check.HazardLine` | {0} is a hazard, not a creature. Add a complex hazard with Hazards in the add panel, or place it on the place's page. | {0}: это опасность, а не существо. Сложную опасность добавьте через «Опасности» в панели добавления, остальные поставьте на странице места. | {0}: זו סכנה ולא יצור. סכנה מורכבת מוסיפים דרך "סכנות" בחלונית ההוספה, ואת האחרות מציבים בדף המקום. |
| `Encounters.Check.NoName` | Give the encounter a name. | Дайте столкновению название. | תנו שם למפגש הקרב. |
| `Encounters.Check.Warnings` | Worth a look | Стоит обратить внимание | כדאי לשים לב |
| `Encounters.Delete` | Delete encounter | Удалить столкновение | מחיקת מפגש הקרב |
| `Encounters.Delete.Body` | This deletes the encounter and cannot be undone. | Столкновение будет удалено, вернуть его нельзя. | מפגש הקרב יימחק ולא יהיה אפשר להחזיר אותו. |
| `Encounters.Delete.Title` | Delete {0}? | Удалить «{0}»? | למחוק את {0}? |
| `Encounters.Deleted` | Deleted {0} | Удалено: {0} | {0} נמחק |
| `Encounters.Details` | Details | Основное | פרטים |
| `Encounters.EditTitle` | Edit {0} | Изменить: {0} | עריכת {0} |
| `Encounters.Error.NotAllowed` | Only the GM can do this. | Это может делать только мастер. | רק מנחה המשחק יכול לעשות זאת. |
| `Encounters.Error.NotSaved` | The encounter could not be saved. Reload the page and try again. | Не удалось сохранить столкновение. Обновите страницу и попробуйте ещё раз. | לא ניתן היה לשמור את מפגש הקרב. טענו את הדף מחדש ונסו שוב. |
| `Encounters.Field.Name` | Name | Название | שם |
| `Encounters.Field.Notes` | Notes | Заметки | הערות |
| `Encounters.Field.Place` | Place | Место | מקום |
| `Encounters.Field.Status` | Status | Статус | מצב |
| `Encounters.Missing.Body` | It may have been deleted. | Возможно, его удалили. | ייתכן שהוא נמחק. |
| `Encounters.Missing.Title` | Encounter not found | Столкновение не найдено | מפגש הקרב לא נמצא |
| `Encounters.NotDeleted` | The encounter could not be deleted. | Не удалось удалить столкновение. | לא ניתן היה למחוק את מפגש הקרב. |
| `Encounters.NotSaved` | The encounter was not saved. | Столкновение не сохранено. | מפגש הקרב לא נשמר. |
| `Encounters.NotesHint` | Only you see these. Tactics, terrain, what sets it off, and how it can end. | Видны только вам. Тактика, местность, что запускает бой и чем он может закончиться. | רק אתם רואים אותן. טקטיקה, שטח, מה מפעיל את הקרב ואיך הוא יכול להסתיים. |
| `Encounters.Required` | (required) | (обязательно) | (חובה) |
| `Encounters.Save` | Save encounter | Сохранить столкновение | שמירת מפגש הקרב |
| `Encounters.Saved` | Saved {0} | Сохранено: {0} | {0} נשמר |
| `Encounters.Start` | Start at the table | Начать за столом | התחלה ליד השולחן |
| `Encounters.Start.NoTable` | Start the table to run this encounter | Запустите стол, чтобы провести это столкновение | הפעילו את השולחן כדי לקיים את מפגש הקרב הזה |
| `Encounters.Start.SaveFirst` | Save your changes to start it. | Сохраните изменения, чтобы начать. | שמרו את השינויים כדי להתחיל. |
| `Encounters.StartsHidden` | Starts hidden | Скрыт в начале | מתחיל מוסתר |
| `Encounters.StartsHiddenFor` | Starts hidden: {0} | Скрыт в начале: {0} | מתחיל מוסתר: {0} |
| `Encounters.Status.RanReadOnly` | Ran. Starting it at the table sets this, so you cannot change it here. | Проведено. Этот статус ставится, когда столкновение начинают за столом, поэтому здесь его не изменить. | התקיים. המצב הזה נקבע כשמתחילים את מפגש הקרב ליד השולחן, ולכן אי אפשר לשנות אותו כאן. |
| `Encounters.Warn.CastGone` | {0} is no longer in the cast. | {0} больше нет среди персонажей. | {0} כבר לא בין הדמויות. |
| `Encounters.Warn.EntryGone` | {0} is no longer in the Library. | {0} больше нет в Библиотеке. | {0} כבר לא בספרייה. |
| `Encounters.Warn.PlaceGone` | The place was deleted. | Место удалено. | המקום נמחק. |

## The editor: the lineup

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Encounters.Added` | Added {0} | Добавлено: {0} | {0} נוסף |
| `Encounters.AddedCount` | Added {0} ({1}) | Добавлено: {0} ({1}) | {0} נוסף ({1}) |
| `Encounters.AddedShortlist` | {0, plural, one{# creature added from the shortlist} other{# creatures added from the shortlist}} | {0, plural, one{Добавлено # существо из списка} few{Добавлено # существа из списка} many{Добавлено # существ из списка} other{Добавлено # существа из списка}} | {0, plural, one{נוסף יצור אחד מהרשימה הקצרה} two{נוספו שני יצורים מהרשימה הקצרה} other{נוספו # יצורים מהרשימה הקצרה}} |
| `Encounters.AlreadyThere` | {0} is already in this encounter | {0} уже в этом столкновении | {0} כבר במפגש הקרב הזה |
| `Encounters.CastMember` | Cast member | Из персонажей | מהדמויות |
| `Encounters.Creatures` | Creatures | Существа | יצורים |
| `Encounters.Creatures.Empty` | No creatures yet. Add some with the search. | Существ пока нет. Добавьте их через поиск. | אין עדיין יצורים. הוסיפו אותם בעזרת החיפוש. |
| `Encounters.Gone.Cast` | No longer in the cast | Его больше нет среди персонажей | כבר לא בין הדמויות |
| `Encounters.Gone.Entry` | Not in the Library | Нет в Библиотеке | לא בספרייה |
| `Encounters.Homebrew` | Homebrew | Собственный | תוכן ביתי |
| `Encounters.HowMany` | How many {0} | Сколько: {0} | כמה {0} |
| `Encounters.NoteFor` | Note for {0} | Заметка: {0} | הערה עבור {0} |
| `Encounters.NotePlaceholder` | Waits behind the door | Ждёт за дверью | מחכה מאחורי הדלת |
| `Encounters.Remove` | Remove {0} | Убрать: {0} | הסרת {0} |
| `Encounters.Removed` | Removed {0} | Убрано: {0} | {0} הוסר |

## The editor: the add panel

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Encounters.Add` | Add creatures | Добавить существ | הוספת יצורים |
| `Encounters.Add.Button` | Add | Добавить | הוספה |
| `Encounters.Add.Capped` | Showing the first {0} results. | Показаны первые результаты: {0}. | מוצגות {0} התוצאות הראשונות. |
| `Encounters.Add.Cast` | Cast | Персонажи | דמויות |
| `Encounters.Add.Count` | {0, plural, one{# result} other{# results}} | {0, plural, one{# результат} few{# результата} many{# результатов} other{# результата}} | {0, plural, one{תוצאה אחת} two{שתי תוצאות} other{# תוצאות}} |
| `Encounters.Add.Creature` | Add {0} | Добавить: {0} | הוספה: {0} |
| `Encounters.Add.Library` | Library | Библиотека | ספרייה |
| `Encounters.Add.Placeholder` | Search by name | Поиск по названию | חיפוש לפי שם |
| `Encounters.Add.SearchCast` | Search the cast | Поиск среди персонажей | חיפוש בין הדמויות |
| `Encounters.Add.SearchCreatures` | Search the Library's creatures and your homebrew | Поиск среди существ Библиотеки и ваших собственных материалов | חיפוש ביצורי הספרייה ובתוכן הביתי שלכם |
| `Encounters.Add.Shortlist` | Add from the Bestiary shortlist | Добавить из списка бестиария | הוספה מהרשימה הקצרה של ספר המפלצות |
| `Encounters.Add.Source` | Where to add from | Откуда добавить | מאיפה להוסיף |

The Hebrew «הוספה: {0}» replaced «הוספת {0}» so a button that reads «הוספה» is named with the same word (a screen reader's name for it holds the words on the button).

## The editor: hazards (the Hazards tab and the panel)

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Encounters.Add.Hazard` | Add {0} to the place | Добавить в это место: {0} | הוספה למקום: {0} |
| `Encounters.Add.HazardPendingAlready` | A hazard is already waiting to be placed at {0}. A place holds one. | В месте «{0}» уже ждёт постановки опасность. В месте может быть только одна. | סכנה כבר ממתינה להצבה במקום {0}. במקום אחד יש סכנה אחת. |
| `Encounters.Add.HazardPlaced` | {0} will be placed at {1} when you save. | {0}: будет поставлена в месте «{1}» при сохранении. | {0}: תוצב במקום {1} בעת השמירה. |
| `Encounters.Add.HazardTaken` | {0} already has a hazard ({1}). A place holds one. | В месте «{0}» уже есть опасность ({1}). В месте может быть только одна. | במקום {0} כבר יש סכנה ({1}). במקום אחד יש סכנה אחת. |
| `Encounters.Add.Hazards` | Hazards | Опасности | סכנות |
| `Encounters.Add.HazardsChoosePlace` | Choose a place first | Сначала выберите место | בחרו קודם מקום |
| `Encounters.Add.HazardsHint` | A complex hazard added here is placed on this encounter's place when you save. It takes its own turn in the initiative order. | Сложная опасность, добавленная здесь, ставится в месте этого столкновения при сохранении. Она ходит в порядке инициативы сама. | סכנה מורכבת שנוספה כאן תוצב במקום של מפגש הקרב הזה בעת השמירה. היא פועלת בתורה בסדר היוזמה. |
| `Encounters.Add.SearchHazards` | Search the Library's complex hazards and your homebrew | Поиск сложных опасностей в Библиотеке и среди ваших собственных материалов | חיפוש סכנות מורכבות בספרייה ובתכנים שלכם |
| `Encounters.Error.HazardGone` | That hazard is no longer in the Library. Choose another. | Этой опасности больше нет в Библиотеке. Выберите другую. | הסכנה הזאת כבר אינה בספרייה. בחרו אחרת. |
| `Encounters.Error.HazardNoPlace` | Choose a place before adding a hazard to it. | Выберите место, прежде чем добавлять в него опасность. | בחרו מקום לפני שמוסיפים לו סכנה. |
| `Encounters.Error.HazardNotComplex` | Only a complex hazard, one that takes its own turn, can be added here. Place the others on the place's page. | Здесь можно добавить только сложную опасность, которая ходит сама. Остальные ставятся на странице места. | אפשר להוסיף כאן רק סכנה מורכבת, כזאת שפועלת בתורה. את האחרות מציבים בדף המקום. |
| `Encounters.Error.HazardNotPlaced` | The hazard could not be placed on the place. Nothing was saved. | Не удалось поставить опасность в месте. Ничего не сохранено. | לא ניתן היה להציב את הסכנה במקום. דבר לא נשמר. |
| `Encounters.Error.HazardPlaceGone` | The place is no longer there, so the hazard could not be placed. Nothing was saved. | Места больше нет, поэтому опасность не поставлена. Ничего не сохранено. | המקום כבר אינו קיים, ולכן הסכנה לא הוצבה. דבר לא נשמר. |
| `Encounters.Error.HazardTaken` | The place already has a hazard. A place holds one, so nothing was saved. | В месте уже есть опасность, а в месте может быть только одна. Ничего не сохранено. | כבר יש סכנה במקום, ובמקום אחד יש סכנה אחת. דבר לא נשמר. |
| `Encounters.Error.NotSavedHazardPlaced` | The hazard was placed on the place, but the encounter was not saved. Save again to store it. | Опасность поставлена в месте, но столкновение не сохранено. Сохраните ещё раз. | הסכנה הוצבה במקום, אבל מפגש הקרב לא נשמר. שמרו שוב. |
| `Encounters.Hazards` | Hazards at this place | Опасности в этом месте | סכנות במקום הזה |
| `Encounters.Hazards.At` | At {0} | Место: {0} | במקום {0} |
| `Encounters.Hazards.ChoosePlace` | Choose a place to see the hazards placed there. | Выберите место, чтобы увидеть его опасности. | בחרו מקום כדי לראות את הסכנות שבו. |
| `Encounters.Hazards.JoinsFor` | {0} joins the initiative order | {0}: вступает в порядок инициативы | {0}: מצטרפת לסדר היוזמה |
| `Encounters.Hazards.JoinsOrder` | Joins the initiative order | Вступает в порядок инициативы | מצטרפת לסדר היוזמה |
| `Encounters.Hazards.LeftOutFor` | {0} will not join the initiative order | {0}: не вступит в порядок инициативы | {0}: לא תצטרף לסדר היוזמה |
| `Encounters.Hazards.None` | No hazards are placed here. Place one on the place's page, or add a complex one here. | Здесь нет опасностей. Поставьте их на странице места или добавьте здесь сложную. | אין כאן סכנות. הציבו אותן בדף המקום, או הוסיפו כאן סכנה מורכבת. |
| `Encounters.Hazards.OpenPlace` | Open the place | Открыть место | פתיחת המקום |
| `Encounters.Hazards.Pending` | {0} will be placed at {1} when you save | {0}: будет поставлена в месте «{1}» при сохранении | {0}: תוצב במקום {1} בעת השמירה |
| `Encounters.Hazards.PendingCleared` | {0} was not added: {1} already has a hazard. | {0} не добавлена: в месте «{1}» уже есть опасность. | {0} לא נוספה: כבר יש סכנה במקום {1}. |
| `Encounters.Hazards.PendingRemove` | Don't place {0} | Не ставить: {0} | לא להציב: {0} |
| `Encounters.Hazards.WontJoin.Disabled` | Disabled: it won't join | Обезврежена: не вступит в порядок инициативы | מנוטרלת: לא תצטרף |
| `Encounters.Hazards.WontJoin.Triggered` | Triggered: it won't join | Уже сработала: не вступит в порядок инициативы | כבר הופעלה: לא תצטרף |

## The editor: difficulty readout and "Rate for a different party"

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Encounters.Difficulty.Above` | Above {0} | Выше «{0}» | מעל לרמה {0} |
| `Encounters.Difficulty.Bands` | Bands for this party | Пороги для этого отряда | הטווחים לקבוצה הזאת |
| `Encounters.Difficulty.Below` | Below {0} | Ниже «{0}» | מתחת לרמה {0} |
| `Encounters.Difficulty.Characters` | Characters | Число персонажей | דמויות |
| `Encounters.Difficulty.Different` | Rate for a different party | Оценить для другого отряда | הערכה עבור קבוצה אחרת |
| `Encounters.Difficulty.DifferentHint` | This only changes what is shown here. It is not saved. | Это меняет только то, что показано здесь. Оно не сохраняется. | זה משנה רק את מה שמוצג כאן. הוא לא נשמר. |
| `Encounters.Difficulty.From` | {0} from {1} | {0}: от {1} | {0} מ-{1} |
| `Encounters.Difficulty.HazardsJoin` | Joining the order: {0} | Вступают в порядок: {0} | מצטרפות לסדר: {0} |
| `Encounters.Difficulty.HazardsNotCounted` | {0, plural, one{# hazard is not counted} other{# hazards are not counted}} | {0, plural, one{# опасность не учитывается} few{# опасности не учитываются} many{# опасностей не учитывается} other{# опасности не учитываются}} | {0, plural, one{סכנה אחת לא נספרת} two{שתי סכנות לא נספרות} other{# סכנות לא נספרות}} |
| `Encounters.Difficulty.Level` | Level | Уровень | דרגה |
| `Encounters.Difficulty.LevelUnknown` | {1, plural, one{The level of {0} is not known, so there is no readout. Set it on the character's sheet, or rate for a different party.} other{The levels of {0} are not known, so there is no readout. Set them on the characters' sheets, or rate for a different party.}} | {1, plural, one{Уровень персонажа {0} неизвестен, поэтому оценки нет. Укажите его в листе персонажа или оцените для другого отряда.} few{Уровни персонажей {0} неизвестны, поэтому оценки нет. Укажите их в листах персонажей или оцените для другого отряда.} many{Уровни персонажей {0} неизвестны, поэтому оценки нет. Укажите их в листах персонажей или оцените для другого отряда.} other{Уровни персонажей {0} неизвестны, поэтому оценки нет. Укажите их в листах персонажей или оцените для другого отряда.}} | {1, plural, one{הדרגה של {0} לא ידועה, ולכן אין הערכה. אפשר להגדיר אותה בדף הדמות, או להעריך עבור קבוצה אחרת.} other{הדרגות של {0} לא ידועות, ולכן אין הערכה. אפשר להגדיר אותן בדפי הדמויות, או להעריך עבור קבוצה אחרת.}} |
| `Encounters.Difficulty.Negative` | {0} has a negative value | У существа «{0}» отрицательное значение | {0}: הערך שלילי |
| `Encounters.Difficulty.NoActor` | {0} is no longer in the cast | {0} больше нет среди персонажей | {0} כבר לא בין הדמויות |
| `Encounters.Difficulty.NoBudgetRow` | The system has no budget row for level {0}. | В системе нет строки бюджета для уровня {0}. | במערכת אין שורת תקציב לדרגה {0}. |
| `Encounters.Difficulty.NoEntry` | {0} is no longer in the Library | {0} больше нет в Библиотеке | {0} כבר לא בספרייה |
| `Encounters.Difficulty.NoParty` | There are no characters to rate for. | Нет персонажей, для которых можно оценить сложность. | אין דמויות שאפשר להעריך עבורן. |
| `Encounters.Difficulty.NoValue` | {0} has no {1} | У существа «{0}» нет значения: {1} | {0}: אין ערך ({1}) |
| `Encounters.Difficulty.NotCounted` | {0, plural, one{# line not counted} other{# lines not counted}} | {0, plural, one{# строка не учтена} few{# строки не учтены} many{# строк не учтено} other{# строки не учтено}} | {0, plural, one{שורה אחת לא נספרה} two{שתי שורות לא נספרו} other{# שורות לא נספרו}} |
| `Encounters.Difficulty.NotWhole` | The level of {0} is not a whole number | Уровень существа «{0}» не целое число | הדרגה של {0} אינה מספר שלם |
| `Encounters.Difficulty.Nothing` | Nothing counted yet | Пока ничего не учтено | עדיין לא נספר דבר |
| `Encounters.Difficulty.Party` | {0, plural, one{# character} other{# characters}} of level {1} | {0, plural, one{# персонажа} few{# персонажей} many{# персонажей} other{# персонажа}} уровня {1} | {0, plural, one{דמות אחת} two{שתי דמויות} other{# דמויות}} בדרגה {1} |
| `Encounters.Difficulty.PartyLevelVaries` | {0, plural, one{# character} other{# characters}} at party level {1} (levels vary) | {0, plural, one{# персонажа} few{# персонажей} many{# персонажей} other{# персонажа}}, уровень отряда {1} (уровни разные) | {0, plural, one{דמות אחת} two{שתי דמויות} other{# דמויות}}, דרגת הקבוצה {1} (הדרגות שונות) |
| `Encounters.Difficulty.PartyVaries` | {0, plural, one{# character} other{# characters}}, levels {1} to {2} | {0, plural, one{# персонажа} few{# персонажей} many{# персонажей} other{# персонажа}}, уровни от {1} до {2} | {0, plural, one{דמות אחת} two{שתי דמויות} other{# דמויות}}, בדרגות {1} עד {2} |
| `Encounters.Difficulty.Source.Default` | No characters in this campaign yet; rating for {0}. | В этой кампании пока нет персонажей; оценка для {0}. | אין עדיין דמויות בקמפיין הזה; ההערכה עבור {0}. |
| `Encounters.Difficulty.Source.Override` | Rating for a different party: {0}. | Оценка для {0} (другой отряд). | ההערכה עבור קבוצה אחרת: {0}. |
| `Encounters.Difficulty.Summary` | {0}: {1} for {2} | {0}: {1} для {2} | {0}: {1} עבור {2} |
| `Encounters.Difficulty.Title` | Difficulty | Сложность | רמת קושי |
| `Encounters.Difficulty.TooFarAbove` | {0} is too far above the party for the table | {0} намного сильнее отряда: в таблице такого нет | {0} חזק מדי ביחס לקבוצה, ואין לו שורה בטבלה |
| `Encounters.Difficulty.TooFarBelow` | {0} is too far below the party for the table | {0} намного слабее отряда: в таблице такого нет | {0} חלש מדי ביחס לקבוצה, ואין לו שורה בטבלה |
| `Encounters.Difficulty.TotalUnit` | {0} {1} | {0} {1} | {0} {1} |
| `Encounters.Difficulty.UpTo` | {0} up to {1} | {0}: до {1} | {0} עד {1} |
| `Encounters.Difficulty.UseCampaign` | Use the campaign's party | Взять отряд кампании | שימוש בקבוצת הקמפיין |

In Russian the party text («4 персонажей уровня 3») is in the genitive, because it always follows «для»; the band labels are feminine («Умеренная: 250 ОО для …»).

## At the table: the prepared-encounter picker, its confirm and its notes

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Combat.Hazard.None` | No hazard is placed here. Place one on a place's page. | Здесь нет опасностей. Поставьте опасность на странице места. | אין כאן סכנה. אפשר להציב אחת בדף המקום. |
| `Combat.NoEncounterHint` | No encounter is running. Start one to roll initiative; the cards below work either way. | Столкновение не идёт. Начните его, чтобы бросить инициативу; карточки ниже работают в любом случае. | אין מפגש קרב פעיל. התחילו אחד כדי להטיל יוזמה; הכרטיסים למטה פועלים בכל מקרה. |
| `Combat.Prepared.Confirm` | Start {0}? {1, plural, one{One creature joins the fight and rolls initiative.} other{# creatures join the fight and roll initiative.}} | Начать «{0}»? {1, plural, one{В бой вступает # существо и бросает инициативу.} few{В бой вступают # существа и бросают инициативу.} many{В бой вступают # существ и бросают инициативу.} other{В бой вступают # существа и бросают инициативу.}} | להתחיל את {0}? {1, plural, one{יצור אחד מצטרף לקרב ומטיל יוזמה.} two{שני יצורים מצטרפים לקרב ומטילים יוזמה.} other{# יצורים מצטרפים לקרב ומטילים יוזמה.}} |
| `Combat.Prepared.ConfirmBoth` | Start {0}? {1, plural, one{# creature} other{# creatures}} and {2, plural, one{# hazard} other{# hazards}} join the fight and roll initiative. | Начать «{0}»? В бой вступают {1, plural, one{# существо} few{# существа} many{# существ} other{# существа}} и {2, plural, one{# опасность} few{# опасности} many{# опасностей} other{# опасности}}. Все бросают инициативу. | להתחיל את {0}? {1, plural, one{יצור אחד} two{שני יצורים} other{# יצורים}} {2, plural, one{וסכנה אחת} two{ושתי סכנות} other{ו־# סכנות}} מצטרפים לקרב ומטילים יוזמה. |
| `Combat.Prepared.ConfirmHazards` | Start {0}? {1, plural, one{One hazard joins the fight and rolls initiative.} other{# hazards join the fight and roll initiative.}} | Начать «{0}»? {1, plural, one{В бой вступает # опасность и бросает инициативу.} few{В бой вступают # опасности и бросают инициативу.} many{В бой вступают # опасностей и бросают инициативу.} other{В бой вступают # опасности и бросают инициативу.}} | להתחיל את {0}? {1, plural, one{סכנה אחת מצטרפת לקרב ומטילה יוזמה.} two{שתי סכנות מצטרפות לקרב ומטילות יוזמה.} other{# סכנות מצטרפות לקרב ומטילות יוזמה.}} |
| `Combat.Prepared.ConfirmTitle` | Start a prepared encounter | Начать подготовленное столкновение | התחלת מפגש קרב מוכן |
| `Combat.Prepared.LineNote` | {0}: {1} | {0}: {1} | {0}: {1} |
| `Combat.Prepared.ListLabel` | Prepared encounters | Подготовленные столкновения | מפגשי קרב מוכנים |
| `Combat.Prepared.Manage` | Prepare one | Подготовить | הכנת מפגש קרב |
| `Combat.Prepared.NoNotes` | This encounter has no notes. | В этом столкновении нет заметок. | במפגש הקרב הזה אין הערות. |
| `Combat.Prepared.None` | No encounters are ready to start. | Нет столкновений, готовых к началу. | אין מפגשי קרב מוכנים להתחלה. |
| `Combat.Prepared.Notes` | Encounter notes | Заметки о столкновении | הערות למפגש הקרב |
| `Combat.Prepared.Open` | Start a prepared encounter | Начать подготовленное столкновение | התחלת מפגש קרב מוכן |
| `Combat.Prepared.OpenPlan` | Open the encounter | Открыть столкновение | פתיחת מפגש הקרב |
| `Combat.Prepared.Refused.Busy` | An encounter is already being started. | Столкновение уже запускается. | מפגש קרב כבר בתהליך התחלה. |
| `Combat.Prepared.Refused.EncounterRunning` | End the current encounter first. | Сначала завершите текущее столкновение. | סיימו קודם את מפגש הקרב הנוכחי. |
| `Combat.Prepared.Refused.Failed` | The encounter could not be started. | Не удалось начать столкновение. | לא ניתן היה להתחיל את מפגש הקרב. |
| `Combat.Prepared.Refused.Invalid` | This encounter has problems. Open it and fix them first. | В этом столкновении есть ошибки. Откройте его и сначала исправьте их. | במפגש הקרב יש בעיות. פתחו אותו ותקנו אותן קודם. |
| `Combat.Prepared.Refused.NoTable` | Start the table first. | Сначала запустите стол. | הפעילו קודם את השולחן. |
| `Combat.Prepared.Refused.NobodyJoined` | Nobody in this encounter could join the fight. | Никто из этого столкновения не смог вступить в бой. | אף אחד במפגש הקרב הזה לא יכול היה להצטרף לקרב. |
| `Combat.Prepared.Refused.NotAllowed` | Only the GM can start an encounter. | Начать столкновение может только мастер. | רק מנחה המשחק יכול להתחיל מפגש קרב. |
| `Combat.Prepared.Refused.NotFound` | That encounter is gone. | Этого столкновения больше нет. | מפגש הקרב הזה כבר לא קיים. |
| `Combat.Prepared.Refused.TableClosed` | The table ended before the encounter could start. | Стол закрылся раньше, чем началось столкновение. | השולחן נסגר לפני שמפגש הקרב יכול היה להתחיל. |
| `Combat.Prepared.RunNotRecorded` | The encounter started, but it could not be marked as run. | Столкновение началось, но отметить его как проведённое не удалось. | מפגש הקרב התחיל, אך לא ניתן היה לסמן אותו כמפגש שהתקיים. |
| `Combat.Prepared.Start` | Start | Начать | התחלה |
| `Combat.Prepared.StartLabel` | Start {0} | Начать: {0} | התחלת {0} |
| `Combat.Prepared.Started` | {0} started. | Столкновение «{0}» началось. | מפגש הקרב {0} התחיל. |
| `Combat.Prepared.StartedSome` | {0} started. These did not join: {1}. | Столкновение «{0}» началось. Не вступили в бой: {1}. | מפגש הקרב {0} התחיל. אלה לא הצטרפו: {1}. |
| `Combat.QuickAddPlaceHazards` | Hazards on {0} | Опасности места «{0}» | סכנות במקום {0} |
| `Combat.StartEncounter` | Start encounter | Начать столкновение | התחלת מפגש קרב |

## The session log

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Event.Encounter.Ended` | The encounter ended after {0, plural, one{# round} other{# rounds}}. | Столкновение закончилось через {0, plural, one{# раунд} few{# раунда} many{# раундов} other{# раунда}}. | מפגש הקרב הסתיים אחרי {0, plural, one{סבב אחד} two{שני סבבים} other{# סבבים}}. |
| `Event.Encounter.None` | There was no encounter to end. | Не было столкновения, которое можно завершить. | אין מפגש קרב לסיים. |
| `Event.Encounter.NotJoined` | {1, plural, one{One creature of {0} did not join.} other{# creatures of {0} did not join.}} | {1, plural, one{# существо из «{0}» не вступило в бой.} few{# существа из «{0}» не вступили в бой.} many{# существ из «{0}» не вступили в бой.} other{# существа из «{0}» не вступили в бой.}} | {1, plural, one{יצור אחד מתוך {0} לא הצטרף.} two{שני יצורים מתוך {0} לא הצטרפו.} other{# יצורים מתוך {0} לא הצטרפו.}} |
| `Event.Encounter.StartEmpty` | {0} was not started: nobody in it is at the table. | «{0}» не начато: никого из него нет за столом. | מפגש הקרב {0} לא התחיל: אף אחד ממנו אינו ליד השולחן. |
| `Event.Encounter.StartRefused` | {0} was not started: an encounter is already running. | «{0}» не начато: столкновение уже идёт. | מפגש הקרב {0} לא התחיל: מפגש קרב כבר מתנהל. |
| `Event.Encounter.Started` | Encounter started: {0} at {1}. | Столкновение началось: {0}, место: {1}. | מפגש קרב התחיל: {0}, מקום: {1}. |
| `Event.Encounter.StartedNoPlace` | Encounter started: {0}. | Столкновение началось: {0}. | מפגש קרב התחיל: {0}. |
| `Event.Type.EncounterEnded` | Encounter end | Конец столкновения | סוף מפגש קרב |

## The world builder chat and review card

| Key | English | Russian | Hebrew |
|---|---|---|---|
| `Page.Campaign.Section.Encounters` | Encounters | Столкновения | מפגשי קרב |
| `WorldBuilder.Error.EncounterRefMissing` | “{0}” points at a place or creature that this apply did not save, so the encounter was not saved. Untick it, or apply the rest and ask for it again. | «{0}» ссылается на место или существо, которое при этом применении не сохранилось, поэтому столкновение не сохранено. Снимите с него отметку или примените остальное и попросите его снова. | „{0}” מצביע על מקום או יצור שלא נשמרו בהחלה הזו, ולכן מפגש הקרב לא נשמר. הסירו ממנו את הסימון, או החילו את השאר ובקשו אותו שוב. |
| `WorldBuilder.Error.EncounterRefused` | The encounter “{0}” could not be saved. {1} | Столкновение «{0}» не удалось сохранить. {1} | אי אפשר היה לשמור את מפגש הקרב „{0}”. {1} |
| `WorldBuilder.Error.EncountersUnavailable` | Prepared encounters are not available here. | Подготовленные столкновения здесь недоступны. | מפגשי קרב מוכנים אינם זמינים כאן. |
| `WorldChat.Can.Encounters` | Prepared encounters: a place, a lineup and notes, ready for you to start at the table. | Подготовленные столкновения: место, состав и заметки, которые вы сможете запустить за столом. | מפגשי קרב מוכנים: מקום, הרכב והערות, שאפשר להתחיל ליד השולחן. |
| `WorldChat.Edit.Encounter` | changes the encounter | меняет столкновение | משנה את מפגש הקרב |
| `WorldChat.Kind.encounter` | Encounters | Столкновения | מפגשי קרב |
| `WorldChat.Proposal.DeletesEncounter` | Deletes the encounter “{0}”. This cannot be undone. | Удаляет столкновение «{0}». Это нельзя отменить. | מוחק את מפגש הקרב „{0}”. אי אפשר לבטל את זה. |
| `WorldChat.Proposal.Encounter.Empty` | Nobody yet | Пока никого | עדיין אף אחד |
| `WorldChat.Proposal.Encounter.Hazards` | Hazards at the place: {0} | Опасности в этом месте: {0} | סכנות במקום: {0} |
| `WorldChat.Proposal.Encounter.Hidden` | starts hidden | начинает скрытым | מתחיל מוסתר |
| `WorldChat.Proposal.Encounter.LeftOut` | Left out of the order: {0} | Не вступают в порядок инициативы: {0} | לא מצטרפות לסדר: {0} |
| `WorldChat.Proposal.Encounter.LeftOutNone` | Left out of the order: nothing (every hazard there joins) | Не вступают в порядок инициативы: ни одна (вступают все опасности в этом месте) | לא מצטרפות לסדר: אף אחת (כל הסכנות במקום מצטרפות) |
| `WorldChat.Proposal.Encounter.Lineup` | Who is in it | Кто участвует | מי משתתף |
| `WorldChat.Proposal.Encounter.LineupBefore` | Now: {0} | Сейчас: {0} | עכשיו: {0} |
| `WorldChat.Proposal.Encounter.NoPlace` | No place | Без места | ללא מקום |
| `WorldChat.Proposal.Encounter.Notes` | Notes | Заметки | הערות |
| `WorldChat.Proposal.Encounter.Place` | Set in {0} | Происходит: {0} | מתרחש: {0} |
| `WorldChat.Proposal.Encounter.PlaceChange` | Place: changes from {0} to {1} | Место: меняется с «{0}» на «{1}» | מקום: היה „{0}”, יהיה „{1}” |
| `WorldChat.Proposal.Encounter.Readout` | Difficulty: {0} | Сложность: {0} | רמת קושי: {0} |
| `WorldChat.Proposal.Encounter.Status` | Status: {0} | Статус: {0} | מצב: {0} |
| `WorldChat.Proposal.Encounter.StatusChange` | Status: changes from {0} to {1} | Статус: меняется с «{0}» на «{1}» | מצב: היה „{0}”, יהיה „{1}” |
| `WorldChat.Starter.encounter.Label` | An encounter | Столкновение | מפגש קרב |
| `WorldChat.Starter.encounter.Text` | Prepare an encounter for the next session: where it takes place, who is in it, and how it could end other than by killing everyone. | Подготовь столкновение для следующей сессии: где оно происходит, кто в нём участвует и как оно может закончиться иначе, чем гибелью всех. | הכן מפגש קרב לקראת המפגש הבא: איפה הוא מתרחש, מי משתתף בו, ואיך הוא יכול להסתיים בדרך אחרת מהריגת כולם. |
| `WorldChat.Starter.encounter.TextAt` | Prepare an encounter at {0}: build on what is already there, and say in the notes what the creatures want. | Подготовь столкновение в месте «{0}»: опирайся на то, что там уже есть, и напиши в заметках, чего хотят существа. | הכן מפגש קרב ב„{0}”: בנה על מה שכבר נמצא שם, וכתוב בהערות מה היצורים רוצים. |
| `WorldChat.Tool.Encounter` | Read the encounter “{0}” | Прочитано столкновение «{0}» | נקרא מפגש הקרב „{0}” |
| `WorldChat.Tool.Encounters` | Looked at the prepared encounters | Просмотрены подготовленные столкновения | נבדקו מפגשי הקרב המוכנים |
| `WorldChat.Tool.Hazards` | Looked at the hazards at “{0}” | Просмотрены опасности места «{0}» | נבדקו הסכנות ב„{0}” |
| `WorldChat.Tool.Rate` | Rated a lineup (lines: {0}) | Оценён состав (строк: {0}) | נבדק הרכב (שורות: {0}) |
