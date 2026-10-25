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

    /// <summary>
    /// Parses an outline reply: <c>{"drafts": [{"ref", "kind", "type", "name", "pitch", "links"}]}</c>. Drafts of a kind
    /// or type the rules do not allow are dropped (and listed in <see cref="WorldBundle.Dropped"/>); missing or repeated refs
    /// are renumbered; links to anything that is neither another draft nor a listed existing key are removed.
    /// </summary>
    public static WorldBundle ParseOutline(string text, BundleRules rules)
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

            var draft = new WorldDraft
            {
                Ref = $"d{bundle.Drafts.Count + 1}",
                Kind = kind,
                Type = type,
                Name = name,
                OutlineName = name,
                Pitch = Cut(FieldCoercion.String(element, "pitch"), MaxPitch)
            };
            if (FieldCoercion.String(element, "ref") is { } given)
            {
                renamed.TryAdd(given, draft.Ref);
            }

            rawLinks[draft] = element.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
                ? links.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!.Trim()).ToList()
                : new List<string>();
            bundle.Drafts.Add(draft);
        }

        var existing = rules.Existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in bundle.Drafts)
        {
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

                if (resolved != null && !draft.Links.Contains(resolved))
                {
                    draft.Links.Add(resolved);
                }
            }
        }

        return bundle;
    }

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
