using System.Text.Json;
using Codex.Core.AI;

namespace Codex.Core.WorldBuilder;

/// <summary>
/// One thing the model proposed: a homebrew entry or a wiki page. The outline call fills the name, pitch and links;
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

    /// <summary>A wiki page's text, or a homebrew entry's description (its rules text).</summary>
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
    Unnamed
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
            var name = FieldCoercion.String(element, "name") ?? string.Empty;
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
                Pitch = FieldCoercion.String(element, "pitch") ?? string.Empty
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

        draft.Body = body;
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

        draft.Body = FieldCoercion.String(root, "description") ?? draft.Body;
        draft.Tags = root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList()
            : new List<string>();
        draft.Properties = properties;
        draft.Expanded = true;
    }

    /// <summary>
    /// The text to save for <paramref name="text"/>: a <c>[[link]]</c> to another draft (by its ref or its outline name) is
    /// rewritten to the draft's final name when that draft is saved as a wiki page, and left as plain text otherwise (a homebrew
    /// entry or an unticked draft has no page to link to). Other links are kept, unless <paramref name="keepLinks"/> is false
    /// (homebrew text, which is not wiki text), when every link becomes plain text.
    /// </summary>
    public static string ResolveLinks(string text, WorldBundle bundle, bool keepLinks = true)
    {
        return WikiLink.Replace(text, match =>
        {
            var target = match.Groups[1].Value.Trim();
            var shown = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            var draft = bundle.Drafts.FirstOrDefault(d => string.Equals(d.Ref, target, StringComparison.OrdinalIgnoreCase)
                                                          || string.Equals(d.OutlineName, target, StringComparison.OrdinalIgnoreCase)
                                                          || string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase));
            if (draft == null)
            {
                return keepLinks ? match.Value : shown ?? target;
            }

            var linked = keepLinks && draft.Selected && draft.Kind == DraftKinds.Wiki;
            return linked ? (shown == null ? $"[[{draft.Name}]]" : $"[[{draft.Name}|{shown}]]") : shown ?? draft.Name;
        });
    }

    private static readonly System.Text.RegularExpressions.Regex WikiLink = new(@"\[\[([^\[\]|]+?)(?:\|([^\[\]]+?))?\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);
}
