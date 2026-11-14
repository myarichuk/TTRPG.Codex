---
title: Make your own system
lead: A game system in TTRPG.Codex is a folder of plain text files. This guide builds a small one from nothing, step by step, and shows how to load it and test it.
description: A developer guide to writing a TTRPG.Codex game system: the system.yaml file, sheet fields and layout, derived values, lists, features with modifiers, uses and choices, moments, translations, how to check and load it, and common mistakes.
---

You do not need to write code. If you can read a YAML file, you can describe a game: the numbers on a character sheet, the things players pick from, and what each pick does. The app reads those files and builds the sheet, the Library and the table from them.

This guide is a walkthrough. For every key, with its type and default, use the [full reference](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/SYSTEM_YAML_REFERENCE.md). It is generated from the app itself, so it is always right.

## What a system is {#what}

A system is a folder under `content/systems/`. It holds one `system.yaml` and, beside it, any number of **content packs**.

| Part | What it is |
| --- | --- |
| `system.yaml` | The rules as data: what a sheet holds, how its numbers are worked out, which kinds of Library entry exist, and the moments the game cares about. |
| `system.ru.yaml`, `system.he.yaml` | Optional. The same system's words in another language. |
| `packs/<name>/` | Library entries: classes, talents, spells, monsters, whatever your system has. Each entry is one small YAML file. |
| `spec.yaml` | Optional. Checks you write, such as "this sheet should work out to 8". |

The app ships with two systems you can read while you work. They are the best examples there are, because they are the real thing:

- the SRD 5.2.1 system: `content/systems/srd5e/system.yaml`
- the Forged in the Dark SRD system: `content/systems/fitd-srd/system.yaml`

The snippets below come from them where they fit, and from a small made-up game called **Lantern Run** that we build together.

> [!NOTE]
> A system describes rules, not a book. Write in your own words, and do not copy another publisher's text, names or setting. If your system is based on a licensed document, say so in the `license` block, as the bundled systems do.

## Start with a folder {#folder}

Make this folder next to the bundled ones:

```text
content/systems/lantern-run/
  system.yaml
  packs/
    core/
      manifest.yaml
      talents/
        deep-pockets.yaml
```

Everything the loader needs is in `system.yaml`. Start with who the system is:

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

The first line is a comment, and it is worth keeping. An editor with a YAML language server (VS Code with the YAML extension, or a JetBrains editor) reads it, completes keys as you type and underlines a misspelt one.

Only `id` and `name` are required. The `id` is the system's key: lower-case letters, digits, `-` or `_`. Campaigns remember it, so pick it once and never change it. The `version` is the one to change: when the rules change, raise it, and each campaign's DM is told what is different.

## Say what kinds of entry exist {#types}

Packs hold Library entries, and `entityTypes` says which kinds there can be. Lantern Run has one, the talent:

```yaml
entityTypes:
  - { type: talent, label: Talent, pluralLabel: Talents, folder: talents, playerFacing: true }

conditions: [Shaken, Lost]
```

`folder` is where entries of that type live inside a pack. `playerFacing: true` lets players see them in their pickers. `conditions` fills the condition picker at the live table.

The Forged in the Dark SRD system does the same, with extra fields on some types:

```yaml
entityTypes:
  - { type: crew-upgrade, label: Crew upgrade, pluralLabel: Crew upgrades, folder: crew-upgrades, fields: [{ key: cost, label: Upgrade boxes, type: Number }] }
```

## Add a sheet {#sheet}

A **sheet kind** is one kind of character sheet. A system can have several: the Forged in the Dark SRD system has a scoundrel sheet and a crew sheet. Ours has one, the delver. Everything about the sheet goes under it.

```yaml
sheetKinds:
  - kind: delver
    label: Delver
    role: player
```

### A set of skills {#skills}

A **keyed list** is a fixed set of named things, each with its own fields. Skills are a good fit. Here each skill has a rating of 0 to 3, drawn as dots, and a `group` we will use in a moment:

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

The Forged in the Dark SRD system has twelve actions in exactly this shape.

### A derived value {#derived}

A **value** is a number on the sheet. Give it a `formula` and the app works it out whenever something changes. Formulas read other values, keyed lists and lists. This one counts the skills that have at least one dot:

```yaml
    values:
      - { key: trained, label: Trained skills, formula: "count(skills, s => s.rating >= 1)" }
      - { key: packMax, label: Pack slots, formula: "4 + trained", modifiers: packMax }
```

`packMax` starts at 4 and adds one for each trained skill. The `modifiers: packMax` part says that anything else on the sheet that targets `packMax` is added on top. We use that in a moment.

The SRD 5.2.1 system has the same idea as a one-line formula:

```yaml
    - { key: proficiencyBonus, label: Proficiency Bonus, formula: "2 + (level - 1) / 4", category: Combat }
```

Arithmetic is whole numbers. `floor`, `ceil`, `max` and `min` are there when you need them.

### Things that go up and down {#tracks}

A **track** is a number that changes in play and has bounds: hit points, stress, oil in a lantern.

```yaml
    tracks:
      - { key: lantern, label: Lantern oil, max: 6, start: max, display: boxes }
      - { key: nerve, label: Nerve, max: 5, display: boxes }
```

A track can do more than count. In the Forged in the Dark SRD system, a full stress track clears itself and adds one trauma, and the player picks which:

```yaml
      - key: stress
        label: Stress
        max: 9
        display: boxes
        overflow: { into: trauma, by: 1, then: reset, ask: { who: player, choices: [Cold, Haunted, Obsessed, Paranoid, Reckless, Soft, Unstable, Vicious] } }
```

### Lay the sheet out {#layout}

Without a `layout`, the app lists every value, track and list in turn. A layout puts them into tabs, each a section with widgets:

```yaml
    layout:
      - { section: Skills, widgets: [{ widget: table, key: skills, groupBy: group, columns: [rating] }, { widget: values, keys: [trained, packMax] }] }
      - { section: Condition, widgets: [{ widget: track, key: lantern }, { widget: track, key: nerve }, { widget: moments }] }
      - { section: Talents, widgets: [{ widget: list, key: talents }, { widget: features }] }
```

The widgets used here are `table`, `values`, `track`, `list`, `moments` and `features`. Each section needs at least one widget. The reference lists the rest.

## Give the sheet something to pick {#features}

A **list** holds picks from the Library: classes, feats, spells, talents. A **feature source** says that the entries picked on a list can carry rules of their own. Add both to the delver:

```yaml
    features:
      - { list: talents }

    lists:
      - key: talents
        label: Talents
        type: talent
```

Now the rules do not live in `system.yaml` at all. They live on each talent, in its own file under `properties`. There are three things a feature can do.

### Change a number: `modifiers` {#modifiers}

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

Anyone who picks this talent has two more pack slots. The `target` is the key of a value. The `amount` is a number or a formula. The Forged in the Dark SRD system's Mule ability is the same file with a different target and a smaller bonus:

```yaml
properties:
  modifiers:
  - target: LoadMax
    amount: 2
```

### Give something to spend: `uses` {#uses}

A **use** is a counter on the sheet that a feature gives, such as "once per camp". The `recovers` part says when it refills:

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

`camp` is not built in. We declare it in the next step. A rest, `initiative` and `dawn` also work in `on`. The SRD 5.2.1 system's Rage recovers part of its uses on one rest and all of them on another, and its maximum is a formula over the character's level:

```yaml
    uses:
      key: rage
      max: 'classLevel >= 17 ? 6 : classLevel >= 12 ? 5 : classLevel >= 6 ? 4 : classLevel >= 3 ? 3 : 2'
      recovers:
      - on: Short Rest
        amount: 1
      - on: Long Rest
```

### Let the player choose: `adds` {#choice}

An `adds` line gives the sheet something, and when it has a `choice`, the player decides what. This talent lets the player pick one skill:

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

The Forged in the Dark SRD system's Expertise ability is written exactly like this. The `choice` is a name of your own for the pick. The sheet stores it, shows a picker, and tells the player while a choice is still open.

A choice can also pick Library entries, and read an earlier choice. This is the SRD 5.2.1 system's Magic Initiate feat, shortened: first pick a spell list, then pick two cantrips from it.

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

The first line has no `to`, so it adds nothing to the sheet: it is only a value other lines can read. Write it before the lines that depend on it.

## Add a moment {#moments}

A **moment** is a named time when counters come back. Rests, `initiative` and `dawn` already exist. Declare your own in `system.yaml`:

```yaml
moments:
  - { key: camp, label: Camp }
```

That is enough for `second-wind` to refill. The `moments` widget in our layout gives the sheet a **Camp** button, and pressing it refills every counter that recovers `on: camp`.

A moment can also fire by itself when a value changes. The Forged in the Dark SRD system's load moment fires when the player sets the sheet's `load` value:

```yaml
moments:
  - { key: downtime, label: Downtime }
  - { key: loadChosen, label: Load chosen, onSet: load }
```

## The pack's manifest {#manifest}

Every pack has a `manifest.yaml` that says which system it is for:

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

`systemId` must match the system's `id`. Without it the pack has nowhere to go.

## Translate it {#translations}

A translation never changes the rules. Keys, ids and formulas stay in English, and only the words people read are translated.

Put the system's words in a sibling file named for the language, `system.ru.yaml`. It uses the same keys, and the sheet's own labels (the kind, skills, tracks and sections) go under `values`, looked up by their English word:

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

The entries get a **translation pack**, a sibling folder of the pack it translates. It has the same type folders, and each file lists entries by `id` with only the text to show:

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

Hebrew is the same with `system.he.yaml`. The app reads the page right to left by itself.

> [!TIP]
> Machine translations are a fine first draft. Say so in the pack's description, and ask a native speaker to read it before you share it.

## Check it {#check}

You do not have to start the app to find out whether a system loads. The checker loads it the way the server does and prints what is wrong. It needs the .NET SDK and a copy of the repository:

```bash
dotnet run --project tools/Codex.SystemCheck -- \
  content/systems/lantern-run
```

A good run prints `LOADED`. Anything it dislikes is a `PROBLEM` line: a key the app does not read, a missing script, a list that does not exist. Fix each one. A misspelt key is only ignored, never an error, so this is how you find it.

You can also write expectations, small checks in `spec.yaml` that the checker runs for you:

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

Mara has two trained skills, so her pack is 4 + 2, and Deep pockets adds 2 more. The checker prints `PASS` for each one that holds and `FAIL`, with what came out instead, for each that does not.

## Load it in the app {#load}

There are two ways, depending on how you run the app.

**From source.** The app reads the repository's `content/` folder, so a folder you put there is a system. In development the app watches the folder and reloads on its own a couple of seconds after you save. If it does not, open [[ui:Page.About.Title]] and press [[ui:Page.About.Reload]].

**An installed app.** Make a zip of the system's folder, then use [[ui:Page.About.Install]] on [[ui:Page.About.Title]] and choose the file. The checker makes the zip for you:

```bash
dotnet run --project tools/Codex.SystemCheck -- \
  content/systems/lantern-run --pack lantern-run.zip
```

The page tells you what the zip replaces and what changed before anything is installed, and an install that does not load is put back as it was. By default only the server's admin can install.

Then press [[ui:Page.Home.New]], give the campaign a name and choose your system. Open a sheet and try it:

1. Give the delver a few skill dots and watch **Trained skills** and **Pack slots** change.
2. Add the **Deep pockets** talent. Pack slots go up by two.
3. Add **Specialist** and pick a skill when the sheet asks.
4. Add **Second wind**, use it, then press **Camp** and see it come back.

When you change the rules, raise `version`. The campaign's DM sees what changed the next time they open it.

## Common mistakes {#mistakes}

- **A misspelt key is silently ignored.** `moddifiers` does not fail; it just does nothing. Run the checker after every change, and use the schema comment so your editor catches it first.
- **Changing the `id`.** Campaigns find their system by it. Change `name` as often as you like, never `id`.
- **Forgetting to raise `version`.** Players and DMs cannot tell the rules changed.
- **A modifier that nothing reads.** A feature's `target` has to be the key of a value, and a value only picks up modifiers when it names its own key in `modifiers`, as `packMax` does above. Without that line the talent loads fine and changes nothing.
- **Declaring a moment but never using it, or using one you never declared.** `recovers: [{ on: camp }]` needs `camp` in `moments` (or a rest of that name).
- **A layout section with no widgets.** Every section needs at least one.
- **A `choice` that reads a later one.** Write the choice that others read first.
- **Translating keys.** In `system.ru.yaml` and in a translation pack, ids, keys, formulas and `target`s stay English. Only display text changes. And a translated row list must have every row, in the same order.
- **A translation pack inside the pack it translates.** Put it in a sibling folder.
- **Copying a publisher's text.** Rules are not the same as the words someone wrote about them. Use your own words and keep the licence block honest.

## Where to go next {#next}

- The [full `system.yaml` reference](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/SYSTEM_YAML_REFERENCE.md), every key and type.
- [Systems and content packs](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/CONTENT_PACKS.md), the pack format and how loading and versions work.
- The two bundled systems, which use almost everything described here.
- A [new issue](https://github.com/myarichuk/TTRPG.Codex/issues) if a rule you want cannot be written as data yet.
