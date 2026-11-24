using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.Core.WorldBuilder;

namespace Codex.Web.Services;

/// <summary>What <c>propose_changes</c> takes; the model reads this shape as the tool's schema.</summary>
public sealed class ProposalInput
{
    [JsonPropertyName("summary"), Description("What the proposal adds or changes, in one sentence.")]
    public string? Summary { get; set; }

    [JsonPropertyName("region"), Description("The region new top-level places go into: an existing region's name, or a new one's. Empty: the campaign's first region.")]
    public string? Region { get; set; }

    [JsonPropertyName("regionKind"), Description("When the region is new: what it is, which sets what goes straight inside it. general, realm, wilderness, city, dungeon (its floors are sections), vessel (a ship; its decks are sections) or plane.")]
    public string? RegionKind { get; set; }

    [JsonPropertyName("entries"), Description("Every entry of the proposal: about 8 is best for the game master to review, at most 30.")]
    public List<ProposalEntry>? Entries { get; set; }
}

/// <summary>What <c>add_entries</c> takes: only the entries to put beside those already waiting.</summary>
public sealed class AdditionInput
{
    [JsonPropertyName("entries"), Description("ONLY the new entries (a few at a time), each with a ref the waiting proposal does not use.")]
    public List<ProposalEntry>? Entries { get; set; }
}

/// <summary>One entry of a proposal; see <see cref="WorldBundleParser.ParseProposal"/> for how each field is read.</summary>
public sealed class ProposalEntry
{
    [JsonPropertyName("ref"), Description("A short id other entries use: \"p1\", \"n2\".")]
    public string? Ref { get; set; }

    [JsonPropertyName("kind"), Description("place, npc, wiki, fact, homebrew, edit, tags, bestiary or encounter.")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("text"), Description("The page's text in markdown (a fact's details; a homebrew entry's rules text; an encounter's notes for the game master: what the creatures want and how the fight can end other than by killing them), in full sentences under short headings, usually 150 to 350 words: written for a game master to run from, never clipped notes. Link entries as [[ref]] and pages as [[existing:slug]].")]
    public string? Text { get; set; }

    [JsonPropertyName("pitch"), Description("One or two plain sentences, at most 300 characters, saying what the entry is: shown in lists and on the review card, and as a place's short description.")]
    public string? Pitch { get; set; }

    [JsonPropertyName("target"), Description("For an edit: the key of what to change, as search_world, get_page, list_places or search_library show it: existing:<slug> (a page's text), existing:place:<id> (a place's short description), existing:actor:<id> (a cast member's history) or existing:homebrew:<type>/<id> (a homebrew entry), existing:region:<id> (a region), existing:fact:<id> (a secret in Lore) or existing:sheet:<id> (a character's sheet values; see \"sheet\") or existing:encounter:<id> (a prepared encounter, from list_encounters; it takes \"rename\", \"place\", \"creatures\", \"text\" (its notes), \"tags\" and \"status\", each replacing what it names in full). When the edit changes the text of a page, place or cast member, the \"text\" is the COMPLETE new text, replacing the old; an edit may instead (or also) rename, move or retype a place, or delete.")]
    public string? Target { get; set; }

    [JsonPropertyName("rename"), Description("For an edit: its new name, when it should get one (a place, a cast member, a page's title or a homebrew entry).")]
    public string? Rename { get; set; }

    [JsonPropertyName("regionKind"), Description("For an edit of a region: what it becomes: general, realm, wilderness, city, dungeon, vessel or plane.")]
    public string? RegionKind { get; set; }

    [JsonPropertyName("delete"), Description("For an edit: true to delete what the target names (a place, page, cast member, homebrew entry or prepared encounter; deleting an encounter does not touch its creatures or its place). It stands alone, with no other change in the entry. The places inside a deleted place move up to its parent. The game master reviews and ticks it like any change; nothing is deleted until they apply it.")]
    public bool? Delete { get; set; }

    [JsonPropertyName("links"), Description("Refs and existing:slug keys this entry is about (a fact's subjects).")]
    public List<string>? Links { get; set; }

    [JsonPropertyName("placeKind"), Description("For a place, or an edit of one (to change its kind): area, settlement, district, site (a building, ship or dungeon), section (a floor, deck or wing; one floor of a dungeon), room, or feature (a chest, safe, altar or secret door in a room).")]
    public string? PlaceKind { get; set; }

    [JsonPropertyName("feature"), Description("For a feature: the game master's notes on what the party can open, find or be attacked by. Players never see them.")]
    public ProposalFeature? Feature { get; set; }

    [JsonPropertyName("parent"), Description("For a place: the ref of the place entry it is inside, or existing:place:<id>. For an edit of a place: where it moves to (the same region only); empty or \"none\" moves it out to the top of its region; leave it out to keep it where it is.")]
    public string? Parent { get; set; }

    [JsonPropertyName("cast"), Description("For an npc: true to add them to the cast too.")]
    public bool? Cast { get; set; }

    [JsonPropertyName("stats"), Description("For an npc who fights: the Library creature whose statistics suit them, as search_library names it. For an edit of a cast member: the Library creature (type/id) whose stat block is laid over their sheet.")]
    public string? Stats { get; set; }

    [JsonPropertyName("pageKind"), Description("For a wiki page: lore, faction or quest.")]
    public string? PageKind { get; set; }

    [JsonPropertyName("type"), Description("For a homebrew entry: its type.")]
    public string? Type { get; set; }

    [JsonPropertyName("basedOn"), Description("For a homebrew entry that is a variant of a Library entry: that entry as search_library lists it (type/id). Its fields and text are the starting point; give only the \"properties\" that differ, and the complete \"text\" if it changes.")]
    public string? BasedOn { get; set; }

    [JsonPropertyName("bestiary"), Description("For a homebrew creature: true to put it on the Bestiary shortlist.")]
    public bool? Bestiary { get; set; }

    [JsonPropertyName("entry"), Description("For a bestiary entry: the Library creature to put on the Bestiary shortlist, as search_library lists it (type/id).")]
    public string? Entry { get; set; }

    [JsonPropertyName("tags"), Description("For a new page, homebrew entry or encounter: two to five short lower-case tags. For an edit of a homebrew entry or an encounter: its COMPLETE new tags.")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("targets"), Description("For a tags entry: the keys of the wiki pages (existing:<slug>) and homebrew entries (existing:homebrew:<type>/<id>) to tag.")]
    public List<string>? Targets { get; set; }

    [JsonPropertyName("add"), Description("For a tags entry: the tags to put on every target.")]
    public List<string>? Add { get; set; }

    [JsonPropertyName("remove"), Description("For a tags entry: the tags to take off every target.")]
    public List<string>? Remove { get; set; }

    [JsonPropertyName("resolves"), Description("For an edit that fixes consistency issues: their ids exactly as list_issues gives them, each about the very thing this edit changes. Leave it out for any other entry. An id that is not an open issue, or is about something else, or cannot be fixed by a proposal (fix_via \"report-only\"), refuses the whole proposal. The app checks afterwards whether the issue is really gone.")]
    public List<string>? Resolves { get; set; }

    [JsonPropertyName("sheet"), Description("For an edit of a character's sheet (target existing:sheet:<id>, read it with get_page): the sheet keys to set, as {\"key\": value}; null clears a key. Only the keys get_page lists; a Library reference (class, background, inventory items) must be an id search_library lists. Never invent gear or values: set only what the game master asked for, or what the issue's own rule and the Library's text state. The app checks the values against the system's rules and refuses what would break them.")]
    public Dictionary<string, JsonElement>? Sheet { get; set; }

    [JsonPropertyName("place"), Description("For an encounter, or an edit of one: where it is set: the ref of a place entry in this proposal, or existing:place:<id>. Leave it out for no place. In an edit, \"none\" takes the place away; leave it out (or empty) to keep it.")]
    public string? Place { get; set; }

    [JsonPropertyName("creatures"), Description("For an encounter, or an edit of one (then the COMPLETE new lineup): who is in it. Each line is an \"entry\" (a Library creature as search_library lists it, type/id; an existing homebrew one, existing:homebrew:<type>/<id>; the ref of a homebrew creature entry in this proposal; or a cast member, existing:actor:<id>, who is not a player character), a \"count\" (1 to 20, default 1; a cast member is always 1), a \"note\" and \"hidden\" (true when it starts hidden). A hazard is not a creature: put it on a place's feature.")]
    public List<ProposalCreature>? Creatures { get; set; }

    [JsonPropertyName("status"), Description("For an encounter, or an edit of one: draft (the default for a new one) or ready. Never started or run: only the game master starts an encounter.")]
    public string? Status { get; set; }

    [JsonPropertyName("properties"), Description("For a homebrew entry: its fields, by the keys homebrew_fields lists. For an edit of one: only the fields that change.")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}


/// <summary>One line of an encounter's lineup, read by <see cref="WorldBundleParser.ReadLines"/>.</summary>
public sealed class ProposalCreature
{
    [JsonPropertyName("entry"), Description("A Library creature (type/id), existing:homebrew:<type>/<id>, the ref of a homebrew creature entry in this proposal, or existing:actor:<id> for a cast member.")]
    public string? Entry { get; set; }

    [JsonPropertyName("count"), Description("How many, 1 to 20; a cast member is always 1.")]
    public int? Count { get; set; }

    [JsonPropertyName("note"), Description("A short note for the game master: where they stand, what they do.")]
    public string? Note { get; set; }

    [JsonPropertyName("hidden"), Description("True when a Library or homebrew creature line starts hidden from the players. It does nothing for a cast member, who keeps their own visibility.")]
    public bool? Hidden { get; set; }
}

/// <summary>A feature's notes, read by <see cref="WorldBundleParser.ParseProposal"/>.</summary>
public sealed class ProposalFeature
{
    [JsonPropertyName("container"), Description("True when it holds things: a chest, a safe, a hollow statue.")]
    public bool? Container { get; set; }

    [JsonPropertyName("lock"), Description("How it is locked and opened, in the system's terms (\"DC 15 to pick; the innkeeper has the key\").")]
    public string? Lock { get; set; }

    [JsonPropertyName("trap"), Description("What it does to whoever opens it carelessly, and how to spot and disarm it.")]
    public string? Trap { get; set; }

    [JsonPropertyName("hazard"), Description("Only where the system has a hazard type: a trap or other danger the table can roll. A Library hazard as type/id (as search_library lists it), an existing homebrew one as existing:homebrew:type/id, or the ref of a homebrew hazard entry in this proposal.")]
    public string? Hazard { get; set; }

    [JsonPropertyName("concealment"), Description("How it is hidden (a false wall, an illusion) and how it is found.")]
    public string? Concealment { get; set; }

    [JsonPropertyName("contents"), Description("What is inside: coins, items, papers.")]
    public string? Contents { get; set; }

    [JsonPropertyName("creature"), Description("What it really is or what lives in it, when that is a creature: \"a mimic\".")]
    public string? Creature { get; set; }
}
