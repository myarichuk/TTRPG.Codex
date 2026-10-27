using System.Globalization;
using System.Text.Json;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>What saving a world-builder bundle stored.</summary>
public sealed record WorldBuildResult(
    IReadOnlyList<HomebrewEntryDocument> Entries,
    IReadOnlyList<WikiPageDocument> Pages,
    IReadOnlyList<ActorDocument> Actors,
    IReadOnlyList<FactDocument> Facts,
    IReadOnlyList<LocationDocument> Places,
    IReadOnlyList<BestiaryEntry> Shortlisted)
{
    public int Count => Entries.Count + Pages.Count + Actors.Count + Facts.Count + Places.Count;
}

/// <summary>
/// The world builder (docs/WORLD_BUILDER.md): turns a recipe's answers into an outline of drafts, writes each draft in full,
/// and saves the ones the DM keeps. The model only drafts; every write goes through the repositories with the DM's access,
/// and what it saves starts hidden from players (homebrew unpublished; wiki pages, secrets, cast members and places DM-only). DM only,
/// on the DM's own key.
/// </summary>
public sealed class WorldBuilderService(AiProviderService providers, ISystemCatalog catalog, CampaignLibrary library, IHomebrewRepository homebrew, IWikiRepository wiki,
    IFactRepository facts, CastService cast, IRegionRepository regions, ICampaignRepository campaigns, IActorRepository actorRepository, AiFeatures ai)
{
    /// <summary>The language the model writes in: the one the DM reads the app in.</summary>
    private static string Lang => SupportedCultures.Normalize(CultureInfo.CurrentUICulture.Name);

    private const int ContextCap = 40;

    public IReadOnlyList<WorldRecipe> Recipes => WorldRecipes.All;

    /// <summary>Asks for the recipe's drafts. <paramref name="answers"/> is keyed by question key; an empty answer is left to the model.</summary>
    public Task<WorldBundle> OutlineAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, CancellationToken ct = default) =>
        OutlineAsync(campaign, access, recipe, answers, null, null, ct);

    /// <summary>Makes the change the DM asked for to <paramref name="bundle"/>'s ideas (add, drop, darker...), keeping every draft
    /// the reply keeps with what was written, renamed or ticked on it (<see cref="WorldBundleParser.ParseRevision"/>). Only the
    /// drafts' names, pitches and links are sent, not their texts.</summary>
    public Task<WorldBundle> ReviseAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, WorldBundle bundle, string instruction, CancellationToken ct = default) =>
        OutlineAsync(campaign, access, recipe, answers, bundle, RequireInstruction(instruction), ct);

    private async Task<WorldBundle> OutlineAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers,
        WorldBundle? previous, string? instruction, CancellationToken ct)
    {
        DmOnly(access);
        var assistant = await providers.AssistantForAsync(access.UserId);
        var system = catalog.GetDefinition(campaign.SystemId);
        var types = await HomebrewTypesAsync(campaign, access, system);
        // When the DM picked the type (the homebrew recipe's first question), that type is the only one the reply may use.
        var chosen = recipe.Questions.Where(q => q.Type == QuestionTypes.ContentType)
            .Select(q => answers.GetValueOrDefault(q.Key))
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (chosen != null && types.Where(t => string.Equals(t.Type, chosen, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } only)
        {
            types = only;
        }

        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var taken = (await homebrew.GetVisibleForCampaignAsync(access)).Select(h => h.Name)
            .Concat(pages.Select(p => p.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ContextCap * 2)
            .ToList();

        var kinds = recipe.Drafts.Select(k => k switch
        {
            DraftKinds.Homebrew => "homebrew: a Library entry of one of the content types",
            DraftKinds.Wiki => "wiki: a page of the campaign's world wiki",
            DraftKinds.Npc => "npc: a person (a leader, a member, a contact), written as a wiki page",
            DraftKinds.Fact => "fact: a secret or rumour the party can uncover, stated in one sentence as its name",
            DraftKinds.Place => "place: a district, landmark or building, written as a wiki page",
            _ => k
        }).ToList();

        var request = new WorldOutlineRequest(
            recipe.Prompt,
            catalog.GetDisplayName(campaign.SystemId),
            Answers(recipe, answers, system),
            kinds,
            recipe.Drafts.Contains(DraftKinds.Homebrew) ? types.Select(t => $"{t.Type} ({PromptLabel(t.Type, t)}{(t.ActorBlueprint ? ", a " + (system?.Canonical.Ai.Creature ?? "creature") : "")})").ToList() : Array.Empty<string>(),
            pages.Take(ContextCap).Select(p => $"{WorldBundle.ExistingPrefix}{p.Slug} ({p.Title})").ToList(),
            taken,
            CampaignContext(campaign),
            Lang,
            previous?.Drafts.Select(d => JsonSerializer.Serialize(new
            {
                @ref = d.Ref,
                kind = d.Kind,
                type = d.Type,
                name = string.IsNullOrWhiteSpace(d.Name) ? d.OutlineName : d.Name.Trim(),
                pitch = d.Pitch,
                links = d.Links,
                stats = d.Stats?.Name ?? d.SuggestedStats
            }, PromptJson)).ToList(),
            instruction,
            CreatureTypes(system).Count > 0,
            system?.Canonical.Ai.Creature ?? "creature",
            system?.Canonical.Ai.CreatureExamples);

        var rules = new BundleRules(recipe.Drafts, TypeSpellings(types, campaign.SystemId), pages.Select(p => p.Slug).ToList());
        var bundle = await Unusable(() => previous == null
            ? assistant.OutlineWorldAsync(request, rules, ct)
            : assistant.ReviseWorldAsync(request, rules, previous, ct));
        await FindStatsAsync(campaign, access, bundle);
        return bundle;
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
    public async Task FindStatsAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle)
    {
        var system = catalog.GetDefinition(campaign.SystemId);
        var wanted = bundle.Drafts.Where(d => d.Kind == DraftKinds.Npc && d is { StatsChosen: false, Stats: null } && !string.IsNullOrWhiteSpace(d.SuggestedStats)).ToList();
        foreach (var draft in wanted)
        {
            var name = draft.SuggestedStats!.Trim();
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

    // Names in the DM's language stay readable in the prompt rather than becoming \u escapes.
    private static readonly JsonSerializerOptions PromptJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string RequireInstruction(string? instruction) =>
        string.IsNullOrWhiteSpace(instruction) ? throw new LocalizedException("WorldBuilder.Error.NoChange") : instruction.Trim();

    /// <summary>Writes one draft of <paramref name="bundle"/> in full: a wiki page's text, or a homebrew entry's description, tags and fields.</summary>
    public Task ExpandAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, WorldBundle bundle, WorldDraft draft, CancellationToken ct = default) =>
        ExpandAsync(campaign, access, recipe, answers, bundle, draft, null, ct);

    /// <summary>Rewrites one draft with the change the DM asked for, starting from what is written so far (or from its pitch).
    /// For a homebrew entry the model returns the whole entry again, so fields the change does not touch are kept.</summary>
    public Task ChangeAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, WorldBundle bundle, WorldDraft draft, string instruction, CancellationToken ct = default) =>
        ExpandAsync(campaign, access, recipe, answers, bundle, draft, RequireInstruction(instruction), ct);

    private async Task ExpandAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, WorldBundle bundle, WorldDraft draft,
        string? instruction, CancellationToken ct)
    {
        DmOnly(access);
        var assistant = await providers.AssistantForAsync(access.UserId);
        var system = catalog.GetDefinition(campaign.SystemId);
        var related = bundle.Drafts.Where(d => d != draft && d.Selected)
            .Select(d => $"{d.Name} ({KindForPrompt(d)}): {d.Pitch}")
            .ToList();
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).Take(ContextCap).Select(p => p.Title).ToList();

        if (draft.Kind != DraftKinds.Homebrew)
        {
            var request = new WorldExpandRequest(draft.Name, draft.Pitch, null, catalog.GetDisplayName(campaign.SystemId), Array.Empty<FieldSpec>(),
                Answers(recipe, answers, system), related, pages, CampaignContext(campaign), Lang, draft.Kind, recipe.Write.GetValueOrDefault(draft.Kind),
                instruction, draft.Expanded ? draft.Body : null);
            await Unusable(async () =>
            {
                await assistant.ExpandPageAsync(draft, request, ct);
                return true;
            });
            return;
        }

        // The prompt names the type and its fields as system.yaml does, whatever language the DM reads.
        var type = system?.Canonical.FindType(draft.Type ?? string.Empty)
                   ?? new ContentTypeDefinition { Type = draft.Type ?? string.Empty, Label = CampaignLibrary.Humanize(draft.Type ?? "entry") };
        var fields = await FieldSpecsAsync(campaign, access, system, type);
        var homebrewRequest = new WorldExpandRequest(draft.Name, draft.Pitch, PromptLabel(type.Type, type), catalog.GetDisplayName(campaign.SystemId), fields,
            Answers(recipe, answers, system), related, pages, CampaignContext(campaign), Lang, DraftKinds.Homebrew, recipe.Write.GetValueOrDefault(DraftKinds.Homebrew),
            instruction, draft.Expanded ? JsonSerializer.Serialize(new { description = draft.Body, tags = draft.Tags, properties = draft.Properties }, PromptJson) : null);
        var culture = Lang;
        await Unusable(async () =>
        {
            await assistant.ExpandHomebrewAsync(draft, homebrewRequest, system == null ? null : (key, value) => system.CanonicalOption(culture, key, value), ct);
            return true;
        });
    }

    /// <summary>Saves the selected drafts: wiki pages and people's pages first (DM-only), so their slugs are settled before
    /// links are rewritten, then homebrew entries (unpublished; creatures ticked for it also on the Bestiary shortlist), the people ticked for the cast (hidden), and secrets (DM-only
    /// in Lore, related to what they link to). A draft that was never written in full is saved with its pitch as its text.</summary>
    // A recipe's main page is about what the recipe makes.
    private static WikiPageKind KindOf(string recipeKey) => recipeKey switch
    {
        "faction" => WikiPageKind.Faction,
        "settlement" => WikiPageKind.Place,
        "quest" => WikiPageKind.Quest,
        _ => WikiPageKind.General
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

    public async Task<WorldBuildResult> SaveAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, WorldBundle bundle, CancellationToken ct = default)
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
            var tags = new List<string> { Tag(l, recipe.Key) };
            if (draft.Kind is DraftKinds.Npc or DraftKinds.Place) tags.Add(Tag(l, draft.Kind));
            var page = new WikiPageDocument
            {
                Title = draft.PageTitle ?? draft.Name.Trim(),
                Body = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle),
                Visibility = WikiVisibility.DmOnly,
                Kind = draft.Kind switch { DraftKinds.Npc => WikiPageKind.Person, DraftKinds.Place => WikiPageKind.Place, _ => KindOf(recipe.Key) },
                Tags = tags,
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
        var existing =(await homebrew.GetVisibleForCampaignAsync(access)).ToList();
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
                AuthorUserId = access.UserId
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

        var shortlisted = await ShortlistAsync(campaign, shortlist);

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

        var places = await PlacesAsync(bundle, Pending("place").ToList(), access);
        foreach (var (draft, place) in places)
        {
            Saved(draft, "place", place.Id);
        }

        await LinkPagesToPlacesAsync(places.Select(p => p.Place), access);

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
        // saved page" opens the bundle's first page even when the first attempt stored it.
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

        return new WorldBuildResult(entries, pages, actors, saved, places.Select(p => p.Place).ToList(), shortlisted);
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

    /// <summary>The places ticked for the World page, hidden, in a region named after the bundle's main page (the settlement);
    /// a region of that name is reused. A place's description is its pitch, links flattened: players read it once the place is
    /// revealed, and the page's text tells what the place hides.</summary>
    private async Task<List<(WorldDraft Draft, LocationDocument Place)>> PlacesAsync(WorldBundle bundle, List<WorldDraft> pending, CampaignAccess access)
    {
        var ticked = pending.Where(d => d.Kind == DraftKinds.Place && d.OnMap).ToList();
        if (ticked.Count == 0)
        {
            return new List<(WorldDraft, LocationDocument)>();
        }

        var placed = ticked.Select(d => (Draft: d, Place: new LocationDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = d.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(d.Pitch) ? null : WorldBundleParser.ResolveLinks(d.Pitch, bundle, keepLinks: false),
            Visibility = ActorVisibility.Hidden,
            // The page written for this draft is the place's page.
            WikiPageId = d.SavedAs.GetValueOrDefault(PageStep)
        })).ToList();

        // The recipe's main page comes first in the outline; it names the region even when the DM did not keep the page.
        var main = bundle.Drafts.FirstOrDefault(d => d.Kind == DraftKinds.Wiki);
        var name = main == null ? ticked[0].Name.Trim() : string.IsNullOrWhiteSpace(main.Name) ? main.OutlineName.Trim() : main.Name.Trim();
        var region = (await regions.GetVisibleForCampaignAsync(access)).FirstOrDefault(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (region == null)
        {
            region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = access.CampaignId, Name = name, Locations = placed.Select(p => p.Place).ToList() };
            if (!await regions.SaveAsync(region, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            return placed;
        }

        foreach (var (_, place) in placed)
        {
            if (!await regions.UpsertLocationAsync(region.Id, place, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
        }

        return placed;
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

    /// <summary>Gives each new page a title of its own: "Name (2)" when an existing page or another draft has the name, so links
    /// (which go by title) reach it. The draft keeps its name, which a cast member or a place on the World page uses.</summary>
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

    private static string KindForPrompt(WorldDraft draft) => draft.Kind switch
    {
        DraftKinds.Homebrew => draft.Type ?? "homebrew",
        DraftKinds.Npc => "person",
        DraftKinds.Fact => "secret",
        DraftKinds.Place => "place",
        _ => "wiki page"
    };

    /// <summary>The types a homebrew draft may have, as the DM reads them: the system's declared types (and any extra ones the Library has).</summary>
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

    /// <summary>The fields the expand call fills: the type's editor fields, with the options a reference may pick and the
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

    /// <summary>The answers as the model reads them: the English question, then the option key, the type's English label, yes/no or the text.</summary>
    public static List<WorldAnswer> Answers(WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, SystemDefinition? system)
    {
        var result = new List<WorldAnswer>();
        foreach (var question in recipe.Questions)
        {
            var answer = answers.TryGetValue(question.Key, out var given) ? given?.Trim() : null;
            if (string.IsNullOrEmpty(answer))
            {
                if (question.Required) throw new LocalizedException("WorldBuilder.Error.Required");
                result.Add(new WorldAnswer(question.Ask, question.Surprise ? "surprise me" : "no preference"));
                continue;
            }

            result.Add(new WorldAnswer(question.Ask, question.Type switch
            {
                QuestionTypes.Toggle => answer == "true" ? "yes" : "no",
                QuestionTypes.ContentType => $"{PromptLabel(answer, system?.Canonical.FindType(answer))} (type id: {answer})",
                _ => answer
            }));
        }

        return result;
    }

    /// <summary>A type as the model reads it: the label system.yaml gives it, or its key made readable when it has none.</summary>
    private static string PromptLabel(string key, ContentTypeDefinition? type) =>
        string.IsNullOrWhiteSpace(type?.Label) ? CampaignLibrary.Humanize(key) : type.Label;

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

    // A reply that would not parse even after the retry is the model's fault, not the DM's: say so in their language.
    private static async Task<T> Unusable<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (FormatException)
        {
            throw new LocalizedException("WorldBuilder.Error.Unusable");
        }
        catch (InvalidOperationException)
        {
            // An empty reply, or a provider that answered with something other than text.
            throw new LocalizedException("WorldBuilder.Error.Unusable");
        }
        catch (HttpRequestException)
        {
            throw new LocalizedException("WorldBuilder.Error.Unreachable");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // HttpClient's own timeout (a slow local model), not the DM closing the wizard.
            throw new LocalizedException("WorldBuilder.Error.TooSlow");
        }
    }
}
