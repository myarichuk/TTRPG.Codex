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

Until the campaign is set up, the Overview shows you a [[ui:Campaign.Setup.Title]] list: describe the campaign, invite players, add a character, write a page, put a place on the map and plan a session. Each step ticks itself off when it is done, however it got done: the pages, places and cast members you accept from the [[ui:Page.Campaign.Section.Build]] count too. The invite step copies the link; it ticks off once a player joins. The cross puts the list away for good, and so does finishing it.

![The Overview's Getting started list: the campaign described, the players invited and the cast begun; a page, a place and a session still to do.](shot:setup)

When a player opens the link they sign in or create an account and land in the campaign as a player. A link works for anyone who has it, so if it spreads further than you wanted, press [[ui:Campaign.Settings.NewLink]] in Settings: the old link stops working. Before making one, choose how long new links work: [[ui:Campaign.Settings.InviteValidity.Forever]], [[ui:Campaign.Settings.InviteValidity.Week]], [[ui:Campaign.Settings.InviteValidity.Day]], or [[ui:Campaign.Settings.InviteValidity.Once]] (the first player to use it gets in, and nobody after them).

![The Campaigns page, with a campaign card and a New campaign button.](shot:home)

> [!TIP]
> Only people who can reach your computer can open the link at all. See [Let your players join](../install/#players) for Wi-Fi and Tailscale.

## Characters, NPCs and monsters {#cast}

Open [[ui:Page.Campaign.Section.Cast]].

- **Players** press [[ui:Campaign.Cast.NewPlayer]] and build their own character. Each player can keep their own characters and retire them with [[ui:Campaign.Cast.Retire]]. A retired character leaves the cast and the table, but notes and pages that mention it keep its name, and the DM can press [[ui:Campaign.Cast.Restore]] in the retired list at the foot of the cast to put it back.
- **The DM** presses [[ui:Campaign.Cast.NewDm]] for an NPC or monster, or [[ui:Campaign.Cast.CreateWithAi]] to turn a one-line concept into a draft sheet that uses only classes, species and backgrounds in your Library.

Every NPC and monster has a visibility, and this is how you keep secrets:

| State | What players see |
| --- | --- |
| [[ui:Campaign.Cast.HiddenFromPlayers]] | Nothing at all, not even a name. |
| [[ui:Actor.Sheet.VisLabelUnknown]] | A stranger: only the public name and description you give on the sheet, such as "A figure in a wet cloak". Never the real name, history, portrait or sheet, though the stranger can still be mentioned, speak, and take part in a fight. |
| [[ui:Actor.Sheet.VisLabelKnown]] | The full character, name and public bio. |

Use [[ui:Campaign.Cast.Stranger]], [[ui:Campaign.Cast.Reveal]] and [[ui:Campaign.Cast.Hide]] on a row to move a character between states as the story demands.

![The cast: player characters on the left, NPCs and monsters on the right, with Reveal and Hide controls.](shot:cast)

The [[ui:Page.Campaign.Section.Bestiary]] is a shortlist of Library creatures you expect to need, each with a note and a count. When the fight starts, add them to the cast in one click.

## The character sheet {#sheet}

Click any character to open their sheet. It is drawn from the game system's definition, so a homebrew system gets its own sheet. At the top, a plate shows the portrait, name, class and level, and the numbers that matter: hit points, armor class, proficiency bonus, initiative and speed.

![A character sheet with the stat plate, attribute scores and the level-up panel.](shot:sheet)

Below it, tabs hold the attributes and skills, the picks (feats, spells and so on), the inventory, the history and, for the DM only, private notes about the character. Press [[ui:Actor.Sheet.SaveChanges]] when you are done; a counter next to it shows how many edits are unsaved. Ctrl+S (Cmd+S on a Mac) saves too, here and in every other editor; on a phone, Save stays at the bottom of the screen while you write.

**Rule checks.** Point buy, class spell lists, subclass and feat limits come from the system. Under [[ui:Page.Campaign.Section.Settings]] you set **[[ui:Campaign.Settings.Rules.Label]]** to *Strict* (players cannot save a sheet that breaks a rule), *Warn only*, or *Off*. The DM can always enter scores by hand. A character with several classes prepares spells against each class's own limit, so a wizard 5 / cleric 1 cannot spend the cleric's share on wizard spells. A homebrew class with no hit die, or with spell slots but no spellcasting ability, is named in a warning, and so are skills chosen beyond what the class offers.

**Carried weight.** The inventory adds up what the character carries and shows it against what they can carry (for the SRD, Strength × 15 pounds). Carrying more is a warning, not a block.

**Levelling up.** The [[ui:Actor.Sheet.LevelUp]] panel explains what the next level brings from the Library's class entries and which choices it forces. With an AI model set up, it can also suggest picks that fit the character and the story.

**Live hit points.** Hit points on the sheet and on the live table are the same number: change one and the other follows.

## Build your world {#world}

### Places {#places}

[[ui:Page.Campaign.Section.Places]] holds regions and the places inside them, which can nest (a tavern inside a village inside a coast). New places start hidden; press [[ui:Campaign.World.Reveal]] to show one to the party. Setting packs can add ready-made places too.

![Regions and places, with Reveal and Edit buttons, plus places offered by packs.](shot:places)

### The wiki {#wiki}

[[ui:Page.Campaign.Section.Wiki]] is a set of pages you write in plain text.

![The wiki's page list, with filters by kind, sorting, and a count of the DM-only blocks on a page.](shot:wiki-list)

- Link pages by typing a title in double brackets, like `[[The Drowned Chapel]]`: typing `[[` offers the pages you have, and the page list beside the editor puts a link where the cursor is. Backlinks list what mentions what. Hover over a link or a mention to see a card with the first lines and the picture; it shows only what the reader is allowed to see.
- The page list filters by kind (person, place, faction, quest) as well as by tag, and sorts by title or by when a page last changed.
- Mention a character with `@`, a place with `#` and an item with `$`.
- Every page starts as [[ui:Campaign.Wiki.DmOnly]]. Press [[ui:Campaign.Wiki.SharePage]] to make it readable by the whole party.
- A shared page can still hide parts of itself. Put a secret between a line `:::dm` and a line `:::` (the eye button in the toolbar, or Ctrl+Shift+H, does it for you): players never receive that text, not even its links. Change `:::dm` to `:::party` when the party learns it, and use `:::players` for what the players are told instead of the truth. The same works in a place's description and a character's history.
- Press [[ui:Campaign.Wiki.ViewAs.Party]] above a page to read it exactly as the players do.
- [[ui:Campaign.Wiki.Secrets.PlayersVersion]] beside a secret writes what the players are told instead (drafted by the AI when one is set up): you read and change it, and it goes in as a `:::players` block right after the secret.
- A block `:::for @Aria` in a character's history is read by Aria's player only, apart from the history they edit.
- To reveal a secret at the table, press [[ui:Common.Secret.Reveal]] on the block itself, or [[ui:Campaign.Wiki.Secrets.RevealEllipsis]] in [[ui:Campaign.Wiki.Secrets]] beside the page, which lists every secret on it. Either opens a dialog with the whole secret in it: reveal it to the party, or to some characters only (it becomes `:::for @Aria`). [[ui:Common.Secret.Hide]] takes it back. If a session is running, the log notes that something was revealed on the page (its title, never the secret). Players see what was revealed since they last opened a page marked [[ui:Campaign.Wiki.NewReveal]].
- **Templates** save typing. In a page (and in a place's description, a character's history and your DM notes), type a template's abbreviation and press Tab (`enc` for an encounter, `npc` for a person in brief, `dmb` for a DM-only block, `dmp` for the truth beside what players are told), or type `/` and pick one. The template goes in and Tab moves from field to field (Shift+Tab goes back, Esc stops); on a phone or tablet, the arrows on the hint under the box do the same. As in a code editor, a field waiting to be filled shows its name, greyed, and typing replaces it; a name you leave standing is taken out when you finish or save. A field used twice fills in both places. A new page can start from the outline of its kind, and a page written earlier gets a button that adds the headings it lacks. Change the built-in templates, turn them off or write your own under [[ui:Campaign.Templates.Title]] (the button at the top of the wiki); the AI writes pages to your outlines too. In a template, `$name$` is a field, `$name:text$` a field with text already in it, and `$END$` where the cursor ends up.
- Every place already has a wiki page of its own, and a page can be put on the map, or promoted into the cast.
- Players can leave notes on any page they can read.

![A wiki page as the DM sees it: a DM-only secret with what the players are told instead, and the card a link shows on hover.](shot:wiki-page)
![Writing a page: typing double brackets offers the other pages, and the bar under the text says the changes are not saved yet.](shot:wiki-edit)

### Lore {#lore}

[[ui:Page.Campaign.Section.Lore]] is for facts. Each fact is visible to the DM only, revealed to one character who then knows it, or shared with the whole party. Add one with [[ui:Campaign.Lore.AddFact]]. Mention @characters and #places in its details, or link a `[[page]]`, and the fact shows on their pages and sheets too; a page or sheet has a button to write one more about it. The DM also finds every secret of the wiki here, with who it is revealed to, and players find what the wiki revealed to them, newest first.

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
4. Click a combatant to open their controls. Apply damage or healing, add temporary hit points, and add a condition with a round count: it counts down by itself and expires at the right moment. Death saves, hit dice, spell slots and exhaustion are tracked here as well. Pressed the wrong button? The undo button in the same panel takes back the last change to that creature.
5. The dice tray rolls any expression such as `1d20` or `2d6+3`. Tick **[[ui:Combat.SecretRoll]]** and only you see the result.
6. The [[ui:Combat.Notes.Tab]] tab on the edge of the screen holds quick notes you can mention people and places in. They flow into the session log.
7. When the fight is over, press [[ui:Combat.EndEncounter]]. When the evening is over, press [[ui:Combat.EndTable]].

![The live table: initiative order, hit-point bars, condition badges and the dice tray.](shot:table)

### What players see {#players-view}

Players follow the same initiative order and see changes the moment you make them. They see exact numbers for their own characters, while creatures show only how they look ([[ui:Combat.Hp.Healthy]], [[ui:Combat.Hp.Bloodied]] and so on), and hidden NPCs do not appear in the order at all. While a hidden creature acts, the turn banner reads “[[ui:Combat.SomeoneUnseen]]”, as a DM pauses behind the screen; its name, rolls and place in the order stay hidden. On a phone the table is laid out for one hand.

![A player's phone: the initiative order and a character sheet.](shot:phone-table)
![A player's phone: the initiative order and a character sheet.](shot:phone-sheet)

## After the session {#after}

Open [[ui:Page.Campaign.Section.Sessions]] and choose the session. Everything that happened at the table is in its event log: rolls, damage, conditions, turns.

- **Recap.** Write what the party should remember, or press [[ui:Session.Prefill]] to start from the log. Once it is saved, the players can read it.
- **DM summary.** A separate, secret summary for you. It can include things the players never saw.
- **Table notes** can be private to their author or shared with the party.
- **My journal** is each person's private notebook, one entry per session or free-standing. Nobody else sees it, not even the DM.

One [[ui:Common.Save]] at the bottom of the page saves everything you changed here, the recap, the summary and your journal alike; Ctrl+S does the same. The DM can remove a session with [[ui:Session.Delete.Label]], though not while the table is running it.

![A session page with the recap editor, the private journal and the event log.](shot:session)

With an AI model set up, [[ui:Session.DraftAi]] writes a first draft of the recap. The player draft is built only from material the players could see; secret rolls and private notes never reach it.

## Print your campaign as a book {#book}

[[ui:Page.Campaign.Section.Book]], the last item under World in the campaign's side rail, opens the book on a page of its own, for the GM only (Settings has a link to it): players do not see the book, and its pages and files are refused to them. It turns the campaign into a book you can edit, then print as a PDF: a title page with the campaign cover, a contents page, and and chapters in the order of a published adventure: an introduction, the story so far (the sessions), the places, the lore and the wiki pages, then the people the party meets and the homebrew as appendices. The players' own characters are not in it. The book is built on the server's own machine from what the campaign holds. Nothing is sent anywhere unless you choose an AI step, and each of those shows its cost first.

The page has a bar on top, the chapters on the left, the chapter's text in the middle and the printed pages on the right, and a dock below with three tabs: **[[ui:Campaign.Book.Draft.Ai.Title]]**, **[[ui:Campaign.Book.Tab.Art]]** and **[[ui:Campaign.Book.Draft.Problems]]**. Drag the bar between the text and the pages to change their widths, or focus it and press Left or Right. The address names the edition and the chapter, so a reload or a link comes back to the same place.

![The Book page: the chapters, the chapter's text, the printed pages and the dock below.](shot:book-panel)

### Pick an edition {#book-editions}

Choose the edition in the bar at the top before anything else. Each edition is a separate book with its own pictures and its own edits.

- **[[ui:Campaign.Book.Edition.Party]]** is made from exactly what a player may read. Hidden characters, DM-only pages and secrets are left out, not blacked out, and a character the party has not met appears under their public description only. This is the one to hand out.
- **[[ui:Campaign.Book.Edition.Dm]]** has everything you can see, with your secrets in boxes. Keep it behind the screen.

### Look, language and paper {#book-look}

The theme list in the bar sets the look. Under the three dots you pick the language and the paper (A4 or Letter). The pages on the right are what the PDF is printed from, and **[[ui:Campaign.Book.Draft.Download]]** prints them as they are. A book you have not touched already follows the campaign, so for a quick copy open the page and download it.

To change the words, type in the middle pane.

### Open the editor {#book-editor}

1. Open [[ui:Page.Campaign.Section.Book]] and choose the edition.
2. The page has three panes: the chapters on the left, the chapter's text in the middle and the printed pages on the right. Fold the chapters or the preview with the small button on their headers when you want more room.
3. Type in the middle pane. It is Markdown, and the pages on the right follow as you type. Changes are saved on their own: the title says *Saved* when they are.
4. Under the panes, the dock has three tabs: [[ui:Campaign.Book.Draft.Ai.Title]], [[ui:Campaign.Book.Tab.Art]] and [[ui:Campaign.Book.Draft.Problems]] (what needs your attention).
5. The theme list in the toolbar sets the look: [[ui:Campaign.Book.Draft.Theme.plain]] or [[ui:Campaign.Book.Draft.Theme.ashen-vellum]]. Under the three dots, **[[ui:Campaign.Book.Theme.Open]]** brings in a Homebrewery theme of your own.

![The book editor: chapters, the Markdown of the Cast chapter, the printed pages beside it, and the Ask AI bar below.](shot:book-editor)

> [!TIP]
> The three dots also hold **[[ui:Campaign.Book.Draft.StartOver]]**, which throws away your edits and makes the book again from the campaign. It asks first.

### When the campaign changes {#book-stale}

A chapter you have not edited quietly follows the campaign. A chapter you have edited never changes by itself. When the campaign moves on, it shows a notice, and you choose:

- **[[ui:Campaign.Book.Draft.Merge.Button]]** takes in the campaign's changes and keeps your edits. It works one entry at a time, and you review each change before it is kept. Only an entry that both you and the campaign changed ever needs an AI model, and the cost is shown first. Choose [[ui:Campaign.Book.Draft.Merge.WithoutAi]] to merge with no model at all: those entries then show the campaign's text, and you reject it to keep yours.
- **[[ui:Campaign.Book.Draft.Regenerate]]** replaces the chapter with a fresh one from the campaign. Your edits to it are gone, but **[[ui:Campaign.Book.Draft.Undo]]** brings them back.
- **[[ui:Campaign.Book.Draft.Keep]]** dismisses the notice until the campaign changes again.

![A chapter you edited, after the campaign changed: the notice with its three choices.](shot:book-stale)

### Fit pages {#book-fit}

Text does not always break into pages where you would like. **[[ui:Campaign.Book.Draft.Fit]]** lays the book out as it prints and writes a page break wherever a page runs over. Run it again after you edit. Anything too tall for a page even on its own is listed on the [[ui:Campaign.Book.Draft.Problems]] tab, so you can shorten or split it before it prints cut off.

### Pictures {#book-pictures}

The [[ui:Campaign.Book.Tab.Art]] tab of the dock is the tray. Open **[[ui:Campaign.Book.Art.Title]]** to see every place a picture can go: the cover, above a chapter, and beside an entry.

- **Your own pictures.** [[ui:Campaign.Book.Art.Upload]] puts one in a place (PNG, JPEG, WebP, GIF or AVIF, under 6 MB). [[ui:Campaign.Book.Art.Replace]] and [[ui:Campaign.Book.Art.Remove]] change it later. A picture added in the party edition prints in both books. One added in the GM edition prints in the GM book only.
- **The art director.** With a text model set up, **[[ui:Campaign.Book.Art.Suggest.Start]]** reads a short outline of the edition, without secrets, and suggests what each picture could show. Before it sends anything, the tray says how many requests it needs, about how many tokens, and the cost if you entered prices. Suggestions are text only.
- **Drawing.** With a picture model set up, tick the places you want drawn, choose a style for the whole book, and read the cost: the number of pictures and the price of each. Nothing is drawn until you press **[[ui:Campaign.Book.Art.Draw.Pick]]**. The picture a place already has is kept among its versions; if the place already has three, the tray says first which one a new drawing drops.
- **Versions.** A place keeps up to three pictures, whether you added them or the model drew them, and only the one in use prints. Open the place's versions to see them, press **[[ui:Campaign.Book.Art.Versions.Use]]** to switch, or **[[ui:Campaign.Book.Art.Versions.Delete]]** to remove one for good (it asks first). **[[ui:Campaign.Book.Art.Remove]]** only takes the picture out of the book: it stays among the versions, so you can put it back.
- **Small pictures and scenes.** Every entry can have one small picture that closes it, listed as "Small picture after" the entry. Each session of the Chronicle, in both editions, also has a place for a scene picture. The art director may suggest both, and they work like any other place.

![The Pictures tab: the places for pictures, the versions of one picture, the art director's suggestions and the drawing choices, before anything is run.](shot:book-art)

> [!NOTE]
> Both steps use the models from [[ui:Layout.User.AiSettings]], and bill your provider. Without a model the tray says so and everything else keeps working. Your own picture style from AI settings, if you set one, is used for every picture in place of the style choices.

### Optional AI {#book-ai}

All of this is optional, and it never runs by itself. Nothing reaches a model before you press a button, and the editor shows the cost first. Secrets are never sent: they are cut out before a model reads the text and put back afterwards.

- **Ask AI.** In the editor's [[ui:Campaign.Book.Draft.Ai.Title]] tray, select a passage, or nothing for the whole chapter, then press a preset: **[[ui:Campaign.Book.Draft.Ai.Preset.shape]]**, **[[ui:Campaign.Book.Draft.Ai.Preset.shorter]]**, **[[ui:Campaign.Book.Draft.Ai.Preset.readaloud]]** or **[[ui:Campaign.Book.Draft.Ai.Preset.tidy]]**. Or type what you want, such as "in the voice of a ship's log", and press **[[ui:Campaign.Book.Draft.Ai.Ask]]**.
- **As the book.** [[ui:Campaign.Book.Draft.Ai.Preset.shape]] rewrites a chapter the way this game's published books read, one entry at a time, from the campaign's notes. The three dots hold **[[ui:Campaign.Book.Draft.ShapeBook]]**: it goes chapter by chapter, and you review each chapter before the next is sent.
- **The cost, first.** Before anything is sent, you see the model, how many requests, about how many tokens, and what that is in money if you have entered prices in AI settings. Press **[[ui:Campaign.Book.Draft.Ai.Send]]** to go on, or Cancel.
- **You review everything.** The model's answer is never written straight into the book. It comes up as a list of changes, with what goes struck through and what comes in marked. Accept or reject each one, then **[[ui:Campaign.Book.Draft.Ai.KeepAccepted]]**, **[[ui:Campaign.Book.Draft.Ai.AcceptRest]]** or **[[ui:Campaign.Book.Draft.Ai.Discard]]**. A reply that adds names, numbers or secrets the campaign does not hold is refused.

The [[ui:Campaign.Book.Draft.Ai.Title]] tab also says what this edition's AI work has cost so far, over every run.

> [!NOTE]
> A rewrite is a tidy-up, not new writing: the model is told to invent nothing. It costs your own tokens, and the estimate is only an estimate.

### Check the party book before you hand it out {#book-leak}

Once you can type freely, nothing can stop you pasting a secret into the party book. So when you download the party edition, the editor first compares it with the GM edition. If it finds words of a secret, the name of a hidden character, or anything else only the GM edition should hold, nothing is printed. The [[ui:Campaign.Book.Draft.Problems]] tab opens with each hit, the chapter it is in (click it to go there) and the words found.

Fix the text, or press **[[ui:Campaign.Book.Draft.Leak.Anyway]]** if you know better. It is a warning, not a lock. The GM edition is not checked.

![The leak check: a hidden character's name typed into the party book's Cast, found before anything is downloaded.](shot:book-leak)

### A copy for each player {#book-players}

Some things only one player's character was told. Write them for that character in the campaign (a `:::for` block), and they stay out of the party book. In the editor of the party edition, open the three dots and look under **[[ui:Campaign.Book.Draft.ForPlayer]]**: every player who owns a character is listed by their characters' names. Choosing one makes that player's own copy: the party book, plus a note under each entry with what only their characters were told, headed "Only for" and their name. The file is named after the campaign and the characters. Nothing is stored, so make it again after you edit.

![The editor's three dots menu in the party edition, listing a copy for the player of Aria Stonehelm.](shot:book-player)

### Download {#book-download}

- **[[ui:Campaign.Book.Draft.Download]]**, in the editor's toolbar, prints the book on the server with Chrome, Edge or Chromium (one of them must be installed on that machine) and sends you the file. If a page still prints cut off, the Problems tab says which.
- **[[ui:Campaign.Book.Draft.Markdown]]**, under the three dots, gives the chapters as one text file for Homebrewery or a text editor. The pictures and the stylesheet are not in it. The GM edition holds every secret, so the editor asks before it makes that file.

![A printed party book: the cover with the campaign's picture, the Cast with two portraits, and a page in two columns.](shot:book-pages)

### Other languages {#book-languages}

The book follows the language you pick under the three dots on the Book page (it starts with the language of the app), in English, Russian or Hebrew. Each language has its own copy of the book, with its own edits, because the chapter titles are translated. The Plain and Ashen Vellum themes print Russian and Hebrew, and Hebrew is mirrored right to left. The Homebrewery layout is English only, and the editor greys it out for the other two.

## Optional AI {#ai}

AI is off until you set it up, and it never runs by itself. Click your name in the header and choose [[ui:Layout.User.AiSettings]].

1. Add a **text model**. OpenRouter is pre-selected (one key, most current models), and there are presets for OpenAI, Groq, Mistral, a local Ollama or LM Studio, and any custom `/v1/chat/completions` endpoint.
2. Paste your key. Keys are stored encrypted on your server and tied to your account, and settings are per user. If you set up AI in an older version, enter your keys once more: the page says which ones.
3. Optionally add an **image model** to draw portraits and art.

![AI settings: text and image models, with OpenRouter pre-selected.](shot:ai-settings)

The person running the server can also give the whole server a model and key in its settings file. Anyone without models of their own then uses those, within a daily allowance per account (200 requests and 20 pictures unless the server sets other numbers). AI settings says when this is so and how much you have used today; the server's admin sees everyone's use.

What it can do: draft session recaps, write or rewrite a character's history or the DM's notes on an NPC, give level-up advice, build a character from a concept, expand rough wiki notes, and, in the [[ui:Page.Campaign.Section.Build]], hold named chats that read your campaign and Library and propose places, people, factions, quests, secrets, spells, items and creatures. Each proposal is reviewed entry by entry, and **nothing is saved until you apply it**; everything starts hidden. Every tool call the model makes can be opened, down to the raw JSON.

> [!NOTE]
> The world builder needs a model that supports tool calls. Nothing is sent to any model until someone clicks an AI button, and the AI writes in the reader's language.

## Campaign settings {#settings}

[[ui:Page.Campaign.Section.Settings]] is for the DM and holds:

- **Details:** name, tagline, description, cover art and the sheet-check rule.
- **[[ui:Campaign.Settings.Packs.Title]]:** untick a pack to keep it out of this campaign's Library.
- **[[ui:Campaign.Settings.Members]]:** give each person a role of [[ui:Campaign.Settings.Role.DM]], [[ui:Campaign.Settings.Role.Player]] or [[ui:Campaign.Settings.Role.Observer]], or remove them. Removed players lose access; their characters stay in the cast.
- **Invite link:** copy it, or make a new one that lasts until replaced, for a week, for a day, or for one player.
- **Backup:** [[ui:Campaign.Settings.Export]] downloads the whole campaign as one file, and [[ui:Campaign.Settings.Import]] restores it, even as a new campaign. Export regularly.

![Campaign settings: details, content packs, members and backup.](shot:settings)

## Language and theme {#language}

TTRPG.Codex speaks English, Russian and Hebrew. Pick the language from the picker in the header or on the sign-in page; it is remembered for your account. The interface, the game system's labels and the SRD rules content all follow it, and Hebrew mirrors the layout right to left. The launcher has its own language setting.

The header also has a theme toggle: dark stone by default, or [[ui:Layout.Theme.ToParchment]].

![The live table in the light parchment theme.](shot:table-light)

You are ready to play. If something does not work, the [install guide](../install/#troubleshooting) has a troubleshooting section, and [issues](https://github.com/myarichuk/TTRPG.Codex/issues) are welcome.
