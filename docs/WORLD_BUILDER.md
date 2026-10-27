# World builder

Status: built. The **World builder** section (`/campaigns/{id}/build`, DM only) is a conversation with the DM's own text
model, which reads the campaign through tools and proposes related pages, places, people, secrets and homebrew in one
go. The wiki's *Build with AI* menu and the Library's *Homebrew with AI* button open it with a starter message. The
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
   a homebrew entry, a fact.
4. **Stored data stays canonical.** Names and prose are in the DM's language; option values (spell school, damage type)
   are mapped back to the system's values (`SystemDefinition.CanonicalOption`).
5. **Original names only**, as the shipped content.

## 2. The conversation

`WorldChat` (one per campaign per DM connection, kept by the scoped `WorldChatSessions`) holds the message history and
the pending proposal. Each DM message runs the model through `Microsoft.Extensions.AI`'s function invocation, at most
`WorldChat.MaxToolRounds` round trips. The page shows what the model looks up while it works, and *Stop* cancels the turn
(the conversation is left as it was).

The system prompt (`AiPrompts.WorldChat`) asks the model to look before inventing, to put related things in one proposal,
never to claim something was saved, and to write usable pages: a place says what it looks, sounds and smells like, who is
there, what the party can do and what it hides; a speaking person has a role, a look and voice, a want, a fear and two
memories; secrets only where a plot lives in them, each as a `fact` linked to what it concerns. (The checklist is borrowed
from CampaignVault's seeding guidance.)

Models that cannot call tools get a clear message (`WorldChat.Error.NoTools`).

## 3. Tools

| Tool | Returns |
|---|---|
| `search_world(query)` | Pages, places and cast members matching, one line each with the key a proposal uses (`existing:slug`, `existing:place:<id>`) |
| `get_page(page)` | One page in full, mentions as names |
| `list_places()` | Every region and its places as a tree, with keys |
| `search_library(query, type)` | `type/id: Name` lines; creatures when no type |
| `homebrew_fields(type)` | The fields a homebrew entry of a type fills |
| `propose_changes(proposal)` | Checks the proposal: errors per entry for the model to fix, or "ready for review" |

Read tools return short text, capped; they never write.

## 4. The proposal

```json
{"summary": "...", "region": "The Grey Coast", "entries": [
  {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Saltmere", "text": "markdown, [[n]], [[existing:slug]]"},
  {"ref": "m", "kind": "place", "placeKind": "district", "parent": "t", "name": "The Gutting Rows", "text": "..."},
  {"ref": "n", "kind": "npc", "name": "Wendel Tarr", "cast": true, "stats": "Bandit", "text": "..."},
  {"ref": "w", "kind": "wiki", "pageKind": "faction", "name": "...", "text": "..."},
  {"ref": "s", "kind": "fact", "name": "The secret in one sentence", "links": ["t"], "text": "who knows, how it is learned"},
  {"ref": "h", "kind": "homebrew", "type": "monster", "bestiary": true, "name": "...", "text": "rules text", "properties": {}}
]}
```

`WorldBundleParser.ParseProposal` (pure, unit-tested) turns it into a `WorldBundle` of `WorldDraft`s: it resolves links to
other entries and existing pages, checks parents (another place entry or an existing place; no loops), coerces homebrew
fields, and reports errors as `entries[i] (kind 'name'): ...` or `ref (kind 'name'): ...`. `WorldBuilderService.CheckAsync`
adds the campaign's pages, places and homebrew types, and finds each person's stats in the Library (exact name, or the
`type/id` that `search_library` lists).

Saving (`SaveAsync`) writes pages first so titles are settled (`Name (2)` when taken) and links rewritten, then homebrew
(and the Bestiary shortlist), then cast members (page as history, AI-picked gear), then places, parents before children,
in the parent's region or the proposal's region (found by name, else the campaign's first, else a new one), then facts
related to what they link to. Each entry records what it became (`SavedAs`), so applying again after a failure adds only
the rest.

## 5. Code shape

| Piece | Where |
|---|---|
| `WorldBundle`, `WorldDraft`, `DraftKinds`, `WorldBundleParser` | `Codex.Core/WorldBuilder/WorldBundle.cs` |
| `AiPrompts.WorldChat`, `WorldChatRequest` | `Codex.Core/AI/AiPrompts.cs` |
| `WorldBuilderService` (tool reads, check, save) | `Codex.Web/Services/WorldBuilderService.cs` |
| `WorldChat`, `WorldChatSessions`, `ProposalInput` | `Codex.Web/Services/WorldChat.cs` |
| `WorldChatSection.razor` | `Codex.Web/Components/Campaign` |
| Strings | `Strings.WorldChat{,.ru,.he}.resx`, tags in `Strings.WorldBuilder*` |

Tests: `WorldBuilderTests` (proposal parsing, links, saving and nesting, the tool loop with a scripted model that feeds a
proposal error back and fixes it).

## 6. Phase 2: the same tools over MCP

The tools above are plain methods on `WorldBuilderService`, and a proposal is plain data (`WorldBundle`), so an MCP server
(`ModelContextProtocol.AspNetCore`) can expose the same six tools to an external assistant (Claude Desktop, an IDE).
`propose_changes` there puts the proposal in the campaign's review queue, where the DM applies it in the app as in §2;
the MCP side never writes. Because the app is often reached over the LAN, the endpoint needs a per-user token the DM
creates in settings, scoped to one campaign, DM-only, revocable and off by default.

## 7. Not done yet

- Proposals change only new things; editing an existing page from the chat (say, "append a rumour to Saltmere") is not a
  proposal kind yet.
- The conversation lives as long as the DM's connection; it is not stored.
