using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>What saving a world-building proposal stored.</summary>
public sealed record WorldBuildResult(
    IReadOnlyList<HomebrewEntryDocument> Entries,
    IReadOnlyList<WikiPageDocument> Pages,
    IReadOnlyList<ActorDocument> Actors,
    IReadOnlyList<FactDocument> Facts,
    IReadOnlyList<LocationDocument> Places,
    IReadOnlyList<BestiaryEntry> Shortlisted,
    IReadOnlyList<WorldEdit>? Edits = null,
    int Retagged = 0,
    int Picked = 0)
{
    /// <summary>The existing entries whose text the proposal replaced.</summary>
    public IReadOnlyList<WorldEdit> Changes => Edits ?? Array.Empty<WorldEdit>();

    /// <summary>Entries saved: <see cref="Retagged"/> counts the pages and homebrew entries whose tags changed, <see cref="Picked"/>
    /// the Library creatures put on the Bestiary by an entry of their own (a new homebrew creature put there is one entry, not two).</summary>
    public int Count => Entries.Count + Pages.Count + Actors.Count + Facts.Count + Places.Count + Changes.Count + Retagged + Picked;
}

/// <summary>An existing entry a proposal changed: what kind of text, its id (a page's slug), and its name.</summary>
public sealed record WorldEdit(EditTarget Target, string Id, string Name);

/// <summary>
/// The world builder (docs/WORLD_BUILDER.md): what the world-building chat's tools read about the campaign, the check of a
/// proposal, and the save of the entries the DM keeps. The model only proposes; every write goes through the repositories with
/// the DM's access, and what it saves starts hidden from players (homebrew unpublished; wiki pages, secrets, cast members and
/// places DM-only). DM only.
/// </summary>
public sealed class WorldBuilderService(ISystemCatalog catalog, CampaignLibrary library, IHomebrewRepository homebrew, IWikiRepository wiki,
    IFactRepository facts, CastService cast, IRegionRepository regions, ICampaignRepository campaigns, IActorRepository actorRepository, AiFeatures ai)
{
    /// <summary>The language the model writes in: the one the DM reads the app in.</summary>
    private static string Lang => SupportedCultures.Normalize(CultureInfo.CurrentUICulture.Name);

    // Most lines a list tool returns, and most characters of a page get_page returns: enough to work with, not a whole wiki.
    private const int ContextCap = 40;
    private const int MaxPageText = 6000;

    // ---- What the chat's model is told and can read ----

    /// <summary>The campaign as the chat's system prompt describes it.</summary>
    public async Task<WorldChatRequest> ChatRequestAsync(CampaignDocument campaign, CampaignAccess access)
    {
        DmOnly(access);
        var system = catalog.GetDefinition(campaign.SystemId);
        var types = await HomebrewTypesAsync(campaign, access, system);
        return new WorldChatRequest(
            catalog.GetDisplayName(campaign.SystemId),
            CampaignContext(campaign),
            types.Select(t => $"{t.Type} ({PromptLabel(t.Type, t)}{(t.ActorBlueprint ? ", a " + (system?.Canonical.Ai.Creature ?? "creature") : "")})").ToList(),
            Lang,
            system?.Canonical.Ai.Creature ?? "creature",
            system?.Canonical.Ai.CreatureExamples);
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
            }

            key = key[WorldBundle.ExistingPrefix.Length..];
        }

        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var page = pages.FirstOrDefault(p => string.Equals(p.Slug, key, StringComparison.OrdinalIgnoreCase))
                   ?? pages.FirstOrDefault(p => string.Equals(p.Slug, WikiText.Slugify(key), StringComparison.OrdinalIgnoreCase));
        if (page == null) return null;

        var names = Names((await actorRepository.GetVisibleForCampaignAsync(access)).ToList(), (await regions.GetVisibleForCampaignAsync(access)).ToList());
        var text = Editable(page.Body, names);
        if (text.Length > MaxPageText) text = text[..MaxPageText] + "\n[...] (too long to show whole: it cannot be replaced by an edit)";
        return $"# {page.Title}\nkey: existing:{page.Slug}; kind: {page.Kind.ToString().ToLowerInvariant()}; {(page.Visibility == WikiVisibility.Party ? "shared with the party" : "DM only")}\n\n{text}";
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
            sb.AppendLine($"Region: {region.Name}");
            foreach (var (place, depth) in PlaceTree.Ordered(region.Locations))
            {
                sb.AppendLine($"{new string(' ', 2 + depth * 2)}- {place.Name} ({KindName(place.Kind)}) key: existing:{WorldBundle.PlacePrefix}{place.Id}");
            }
        }

        return sb.Length == 0 ? "The campaign has no places yet. New top-level places go into a region named by the proposal's \"region\"." : sb.ToString().TrimEnd();
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

        return sb.ToString().TrimEnd();
    }

    /// <summary>Checks a proposal against what the campaign has, and finds each person's stats in the Library. Nothing is saved.</summary>
    public async Task<ProposalCheck> CheckAsync(CampaignDocument campaign, CampaignAccess access, string json)
    {
        DmOnly(access);
        var system = catalog.GetDefinition(campaign.SystemId);
        var types = await HomebrewTypesAsync(campaign, access, system);
        var spellings = TypeSpellings(types, campaign.SystemId);

        // The parser is pure, so the fields of every type the proposal names are looked up first.
        var fields = new Dictionary<string, IReadOnlyList<FieldSpec>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TypeField.Matches(json).Concat(HomebrewKey.Matches(json)))
        {
            if (spellings.GetValueOrDefault(match.Groups[1].Value) is { } key && !fields.ContainsKey(key))
            {
                var definition = system?.Canonical.FindType(key) ?? new ContentTypeDefinition { Type = key };
                fields[key] = await FieldSpecsAsync(campaign, access, system, definition);
            }
        }

        var pages = (await wiki.GetVisibleForCampaignAsync(access)).GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var places = (await regions.GetVisibleForCampaignAsync(access)).SelectMany(r => r.Locations).Select(l => l.Id).ToList();
        var castIds = (await actorRepository.GetVisibleForCampaignAsync(access)).Where(a => a.Kind != ActorKind.PlayerCharacter).Select(a => a.Id).ToList();
        var brews = (await homebrew.GetVisibleForCampaignAsync(access)).ToList();
        var brewKeys = brews.GroupBy(h => $"{h.Type}/{h.EntryId}", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var culture = Lang;
        var rules = new ProposalRules(spellings, pages, places, key => fields.GetValueOrDefault(key) ?? Array.Empty<FieldSpec>(),
            system == null ? null : (key, value) => system.CanonicalOption(culture, key, value), castIds, brewKeys);
        var check = WorldBundleParser.ParseProposal(json, rules);

        await FindStatsAsync(campaign, access, check.Bundle);
        var editErrors = await FillEditsAsync(check.Bundle, access);
        editErrors.AddRange(await VariantsAsync(campaign, access, check.Bundle));
        editErrors.AddRange(await PicksAsync(campaign, access, check.Bundle));
        FillTagged(check.Bundle, (await wiki.GetVisibleForCampaignAsync(access)).ToList(), brews);
        var warnings = check.Warnings.ToList();
        foreach (var draft in check.Bundle.Drafts.Where(d => d is { Kind: DraftKinds.Npc, Stats: null } && d.SuggestedStats != null))
        {
            warnings.Add($"{draft.Ref} (npc '{draft.Name}'): stats '{draft.SuggestedStats}' are not in the Library, so the sheet starts blank; use a name search_library finds.");
        }

        return check with { Warnings = warnings, Errors = check.Errors.Concat(editErrors).ToList() };
    }

    /// <summary>Names each edit after what it changes and keeps that text as it is now, for the review card to show beside the new
    /// one, and notes when players can already read it (applying the edit changes what they see at once).</summary>
    private async Task<List<string>> FillEditsAsync(WorldBundle bundle, CampaignAccess access)
    {
        var errors = new List<string>();
        var edits = bundle.Drafts.Where(d => d.Kind == DraftKinds.Edit && d.Target != null).ToList();
        if (edits.Count == 0) return errors;

        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var allRegions = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var actors = (await actorRepository.GetVisibleForCampaignAsync(access)).ToList();
        var brews = edits.Any(d => WorldBundle.ParseTarget(d.Target!, out _) == EditTarget.Homebrew) ? (await homebrew.GetVisibleForCampaignAsync(access)).ToList() : new List<HomebrewEntryDocument>();
        var names = Names(actors, allRegions);
        foreach (var draft in edits)
        {
            switch (WorldBundle.ParseTarget(draft.Target!, out var id))
            {
                case EditTarget.Homebrew:
                    if (brews.FirstOrDefault(h => string.Equals($"{h.Type}/{h.EntryId}", id, StringComparison.OrdinalIgnoreCase)) is { } brew)
                    {
                        draft.Name = brew.Name;
                        draft.Before = brew.Description;
                        draft.BeforeProperties = new Dictionary<string, object>(brew.Properties);
                        draft.BeforeTags = new List<string>(brew.Tags);
                        draft.BeforeStamp = HomebrewStamp(brew);
                        draft.SharedWithPlayers = brew.IsPublished;
                        draft.Type = brew.Type;
                        // No new text: the review shows the text it keeps, not an empty one.
                        if (draft.Body == null) draft.Body = brew.Description;
                    }

                    break;
                case EditTarget.Place:
                    if (allRegions.SelectMany(r => r.Locations).FirstOrDefault(p => p.Id == id) is { } place)
                    {
                        draft.Name = place.Name;
                        draft.Before = Plain(place.Description, names);
                        draft.BeforeStamp = Stamp(place.Description);
                        draft.SharedWithPlayers = place.Visibility == ActorVisibility.Known;
                        TooLong(draft, place.Description);
                    }

                    break;
                case EditTarget.Actor:
                    if (actors.FirstOrDefault(a => a.Id == id) is { } actor)
                    {
                        draft.Name = actor.Name;
                        draft.Before = Plain(actor.Biography, names);
                        draft.BeforeStamp = Stamp(actor.Biography);
                        draft.SharedWithPlayers = actor.Visibility == ActorVisibility.Known;
                        TooLong(draft, actor.Biography);
                    }

                    break;
                default:
                    if (pages.FirstOrDefault(p => string.Equals(p.Slug, id, StringComparison.OrdinalIgnoreCase)) is { } page)
                    {
                        draft.Name = page.Title;
                        draft.Before = Plain(page.Body, names);
                        draft.BeforeStamp = Stamp(page.Body);
                        draft.SharedWithPlayers = page.Visibility == WikiVisibility.Party;
                        TooLong(draft, page.Body);
                    }

                    break;
            }
        }

        return errors;

        // get_page shows only the first MaxPageText characters, so a longer text was never fully read, and "the complete new text"
        // written from it would cut the rest off.
        void TooLong(WorldDraft draft, string? stored)
        {
            if ((stored?.Length ?? 0) > MaxPageText)
            {
                errors.Add($"{draft.Ref} (edit '{draft.Name}'): the current text is {stored!.Length} characters, more than get_page shows ({MaxPageText}), " +
                           "so it cannot be replaced as a whole without losing the end; put the new material in a new entry instead.");
            }
        }
    }

    private static readonly Regex TypeField = new("\"type\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    // An edit of a homebrew entry fills the fields of the entry's type, named in its key.
    private static readonly Regex HomebrewKey = new("existing:homebrew:([^/\"]+)/", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Starts each variant from the Library entry it is based on: its fields, with the ones the model gave on top, and its
    /// text and tags unless the model wrote its own. The DM reviews the whole entry, not only what differs.</summary>
    private async Task<List<string>> VariantsAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle)
    {
        var errors = new List<string>();
        foreach (var draft in bundle.Drafts.Where(d => d is { Kind: DraftKinds.Homebrew, BasedOn: not null }))
        {
            var key = draft.BasedOn!;
            var homebrewKey = key.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix, StringComparison.OrdinalIgnoreCase);
            if (homebrewKey) key = key[(WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix).Length..];
            var item = key.Split('/') is [var type, var id]
                ? homebrewKey ? await library.GetAsync(campaign, access, type, id, isHomebrew: true) : await library.GetAsync(campaign, access, type, id)
                : null;
            if (item == null)
            {
                errors.Add($"{draft.Ref} (homebrew '{draft.Name}'): basedOn '{draft.BasedOn}' is not a Library entry; use type/id as search_library lists it.");
                continue;
            }

            if (!string.Equals(item.Entry.Type, draft.Type, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{draft.Ref} (homebrew '{draft.Name}'): basedOn '{draft.BasedOn}' is a {item.Entry.Type}, but the entry is a {draft.Type}; a variant has its base's type.");
                continue;
            }

            var properties = item.Entry.Properties.ToDictionary(p => p.Key, p => p.Value);
            foreach (var (name, value) in draft.Properties) properties[name] = value;
            draft.Properties = properties;
            draft.Body ??= item.Entry.Description;
            if (draft.Tags.Count == 0) draft.Tags = new List<string>(item.Entry.Tags);
            if (string.IsNullOrWhiteSpace(draft.Pitch)) draft.Pitch = TextFormatting.Summary(draft.Body ?? string.Empty, 300);
            draft.BasedOn = $"{item.Entry.Type}/{item.Entry.Id}";
            draft.BasedOnName = item.IsHomebrew ? item.Entry.Name : (await library.GetAsync(campaign, access, item.Entry.Type, item.Entry.Id, Lang))?.Entry.Name ?? item.Entry.Name;
            draft.ImportedFrom = item.IsHomebrew ? item.Homebrew!.ImportedFrom : item.SourceName;
        }

        return errors;
    }

    /// <summary>Finds the creature each Bestiary pick names in the Library. Unlike a person's stats, a pick that names nothing is an
    /// error: it is the whole entry.</summary>
    private async Task<List<string>> PicksAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle)
    {
        var errors = new List<string>();
        var system = catalog.GetDefinition(campaign.SystemId);
        foreach (var draft in bundle.Drafts.Where(d => d.Kind == DraftKinds.Bestiary))
        {
            draft.Stats = null;
            await FindStatsAsync(campaign, access, bundle, draft);
            if (draft.Stats is not { } creature)
            {
                errors.Add($"{draft.Ref} (bestiary '{draft.Name}'): '{draft.SuggestedStats}' is not a creature in the Library; use type/id as search_library lists it.");
                continue;
            }

            if (system?.FindType(creature.Type)?.ActorBlueprint != true)
            {
                errors.Add($"{draft.Ref} (bestiary '{draft.Name}'): '{creature.Type}/{creature.Id}' is not a creature, so it cannot go on the Bestiary.");
                continue;
            }

            draft.Name = draft.OutlineName = creature.Shown;
            draft.SharedWithPlayers = campaign.Bestiary.Any(b => string.Equals(b.EntryType, creature.Type, StringComparison.OrdinalIgnoreCase) && b.EntryId == creature.Id && b.IsHomebrew == creature.Homebrew);
        }

        return errors;
    }

    /// <summary>Names each thing a tags entry changes and records its tags as they are now, for the review card.</summary>
    private static void FillTagged(WorldBundle bundle, List<WikiPageDocument> pages, List<HomebrewEntryDocument> brews)
    {
        foreach (var draft in bundle.Drafts.Where(d => d.Kind == DraftKinds.Tags))
        {
            draft.Tagged = draft.Targets.Select(target =>
            {
                var kind = WorldBundle.ParseTarget(target, out var id);
                var page = kind == EditTarget.Page ? pages.FirstOrDefault(p => string.Equals(p.Slug, id, StringComparison.OrdinalIgnoreCase)) : null;
                var brew = kind == EditTarget.Homebrew ? brews.FirstOrDefault(h => string.Equals($"{h.Type}/{h.EntryId}", id, StringComparison.OrdinalIgnoreCase)) : null;
                return new TaggedThing { Key = target, Name = page?.Title ?? brew?.Name ?? id, Before = new List<string>(page?.Tags ?? brew?.Tags ?? new List<string>()) };
            }).ToList();
        }
    }

    /// <summary>The system's types whose entries can stand on the table with a stat block (monsters).</summary>
    private static List<string> CreatureTypes(SystemDefinition? system) =>
        system?.EntityTypes.Where(t => t.ActorBlueprint).Select(t => t.Type).ToList() ?? new List<string>();

    /// <summary>The Library creatures a person's stats may come from, as the DM reads them: the campaign's packs and its homebrew.</summary>
    public async Task<IReadOnlyList<LibraryItem>> CreaturesAsync(CampaignDocument campaign, CampaignAccess access)
    {
        var items = new List<LibraryItem>();
        foreach (var type in CreatureTypes(catalog.GetDefinition(campaign.SystemId)))
        {
            items.AddRange(await library.OptionsAsync(campaign, access, type, Lang));
        }

        return items;
    }

    /// <summary>Finds the creature each person's suggested stats name in the Library. Only an exact name (or id) counts: a near
    /// miss would give the person the wrong stat block, and a blank sheet is the safer start.</summary>
    public async Task FindStatsAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle, WorldDraft? only = null)
    {
        var system = catalog.GetDefinition(campaign.SystemId);
        var wanted = only != null ? new List<WorldDraft> { only }
            : bundle.Drafts.Where(d => d.Kind == DraftKinds.Npc && d.Stats == null && !string.IsNullOrWhiteSpace(d.SuggestedStats)).ToList();
        foreach (var draft in wanted)
        {
            var name = draft.SuggestedStats!.Trim();
            // "monster/veteran", as search_library lists it, is a type and an id.
            if (name.Split('/') is [var typeKey, var id] && await library.GetAsync(campaign, access, typeKey, id) is { } byId)
            {
                draft.Stats = await CreatureAsync(campaign, access, byId);
                continue;
            }

            foreach (var type in CreatureTypes(system))
            {
                // As the packs name it (the prompt asks for the English name), then in the DM's language, for a model that translated it anyway.
                var exact = (await library.SearchAsync(campaign, access, name, type, limit: 20)).FirstOrDefault(i => Names(i, name))
                            ?? (await library.SearchAsync(campaign, access, name, type, limit: 20, culture: Lang)).FirstOrDefault(i => Names(i, name));
                if (exact == null) continue;
                draft.Stats = await CreatureAsync(campaign, access, exact);
                break;
            }
        }

        static bool Names(LibraryItem item, string name) =>
            string.Equals(item.Entry.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Entry.Id, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A Library creature as a draft records it: the pack's own name, which the model reads, and the one the DM reads.</summary>
    public async Task<CreatureRef> CreatureAsync(CampaignDocument campaign, CampaignAccess access, LibraryItem item)
    {
        var shown = item.IsHomebrew ? item.Entry.Name : (await library.GetAsync(campaign, access, item.Entry.Type, item.Entry.Id, Lang))?.Entry.Name ?? item.Entry.Name;
        var name = item.IsHomebrew ? item.Entry.Name : (await library.GetAsync(campaign, access, item.Entry.Type, item.Entry.Id))?.Entry.Name ?? item.Entry.Name;
        return new CreatureRef(item.Entry.Type, item.Entry.Id, item.IsHomebrew, name, shown);
    }

    // ---- Saving ----

    private static WikiPageKind PageKindOf(WorldDraft draft) => draft.Kind switch
    {
        DraftKinds.Npc => WikiPageKind.Person,
        DraftKinds.Place => WikiPageKind.Place,
        _ => draft.PageKind switch
        {
            "faction" => WikiPageKind.Faction,
            "quest" => WikiPageKind.Quest,
            _ => WikiPageKind.General
        }
    };

    // The tag a saved page gets, in the DM's words: what kind of page it is, when that says more than "lore".
    private static string? TagKey(WorldDraft draft) => draft.Kind switch
    {
        DraftKinds.Npc => "npc",
        DraftKinds.Place => draft.PlaceKind == "settlement" ? "settlement" : "place",
        _ => draft.PageKind is "faction" or "quest" ? draft.PageKind : null
    };

    /// <summary>What a person added to the cast carries, picked by the model; none when the system has no inventory or the call
    /// fails (the person is still worth saving without it).</summary>
    private async Task<IReadOnlyList<object>?> GearAsync(string name, string text, CampaignDocument campaign, CampaignAccess access, CancellationToken ct)
    {
        try
        {
            return await ai.EquipAsync($"{name.Trim()}: {TextFormatting.Summary(text, 800)}", campaign, catalog.GetDefinition(campaign.SystemId), access.UserId,
                async type => (await library.OptionsAsync(campaign, access, type)).Select(i => i.Entry).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Saves the selected entries: wiki pages and people's and places' pages first (DM-only), so their titles are settled
    /// before links are rewritten, then homebrew entries (unpublished; creatures ticked for it also on the Bestiary shortlist), the
    /// people ticked for the cast (hidden, their page as their history, with gear the model picks), the places (hidden, nested as
    /// proposed) and the secrets (DM-only in Lore, related to what they link to). Retrying after a failure skips what was stored.</summary>
    public async Task<WorldBuildResult> SaveAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle, CancellationToken ct = default)
    {
        DmOnly(access);
        var l = Localizer.For(Lang);
        // What each saved draft became, so a secret can point at the pages and cast members it is about.
        // A retry after a failed save starts from what the first attempt stored (WorldDraft.SavedAs) and skips those steps.
        var savedIds = Kept(bundle).ToDictionary(d => d.Ref, d => d.SavedAs.Values.ToList());
        var earlier = Kept(bundle).SelectMany(d => d.SavedAs.Select(s => (Draft: d, Step: s.Key, Id: s.Value))).ToList();
        void Saved(WorldDraft draft, string step, string id)
        {
            draft.SavedAs[step] = id;
            savedIds[draft.Ref].Add(id);
        }

        IEnumerable<WorldDraft> Pending(string step) => Kept(bundle).Where(d => !d.SavedAs.ContainsKey(step));

        var pages = new List<WikiPageDocument>();
        var newPages = Pending(PageStep).Where(d => DraftKinds.IsPage(d.Kind)).ToList();
        if (newPages.Count > 0)
        {
            await UniqueTitlesAsync(newPages, Kept(bundle).Where(d => d.SavedAs.ContainsKey(PageStep)), access);
        }

        foreach (var draft in newPages)
        {
            var page = new WikiPageDocument
            {
                Title = draft.PageTitle ?? draft.Name.Trim(),
                Body = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle),
                Visibility = WikiVisibility.DmOnly,
                Kind = PageKindOf(draft),
                Tags = (TagKey(draft) is { } tag ? new[] { Tag(l, tag) } : Array.Empty<string>()).Concat(draft.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AuthorUserId = access.UserId
            };
            if (!await wiki.TrySaveAsync(page, access))
            {
                throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }

            pages.Add(page);
            Saved(draft, PageStep, page.Id);
        }

        var system = catalog.GetDefinition(campaign.SystemId);
        var entries = new List<HomebrewEntryDocument>();
        var shortlist = new List<BestiaryEntry>();
        var existing = (await homebrew.GetVisibleForCampaignAsync(access)).ToList();
        foreach (var draft in Pending("homebrew").Where(d => d.Kind == DraftKinds.Homebrew && !string.IsNullOrEmpty(d.Type)))
        {
            var entry = new HomebrewEntryDocument
            {
                CampaignId = campaign.Id,
                SystemId = campaign.SystemId,
                Type = draft.Type!,
                Name = draft.Name.Trim(),
                EntryId = UniqueEntryId(existing.Concat(entries), draft.Type!, draft.Name),
                Description = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle, keepLinks: false),
                Tags = new List<string>(draft.Tags),
                Properties = new Dictionary<string, object>(draft.Properties),
                IsPublished = false,
                AuthorUserId = access.UserId,
                ImportedFrom = draft.ImportedFrom
            };
            if (!await homebrew.TrySaveAsync(entry, access))
            {
                throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }

            entries.Add(entry);
            Saved(draft, "homebrew", entry.Id);
        }

        // Creatures an earlier attempt stored are shortlisted too: that attempt may have failed before it got this far.
        var stored = existing.Concat(entries).ToDictionary(e => e.Id);
        foreach (var draft in Kept(bundle).Where(d => d.Bestiary && d.SavedAs.ContainsKey("homebrew")))
        {
            if (stored.TryGetValue(draft.SavedAs["homebrew"], out var entry) && system?.FindType(entry.Type)?.ActorBlueprint == true)
            {
                shortlist.Add(new BestiaryEntry { EntryType = entry.Type, EntryId = entry.EntryId, IsHomebrew = true, Name = entry.Name });
            }
        }

        // Library creatures picked for the Bestiary by an entry of their own; one already there is left as it is.
        var picks = Pending("bestiary").Where(d => d is { Kind: DraftKinds.Bestiary, Stats: not null }).ToList();
        foreach (var draft in picks)
        {
            var creature = draft.Stats!;
            shortlist.Add(new BestiaryEntry { EntryType = creature.Type, EntryId = creature.Id, IsHomebrew = creature.Homebrew, Name = creature.Name });
        }

        var shortlisted = await ShortlistAsync(campaign, shortlist);
        foreach (var draft in picks) Saved(draft, "bestiary", $"{draft.Stats!.Type}/{draft.Stats.Id}");

        var actors = new List<ActorDocument>();
        var castDrafts = Pending("cast").Where(d => d.Kind == DraftKinds.Npc && d.Cast).ToList();
        var allPages = castDrafts.Count == 0 ? new List<WikiPageDocument>() : (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        foreach (var draft in castDrafts)
        {
            // The sheet starts from the creature picked for the person, or blank; either way the model picks what they carry.
            // A creature that has gone since (its pack switched off, the homebrew deleted) leaves the blank sheet.
            var creature = draft.Stats is { } stats ? await library.GetAsync(campaign, access, stats.Type, stats.Id, stats.Homebrew) : null;
            var text = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle, keepLinks: false);
            var npc = new NewNpc(draft.Name) { Creature = creature, Equipment = await GearAsync(draft.Name, text, campaign, access, ct) };
            // The person's page becomes their history, as promoting a page from the wiki does; revealing them warns the DM first.
            var page = draft.SavedAs.TryGetValue(PageStep, out var pageId) ? allPages.FirstOrDefault(p => p.Id == pageId) : null;
            var actor = (page != null
                    ? await cast.PromotePageAsync(page, npc, campaign, system, access)
                    : await cast.AddNpcAsync(npc with { Biography = text }, campaign, system, access))
                ?? throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            actors.Add(actor);
            Saved(draft, "cast", actor.Id);
        }

        var places = await PlacesAsync(bundle, Pending("place").ToList(), l, access);
        foreach (var (draft, place) in places)
        {
            Saved(draft, "place", place.Id);
        }

        await LinkPagesToPlacesAsync(places.Select(p => p.Place), access);

        var edited = new List<WorldEdit>();
        foreach (var draft in Pending("edit").Where(d => d.Kind == DraftKinds.Edit && d.Target != null))
        {
            edited.Add(await ApplyEditAsync(draft, bundle, access));
            Saved(draft, "edit", draft.Target!);
        }

        var retagged = 0;
        foreach (var draft in Pending("tags").Where(d => d.Kind == DraftKinds.Tags))
        {
            retagged += await RetagAsync(draft, access);
            Saved(draft, "tags", string.Join(",", draft.Targets));
        }

        var saved = new List<FactDocument>();
        var known = Kept(bundle).Any(d => d.Kind == DraftKinds.Fact)
            ? (await wiki.GetVisibleForCampaignAsync(access)).ToDictionary(p => p.Slug, p => p.Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();
        foreach (var draft in Pending("fact").Where(d => d.Kind == DraftKinds.Fact))
        {
            var details = draft.Body ?? (string.Equals(draft.Pitch.Trim(), draft.Name.Trim(), StringComparison.OrdinalIgnoreCase) ? null : draft.Pitch);
            var fact = new FactDocument
            {
                Id = Guid.NewGuid().ToString(),
                CampaignId = campaign.Id,
                Summary = draft.Name.Trim(),
                Details = string.IsNullOrWhiteSpace(details) ? null : WorldBundleParser.ResolveLinks(details, bundle, keepLinks: false).Trim(),
                Visibility = FactVisibility.DmOnly,
                RelatedEntityIds = draft.Links
                    .SelectMany(link => link.StartsWith(WorldBundle.ExistingPrefix, StringComparison.Ordinal)
                        ? known.TryGetValue(link[WorldBundle.ExistingPrefix.Length..], out var id) ? new List<string> { id } : new List<string>()
                        : savedIds.GetValueOrDefault(link) ?? new List<string>())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
                    .ToList()
            };
            if (!await facts.SaveAsync(fact, access))
            {
                throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }

            saved.Add(fact);
            Saved(draft, "fact", fact.Id);
        }

        // A retry reports the whole save, the earlier attempt's documents first, so the count is right and "open the first
        // saved page" opens the proposal's first page even when the first attempt stored it.
        var ids = earlier.ToLookup(e => e.Step, e => e.Id);
        if (ids.Count > 0)
        {
            if (ids.Contains(PageStep))
            {
                var all = (await wiki.GetVisibleForCampaignAsync(access)).ToDictionary(page => page.Id);
                pages.InsertRange(0, ids[PageStep].Where(all.ContainsKey).Select(id => all[id]));
            }

            entries.InsertRange(0, ids["homebrew"].Where(stored.ContainsKey).Select(id => stored[id]));
            foreach (var id in ids["cast"].Reverse())
            {
                if (await actorRepository.GetVisibleAsync(id, access) is { } actor) actors.Insert(0, actor);
            }

            if (ids.Contains("place"))
            {
                var all = (await regions.GetVisibleForCampaignAsync(access)).SelectMany(r => r.Locations).ToDictionary(place => place.Id);
                places.InsertRange(0, earlier.Where(e => e.Step == "place" && all.ContainsKey(e.Id)).Select(e => (e.Draft, all[e.Id])));
            }

            if (ids.Contains("fact"))
            {
                var all = (await facts.GetVisibleForCampaignAsync(access, new HashSet<string>())).ToDictionary(fact => fact.Id);
                saved.InsertRange(0, ids["fact"].Where(all.ContainsKey).Select(id => all[id]));
            }
        }

        // Edits an earlier attempt made are reported too, first.
        edited.InsertRange(0, earlier.Where(e => e.Step == "edit").Select(e => EditOf(e.Draft)));
        retagged += earlier.Where(e => e.Step == "tags").Sum(e => e.Draft.Targets.Count);
        var picked = Kept(bundle).Count(d => d.Kind == DraftKinds.Bestiary && d.SavedAs.ContainsKey("bestiary"));
        return new WorldBuildResult(entries, pages, actors, saved, places.Select(p => p.Place).ToList(), shortlisted, edited, retagged, picked);
    }

    /// <summary>True when two field values say the same, however each was stored (a long from the model, a double from the database).</summary>
    internal static bool Same(object? a, object? b) => PropertyJson(a) == PropertyJson(b);

    private static WorldEdit EditOf(WorldDraft draft)
    {
        var target = WorldBundle.ParseTarget(draft.Target ?? string.Empty, out var id);
        return new WorldEdit(target, id, draft.Name);
    }

    /// <summary>Replaces the text an edit targets with its complete new text. The text is the DM's to review beforehand, so a target
    /// that has gone since is an error rather than something to skip quietly.</summary>
    private async Task<WorldEdit> ApplyEditAsync(WorldDraft draft, WorldBundle bundle, CampaignAccess access)
    {
        var target = WorldBundle.ParseTarget(draft.Target!, out var id);
        var gone = new LocalizedException("WorldBuilder.Error.EditTargetGone", draft.Name);
        var body = draft.Body ?? string.Empty;
        var changed = new LocalizedException("WorldBuilder.Error.EditChanged", draft.Name);
        // The text the DM reviewed must still be the text there: someone may have edited it since (another tab, a co-DM).
        void Unchanged(string? stored)
        {
            if (draft.BeforeStamp != null && draft.BeforeStamp != Stamp(stored)) throw changed;
        }

        switch (target)
        {
            case EditTarget.Homebrew:
            {
                var entry = (await homebrew.GetVisibleForCampaignAsync(access)).FirstOrDefault(h => string.Equals($"{h.Type}/{h.EntryId}", id, StringComparison.OrdinalIgnoreCase)) ?? throw gone;
                if (draft.BeforeStamp != null && draft.BeforeStamp != HomebrewStamp(entry)) throw changed;
                if (draft.Rename is { Length: > 0 } rename) entry.Name = rename.Trim();
                // Rules text, not wiki text: links become plain names.
                if (draft.Body != null) entry.Description = WorldBundleParser.ResolveLinks(draft.Body, bundle, keepLinks: false).Trim();
                foreach (var (name, value) in draft.Properties.Where(p => draft.BeforeProperties == null || !Same(draft.BeforeProperties.GetValueOrDefault(p.Key), p.Value)))
                {
                    entry.Properties[name] = value;
                }

                if (draft.Retag) entry.Tags = new List<string>(draft.Tags);
                if (!await homebrew.TrySaveAsync(entry, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                return new WorldEdit(target, $"{entry.Type}/{entry.EntryId}", entry.Name);
            }
            case EditTarget.Place:
            {
                foreach (var region in (await regions.GetVisibleForCampaignAsync(access)).ToList())
                {
                    if (region.Locations.FirstOrDefault(p => p.Id == id) is not { } place) continue;
                    Unchanged(place.Description);
                    // What players read once the place is revealed: plain text, no wiki links (mentions stay).
                    place.Description = ResolveEditText(body, bundle, keepLinks: false).Trim();
                    if (!await regions.UpsertLocationAsync(region.Id, place, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                    return new WorldEdit(target, id, place.Name);
                }

                throw gone;
            }
            case EditTarget.Actor:
            {
                var actor = await actorRepository.GetVisibleAsync(id, access) ?? throw gone;
                Unchanged(actor.Biography);
                actor.Biography = ResolveEditText(body, bundle, keepLinks: false).Trim();
                if (!await actorRepository.TrySaveAsync(actor, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                return new WorldEdit(target, id, actor.Name);
            }
            default:
            {
                var page = (await wiki.GetVisibleForCampaignAsync(access)).FirstOrDefault(p => string.Equals(p.Slug, id, StringComparison.OrdinalIgnoreCase)) ?? throw gone;
                Unchanged(page.Body);
                page.Body = ResolveEditText(body, bundle, keepLinks: true).Trim();
                if (!await wiki.TrySaveAsync(page, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                return new WorldEdit(target, page.Slug, page.Title);
            }
        }
    }

    /// <summary>Adds and takes off a tags entry's tags on each page and homebrew entry it names. Returns how many changed; one
    /// that has gone since is skipped (its tags no longer matter).</summary>
    private async Task<int> RetagAsync(WorldDraft draft, CampaignAccess access)
    {
        static List<string>? Changed(List<string> tags, WorldDraft draft)
        {
            var next = tags.Where(t => !draft.TagsRemoved.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            next.AddRange(draft.TagsAdded.Where(t => !next.Contains(t, StringComparer.OrdinalIgnoreCase)));
            return next.SequenceEqual(tags) ? null : next;
        }

        var changed = 0;
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var brews = (await homebrew.GetVisibleForCampaignAsync(access)).ToList();
        foreach (var target in draft.Targets)
        {
            var kind = WorldBundle.ParseTarget(target, out var id);
            if (kind == EditTarget.Page && pages.FirstOrDefault(p => string.Equals(p.Slug, id, StringComparison.OrdinalIgnoreCase)) is { } page && Changed(page.Tags, draft) is { } pageTags)
            {
                page.Tags = pageTags;
                if (!await wiki.TrySaveAsync(page, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                changed++;
            }
            else if (kind == EditTarget.Homebrew && brews.FirstOrDefault(h => string.Equals($"{h.Type}/{h.EntryId}", id, StringComparison.OrdinalIgnoreCase)) is { } brew && Changed(brew.Tags, draft) is { } brewTags)
            {
                brew.Tags = brewTags;
                if (!await homebrew.TrySaveAsync(brew, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Puts the creatures ticked for the Bestiary on the campaign's shortlist (reload-then-mutate, as the Bestiary page
    /// does, so a concurrent edit is not overwritten) and refreshes the caller's copy of the campaign.</summary>
    private async Task<IReadOnlyList<BestiaryEntry>> ShortlistAsync(CampaignDocument campaign, List<BestiaryEntry> shortlist)
    {
        if (shortlist.Count == 0)
        {
            return shortlist;
        }

        var stored = await campaigns.UpdateAsync(campaign.Id, c =>
        {
            foreach (var entry in shortlist.Where(e => c.Bestiary.All(b => b.Key != e.Key)))
            {
                c.Bestiary.Add(entry);
            }
        });
        if (stored != null)
        {
            campaign.Bestiary = stored.Bestiary;
        }

        return shortlist;
    }

    /// <summary>The proposal's places on the Places list, hidden, each inside its parent: another place of the proposal (saved now
    /// or by an earlier attempt) or one the campaign has, whose region it joins. A place with no parent goes into the proposal's
    /// region (found by name, made when missing), else the campaign's first region, else a new one. A place's description is its
    /// pitch, links flattened: players read it once the place is revealed, and the page's text tells what the place hides.</summary>
    private async Task<List<(WorldDraft Draft, LocationDocument Place)>> PlacesAsync(WorldBundle bundle, List<WorldDraft> pending, Localizer l, CampaignAccess access)
    {
        var ticked = pending.Where(d => d.Kind == DraftKinds.Place && d.OnMap).ToList();
        if (ticked.Count == 0)
        {
            return new List<(WorldDraft, LocationDocument)>();
        }

        var all = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var regionOfPlace = all.SelectMany(r => r.Locations.Select(p => (p.Id, Region: r))).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Region);
        var existingPlaces = all.SelectMany(r => r.Locations).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var made = new Dictionary<string, LocationDocument>();
        var regionOfDraft = new Dictionary<string, RegionDocument?>();
        RegionDocument? fallback = null;
        var newRegions = new List<RegionDocument>();

        // Parents before children: a place's id and region are known before the places inside it are made.
        var placed = new List<(WorldDraft Draft, LocationDocument Place)>();
        var open = ticked.ToList();
        while (open.Count > 0)
        {
            var ready = open.Where(d => d.Parent == null || bundle.Find(d.Parent) is not { } p || !open.Contains(p)).ToList();
            if (ready.Count == 0) ready = open.ToList(); // a loop the check let through: put the rest at the top
            foreach (var draft in ready)
            {
                open.Remove(draft);
                LocationDocument? parent = null;
                RegionDocument? region = null;
                if (draft.Parent?.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix, StringComparison.Ordinal) == true)
                {
                    var id = draft.Parent[(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix).Length..];
                    parent = existingPlaces.GetValueOrDefault(id);
                    region = regionOfPlace.GetValueOrDefault(id);
                }
                else if (draft.Parent != null && bundle.Find(draft.Parent) is { } parentDraft)
                {
                    if (made.TryGetValue(parentDraft.Ref, out var madeParent))
                    {
                        parent = madeParent;
                        region = regionOfDraft.GetValueOrDefault(parentDraft.Ref);
                    }
                    else if (parentDraft.SavedAs.TryGetValue("place", out var savedId) && existingPlaces.TryGetValue(savedId, out var savedParent))
                    {
                        parent = savedParent;
                        region = regionOfPlace.GetValueOrDefault(savedId);
                    }
                }

                if (region == null)
                {
                    parent = null;
                    region = fallback ??= Region(bundle.Region) ?? NewRegion(bundle.Region ?? l["Campaign.Wiki.DefaultRegion"]);
                }

                var place = new LocationDocument
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = draft.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(draft.Pitch) ? null : WorldBundleParser.ResolveLinks(draft.Pitch, bundle, keepLinks: false),
                    Visibility = ActorVisibility.Hidden,
                    Kind = Enum.TryParse<PlaceKind>(draft.PlaceKind, ignoreCase: true, out var kind) ? kind : PlaceTree.ChildKind(parent),
                    ParentId = parent?.Id,
                    // The page written for this draft is the place's page.
                    WikiPageId = draft.SavedAs.GetValueOrDefault(PageStep)
                };
                made[draft.Ref] = place;
                regionOfDraft[draft.Ref] = region;
                placed.Add((draft, place));
            }
        }

        foreach (var region in placed.Select(p => regionOfDraft[p.Draft.Ref]!).Distinct())
        {
            var inRegion = placed.Where(p => regionOfDraft[p.Draft.Ref] == region).Select(p => p.Place).ToList();
            if (newRegions.Contains(region))
            {
                region.Locations = inRegion;
                if (!await regions.SaveAsync(region, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                continue;
            }

            foreach (var place in inRegion)
            {
                if (!await regions.UpsertLocationAsync(region.Id, place, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }
        }

        return placed;

        RegionDocument? Region(string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? all.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : all.FirstOrDefault(r => string.Equals(r.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        RegionDocument NewRegion(string name)
        {
            var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = access.CampaignId, Name = name.Trim() };
            newRegions.Add(region);
            return region;
        }
    }

    private const string PageStep = "page";

    /// <summary>Points each page at the place that names it, so the page shows the place's picture and sharing it reveals the place.</summary>
    private async Task LinkPagesToPlacesAsync(IEnumerable<LocationDocument> places, CampaignAccess access)
    {
        var linked = places.Where(p => !string.IsNullOrEmpty(p.WikiPageId)).ToList();
        if (linked.Count == 0) return;
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var place in linked)
        {
            if (pages.TryGetValue(place.WikiPageId!, out var page) && page.PlaceId != place.Id)
            {
                page.PlaceId = place.Id;
                await wiki.TrySaveAsync(page, access);
            }
        }
    }

    /// <summary>Gives each new page a title of its own: "Name (2)" when an existing page or another entry has the name, so links
    /// (which go by title) reach it. The entry keeps its name, which a cast member or a place uses.</summary>
    private async Task UniqueTitlesAsync(List<WorldDraft> drafts, IEnumerable<WorldDraft> saved, CampaignAccess access)
    {
        var taken = (await wiki.GetVisibleForCampaignAsync(access))
            .SelectMany(p => new[] { p.Slug, WikiText.Slugify(p.Title) })
            .Concat(saved.Select(d => WikiText.Slugify(d.PageTitle ?? d.Name.Trim())))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            var name = draft.Name.Trim();
            for (var n = 2; taken.Contains(WikiText.Slugify(name)); n++)
            {
                name = $"{draft.Name.Trim()} ({n})";
            }

            draft.PageTitle = name;
            taken.Add(WikiText.Slugify(name));
        }
    }

    private static IEnumerable<WorldDraft> Kept(WorldBundle bundle) => bundle.Drafts.Where(d => d.Selected && !string.IsNullOrWhiteSpace(d.Name));

    // Wiki tags are the DM's own words, so the ones the builder adds are written in the DM's language.
    private static string Tag(Localizer l, string key) => l.Has($"WorldBuilder.Tag.{key}") ? l[$"WorldBuilder.Tag.{key}"] : key;

    /// <summary>The types a homebrew entry may have, as the DM reads them: the system's declared types (and any extra ones the Library has).</summary>
    public Task<IReadOnlyList<ContentTypeDefinition>> TypesAsync(CampaignDocument campaign, CampaignAccess access) => library.TypesAsync(campaign, access);

    private async Task<IReadOnlyList<ContentTypeDefinition>> HomebrewTypesAsync(CampaignDocument campaign, CampaignAccess access, SystemDefinition? system)
    {
        var shown = await library.TypesAsync(campaign, access);
        return shown.Select(t => system?.Canonical.FindType(t.Type) ?? t).ToList();
    }

    // Every way the model might name a type: its key and folder, its English labels and the ones the DM reads.
    private Dictionary<string, string> TypeSpellings(IReadOnlyList<ContentTypeDefinition> types, string systemId)
    {
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localized = catalog.GetLocalizedDefinition(systemId);
        foreach (var type in types)
        {
            var shown = localized?.FindType(type.Type);
            foreach (var spelling in new[] { type.Type, type.Folder, type.Label, type.PluralLabel, shown?.Label, shown?.PluralLabel })
            {
                if (!string.IsNullOrWhiteSpace(spelling)) spellings.TryAdd(spelling.Trim(), type.Type);
            }
        }

        return spellings;
    }

    /// <summary>The fields a homebrew entry fills: the type's editor fields, with the options a reference may pick and the
    /// translated label of each dropdown option, so a value written in the DM's language still maps to the stored one.</summary>
    private async Task<List<FieldSpec>> FieldSpecsAsync(CampaignDocument campaign, CampaignAccess access, SystemDefinition? system, ContentTypeDefinition type)
    {
        var specs = new List<FieldSpec>();
        foreach (var field in type.EditorFields().Where(f => f.Type != FieldType.ImagePicker))
        {
            IReadOnlyList<string>? options = field.Type switch
            {
                FieldType.Dropdown => (field.Options ?? new List<string>())
                    .Select(o => system?.DisplayOption(Lang, field.Key, o) is { } shown && shown != o ? $"{o} ({shown})" : o)
                    .ToList(),
                FieldType.EntityReference when field.TargetEntityType != null =>
                    (await library.OptionsAsync(campaign, access, field.TargetEntityType)).Take(80).Select(i => $"{i.Entry.Id} ({i.Entry.Name})").ToList(),
                _ => null
            };
            // Image prompts feed an image model and stay English, as everywhere else.
            var hint = field.Key == "imagePrompt" ? (field.Description + " Write this one in English.").Trim() : field.Description;
            specs.Add(new FieldSpec(field.Key, field.Label, field.Type.ToString(), options, field.Min, field.Max, hint));
        }

        return specs;
    }

    /// <summary>A type as the model reads it: the label system.yaml gives it, or its key made readable when it has none.</summary>
    private static string PromptLabel(string key, ContentTypeDefinition? type) =>
        string.IsNullOrWhiteSpace(type?.Label) ? CampaignLibrary.Humanize(key) : type.Label;

    private static string KindName(PlaceKind kind) => kind == PlaceKind.Site ? "site" : kind.ToString().ToLowerInvariant();

    // "Region > Town": where a place sits.
    private static string Path(RegionDocument region, LocationDocument place) =>
        string.Join(" > ", PlaceTree.Ancestors(region.Locations, place).Select(p => p.Name).Prepend(region.Name));

    private static WikiPageDocument? PageOf(ActorDocument actor, IEnumerable<WikiPageDocument> pages) =>
        string.IsNullOrEmpty(actor.WikiPageId) ? null : pages.FirstOrDefault(p => p.Id == actor.WikiPageId);

    // The names stored mentions stand for, so the model reads "Mother Ilse", not an id.
    private static Dictionary<string, string> Names(IEnumerable<ActorDocument> actors, IEnumerable<RegionDocument> allRegions)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var actor in actors) names[$"actor:{actor.Id}"] = actor.Name;
        foreach (var place in allRegions.SelectMany(r => r.Locations)) names[$"place:{place.Id}"] = place.Name;
        return names;
    }

    private static string Cap(string text) =>
        text.Length > MaxPageText ? text[..MaxPageText] + "\n[...] (too long to show whole: it cannot be replaced by an edit)" : text;

    // A text as the model reads it to edit it: a mention stays a marker, [[actor:id|Name]], so copying a passage back keeps the link.
    private static string Editable(string? body, IReadOnlyDictionary<string, string> names) =>
        Mention.Replace(body ?? string.Empty, m => $"[[{m.Groups[1].Value}|{names.GetValueOrDefault(m.Groups[1].Value) ?? m.Groups[1].Value.Split('/').Last().Split(':').Last()}]]");

    private static readonly Regex MentionMarker = new(@"\[\[((?:actor|place|item):[^\]\s|]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex HeldMarker = new("\u0007(\\d{1,6})\u0008", RegexOptions.Compiled);

    /// <summary>The text an edit saves: links to the proposal's entries and pages resolved as for any entry, but mentions kept as the
    /// stored <c>[[actor:id]]</c> they were (the model wrote them back as <c>[[actor:id|Name]]</c>); resolving them as wiki links
    /// would turn the id into text.</summary>
    internal static string ResolveEditText(string body, WorldBundle bundle, bool keepLinks)
    {
        var held = new List<string>();
        var text = MentionMarker.Replace(body.Replace("\u0007", string.Empty).Replace("\u0008", string.Empty), m =>
        {
            held.Add($"[[{m.Groups[1].Value}]]");
            return $"\u0007{held.Count - 1}\u0008";
        });
        text = WorldBundleParser.ResolveLinks(text, bundle, keepLinks);
        return held.Count == 0 ? text : HeldMarker.Replace(text, m => held[int.Parse(m.Groups[1].Value)]);
    }

    // A homebrew entry is stamped by its last save: an edit compares fields as well as text, and every save moves it.
    private static string HomebrewStamp(HomebrewEntryDocument entry) => Stamp(entry.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + entry.Description);

    private static string Stamp(string? text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    private static readonly Regex Mention = new(@"\[\[((?:actor|place|item):[^\]\s]+)\]\]", RegexOptions.Compiled);

    private static string Plain(string? body, IReadOnlyDictionary<string, string> names) =>
        Mention.Replace(body ?? string.Empty, m => names.GetValueOrDefault(m.Groups[1].Value) ?? m.Groups[1].Value.Split('/').Last().Split(':').Last());

    private static string UniqueEntryId(IEnumerable<HomebrewEntryDocument> existing, string type, string name)
    {
        var taken = existing.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)).Select(e => e.EntryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slug = RavenHomebrewRepository.Slugify(name);
        if (!taken.Contains(slug)) return slug;
        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{slug}-{i}")) return $"{slug}-{i}";
        }
    }

    private static string CampaignContext(CampaignDocument campaign) =>
        $"{campaign.Name}{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}";

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }
}
