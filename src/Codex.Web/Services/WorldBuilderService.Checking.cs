using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

// The world builder's check of a proposal: what it would change, what it lacks, and each person's stats in the Library.
public sealed partial class WorldBuilderService
{
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
                        KeepsSecrets(draft, place.Description, keepLinks: false);
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
                        KeepsSecrets(draft, actor.Biography, keepLinks: false);
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
                        KeepsSecrets(draft, page.Body, keepLinks: true);
                    }

                    break;
            }
        }

        return errors;

        // get_page shows only the first MaxPageText characters, so a longer text was never fully read, and "the complete new text"
        // written from it would cut the rest off. Measured as get_page shows it (Cap(Editable(...))), mentions written out as
        // [[actor:id|Name]], which is longer than the stored text: measuring the stored one let a text read cut short pass (D10).
        void TooLong(WorldDraft draft, string? stored)
        {
            var shown = Editable(stored, names).Length;
            if (shown > MaxPageText)
            {
                errors.Add($"{draft.Ref} (edit '{draft.Name}'): the current text is {shown} characters, more than get_page shows ({MaxPageText}), " +
                           "so it cannot be replaced as a whole without losing the end; put the new material in a new entry instead.");
            }
        }

        // The edit replaces the whole text, so a hidden block left out, rewritten or moved out of its markers would be lost or shown
        // to players. The DM may well miss that in a long diff; the model is told to keep them whole instead.
        void KeepsSecrets(WorldDraft draft, string? stored, bool keepLinks)
        {
            foreach (var words in LostSecrets(draft, bundle, stored, keepLinks))
            {
                errors.Add($"{draft.Ref} (edit '{draft.Name}'): the new text drops or changes the hidden block that starts \"{words}\". " +
                           "Copy every :::dm and :::for block whole, its marker lines included, exactly as get_page shows it, and keep it for the same readers; " +
                           "put new hidden material in a new :::dm block.");
            }
        }
    }

    /// <summary>The hidden blocks of <paramref name="stored"/> that the edit's new text would lose or expose, as their first words.
    /// Both texts go through the same link resolution, so a block copied back verbatim compares equal.</summary>
    private static IReadOnlyList<string> LostSecrets(WorldDraft draft, WorldBundle bundle, string? stored, bool keepLinks) =>
        SecretText.HasMarkers(stored)
            ? SecretText.LostSecrets(ResolveEditText(stored!, bundle, keepLinks), ResolveEditText(draft.Body ?? string.Empty, bundle, keepLinks))
            : Array.Empty<string>();

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
}
