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

    [JsonPropertyName("kind"), Description("place, npc, wiki, fact, homebrew, edit, tags or bestiary.")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("text"), Description("The page's text in markdown (a fact's details; a homebrew entry's rules text), in full sentences under short headings, usually 150 to 350 words: written for a game master to run from, never clipped notes. Link entries as [[ref]] and pages as [[existing:slug]].")]
    public string? Text { get; set; }

    [JsonPropertyName("pitch"), Description("One or two plain sentences, at most 300 characters, saying what the entry is: shown in lists and on the review card, and as a place's short description.")]
    public string? Pitch { get; set; }

    [JsonPropertyName("target"), Description("For an edit: the key of what to change, as search_world, get_page, list_places or search_library show it: existing:<slug> (a page's text), existing:place:<id> (a place's short description), existing:actor:<id> (a cast member's history) or existing:homebrew:<type>/<id> (a homebrew entry). For a page, place or cast member the \"text\" is then the COMPLETE new text, replacing the old.")]
    public string? Target { get; set; }

    [JsonPropertyName("rename"), Description("For an edit of a homebrew entry: its new name, when it should get one.")]
    public string? Rename { get; set; }

    [JsonPropertyName("links"), Description("Refs and existing:slug keys this entry is about (a fact's subjects).")]
    public List<string>? Links { get; set; }

    [JsonPropertyName("placeKind"), Description("For a place: area, settlement, district or site.")]
    public string? PlaceKind { get; set; }

    [JsonPropertyName("parent"), Description("For a place: the ref of the place entry it is inside, or existing:place:<id>.")]
    public string? Parent { get; set; }

    [JsonPropertyName("cast"), Description("For an npc: true to add them to the cast too.")]
    public bool? Cast { get; set; }

    [JsonPropertyName("stats"), Description("For an npc who fights: the Library creature whose statistics suit them, as search_library names it.")]
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

    [JsonPropertyName("tags"), Description("For a new page or homebrew entry: two to five short lower-case tags. For an edit of a homebrew entry: its COMPLETE new tags.")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("targets"), Description("For a tags entry: the keys of the wiki pages (existing:<slug>) and homebrew entries (existing:homebrew:<type>/<id>) to tag.")]
    public List<string>? Targets { get; set; }

    [JsonPropertyName("add"), Description("For a tags entry: the tags to put on every target.")]
    public List<string>? Add { get; set; }

    [JsonPropertyName("remove"), Description("For a tags entry: the tags to take off every target.")]
    public List<string>? Remove { get; set; }

    [JsonPropertyName("properties"), Description("For a homebrew entry: its fields, by the keys homebrew_fields lists. For an edit of one: only the fields that change.")]
    public Dictionary<string, JsonElement>? Properties { get; set; }
}
