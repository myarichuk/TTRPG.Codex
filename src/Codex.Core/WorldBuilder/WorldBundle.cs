using System.Text.Json;
using Codex.Core.AI;
using Codex.Core.Consistency;

namespace Codex.Core.WorldBuilder;

/// <summary>The kinds of entry a world-building proposal may hold, and what each becomes when saved.</summary>
public static class DraftKinds
{
    /// <summary>A Library entry of one of the system's content types, saved as an unpublished homebrew entry.</summary>
    public const string Homebrew = "homebrew";

    /// <summary>A world wiki page (lore, a faction, a quest), saved DM-only.</summary>
    public const string Wiki = "wiki";

    /// <summary>A person, saved as a DM-only wiki page and, when <see cref="WorldDraft.Cast"/> is set, also as a hidden cast member
    /// whose history is that page.</summary>
    public const string Npc = "npc";

    /// <summary>A secret or rumour the party can uncover, saved DM-only in Lore; its name is the fact itself.</summary>
    public const string Fact = "fact";

    /// <summary>A place, saved as a DM-only wiki page and a hidden place on the Places list, inside its parent.</summary>
    public const string Place = "place";

    /// <summary>A change to something the campaign already has: the complete new text of a wiki page, a place's description or a
    /// cast member's history, replacing the old (<see cref="WorldDraft.Target"/> says which). Nothing is made; the DM reviews it
    /// against the current text like any other entry.</summary>
    public const string Edit = "edit";

    /// <summary>Tags to add to or take off things the campaign already has (wiki pages, homebrew entries), so they can be found and
    /// exported together. <see cref="WorldDraft.Targets"/> says which; nothing else about them changes.</summary>
    public const string Tags = "tags";

    /// <summary>A Library creature, from a pack or the campaign's homebrew, to put on the campaign's Bestiary shortlist.</summary>
    public const string Bestiary = "bestiary";

    public static readonly IReadOnlyList<string> All = new[] { Homebrew, Wiki, Npc, Fact, Place, Edit, Tags, Bestiary };

    /// <summary>Kinds that change something the campaign has instead of adding a new thing.</summary>
    public static bool ChangesExisting(string kind) => kind is Edit or Tags or Bestiary;

    /// <summary>Kinds saved as wiki pages, which other entries' text can link to.</summary>
    public static bool IsPage(string kind) => kind is Wiki or Npc or Place;
}

/// <summary>What an edit entry changes the text of.</summary>
public enum EditTarget
{
    /// <summary>A wiki page's body (also the page of a place or a person).</summary>
    Page,
    /// <summary>A place's description, which players read once the place is revealed.</summary>
    Place,
    /// <summary>A cast member's history, which players read once the character is revealed.</summary>
    Actor,
    /// <summary>A homebrew Library entry: its name, rules text, fields and tags (<c>existing:homebrew:&lt;type&gt;/&lt;entryId&gt;</c>).</summary>
    Homebrew,
    /// <summary>A region: its name, what kind it is, or deleting it with every place in it (<c>existing:region:&lt;id&gt;</c>).</summary>
    Region,
    /// <summary>A secret or rumour in Lore: its name (the fact itself) and details (<c>existing:fact:&lt;id&gt;</c>).</summary>
    Fact,
    /// <summary>A character's sheet: the sheet keys the system declares, set or cleared (<c>existing:sheet:&lt;actorId&gt;</c>; <see cref="SheetProposal"/>).</summary>
    Sheet
}

/// <summary>A proposed feature's notes for the DM (see <c>PlaceFeature</c>): free text in the campaign's own terms, empty for none.</summary>
public sealed class DraftFeature
{
    public bool Container { get; set; }
    public string? Lock { get; set; }
    public string? Trap { get; set; }
    public string? Concealment { get; set; }
    public string? Contents { get; set; }
    public string? Creature { get; set; }

    /// <summary>The hazard as the model wrote it (a Library <c>type/id</c>, <c>existing:homebrew:type/id</c>, or the ref of a homebrew hazard in the proposal).
    /// The parser turns it into <see cref="HazardEntry"/> or <see cref="HazardRef"/> and clears it when it names nothing.</summary>
    public string? Hazard { get; set; }

    /// <summary>The Library entry of the hazard, <c>type/id</c>: a pack's, or the campaign's own homebrew one (<see cref="HazardIsHomebrew"/>).</summary>
    public string? HazardEntry { get; set; }

    /// <summary>True when <see cref="HazardEntry"/> is the campaign's own homebrew entry.</summary>
    public bool HazardIsHomebrew { get; set; }

    /// <summary>The ref of a homebrew hazard in the same proposal; it becomes the placed hazard when that entry is saved.</summary>
    public string? HazardRef { get; set; }

    /// <summary>The hazard's name as the review reads it, for an entry the Library has; filled by the check.</summary>
    public string? HazardName { get; set; }
}

/// <summary>An item a proposed person would carry: the Library entry, how many, and whether it is worn or held.</summary>
public sealed class DraftGear
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public bool Equipped { get; set; }
}

/// <summary>
/// One thing a proposal holds: a homebrew entry, a wiki page, a person, a place or a secret. Nothing is stored until the DM
/// applies the proposal; <see cref="SavedAs"/> then records what each became.
/// </summary>
public sealed class WorldDraft
{
    /// <summary>The entry's id inside its proposal ("p1"), which other entries link to; gone once saved.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>One of <see cref="DraftKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>For a homebrew entry: the system's content type key ("spell").</summary>
    public string? Type { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The name the model gave, which other entries' text links to even after the DM renames this one.</summary>
    public string OutlineName { get; set; } = string.Empty;

    /// <summary>A line about the entry, for the review card.</summary>
    public string Pitch { get; set; } = string.Empty;

    /// <summary>Other entries' <see cref="Ref"/>s and <c>existing:&lt;slug&gt;</c> pointers at pages the campaign already has.</summary>
    public List<string> Links { get; set; } = new();

    /// <summary>Ticked on the review card; only selected entries are saved.</summary>
    public bool Selected { get; set; } = true;

    /// <summary>For a person: also add them to the cast, hidden from players, with their page as their history.</summary>
    public bool Cast { get; set; }

    /// <summary>For a place: also put it on the Places list (always, for a proposal; kept for the save step).</summary>
    public bool OnMap { get; set; }

    /// <summary>For a homebrew creature: also put it on the campaign's Bestiary shortlist, besides the Library.</summary>
    public bool Bestiary { get; set; }

    /// <summary>For a person: the creature the model said plays like them ("Veteran"), as it wrote it, until the app finds it in the Library.</summary>
    public string? SuggestedStats { get; set; }

    /// <summary>For a person added to the cast: the Library creature whose stat block their sheet starts from, instead of a blank sheet.</summary>
    public CreatureRef? Stats { get; set; }

    /// <summary>For a person: what they carry if they join the cast, picked by the model when the proposal was checked so the DM sees
    /// it before applying. Null until picked; empty when there is nothing to pick from or the pick failed.</summary>
    public List<DraftGear>? Gear { get; set; }

    /// <summary>For a place: the place it is inside, another entry's <see cref="Ref"/> or <c>existing:place:&lt;id&gt;</c>; empty
    /// for a place straight in the region.</summary>
    public string? Parent { get; set; }

    /// <summary>For a place: "area", "settlement", "district", "site", "section", "room" or "feature".</summary>
    public string? PlaceKind { get; set; }

    /// <summary>For a place that is a chest, a safe or a mimic: its lock, trap, hiding place, contents and creature, for the DM.</summary>
    public DraftFeature? Feature { get; set; }

    /// <summary>For a wiki page: "lore", "faction" or "quest".</summary>
    public string? PageKind { get; set; }

    /// <summary>The title its wiki page is saved under: the name, or "Name (2)" when a page of that name exists. Links go by
    /// title, so they use it; a cast member or a place keeps the name itself.</summary>
    public string? PageTitle { get; set; }

    /// <summary>What saving already made of this entry, by step ("page", "homebrew", "cast", "place", "fact") to document id, so a
    /// save that failed half-way can be retried without writing anything twice.</summary>
    public Dictionary<string, string> SavedAs { get; set; } = new();

    /// <summary>A page's text, a homebrew entry's description (its rules text), a secret's details, or, for an edit, the complete new text.</summary>
    public string? Body { get; set; }

    /// <summary>For an edit: what it changes, as <c>existing:&lt;slug&gt;</c>, <c>existing:place:&lt;id&gt;</c> or <c>existing:actor:&lt;id&gt;</c>.</summary>
    public string? Target { get; set; }

    /// <summary>For an edit: the text as it is now, for the review card to show beside the new one. Filled in when the proposal is checked.</summary>
    public string? Before { get; set; }

    /// <summary>For an edit: players can already read the text, so applying it changes what they see at once.</summary>
    public bool SharedWithPlayers { get; set; }

    /// <summary>For an edit: a fingerprint of the stored text the review card showed, so applying can tell when it changed since.</summary>
    public string? BeforeStamp { get; set; }

    public Dictionary<string, object> Properties { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    /// <summary>For a homebrew entry: the Library entry (<c>type/id</c>) it is a variant of, whose fields and text it starts from.</summary>
    public string? BasedOn { get; set; }

    /// <summary>For a variant: the name of the entry it starts from, as the DM reads it, once the check found it.</summary>
    public string? BasedOnName { get; set; }

    /// <summary>For a variant: where the entry it starts from comes from (a pack's name), kept as the homebrew's attribution.</summary>
    public string? ImportedFrom { get; set; }

    /// <summary>For an edit: its new name, when it gets one (a place, a cast member, a page's title, a homebrew entry).</summary>
    public string? Rename { get; set; }

    /// <summary>For an edit: the thing is deleted, and nothing else about the entry applies. A place's own places move up to its parent.</summary>
    public bool Remove { get; set; }

    /// <summary>For an edit of a region: what kind of region it becomes ("dungeon", "vessel"...).</summary>
    public string? RegionKind { get; set; }

    /// <summary>For an edit of a place: it moves to <see cref="Parent"/> (null: out to the top of its region). Without it the place stays where it is.</summary>
    public bool MoveTo { get; set; }

    /// <summary>For an edit of a homebrew entry: <see cref="Tags"/> are its complete new tags. Otherwise its tags stay.</summary>
    public bool Retag { get; set; }

    /// <summary>For an edit of a homebrew entry: its fields and tags as they are now, beside <see cref="Properties"/> and
    /// <see cref="Tags"/> (the changed fields, and the complete new tags when they change), for the review card.</summary>
    public Dictionary<string, object>? BeforeProperties { get; set; }
    public List<string>? BeforeTags { get; set; }

    /// <summary>For an edit of a sheet: the new values of the keys it sets, each as JSON text (a number, a string, true or false, an array of
    /// strings), by key. Empty for an edit that changes no sheet. Kept as text so it survives the database exactly.</summary>
    public Dictionary<string, string> SheetSet { get; set; } = new();

    /// <summary>For an edit of a sheet: the keys it clears (removes from the sheet).</summary>
    public List<string> SheetClear { get; set; } = new();

    /// <summary>For an edit of a sheet: the current values of the keys it changes, as JSON text (<c>null</c> for a key the sheet lacks), for the
    /// review card and to see at apply time that the sheet is still as reviewed. Filled in when the proposal is checked.</summary>
    public Dictionary<string, string>? BeforeSheet { get; set; }

    /// <summary>What the check found worth the game master's eye on this entry (a homebrew hazard's numbers far from its level), shown on its card. Warnings, not refusals.</summary>
    public List<string> Concerns { get; set; } = new();

    /// <summary>For a tags entry: the keys of the pages and homebrew entries it tags.</summary>
    public List<string> Targets { get; set; } = new();

    /// <summary>For a tags entry: the tags to add and to take off.</summary>
    public List<string> TagsAdded { get; set; } = new();
    public List<string> TagsRemoved { get; set; } = new();

    /// <summary>For a tags entry: what each target is called and the tags it has now, filled in by the check, for the review card.</summary>
    public List<TaggedThing> Tagged { get; set; } = new();

    /// <summary>The ids of the consistency issues (<c>list_issues</c>) this entry says it fixes, each one an open issue about the thing
    /// an edit changes. A claim only: the app re-checks after applying and never takes the model's word that something is fixed. Empty
    /// for an entry that fixes nothing in particular, which is every entry that does not cite one.</summary>
    public List<string> Resolves { get; set; } = new();

    /// <summary>True when the entry wrote its own <c>resolves</c> (an empty list too). A revision of an edit that does replaces what the earlier version
    /// claimed (so a claim can be withdrawn); one that leaves it out keeps the earlier claims (<c>WorldChat.CarryOver</c>).</summary>
    public bool ResolvesGiven { get; set; }
}

/// <summary>Something a tags entry changes: its key, its name and its tags before the change.</summary>
public sealed class TaggedThing
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Before { get; set; } = new();
}

/// <summary>A Library creature, pack or homebrew: <see cref="Name"/> as the pack names it (what the model reads), <see cref="Shown"/>
/// as the DM reads it.</summary>
public sealed record CreatureRef(string Type, string Id, bool Homebrew, string Name, string Shown);

/// <summary>What the model proposed in one go: related entries that link to each other, saved together when the DM applies them.</summary>
public sealed class WorldBundle
{
    public const string ExistingPrefix = "existing:";

    /// <summary>The prefix of an existing place's id, after <see cref="ExistingPrefix"/>: <c>existing:place:&lt;id&gt;</c>.</summary>
    public const string PlacePrefix = "place:";

    /// <summary>The prefix of an existing cast member's id, after <see cref="ExistingPrefix"/>: <c>existing:actor:&lt;id&gt;</c>.</summary>
    public const string ActorPrefix = "actor:";

    /// <summary>The prefix of an existing homebrew entry, after <see cref="ExistingPrefix"/>: <c>existing:homebrew:&lt;type&gt;/&lt;entryId&gt;</c>.</summary>
    public const string HomebrewPrefix = "homebrew:";

    /// <summary>The prefix of an existing region, after <see cref="ExistingPrefix"/>: <c>existing:region:&lt;id&gt;</c>.</summary>
    public const string RegionPrefix = "region:";

    /// <summary>The prefix of an existing secret in Lore, after <see cref="ExistingPrefix"/>: <c>existing:fact:&lt;id&gt;</c>.</summary>
    public const string FactPrefix = "fact:";

    /// <summary>The prefix of an existing cast member's sheet, after <see cref="ExistingPrefix"/>: <c>existing:sheet:&lt;actorId&gt;</c>.</summary>
    public const string SheetPrefix = "sheet:";

    /// <summary>What an <c>existing:</c> key points at: a place (<c>existing:place:id</c>), a cast member (<c>existing:actor:id</c>)
    /// or, otherwise, a wiki page by slug. <paramref name="id"/> is the key without its prefixes.</summary>
    public static EditTarget ParseTarget(string key, out string id)
    {
        var rest = key.Trim();
        if (rest.StartsWith(ExistingPrefix, StringComparison.OrdinalIgnoreCase)) rest = rest[ExistingPrefix.Length..];
        if (rest.StartsWith(PlacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = rest[PlacePrefix.Length..];
            return EditTarget.Place;
        }

        if (rest.StartsWith(ActorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = rest[ActorPrefix.Length..];
            return EditTarget.Actor;
        }

        if (rest.StartsWith(HomebrewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = rest[HomebrewPrefix.Length..];
            return EditTarget.Homebrew;
        }

        if (rest.StartsWith(RegionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = rest[RegionPrefix.Length..];
            return EditTarget.Region;
        }

        if (rest.StartsWith(FactPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = rest[FactPrefix.Length..];
            return EditTarget.Fact;
        }

        if (rest.StartsWith(SheetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = rest[SheetPrefix.Length..];
            return EditTarget.Sheet;
        }

        id = rest;
        return EditTarget.Page;
    }

    /// <summary>What the proposal adds, in a sentence, for the review card.</summary>
    public string? Summary { get; set; }

    /// <summary>The region new top-level places go into (found by name, made when there is none), or empty for the campaign's first.</summary>
    public string? Region { get; set; }


    /// <summary>What a new region made for the proposal is: "dungeon", "vessel" and so on, or null to leave it general.</summary>
    public string? RegionKind { get; set; }

    public List<WorldDraft> Drafts { get; set; } = new();

    public WorldDraft? Find(string reference) => Drafts.FirstOrDefault(d => d.Ref == reference);

    /// <summary>What unticking <paramref name="draft"/> would untick besides it, without changing anything: the ticked places inside
    /// it, and inside those, and the secrets whose subjects would all be unticked. (A secret that also points at a page the campaign
    /// has stays: it is still about something.)</summary>
    public IReadOnlyList<WorldDraft> WouldSkip(WorldDraft draft)
    {
        var gone = new HashSet<WorldDraft> { draft };
        var also = new List<WorldDraft>();
        var pending = new Queue<WorldDraft>();
        pending.Enqueue(draft);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var child in Drafts.Where(d => d.Selected && (d.Kind == DraftKinds.Place || d is { Kind: DraftKinds.Edit, MoveTo: true }) && !gone.Contains(d)
                         && string.Equals(d.Parent, parent.Ref, StringComparison.OrdinalIgnoreCase)))
            {
                gone.Add(child);
                also.Add(child);
                pending.Enqueue(child);
            }
        }

        foreach (var fact in Drafts.Where(d => d.Selected && d.Kind == DraftKinds.Fact && d.Links.Count > 0))
        {
            var about = fact.Links.Select(l => l.StartsWith(ExistingPrefix, StringComparison.OrdinalIgnoreCase) ? null : Find(l)).ToList();
            if (about.All(d => d != null && (!d.Selected || gone.Contains(d)))) also.Add(fact);
        }

        return also;
    }

    /// <summary>Unticks <paramref name="draft"/> and <see cref="WouldSkip"/>'s others, so no place is left without the place it is
    /// inside. Returns the others, so the review can say so.</summary>
    public IReadOnlyList<WorldDraft> Skip(WorldDraft draft)
    {
        var also = WouldSkip(draft);
        draft.Selected = false;
        foreach (var d in also) d.Selected = false;
        return also;
    }

    /// <summary>Ticks <paramref name="draft"/>, and the places it is inside (an entry kept while the place it sits in is skipped would
    /// be saved straight in the region). Returns the places it ticked besides.</summary>
    public IReadOnlyList<WorldDraft> Select(WorldDraft draft)
    {
        draft.Selected = true;
        var also = new List<WorldDraft>();
        var at = draft;
        while ((at.Kind == DraftKinds.Place || at is { Kind: DraftKinds.Edit, MoveTo: true }) && at.Parent is { Length: > 0 } parent && Find(parent) is { } up && !also.Contains(up) && up != draft)
        {
            if (!up.Selected)
            {
                up.Selected = true;
                also.Add(up);
            }

            at = up;
        }

        return also;
    }
}

/// <summary>A <c>[[name]]</c> some entries' text links to that nothing in the proposal or the campaign is called: a place or a
/// person the model mentioned but did not write. <see cref="By"/> names the entries that mention it and <see cref="ByRefs"/> says which they are.</summary>
public sealed record MissingMention(string Name, IReadOnlyList<string> By, IReadOnlyList<string> ByRefs);

/// <summary>What a proposal may refer to. <see cref="HomebrewTypes"/> maps every spelling the model might use for a type (its key,
/// folder, English and translated labels) to the type key; <see cref="ExistingPages"/> maps the campaign's page slugs to their
/// titles; <see cref="HomebrewFields"/> gives a type's fields; <see cref="ExistingHomebrew"/> maps the campaign's homebrew entries,
/// as <c>type/entryId</c>, to their names. <see cref="ExistingPageIds"/> maps the campaign's page slugs to their document ids, which is how
/// an edit of a page is matched to the consistency issues about it (an issue names a page by document id, never by slug). <see cref="System"/> and
/// <see cref="ExistingSheets"/> (the ids of the characters whose sheet a proposal may change: not retired, not a creature) make a sheet edit possible.</summary>
public sealed record ProposalRules(
    IReadOnlyDictionary<string, string> HomebrewTypes,
    IReadOnlyDictionary<string, string> ExistingPages,
    IReadOnlyCollection<string> ExistingPlaces,
    Func<string, IReadOnlyList<FieldSpec>> HomebrewFields,
    Func<string, string, string>? Canonical = null,
    IReadOnlyCollection<string>? ExistingActors = null,
    IReadOnlyDictionary<string, string>? ExistingHomebrew = null,
    IReadOnlyDictionary<string, string>? ExistingRegions = null,
    IReadOnlyDictionary<string, string>? ExistingFacts = null,
    string? HazardType = null,
    IReadOnlyDictionary<string, string>? ExistingPageIds = null,
    Codex.Plugin.Abstractions.SystemDefinition? System = null,
    IReadOnlyCollection<string>? ExistingSheets = null);

/// <summary>A checked proposal: the entries as they would be saved, and what is wrong with it (nothing is saved while there are
/// <see cref="Errors"/>) or worth a second look (<see cref="Warnings"/>), each naming the entry it is about.</summary>
public sealed record ProposalCheck(WorldBundle Bundle, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Reads what the model proposes. Pure, so every rule is unit-tested without a model.</summary>
public static class WorldBundleParser
{
    /// <summary>The most entries one proposal holds: a runaway reply must not become hundreds of pages.</summary>
    public const int MaxDrafts = 30;

    // Longest name, pitch and text kept; the rest is cut. A name becomes a page title and its slug.
    private const int MaxName = 200;
    private const int MaxPitch = 300;
    private const int MaxBody = 20_000;
    private const int MaxStats = 100;

    private static readonly string[] PlaceKinds = { "area", "settlement", "district", "site", "section", "room", "feature" };
    private static readonly string[] RegionKinds = { "general", "realm", "wilderness", "city", "dungeon", "vessel", "plane" };
    private const int MaxFeatureNote = 300;
    private const int MaxFeatureContents = 4000;
    private static readonly string[] PageKinds = { "lore", "faction", "quest" };

    /// <summary>
    /// Checks a proposal, <c>{"summary", "region", "entries": [{"ref", "kind", "name", "text", ...}]}</c>, and turns it into the
    /// entries the DM reviews. Kind-specific fields: a person's <c>cast</c> and <c>stats</c>; a place's <c>placeKind</c> and
    /// <c>parent</c> (another entry's ref or <c>existing:place:&lt;id&gt;</c>); a page's <c>pageKind</c>; a homebrew entry's
    /// <c>type</c>, <c>tags</c> and <c>properties</c>. <c>links</c> (and <c>[[ref]]</c> in the text) point at other entries
    /// or <c>existing:&lt;slug&gt;</c> pages. An entry that can't be saved is an error naming it; one the parser had to guess
    /// about is a warning. An entry may list the consistency issues it fixes in <c>resolves</c> (ids from <c>list_issues</c>);
    /// <paramref name="openIssues"/> are the issues open right now (null: none), and an id that is not one of them, one that no
    /// proposal can fix, or one about something other than what the entry changes is an error, so the whole proposal is refused.
    /// A proposal with no <c>resolves</c> reads exactly as it did before.
    /// </summary>
    public static ProposalCheck ParseProposal(string json, ProposalRules rules, IReadOnlyList<Issue>? openIssues = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var bundle = new WorldBundle();
        JsonDocument doc;
        try
        {
            doc = FieldCoercion.ParseObject(json);
        }
        catch (FormatException)
        {
            return new ProposalCheck(bundle, new[] { "The proposal is not a JSON object." }, warnings);
        }

        using (doc)
        {
            var root = doc.RootElement;
            bundle.Summary = Cut(FieldCoercion.String(root, "summary"), MaxPitch) is { Length: > 0 } summary ? summary : null;
            bundle.Region = Cut(FieldCoercion.String(root, "region"), MaxName) is { Length: > 0 } region ? region : null;
            bundle.RegionKind = OneOf(FieldCoercion.String(root, "regionKind"), RegionKinds);
            if (bundle.RegionKind == null && FieldCoercion.String(root, "regionKind") is { Length: > 0 } badRegion)
            {
                warnings.Add($"regionKind '{badRegion}' is not one of {string.Join(", ", RegionKinds)}; it was ignored.");
            }
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            {
                errors.Add("The proposal has no \"entries\".");
                return new ProposalCheck(bundle, errors, warnings);
            }

            var types = new Dictionary<string, string>(rules.HomebrewTypes, StringComparer.OrdinalIgnoreCase);
            var pages = new Dictionary<string, string>(rules.ExistingPages, StringComparer.OrdinalIgnoreCase);
            var rawLinks = new Dictionary<WorldDraft, List<string>>();
            var index = 0;
            foreach (var element in entries.EnumerateArray())
            {
                var at = index++;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"entries[{at}]: not an object.");
                    continue;
                }

                var name = Cut(FieldCoercion.String(element, "name"), MaxName);
                var kind = FieldCoercion.String(element, "kind")?.Trim().ToLowerInvariant() ?? string.Empty;
                var label = $"entries[{at}] ({(kind.Length == 0 ? "?" : kind)} '{name}')";
                if (bundle.Drafts.Count >= MaxDrafts)
                {
                    errors.Add($"{label}: a proposal holds at most {MaxDrafts} entries; propose the rest next.");
                    continue;
                }

                if (!DraftKinds.All.Contains(kind))
                {
                    errors.Add($"{label}: kind must be one of {string.Join(", ", DraftKinds.All)}.");
                    continue;
                }

                // An edit, a retagging and a Bestiary pick are named by what they change, which the check looks up; the model need not repeat it.
                if (name.Length == 0 && !DraftKinds.ChangesExisting(kind))
                {
                    errors.Add($"{label}: has no name.");
                    continue;
                }

                var reference = FieldCoercion.String(element, "ref")?.Trim();
                if (string.IsNullOrEmpty(reference) || bundle.Find(reference) != null || reference.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var given = reference;
                    reference = $"e{at + 1}";
                    while (bundle.Find(reference) != null) reference += "x";
                    if (!string.IsNullOrEmpty(given)) warnings.Add($"{label}: ref '{given}' was taken, so it is '{reference}' now.");
                }

                var text = Cut(FieldCoercion.String(element, "text") ?? FieldCoercion.String(element, "description"), MaxBody);
                // "[[existing:slug]]" points at a page the campaign has; pages link by title, so it becomes "[[Title]]".
                text = WikiLink.Replace(text, m =>
                {
                    var target = m.Groups[1].Value.Trim();
                    if (!target.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase)) return m.Value;
                    if (!pages.TryGetValue(target[WorldBundle.ExistingPrefix.Length..], out var title))
                    {
                        warnings.Add($"{label}: links to '{target}', which is not a page the campaign has; it is plain text now.");
                        return m.Groups[2].Success ? m.Groups[2].Value : target[WorldBundle.ExistingPrefix.Length..];
                    }

                    return m.Groups[2].Success ? $"[[{title}|{m.Groups[2].Value}]]" : $"[[{title}]]";
                });
                var draft = new WorldDraft
                {
                    Ref = reference,
                    Kind = kind,
                    Name = name,
                    OutlineName = name,
                    Body = text.Length == 0 ? null : text,
                    Pitch = Cut(FieldCoercion.String(element, "pitch") ?? TextFormatting.Summary(text, MaxPitch, keepLinks: true), MaxPitch)
                };

                switch (kind)
                {
                    case DraftKinds.Npc:
                        draft.Cast = element.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.True;
                        draft.SuggestedStats = Cut(FieldCoercion.String(element, "stats"), MaxStats) is { Length: > 0 } stats ? stats : null;
                        break;
                    case DraftKinds.Place:
                        draft.OnMap = true;
                        draft.Parent = FieldCoercion.String(element, "parent")?.Trim() is { Length: > 0 } parent ? parent : null;
                        draft.PlaceKind = OneOf(FieldCoercion.String(element, "placeKind"), PlaceKinds, "location" , "site");
                        if (draft.PlaceKind == null && FieldCoercion.String(element, "placeKind") is { Length: > 0 } badPlace)
                        {
                            warnings.Add($"{label}: placeKind '{badPlace}' is not one of {string.Join(", ", PlaceKinds)}; it was left to the parent.");
                        }

                        draft.Feature = ReadFeature(element);

                        break;
                    case DraftKinds.Wiki:
                        draft.PageKind = OneOf(FieldCoercion.String(element, "pageKind"), PageKinds) ?? "lore";
                        break;
                    case DraftKinds.Edit:
                        if (!ReadEdit(draft, element, label, pages, rules, bundle, errors, warnings, (FieldCoercion.String(element, "text") ?? FieldCoercion.String(element, "description"))?.Length ?? 0)) continue;
                        break;
                    case DraftKinds.Tags:
                        if (!ReadTags(draft, element, label, pages, rules, errors)) continue;
                        break;
                    case DraftKinds.Bestiary:
                        draft.SuggestedStats = Cut(FieldCoercion.String(element, "entry") ?? FieldCoercion.String(element, "stats") ?? FieldCoercion.String(element, "target"), MaxStats) is { Length: > 0 } pick ? pick : null;
                        if (draft.SuggestedStats == null && name.Length > 0) draft.SuggestedStats = name;
                        if (draft.SuggestedStats == null)
                        {
                            errors.Add($"{label}: a bestiary entry needs \"entry\": the Library creature to shortlist, as search_library lists it (type/id).");
                            continue;
                        }

                        if (draft.SuggestedStats.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            draft.SuggestedStats = draft.SuggestedStats[(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix).Length..];
                        }

                        if (draft.Name.Length == 0) draft.Name = draft.OutlineName = draft.SuggestedStats;
                        break;
                    case DraftKinds.Homebrew:
                        if (FieldCoercion.String(element, "type") is not { } typeText || !types.TryGetValue(typeText, out var type))
                        {
                            errors.Add($"{label}: type must be one of the homebrew types ({string.Join(", ", types.Values.Distinct())}).");
                            continue;
                        }

                        draft.Type = type;
                        draft.Bestiary = element.TryGetProperty("bestiary", out var bestiary) && bestiary.ValueKind == JsonValueKind.True;
                        draft.BasedOn = Cut(FieldCoercion.String(element, "basedOn"), MaxStats) is { Length: > 0 } basedOn ? basedOn : null;
                        ApplyHomebrew(draft, element, rules.HomebrewFields(type), rules.Canonical, label, warnings);
                        break;
                }

                // A page's own tags, besides the one saying what kind of page it is.
                if (DraftKinds.IsPage(kind)) draft.Tags = ReadTagList(element, "tags");

                if (kind is not (DraftKinds.Place or DraftKinds.Edit) && FieldCoercion.String(element, "parent") is { Length: > 0 })
                {
                    warnings.Add($"{label}: only a place has a parent; it was ignored.");
                }

                if (draft.Body == null && kind is not DraftKinds.Fact && !DraftKinds.ChangesExisting(kind) && draft.BasedOn == null)
                {
                    warnings.Add($"{label}: has no text, so its page will only hold its name.");
                }

                var links = element.TryGetProperty("links", out var linkList) && linkList.ValueKind == JsonValueKind.Array
                    ? linkList.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!.Trim()).ToList()
                    : new List<string>();
                links.AddRange(WikiLink.Matches(text).Select(m => m.Groups[1].Value.Trim()));
                rawLinks[draft] = links;
                draft.Resolves = ReadIds(element, "resolves", label, errors, out var resolvesGiven);
                draft.ResolvesGiven = resolvesGiven;
                bundle.Drafts.Add(draft);
            }

            ResolveHazards(bundle, rules, warnings);
            CheckResolves(bundle, rules, openIssues ?? Array.Empty<Issue>(), errors);

            // Two pages may share a title ("Sister Maren", "Sister Maren"); a link by title reaches the first, as the wiki's does.
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slug, title) in pages) titles.TryAdd(WikiText.Slugify(title), slug);
            var places = rules.ExistingPlaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (draft, links) in rawLinks)
            {
                var label = $"{draft.Ref} ({draft.Kind} '{draft.Name}')";
                foreach (var link in links.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string? resolved = null;
                    if (link.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var key = link[WorldBundle.ExistingPrefix.Length..];
                        if (pages.ContainsKey(key)) resolved = WorldBundle.ExistingPrefix + key;
                    }
                    else if (bundle.Drafts.FirstOrDefault(d => !DraftKinds.ChangesExisting(d.Kind) && (string.Equals(d.Ref, link, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, link, StringComparison.OrdinalIgnoreCase))) is { } target)
                    {
                        if (target != draft) resolved = target.Ref;
                    }
                    else if (titles.TryGetValue(WikiText.Slugify(link), out var slug))
                    {
                        // A "[[Title]]" of a page the campaign has.
                        resolved = WorldBundle.ExistingPrefix + slug;
                    }

                    if (resolved != null && !draft.Links.Contains(resolved)) draft.Links.Add(resolved);
                }

                if (draft is { Kind: DraftKinds.Edit, Remove: true, Target: { } deleted })
                {
                    // A place deleted while another entry goes inside it would leave that entry nowhere.
                    foreach (var inside in bundle.Drafts.Where(d => d != draft && string.Equals(d.Parent, deleted, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"{inside.Ref} ({inside.Kind} '{inside.Name}'): its parent '{inside.Parent}' is deleted by {draft.Ref}; choose another parent.");
                    }
                }

                if (!(draft.Kind == DraftKinds.Place || draft is { Kind: DraftKinds.Edit, MoveTo: true }) || draft.Parent == null) continue;
                if (draft.Kind == DraftKinds.Edit && string.Equals(draft.Parent, draft.Target, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{label}: a place cannot be moved inside itself.");
                    continue;
                }

                if (draft.Parent.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var wanted = draft.Parent[(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix).Length..];
                    if (rules.ExistingPlaces.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase)) is not { } storedPlace)
                    {
                        errors.Add($"{label}: parent '{draft.Parent}' is not a place the campaign has; use an id from list_places.");
                    }
                    else
                    {
                        // The id as stored, whatever its case in the key: the check and the save compare it exactly.
                        draft.Parent = WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix + storedPlace;
                    }

                    continue;
                }

                var parentDraft = bundle.Drafts.FirstOrDefault(d => string.Equals(d.Ref, draft.Parent, StringComparison.OrdinalIgnoreCase));
                if (parentDraft?.Kind != DraftKinds.Place || parentDraft == draft)
                {
                    errors.Add($"{label}: parent '{draft.Parent}' must be another place entry's ref or existing:place:<id>.");
                    continue;
                }

                draft.Parent = parentDraft.Ref;
            }

            // A loop of parents among the new places has no top to hang from.
            foreach (var draft in bundle.Drafts.Where(d => d.Kind == DraftKinds.Place))
            {
                var seen = new HashSet<string> { draft.Ref };
                for (var parent = draft.Parent; parent != null && bundle.Find(parent) is { } up; parent = up.Parent)
                {
                    if (!seen.Add(up.Ref))
                    {
                        errors.Add($"{draft.Ref} (place '{draft.Name}'): its parents go round in a loop.");
                        break;
                    }
                }
            }
        }

        return new ProposalCheck(bundle, errors, warnings);
    }

    /// <summary>The most issue ids one entry may cite.</summary>
    public const int MaxResolves = 50;

    /// <summary>The ids an entry cites, trimmed and each once. Refused (an error, so the whole proposal is) rather than cut: an id past
    /// <see cref="MaxResolves"/>, or a member that is not a string, would otherwise be left out without a word, and the model would believe it claimed it.
    /// <paramref name="given"/> says the entry wrote the field at all (a revision's own list, even an empty one, replaces what it claimed before).</summary>
    private static List<string> ReadIds(JsonElement element, string name, string label, List<string> errors, out bool given)
    {
        var ids = new List<string>();
        given = false;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return ids;
        given = true;
        if (value.ValueKind == JsonValueKind.String) ids.Add(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in value.EnumerateArray())
            {
                if (member.ValueKind == JsonValueKind.String) ids.Add(member.GetString()!);
                else errors.Add($"{label}: \"{name}\" is a list of issue ids as text, exactly as list_issues gives them; one of its members is not text, so the proposal was refused.");
            }
        }
        else errors.Add($"{label}: \"{name}\" is a list of issue ids as text, exactly as list_issues gives them.");

        var distinct = ids.Select(id => id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > MaxResolves)
        {
            errors.Add($"{label}: \"{name}\" names {distinct.Count} issues, and one entry may name at most {MaxResolves}; none was dropped, so the proposal was refused. Claim fewer, or put the rest on another entry.");
        }

        return distinct;
    }

    /// <summary>
    /// What each entry's <c>resolves</c> may name. The checker, not the model, says what is broken, so an id must be an issue that is open
    /// right now (<see cref="ConsistencyChecker.UnknownIds"/>), one the review gate can fix (not report-only), and about the very thing the
    /// entry changes: an edit of that page, place, cast member's history or secret. Anything else is an error that names the ids, and the
    /// whole proposal is refused with it.
    /// </summary>
    private static void CheckResolves(WorldBundle bundle, ProposalRules rules, IReadOnlyList<Issue> open, List<string> errors)
    {
        var byId = open.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var draft in bundle.Drafts.Where(d => d.Resolves.Count > 0))
        {
            var label = $"{draft.Ref} ({draft.Kind} '{draft.Name}')";
            var unknown = ConsistencyChecker.UnknownIds(draft.Resolves, open);
            if (unknown.Count > 0)
            {
                errors.Add($"{label}: resolves names {(unknown.Count == 1 ? "an id that is not an open issue" : "ids that are not open issues")}: {string.Join(", ", unknown)}. " +
                           "Call list_issues and copy the ids exactly; an issue that was fixed, or that the game master dismissed, is no longer open. " +
                           "The whole proposal was refused; send it again with the right ids, or without \"resolves\" (a revision of an entry that lists its own \"resolves\", even an empty list, replaces the claims it made before).");
            }

            var (kind, targetId) = EditedThing(draft, rules);
            foreach (var id in draft.Resolves.Where(byId.ContainsKey))
            {
                var issue = byId[id];
                if (issue.FixVia == FixVia.ReportOnly)
                {
                    errors.Add($"{label}: resolves {id}, which no proposal can fix (the review gate cannot change it: {issue.Check}); leave it out and tell the game master it is theirs to fix by hand.");
                }
                else if (SheetRoute(draft) != (issue.FixVia == FixVia.Sheet))
                {
                    errors.Add($"{label}: resolves {id}, which is fixed {(issue.FixVia == FixVia.Sheet ? "by an edit of the character's sheet (target existing:sheet:<id>)" : $"by a change of its {FixViaWords(issue.FixVia)}")}, " +
                               $"but this entry is {(SheetRoute(draft) ? "an edit of a sheet" : "not an edit of a sheet")}; resolve an issue only with the kind of edit its fix_via names.");
                }
                else if (kind == null || kind != issue.EntityKind || !string.Equals(targetId, issue.EntityId, StringComparison.Ordinal))
                {
                    errors.Add($"{label}: resolves {id}, an issue about {issue.EntityKind} {issue.EntityId}, but this entry " +
                               (kind == null ? "is not an edit of a page, place, cast member or secret" : $"changes {kind} {targetId}") +
                               "; an entry may resolve only issues about the thing it edits. Put the fix in an edit entry whose target is that thing.");
                }
            }
        }
    }

    private static bool SheetRoute(WorldDraft draft) =>
        draft is { Kind: DraftKinds.Edit, Target: { } t } && WorldBundle.ParseTarget(t, out _) == EditTarget.Sheet;

    private static string FixViaWords(FixVia fixVia) => fixVia switch
    {
        FixVia.ActorBiography => "history",
        FixVia.Place => "place",
        FixVia.Fact => "secret",
        FixVia.Homebrew => "homebrew entry",
        _ => "page"
    };

    /// <summary>What an entry changes, as the consistency checker names things (<c>page</c>, <c>place</c>, <c>actor</c>, <c>fact</c> and the
    /// document id), or null for an entry that is not an edit of one of those.</summary>
    private static (string? Kind, string? Id) EditedThing(WorldDraft draft, ProposalRules rules)
    {
        if (draft is not { Kind: DraftKinds.Edit, Target: { } target }) return (null, null);
        switch (WorldBundle.ParseTarget(target, out var id))
        {
            case EditTarget.Place: return ("place", id);
            case EditTarget.Actor: return ("actor", id);
            case EditTarget.Fact: return ("fact", id);
            case EditTarget.Sheet: return ("actor", id);
            case EditTarget.Homebrew: return ("homebrew", id);
            case EditTarget.Page:
                var pageId = rules.ExistingPageIds is { } ids ? ids.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value : null;
                return pageId == null ? (null, null) : ("page", pageId);
            default: return (null, null);
        }
    }

    /// <summary>Reads an edit: what it targets (which must exist, and be changed only once) and what changes. A page, a place's
    /// description or a cast member's history takes the complete new text; a homebrew entry takes any of a new name, a new rules
    /// text, the fields that change and its complete new tags.</summary>
    private static bool ReadEdit(WorldDraft draft, JsonElement element, string label, Dictionary<string, string> pages, ProposalRules rules, WorldBundle bundle, List<string> errors, List<string> warnings, int textLength)
    {
        var key = FieldCoercion.String(element, "target")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            errors.Add($"{label}: an edit needs a \"target\": the key of what to change (existing:<slug> for a page, existing:place:<id> for a place's description, existing:actor:<id> for a cast member's history, existing:homebrew:<type>/<id> for a homebrew entry).");
            return false;
        }

        var kind = WorldBundle.ParseTarget(key, out var id);
        // The id as stored, whatever its case in the key: what is saved later matches it exactly.
        var stored = kind switch
        {
            EditTarget.Place => rules.ExistingPlaces.FirstOrDefault(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase)),
            EditTarget.Actor => rules.ExistingActors?.FirstOrDefault(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)),
            EditTarget.Homebrew => rules.ExistingHomebrew?.Keys.FirstOrDefault(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)),
            EditTarget.Region => rules.ExistingRegions?.Keys.FirstOrDefault(r => string.Equals(r, id, StringComparison.OrdinalIgnoreCase)),
            EditTarget.Fact => rules.ExistingFacts?.Keys.FirstOrDefault(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase)),
            EditTarget.Sheet => rules.ExistingSheets?.FirstOrDefault(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)),
            _ => pages.Keys.FirstOrDefault(slug => string.Equals(slug, id, StringComparison.OrdinalIgnoreCase))
        };
        if (stored == null)
        {
            errors.Add(kind == EditTarget.Homebrew
                ? $"{label}: target '{key}' is not a homebrew entry of the campaign; a pack entry cannot be changed, so make a homebrew variant of it instead (kind homebrew with \"basedOn\")."
                : kind == EditTarget.Sheet
                    ? $"{label}: target '{key}' is not a character whose sheet a proposal can change (it must exist, must not be retired and must not be a bestiary creature); use existing:sheet:<id> with an id from search_world."
                    : $"{label}: target '{key}' is not something the campaign has; use a key from search_world, get_page or list_places.");
            return false;
        }

        id = stored;
        var canonical = WorldBundle.ExistingPrefix + kind switch
        {
            EditTarget.Place => WorldBundle.PlacePrefix + id,
            EditTarget.Actor => WorldBundle.ActorPrefix + id,
            EditTarget.Homebrew => WorldBundle.HomebrewPrefix + id,
            EditTarget.Region => WorldBundle.RegionPrefix + id,
            EditTarget.Fact => WorldBundle.FactPrefix + id,
            EditTarget.Sheet => WorldBundle.SheetPrefix + id,
            _ => id
        };
        if (bundle.Drafts.Any(d => d.Kind == DraftKinds.Edit && string.Equals(d.Target, canonical, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{label}: '{key}' is already changed by another entry; put every change to it in one.");
            return false;
        }

        if (kind == EditTarget.Sheet) return ReadSheetEdit(draft, element, label, canonical, rules, errors);

        // Cut would drop the end of a text that replaces another: refuse instead of saving half of it.
        if (textLength > MaxBody)
        {
            errors.Add($"{label}: the text is {textLength} characters; an edit holds at most {MaxBody}.");
            return false;
        }

        draft.Remove = element.TryGetProperty("delete", out var delete) && delete.ValueKind == JsonValueKind.True;
        draft.Rename = Cut(FieldCoercion.String(element, "rename"), MaxName) is { Length: > 0 } rename ? rename : null;
        var moves = false;
        if (kind == EditTarget.Place)
        {
            // "parent" present, even empty, says where the place goes; empty or "none" is out to the top of its region.
            // A "parent" says where the place goes; "none" or "top" is out to the top of its region. An empty one (a model that
            // fills every optional field) says nothing, so the place stays where it is.
            if (FieldCoercion.String(element, "parent")?.Trim() is { Length: > 0 } parent)
            {
                moves = true;
                draft.MoveTo = true;
                draft.Parent = parent.Equals("none", StringComparison.OrdinalIgnoreCase) || parent.Equals("top", StringComparison.OrdinalIgnoreCase) ? null : parent;
            }

            draft.PlaceKind = OneOf(FieldCoercion.String(element, "placeKind"), PlaceKinds);
            if (draft.PlaceKind == null && FieldCoercion.String(element, "placeKind") is { Length: > 0 } badKind)
            {
                errors.Add($"{label}: placeKind '{badKind}' is not one of {string.Join(", ", PlaceKinds)}.");
                return false;
            }
        }

        if (kind == EditTarget.Region)
        {
            draft.RegionKind = OneOf(FieldCoercion.String(element, "regionKind"), RegionKinds);
            if (draft.RegionKind == null && FieldCoercion.String(element, "regionKind") is { Length: > 0 } badRegionKind)
            {
                errors.Add($"{label}: regionKind '{badRegionKind}' is not one of {string.Join(", ", RegionKinds)}.");
                return false;
            }

            if (draft.Body != null)
            {
                errors.Add($"{label}: a region has no text; change its \"rename\", its \"regionKind\", or \"delete\" it.");
                return false;
            }
        }

        // A cast member's stat block is laid over from another Library creature; their other sheet entries stay.
        if (kind == EditTarget.Actor && Cut(FieldCoercion.String(element, "stats"), MaxStats) is { Length: > 0 } restat) draft.SuggestedStats = restat;

        if (draft.Remove)
        {
            if (draft.Body != null || draft.Rename != null || moves || draft.PlaceKind != null || draft.RegionKind != null || draft.SuggestedStats != null || draft.Properties.Count > 0)
            {
                errors.Add($"{label}: a \"delete\" stands alone; make the other changes in a separate entry, or none.");
                return false;
            }

            draft.Target = canonical;
            // A delete is the game master's to tick: it arrives unticked, so applying everything never deletes anything unlooked-at.
            draft.Selected = false;
            draft.Name = kind switch
            {
                EditTarget.Homebrew => rules.ExistingHomebrew![id],
                EditTarget.Region => rules.ExistingRegions![id],
                EditTarget.Fact => rules.ExistingFacts![id],
                _ => draft.Name.Length > 0 ? draft.Name : canonical
            };
            draft.OutlineName = draft.Name;
            return true;
        }

        if (kind == EditTarget.Homebrew)
        {
            var type = id.Split('/')[0];
            ApplyHomebrew(draft, element, rules.HomebrewFields(type), rules.Canonical, label, warnings);
            draft.Type = type;
            // Tags only change when the model names them: an edit of the text alone keeps the entry's tags.
            draft.Retag = element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array;
            if (draft.Body == null && draft.Properties.Count == 0 && draft.Rename == null && !draft.Retag)
            {
                errors.Add($"{label}: the edit changes nothing; give a new \"text\" (the complete rules text), the \"properties\" that change, a \"rename\" or the complete new \"tags\", or \"delete\": true.");
                return false;
            }

            draft.Target = canonical;
            draft.Name = rules.ExistingHomebrew![id];
            draft.OutlineName = draft.Name;
            return true;
        }

        if (draft.Body == null && draft.Rename == null && !moves && draft.PlaceKind == null && draft.RegionKind == null && draft.SuggestedStats == null)
        {
            errors.Add($"{label}: an edit needs something to change: its \"text\" (the COMPLETE new text, which replaces the old one), a \"rename\"" +
                       (kind == EditTarget.Place ? ", a new \"parent\", a \"placeKind\"" : kind == EditTarget.Region ? ", a \"regionKind\"" : kind == EditTarget.Actor ? ", new \"stats\"" : string.Empty) +
                       ", or \"delete\": true.");
            return false;
        }

        draft.Target = canonical;
        if (draft.Name.Length == 0)
        {
            draft.Name = kind switch
            {
                EditTarget.Page when pages.TryGetValue(id, out var title) => title,
                EditTarget.Region => rules.ExistingRegions![id],
                EditTarget.Fact => rules.ExistingFacts![id],
                _ => canonical
            };
        }

        draft.OutlineName = draft.Name;
        return true;
    }

    /// <summary>Reads an edit of a character's sheet: the keys to set (with their new values) and to clear. A sheet has no text, no new name
    /// and cannot be deleted; its values are checked for shape here and against the Library and the sheet rules when the proposal is checked.</summary>
    private static bool ReadSheetEdit(WorldDraft draft, JsonElement element, string label, string canonical, ProposalRules rules, List<string> errors)
    {
        if (element.TryGetProperty("delete", out var delete) && delete.ValueKind == JsonValueKind.True
            || draft.Body != null
            || FieldCoercion.String(element, "rename") is { Length: > 0 }
            || FieldCoercion.String(element, "stats") is { Length: > 0 })
        {
            errors.Add($"{label}: an edit of a sheet only sets or clears sheet keys (\"sheet\": {{\"key\": value}}); it has no text, rename, stats or delete. Use an edit of the character for their history or to retire them.");
            return false;
        }

        if (!element.TryGetProperty("sheet", out var sheet))
        {
            errors.Add($"{label}: an edit of a sheet needs \"sheet\": an object of the keys to set (value) or clear (null).");
            return false;
        }

        var before = errors.Count;
        var (set, clear) = SheetProposal.Read(rules.System, sheet, label, errors);
        if (errors.Count > before) return false;
        if (set.Count == 0 && clear.Count == 0)
        {
            errors.Add($"{label}: the sheet edit changes nothing; give at least one key.");
            return false;
        }

        draft.SheetSet = set.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        draft.SheetClear = clear;
        draft.Target = canonical;
        draft.Name = draft.Name.Length > 0 ? draft.Name : canonical;
        draft.OutlineName = draft.Name;
        return true;
    }

    /// <summary>Reads a tags entry: the pages and homebrew entries it tags (each must exist) and the tags to add and take off.</summary>
    private static bool ReadTags(WorldDraft draft, JsonElement element, string label, Dictionary<string, string> pages, ProposalRules rules, List<string> errors)
    {
        var keys = new List<string>();
        if (element.TryGetProperty("targets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            keys.AddRange(list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.Trim()));
        }

        if (FieldCoercion.String(element, "target") is { Length: > 0 } one) keys.Add(one.Trim());
        draft.TagsAdded = ReadTagList(element, "add");
        draft.TagsRemoved = ReadTagList(element, "remove");
        if (draft.TagsAdded.Count == 0 && draft.TagsRemoved.Count == 0)
        {
            errors.Add($"{label}: a tags entry needs \"add\" or \"remove\": the tags to put on or take off.");
            return false;
        }

        foreach (var key in keys.Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var kind = WorldBundle.ParseTarget(key, out var id);
            var stored = kind switch
            {
                EditTarget.Homebrew => rules.ExistingHomebrew?.Keys.FirstOrDefault(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)) is { } h ? WorldBundle.HomebrewPrefix + h : null,
                EditTarget.Page => pages.Keys.FirstOrDefault(slug => string.Equals(slug, id, StringComparison.OrdinalIgnoreCase)),
                _ => null
            };
            if (stored == null)
            {
                errors.Add($"{label}: target '{key}' is not a wiki page (existing:<slug>) or a homebrew entry (existing:homebrew:<type>/<id>) of the campaign; only those carry tags.");
                return false;
            }

            draft.Targets.Add(WorldBundle.ExistingPrefix + stored);
        }

        if (draft.Targets.Count == 0)
        {
            errors.Add($"{label}: a tags entry needs \"targets\": the keys of the pages and homebrew entries to tag.");
            return false;
        }

        if (draft.Targets.Count > MaxTagTargets)
        {
            errors.Add($"{label}: a tags entry changes at most {MaxTagTargets} things; split it.");
            return false;
        }

        if (draft.Name.Length == 0)
        {
            draft.Name = string.Join(" ", draft.TagsAdded.Select(t => "+" + t).Concat(draft.TagsRemoved.Select(t => "−" + t)));
            draft.OutlineName = draft.Name;
        }

        return true;
    }

    /// <summary>The most things one tags entry changes.</summary>
    public const int MaxTagTargets = 100;

    /// <summary>A list of tags as the model wrote it: trimmed, inner spaces collapsed, each at most 40 characters, no repeats
    /// (whatever their case), at most 8.</summary>
    internal static List<string> ReadTagList(JsonElement element, string key) =>
        element.TryGetProperty(key, out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => System.Text.RegularExpressions.Regex.Replace(t.GetString()!, @"\s+", " ").Trim())
                .Select(t => t.Length > 40 ? t[..40].TrimEnd() : t)
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList()
            : new List<string>();

    private static string? OneOf(string? value, string[] allowed, string? alias = null, string? aliasOf = null)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (v == null) return null;
        if (alias != null && v == alias) return aliasOf;
        return allowed.Contains(v) ? v : null;
    }


    // A place's "feature": {"container", "lock", "trap", "hazard", "concealment", "contents", "creature"}; null when it says nothing.
    // A place's hazard names a Library entry (type/id), a homebrew entry the campaign has (existing:homebrew:type/id) or the ref of a homebrew hazard in
    // this proposal. One that names nothing is dropped with a warning (never saved dangling); on a system with no hazard type the hazard is ignored, and
    // its free-text "trap" stays as it always did. A Library entry is looked up by the check, which has the Library.
    private static void ResolveHazards(WorldBundle bundle, ProposalRules rules, List<string> warnings)
    {
        var hazardType = rules.HazardType;
        foreach (var draft in bundle.Drafts.Where(d => d is { Kind: DraftKinds.Place, Feature: not null }))
        {
            var feature = draft.Feature!;
            var label = $"{draft.Ref} (place '{draft.Name}')";
            if (feature.Hazard is not { Length: > 0 } written)
            {
                if (hazardType != null && feature.Trap != null)
                {
                    warnings.Add($"{label}: if this trap rolls (a save, an attack or damage), give the feature \"hazard\" (a Library hazard or a homebrew one in this proposal) and keep \"trap\" for a note.");
                }

                continue;
            }

            feature.Hazard = null;
            if (hazardType == null)
            {
                warnings.Add($"{label}: this system has no hazard type, so the hazard '{written}' was ignored; describe the danger in \"trap\".");
            }
            else if (written.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var key = written[(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix).Length..];
                var found = rules.ExistingHomebrew?.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (found != null && found.Split('/') is [var type, _] && string.Equals(type, hazardType, StringComparison.OrdinalIgnoreCase))
                {
                    feature.HazardEntry = found;
                    feature.HazardIsHomebrew = true;
                    feature.HazardName = rules.ExistingHomebrew![found];
                }
                else
                {
                    warnings.Add($"{label}: hazard '{written}' is not a {hazardType} homebrew entry the campaign has, so it was ignored; use the key search_library lists.");
                }
            }
            else if (bundle.Drafts.FirstOrDefault(d => string.Equals(d.Ref, written, StringComparison.OrdinalIgnoreCase)) is { } made)
            {
                if (made is { Kind: DraftKinds.Homebrew } && string.Equals(made.Type, hazardType, StringComparison.OrdinalIgnoreCase))
                {
                    feature.HazardRef = made.Ref;
                    feature.HazardName = made.Name;
                }
                else
                {
                    warnings.Add($"{label}: hazard '{written}' is not a homebrew {hazardType} entry of this proposal, so it was ignored.");
                }
            }
            else if (written.Split('/') is [var libraryType, { Length: > 0 }] && string.Equals(libraryType, hazardType, StringComparison.OrdinalIgnoreCase))
            {
                feature.HazardEntry = written;
            }
            else
            {
                warnings.Add($"{label}: hazard '{written}' is neither a Library {hazardType} (as {hazardType}/<id>), nor an existing homebrew one, nor the ref of a homebrew {hazardType} in this proposal, so it was ignored.");
            }

            if (feature is { Container: false, Lock: null, Trap: null, Concealment: null, Contents: null, Creature: null, HazardEntry: null, HazardRef: null }) draft.Feature = null;
        }
    }

    private static DraftFeature? ReadFeature(JsonElement element)
    {
        if (!element.TryGetProperty("feature", out var f) || f.ValueKind != JsonValueKind.Object) return null;
        var feature = new DraftFeature
        {
            Container = f.TryGetProperty("container", out var container) && container.ValueKind == JsonValueKind.True,
            Lock = Note(FieldCoercion.String(f, "lock"), MaxFeatureNote),
            Trap = Note(FieldCoercion.String(f, "trap"), MaxFeatureNote),
            Concealment = Note(FieldCoercion.String(f, "concealment"), MaxFeatureNote),
            Contents = Note(FieldCoercion.String(f, "contents"), MaxFeatureContents),
            Creature = Note(FieldCoercion.String(f, "creature"), MaxFeatureNote),
            Hazard = Note(FieldCoercion.String(f, "hazard"), MaxFeatureNote)
        };
        return feature is { Container: false, Lock: null, Trap: null, Concealment: null, Contents: null, Creature: null, Hazard: null } ? null : feature;

        static string? Note(string? text, int max) => Cut(text, max) is { Length: > 0 } cut ? cut : null;
    }

    /// <summary>
    /// Fills a homebrew entry's fields from <c>{"tags", "properties": {field key: value}}</c> (its text is already the description).
    /// Values are coerced to their field's type and, through <paramref name="canonical"/> (field key, value written), back to
    /// the stored value of a translated option; unknown keys and values that do not fit are dropped.
    /// </summary>
    private static void ApplyHomebrew(WorldDraft draft, JsonElement root, IReadOnlyList<FieldSpec> fields, Func<string, string, string>? canonical, string label, List<string> warnings)
    {
        var specs = fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, object>();
        if (root.TryGetProperty("properties", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in values.EnumerateObject())
            {
                if (!specs.TryGetValue(prop.Name, out var spec)) continue;
                var value = FieldCoercion.Coerce(prop.Value, spec, canonical == null ? null : v => canonical(spec.Key, v));
                if (value != null)
                {
                    properties[spec.Key] = value;
                }

                // FieldCoercion drops a row with no "name" (or, where rows have none, nothing in it), so a list of them must not vanish unseen.
                if (spec.Type == "Collection" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var asked = prop.Value.EnumerateArray().Count(e => e.ValueKind == JsonValueKind.Object);
                    var kept = (value as System.Collections.ICollection)?.Count ?? 0;
                    if (asked > kept) warnings.Add($"{label}: {asked - kept} row(s) of '{spec.Key}' {(spec.RowsNeedName ? "have no \"name\" and were dropped; every row of this list needs one" : "had nothing in them and were dropped")}.");
                }
            }
        }

        draft.Tags = ReadTagList(root, "tags");
        draft.Properties = properties;
    }

    /// <summary>
    /// The text to save for <paramref name="text"/>: a <c>[[link]]</c> to another entry (by its ref or its name as proposed) is
    /// rewritten to the entry's final title when that entry is saved as a wiki page (a page, a person or a place), and left as plain
    /// text otherwise (a homebrew entry, a secret or an unticked entry has no page to link to). Other links are kept, unless
    /// <paramref name="keepLinks"/> is false (homebrew text, which is not wiki text), when every link becomes plain text.
    /// </summary>
    public static string ResolveLinks(string text, WorldBundle bundle, bool keepLinks = true)
    {
        return WikiLink.Replace(text, match =>
        {
            var target = match.Groups[1].Value.Trim();
            var shown = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            // The ref wins, then the name it was proposed under, then the DM's rename: renaming one entry to another's proposed name
            // must not steal the links meant for the other.
            // An edit, a retagging or a Bestiary pick has no page of its own to link to (an edit is named after the page it changes,
            // which the link already reaches).
            var linkable = bundle.Drafts.Where(d => !DraftKinds.ChangesExisting(d.Kind)).ToList();
            var draft = linkable.FirstOrDefault(d => string.Equals(d.Ref, target, StringComparison.OrdinalIgnoreCase))
                        ?? linkable.FirstOrDefault(d => string.Equals(d.OutlineName, target, StringComparison.OrdinalIgnoreCase))
                        ?? linkable.FirstOrDefault(d => string.Equals(d.Name.Trim(), target, StringComparison.OrdinalIgnoreCase));
            if (draft == null)
            {
                return keepLinks ? match.Value : shown ?? target;
            }

            // An entry whose name was cleared is not saved, so it is named as it was proposed, unlinked.
            var name = string.IsNullOrWhiteSpace(draft.Name) ? draft.OutlineName : draft.Name.Trim();
            var linked = keepLinks && draft.Selected && !string.IsNullOrWhiteSpace(draft.Name) && DraftKinds.IsPage(draft.Kind);
            var title = draft.PageTitle ?? name;
            return linked ? (shown == null ? $"[[{title}]]" : $"[[{title}|{shown}]]") : shown ?? name;
        });
    }

    /// <summary>The names the proposal's wiki text links to that nothing is called: not an entry of the proposal (ticked or not, as
    /// proposed or as renamed) and not in <paramref name="known"/> (the titles of the campaign's pages, places and cast). Links
    /// by key (<c>existing:…</c>, <c>actor:…</c>) point at something that exists and are never missing. In the order they first
    /// appear.</summary>
    public static IReadOnlyList<MissingMention> MissingMentions(WorldBundle bundle, IReadOnlyCollection<string> known)
    {
        // By slug, the way the wiki finds the page a link means: "Brinewick", "brinewick" and "Brine-wick" are one name, and a curly
        // apostrophe is a straight one.
        var have = new HashSet<string>(known.Select(WikiText.Slugify), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(bundle.Region)) have.Add(WikiText.Slugify(bundle.Region));
        foreach (var d in bundle.Drafts)
        {
            have.Add(WikiText.Slugify(d.Ref));
            have.Add(WikiText.Slugify(d.OutlineName));
            have.Add(WikiText.Slugify(d.Name));
        }

        // One line per name however it is spelled, shown as it was first written.
        var missing = new Dictionary<string, (string Name, List<WorldDraft> By)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var d in bundle.Drafts.Where(d => d.Selected && !string.IsNullOrWhiteSpace(d.Body) && (DraftKinds.IsPage(d.Kind) || d is { Kind: DraftKinds.Edit } && WorldBundle.ParseTarget(d.Target ?? string.Empty, out _) != EditTarget.Homebrew)))
        {
            foreach (System.Text.RegularExpressions.Match match in WikiLink.Matches(d.Body!))
            {
                var name = match.Groups[1].Value.Trim();
                // A link by key (existing:slug, actor:id, place:id, item:id) points at something that is there. A name may hold a colon.
                if (name.Length == 0 || KeyLink.IsMatch(name)) continue;
                var slug = WikiText.Slugify(name);
                if (have.Contains(slug)) continue;
                if (!missing.TryGetValue(slug, out var entry))
                {
                    missing[slug] = entry = (name, new List<WorldDraft>());
                    order.Add(slug);
                }

                if (!entry.By.Contains(d)) entry.By.Add(d);
            }
        }

        return order.Select(slug => new MissingMention(missing[slug].Name, missing[slug].By.Select(d => d.Name).ToList(), missing[slug].By.Select(d => d.Ref).ToList())).ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex KeyLink = new(@"^(existing|actor|place|item):", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Cut(string? text, int max) => text == null ? string.Empty : text.Length <= max ? text.Trim() : text[..max].TrimEnd();

    private static readonly System.Text.RegularExpressions.Regex WikiLink = new(@"\[\[([^\[\]|]+?)(?:\|([^\[\]]+?))?\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);
}
