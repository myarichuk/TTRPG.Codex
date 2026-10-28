---
title: Using TTRPG.Codex
lead: A walkthrough for DMs and players, in the order a campaign actually unfolds. Every button name below is exactly as the app shows it.
description: A usage guide to TTRPG.Codex: creating a campaign, inviting players, characters and sheets, the world wiki, the Library and homebrew, running the live table, session recaps, optional AI and campaign settings.
---

## The big picture {#overview}

A **campaign** has one DM (the person who made it) and any number of players. Players join with an invite link and follow along in their own browser. What each person sees depends on their role, so the DM can prepare secrets freely: nothing reaches a player until it is revealed.

Inside a campaign, a side rail holds the sections. On a phone it becomes a strip along the bottom.

| Section | What it is for |
| --- | --- |
| [[ui:Page.Campaign.Section.Overview]] | The description, a cast summary, who is online, members and the invite link. |
| [[ui:Page.Campaign.Section.Table]] | The live table: initiative, hit points, conditions, dice. |
| [[ui:Page.Campaign.Section.Sessions]] | Planned, live and finished sessions, with recaps, notes and the event log. |
| [[ui:Page.Campaign.Section.Journal]] | Your private notebook. Nobody else can read it, the DM included. |
| [[ui:Page.Campaign.Section.Cast]] | Player characters, NPCs and monsters, with portraits. |
| [[ui:Page.Campaign.Section.Bestiary]] | The DM's shortlist of creatures for upcoming encounters. |
| [[ui:Page.Campaign.Section.Places]] | Regions and the places inside them. |
| [[ui:Page.Campaign.Section.Wiki]] | Linked pages about your world. |
| [[ui:Page.Campaign.Section.Build]] | The AI world builder (optional). |
| [[ui:Page.Campaign.Section.Lore]] | Facts, and who knows them. |
| [[ui:Page.Campaign.Section.Library]] | Rules reference and your homebrew. |
| [[ui:Page.Campaign.Section.Settings]] | Details, packs, members, backup. |

![The campaign overview: the cast at a glance, members and the invite link.](shot:overview)

## Create your campaign {#start}

1. Sign in and press [[ui:Page.Home.New]]. Give the campaign a name and choose a game system. The system decides which character sheet, conditions and Library content you get.
2. Open [[ui:Page.Campaign.Section.Settings]] to add a tagline, a description players will see on the overview, and cover art.
3. Copy the **invite link** from the Overview (or from Settings) and send it to your players.

When a player opens the link they sign in or create an account and land in the campaign as a player. A link works for anyone who has it, so if it spreads further than you wanted, press [[ui:Campaign.Settings.NewLink]] in Settings: the old link stops working.

![The Campaigns page, with a campaign card and a New campaign button.](shot:home)

> [!TIP]
> Only people who can reach your computer can open the link at all. See [Let your players join](../install/#players) for Wi-Fi and Tailscale.

## Characters, NPCs and monsters {#cast}

Open [[ui:Page.Campaign.Section.Cast]].

- **Players** press [[ui:Campaign.Cast.NewPlayer]] and build their own character. Each player can keep their own characters and retire them.
- **The DM** presses [[ui:Campaign.Cast.NewDm]] for an NPC or monster, or [[ui:Campaign.Cast.CreateWithAi]] to turn a one-line concept into a draft sheet that uses only classes, species and backgrounds in your Library.

Every NPC and monster has a visibility, and this is how you keep secrets:

| State | What players see |
| --- | --- |
| [[ui:Campaign.Cast.HiddenFromPlayers]] | Nothing at all, not even a name. |
| [[ui:Campaign.Cast.IdentityUnknown]] | Only a public name and description, such as "A figure in a wet cloak". |
| [[ui:Actor.Sheet.VisLabelKnown]] | The full character, name and public bio. |

Use [[ui:Campaign.Cast.Reveal]] and [[ui:Campaign.Cast.Hide]] on a row to move a character between states as the story demands.

![The cast: player characters on the left, NPCs and monsters on the right, with Reveal and Hide controls.](shot:cast)

The [[ui:Page.Campaign.Section.Bestiary]] is a shortlist of Library creatures you expect to need, each with a note and a count. When the fight starts, add them to the cast in one click.

## The character sheet {#sheet}

Click any character to open their sheet. It is drawn from the game system's definition, so a homebrew system gets its own sheet. At the top, a plate shows the portrait, name, class and level, and the numbers that matter: hit points, armor class, proficiency bonus, initiative and speed.

![A character sheet with the stat plate, attribute scores and the level-up panel.](shot:sheet)

Below it, tabs hold the attributes and skills, the picks (feats, spells and so on), the inventory, the history and, for the DM only, private notes about the character. Press [[ui:Actor.Sheet.SaveChanges]] when you are done; a counter next to it shows how many edits are unsaved.

**Rule checks.** Point buy, class spell lists, subclass and feat limits come from the system. Under [[ui:Page.Campaign.Section.Settings]] you set **[[ui:Campaign.Settings.Rules.Label]]** to *Strict* (players cannot save a sheet that breaks a rule), *Warn only*, or *Off*. The DM can always enter scores by hand.

**Levelling up.** The [[ui:Actor.Sheet.LevelUp]] panel explains what the next level brings from the Library's class entries and which choices it forces. With an AI model set up, it can also suggest picks that fit the character and the story.

**Live hit points.** Hit points on the sheet and on the live table are the same number: change one and the other follows.

## Build your world {#world}

### Places {#places}

[[ui:Page.Campaign.Section.Places]] holds regions and the places inside them, which can nest (a tavern inside a village inside a coast). New places start hidden; press [[ui:Campaign.World.Reveal]] to show one to the party. Setting packs can add ready-made places too.

![Regions and places, with Reveal and Edit buttons, plus places offered by packs.](shot:places)

### The wiki {#wiki}

[[ui:Page.Campaign.Section.Wiki]] is a set of pages you write in plain text.

- Link pages by typing a title in double brackets, like `[[The Drowned Chapel]]`. Backlinks list what mentions what.
- Mention a character with `@`, a place with `#` and an item with `$`.
- Every page starts as [[ui:Campaign.Wiki.DmOnly]]. Press [[ui:Campaign.Wiki.SharePage]] to make it readable by the whole party.
- Every place already has a wiki page of its own, and a page can be put on the map, or promoted into the cast.
- Players can leave notes on any page they can read.

![A wiki page about a harbour, with a link to another page, backlinks and a notes panel.](shot:wiki-page)

### Lore {#lore}

[[ui:Page.Campaign.Section.Lore]] is for facts. Each fact is visible to the DM only, revealed to one character who then knows it, or shared with the whole party. Add one with [[ui:Campaign.Lore.AddFact]].

![The lore page: facts with their visibility.](shot:lore)

## The Library and homebrew {#library}

[[ui:Page.Campaign.Section.Library]] is your rules reference: classes, subclasses, species, backgrounds, feats, spells, creatures, conditions, equipment, magic items and a rules glossary. Search by name, tag or text, and filter by type.

![The Library showing creatures, with a creature's entry open on the right.](shot:library)

The DM can extend it:

- [[ui:Campaign.Library.NewHomebrew]] walks you through writing an equipment item, class, subclass, feat, spell or creature of your own. Homebrew starts hidden from players.
- [[ui:Campaign.Library.HomebrewCopy]] on any entry makes your own editable variant of it.
- [[ui:Campaign.Library.ImportFromCampaign]] brings homebrew over from another campaign you run.
- [[ui:Campaign.Library.ExportPack]] saves homebrew as a pack file, all of it or only entries carrying a tag you choose, and [[ui:Campaign.Library.ImportPack]] loads one. That is how you share a set of rules with another DM.

To add a whole new game system, or a setting pack, write YAML files and drop them in your `content` folder. The format is described in [the content pack documentation](https://github.com/myarichuk/TTRPG.Codex/blob/main/docs/CONTENT_PACKS.md).

## Run the table {#table}

This is the part you use at the table.

1. Open [[ui:Page.Campaign.Section.Table]] and press [[ui:Combat.Console.StartTable]]. Every player's page attaches to the running table; until then they see “[[ui:Combat.Console.WaitingDm]]”. Players press [[ui:Campaign.Overview.JoinTable]].
2. Add combatants with [[ui:Combat.AddToInitiative]]: pick someone, then either roll a d20 for them or type their total and press [[ui:Combat.Set]]. Use [[ui:Combat.QuickAdd]] to search the Library and drop a monster straight in.
3. Press [[ui:Combat.NextTurn]] to move through the order ([[ui:Combat.Previous]] steps back). The active turn is highlighted, and the round counter advances.
4. Click a combatant to open their controls. Apply damage or healing, add temporary hit points, and add a condition with a round count: it counts down by itself and expires at the right moment. Death saves, hit dice, spell slots and exhaustion are tracked here as well.
5. The dice tray rolls any expression such as `1d20` or `2d6+3`. Tick **[[ui:Combat.SecretRoll]]** and only you see the result.
6. The [[ui:Combat.Notes.Tab]] tab on the edge of the screen holds quick notes you can mention people and places in. They flow into the session log.
7. When the fight is over, press [[ui:Combat.EndEncounter]]. When the evening is over, press [[ui:Combat.EndTable]].

![The live table: initiative order, hit-point bars, condition badges and the dice tray.](shot:table)

### What players see {#players-view}

Players follow the same initiative order and see changes the moment you make them. They see exact numbers for their own characters, while creatures show only how they look ([[ui:Combat.Hp.Healthy]], [[ui:Combat.Hp.Bloodied]] and so on), and hidden NPCs do not appear in the order at all. On a phone the table is laid out for one hand.

![A player's phone: the initiative order and a character sheet.](shot:phone-table)
![A player's phone: the initiative order and a character sheet.](shot:phone-sheet)

## After the session {#after}

Open [[ui:Page.Campaign.Section.Sessions]] and choose the session. Everything that happened at the table is in its event log: rolls, damage, conditions, turns.

- **Recap.** Write what the party should remember, or press [[ui:Session.Prefill]] to start from the log. Press [[ui:Session.SaveRecap]] and the players can read it.
- **DM summary.** A separate, secret summary for you. It can include things the players never saw.
- **Table notes** can be private to their author or shared with the party.
- **My journal** is each person's private notebook, one entry per session or free-standing. Nobody else sees it, not even the DM. Save with [[ui:Session.SaveJournal]].

![A session page with the recap editor, the private journal and the event log.](shot:session)

With an AI model set up, [[ui:Session.DraftAi]] writes a first draft of the recap. The player draft is built only from material the players could see; secret rolls and private notes never reach it.

## Optional AI {#ai}

AI is off until you set it up, and it never runs by itself. Click your name in the header and choose [[ui:Layout.User.AiSettings]].

1. Add a **text model**. OpenRouter is pre-selected (one key, most current models), and there are presets for OpenAI, Groq, Mistral, a local Ollama or LM Studio, and any custom `/v1/chat/completions` endpoint.
2. Paste your key. Keys are stored encrypted on your server, and settings are per user.
3. Optionally add an **image model** to draw portraits and art.

![AI settings: text and image models, with OpenRouter pre-selected.](shot:ai-settings)

What it can do: draft session recaps, write or rewrite a character's history or the DM's notes on an NPC, give level-up advice, build a character from a concept, expand rough wiki notes, and, in the [[ui:Page.Campaign.Section.Build]], hold named chats that read your campaign and Library and propose places, people, factions, quests, secrets, spells, items and creatures. Each proposal is reviewed entry by entry, and **nothing is saved until you apply it**; everything starts hidden. Every tool call the model makes can be opened, down to the raw JSON.

> [!NOTE]
> The world builder needs a model that supports tool calls. Nothing is sent to any model until someone clicks an AI button, and the AI writes in the reader's language.

## Campaign settings {#settings}

[[ui:Page.Campaign.Section.Settings]] is for the DM and holds:

- **Details:** name, tagline, description, cover art and the sheet-check rule.
- **[[ui:Campaign.Settings.Packs.Title]]:** untick a pack to keep it out of this campaign's Library.
- **[[ui:Campaign.Settings.Members]]:** give each person a role of [[ui:Campaign.Settings.Role.DM]], [[ui:Campaign.Settings.Role.Player]] or [[ui:Campaign.Settings.Role.Observer]], or remove them. Removed players lose access; their characters stay in the cast.
- **Invite link:** copy it, or make a new one.
- **Backup:** [[ui:Campaign.Settings.Export]] downloads the whole campaign as one file, and [[ui:Campaign.Settings.Import]] restores it, even as a new campaign. Export regularly.

![Campaign settings: details, content packs, members and backup.](shot:settings)

## Language and theme {#language}

TTRPG.Codex speaks English, Russian and Hebrew. Pick the language from the picker in the header or on the sign-in page; it is remembered for your account. The interface, the game system's labels and the SRD rules content all follow it, and Hebrew mirrors the layout right to left. The launcher has its own language setting.

The header also has a theme toggle: dark stone by default, or [[ui:Layout.Theme.ToParchment]].

![The live table in the light parchment theme.](shot:table-light)

You are ready to play. If something does not work, the [install guide](../install/#troubleshooting) has a troubleshooting section, and [issues](https://github.com/myarichuk/TTRPG.Codex/issues) are welcome.
