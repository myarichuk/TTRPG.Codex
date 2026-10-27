using System.Text.Json;
using Codex.Core.AI;

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

    public static readonly IReadOnlyList<string> All = new[] { Homebrew, Wiki, Npc, Fact, Place };

    /// <summary>Kinds saved as wiki pages, which other entries' text can link to.</summary>
    public static bool IsPage(string kind) => kind is Wiki or Npc or Place;
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

    /// <summary>For a place: the place it is inside, another entry's <see cref="Ref"/> or <c>existing:place:&lt;id&gt;</c>; empty
    /// for a place straight in the region.</summary>
    public string? Parent { get; set; }

    /// <summary>For a place: "area", "settlement", "district" or "site".</summary>
    public string? PlaceKind { get; set; }

    /// <summary>For a wiki page: "lore", "faction" or "quest".</summary>
    public string? PageKind { get; set; }

    /// <summary>The title its wiki page is saved under: the name, or "Name (2)" when a page of that name exists. Links go by
    /// title, so they use it; a cast member or a place keeps the name itself.</summary>
    public string? PageTitle { get; set; }

    /// <summary>What saving already made of this entry, by step ("page", "homebrew", "cast", "place", "fact") to document id, so a
    /// save that failed half-way can be retried without writing anything twice.</summary>
    public Dictionary<string, string> SavedAs { get; set; } = new();

    /// <summary>A page's text, a homebrew entry's description (its rules text), or a secret's details.</summary>
    public string? Body { get; set; }

    public Dictionary<string, object> Properties { get; set; } = new();
    public List<string> Tags { get; set; } = new();
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

    /// <summary>What the proposal adds, in a sentence, for the review card.</summary>
    public string? Summary { get; set; }

    /// <summary>The region new top-level places go into (found by name, made when there is none), or empty for the campaign's first.</summary>
    public string? Region { get; set; }

    public List<WorldDraft> Drafts { get; set; } = new();

    public WorldDraft? Find(string reference) => Drafts.FirstOrDefault(d => d.Ref == reference);
}

/// <summary>What a proposal may refer to. <see cref="HomebrewTypes"/> maps every spelling the model might use for a type (its key,
/// folder, English and translated labels) to the type key; <see cref="ExistingPages"/> maps the campaign's page slugs to their
/// titles; <see cref="HomebrewFields"/> gives a type's fields.</summary>
public sealed record ProposalRules(
    IReadOnlyDictionary<string, string> HomebrewTypes,
    IReadOnlyDictionary<string, string> ExistingPages,
    IReadOnlyCollection<string> ExistingPlaces,
    Func<string, IReadOnlyList<FieldSpec>> HomebrewFields,
    Func<string, string, string>? Canonical = null);

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

    private static readonly string[] PlaceKinds = { "area", "settlement", "district", "site" };
    private static readonly string[] PageKinds = { "lore", "faction", "quest" };

    /// <summary>
    /// Checks a proposal, <c>{"summary", "region", "entries": [{"ref", "kind", "name", "text", ...}]}</c>, and turns it into the
    /// entries the DM reviews. Kind-specific fields: a person's <c>cast</c> and <c>stats</c>; a place's <c>placeKind</c> and
    /// <c>parent</c> (another entry's ref or <c>existing:place:&lt;id&gt;</c>); a page's <c>pageKind</c>; a homebrew entry's
    /// <c>type</c>, <c>tags</c> and <c>properties</c>. <c>links</c> (and <c>[[ref]]</c> in the text) point at other entries
    /// or <c>existing:&lt;slug&gt;</c> pages. An entry that can't be saved is an error naming it; one the parser had to guess
    /// about is a warning.
    /// </summary>
    public static ProposalCheck ParseProposal(string json, ProposalRules rules)
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

                if (name.Length == 0)
                {
                    errors.Add($"{label}: has no name.");
                    continue;
                }

                if (!DraftKinds.All.Contains(kind))
                {
                    errors.Add($"{label}: kind must be one of {string.Join(", ", DraftKinds.All)}.");
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

                        break;
                    case DraftKinds.Wiki:
                        draft.PageKind = OneOf(FieldCoercion.String(element, "pageKind"), PageKinds) ?? "lore";
                        break;
                    case DraftKinds.Homebrew:
                        if (FieldCoercion.String(element, "type") is not { } typeText || !types.TryGetValue(typeText, out var type))
                        {
                            errors.Add($"{label}: type must be one of the homebrew types ({string.Join(", ", types.Values.Distinct())}).");
                            continue;
                        }

                        draft.Type = type;
                        draft.Bestiary = element.TryGetProperty("bestiary", out var bestiary) && bestiary.ValueKind == JsonValueKind.True;
                        ApplyHomebrew(draft, element, rules.HomebrewFields(type), rules.Canonical);
                        break;
                }

                if (kind != DraftKinds.Place && FieldCoercion.String(element, "parent") is { Length: > 0 })
                {
                    warnings.Add($"{label}: only a place has a parent; it was ignored.");
                }

                if (draft.Body == null && kind != DraftKinds.Fact)
                {
                    warnings.Add($"{label}: has no text, so its page will only hold its name.");
                }

                var links = element.TryGetProperty("links", out var linkList) && linkList.ValueKind == JsonValueKind.Array
                    ? linkList.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!.Trim()).ToList()
                    : new List<string>();
                links.AddRange(WikiLink.Matches(text).Select(m => m.Groups[1].Value.Trim()));
                rawLinks[draft] = links;
                bundle.Drafts.Add(draft);
            }

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
                    else if (bundle.Drafts.FirstOrDefault(d => string.Equals(d.Ref, link, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, link, StringComparison.OrdinalIgnoreCase)) is { } target)
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

                if (draft.Kind != DraftKinds.Place || draft.Parent == null) continue;
                if (draft.Parent.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!places.Contains(draft.Parent[(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix).Length..]))
                    {
                        errors.Add($"{label}: parent '{draft.Parent}' is not a place the campaign has; use an id from list_places.");
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

    private static string? OneOf(string? value, string[] allowed, string? alias = null, string? aliasOf = null)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (v == null) return null;
        if (alias != null && v == alias) return aliasOf;
        return allowed.Contains(v) ? v : null;
    }

    /// <summary>
    /// Fills a homebrew entry's fields from <c>{"tags", "properties": {field key: value}}</c> (its text is already the description).
    /// Values are coerced to their field's type and, through <paramref name="canonical"/> (field key, value written), back to
    /// the stored value of a translated option; unknown keys and values that do not fit are dropped.
    /// </summary>
    private static void ApplyHomebrew(WorldDraft draft, JsonElement root, IReadOnlyList<FieldSpec> fields, Func<string, string, string>? canonical)
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
            }
        }

        draft.Tags = root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList()
            : new List<string>();
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
            var draft = bundle.Drafts.FirstOrDefault(d => string.Equals(d.Ref, target, StringComparison.OrdinalIgnoreCase))
                        ?? bundle.Drafts.FirstOrDefault(d => string.Equals(d.OutlineName, target, StringComparison.OrdinalIgnoreCase))
                        ?? bundle.Drafts.FirstOrDefault(d => string.Equals(d.Name.Trim(), target, StringComparison.OrdinalIgnoreCase));
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

    private static string Cut(string? text, int max) => text == null ? string.Empty : text.Length <= max ? text.Trim() : text[..max].TrimEnd();

    private static readonly System.Text.RegularExpressions.Regex WikiLink = new(@"\[\[([^\[\]|]+?)(?:\|([^\[\]]+?))?\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);
}
