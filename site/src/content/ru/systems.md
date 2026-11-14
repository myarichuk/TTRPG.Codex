---
title: Создайте свою систему
lead: Игровая система в TTRPG.Codex — это папка с обычными текстовыми файлами. В этом руководстве мы шаг за шагом собираем маленькую систему с нуля, а потом загружаем её и проверяем.
description: Руководство для разработчика по созданию игровой системы TTRPG.Codex: файл system.yaml, поля и раскладка листа, вычисляемые значения, списки, особенности с модификаторами, зарядами и выбором, моменты, переводы, проверка, загрузка и частые ошибки.
---

> [!NOTE]
> Машинный черновик перевода: страницу ещё не читал носитель языка. Блоки кода совпадают с английской версией, а английский текст остаётся основным.

Писать код не нужно. Если вы умеете читать YAML-файл, вы можете описать игру: числа на листе персонажа, то, из чего игроки выбирают, и то, что делает каждый выбор. Приложение читает эти файлы и по ним строит лист, Библиотеку и стол.

Это пошаговое руководство. Все ключи с типами и значениями по умолчанию описаны в [полном справочнике](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/SYSTEM_YAML_REFERENCE.md). Он создаётся из самого приложения, поэтому всегда точен.

## Что такое система {#what}

Система — это папка в `content/systems/`. В ней лежит один файл `system.yaml` и рядом любое число **наборов материалов**.

| Часть | Что это |
| --- | --- |
| `system.yaml` | Правила в виде данных: что хранит лист, как считаются его числа, какие виды записей есть в Библиотеке и какие моменты важны игре. |
| `system.ru.yaml`, `system.he.yaml` | Необязательно. Слова той же системы на другом языке. |
| `packs/<имя>/` | Записи Библиотеки: классы, таланты, заклинания, монстры — всё, что есть в вашей системе. Каждая запись — небольшой YAML-файл. |
| `spec.yaml` | Необязательно. Ваши проверки, например «этот лист должен посчитаться как 8». |

В приложении есть две системы, которые можно читать по ходу работы. Это лучшие примеры, потому что они настоящие:

- система SRD 5.2.1: `content/systems/srd5e/system.yaml`
- система Forged in the Dark SRD: `content/systems/fitd-srd/system.yaml`

Фрагменты ниже взяты из них там, где это уместно, и из небольшой выдуманной игры **Lantern Run**, которую мы собираем вместе.

> [!NOTE]
> Система описывает правила, а не книгу. Пишите своими словами и не копируйте текст, названия и мир других издателей. Если ваша система основана на документе с лицензией, укажите это в блоке `license`, как делают встроенные системы.

## Начните с папки {#folder}

Создайте такую папку рядом со встроенными:

```text
content/systems/lantern-run/
  system.yaml
  packs/
    core/
      manifest.yaml
      talents/
        deep-pockets.yaml
```

Всё, что нужно загрузчику, — в `system.yaml`. Начните с того, кто эта система:

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

Первая строка — комментарий, и её стоит оставить. Редактор с языковым сервером YAML (VS Code с расширением YAML или редактор JetBrains) читает её, подсказывает ключи по ходу набора и подчёркивает ошибку в написании.

Обязательны только `id` и `name`. `id` — ключ системы: строчные буквы, цифры, `-` или `_`. Кампании запоминают его, так что выберите один раз и никогда не меняйте. Менять нужно `version`: когда правила изменились, увеличьте её, и Мастер каждой кампании увидит, что поменялось.

## Скажите, какие бывают записи {#types}

Наборы материалов хранят записи Библиотеки, а `entityTypes` говорит, какие их виды бывают. В Lantern Run он один — талант:

```yaml
entityTypes:
  - { type: talent, label: Talent, pluralLabel: Talents, folder: talents, playerFacing: true }

conditions: [Shaken, Lost]
```

`folder` — папка внутри набора, где лежат записи этого вида. `playerFacing: true` показывает их игрокам в списках выбора. `conditions` заполняет список состояний за живым столом.

Система Forged in the Dark SRD делает то же самое, а у некоторых видов есть дополнительные поля:

```yaml
entityTypes:
  - { type: crew-upgrade, label: Crew upgrade, pluralLabel: Crew upgrades, folder: crew-upgrades, fields: [{ key: cost, label: Upgrade boxes, type: Number }] }
```

## Добавьте лист {#sheet}

**Вид листа** — это один тип листа персонажа. В системе может быть несколько: у системы Forged in the Dark SRD есть лист проходимца и лист команды. У нас один — исследователь. Всё, что относится к листу, лежит под ним.

```yaml
sheetKinds:
  - kind: delver
    label: Delver
    role: player
```

### Набор навыков {#skills}

**Именованный список** — это фиксированный набор именованных вещей, у каждой свои поля. Навыки подходят отлично. Здесь у каждого навыка рейтинг от 0 до 3, нарисованный точками, и `group`, который нам скоро пригодится:

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

В системе Forged in the Dark SRD двенадцать действий устроены точно так же.

### Вычисляемое значение {#derived}

**Значение** — это число на листе. Задайте ему `formula`, и приложение будет пересчитывать его при любом изменении. Формулы читают другие значения, именованные списки и списки. Эта считает навыки, у которых есть хотя бы одна точка:

```yaml
    values:
      - { key: trained, label: Trained skills, formula: "count(skills, s => s.rating >= 1)" }
      - { key: packMax, label: Pack slots, formula: "4 + trained", modifiers: packMax }
```

`packMax` начинается с 4 и прибавляет по единице за каждый освоенный навык. Строка `modifiers: packMax` значит, что всё остальное на листе, нацеленное на `packMax`, добавляется сверху. Мы воспользуемся этим чуть позже.

В системе SRD 5.2.1 та же идея записана одной формулой:

```yaml
    - { key: proficiencyBonus, label: Proficiency Bonus, formula: "2 + (level - 1) / 4", category: Combat }
```

Арифметика целочисленная. Когда нужно, есть `floor`, `ceil`, `max` и `min`.

### То, что растёт и убывает {#tracks}

**Шкала** — это число, которое меняется в игре и имеет границы: хиты, стресс, масло в фонаре.

```yaml
    tracks:
      - { key: lantern, label: Lantern oil, max: 6, start: max, display: boxes }
      - { key: nerve, label: Nerve, max: 5, display: boxes }
```

Шкала умеет больше, чем просто считать. В системе Forged in the Dark SRD заполненная шкала стресса очищается и добавляет одну травму, а игрок выбирает, какую:

```yaml
      - key: stress
        label: Stress
        max: 9
        display: boxes
        overflow: { into: trauma, by: 1, then: reset, ask: { who: player, choices: [Cold, Haunted, Obsessed, Paranoid, Reckless, Soft, Unstable, Vicious] } }
```

### Разложите лист {#layout}

Без `layout` приложение показывает все значения, шкалы и списки подряд. Раскладка собирает их во вкладки: каждая — раздел с виджетами:

```yaml
    layout:
      - { section: Skills, widgets: [{ widget: table, key: skills, groupBy: group, columns: [rating] }, { widget: values, keys: [trained, packMax] }] }
      - { section: Condition, widgets: [{ widget: track, key: lantern }, { widget: track, key: nerve }, { widget: moments }] }
      - { section: Talents, widgets: [{ widget: list, key: talents }, { widget: features }] }
```

Здесь использованы виджеты `table`, `values`, `track`, `list`, `moments` и `features`. В каждом разделе должен быть хотя бы один виджет. Остальные перечислены в справочнике.

## Дайте листу то, из чего выбирать {#features}

**Список** хранит выбор из Библиотеки: классы, черты, заклинания, таланты. **Источник особенностей** говорит, что записи, выбранные в списке, несут собственные правила. Добавьте и то и другое исследователю:

```yaml
    features:
      - { list: talents }

    lists:
      - key: talents
        label: Talents
        type: talent
```

Теперь правила живут не в `system.yaml`, а в каждом таланте, в его собственном файле под `properties`. Особенность может делать три вещи.

### Изменить число: `modifiers` {#modifiers}

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

У того, кто выбрал этот талант, на два места в рюкзаке больше. `target` — ключ значения. `amount` — число или формула. Способность Mule из системы Forged in the Dark SRD — тот же файл с другой целью и меньшим бонусом:

```yaml
properties:
  modifiers:
  - target: LoadMax
    amount: 2
```

### Дать то, что можно потратить: `uses` {#uses}

**Заряд** — это счётчик на листе, который даёт особенность, например «раз за привал». Часть `recovers` говорит, когда он восстанавливается:

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

`camp` не встроен: мы объявим его на следующем шаге. В `on` работают также отдых, `initiative` и `dawn`. У Ярости в системе SRD 5.2.1 часть зарядов возвращается после одного отдыха, а все — после другого, а максимум считается формулой от уровня персонажа:

```yaml
    uses:
      key: rage
      max: 'classLevel >= 17 ? 6 : classLevel >= 12 ? 5 : classLevel >= 6 ? 4 : classLevel >= 3 ? 3 : 2'
      recovers:
      - on: Short Rest
        amount: 1
      - on: Long Rest
```

### Дать игроку выбор: `adds` {#choice}

Строка `adds` что-то даёт листу, а если в ней есть `choice`, то решает игрок. Этот талант позволяет выбрать один навык:

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

Способность Expertise из системы Forged in the Dark SRD записана точно так же. `choice` — ваше собственное имя выбора. Лист запоминает его, показывает выбор и сообщает игроку, пока выбор остаётся открытым.

Выбор может также указывать записи Библиотеки и читать предыдущий выбор. Это черта Magic Initiate из системы SRD 5.2.1 в сокращении: сначала выбирается список заклинаний, затем из него два заговора.

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

У первой строки нет `to`, поэтому она ничего не добавляет листу: это просто значение, которое могут читать другие строки. Пишите её перед теми, кто от неё зависит.

## Добавьте момент {#moments}

**Момент** — это названное время, когда счётчики возвращаются. Отдыхи, `initiative` и `dawn` уже есть. Свой момент объявите в `system.yaml`:

```yaml
moments:
  - { key: camp, label: Camp }
```

Этого достаточно, чтобы `second-wind` восстанавливался. Виджет `moments` в нашей раскладке добавляет на лист кнопку **Привал**, и нажатие восстанавливает все счётчики с `on: camp`.

Момент может срабатывать и сам, когда меняется значение. Момент загрузки в системе Forged in the Dark SRD срабатывает, когда игрок задаёт значение `load` листа:

```yaml
moments:
  - { key: downtime, label: Downtime }
  - { key: loadChosen, label: Load chosen, onSet: load }
```

## Манифест набора {#manifest}

У каждого набора есть `manifest.yaml`, где сказано, для какой он системы:

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

`systemId` должен совпадать с `id` системы. Без него набору некуда попасть.

## Переведите {#translations}

Перевод никогда не меняет правила. Ключи, идентификаторы и формулы остаются английскими, переводятся только слова, которые читают люди.

Положите слова системы в соседний файл, названный по языку, — `system.ru.yaml`. Он использует те же ключи, а собственные подписи листа (вид, навыки, шкалы и разделы) идут под `values` и ищутся по английскому слову:

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

Записи получают **набор-перевод** — соседнюю папку рядом с тем набором, который он переводит. В ней те же папки видов, а каждый файл перечисляет записи по `id` и только тот текст, который нужно показать:

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

С ивритом всё так же, нужен только `system.he.yaml`. Страница справа налево отображается сама.

> [!TIP]
> Машинный перевод — хороший первый черновик. Напишите об этом в описании набора и попросите носителя языка прочитать его, прежде чем делиться.

## Проверьте {#check}

Чтобы узнать, загружается ли система, приложение запускать не нужно. Проверка загружает её так же, как сервер, и печатает, что не так. Нужны .NET SDK и копия репозитория:

```bash
dotnet run --project tools/Codex.SystemCheck -- \
  content/systems/lantern-run
```

Хороший запуск печатает `LOADED`. Всё, что не понравилось, — строка `PROBLEM`: ключ, которого приложение не читает, пропавший скрипт, несуществующий список. Исправьте каждую. Неверно написанный ключ просто игнорируется, ошибкой он не бывает, так что именно так его и находят.

Можно также написать ожидания — маленькие проверки в `spec.yaml`, которые запускает та же утилита:

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

У Мары два освоенных навыка, поэтому рюкзак у неё 4 + 2, а «Глубокие карманы» добавляют ещё 2. Проверка печатает `PASS` для каждого выполненного ожидания и `FAIL` с тем, что получилось, для каждого невыполненного.

## Загрузите в приложение {#load}

Способов два, в зависимости от того, как вы запускаете приложение.

**Из исходников.** Приложение читает папку `content/` репозитория, так что папка, которую вы туда положили, и есть система. В режиме разработки приложение следит за папкой и перезагружается само через пару секунд после сохранения. Если этого не случилось, откройте страницу [[ui:Page.About.Title]] и нажмите [[ui:Page.About.Reload]].

**Установленное приложение.** Заархивируйте папку системы в zip, затем на странице [[ui:Page.About.Title]] нажмите [[ui:Page.About.Install]] и выберите файл. Архив для вас сделает утилита проверки:

```bash
dotnet run --project tools/Codex.SystemCheck -- \
  content/systems/lantern-run --pack lantern-run.zip
```

Страница сообщает, что архив заменяет и что изменилось, ещё до установки, а установка, которая не загрузилась, откатывается. По умолчанию устанавливать может только администратор сервера.

Затем нажмите [[ui:Page.Home.New]], назовите кампанию и выберите свою систему. Откройте лист и попробуйте:

1. Дайте исследователю несколько точек в навыках и смотрите, как меняются «Trained skills» и «Pack slots».
2. Добавьте талант «Глубокие карманы». Мест в рюкзаке станет на два больше.
3. Добавьте «Specialist» и выберите навык, когда лист попросит.
4. Добавьте «Second wind», потратьте заряд, затем нажмите **Привал** и посмотрите, как он вернётся.

Меняя правила, увеличивайте `version`. Мастер кампании увидит, что изменилось, когда откроет её в следующий раз.

## Частые ошибки {#mistakes}

- **Неверно написанный ключ молча игнорируется.** `moddifiers` не вызывает ошибку, а просто ничего не делает. Запускайте проверку после каждого изменения и оставляйте комментарий со схемой, чтобы редактор поймал ошибку раньше.
- **Смена `id`.** Кампании находят свою систему по нему. `name` можно менять сколько угодно, `id` — никогда.
- **Забытое увеличение `version`.** Игроки и Мастера не узнают, что правила изменились.
- **Модификатор, который ничего не читает.** `target` особенности должен быть ключом значения, а значение подхватывает модификаторы, только если указывает свой ключ в `modifiers`, как `packMax` выше. Без этой строки талант загрузится и ничего не изменит.
- **Объявленный, но неиспользуемый момент, или используемый, но не объявленный.** Для `recovers: [{ on: camp }]` нужен `camp` в `moments` (или отдых с таким именем).
- **Раздел раскладки без виджетов.** В каждом разделе нужен хотя бы один.
- **`choice`, который читает более поздний выбор.** Сначала пишите тот выбор, который читают остальные.
- **Перевод ключей.** В `system.ru.yaml` и в наборе-переводе идентификаторы, ключи, формулы и `target` остаются английскими. Меняется только отображаемый текст. А переведённый список строк должен содержать все строки в том же порядке.
- **Набор-перевод внутри переводимого набора.** Кладите его в соседнюю папку.
- **Копирование текста издателя.** Правила — это не то же самое, что слова, которыми их кто-то записал. Пишите своими словами и ведите блок лицензии честно.

## Куда дальше {#next}

- [Полный справочник по `system.yaml`](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/SYSTEM_YAML_REFERENCE.md): каждый ключ и тип.
- [Системы и наборы материалов](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/CONTENT_PACKS.md): формат набора, загрузка и версии.
- Две встроенные системы: они используют почти всё, что описано здесь.
- [Новое обращение](https://github.com/myarichuk/TTRPG.Codex/issues), если нужное вам правило пока нельзя записать данными.
