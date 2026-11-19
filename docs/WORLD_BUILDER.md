# World builder

Status: built. The **World builder** section (`/campaigns/{id}/build`, GM only) holds the GM's conversations with their own
text model, which reads the campaign and the Library through tools and proposes related pages, places, people, secrets and
homebrew in one go, changes to what the campaign has, Bestiary picks and tags. The wiki's *Build with AI* menu and the Library's *Homebrew with AI* button open it with a starter message. The
guided recipe wizards it replaced are gone. Phase 2, the same tools over MCP (§6), is design only.

Related, outside the chat: any wiki page can be **promoted** to the cast (*Add to cast*: a Library stat block, a blank
sheet or an AI-made sheet; the page becomes the character's history) or **put on the map** (a page of kind *Place* gets a
place on save, nested in a region's tree). See `CastService.PromotePageAsync` and `PlaceWikiSync`.

## 1. Principles

1. **The model proposes, the GM applies.** The model reads with tools and proposes with `propose_changes`, which checks
   the proposal and holds it; it never writes. Saving happens only when the GM clicks *Apply* on the review card, through
   `WorldBuilderService.SaveAsync`, the one write path.
2. **Saved material starts hidden.** Wiki pages `DmOnly`, homebrew unpublished, facts `DmOnly`, places and cast members
   `Hidden`. Revealing a cast member whose history came from a page warns the GM first.
3. **Reuse what exists.** Every entry becomes a document the app already has: a page (`WikiPageDocument`, with a kind),
   a place (`LocationDocument` in a region, with a parent and a kind), a cast member (`ActorDocument`, linked to its page),
   a homebrew entry, a fact, a Bestiary shortlist line. Pack entries are never changed; a variant is a homebrew copy.
4. **Stored data stays canonical.** Names and prose are in the GM's language; option values (spell school, damage type)
   are mapped back to the system's values (`SystemDefinition.CanonicalOption`).
5. **Original names only**, as the shipped content.

## 2. The conversations

A GM keeps as many conversations per campaign as they like (`WorldChatDocument`, `worldchats/<guid>`). Each is named after
its first message until the GM renames it, is private to the GM who started it (a co-GM sees their own), is stored after
every turn and every decision on its proposal, is deleted with the campaign, and stays out of the campaign export, like the
journal: they are working notes. `WorldChatSessions` (scoped to the GM's connection) keeps the chats opened so far, so a turn
keeps running while the GM looks at another chat; `/build?chat=<guid>` opens one.

A chat stores the history as the model reads it (`WorldChatMessage`: text, tool calls, tool results; reasoning is not kept,
and the system prompt is made afresh for each message from the campaign as it is then), the log as the page shows it
(`WorldChatLine`), and the waiting proposal with the GM's ticks and decisions.

Each GM message runs the model through `Microsoft.Extensions.AI`'s function invocation, at most `WorldChat.MaxToolRounds` round
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

On a system with a hazard type (srd5e), a place's `feature` also takes `"hazard"`: a Library hazard, an existing homebrew one or the ref of a homebrew hazard in the same proposal. A homebrew
hazard is checked against the system's `hazards.scaling` and the shape's loader checks before the GM sees the proposal: a number out of range gets a warning (with the system's range) on the hazard's card, and the GM decides; a hazard the table cannot run is refused. A system without a
hazard type keeps the free-text `trap`. See HAZARDS_DESIGN.md, section 11 (H6).

Models that cannot call tools get a clear message (`WorldChat.Error.NoTools`).

### Prompt caching

Every round of the tool loop sends the tools, the system prompt and the whole history again, so the chat (and the character
assistant's tool loop) ask the model client for a prompt cache with `PromptCachingChatClient.WithPromptCaching`. Only the
Anthropic client acts on it: `AIClientFactory` puts a `PromptCachingChatClient` beside it, which marks up to four
breakpoints (the last tool, the system prompt, the last plain user message before the final one, and the final message) on
*copies* of the messages, never on the stored history. Other providers cache a repeated prompt start on their own (OpenAI,
Groq, a local llama.cpp), so there the work is keeping the start of the prompt the same, which is also why:

- `AiPrompts.WorldChat` puts everything that differs by campaign (the game system, the campaign's own words, the outlines,
  the homebrew types) after the instructions, which are the same for every campaign;
- `WorldChat.ForModel` cuts old tool results in steps of `CutStep` of the GM's messages, not one more at each message. A cut
  changes the conversation from its first result on, and a cache reuses only what comes before the change.

The input a client library reports counts what was read from and written to the cache as part of it. `WorldChatUsage` keeps
`CacheReadTokens` and `CacheWriteTokens` beside `InputTokens`, and `AiPrice.Cost` prices them apart: reads at the optional
*Cached input* price on the AI settings page (the input price when it is empty), writes at 1.25 times the input price
(Anthropic's 5-minute rate). A one-shot call (rewrite, summary, the book's art director and editor) asks for no cache: it
would pay the write premium for an entry nothing reads.

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
| `list_issues(scope?, severity?)` | The consistency checker's open issues as JSON, most severe first, at most 50 (section 9) |
| `propose_changes(proposal)` | Checks the proposal: errors per entry for the model to fix, or "ready for review" |
| `add_entries(entries)` | Puts more entries beside the waiting proposal, keeping the GM's ticks |

Read tools return short text, capped; they never write.

## 4. The proposal

```json
{"summary": "...", "region": "The Grey Coast", "regionKind": "realm", "entries": [
  {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Saltmere", "tags": ["grey-coast"], "text": "markdown, [[n]], [[existing:slug]]"},
  {"ref": "m", "kind": "place", "placeKind": "district", "parent": "t", "name": "The Gutting Rows", "text": "..."},
  {"ref": "i", "kind": "place", "placeKind": "site", "parent": "m", "name": "The Salt Lantern", "text": "..."},
  {"ref": "c", "kind": "place", "placeKind": "room", "parent": "i", "name": "Cellar", "text": "..."},
  {"ref": "x", "kind": "place", "placeKind": "feature", "parent": "c", "name": "Strongbox", "text": "...",
   "feature": {"container": true, "lock": "DC 15; Maud has the key", "trap": "...", "concealment": "behind a false wall", "contents": "40 gp, a letter", "creature": "a mimic"}},
  {"ref": "n", "kind": "npc", "name": "Wendel Tarr", "cast": true, "stats": "Bandit", "text": "..."},
  {"ref": "w", "kind": "wiki", "pageKind": "faction", "name": "...", "text": "..."},
  {"ref": "s", "kind": "fact", "name": "The secret in one sentence", "links": ["t"], "text": "who knows, how it is learned"},
  {"ref": "h", "kind": "homebrew", "type": "monster", "bestiary": true, "name": "...", "text": "rules text", "properties": {}},
  {"ref": "v", "kind": "homebrew", "type": "spell", "name": "Rimeburst", "basedOn": "spell/fireball", "properties": {"damageType": "Cold"}},
  {"ref": "e", "kind": "edit", "target": "existing:saltmere", "text": "the COMPLETE new text"},
  {"ref": "b", "kind": "edit", "target": "existing:homebrew:spell/brine-ward", "rename": "...", "properties": {"level": 3}, "tags": ["salt"]},
  {"ref": "m", "kind": "edit", "target": "existing:place:<id>", "parent": "existing:place:<id> | new ref | none", "placeKind": "room", "rename": "..."},
  {"ref": "d", "kind": "edit", "target": "existing:place:<id> | existing:<slug> | existing:actor:<id> | existing:homebrew:<type>/<id>", "delete": true},
  {"ref": "g", "kind": "tags", "targets": ["existing:saltmere", "existing:homebrew:spell/brine-ward"], "add": ["shareable"], "remove": ["town"]},
  {"ref": "p", "kind": "bestiary", "entry": "monster/goblin-warrior"}
]}
```

A place's `placeKind` is one of area, settlement, district, site (a building, ship or dungeon), section (a floor, deck or
dungeon level), room and feature; left out, it follows the parent (or, at the top, the new region's `regionKind`: a dungeon's
or vessel's top places are sections). A feature's `feature` notes are saved on the place for the GM and never sent to
players; what is inside stays text, and the GM hands the items over from Items when the party takes them.

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

The conversation sets the AI's words like a book's page and the GM's at the end side. Before the first message it offers
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
`propose_changes` there puts the proposal in a chat of the campaign, where the GM applies it in the app as in §2; the MCP side
never writes. Because the app is often reached over the LAN, the endpoint needs a per-user token the GM creates in settings,
scoped to one campaign, GM-only, revocable and off by default.

## 8. Not done yet

- A chat is not shared between co-DMs, and two tabs on one chat save over each other (the last save wins).
- Changing a homebrew entry's type, or deleting anything, is not a proposal kind.
- Export by tag covers homebrew; wiki pages carry tags but are not part of a pack.

## 9. The consistency checker

The model never decides whether something is broken. A checker in code (`Codex.Core.Consistency`) reads one snapshot of the campaign
and returns typed issues; the model is handed them and proposes fixes. No check calls a model, a clock or a random number, and
the same data gives the same issues in the same order (`ConsistencyChecker.Run`, tests in `tests/Codex.Tests/Consistency`).

**An issue** is `{ id, check, severity (error|warning|info), entity_kind, entity_id, field, code, args, message, fix_hint, bundle, fix_via }`.
`id` is `check:kind:entityId:field[:detail]`, built from document ids and never from a slug or a title, so it survives a rename;
two issues that would share one are numbered `~2`, `~3`. **Stability:** an id names the problem, not its position or its numbers, so a dismissal or a
`resolves` claim stays on the same problem when something else changes. A secret-block issue's detail is the slug of the block's opening line (cut at 40
characters; `.2`, `.3` only for a later block of the same field that opens with the very same line), never the block's number, so adding a block earlier in
the text moves no other id. A `sheet-rule` issue's detail is the rule's key (the field is already in the id) and, for a rule about one item among several
(`Actor.Rule.NotInLibrary`, `Pick`, `Once`, `Prerequisite`, the armor and weapon training rules and the like; `SheetChecks.ItemArgs`), the slug of that item; never
the numbers or the sentence, which a partial fix changes (five feats picked where two are allowed is the same issue at four). Every id detail made from text a person
typed is a bounded slug. The `~n` suffix is the fallback for two issues a check cannot tell apart (two violations of one rule with no item, two blocks with one opening
line): it is given in the order of what the issues say (`Issue.Order` ends on message and hint), so it does not depend on the order a check produced them in, but
fixing the first of two such issues still gives the bare id to the other; that is the one limit of the scheme. (Dismissals made before this scheme keep their old
ids, which then read as "the problem is gone", and can be restored and made again.) `code` and `args` are for the app to show the sentence in the reader's
language; `message` and `fix_hint` are English, for the model. `bundle` groups a screen or a list by entity. `fix_via` says which
kind of proposal can fix it (`page`, `place`, `fact`, `actor-biography`, `sheet`, `homebrew`), or `report-only`: the review gate cannot change a
page's structural links, who owns a character, the campaign's packs and the like, so those are for the DM. A proposal may resolve an issue only with
the kind of edit its `fix_via` names (a sheet issue by a sheet edit, never by an edit of the history).

**Checks (v1).**

| Check | Finds | Severity |
|---|---|---|
| `broken-link` | `[[Title]]` in a page or a fact that reaches no page; the hint names a page that would match if articles and doubled words were ignored (`[[The the Mere of Dead Men]]`) | warning |
| `unlinked-mention` | a page's text naming another page in plain words (two words or more, a capital or three words), where a link was meant; not when the page already links it, never from a party page to a DM-only one | warning |
| `visibility-leak` | a party page linking a DM-only page (players see the words without the link, or nothing if it has no label); links inside DM blocks do not count | warning |
| `dangling-mention` | `[[actor:id]]` / `[[place:id]]` whose target is gone, in any text; a retired character still exists | error |
| `dangling-reference` | a page's `ActorId`/`PlaceId`, a character's or place's `WikiPageId`, naming a document that is gone | error, report-only |
| `empty-bio`, `empty-body`, `todo-marker` | nothing written; placeholder words. Never filled in by a model | info |
| `empty-inventory` | no gear where a class or background entry carries a starting kit (`startingEquipment` / `equipment`); the free text is noticed, not parsed. Fixable only by a DM-approved sheet edit: the hint says to ask the DM, to propose only items the kit's own text names and the Library has, and never to fill gear in on its own | info, `sheet` |
| `secret-unclosed`, `secret-unknown-audience`, `secret-for-missing` | a `:::` block never closed; an opening word the app does not know (it is treated as DM-only); a `:::for` block naming nobody or a character who is gone | warning |
| `sheet-rule` | what the system's own sheet rules say about a saved character (`SheetRuleEngine.Recompute` on a copy): violations are errors, warnings are warnings. Not for retired characters or bestiary creatures, nothing when the campaign's enforcement is `off`, and the point-buy rules are left out | error / warning, `sheet` where the rule is about a key a sheet edit may set, otherwise report-only |

A system reaches this today through its own `sheetRules` (constraints, lists, multiclass, `SheetCheckDefinition` formulas), which
the `sheet-rule` check surfaces, and through its own `issueChecks` (below).

**Checks (v2).** All are pure functions of the snapshot with ids built from document ids, a repeated-run test and a fixture
(`WorldStructureCheckTests`). The snapshot (`WorldSnapshot`) now also carries each place's parent, hazard and creature, a fact's related ids and knowers,
a page's tags and kind, a character's owner, blueprint and hazard link, the campaign (`CampaignView`: owner, members, system, packs, what the loader
reported, the Bestiary) and the homebrew entries (`HomebrewView`); `ConsistencySnapshots.From` maps the documents into them.

| Check | Finds | Severity, fix |
|---|---|---|
| `place-tree` | a place inside a place that is gone, inside itself, inside a place of another region, or in a loop (reported once, on the loop's lowest id) | error, `place` (an edit that moves the place) |
| `duplicate-slug`, `near-duplicate-title` | pages that share a slug (a link reaches one of them); titles equal once articles and doubled words are ignored (`TextNorm.Canonical`) | warning / info, report-only (merging or renaming is the DM's call) |
| `fact-reference` | a secret's stored related id (`actor:`, `place:`, `item:`) that is gone and that the text no longer mentions (a mention still in the text is `dangling-mention`'s), and a knower that is not on the cast | warning, report-only |
| `dangling-placement` | a place whose feature names a creature that is not on the cast; a hazard on the table standing on a place that is gone; a Library hazard placed on a place that the Library no longer has | error / warning, report-only |
| `owner-not-member` | a character whose player is not a member of the campaign | warning, report-only |
| `homebrew-reference`, `homebrew-duplicate` | a homebrew entry's reference field naming a Library entry that is not there; two entries with one `type/entryId` | warning / error, `homebrew` / report-only |
| `hazard-shape` | a homebrew hazard whose scaled rows, routes, damage or numbers do not hold together: `HazardNumbers.Problems`, the editor's own messages | warning, `homebrew` |
| `tag-case` | one tag written in two cases across pages and homebrew; the less used spelling is reported | info, `homebrew` for an entry, report-only for a page |
| `system-unknown`, `system-drift`, `pack-drift`, `pack-problem` | the campaign's system is not installed; the system changed since the campaign last played under it (`SystemStamp.ChangesTo`); the pack choice names a pack the system lacks; what loading the system's packs reported | error / warning / info / warning, report-only |
| `library-reference` | a creature's source (`BlueprintId`) the Library no longer has; gear on a sheet the Library lacks (only where the sheet rules do not already say so); a Bestiary entry that is gone | info / warning, report-only |
| `system-rule`, `check-failed` | the checks of the campaign's game system (below); one that could not run | as the system says / info |

**Noisy checks.** `orphan-page` (nothing links to a page), `backlink-mismatch` (a page and a character or place point at each other on one side only),
`person-page-no-actor` and `retired-mention` are true but rarely worth a DM's time, so they are in `ConsistencyChecker.Noisy`, never in `Default`.
They run only for `list_issues` scope `noise` (or one of their names) or the Issues panel's "show minor findings" switch
(`IssueFilter.WantsNoise`, `ConsistencyChecker.For`), so they never reach the default list, the issue-count note or a `resolves` claim. They can still be dismissed: `ConsistencyService.DismissAsync` tests "is this id open" against the default and the noisy checks together (the panel lists them
with the switch on), which changes nothing about what the default list, the note or `resolves` see; a made-up id is still refused. (A dismissed minor finding shows as
"the problem is gone" in the dismissed list while the switch is off, since its check does not run then.)

**Cost and the time budget.** The plain-text mention scan walks a trie of the pages' names word by word (`UnlinkedMentionCheck`), so a position in a text costs
as many steps as the name it could be, not the length of the longest title (500 pages of 1000 words with a 40-word title: 10.7 s before, 0.24 s after; 1000 pages of
2000 words with a 60-word title: 80 s before, 0.56 s after, snapshot included). Which mentions are reported is unchanged; a test keeps the first, simple
version as an oracle on generated worlds. `ConsistencyChecker.Run` takes a `CancellationToken` and looks at it between checks (a check is never interrupted, so
every check stays pure); the per-message count note (`WorldBuilderService.IssueSummaryAsync`) runs under a budget (`IssueSummaryBudget`, 5 s) and is left out, silently,
when the check goes over it, so a pathological campaign cannot stall a chat message.

**Loading cost.** The count note runs a snapshot per DM message, so the v2 checks add no heavy reading: members, packs, the system's changes and
what the loader reported are in memory, and the only new read is the campaign's homebrew list, one query beside the several the Library's resolver
already makes for the sheet's types (`ConsistencyService`). It is always read, never skipped by scope, so an issue is the same issue whichever scope
asked for it. Entries a check names (a hazard, a creature) are found one by one (`LookupResolver`: the pack's entry or the homebrew one), never by
loading a whole type.

**The sheet edit target.** `existing:sheet:<actorId>` (`EditTarget.Sheet`), with `"sheet": {"key": value, "other": null}` on an edit entry (null clears
a key). Only the keys the system declares can be set (`SheetProposal.EditableKeys`: attributes, actor fields, the inventory, the system's lists; never
a derived value, a collection row or an image); the model reads them, with how each is written and its value now, through `get_page` on that key.
A value is checked for shape (type, bounds, options, at most 30 keys, 100 list entries), then in `WorldBuilderService.SheetsAsync` against the Library (an id it does not have is refused) and, for a list value, the choices written after a pick's `|` (`id|name:number,...`; `SheetProposal.SuffixProblems`, whatever the enforcement): an
inventory pick takes the inventory's quantity name (1 to 999, a limit of the proposal; the rules have none), its equipped name (`:1`) and `rune.<id>` of a rune the
Library has; a pick of the list that grants score increases takes the system's attributes, each at least 1, together no more than the entry grants; any other list takes
no choices; anything else (a malformed part, an undefined name, `qty:9999`) is refused in words. (The engine ignores a choice it has no use for, and reports an
overspent increase only with enforcement on, so without this a typo or an invented amount was saved and looked as if it did something.) An id the Library does not have is refused, so nothing is invented; then the system's own rules: the changed sheet is run through
`SheetRuleEngine.Recompute` beside the old one and the proposal is refused when it introduces a rule violation the sheet did not have
(a violation that was there stays the DM's; `enforcement: off` skips the rules, never the Library; the point-buy rules are left out). The rules run with the sheets the character's links point to
(`ILinkedSheets`), as on the sheet page: loaded with the campaign's own access for the propose-time check and the apply, read from the snapshot's actors
(`SnapshotLinkedSheets`) for `sheet-rule`; without them a value that depends on a link (a track's maximum) is left stale instead of recomputed.
Applying checks it all again, so a character who has since been retired or turned into a creature, and a key the system no longer lists as editable (it
was reloaded between the review and the apply), are refused (`SheetProposal.Check` fails on such a key, set or cleared, instead of skipping it).
A retired character,
a bestiary creature and a character that is gone are refused; a player's character is allowed, since the DM reviews every change and the sheet checks
cover them. The review card shows each key now and after; applying checks again that the sheet still holds what the card showed (`BeforeStamp`, otherwise
`EditChanged`), runs the check again, sets the keys, lets the rules recompute what they derive (what the sheet page does after every edit), syncs the state and saves it
the way the sheet page does (`CastService.SaveSheetAsync` -> `CampaignRuntimeManager.TrySaveActorAsync`): a character at a live table is saved through the table
(`ReplaceActorCommand`, merged with the table's current state against the sheet as it was before the edit, so the edit shows at the table at once and the table's
next write cannot put the old value back), anyone else with an ordinary guarded save. (A creature's stat block laid over a character with `stats` on an
actor edit is still saved straight to the store; that is not a sheet edit.) `resolves` works as for every edit and also requires the issue's `fix_via` to match the kind of edit, so a sheet issue cannot be claimed by
an edit of the history or the other way round, and the post-apply re-check sees the result like any other.

**A system's own checks (layers 2 and 3).** `issueChecks` in `system.yaml` (model in `Codex.Plugin.Abstractions/IssueChecks.cs`, loader checks in
`IssueCheckProblems`, run by `SystemRuleCheck`; the schema and reference in `docs/` are generated). Layer 2 is data, no expression language:

```yaml
issueChecks:
  - id: wizard-needs-spells          # lower-case id, unique; part of every issue id
    rule: required                   # required | empty | range | reference | count
    field: spells                    # an attribute, actor field, the inventory or a list key
    when: { field: class, is: wizard }   # optional; exactly one of is, in, notEmpty, empty
    severity: warning                # error | warning (default) | info
    message: "{name} is a wizard with no {field}."   # {name} {field} {value} {min} {max}
```

Unknown keys under `issueChecks`, an unknown rule or severity or blank, a field that is not a sheet key, a `range` over a non-number and the like refuse
the system at load with a sentence naming the check. A rule is about player and non-player characters unless `roles` says otherwise, never a retired
one, and nothing is reported where the campaign's enforcement is `off`; it is fixable by a sheet edit where its field can be set and the character is not a bestiary creature or a hazard (a rule with `roles: [monster]` is report-only: no sheet edit reaches them). Layer 3 is compiled: an
entry with `type: "Namespace.Type, Assembly"` names a class in a DLL beside `system.yaml` that implements `ICodexIssueCheck` (plain data in and out, like
the other compiled suppliers; it references only `Codex.Plugin.Abstractions`), resolved by `CompiledSuppliers` like a function. It is given plain copies of
the cast, pages and places and returns `CodexIssue`s; the host builds ids (`system-rule:<kind>:<entity>:<field>:<check id>[.<detail>]`), sorts and shows
them. One that is missing, throws, or returns what cannot be read becomes one `info` issue, `check-failed`, and the other checks and the run go on. Nothing
limits what compiled code does or how long it takes (as for every compiled supplier: it runs with the server's rights and is installed by copying it onto
the server), so it must be deterministic and quick. A sample is `LongNameCheck` in `tests/Codex.ContractFixture`, tested in `SystemIssueCheckTests`.

**Dismissals.** A red link that is meant to stay red is dismissed by the DM, and only the DM (`ConsistencyService.DismissAsync`; an id
that is not an open issue is refused). The document `ConsistencyDismissals/{campaignId}` holds the ids and the DM's optional note; results are never stored.
The campaign export carries the document (`CampaignBundle.Dismissals`, null in an older bundle, which restores as it always did and leaves stored dismissals
alone); importing a bundle replaces the stored document with the file's, tidied like a dismissal made in the app (empty or over-long ids dropped, notes cut).
Ids of problems that are gone are kept, harmlessly: they apply again if the same problem comes back. Imported as a copy, the ids follow the documents' new ids,
because an issue id is built from them.

**Fixes** are proposals through the review gate that name the issue ids they resolve.

*Wired now (phase 2).*
- `list_issues(scope?, severity?)` is a read-only tool of the chat (DM only, like every tool). It lists the open issues (never the dismissed ones) in
  `Issue.Order` as snake_case JSON, one issue to a line, fenced like the other data tools, at most 50 (`IssueListing.MaxShown`); a longer list ends with
  a line `(N more not shown, of M; narrow with scope or severity to see them)`. `scope` is `all`, a check name, `noise` (the noisy checks only), or `page:<id>` / `actor:<id>` / `place:<id>` /
  `fact:<id>` / `homebrew:<type/id>` / `campaign:<id>`; `severity` is the least severe level (`warning` gives errors and warnings). An unknown scope or severity is answered in words ("Nothing
  was listed: ...", `IssueFilter.IsKnownScope`), never with an empty list that would read as all clear. The tool sits at a fixed place in the tool list
  (after `list_tags`) so the tools prompt cache is built once. The system prompt says, in a sentence that never changes, that code decides what is an issue
  and how severe, that an `info` issue is never filled in with invented content, and that a report-only issue cannot be fixed by a proposal.
- `resolves` is an optional list of issue ids on a proposal entry (`ProposalEntry.Resolves`, `WorldDraft.Resolves`). `WorldBundleParser.ParseProposal(json,
  rules, openIssues)` checks it, and any failure is an error that refuses the whole proposal: an id that is not an open issue
  (`ConsistencyChecker.UnknownIds`; the error names the ids and says to call `list_issues`), an id the gate cannot fix (`report-only`), an entry that is not
  an edit, or an issue about something other than what the edit changes (kind and document id; a page is matched by its document id, through
  `ProposalRules.ExistingPageIds`). A proposal with no `resolves` reads as it always did, and the checker only runs for a proposal that has the word.
  A revision of an edit (same ref, same target) keeps what the earlier one resolved (`WorldChat.CarryOver`); `add_entries` re-checks the waiting entries'
  claims together with the new ones.

*Wired now (phase 3).*
- **The re-check after an apply.** `WorldChat.ApplyNowAsync` reads the ids the kept entries resolve before anything is cleared, takes the open issues just
  before `WorldBuilderService.SaveAsync` and again just after (`WorldBuilderService.OpenIssuesAsync`, DM only: one full uncached snapshot each), and sets
  them side by side with `ConsistencyChecker.Compare`. The claim is only what is checked: the model's word that a fix worked decides nothing. It runs only
  when some kept entry has `resolves` (no claim, no two snapshots, and the note reads as it always did). Unticked entries' claims are not checked.
  The result is `WorldBuildResult.Recheck` (`ApplyRecheck`: `Failed`, `Resolved`, `StillOpen`, `NotFound`, `ResolvedUnclaimed`, `New` as issue ids, in the
  checker's order), null when it did not run. The model is told in the "(A note from the app ...)" message: "The app re-ran its checks after the apply;
  what its checks find, not what you claimed, decides what is fixed. resolved: ...; still open (the change did not fix them): ...; ..." (only the parts
  that have something in them). If the re-check itself throws, the apply still succeeded and the note says the checks could not run and that nothing
  may be said to be fixed. Nothing is retried or fixed automatically; a `StillOpen` or `New` issue is the game master's to ask about.
- **The issue summary.** At the start of each message of a game master with open issues, the chat adds one line to the history after the message, as a
  note of its own: "(A note from the app, not from the game master: N open issues (E errors, W warnings, I info); call list_issues for details.)". Counts
  only, in the checker's own words, once per message (never on a later round of the tool loop), never in the system prompt, and absent when nothing is open or
  the check fails. It is stored with the history, so every later request repeats it byte for byte and the prefix before it stays cached. It costs one
  snapshot per message.
- **The prompt cache.** `PromptCacheRegressionTests` captures the requests below `PromptCachingChatClient` for two messages of two agent steps each and
  asserts that tools and system prompt are identical, in the same tool order, with no date, time, GUID or request id, that the breakpoints are on the last
  tool, the system prompt and the last message of every step, that each request repeats the one before it, and that the issue summary changes only the
  user turn.

*Wired now (the rest).*
- **The Issues panel** (`IssuesSection`, the campaign's "Issues" section, DM only: not in the rail or the menu for anyone else, and a player who opens its
  address sees only that it is for the game master, with nothing read). Open issues by severity, filters by severity and by kind, the minor-findings
  switch, dismissal with an optional note (`ConsistencyService.DismissAsync`; an id that is not open is refused in words), the dismissed list with restore
  (including dismissals whose problem is gone) and, from `IssueRecheckLog`, what the last apply's re-check found (`ApplyRecheckNote`, also shown under the
  world builder's proposal after an apply). Every call of `ConsistencyService` refuses anyone but the DM.
- **Localisation.** The sentences are made at read time from `code` + `args` (`IssueText.Message`) with the `Consistency.*` keys in `Strings.Consistency*.resx`
  (every code is in `ConsistencyCodes.Known`; a test sees that each has text in en, he and ru, that the English text equals the message the model reads, and
  that no source literal is missing). The sheet rules use their own `Actor.Rule.*` keys; a system's own check falls back to its English message. The model
  still gets English.
- **The secret opener.** `SecretBlock.OpenerWord` is the word a block was opened with, as written; the parsing and its fail-closed reading are unchanged.
  `secret-unknown-audience` reads it instead of re-reading the opening line.

*The prompt cache.* One-time invalidation of the cached prefix when this shipped: the `propose_changes` / `add_entries` schema gained the `sheet` field and
its `target` text names `existing:sheet:<id>`, `get_page`'s description and parameter text mention the sheet, and the system prompt's `list_issues` sentence
says what a `sheet` issue is. The tool order is unchanged (`list_issues` stays after `list_tags`, nothing was added), and `PromptCacheRegressionTests` still holds.

*Still to do.*
- Point-buy and similar derived budgets are out of scope for the sheet checks (the bridge leaves `Actor.Rule.PointBuy*` out, and a sheet edit never
  judges them): the budget is a derived total that no single key fixes, and the rules engine already shows it on the sheet.
- Deferred, with reasons: a proposal cannot claim a `tags` entry or a homebrew `delete` in `resolves` (only `edit` entries carry `resolves`), so `tag-case` on pages is
  report-only; the compiled-check sandbox is the supplier contract's (no time or memory limit), which a bounded worker would change for every supplier at once;
  a system's own `issueChecks` sentence is English-only in the app unless the app has text for its `code`.
