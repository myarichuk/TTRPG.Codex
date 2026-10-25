# World builder

Status: the **homebrew recipe** (Library → *Build with AI*) and the **faction**, **settlement** and **quest recipes**
(Wiki → *Build with AI*) are built, shown to the DM once a text model is set up. People (`npc`), places (`place`) and
secrets (`fact`) are draft kinds; a person can also join the cast, a place can also go on the World page, and a homebrew
creature can also go on the Bestiary shortlist. The world
recipe, Library blueprints for cast members and MCP are still design only.

A set of guided wizards that help the DM create campaign material with the AI: a homebrew item, spell or creature,
a faction, a city, a world, a quest or story arc. The wizard asks a short series of questions, the AI proposes a
*bundle* of linked drafts (a city plus its ruler, two factions and a rumour), the DM reviews them, and the app saves
the chosen ones into the wiki, the Library (homebrew) and the bestiary.

It is offered only when the DM has set up a text model (BYOK, `AiFeatures.IsConfiguredAsync`); without one the
entry points are hidden, as the other AI buttons are. It runs on the DM's own key (`AiProviderService.AssistantForAsync`).

## 1. Principles

1. **The app drives, the model drafts.** The app owns the questions, the steps and every write. The model only
   returns JSON drafts. It does not call tools that save anything. This works with every provider the settings page
   offers, including small local models (Ollama, LM Studio) whose tool calling is unreliable.
2. **Nothing is saved until the DM says so.** Generated material lands in a review step; the DM ticks what to keep,
   edits it in place, and saves. Saved material starts hidden: wiki pages `DmOnly`, homebrew `IsPublished = false`,
   facts `DmOnly`, places `Hidden`.
3. **Reuse what exists.** Every draft becomes a document the app already has (table in §3). A new document type is
   added only where nothing fits (quests, §3.2).
4. **Stored data stays canonical.** Names and prose are written in the DM's language (`AiPrompts.StyleFor`), but
   option values (spell school, damage type, size, any dropdown) are stored as the system's canonical values, as
   everywhere else (see [LOCALIZATION.md](LOCALIZATION.md)). The app validates and fixes them before showing a draft.
5. **Original names only.** The prompt asks for original names, and forbids product-identity names of published
   settings and creatures, the same rule the shipped content follows.

## 2. How a wizard runs

```
Choose a recipe ─▶ Questions (2–6 steps) ─▶ Outline ─▶ Review & expand ─▶ Save
                     fixed, from the recipe   one AI call   per draft, on demand   app writes, DM access
```

1. **Choose a recipe.** From a "Build with AI" menu on the Wiki, Library (homebrew), Bestiary and World sections, or a
   campaign-wide "World builder" entry on the overview. Opening it from a page pre-fills the context: "Faction" opened
   from the wiki page *Port Varr* links the faction to Port Varr.
2. **Questions.** Each recipe is a short list of questions: choices (tone, size, power level), short text ("what does
   the faction want?") and "surprise me" on every question. The questions are data (§4), so they are translated and
   always the same; the model does not invent the form. An optional last step, "Anything else?", is free text.
3. **Outline (pass 1).** One call returns the bundle: each draft with a kind, a name, a one-line pitch and its links
   to the other drafts and to existing pages. The prompt includes the campaign's description, the names of existing
   wiki pages, homebrew entries, places and NPCs (capped, like `ExpandWikiAsync` caps related titles), so the model
   links to them instead of inventing duplicates.
4. **Review & expand (pass 2).** The DM sees the outline as cards with a checkbox each (all ticked), can drop, rename or
   "regenerate" one, and asks for the full text of each draft separately: a wiki page body, or a homebrew entry's fields.
   Expanding one draft at a time keeps each call small enough for local models with short context windows, and lets
   the DM stop after the outline. The full text is shown in the same preview the entry will have when saved.
5. **Save.** The app writes the ticked drafts through the repositories with the DM's `CampaignAccess`, turns draft ids
   into real `[[links]]` and ids (§5), and opens the first saved page. Undo is per item (delete), as today.

The layout reuses `HomebrewWizard`'s dialog: steps down the side, the current step in the middle, the preview on the
side (stacked on phones, as the phone layout already does for sheets).

## 3. Recipes and where their drafts go

| Recipe | Questions (examples) | Drafts it can propose | Saved as |
|---|---|---|---|
| **Item / spell / creature / any homebrew type** | type, role ("a cursed heirloom"), power level, theme | the entry; optionally a wiki page for its history | `HomebrewEntryDocument` (fields from the type's `ContentTypeDefinition.EditorFields()`); a creature (a type with `actorBlueprint`) ticked "also add to the Bestiary shortlist" also becomes a `BestiaryEntry` (built) |
| **Faction** (built) | what kind of group, what it wants, open or secret, reach, tone, its enemy, how many people, a secret or not | the faction page, its leader and 0–2 members, a secret | the faction as a wiki page, people as wiki pages and, when ticked, cast members (§3.1), the secret as a `FactDocument`; pages are tagged with the recipe ("faction") and people also "person", in the DM's language |
| **Settlement** (built) | size, where it is, what it is known for, who rules, trouble brewing, tone, a sketch or in full, rumours or not | the settlement page, 2 or 4 places, 3 or 5 people, 1 or 2 factions, 1 or 2 rumours | wiki pages (tag "settlement"; places also "place", people also "person"); a place ticked "also add to the World page" becomes a hidden `LocationDocument` in a region named after the settlement (an existing region of that name is reused); rumours as `FactDocument`s |
| **World / region** | genre and tone, magic level, gods or not, the big conflict, scale | the overview page, 3–6 regions, powers (factions), a short history | wiki pages (tags `world`, `region`, `history`), linked from the overview |
| **Quest / story arc** (built) | hook, stakes, villain, length (one session, a short or long arc), the party's level, tone, what to tie it into, a creature or reward or not | the quest page, 2–4 people (the villain first), 1–3 places, 1–2 clues, a homebrew creature and a reward item | the quest as a wiki page with a fixed outline (tag "quest", §3.2); people, places and clues as in the other recipes; the creature and item as unpublished homebrew |

Every recipe works for any system: homebrew fields come from the system's own content types, and the setting
recipes only write wiki pages, places and facts.

### 3.1 NPCs

Today an NPC is an `ActorDocument` (a sheet on the table) and lore lives in the wiki. The world builder writes NPCs as
**wiki pages** by default (name, role, look, wants, secret), because most NPCs never need a sheet. On the review card
the DM can tick "also add to the cast" to create a hidden `ActorDocument` (`CastService.AddNpcAsync`): the sheet starts on
the system's defaults, the page's text (links flattened) goes into `Biography`, and `DmNotes` points back to the page. Not
built yet: a Library creature as blueprint when the model suggests one that exists.

### 3.2 Quests

There is no quest document. The quest recipe writes a quest as a wiki page with a fixed outline (Hook, Stakes, Acts,
Who's involved, Rewards, If the party fails; the recipe's `write.wiki` instruction, §4), so it is useful at once and can be edited like any page. A real quest
document (status, steps the DM ticks off, which the session log and the AI recap could refer to) is a separate feature and
is not part of this design.

## 4. Recipes as data

Recipes are YAML files embedded in `Codex.Core` (`src/Codex.Core/WorldBuilder/Recipes/*.yaml`, loaded and checked by
`WorldRecipes`), one per recipe, so a new recipe needs no C# code. A question is `text`, `longText`, `choice`, `toggle` or
`contentType` (the system's types). An empty answer goes to the model as "surprise me" when the question allows it, and as
"no preference" otherwise. A sketch of a later recipe:

```yaml
# src/Codex.Core/WorldBuilder/Recipes/faction.yaml
key: faction
icon: flag
drafts: [faction, npc, fact]       # kinds this recipe may return (validated)
questions:
  - key: goal
    type: text                      # text | longText | choice | toggle | contentType
    surprise: true                  # offers "Surprise me"
  - key: methods
    type: choice
    options: [open, secret, mixed]
  - key: reach
    type: choice
    options: [local, regional, world]
prompt: |
  Design one faction for the campaign and the people and secret that bring it to the table. ...
```

A recipe may also give `write:` instructions per draft kind, added to the call that writes a draft of that kind in
full; the quest recipe uses it for the quest page's fixed sections and to size homebrew to the party.

Each question also has an English `ask:`, which is what the model reads. Labels, help text and option labels come from
`Strings.WorldBuilder*.resx` as `WorldBuilder.Recipe.<recipe>.<question>[.<option>|.Hint]`, plus `.Title` and `.Intro`.
`WorldBuilderTests.EveryRecipeLabel_IsTranslated` fails on a missing one. A system may ship its own
recipes in its folder later (for example a "deity" recipe for 5e); not needed for the first version.

## 5. The bundle the model returns

```json
{
  "drafts": [
    { "ref": "d1", "kind": "faction", "name": "The Salt Wardens", "pitch": "Smugglers who police the docks they rob.",
      "links": ["d2", "existing:port-varr"] },
    { "ref": "d2", "kind": "npc", "name": "Mother Ilse", "pitch": "Their leader, a retired harbour pilot." },
    { "ref": "d3", "kind": "fact", "name": "The wardens pay the harbour master", "links": ["d1"] },
    { "ref": "d4", "kind": "homebrew", "type": "magic-item", "name": "Tide-glass", "pitch": "..." }
  ]
}
```

- Kinds are `homebrew`, `wiki`, `npc` (a person: a wiki page, optionally a cast member), `place` (a wiki page, optionally a
  place on the World page) and `fact` (a secret: its name is
  the fact itself, its text two to four plain sentences). A recipe's own subject, such as the faction, is a `wiki` draft.
  Links in text resolve to `wiki`, `npc` and `place` drafts; a secret has no page, so a link to it reads as plain text.
- `ref` is a draft id that lives only until the save; `existing:<slug or id>` points at something already in the campaign.
  Unknown `existing:` targets are dropped, unknown kinds or homebrew types are dropped with a note on the review step.
- On save the app writes pages first (so `UniqueSlugAsync` settles the slugs), then rewrites `[[d2]]` in bodies to
  `[[Mother Ilse]]` with the final title, then writes homebrew (and the ticked creatures' shortlist entries), cast members, and facts (`RelatedEntityIds` holds the ids
  of the pages, cast members, places and existing pages the secret links to), then places.
- The expand call for a homebrew draft asks for its fields by key, with each field's type and allowed options
  (from `FieldDefinition`), and the reply goes through the same kind of coercion as `AiAssistant.ParseDraft`: numbers
  parsed, options matched to their canonical value (also through the system translation, so a Hebrew school name still
  maps to `Evocation`; `SystemDefinition.CanonicalOption`), anything else dropped. After saving, the Library opens the
  first saved entry in `HomebrewWizard` on its Review step, so the DM checks the rules and publishes it.
- JSON mode with the plain retry, as `CreateCharacterAsync` does; a reply that does not parse gets one "return only the
  JSON" retry, then an error the DM can act on ("The model did not return a usable draft; try again or pick another model").

## 6. Code shape

| Piece | Where | What |
|---|---|---|
| `WorldRecipe`, `WorldRecipes` | `Codex.Core/WorldBuilder` | Loads and validates the embedded `Recipes/*.yaml` |
| `WorldBundle`, `WorldDraft` | `Codex.Core/WorldBuilder` | The bundle shape and its parser/validator (pure, unit-tested without a model) |
| `AiPrompts.WorldOutline`, `AiPrompts.WorldExpand` | `Codex.Core/AI/AiPrompts.cs` | Prompts, written like the existing ones, with `StyleFor` |
| `AiAssistant.OutlineWorldAsync`, `ExpandHomebrewAsync`, `ExpandPageAsync` | `Codex.Core/AI/AiAssistant.cs` | JSON calls with the existing fallback and one retry; `FieldCoercion` is shared with character creation |
| `WorldBuilderService` | `Codex.Web/Services` | Gathers campaign context, calls the assistant, commits a reviewed bundle through the repositories (DM access) |
| `WorldBuilderWizard.razor` | `Codex.Web/Components/Shared` | The dialog (questions, outline cards, expand, save) |
| Strings | `Strings.WorldBuilder{,.ru,.he}.resx` | All labels, question texts and errors |

Tests: recipe loading, bundle parsing (bad JSON, unknown kinds, dangling refs), option coercion including translated
values, link rewriting on save, DM-only enforcement and the one-retry rule (a scripted `IChatClient`), in `WorldBuilderTests`.
There is no Playwright test yet; the faction, settlement and quest flows were checked by hand against a scripted model.

## 7. Phase 2: the same operations over MCP

Once the wizard works, the same operations can be offered as an MCP server (the `ModelContextProtocol.AspNetCore`
package), so a DM using an external assistant (Claude Desktop, an IDE) can build the world from there. Tools: `list_recipes`,
`campaign_context` (names of pages, places, homebrew), `search_library`, `propose_bundle` (stores a bundle as pending), and
**no direct writes**: a proposed bundle shows up in the app's review step, where the DM saves it as in §2.

That limit matters because the app is often reached over the LAN: the endpoint needs a per-user token the DM creates
in settings, scoped to one campaign, DM-only, revocable, and off by default. MCP is not needed for the in-app wizard, which
calls the model directly.

## 8. Decisions to make

1. **Quests: wiki page now, quest document later?** Recommended: yes, a templated wiki page now (§3.2).
2. **Does a city create places on the World map as well as wiki pages?** Recommended: an opt-in tick on the review
   step ("also add landmarks to the map"), off by default, since a region's map is laid out by hand.
3. **NPCs as wiki pages by default, cast actors on request?** Recommended: yes (§3.1).
4. **MCP in the first version?** Recommended: no; phase 2 after the wizard and the bundle format have settled.
5. **Recipes per system?** Recommended: generic recipes only for now; the YAML format leaves room for system recipes.
6. **First recipes to build:** homebrew entry (it reuses `HomebrewWizard` and has the clearest value), then faction and
   city, then world and quest.
