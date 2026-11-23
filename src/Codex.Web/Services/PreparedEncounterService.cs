using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>One problem a plan has: a resource key and its arguments, so the page says it in the reader's language and a test can name it.</summary>
public sealed record EncounterIssue(string Key, params object[] Args);

/// <summary>What <see cref="PreparedEncounterService.CheckAsync"/> found. Errors stop a save; warnings only say that something the plan names has gone.</summary>
public sealed record EncounterCheck(IReadOnlyList<EncounterIssue> Errors, IReadOnlyList<EncounterIssue> Warnings)
{
    public bool Ok => Errors.Count == 0;

    public static EncounterCheck Refused { get; } = new(new[] { new EncounterIssue("Encounters.Error.NotAllowed") }, Array.Empty<EncounterIssue>());
}

/// <summary>A plan on the list: its place's name and path, a one-line lineup, and how many times it has been run.</summary>
public sealed record EncounterRow(PreparedEncounterDocument Plan, string? PlaceName, string? PlacePath, bool PlaceGone, string Summary, int LineCount, PreparedStatus Status, int RunCount)
{
    /// <summary>What a URL carries for this plan.</summary>
    public string Route => PreparedEncounterService.RouteOf(Plan.Id);
}

/// <summary>A line of a plan, resolved: the name it has now (its stored name when its entry or actor is gone) and whether it was found.</summary>
public sealed record EncounterLineView(PreparedLine Line, string Name, bool Found, string? Href);

/// <summary>A hazard placed at the plan's place or under it, read live, with the place it stands on.</summary>
public sealed record EncounterHazard(string Name, HazardState State, string PlaceName, string PlaceId, string RegionId);

/// <summary>A place the picker offers: with its region, and how deep in the region's tree it sits.</summary>
public sealed record EncounterPlaceChoice(string RegionId, string RegionName, string PlaceId, string PlaceName, int Depth)
{
    /// <summary>What the select's option carries: the region and the place together, as a plan stores them.</summary>
    public string Value => RegionId + "|" + PlaceId;
}

/// <summary>One plan in full: lines resolved against the Library and the cast, the place and its hazards read live, and what the check says about it.</summary>
public sealed record EncounterView(PreparedEncounterDocument Plan, IReadOnlyList<EncounterLineView> Lines, string? PlaceName, string? PlacePath, bool PlaceGone,
    IReadOnlyList<EncounterHazard> Hazards, EncounterCheck Check);

/// <summary>The outcome of a save: whether it was stored, the check that decided it, and the plan's id (set for a new plan once stored).</summary>
public sealed record EncounterSave(bool Saved, EncounterCheck Check, string? Id);

/// <summary>
/// The one place prepared encounters are read, checked, saved and deleted (ENCOUNTER_BUILDER_DESIGN.md 4.1). GM only: every method answers nothing to anyone else, and the repository
/// refuses them besides. Starting a plan and the difficulty readout come in later steps.
/// </summary>
public sealed class PreparedEncounterService(IPreparedEncounterRepository plans, IRegionRepository regions, IActorRepository actors, CampaignLibrary library, IContentRegistry registry)
{
    public const int MaxCount = 20;

    /// <summary>What one call reads once, however many lines the plan has: the regions, the cast and the Library (homebrew included).</summary>
    private sealed record Reads(IReadOnlyList<RegionDocument> Regions, IReadOnlyDictionary<string, ActorDocument> Cast, Func<string, string, bool, LibraryItem?> Lookup);

    /// <summary>The part of a plan's id a URL carries: the id without the <c>preparedencounters/</c> prefix when it has one. A copied campaign's plans have bare GUID ids, so
    /// nothing may rely on the prefix; <see cref="ViewAsync"/> and <see cref="DeleteAsync"/> take either shape.</summary>
    public static string RouteOf(string id) =>
        id.StartsWith(PreparedEncounterDocument.IdPrefix, StringComparison.Ordinal) ? id[PreparedEncounterDocument.IdPrefix.Length..] : id;

    /// <summary>The plan a URL argument names: the prefixed id first, then the bare one. Safe because the repository looks an id up in the plans' collection only, and in the access's campaign.</summary>
    private async Task<PreparedEncounterDocument?> FindAsync(CampaignAccess access, string? route)
    {
        if (!access.IsDm || string.IsNullOrWhiteSpace(route)) return null;
        return await plans.GetAsync(PreparedEncounterDocument.IdPrefix + route, access) ?? await plans.GetAsync(route, access);
    }

    private async Task<Reads> ReadAsync(CampaignDocument campaign, CampaignAccess access, string? culture) =>
        new((await regions.GetVisibleForCampaignAsync(access)).ToList(),
            (await actors.GetVisibleForCampaignAsync(access)).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First()),
            await library.LookupAsync(campaign, access, culture));

    public async Task<IReadOnlyList<EncounterRow>> ListAsync(CampaignDocument campaign, CampaignAccess access)
    {
        if (!access.IsDm) return Array.Empty<EncounterRow>();
        var all = await plans.ListAsync(access);
        var places = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var rows = new List<EncounterRow>();
        foreach (var plan in all)
        {
            var (_, _, name, path, gone) = FindPlace(places, plan);
            rows.Add(new EncounterRow(plan, name, path, gone, Summary(plan), plan.Lines.Count, plan.Status, plan.RunEncounterIds.Count));
        }

        return rows;
    }

    public async Task<EncounterView?> ViewAsync(CampaignDocument campaign, CampaignAccess access, string? route, string? culture = null)
    {
        if (await FindAsync(access, route) is not { } plan) return null;
        var reads = await ReadAsync(campaign, access, culture);
        var check = Check(campaign, plan, reads);
        var (region, place, placeName, placePath, gone) = FindPlace(reads.Regions, plan);

        var lines = plan.Lines.Select(l => Resolve(campaign, l, reads)).ToList();
        return new EncounterView(plan, lines, placeName, placePath, gone, ReadHazards(region, place, reads.Lookup), check);
    }

    /// <summary>The hazards at a place and under it, read live: the same walk the Hazards panel uses. Empty for a place that is not there, and for anyone but a GM.</summary>
    public async Task<IReadOnlyList<EncounterHazard>> HazardsAtAsync(CampaignDocument campaign, CampaignAccess access, string? regionId, string? placeId, string? culture = null)
    {
        if (!access.IsDm || string.IsNullOrEmpty(placeId)) return Array.Empty<EncounterHazard>();
        var all = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var (region, place, _, _, _) = FindPlace(all, new PreparedEncounterDocument { RegionId = regionId, PlaceId = placeId });
        return ReadHazards(region, place, await library.LookupAsync(campaign, access, culture));
    }

    private static IReadOnlyList<EncounterHazard> ReadHazards(RegionDocument? region, LocationDocument? place, Func<string, string, bool, LibraryItem?> lookup)
    {
        var hazards = new List<EncounterHazard>();
        if (region == null || place == null) return hazards;
        foreach (var at in region.Locations.Where(l => l.Feature?.Hazard != null && PlaceTree.Chain(region, l).Any(c => c.Id == place.Id)))
        {
            var stored = at.Feature!.Hazard!;
            hazards.Add(new EncounterHazard(lookup(stored.EntryType, stored.EntryId, stored.IsHomebrew)?.Entry.Name ?? stored.EntryId, stored.State, at.Name, at.Id, region.Id));
        }

        return hazards;
    }

    /// <summary>Every place of the campaign for the picker: regions in order, each region's places as a tree (parents first, <c>Depth</c> how far in).</summary>
    public async Task<IReadOnlyList<EncounterPlaceChoice>> PlacesAsync(CampaignAccess access)
    {
        if (!access.IsDm) return Array.Empty<EncounterPlaceChoice>();
        var choices = new List<EncounterPlaceChoice>();
        foreach (var region in (await regions.GetVisibleForCampaignAsync(access)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            foreach (var (place, depth) in PlaceTree.Ordered(region.Locations)) choices.Add(new EncounterPlaceChoice(region.Id, region.Name, place.Id, place.Name, depth));
        }

        return choices;
    }

    public async Task<EncounterCheck> CheckAsync(CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument plan)
    {
        if (!access.IsDm) return EncounterCheck.Refused;
        return Check(campaign, plan, await ReadAsync(campaign, access, null));
    }

    private EncounterCheck Check(CampaignDocument campaign, PreparedEncounterDocument plan, Reads reads)
    {
        var errors = new List<EncounterIssue>();
        var warnings = new List<EncounterIssue>();
        var system = library.Definition(campaign);
        var hazardActors = HazardActors(campaign);
        if (string.IsNullOrWhiteSpace(plan.Name)) errors.Add(new EncounterIssue("Encounters.Check.NoName"));

        foreach (var line in plan.Lines)
        {
            var label = string.IsNullOrWhiteSpace(line.Name) ? line.EntryId : line.Name;
            switch (line.Kind)
            {
                case PreparedLineKind.Creature:
                    if (line.Count is < 1 or > MaxCount) errors.Add(new EncounterIssue("Encounters.Check.CountRange", label, MaxCount));
                    var type = system?.FindType(line.EntryType);
                    var item = reads.Lookup(line.EntryType, line.EntryId, line.IsHomebrew);
                    // Only the system's creature types make creature lines: a hazard (or anything else) filed there by an AI proposal or a crafted import is refused.
                    if (type != null && !(type.ActorBlueprint && type.Hazard == null)) errors.Add(new EncounterIssue("Encounters.Check.HazardLine", label));
                    else if (item == null) warnings.Add(new EncounterIssue("Encounters.Warn.EntryGone", label));
                    else if (IsHazard(item.Entry, item.IsHomebrew, hazardActors)) errors.Add(new EncounterIssue("Encounters.Check.HazardLine", label));
                    break;
                case PreparedLineKind.CastMember:
                    if (line.Count != 1) errors.Add(new EncounterIssue("Encounters.Check.CastCount", label));
                    var actor = !string.IsNullOrEmpty(line.ActorId) && reads.Cast.TryGetValue(line.ActorId, out var found) ? found : null;
                    if (actor == null) warnings.Add(new EncounterIssue("Encounters.Warn.CastGone", label));
                    // A complex hazard that took a turn is an actor too, but it belongs to its place.
                    else if (actor.Hazard != null) errors.Add(new EncounterIssue("Encounters.Check.HazardLine", actor.Name));
                    // A companion or familiar is a player-character actor too: the players run them, so a plan cannot. Any other actor is the GM's to run.
                    else if (actor.Kind == ActorKind.PlayerCharacter) errors.Add(new EncounterIssue("Encounters.Check.CastIsPlayer", actor.Name));
                    break;
                default:
                    // Hazards are placed on the place, where their state lives (ENCOUNTER_BUILDER_DESIGN.md 2). A hazard line is a later step.
                    errors.Add(new EncounterIssue("Encounters.Check.HazardLine", label));
                    break;
            }
        }

        if (!string.IsNullOrEmpty(plan.PlaceId) && FindPlace(reads.Regions, plan).Gone) warnings.Add(new EncounterIssue("Encounters.Warn.PlaceGone"));
        return new EncounterCheck(errors, warnings);
    }

    /// <summary>Checks the plan and, when it has no errors, stores it. A new plan has an empty id; the repository gives it one and writes it back on <paramref name="plan"/>,
    /// and <see cref="EncounterSave.Id"/> says it. Only Draft and Ready are set here: Ran is set by starting the plan, never by a save (the repository keeps it so).</summary>
    public async Task<EncounterSave> SaveAsync(CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument plan)
    {
        if (!access.IsDm) return new EncounterSave(false, EncounterCheck.Refused, null);
        plan.CampaignId = access.CampaignId;
        plan.Name = plan.Name?.Trim() ?? string.Empty;
        plan.Notes = string.IsNullOrWhiteSpace(plan.Notes) ? null : plan.Notes;
        if (string.IsNullOrEmpty(plan.PlaceId)) plan.PlaceId = plan.RegionId = null;
        foreach (var line in plan.Lines) line.Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim();

        var check = await CheckAsync(campaign, access, plan);
        if (!check.Ok) return new EncounterSave(false, check, null);
        if (!await plans.SaveAsync(plan, access))
        {
            return new EncounterSave(false, new EncounterCheck(new[] { new EncounterIssue("Encounters.Error.NotSaved") }, check.Warnings), null);
        }

        return new EncounterSave(true, check, plan.Id);
    }

    public async Task<bool> DeleteAsync(CampaignDocument campaign, CampaignAccess access, string? route) =>
        await FindAsync(access, route) is { } plan && await plans.DeleteAsync(plan.Id, access);

    /// <summary>The Library's creatures for the add search: the types the system makes actors of, each searched as the Bestiary does, with every complex hazard left out
    /// (HAZARDS_DESIGN.md 3.3: creatures and hazards are never mixed). A hazard is placed on the place, not added here.</summary>
    public async Task<IReadOnlyList<LibraryItem>> SearchCreaturesAsync(CampaignDocument campaign, CampaignAccess access, string query, string? culture = null, int limit = 30)
    {
        if (!access.IsDm || library.Definition(campaign) is not { } system) return Array.Empty<LibraryItem>();
        var hazardActors = HazardActors(campaign);
        var types = system.EntityTypes.Where(t => t.ActorBlueprint && t.Hazard == null).Select(t => t.Type).DefaultIfEmpty("monster");
        var found = new List<LibraryItem>();
        foreach (var type in types)
        {
            foreach (var item in await library.SearchAsync(campaign, access, query, type, limit: 60, culture))
            {
                if (!IsHazard(item.Entry, item.IsHomebrew, hazardActors)) found.Add(item);
            }
        }

        return found.Take(limit).ToList();
    }

    /// <summary>The cast members a plan can name: every actor in play that is not a player character (a companion or familiar is one) and not a hazard, non-player characters first.
    /// Creatures cast from the Library are monster actors, and the GM runs them as well.</summary>
    public async Task<IReadOnlyList<ActorDocument>> CastChoicesAsync(CampaignAccess access, string query)
    {
        if (!access.IsDm) return Array.Empty<ActorDocument>();
        return (await actors.GetVisibleForCampaignAsync(access))
            .Where(a => a.Kind != ActorKind.PlayerCharacter && !a.Retired && a.Hazard == null
                        && (string.IsNullOrWhiteSpace(query) || a.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(a => a.Kind == ActorKind.NonPlayerCharacter ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The line as the Bestiary's shortlist entry gives it: one creature line, keyed the same way (<see cref="BestiaryEntry.Key"/>).</summary>
    public static PreparedLine LineFrom(BestiaryEntry entry) => new()
    {
        Kind = PreparedLineKind.Creature, EntryType = entry.EntryType, EntryId = entry.EntryId, IsHomebrew = entry.IsHomebrew, Name = entry.Name,
        Count = Math.Clamp(entry.Count, 1, MaxCount), Note = entry.Note
    };

    /// <summary>The key a creature line shares with <see cref="BestiaryEntry.Key"/>.</summary>
    public static string KeyOf(PreparedLine line) => $"{(line.IsHomebrew ? "homebrew" : "pack")}:{line.EntryType}:{line.EntryId}";

    /// <summary>The system's complex hazards as the registry holds them (actors tagged <c>hazard</c>), with the entry type each came from.</summary>
    private IReadOnlyList<(string? Type, string Id)> HazardActors(CampaignDocument campaign) =>
        registry.GetActorsBySystem(campaign.SystemId)
            .Where(a => a.Tags.Contains("hazard", StringComparer.OrdinalIgnoreCase))
            .Select(a => (Type: a.Metadata.TryGetValue("entryType", out var t) ? t?.ToString() : null, a.Id))
            .ToList();

    private static bool IsHazard(ContentEntry entry, bool isHomebrew, IReadOnlyList<(string? Type, string Id)> hazardActors) =>
        entry.Tags.Contains("hazard", StringComparer.OrdinalIgnoreCase)
        || (!isHomebrew && hazardActors.Any(h => h.Id == entry.Id && (h.Type == null || h.Type == entry.Type)));

    private static EncounterLineView Resolve(CampaignDocument campaign, PreparedLine line, Reads reads)
    {
        var basePath = $"/campaigns/{campaign.Id}";
        if (line.Kind == PreparedLineKind.CastMember)
        {
            return !string.IsNullOrEmpty(line.ActorId) && reads.Cast.TryGetValue(line.ActorId, out var actor)
                ? new EncounterLineView(line, actor.Name, true, $"{basePath}/actors/{Uri.EscapeDataString(actor.Id)}")
                : new EncounterLineView(line, line.Name, false, null);
        }

        var item = reads.Lookup(line.EntryType, line.EntryId, line.IsHomebrew);
        return item == null
            ? new EncounterLineView(line, line.Name, false, null)
            : new EncounterLineView(line, item.Entry.Name, true, $"{basePath}/library/{Uri.EscapeDataString(item.Entry.Type)}/{Uri.EscapeDataString(item.Entry.Id)}");
    }

    /// <summary>The region and place a plan names, and what to call them. <c>Gone</c> is true when it names a place that no longer exists.</summary>
    private static (RegionDocument? Region, LocationDocument? Place, string? Name, string? Path, bool Gone) FindPlace(IReadOnlyList<RegionDocument> all, PreparedEncounterDocument plan)
    {
        if (string.IsNullOrEmpty(plan.PlaceId)) return (null, null, null, null, false);
        var region = all.FirstOrDefault(r => r.Id == plan.RegionId) ?? all.FirstOrDefault(r => r.Locations.Any(l => l.Id == plan.PlaceId));
        var place = region?.Locations.FirstOrDefault(l => l.Id == plan.PlaceId);
        return region == null || place == null ? (null, null, null, null, true) : (region, place, place.Name, $"{region.Name} > {place.Name}", false);
    }

    /// <summary>"Bandit ×3, Wendel Tarr": every line by its stored name, a count after a creature that comes more than once.</summary>
    private static string Summary(PreparedEncounterDocument plan) =>
        string.Join(", ", plan.Lines.Select(l => l.Kind == PreparedLineKind.Creature && l.Count > 1 ? $"{l.Name} ×{l.Count}" : l.Name).Where(n => n.Length > 0));
}
