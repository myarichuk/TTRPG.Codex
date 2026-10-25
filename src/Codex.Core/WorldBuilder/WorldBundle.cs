using System.Text.Json;
using Codex.Core.AI;

namespace Codex.Core.WorldBuilder;

/// <summary>
/// One thing the model proposed: a homebrew entry, a wiki page, a person, a place or a secret. The outline call fills the name, pitch and links;
/// the expand call fills <see cref="Body"/> (and, for homebrew, <see cref="Properties"/> and <see cref="Tags"/>).
/// Nothing is stored until the DM saves the bundle.
/// </summary>
public sealed class WorldDraft
{
    /// <summary>The draft's id inside its bundle ("d1"); gone once saved.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>One of <see cref="DraftKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>For a homebrew draft: the system's content type key ("spell").</summary>
    public string? Type { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The name the outline gave, which the expanded text links to even after the DM renames the draft.</summary>
    public string OutlineName { get; set; } = string.Empty;

    public string Pitch { get; set; } = string.Empty;

    /// <summary>Other drafts' <see cref="Ref"/>s and <c>existing:&lt;key&gt;</c> pointers at what the campaign already has.</summary>
    public List<string> Links { get; set; } = new();

    /// <summary>Ticked on the review step; only selected drafts are saved.</summary>
    public bool Selected { get; set; } = true;

    public bool Expanded { get; set; }

    /// <summary>For a person: also add them to the cast, hidden from players, besides their wiki page.</summary>
    public bool Cast { get; set; }

    /// <summary>For a place: also add it to the World page, hidden, besides its wiki page.</summary>
    public bool OnMap { get; set; }

    /// <summary>For a homebrew creature: also put it on the campaign's Bestiary shortlist, besides the Library.</summary>
    public bool Bestiary { get; set; }

    /// <summary>For a person: the creature the model said plays like them ("Veteran"), as it wrote it, until the app finds it in the Library.</summary>
    public string? SuggestedStats { get; set; }

    /// <summary>For a person added to the cast: the Library creature whose stat block their sheet starts from, instead of a blank sheet.</summary>
    public CreatureRef? Stats { get; set; }

    /// <summary>The DM picked <see cref="Stats"/> (or a blank sheet) themselves, so a change to the outline leaves it alone.</summary>
    public bool StatsChosen { get; set; }

    /// <summary>The title its wiki page is saved under: the name, or "Name (2)" when a page of that name exists. Links go by
    /// title, so they use it; a cast member or a place keeps the name itself.</summary>
    public string? PageTitle { get; set; }

    /// <summary>What saving already made of this draft, by step ("page", "homebrew", "cast", "place", "fact") to document id, so a
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

/// <summary>A draft the parser left out, and why, so the review step can say so.</summary>
public sealed record DroppedDraft(string Name, DropReason Reason);

public enum DropReason
{
    /// <summary>A kind the recipe does not make.</summary>
    Kind,

    /// <summary>A homebrew type the system does not have.</summary>
    Type,

    /// <summary>No name.</summary>
    Unnamed,

    /// <summary>Past the most drafts one outline may propose (<see cref="WorldBundleParser.MaxDrafts"/>).</summary>
    TooMany
}

/// <summary>The drafts one outline call proposed.</summary>
public sealed class WorldBundle
{
    public const string ExistingPrefix = "existing:";

    public List<WorldDraft> Drafts { get; set; } = new();
    public List<DroppedDraft> Dropped { get; set; } = new();

    /// <summary>After a change to the outline: drafts the reply left out but the DM had worked on (written or renamed), which
    /// stay in the bundle unticked rather than being thrown away.</summary>
    public List<string> SetAside { get; set; } = new();

    public WorldDraft? Find(string reference) => Drafts.FirstOrDefault(d => d.Ref == reference);
}

/// <summary>What a reply may contain. <see cref="HomebrewTypes"/> maps every spelling the model might use for a type (its
/// key, folder, English and translated labels) to the type key; <see cref="Existing"/> holds the keys after
/// <see cref="WorldBundle.ExistingPrefix"/> the prompt listed.</summary>
public sealed record BundleRules(
    IReadOnlyCollection<string> Kinds,
    IReadOnlyDictionary<string, string> HomebrewTypes,
    IReadOnlyCollection<string> Existing);

/// <summary>Reads the model's replies to the outline and expand calls. Pure, so every rule is unit-tested without a model.</summary>
public static class WorldBundleParser
{
    /// <summary>The most drafts one outline keeps: every draft is a model call to write, so a runaway reply must not become hundreds.</summary>
    public const int MaxDrafts = 20;

    // Longest name, pitch and text kept from a reply; the rest is cut. A name becomes a page title and its slug.
    private const int MaxName = 200;
    private const int MaxPitch = 500;
    private const int MaxBody = 20_000;
    private const int MaxStats = 100;

    /// <summary>
    /// Parses an outline reply: <c>{"drafts": [{"ref", "kind", "type", "name", "pitch", "links"}]}</c>. Drafts of a kind
    /// or type the rules do not allow are dropped (and listed in <see cref="WorldBundle.Dropped"/>); missing or repeated refs
    /// are renumbered; links to anything that is neither another draft nor a listed existing key are removed.
    /// </summary>
    public static WorldBundle ParseOutline(string text, BundleRules rules) => Parse(text, rules, null);

    /// <summary>
    /// Parses the reply to a change the DM asked of an outline: the whole revised outline, in the outline's shape. A reply draft
    /// that is one of <paramref name="previous"/>'s (same kind and type, matched by its ref, then by the name the outline gave, then
    /// by its current name) is that draft, kept with its ref, text, ticks and what saving made of it; it takes the reply's name
    /// and pitch only while it is unwritten, and its name only while the DM has not renamed it. Other reply drafts are new, with
    /// refs after the highest one in use. A draft the reply left out is removed, unless it was written or renamed: then it stays,
    /// unticked, and is listed in <see cref="WorldBundle.SetAside"/>. Nothing in <paramref name="previous"/> changes when the
    /// reply cannot be read.
    /// </summary>
    public static WorldBundle ParseRevision(string text, BundleRules rules, WorldBundle previous) => Parse(text, rules, previous);

    private static WorldBundle Parse(string text, BundleRules rules, WorldBundle? previous)
    {
        using var doc = FieldCoercion.ParseObject(text);
        var root = doc.RootElement;
        var bundle = new WorldBundle();
        if (!root.TryGetProperty("drafts", out var drafts) || drafts.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The reply has no \"drafts\" list.");
        }

        var kinds = rules.Kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, string>(rules.HomebrewTypes, StringComparer.OrdinalIgnoreCase);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawLinks = new Dictionary<WorldDraft, List<string>>();
        // Drafts of the previous outline the reply kept, with the name and pitch it gave them; applied only once the whole reply is read.
        var kept = new Dictionary<WorldDraft, (string Name, string Pitch, string? Stats)>();
        var next = 1 + (previous?.Drafts.Select(d => int.TryParse(d.Ref.TrimStart('d'), out var n) ? n : 0).DefaultIfEmpty(0).Max() ?? 0);
        foreach (var element in drafts.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var name = Cut(FieldCoercion.String(element, "name"), MaxName);
            var kind = FieldCoercion.String(element, "kind")?.ToLowerInvariant() ?? string.Empty;
            if (name.Length == 0)
            {
                bundle.Dropped.Add(new DroppedDraft(string.Empty, DropReason.Unnamed));
                continue;
            }

            if (!kinds.Contains(kind))
            {
                bundle.Dropped.Add(new DroppedDraft(name, DropReason.Kind));
                continue;
            }

            if (bundle.Drafts.Count >= MaxDrafts)
            {
                bundle.Dropped.Add(new DroppedDraft(name, DropReason.TooMany));
                continue;
            }

            string? type = null;
            if (kind == DraftKinds.Homebrew && (FieldCoercion.String(element, "type") is not { } typeText || !types.TryGetValue(typeText, out type)))
            {
                bundle.Dropped.Add(new DroppedDraft(name, DropReason.Type));
                continue;
            }

            var pitch = Cut(FieldCoercion.String(element, "pitch"), MaxPitch);
            var stats = kind == DraftKinds.Npc && Cut(FieldCoercion.String(element, "stats"), MaxStats) is { Length: > 0 } s ? s : null;
            var given = FieldCoercion.String(element, "ref");
            WorldDraft draft;
            if (previous != null && Match(previous, kept, given, name, kind, type) is { } match)
            {
                draft = match;
                kept[draft] = (name, pitch, stats);
            }
            else
            {
                draft = new WorldDraft { Ref = $"d{next++}", Kind = kind, Type = type, Name = name, OutlineName = name, Pitch = pitch, SuggestedStats = stats };
            }

            if (given != null)
            {
                renamed.TryAdd(given, draft.Ref);
            }

            rawLinks[draft] = element.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
                ? links.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!.Trim()).ToList()
                : new List<string>();
            bundle.Drafts.Add(draft);
        }

        // A kept draft is still known by its own ref, for a reply that links to it without repeating it.
        foreach (var draft in kept.Keys)
        {
            renamed.TryAdd(draft.Ref, draft.Ref);
        }

        var setAside = previous?.Drafts.Where(d => !kept.ContainsKey(d) && HasWork(d)).ToList() ?? new List<WorldDraft>();
        var final = bundle.Drafts.Concat(setAside).Select(d => d.Ref).ToHashSet();
        var existing = rules.Existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedLinks = new Dictionary<WorldDraft, List<string>>();
        foreach (var draft in bundle.Drafts)
        {
            var list = resolvedLinks[draft] = new List<string>();
            foreach (var link in rawLinks[draft])
            {
                string? resolved = null;
                if (link.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var key = link[WorldBundle.ExistingPrefix.Length..];
                    if (existing.Contains(key)) resolved = WorldBundle.ExistingPrefix + key;
                }
                else if (renamed.TryGetValue(link, out var target) && target != draft.Ref)
                {
                    resolved = target;
                }

                if (resolved != null && !list.Contains(resolved))
                {
                    list.Add(resolved);
                }
            }
        }

        // The whole reply is read: only now do the previous outline's drafts change.
        foreach (var (draft, list) in resolvedLinks)
        {
            draft.Links = list;
        }

        foreach (var (draft, (name, pitch, stats)) in kept)
        {
            // Stats the DM did not pick follow the reply, so a change such as "make her a priest" can bring new ones.
            if (!draft.StatsChosen && !string.Equals(stats, draft.Stats?.Name ?? draft.SuggestedStats, StringComparison.OrdinalIgnoreCase))
            {
                draft.SuggestedStats = stats;
                draft.Stats = null;
            }

            if (draft.Expanded) continue;
            if (draft.Name == draft.OutlineName) draft.Name = name;
            draft.Pitch = pitch;
        }

        foreach (var draft in setAside)
        {
            draft.Selected = false;
            draft.Links = draft.Links.Where(l => l.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase) || final.Contains(l)).ToList();
            bundle.Drafts.Add(draft);
            bundle.SetAside.Add(string.IsNullOrWhiteSpace(draft.Name) ? draft.OutlineName : draft.Name);
        }

        return bundle;
    }

    // The previous draft a reply draft is, if any: same kind and type, by the reply's ref, then the outline's name, then the current name.
    private static WorldDraft? Match(WorldBundle previous, IReadOnlyDictionary<WorldDraft, (string, string, string?)> kept, string? given, string name, string kind, string? type)
    {
        var open = previous.Drafts.Where(d => !kept.ContainsKey(d) && d.Kind == kind && string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
        return (given == null ? null : open.FirstOrDefault(d => string.Equals(d.Ref, given, StringComparison.OrdinalIgnoreCase)))
               ?? open.FirstOrDefault(d => string.Equals(d.OutlineName, name, StringComparison.OrdinalIgnoreCase))
               ?? open.FirstOrDefault(d => string.Equals(d.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    // Work a change to the outline must not throw away: a written text, the DM's rename, stats they picked, or something already saved.
    private static bool HasWork(WorldDraft draft) => draft.Expanded || draft.Name != draft.OutlineName || draft.StatsChosen || draft.SavedAs.Count > 0;

    /// <summary>Fills a wiki draft from the expand reply, which is the page text itself.</summary>
    public static void ApplyPage(WorldDraft draft, string text)
    {
        var body = (text ?? string.Empty).Trim();
        if (body.Length == 0)
        {
            throw new FormatException("The model returned an empty page.");
        }

        draft.Body = Cut(body, MaxBody);
        draft.Expanded = true;
    }

    /// <summary>
    /// Fills a homebrew draft from the expand reply: <c>{"description", "tags", "properties": {field key: value}}</c>.
    /// Values are coerced to their field's type and, through <paramref name="canonical"/> (field key, value written), back to
    /// the stored value of a translated option; unknown keys and values that do not fit are dropped.
    /// </summary>
    public static void ApplyHomebrew(WorldDraft draft, string text, IReadOnlyList<FieldSpec> fields, Func<string, string, string>? canonical = null)
    {
        using var doc = FieldCoercion.ParseObject(text);
        var root = doc.RootElement;
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

        draft.Body = FieldCoercion.String(root, "description") is { } description ? Cut(description, MaxBody) : draft.Body;
        draft.Tags = root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList()
            : new List<string>();
        draft.Properties = properties;
        draft.Expanded = true;
    }

    /// <summary>
    /// The text to save for <paramref name="text"/>: a <c>[[link]]</c> to another draft (by its ref or its outline name) is
    /// rewritten to the draft's final name when that draft is saved as a wiki page (a page, a person or a place), and left as plain text
    /// otherwise (a homebrew entry, a secret or an unticked draft has no page to link to). Other links are kept, unless <paramref name="keepLinks"/> is false
    /// (homebrew text, which is not wiki text), when every link becomes plain text.
    /// </summary>
    public static string ResolveLinks(string text, WorldBundle bundle, bool keepLinks = true)
    {
        return WikiLink.Replace(text, match =>
        {
            var target = match.Groups[1].Value.Trim();
            var shown = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            // The ref wins, then the name the outline gave, then the DM's rename: renaming one draft to another's outline name
            // must not steal the links meant for the other.
            var draft = bundle.Drafts.FirstOrDefault(d => string.Equals(d.Ref, target, StringComparison.OrdinalIgnoreCase))
                        ?? bundle.Drafts.FirstOrDefault(d => string.Equals(d.OutlineName, target, StringComparison.OrdinalIgnoreCase))
                        ?? bundle.Drafts.FirstOrDefault(d => string.Equals(d.Name.Trim(), target, StringComparison.OrdinalIgnoreCase));
            if (draft == null)
            {
                return keepLinks ? match.Value : shown ?? target;
            }

            // A draft whose name was cleared is not saved, so it is named as the outline named it, unlinked.
            var name = string.IsNullOrWhiteSpace(draft.Name) ? draft.OutlineName : draft.Name.Trim();
            var linked = keepLinks && draft.Selected && !string.IsNullOrWhiteSpace(draft.Name) && DraftKinds.IsPage(draft.Kind);
            var title = draft.PageTitle ?? name;
            return linked ? (shown == null ? $"[[{title}]]" : $"[[{title}|{shown}]]") : shown ?? name;
        });
    }

    private static string Cut(string? text, int max) => text == null ? string.Empty : text.Length <= max ? text : text[..max].TrimEnd();

    private static readonly System.Text.RegularExpressions.Regex WikiLink = new(@"\[\[([^\[\]|]+?)(?:\|([^\[\]]+?))?\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);
}
