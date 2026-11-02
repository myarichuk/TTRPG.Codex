using Codex.Core;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

// The world builder's save: the entries the DM keeps, stored hidden from players, and the links between them.
public sealed partial class WorldBuilderService
{
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

    /// <summary>Picks what each person of the proposal would carry into the cast, for those not picked for yet (an earlier check
    /// picked for the rest), so the DM sees it while reviewing and applying needs no model. Nothing is picked when the system has
    /// no inventory or no model is set up; a pick that fails leaves that person without gear.</summary>
    public async Task PickGearAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle, CancellationToken ct = default)
    {
        var wanting = bundle.Drafts.Where(d => d.Kind == DraftKinds.Npc && d.Gear == null && !d.SavedAs.ContainsKey("cast")).ToList();
        if (wanting.Count == 0) return;
        var characters = wanting
            .Select(d => $"{d.Name.Trim()}: {TextFormatting.Summary(WorldBundleParser.ResolveLinks(d.Body ?? d.Pitch, bundle, keepLinks: false), 800)}")
            .ToList();
        var picks = await ai.PickGearAsync(characters, campaign, catalog.GetDefinition(campaign.SystemId), access.UserId, ItemsFor(campaign, access), ct);
        for (var i = 0; i < wanting.Count; i++)
        {
            wanting[i].Gear = picks?[i] ?? new List<DraftGear>();
        }
    }

    private Func<string, Task<IReadOnlyList<ContentEntry>>> ItemsFor(CampaignDocument campaign, CampaignAccess access) =>
        async type => (await library.OptionsAsync(campaign, access, type)).Select(i => i.Entry).ToList();

    /// <summary>Saves the selected entries: wiki pages and people's and places' pages first (DM-only), so their titles are settled
    /// before links are rewritten, then homebrew entries (unpublished; creatures ticked for it also on the Bestiary shortlist), the
    /// people ticked for the cast (hidden, their page as their history, with the gear picked when the proposal was checked), the places (hidden, nested as
    /// proposed) and the secrets (DM-only in Lore, related to what they link to). Retrying after a failure skips what was stored.</summary>
    /// <param name="onStored">Called after each entry is stored (its <see cref="WorldDraft.SavedAs"/> set), so the caller can keep that at
    /// once: an apply cut short by a crash or a closed tab then still knows what it made, and a retry does not make it twice.</param>
    public async Task<WorldBuildResult> SaveAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle, CancellationToken ct = default, Func<Task>? onStored = null)
    {
        DmOnly(access);
        var l = Localizer.For(Lang);
        // What each saved draft became, so a secret can point at the pages and cast members it is about.
        // A retry after a failed save starts from what the first attempt stored (WorldDraft.SavedAs) and skips those steps.
        var savedIds = Kept(bundle).ToDictionary(d => d.Ref, d => d.SavedAs.Values.ToList());
        var earlier = Kept(bundle).SelectMany(d => d.SavedAs.Select(s => (Draft: d, Step: s.Key, Id: s.Value))).ToList();
        async Task Saved(WorldDraft draft, string step, string id)
        {
            draft.SavedAs[step] = id;
            savedIds[draft.Ref].Add(id);
            if (onStored != null) await onStored();
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
            if (await wiki.TrySaveAsync(page, access) != SaveOutcome.Saved)
            {
                throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }

            pages.Add(page);
            await Saved(draft, PageStep, page.Id);
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
            await Saved(draft, "homebrew", entry.Id);
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

        var shortlisted = await ShortlistAsync(campaign, access, shortlist);
        foreach (var draft in picks) await Saved(draft, "bestiary", $"{draft.Stats!.Type}/{draft.Stats.Id}");

        var actors = new List<ActorDocument>();
        var castDrafts = Pending("cast").Where(d => d.Kind == DraftKinds.Npc && d.Cast).ToList();
        var allPages = castDrafts.Count == 0 ? new List<WikiPageDocument>() : (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        foreach (var draft in castDrafts)
        {
            // The sheet starts from the creature picked for the person, or blank.
            // A creature that has gone since (its pack switched off, the homebrew deleted) leaves the blank sheet.
            var creature = draft.Stats is { } stats ? await library.GetAsync(campaign, access, stats.Type, stats.Id, stats.Homebrew) : null;
            var text = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle, keepLinks: false);
            // The gear the DM saw in the proposal; picking it was part of checking the proposal, so applying asks the model nothing.
            var npc = new NewNpc(draft.Name) { Creature = creature, Equipment = await ai.StoredGearAsync(draft.Gear, system, ItemsFor(campaign, access)) };
            // The person's page becomes their history, as promoting a page from the wiki does; revealing them warns the DM first.
            var page = draft.SavedAs.TryGetValue(PageStep, out var pageId) ? allPages.FirstOrDefault(p => p.Id == pageId) : null;
            var actor = (page != null
                    ? await cast.PromotePageAsync(page, npc, campaign, system, access)
                    : await cast.AddNpcAsync(npc with { Biography = text }, campaign, system, access))
                ?? throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            actors.Add(actor);
            await Saved(draft, "cast", actor.Id);
        }

        // Each place is recorded as soon as it is stored, so a failure further on does not make a retry store it twice (D10).
        var places = await PlacesAsync(bundle, Pending("place").ToList(), l, access, (draft, id) => Saved(draft, "place", id));

        await LinkPagesToPlacesAsync(places.Select(p => p.Place), access);

        var edited = new List<WorldEdit>();
        foreach (var draft in Pending("edit").Where(d => d.Kind == DraftKinds.Edit && d.Target != null))
        {
            edited.Add(await ApplyEditAsync(draft, bundle, access));
            await Saved(draft, "edit", draft.Target!);
        }

        var retagged = 0;
        foreach (var draft in Pending("tags").Where(d => d.Kind == DraftKinds.Tags))
        {
            retagged += await RetagAsync(draft, access);
            await Saved(draft, "tags", string.Join(",", draft.Targets));
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
            await Saved(draft, "fact", fact.Id);
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

    /// <summary>What an apply that stopped part way already made of <paramref name="bundle"/>, as links the conversation keeps: the
    /// entries a discard leaves behind.</summary>
    public async Task<List<WorldChatLink>> SavedLinksAsync(WorldBundle bundle, CampaignAccess access)
    {
        var links = new List<WorldChatLink>();
        var saved = bundle.Drafts.Where(d => d.SavedAs.Count > 0).ToList();
        if (saved.Count == 0) return links;
        var pages = saved.Any(d => d.SavedAs.ContainsKey(PageStep))
            ? (await wiki.GetVisibleForCampaignAsync(access)).ToDictionary(p => p.Id)
            : new Dictionary<string, WikiPageDocument>();
        var entries = saved.Any(d => d.SavedAs.ContainsKey("homebrew"))
            ? (await homebrew.GetVisibleForCampaignAsync(access)).ToDictionary(h => h.Id)
            : new Dictionary<string, HomebrewEntryDocument>();
        var facts = 0;
        var retagged = 0;
        foreach (var draft in saved)
        {
            foreach (var (step, id) in draft.SavedAs)
            {
                switch (step)
                {
                    case PageStep when pages.TryGetValue(id, out var page):
                        links.Add(new WorldChatLink { Kind = "page", Id = page.Slug, Name = page.Title });
                        break;
                    case "homebrew" when entries.TryGetValue(id, out var entry):
                        links.Add(new WorldChatLink { Kind = "homebrew", Id = $"{entry.Type}/{entry.EntryId}", Name = entry.Name });
                        break;
                    case "cast":
                        links.Add(new WorldChatLink { Kind = "actor", Id = id, Name = draft.Name });
                        break;
                    case "place" when !draft.SavedAs.ContainsKey(PageStep):
                        links.Add(new WorldChatLink { Kind = "place", Id = id, Name = draft.Name });
                        break;
                    case "bestiary":
                        links.Add(new WorldChatLink { Kind = "bestiary", Id = id, Name = draft.Stats?.Name ?? draft.Name });
                        break;
                    case "edit":
                        var change = EditOf(draft);
                        links.Add(new WorldChatLink { Kind = "change-" + change.Target.ToString().ToLowerInvariant(), Id = change.Id, Name = change.Name });
                        break;
                    case "fact":
                        facts++;
                        break;
                    case "tags":
                        retagged += draft.Targets.Count;
                        break;
                }
            }
        }

        if (retagged > 0) links.Add(new WorldChatLink { Kind = "tags", Name = retagged.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (facts > 0) links.Add(new WorldChatLink { Kind = "lore", Name = facts.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return links;
    }

    /// <summary>True when two field values say the same, however each was stored (a long from the model, a double from the database).</summary>
    internal static bool Same(object? a, object? b) => PropertyJson(a) == PropertyJson(b);

    private static WorldEdit EditOf(WorldDraft draft)
    {
        var target = WorldBundle.ParseTarget(draft.Target ?? string.Empty, out var id);
        return new WorldEdit(target, id, draft.Name, draft.SharedWithPlayers);
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
        // Nor may it lose or expose a hidden block. The check refused such an edit already; this holds even if that was skipped.
        void Unchanged(string? stored, bool keepLinks)
        {
            if (draft.BeforeStamp != null && draft.BeforeStamp != Stamp(stored)) throw changed;
            if (LostSecrets(draft, bundle, stored, keepLinks).Count > 0) throw new LocalizedException("WorldBuilder.Error.EditLosesSecret", draft.Name);
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
                    return new WorldEdit(target, $"{entry.Type}/{entry.EntryId}", entry.Name, entry.IsPublished);
                }
            case EditTarget.Place:
                {
                    foreach (var region in (await regions.GetVisibleForCampaignAsync(access)).ToList())
                    {
                        if (region.Locations.FirstOrDefault(p => p.Id == id) is not { } place) continue;
                        Unchanged(place.Description, keepLinks: false);
                        // What players read once the place is revealed: plain text, no wiki links (mentions stay).
                        place.Description = ResolveEditText(body, bundle, keepLinks: false).Trim();
                        if (!await regions.UpsertLocationAsync(region.Id, place, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                        return new WorldEdit(target, id, place.Name, place.Visibility == ActorVisibility.Known);
                    }

                    throw gone;
                }
            case EditTarget.Actor:
                {
                    var actor = await actorRepository.GetVisibleAsync(id, access) ?? throw gone;
                    Unchanged(actor.Biography, keepLinks: false);
                    actor.Biography = ResolveEditText(body, bundle, keepLinks: false).Trim();
                    if (!await actorRepository.TrySaveAsync(actor, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                    return new WorldEdit(target, id, actor.Name, actor.Visibility == ActorVisibility.Known);
                }
            default:
                {
                    var page = (await wiki.GetVisibleForCampaignAsync(access)).FirstOrDefault(p => string.Equals(p.Slug, id, StringComparison.OrdinalIgnoreCase)) ?? throw gone;
                    Unchanged(page.Body, keepLinks: true);
                    page.Body = ResolveEditText(body, bundle, keepLinks: true).Trim();
                    switch (await wiki.TrySaveAsync(page, access))
                    {
                        case SaveOutcome.Changed: throw changed;
                        case SaveOutcome.Refused: throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                    }

                    return new WorldEdit(target, page.Slug, page.Title, page.Visibility == WikiVisibility.Party);
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
                switch (await wiki.TrySaveAsync(page, access))
                {
                    case SaveOutcome.Changed: throw new LocalizedException("WorldBuilder.Error.EditChanged", draft.Name);
                    case SaveOutcome.Refused: throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                }

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
    private async Task<IReadOnlyList<BestiaryEntry>> ShortlistAsync(CampaignDocument campaign, CampaignAccess access, List<BestiaryEntry> shortlist)
    {
        if (shortlist.Count == 0)
        {
            return shortlist;
        }

        var stored = await campaigns.UpdateAsDmAsync(access, c =>
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
    /// pitch, links flattened: players read it once the place is revealed, and the page's text tells what the place hides.
    /// <paramref name="stored"/> is told each place as soon as it is stored, before the next region is written.</summary>
    private async Task<List<(WorldDraft Draft, LocationDocument Place)>> PlacesAsync(WorldBundle bundle, List<WorldDraft> pending, Localizer l, CampaignAccess access,
        Func<WorldDraft, string, Task> stored)
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
            var inRegion = placed.Where(p => regionOfDraft[p.Draft.Ref] == region).ToList();
            if (newRegions.Contains(region))
            {
                region.Locations = inRegion.Select(p => p.Place).ToList();
                if (!await regions.SaveAsync(region, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                foreach (var (draft, place) in inRegion) await stored(draft, place.Id);
                continue;
            }

            foreach (var (draft, place) in inRegion)
            {
                if (!await regions.UpsertLocationAsync(region.Id, place, access)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
                await stored(draft, place.Id);
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
}
