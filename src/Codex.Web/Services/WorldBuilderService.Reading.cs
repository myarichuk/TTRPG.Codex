using System.Globalization;
using System.Text;
using System.Text.Json;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

// The world builder's reading: what the world-building chat's model is told and what its tools read about the campaign.
public sealed partial class WorldBuilderService
{
    /// <summary>The campaign as the chat's system prompt describes it.</summary>
    public async Task<WorldChatRequest> ChatRequestAsync(CampaignDocument campaign, CampaignAccess access)
    {
        DmOnly(access);
        var system = catalog.GetDefinition(campaign.SystemId);
        var types = await HomebrewTypesAsync(campaign, access, system);
        return new WorldChatRequest(
            catalog.GetDisplayName(campaign.SystemId),
            AiPrompts.WithSetting(CampaignContext(campaign), system?.Canonical.Setting),
            types.Select(t => $"{t.Type} ({PromptLabel(t.Type, t)}{(t.ActorBlueprint ? ", a " + (system?.Canonical.Ai.Creature ?? "creature") : "")})").ToList(),
            Lang,
            system?.Canonical.Ai.Creature ?? "creature",
            system?.Canonical.Ai.CreatureExamples,
            await OutlinesAsync(access));
    }

    // The DM's template for each kind of page the builder writes, as the model is shown it.
    private async Task<IReadOnlyDictionary<string, string>?> OutlinesAsync(CampaignAccess access)
    {
        if (templates == null)
        {
            return null;
        }

        var all = await templates.GetAsync(access, new Localizer());
        var outlines = new Dictionary<string, string>();
        foreach (var kind in new[] { WikiPageKind.Person, WikiPageKind.Place, WikiPageKind.Faction, WikiPageKind.Quest })
        {
            if (TemplateSet.ForKind(all, kind) is { } template && !string.IsNullOrWhiteSpace(template.Body))
            {
                outlines[kind.ToString().ToLowerInvariant()] = LiveTemplate.Sample(template.Body);
            }
        }

        return outlines;
    }

    /// <summary>Pages, places and cast members whose name (or a page's text) holds <paramref name="query"/>; with no query, an
    /// overview of what the campaign has. One line each, with the key a proposal uses to point at it.</summary>
    public async Task<string> SearchWorldAsync(CampaignDocument campaign, CampaignAccess access, string? query)
    {
        DmOnly(access);
        var q = query?.Trim() ?? string.Empty;
        bool Hit(string? text) => q.Length == 0 || (text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var allRegions = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var actors = (await actorRepository.GetVisibleForCampaignAsync(access)).ToList();
        var names = Names(actors, allRegions);
        var lines = new List<string>();
        lines.AddRange(pages.Where(p => Hit(p.Title) || Hit(p.Body)).Take(ContextCap)
            .Select(p => $"page existing:{p.Slug} - {p.Title} ({p.Kind.ToString().ToLowerInvariant()}): {TextFormatting.Summary(Plain(p.Body, names), 140)}"));
        lines.AddRange(allRegions.Where(r => Hit(r.Name)).Take(ContextCap)
            .Select(r => $"region existing:{WorldBundle.RegionPrefix}{r.Id} - {r.Name} ({r.Kind.ToString().ToLowerInvariant()}, {r.Locations.Count} places)"));
        lines.AddRange((await facts.GetVisibleForCampaignAsync(access, new HashSet<string>())).Where(f => Hit(f.Summary) || Hit(f.Details)).Take(ContextCap)
            .Select(f => $"secret existing:{WorldBundle.FactPrefix}{f.Id} - {f.Summary}"));
        foreach (var region in allRegions)
        {
            lines.AddRange(region.Locations.Where(l => Hit(l.Name)).Take(ContextCap)
                .Select(l => $"place existing:{WorldBundle.PlacePrefix}{l.Id} - {l.Name} ({KindName(l.Kind)}) in {Path(region, l)}"));
        }

        lines.AddRange(actors.Where(a => a.Kind != ActorKind.PlayerCharacter && Hit(a.Name)).Take(ContextCap)
            .Select(a => $"cast existing:{WorldBundle.ActorPrefix}{a.Id} - {a.Name} ({(a.Kind == ActorKind.Monster ? "monster" : "npc")}){(PageOf(a, pages) is { } page ? $", page existing:{page.Slug}" : "")}"));
        lines.AddRange(actors.Where(a => a.Kind == ActorKind.PlayerCharacter && Hit(a.Name)).Take(12).Select(a => $"player character - {a.Name}"));

        if (lines.Count == 0)
        {
            return q.Length == 0 ? "The campaign has no pages, places or cast members yet." : $"Nothing in the campaign mentions \"{q}\".";
        }

        return string.Join('\n', lines);
    }

    /// <summary>What the ticked entries' text links to by name that neither the proposal nor the campaign has (a page's title or slug,
    /// a place, a cast member): things the model mentioned and did not write. The DM can ask for them one by one.</summary>
    public async Task<IReadOnlyList<MissingMention>> MissingMentionsAsync(CampaignAccess access, WorldBundle bundle)
    {
        DmOnly(access);
        var known = new List<string>();
        foreach (var page in await wiki.GetVisibleForCampaignAsync(access))
        {
            known.Add(page.Title);
            known.Add(page.Slug);
        }

        var allRegions = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        known.AddRange(allRegions.Select(r => r.Name));
        known.AddRange(allRegions.SelectMany(r => r.Locations).Select(l => l.Name));
        known.AddRange((await actorRepository.GetVisibleForCampaignAsync(access)).Select(a => a.Name));
        return WorldBundleParser.MissingMentions(bundle, known);
    }

    /// <summary>One page's full text (mentions as names), by its slug or title; null when there is no such page.</summary>
    public async Task<string?> PageAsync(CampaignAccess access, string slugOrTitle)
    {
        DmOnly(access);
        var key = slugOrTitle.Trim();
        if (key.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // A place's description and a cast member's history are texts of their own, which an edit can replace.
            switch (WorldBundle.ParseTarget(key, out var targetId))
            {
                case EditTarget.Place: return await PlaceTextAsync(targetId, access);
                case EditTarget.Actor: return await ActorTextAsync(targetId, access);
                case EditTarget.Fact: return await FactTextAsync(targetId, access);
                case EditTarget.Region: return await RegionTextAsync(targetId, access);
            }

            key = key[WorldBundle.ExistingPrefix.Length..];
        }

        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var page = pages.FirstOrDefault(p => string.Equals(p.Slug, key, StringComparison.OrdinalIgnoreCase))
                   ?? pages.FirstOrDefault(p => string.Equals(p.Slug, WikiText.Slugify(key), StringComparison.OrdinalIgnoreCase));
        if (page == null) return null;

        var names = Names((await actorRepository.GetVisibleForCampaignAsync(access)).ToList(), (await regions.GetVisibleForCampaignAsync(access)).ToList());
        var text = Cap(Editable(page.Body, names));
        return $"# {page.Title}\nkey: existing:{page.Slug}; kind: {page.Kind.ToString().ToLowerInvariant()}; {(page.Visibility == WikiVisibility.Party ? "shared with the party" : "GM only")}\n\n{text}";
    }

    private async Task<string?> PlaceTextAsync(string id, CampaignAccess access)
    {
        var allRegions = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        foreach (var region in allRegions)
        {
            if (region.Locations.FirstOrDefault(p => p.Id == id) is not { } place) continue;
            var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
            var names = Names((await actorRepository.GetVisibleForCampaignAsync(access)).ToList(), allRegions);
            var page = string.IsNullOrEmpty(place.WikiPageId) ? null : pages.FirstOrDefault(p => p.Id == place.WikiPageId);
            return $"# {place.Name}\nkey: existing:{WorldBundle.PlacePrefix}{place.Id}; place in {Path(region, place)}; " +
                   $"this is the place's short description, which players read once the place is revealed ({(place.Visibility == ActorVisibility.Known ? "revealed: players can read it now" : "hidden from players for now")})" +
                   $"{(page != null ? $"; its page, which may hold what the place hides: existing:{page.Slug}" : "")}\n\n{Cap(Editable(place.Description, names))}";
        }

        return null;
    }

    private async Task<string?> FactTextAsync(string id, CampaignAccess access)
    {
        if ((await facts.GetVisibleForCampaignAsync(access, new HashSet<string>())).FirstOrDefault(f => f.Id == id) is not { } fact) return null;
        var names = Names((await actorRepository.GetVisibleForCampaignAsync(access)).ToList(), (await regions.GetVisibleForCampaignAsync(access)).ToList());
        return $"# {fact.Summary}\nkey: existing:{WorldBundle.FactPrefix}{fact.Id}; a secret or rumour in Lore, {(fact.Visibility == FactVisibility.DmOnly ? "known only to the game master" : "already known to players")}; " +
               $"the title above is the fact itself and the text below is its details\n\n{Cap(Editable(fact.Details, names))}";
    }

    private async Task<string?> RegionTextAsync(string id, CampaignAccess access)
    {
        if ((await regions.GetVisibleForCampaignAsync(access)).FirstOrDefault(r => r.Id == id) is not { } region) return null;
        return $"# {region.Name}\nkey: existing:{WorldBundle.RegionPrefix}{region.Id}; a region ({region.Kind.ToString().ToLowerInvariant()}) holding {region.Locations.Count} place(s); " +
               "it has no text: it can be renamed, changed to another regionKind, or deleted with every place in it. list_places shows its places.";
    }

    private async Task<string?> ActorTextAsync(string id, CampaignAccess access)
    {
        if (await actorRepository.GetVisibleAsync(id, access) is not { } actor) return null;
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var names = Names((await actorRepository.GetVisibleForCampaignAsync(access)).ToList(), (await regions.GetVisibleForCampaignAsync(access)).ToList());
        var page = PageOf(actor, pages);
        var text = Cap(Editable(actor.Biography, names));
        return $"# {actor.Name}\nkey: existing:{WorldBundle.ActorPrefix}{actor.Id}; this is the character's history, which players read once the character is revealed " +
               $"({(actor.Visibility == ActorVisibility.Known ? "revealed: players can read it now" : "hidden from players for now")})" +
               $"{(page != null ? $"; their page: existing:{page.Slug}" : "")}\n\n{text}";
    }

    /// <summary>Every region and the places in it as a tree, with the key a proposal uses as a place's parent.</summary>
    public async Task<string> PlacesAsync(CampaignAccess access)
    {
        DmOnly(access);
        var sb = new StringBuilder();
        foreach (var region in (await regions.GetVisibleForCampaignAsync(access)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine((region.Kind == RegionKind.General ? $"Region: {region.Name}" : $"Region: {region.Name} ({region.Kind.ToString().ToLowerInvariant()})") + $" key: existing:{WorldBundle.RegionPrefix}{region.Id}");
            foreach (var (place, depth) in PlaceTree.Ordered(region.Locations))
            {
                sb.AppendLine($"{new string(' ', 2 + depth * 2)}- {place.Name} ({KindName(place.Kind)}{FeatureNotes(place.Feature)}) key: existing:{WorldBundle.PlacePrefix}{place.Id}");
            }
        }

        return sb.Length == 0 ? "The campaign has no places yet. New top-level places go into a region named by the proposal's \"region\"." : sb.ToString().TrimEnd();
    }


    // "; locked: DC 15; creature: a mimic": what a feature hides, so the model neither repeats nor contradicts it.
    private static string FeatureNotes(PlaceFeature? feature)
    {
        if (feature == null) return string.Empty;
        var notes = new List<string>();
        if (feature.Container) notes.Add("container");
        if (feature.Lock is { Length: > 0 } locked) notes.Add($"lock: {locked}");
        if (feature.Trap is { Length: > 0 } trap) notes.Add($"trap: {trap}");
        if (feature.Concealment is { Length: > 0 } hidden) notes.Add($"hidden: {hidden}");
        if (feature.Creature is { Length: > 0 } creature) notes.Add($"creature: {creature}");
        if (feature.Contents is { Length: > 0 } contents) notes.Add($"contents: {TextFormatting.Summary(contents, 120)}");
        return notes.Count == 0 ? string.Empty : "; " + string.Join("; ", notes);
    }

    /// <summary>A Library search, for a person's stats or for what exists already: "type/id: Name" lines. Without a type it
    /// searches the system's creatures.</summary>
    public async Task<string> SearchLibraryAsync(CampaignDocument campaign, CampaignAccess access, string? query, string? type)
    {
        DmOnly(access);
        var system = catalog.GetDefinition(campaign.SystemId);
        var searched = string.IsNullOrWhiteSpace(type) ? CreatureTypes(system) : new List<string> { type.Trim() };
        var lines = new List<string>();
        foreach (var t in searched)
        {
            lines.AddRange((await library.SearchAsync(campaign, access, query?.Trim() ?? string.Empty, t, limit: 20))
                .Select(i => $"{i.Entry.Type}/{i.Entry.Id}: {i.Entry.Name}" + (i.Homebrew is { } brew
                    ? $" (homebrew, key existing:{WorldBundle.HomebrewPrefix}{brew.Type}/{brew.EntryId}{(brew.Tags.Count > 0 ? "; tags: " + string.Join(", ", brew.Tags) : "")})"
                    : "")));
        }

        return lines.Count == 0 ? "No Library entry matches." : string.Join('\n', lines.Take(ContextCap));
    }

    /// <summary>One Library entry in full, by <c>type/id</c> or a homebrew entry's key: where it comes from, its tags, every field
    /// with its value (as JSON) and its rules text. What a variant starts from and what an edit of a homebrew entry changes.</summary>
    public async Task<string> EntryAsync(CampaignDocument campaign, CampaignAccess access, string key)
    {
        DmOnly(access);
        var text = key.Trim();
        var homebrewKey = text.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix, StringComparison.OrdinalIgnoreCase);
        if (homebrewKey) text = text[(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix).Length..];
        if (text.Split('/') is not [var type, var id] || type.Length == 0 || id.Length == 0)
        {
            return $"\"{key}\" is not an entry key; give type/id as search_library lists it (\"spell/fireball\").";
        }

        var item = homebrewKey ? await library.GetAsync(campaign, access, type, id, isHomebrew: true) : await library.GetAsync(campaign, access, type, id);
        if (item == null) return $"There is no Library entry {type}/{id}. Use search_library to find one.";

        var sb = new StringBuilder($"# {item.Entry.Name}\n");
        sb.AppendLine(item.Homebrew is { } brew
            ? $"type: {brew.Type}; homebrew of this campaign, key existing:{WorldBundle.HomebrewPrefix}{brew.Type}/{brew.EntryId} (an edit can change it); {(brew.IsPublished ? "published: players can read it" : "a draft, hidden from players")}"
            : $"type: {item.Entry.Type}; from the pack \"{item.SourceName}\", which cannot be changed: make a homebrew variant with \"basedOn\": \"{item.Entry.Type}/{item.Entry.Id}\"");
        if (item.Entry.Tags.Count > 0) sb.AppendLine("tags: " + string.Join(", ", item.Entry.Tags));
        if (item.Entry.Properties.Count > 0)
        {
            sb.AppendLine("properties:");
            foreach (var (name, value) in item.Entry.Properties) sb.AppendLine($"- {name}: {PropertyJson(value)}");
        }

        var description = item.Entry.Description ?? string.Empty;
        sb.AppendLine().Append(description.Length > MaxPageText ? description[..MaxPageText] + "\n[...]" : description);
        return sb.ToString().TrimEnd();
    }

    /// <summary>A field's value as the model reads it: JSON, whatever shape the database or a pack gave it.</summary>
    internal static string PropertyJson(object? value)
    {
        static System.Text.Json.Nodes.JsonNode? Node(object? v) => v switch
        {
            null => null,
            string text => System.Text.Json.Nodes.JsonValue.Create(text),
            bool b => System.Text.Json.Nodes.JsonValue.Create(b),
            long or int or short or byte => System.Text.Json.Nodes.JsonValue.Create(Convert.ToInt64(v, CultureInfo.InvariantCulture)),
            double or float or decimal => System.Text.Json.Nodes.JsonValue.Create(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
            JsonElement element => System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText()),
            System.Collections.IDictionary map => new System.Text.Json.Nodes.JsonObject(map.Keys.Cast<object>().Select(k => KeyValuePair.Create(k.ToString() ?? string.Empty, Node(map[k])))),
            System.Collections.IEnumerable list => new System.Text.Json.Nodes.JsonArray(list.Cast<object?>().Select(Node).ToArray()),
            _ => System.Text.Json.Nodes.JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture))
        };

        return Node(value)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }

    /// <summary>The tags the campaign's wiki pages and homebrew entries use, most used first, so new ones match them.</summary>
    public async Task<string> TagsAsync(CampaignAccess access)
    {
        DmOnly(access);
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).SelectMany(p => p.Tags).ToList();
        var brews = (await homebrew.GetVisibleForCampaignAsync(access)).SelectMany(h => h.Tags).ToList();
        var tags = pages.Select(t => (Tag: t, Page: true)).Concat(brews.Select(t => (Tag: t, Page: false)))
            .GroupBy(t => t.Tag.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.First().Tag.Trim(), Pages: g.Count(t => t.Page), Homebrew: g.Count(t => !t.Page)))
            .OrderByDescending(t => t.Pages + t.Homebrew).ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(ContextCap * 2)
            .ToList();
        return tags.Count == 0
            ? "Nothing in the campaign is tagged yet."
            : string.Join('\n', tags.Select(t => $"{t.Tag}: {(t.Pages > 0 ? $"{t.Pages} page(s)" : "")}{(t.Pages > 0 && t.Homebrew > 0 ? ", " : "")}{(t.Homebrew > 0 ? $"{t.Homebrew} homebrew" : "")}"));
    }

    /// <summary>The fields a homebrew entry of <paramref name="type"/> fills, as "key: label, type, options; help" lines.</summary>
    public async Task<string> HomebrewFieldsAsync(CampaignDocument campaign, CampaignAccess access, string type)
    {
        DmOnly(access);
        var system = catalog.GetDefinition(campaign.SystemId);
        var key = TypeSpellings(await HomebrewTypesAsync(campaign, access, system), campaign.SystemId).GetValueOrDefault(type.Trim());
        if (key == null) return $"\"{type}\" is not a homebrew type.";
        var definition = system?.Canonical.FindType(key) ?? new ContentTypeDefinition { Type = key, Label = CampaignLibrary.Humanize(key) };
        var sb = new StringBuilder($"Fields of a {PromptLabel(key, definition)} (type \"{key}\"):\n");
        foreach (var f in await FieldSpecsAsync(campaign, access, system, definition))
        {
            var detail = f.Options is { Count: > 0 } ? ", options: " + string.Join(" | ", f.Options)
                : f.Min.HasValue || f.Max.HasValue ? $", {f.Min?.ToString() ?? "?"}-{f.Max?.ToString() ?? "?"}"
                : "";
            var hint = string.IsNullOrWhiteSpace(f.Hint) ? "" : "; " + f.Hint.Trim();
            sb.AppendLine($"- {f.Key}: {f.Label}, {f.Type}{detail}{hint}");
        }

        // Where the system reads features or rolls off this type, say how an entry states what it does on the sheet.
        if (system != null && Codex.Core.Rules.EntryGuide.For(system.Canonical, key) is { } grants) sb.AppendLine().AppendLine(grants);

        return sb.ToString().TrimEnd();
    }
}
