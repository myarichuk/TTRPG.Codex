# World builder

Status: built. The **World builder** section (`/campaigns/{id}/build`, DM only) holds the DM's conversations with their own
text model, which reads the campaign and the Library through tools and proposes related pages, places, people, secrets and
homebrew in one go, changes to what the campaign has, Bestiary picks and tags. The wiki's *Build with AI* menu and the Library's *Homebrew with AI* button open it with a starter message. The
guided recipe wizards it replaced are gone. Phase 2, the same tools over MCP (§6), is design only.

Related, outside the chat: any wiki page can be **promoted** to the cast (*Add to cast*: a Library stat block, a blank
sheet or an AI-made sheet; the page becomes the character's history) or **put on the map** (a page of kind *Place* gets a
place on save, nested in a region's tree). See `CastService.PromotePageAsync` and `PlaceWikiSync`.

## 1. Principles

1. **The model proposes, the DM applies.** The model reads with tools and proposes with `propose_changes`, which checks
   the proposal and holds it; it never writes. Saving happens only when the DM clicks *Apply* on the review card, through
   `WorldBuilderService.SaveAsync`, the one write path.
2. **Saved material starts hidden.** Wiki pages `DmOnly`, homebrew unpublished, facts `DmOnly`, places and cast members
   `Hidden`. Revealing a cast member whose history came from a page warns the DM first.
3. **Reuse what exists.** Every entry becomes a document the app already has: a page (`WikiPageDocument`, with a kind),
   a place (`LocationDocument` in a region, with a parent and a kind), a cast member (`ActorDocument`, linked to its page),
   a homebrew entry, a fact, a Bestiary shortlist line. Pack entries are never changed; a variant is a homebrew copy.
4. **Stored data stays canonical.** Names and prose are in the DM's language; option values (spell school, damage type)
   are mapped back to the system's values (`SystemDefinition.CanonicalOption`).
5. **Original names only**, as the shipped content.

## 2. The conversations

A DM keeps as many conversations per campaign as they like (`WorldChatDocument`, `worldchats/<guid>`). Each is named after
its first message until the DM renames it, is private to the DM who started it (a co-DM sees their own), is stored after
every turn and every decision on its proposal, is deleted with the campaign, and stays out of the campaign export, like the
journal: they are working notes. `WorldChatSessions` (scoped to the DM's connection) keeps the chats opened so far, so a turn
keeps running while the DM looks at another chat; `/build?chat=<guid>` opens one.

A chat stores the history as the model reads it (`WorldChatMessage`: text, tool calls, tool results; reasoning is not kept,
and the system prompt is made afresh for each message from the campaign as it is then), the log as the page shows it
(`WorldChatLine`), and the waiting proposal with the DM's ticks and decisions.

Each DM message runs the model through `Microsoft.Extensions.AI`'s function invocation, at most `WorldChat.MaxToolRounds` round
trips. Every tool call is recorded as a `WorldChatStep`: the tool, its arguments as JSON, what it returned (cut at 20,000
characters), the error it hit, whether it refused what it was given (a proposal with errors), and how long it took. A reply
keeps its steps, the model's reasoning (for models that share it) and a `WorldChatUsage`: the model, the number of model
calls, the tokens in and out when the server reports them, and the time. While the model works, `WorldChat.Live` holds the
steps so far (the last one maybe still running), the reply as it streams and the reasoning; `WorldChat.Changed` tells the
page to redraw. *Stop* (or Escape) cancels the turn and the conversation is left as it was, message handed back.

The system prompt (`AiPrompts.WorldChat`) asks the model to look before inventing, to put related things in one proposal,
never to claim something was saved, and to write usable pages: a place says what it looks, sounds and smells like, who is
there, what the party can do and what it hides; a speaking person has a role, a look and voice, a want, a fear and two
memories; secrets only where a plot lives in them, each as a `fact` linked to what it concerns. (The checklist is borrowed
from CampaignVault's seeding guidance.) It also asks for two to five tags on every new page and homebrew entry, reusing the
campaign's own.

Models that cannot call tools get a clear message (`WorldChat.Error.NoTools`).

## 3. Tools

| Tool | Returns |
|---|---|
| `search_world(query)` | Pages, places and cast members matching, one line each with the key a proposal uses (`existing:slug`, `existing:place:<id>`, `existing:actor:<id>`) |
| `get_page(page)` | One page in full, a place's description or a cast member's history, mentions kept as `[[actor:id\|Name]]` |
| `list_places()` | Every region and its places as a tree, with keys |
| `search_library(query, type)` | `type/id: Name` lines, packs and homebrew; a homebrew line carries its key (`existing:homebrew:<type>/<id>`) and tags |
| `get_entry(entry)` | One Library entry in full: where it comes from, its tags, every field as JSON, its rules text |
| `homebrew_fields(type)` | The fields a homebrew entry of a type fills |
| `list_tags()` | The tags the campaign's pages and homebrew use, with counts |
| `propose_changes(proposal)` | Checks the proposal: errors per entry for the model to fix, or "ready for review" |
| `add_entries(entries)` | Puts more entries beside the waiting proposal, keeping the DM's ticks |

Read tools return short text, capped; they never write.

## 4. The proposal

```json
{"summary": "...", "region": "The Grey Coast", "entries": [
  {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Saltmere", "tags": ["grey-coast"], "text": "markdown, [[n]], [[existing:slug]]"},
  {"ref": "m", "kind": "place", "placeKind": "district", "parent": "t", "name": "The Gutting Rows", "text": "..."},
  {"ref": "n", "kind": "npc", "name": "Wendel Tarr", "cast": true, "stats": "Bandit", "text": "..."},
  {"ref": "w", "kind": "wiki", "pageKind": "faction", "name": "...", "text": "..."},
  {"ref": "s", "kind": "fact", "name": "The secret in one sentence", "links": ["t"], "text": "who knows, how it is learned"},
  {"ref": "h", "kind": "homebrew", "type": "monster", "bestiary": true, "name": "...", "text": "rules text", "properties": {}},
  {"ref": "v", "kind": "homebrew", "type": "spell", "name": "Rimeburst", "basedOn": "spell/fireball", "properties": {"damageType": "Cold"}},
  {"ref": "e", "kind": "edit", "target": "existing:saltmere", "text": "the COMPLETE new text"},
  {"ref": "b", "kind": "edit", "target": "existing:homebrew:spell/brine-ward", "rename": "...", "properties": {"level": 3}, "tags": ["salt"]},
  {"ref": "g", "kind": "tags", "targets": ["existing:saltmere", "existing:homebrew:spell/brine-ward"], "add": ["shareable"], "remove": ["town"]},
  {"ref": "p", "kind": "bestiary", "entry": "monster/goblin-warrior"}
]}
```

`WorldBundleParser.ParseProposal` (pure, unit-tested) turns it into a `WorldBundle` of `WorldDraft`s: it resolves links to
other entries and existing pages, checks parents (another place entry or an existing place; no loops), coerces homebrew
fields, checks what an edit or a tags entry points at (an edit of a pack entry is refused with a pointer to `basedOn`), and
reports errors as `entries[i] (kind 'name'): ...` or `ref (kind 'name'): ...`. `WorldBuilderService.CheckAsync` adds the
campaign's pages, places, cast and homebrew, finds each person's stats and each Bestiary pick in the Library (exact name, or
the `type/id` that `search_library` lists), starts each variant from its Library entry (its fields with the model's on top,
its text and tags unless the model wrote its own), and records for the review what each change replaces: the current text, a
homebrew entry's fields and tags, each tagged thing's tags, and a stamp to tell at saving whether it changed since.

Saving (`SaveAsync`) writes pages first so titles are settled (`Name (2)` when taken) and links rewritten, then homebrew
(variants keep where they came from as their attribution), then the Bestiary shortlist (new homebrew creatures ticked for it
and the picked Library creatures), then cast members (page as history, AI-picked gear), then places, parents before children,
in the parent's region or the proposal's region (found by name, else the campaign's first, else a new one), then edits (each
refused if what it replaces changed since the review), then tags, then facts related to what they link to. Each entry
records what it became (`SavedAs`), so applying again after a failure adds only the rest.

## 5. The page

`WorldChatSection` is a workbench filling the screen under the header, three framed panes laid out by container queries on
the workbench (the side rail and the notes dock change its width at a given screen width):

- **1280px and wider**: the chats (new, rename, delete; a badge for a waiting proposal, a spinner for a turn under way), the
  conversation, the proposal.
- **760 to 1279px**: the conversation and the proposal; the chats open as a drawer over them.
- **Under 760px**: one pane at a time, picked from tabs (*Chats*, *Conversation*, *Proposal* with a count).

The conversation sets the AI's words like a book's page and the DM's at the end side. Before the first message it offers
numbered starters (a settlement, a faction, a quest, people, homebrew, a variant, revising a page, tagging for sharing). Under
each reply the steps fold into one line (*7 steps · 0.4 s · 1 needed another try*); opened, each step is a line with what it
did, how it came out and how long it took, and opens to its arguments and result, and from there to the raw JSON
(`WorldChatSteps`, the dialog). While the model works the steps show live with the one running, its reasoning, what it is
writing, and a status line with a clock. The message box grows as it is typed in; Enter sends where there is a keyboard
(Shift+Enter for a new line), a touch screen's Enter makes a new line.

The proposal (`WorldProposalPanel`) lists its entries by kind, each with a diamond that keeps it in or leaves it out (leaving
out a place leaves out what is inside it), and opens one at a time in a reader with *Keep* and *Skip*, which move on to the
next entry waiting. An edit shows its changes word by word (`TextDiff`, pure and unit-tested), the new text or the current
one; a homebrew edit its new name, the fields that change (old → new) and its tags; a variant every field it will have; a
tags entry what each target has now. What the proposal's texts mention that nothing is called can be asked for in one click.
After applying, the panel and the conversation link to everything made or changed.

## 6. Code shape

| Piece | Where |
|---|---|
| `WorldBundle`, `WorldDraft`, `DraftKinds`, `WorldBundleParser` | `Codex.Core/WorldBuilder/WorldBundle.cs` |
| `TextDiff` | `Codex.Core/TextDiff.cs` |
| `AiPrompts.WorldChat`, `WorldChatRequest` | `Codex.Core/AI/AiPrompts.cs` |
| `WorldChatDocument` and its parts, `IWorldChatRepository` | `Codex.Persistence/WorldChatModels.cs` |
| `HomebrewTags` (export by tag) | `Codex.Persistence/HomebrewModels.cs` |
| `WorldBuilderService` (tool reads, check, save) | `Codex.Web/Services/WorldBuilderService.cs` |
| `WorldChat`, `WorldChatLive`, `WorldChatSessions`, `ProposalInput` | `Codex.Web/Services/WorldChat.cs` |
| `WorldChatSection`, `WorldChatSteps`, `WorldProposalPanel`, `WorldChatLinks`, `WorldChatView` | `Codex.Web/Components/Campaign` |
| Strings | `Strings.WorldChat{,.ru,.he}.resx`, tags in `Strings.WorldBuilder*` |

Tests: `WorldBuilderTests` (proposal parsing, changes to what exists, links, saving and nesting, variants and Bestiary picks,
the tool loop with a scripted model that feeds a proposal error back and fixes it, the stored history a chat carries on from,
stopping), `TextDiffTests` (the diff rebuilds both texts; tags for export), `PersistenceTest` (a chat's round trip, its owner,
cascade delete).

## 7. Phase 2: the same tools over MCP

The tools above are plain methods on `WorldBuilderService`, and a proposal is plain data (`WorldBundle`), so an MCP server
(`ModelContextProtocol.AspNetCore`) can expose the same tools to an external assistant (Claude Desktop, an IDE).
`propose_changes` there puts the proposal in a chat of the campaign, where the DM applies it in the app as in §2; the MCP side
never writes. Because the app is often reached over the LAN, the endpoint needs a per-user token the DM creates in settings,
scoped to one campaign, DM-only, revocable and off by default.

## 8. Not done yet

- A chat is not shared between co-DMs, and two tabs on one chat save over each other (the last save wins).
- Changing a homebrew entry's type, or deleting anything, is not a proposal kind.
- Export by tag covers homebrew; wiki pages carry tags but are not part of a pack.
