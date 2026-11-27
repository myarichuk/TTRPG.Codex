using System.Collections.Concurrent;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging;

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
public sealed record EncounterRow(PreparedEncounterDocument Plan, string? PlaceName, string? PlacePath, bool PlaceGone, string Summary, int LineCount, PreparedStatus Status, int RunCount,
    DifficultyRating? Rating = null)
{
    /// <summary>What a URL carries for this plan.</summary>
    public string Route => PreparedEncounterService.RouteOf(Plan.Id);
}

/// <summary>A line of a plan, resolved: the name it has now (its stored name when its entry or actor is gone) and whether it was found.</summary>
public sealed record EncounterLineView(PreparedLine Line, string Name, bool Found, string? Href);

/// <summary>A hazard placed at the plan's place or under it, read live, with the place it stands on.</summary>
/// <param name="Entry">The Library entry as <c>type/id</c> (a homebrew one too: see <paramref name="IsHomebrew"/>).</param>
/// <param name="Complex">True for a hazard that is also an actor with a turn in initiative.</param>
public sealed record EncounterHazard(string Name, HazardState State, string PlaceName, string PlaceId, string RegionId, string Entry = "", bool IsHomebrew = false, bool Complex = false)
{
    /// <summary>The key of this placement in <see cref="PreparedEncounterDocument.HazardsLeftOut"/>.</summary>
    public string Key => PreparedEncounterDocument.HazardKey(RegionId, PlaceId);

    /// <summary>Why a complex hazard would not join the order whatever the plan says (the table has it already is not known here): disarmed, or already set off. Null when it can.</summary>
    public HazardState? CannotJoin => State is HazardState.Disabled or HazardState.Triggered ? State : null;
}

/// <summary>A complex hazard to place on the plan's place when the plan is saved (ENCOUNTER_BUILDER_DESIGN.md 18): the Library entry as <c>type/id</c>, and whether it is the campaign's own.</summary>
public sealed record HazardToPlace(string Entry, bool IsHomebrew);

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
public sealed record EncounterSave(bool Saved, EncounterCheck Check, string? Id, bool HazardPlaced = false);

/// <summary>Why a prepared encounter was not started. The page words each one as <c>Combat.Prepared.Refused.{value}</c>.</summary>
public enum EncounterStartProblem
{
    NotAllowed,       // not a GM, or the campaign's system is missing
    NoTable,          // the table is not running for this campaign
    NotFound,         // no such plan in this campaign
    Invalid,          // the plan's check has errors (Check says which)
    EncounterRunning, // an encounter with participants is running ("End the current encounter first")
    Busy,             // another start of a plan in this campaign is under way
    NobodyJoined,     // no line made or named anyone who could join
    TableClosed,      // the table ended while the creatures were being made
    Failed            // the start command threw; logged
}

/// <summary>What starting a plan came to. <see cref="NotJoined"/> names, by their plan names, the lines (or copies) that did not join: an entry gone from the
/// Library, a cast member gone or retired, a creature the cast could not make, an actor the table did not hold. <see cref="RunNotRecorded"/> is true when the
/// encounter started but the plan could not be marked as run.</summary>
public sealed record EncounterStart(bool Started, EncounterStartProblem? Problem, string? EncounterId, int Joined, IReadOnlyList<string> NotJoined,
    bool RunNotRecorded = false, EncounterCheck? Check = null, int JoinedHazards = 0)
{
    public string? ProblemKey => Problem is { } p ? "Combat.Prepared.Refused." + p : null;

    public static EncounterStart Refused(EncounterStartProblem problem, EncounterCheck? check = null) => new(false, problem, null, 0, Array.Empty<string>(), false, check);
}

/// <summary>A party to rate for in place of the campaign's: a number of characters, all of one level. The readout's "Rate for a different party"; never stored.</summary>
public sealed record PartyOverride(int Size, int Level);

/// <summary>Whose party a rating is for: the campaign's characters, a <see cref="PartyOverride"/>, or a stand-in because the campaign has no characters yet.</summary>
public enum PartySource { Campaign, Override, Default }

/// <summary>A plan rated (ENCOUNTER_BUILDER_DESIGN.md 16): the rating, whose party it is for, the characters whose level is not known, the levels the "different party" form may take, and
/// what the form is filled with when it opens (a stand-in party when the campaign has no characters).</summary>
public sealed record EncounterRating(DifficultyRating Rating, PartySource Source, IReadOnlyList<string> UnknownLevels, int LevelFrom, int LevelTo, int DefaultSize, int DefaultLevel);

/// <summary>
/// The one place prepared encounters are read, checked, saved, deleted and started (ENCOUNTER_BUILDER_DESIGN.md 4.1, 4.2). GM only: every method answers nothing to anyone else, and the repository
/// refuses them besides. It also rates a lineup for the party (the difficulty readout, <see cref="RateAsync"/>).
/// </summary>
public sealed class PreparedEncounterService(IPreparedEncounterRepository plans, IRegionRepository regions, IActorRepository actors, CampaignLibrary library,
    IContentRegistry registry, CastService? cast = null, CampaignRuntimeManager? runtimes = null, ILogger<PreparedEncounterService>? logger = null, ComponentRegistry? components = null)
{
    public const int MaxCount = 20;

    /// <summary>The most characters the readout rates for.</summary>
    public const int MaxParty = 20;

    // One start per campaign at a time: a double click, or two GMs, must not make two sets of creatures. A second start is refused at once, not queued.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _starting = new(StringComparer.Ordinal);

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

    /// <summary>Every plan with its place and summary. When the system has a difficulty readout, each row also carries the rating for the campaign's party (read once for the whole list,
    /// not once per plan); with no readout, or no characters in the campaign, the rating is null.</summary>
    public async Task<IReadOnlyList<EncounterRow>> ListAsync(CampaignDocument campaign, CampaignAccess access, string? culture = null)
    {
        if (!access.IsDm) return Array.Empty<EncounterRow>();
        var all = await plans.ListAsync(access);
        var places = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        // A fault while rating must not hide the list: the rows come without a rating.
        RateKit? kit = null;
        try
        {
            kit = all.Count == 0 ? null : await KitAsync(campaign, access, culture);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Reading the party to rate the prepared encounters of campaign {CampaignId} failed", access.CampaignId);
        }

        var rows = new List<EncounterRow>();
        foreach (var plan in all)
        {
            var (_, _, name, path, gone) = FindPlace(places, plan);
            EncounterRating? rating = null;
            try
            {
                rating = kit == null ? null : RateWith(kit, plan, null);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Rating prepared encounter {Plan} failed", plan.Id);
            }

            rows.Add(new EncounterRow(plan, name, path, gone, Summary(plan), plan.Lines.Count, plan.Status, plan.RunEncounterIds.Count, rating is { Source: PartySource.Campaign } ? rating.Rating : null));
        }

        return rows;
    }

    /// <summary>Every plan as stored, the most recently changed first, with nothing resolved or rated: for a caller that only needs ids, names and stamps. Empty for anyone but a GM.</summary>
    public async Task<IReadOnlyList<PreparedEncounterDocument>> PlansAsync(CampaignAccess access) =>
        access.IsDm ? await plans.ListAsync(access) : Array.Empty<PreparedEncounterDocument>();

    public async Task<EncounterView?> ViewAsync(CampaignDocument campaign, CampaignAccess access, string? route, string? culture = null)
    {
        if (await FindAsync(access, route) is not { } plan) return null;
        var reads = await ReadAsync(campaign, access, culture);
        var check = Check(campaign, plan, reads);
        var (region, place, placeName, placePath, gone) = FindPlace(reads.Regions, plan);

        var lines = plan.Lines.Select(l => Resolve(campaign, l, reads)).ToList();
        return new EncounterView(plan, lines, placeName, placePath, gone, ReadHazards(region, place, reads.Lookup, library.Definition(campaign)), check);
    }

    /// <summary>The hazards at a place and under it, read live: the same walk the Hazards panel uses. Empty for a place that is not there, and for anyone but a GM.</summary>
    public async Task<IReadOnlyList<EncounterHazard>> HazardsAtAsync(CampaignDocument campaign, CampaignAccess access, string? regionId, string? placeId, string? culture = null)
    {
        if (!access.IsDm || string.IsNullOrEmpty(placeId)) return Array.Empty<EncounterHazard>();
        var all = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var (region, place, _, _, _) = FindPlace(all, new PreparedEncounterDocument { RegionId = regionId, PlaceId = placeId });
        return ReadHazards(region, place, await library.LookupAsync(campaign, access, culture), library.Definition(campaign));
    }

    private static IReadOnlyList<EncounterHazard> ReadHazards(RegionDocument? region, LocationDocument? place, Func<string, string, bool, LibraryItem?> lookup, SystemDefinition? system = null)
    {
        var hazards = new List<EncounterHazard>();
        if (region == null || place == null) return hazards;
        foreach (var at in region.Locations.Where(l => l.Feature?.Hazard != null && PlaceTree.Chain(region, l).Any(c => c.Id == place.Id)))
        {
            var stored = at.Feature!.Hazard!;
            var item = lookup(stored.EntryType, stored.EntryId, stored.IsHomebrew);
            var complex = item != null && system?.FindType(item.Entry.Type) is { } type && Codex.Core.Systems.HazardNumbers.IsComplex(item.Entry, type);
            hazards.Add(new EncounterHazard(item?.Entry.Name ?? stored.EntryId, stored.State, at.Name, at.Id, region.Id, stored.Entry, stored.IsHomebrew, complex));
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
    /// <param name="place">A complex hazard the editor added: it is placed on the plan's place first (one hazard per place), and the plan is saved only if that worked. A plan never
    /// holds an unplaced hazard line, so this is the whole of "adding a hazard" to a plan.</param>
    public async Task<EncounterSave> SaveAsync(CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument plan, HazardToPlace? place = null)
    {
        if (!access.IsDm) return new EncounterSave(false, EncounterCheck.Refused, null);
        plan.CampaignId = access.CampaignId;
        plan.Name = plan.Name?.Trim() ?? string.Empty;
        plan.Notes = string.IsNullOrWhiteSpace(plan.Notes) ? null : plan.Notes;
        if (string.IsNullOrEmpty(plan.PlaceId)) plan.PlaceId = plan.RegionId = null;
        foreach (var line in plan.Lines) line.Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim();

        var check = await CheckAsync(campaign, access, plan);
        if (!check.Ok) return new EncounterSave(false, check, null);
        if (place != null && await PlaceHazardAsync(campaign, access, plan, place) is { } refused)
        {
            return new EncounterSave(false, new EncounterCheck(new[] { new EncounterIssue(refused) }, check.Warnings), null);
        }

        // A left-out key that names no hazard at the place or under it (the plan moved, or the hazard was taken off the place) says nothing now: it is dropped quietly, so it
        // cannot switch off a different hazard placed there later by the same address. Hazards placed later join as every other does.
        plan.HazardsLeftOut = await TidyLeftOutAsync(access, plan);
        if (!await plans.SaveAsync(plan, access))
        {
            // The hazard may already stand on the place: the page must know, so it does not offer it again and the message does not say nothing happened.
            return new EncounterSave(false, new EncounterCheck(new[] { new EncounterIssue(place != null ? "Encounters.Error.NotSavedHazardPlaced" : "Encounters.Error.NotSaved") }, check.Warnings), null, place != null);
        }

        return new EncounterSave(true, check, plan.Id);
    }

    // ---- Complex hazards in a plan (ENCOUNTER_BUILDER_DESIGN.md 18) ----

    private static ContentTypeDefinition? HazardTypeOf(SystemDefinition? system) => system?.EntityTypes.FirstOrDefault(t => t.Hazard != null);

    /// <summary>The complex hazards that join the order when the plan starts: those placed at the plan's place or under it (the walk the Hazards panel and the quick-add panel
    /// use), not disarmed, not already set off, not left out by the plan, and without an actor at the table already. The one reading of this, so the start, the confirm text
    /// and the readout cannot differ.</summary>
    private async Task<IReadOnlyList<HazardOffer>> JoiningAsync(CampaignDocument campaign, PreparedEncounterDocument plan, Reads reads)
    {
        if (string.IsNullOrEmpty(plan.PlaceId) || library.Definition(campaign) is not { } system || HazardTypeOf(system) is not { Hazard: not null } type) return Array.Empty<HazardOffer>();
        var (region, place, _, _, _) = FindPlace(reads.Regions, plan);
        if (region == null || place == null) return Array.Empty<HazardOffer>();
        var offers = await HazardOffers.ForPlaceAsync(new[] { region }, place.Id, system, type, reads.Cast.Values,
            stored => Task.FromResult(reads.Lookup(stored.EntryType, stored.EntryId, stored.IsHomebrew)?.Entry));
        var left = (plan.HazardsLeftOut ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
        return offers
            .Where(o => !left.Contains(PreparedEncounterDocument.HazardKey(o.Link.RegionId, o.Link.LocationId))
                        && region.Locations.FirstOrDefault(l => l.Id == o.Link.LocationId)?.Feature?.Hazard?.State != HazardState.Triggered)
            .ToList();
    }

    /// <summary>The names of the complex hazards that would join the order if the stored or edited <paramref name="plan"/> started now. Empty for anyone but a GM.</summary>
    public async Task<IReadOnlyList<string>> JoiningHazardsAsync(CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument plan)
    {
        if (!access.IsDm) return Array.Empty<string>();
        return (await JoiningAsync(campaign, plan, await ReadAsync(campaign, access, null))).Select(o => o.Definition.Name).ToList();
    }

    /// <summary>The same for several plans, reading the regions, the cast and the Library once: the names that would join, by plan id. Empty for anyone but a GM.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> JoiningHazardsAsync(CampaignDocument campaign, CampaignAccess access, IEnumerable<PreparedEncounterDocument> forPlans)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var list = forPlans.Where(p => !string.IsNullOrEmpty(p.PlaceId)).ToList();
        if (!access.IsDm || list.Count == 0) return found;
        var reads = await ReadAsync(campaign, access, null);
        foreach (var plan in list) found[plan.Id] = (await JoiningAsync(campaign, plan, reads)).Select(o => o.Definition.Name).ToList();
        return found;
    }

    /// <summary>The Library's and the campaign's own complex hazards for the editor's Hazards group, split from the creatures by the same test the quick-add panel uses
    /// (a hazard is never a creature): the entries of the system's hazard type whose <c>complex</c> field is true. Empty for anyone but a GM.</summary>
    public async Task<IReadOnlyList<LibraryItem>> SearchHazardsAsync(CampaignDocument campaign, CampaignAccess access, string query, string? culture = null, int limit = 30)
    {
        if (!access.IsDm || HazardTypeOf(library.Definition(campaign)) is not { } type) return Array.Empty<LibraryItem>();
        var found = await library.SearchAsync(campaign, access, query, type.Type, limit: 200, culture);
        return found.Where(i => HazardNumbers.IsComplex(i.Entry, type)).Take(limit).ToList();
    }

    /// <summary>Places a complex hazard on the plan's place, the way the Places form does (the place's own hazard, state Hidden). Null when it was placed, else the key of why not.</summary>
    private async Task<string?> PlaceHazardAsync(CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument plan, HazardToPlace hazard)
    {
        if (string.IsNullOrEmpty(plan.PlaceId) || string.IsNullOrEmpty(plan.RegionId)) return "Encounters.Error.HazardNoPlace";
        var system = library.Definition(campaign);
        if (HazardTypeOf(system) is not { } type) return "Encounters.Error.HazardNotComplex";
        var entry = new PlacedHazard { Entry = hazard.Entry, IsHomebrew = hazard.IsHomebrew, State = HazardState.Hidden };
        if (entry.EntryType != type.Type || string.IsNullOrEmpty(entry.EntryId)) return "Encounters.Error.HazardGone";
        if (await library.GetAsync(campaign, access, entry.EntryType, entry.EntryId, entry.IsHomebrew) is not { } item) return "Encounters.Error.HazardGone";
        if (!HazardNumbers.IsComplex(item.Entry, type)) return "Encounters.Error.HazardNotComplex";

        // A patch of the place's hazard alone, made only when it has none, so one hazard per place holds even when the table or another tab placed one meanwhile, and the rest of the
        // place is never rewritten. Idempotent: the same hazard already there counts as placed, so a retry after the plan's own save failed goes through.
        return await regions.PlaceHazardIfNoneAsync(plan.RegionId, plan.PlaceId, entry, access) switch
        {
            HazardPlacing.Placed or HazardPlacing.AlreadyThere => null,
            HazardPlacing.Taken => "Encounters.Error.HazardTaken",
            _ => "Encounters.Error.HazardPlaceGone"
        };
    }

    /// <summary>The plan's left-out keys that still name a hazard placed at its place or under it, once each, in the order they were kept.</summary>
    private async Task<List<string>> TidyLeftOutAsync(CampaignAccess access, PreparedEncounterDocument plan)
    {
        if (plan.HazardsLeftOut == null || plan.HazardsLeftOut.Count == 0) return new List<string>();
        var (region, place, _, _, _) = FindPlace((await regions.GetVisibleForCampaignAsync(access)).ToList(), plan);
        var placed = PlacedKeys(region, place);
        return plan.HazardsLeftOut.Where(placed.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The keys of every hazard placed at <paramref name="place"/> or under it, as <see cref="PreparedEncounterDocument.HazardsLeftOut"/> names them.</summary>
    public static HashSet<string> PlacedKeys(RegionDocument? region, LocationDocument? place) =>
        region == null || place == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : region.Locations.Where(l => l.Feature?.Hazard != null && PlaceTree.Chain(region, l).Any(c => c.Id == place.Id))
                .Select(l => PreparedEncounterDocument.HazardKey(region.Id, l.Id)).ToHashSet(StringComparer.Ordinal);

    public async Task<bool> DeleteAsync(CampaignDocument campaign, CampaignAccess access, string? route) =>
        await FindAsync(access, route) is { } plan && await plans.DeleteAsync(plan.Id, access);

    /// <summary>One plan by its stored id, for the table's notes panel. Null for anyone but a GM.</summary>
    public Task<PreparedEncounterDocument?> GetAsync(CampaignAccess access, string id) => FindAsync(access, RouteOf(id));

    /// <summary>How many actors a plan puts in the order: each creature line's count, and one for each cast-member line.</summary>
    public static int CombatantsOf(PreparedEncounterDocument plan) =>
        plan.Lines.Sum(l => l.Kind == PreparedLineKind.Creature ? Math.Max(0, l.Count) : l.Kind == PreparedLineKind.CastMember ? 1 : 0);

    /// <summary>The plans the table's menu offers (ENCOUNTER_BUILDER_DESIGN.md 5.2): Ready and Ran, never Draft. Those at the place last used come first; within each
    /// group, by name. The place last used is <paramref name="currentPlaceId"/> (the live encounter's place) when there is one, else the place of the plan started
    /// most recently (<see cref="PreparedEncounterDocument.LastRunAt"/>). Empty for anyone but a GM.</summary>
    public async Task<IReadOnlyList<EncounterRow>> StartChoicesAsync(CampaignDocument campaign, CampaignAccess access, string? currentPlaceId, string? culture = null)
    {
        var rows = (await ListAsync(campaign, access, culture)).Where(r => r.Status != PreparedStatus.Draft).ToList();
        var last = !string.IsNullOrEmpty(currentPlaceId)
            ? currentPlaceId
            : rows.Where(r => r.Plan.LastRunAt != null).OrderByDescending(r => r.Plan.LastRunAt).FirstOrDefault()?.Plan.PlaceId;
        return rows
            .OrderBy(r => !(last != null && r.Plan.PlaceId == last))
            .ThenBy(r => r.Plan.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Starts a plan at the table (ENCOUNTER_BUILDER_DESIGN.md 4.2): its creatures are made on the cast (Unknown, or Hidden for a line that starts hidden),
    /// its cast members are named as they are, and on the loop the encounter takes the plan's place and everyone rolls initiative. Then the plan is marked as run.
    /// Any status may be started (the table's menu leaves Drafts out). <paramref name="id"/> is a plan id or its route.</summary>
    public async Task<EncounterStart> StartAsync(CampaignDocument campaign, CampaignAccess access, string id)
    {
        if (!access.IsDm || access.SystemMissing) return EncounterStart.Refused(EncounterStartProblem.NotAllowed);
        if (cast == null || runtimes?.TryGet(access.CampaignId) == null) return EncounterStart.Refused(EncounterStartProblem.NoTable);

        var plan = await FindAsync(access, RouteOf(id));
        if (plan == null) return EncounterStart.Refused(EncounterStartProblem.NotFound);

        // The table can end during any await, so the runtime is never taken as there.
        EncounterStartProblem? Early() =>
            runtimes.TryGet(access.CampaignId) is not { } live ? EncounterStartProblem.NoTable
            : live.Snapshot.Encounter is { Participants.Count: > 0 } ? EncounterStartProblem.EncounterRunning
            : null;

        if (Early() is { } early) return EncounterStart.Refused(early);

        var gate = _starting.GetOrAdd(access.CampaignId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return EncounterStart.Refused(EncounterStartProblem.Busy);
        try
        {
            var reads = await ReadAsync(campaign, access, null);
            var check = Check(campaign, plan, reads);
            if (!check.Ok) return EncounterStart.Refused(EncounterStartProblem.Invalid, check);
            if (Early() is { } later) return EncounterStart.Refused(later);

            var system = library.Definition(campaign);
            var created = new List<string>();
            var ids = new List<string>();
            var notJoined = new List<string>();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);

            var creatures = plan.Lines.Where(l => l.Kind == PreparedLineKind.Creature).ToList();
            // Numbers are per creature, and a visible copy's number is its public name, so they must say nothing about the hidden ones: the visible copies are numbered
            // first, and only when there is more than one of them. Hidden copies take the numbers after (their names are the GM's alone), whatever order the lines are in.
            var visible = creatures.Where(l => !l.StartsHidden).GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.Sum(l => l.Count));
            var totals = creatures.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.Sum(l => l.Count));
            var nextVisible = totals.Keys.ToDictionary(k => k, _ => 1);
            var nextHidden = totals.Keys.ToDictionary(k => k, k => visible.GetValueOrDefault(k) + 1);
            foreach (var line in plan.Lines)
            {
                if (line.Kind == PreparedLineKind.Creature)
                {
                    var key = KeyOf(line);
                    var item = reads.Lookup(line.EntryType, line.EntryId, line.IsHomebrew);
                    if (item == null)
                    {
                        notJoined.Add(line.Name);
                        continue;
                    }

                    int? from = line.StartsHidden
                        ? totals[key] > 1 ? nextHidden[key] : null
                        : visible[key] > 1 ? nextVisible[key] : null;
                    IReadOnlyList<ActorDocument> made;
                    try
                    {
                        made = await cast.AddToCastAsync(item, campaign, system, access, line.Count, line.StartsHidden ? ActorVisibility.Hidden : ActorVisibility.Unknown, from);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Creating {Creature} for prepared encounter {Plan} failed", line.Name, plan.Id);
                        notJoined.Add(line.Name);
                        continue;
                    }

                    if (line.StartsHidden) nextHidden[key] += line.Count;
                    else nextVisible[key] += line.Count;
                    foreach (var actor in made)
                    {
                        created.Add(actor.Id);
                        ids.Add(actor.Id);
                        names[actor.Id] = actor.Name;
                    }

                    if (line.Count - made.Count > 0) notJoined.Add(line.Name);
                }
                else if (line.Kind == PreparedLineKind.CastMember)
                {
                    if (!string.IsNullOrEmpty(line.ActorId) && reads.Cast.TryGetValue(line.ActorId, out var member) && !member.Retired)
                    {
                        ids.Add(member.Id);
                        names[member.Id] = member.Name;
                    }
                    else
                    {
                        notJoined.Add(line.Name);
                    }
                }
            }

            // The complex hazards placed at the place or under it that join the order (JoiningAsync says which). Each gets its actor through the one helper the console's quick-add
            // uses; the command joins it on the loop through JoinInitiativeCommand. An actor this start made is cleaned up with the creatures when the start does not happen.
            var hazardJoins = new List<PreparedHazardJoin>();
            IReadOnlyList<HazardOffer> offers;
            try
            {
                offers = await JoiningAsync(campaign, plan, reads);
            }
            catch (Exception ex)
            {
                // A hazard entry that cannot be read must not leave this start's creatures on the cast with no start behind them.
                logger?.LogError(ex, "Reading the hazards of prepared encounter {Plan} failed", plan.Id);
                await CleanUpAsync();
                return EncounterStart.Refused(EncounterStartProblem.Failed);
            }

            foreach (var offer in offers)
            {
                ActorDocument? actor = null;
                try
                {
                    if (components == null)
                    {
                        notJoined.Add(offer.Definition.Name);
                        continue;
                    }

                    actor = HazardOffers.ToActor(campaign.Id, offer, system, components);
                    if (!await runtimes.TryCreateActorAsync(actor, access))
                    {
                        notJoined.Add(offer.Definition.Name);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Creating the actor of {Hazard} for prepared encounter {Plan} failed", offer.Definition.Name, plan.Id);
                    // It may have been saved before the table failed to take it: if it is stored it is one this start made, so it is cleaned up with the rest.
                    try
                    {
                        if (actor != null && await actors.GetVisibleAsync(actor.Id, access) != null) created.Add(actor.Id);
                    }
                    catch (Exception)
                    {
                        // The store cannot be read either; the actor is taken as not made.
                    }

                    notJoined.Add(offer.Definition.Name);
                    continue;
                }

                created.Add(actor!.Id);
                names[actor.Id] = actor.Name;
                hazardJoins.Add(new PreparedHazardJoin(actor.Id, offer.Initiative));
            }

            async Task CleanUpAsync()
            {
                // Only what this start made: a cast member it reused is never touched.
                foreach (var actorId in created)
                {
                    try
                    {
                        await runtimes.TryDeleteActorAsync(actorId, access);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Removing {Actor} after a prepared encounter did not start failed", actorId);
                    }
                }
            }

            EncounterStart Stopped(EncounterStartProblem problem, IEnumerable<string>? skipped = null) =>
                new(false, problem, null, 0, notJoined.Concat((skipped ?? []).Select(s => names.GetValueOrDefault(s, s))).ToList());

            if (ids.Count == 0 && hazardJoins.Count == 0)
            {
                await CleanUpAsync();
                return Stopped(EncounterStartProblem.NobodyJoined);
            }

            var runtime = runtimes.TryGet(access.CampaignId);
            if (runtime == null)
            {
                await CleanUpAsync();
                return Stopped(EncounterStartProblem.TableClosed);
            }

            var (_, place, placeName, _, _) = FindPlace(reads.Regions, plan);
            var command = new StartPreparedEncounterCommand(plan.Id, plan.Name, place?.Id, placeName ?? string.Empty, ids, access.UserId, hazardJoins);
            try
            {
                await runtime.EnqueueAsync(command);
            }
            catch (Exception ex)
            {
                // The loop reports any later failure (the persists, the session write) as a failed enqueue, though the command has already changed the table. Once the
                // command has started the encounter, its creatures are in the order: they are kept and the start goes on, whatever came after.
                if (command.EncounterId == null)
                {
                    if (ex is not InvalidOperationException) logger?.LogError(ex, "Starting prepared encounter {Plan} failed", plan.Id);
                    await CleanUpAsync();
                    return Stopped(ex is InvalidOperationException ? EncounterStartProblem.TableClosed : EncounterStartProblem.Failed);
                }

                logger?.LogWarning(ex, "Prepared encounter {Plan} started, but the table reported a failure afterwards", plan.Id);
            }

            if (command.Refusal is { } refusal)
            {
                await CleanUpAsync();
                return Stopped(refusal == PreparedStartRefusal.EncounterRunning ? EncounterStartProblem.EncounterRunning : EncounterStartProblem.NobodyJoined, command.Skipped.Concat(command.SkippedHazards));
            }

            // A hazard that did not join is no creature the GM would keep on the cast: its actor goes, so its place offers it again. It is named to the GM like a creature.
            foreach (var skipped in command.SkippedHazards)
            {
                try
                {
                    await runtimes.TryDeleteActorAsync(skipped, access);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Removing {Actor}, a hazard that did not join, failed", skipped);
                }
            }

            notJoined.AddRange(command.Skipped.Concat(command.SkippedHazards).Select(s => names.GetValueOrDefault(s, s)));
            var marked = false;
            try
            {
                marked = await plans.MarkRunAsync(plan.Id, command.EncounterId!, access);
            }
            catch (Exception ex)
            {
                // The encounter is live; a plan that could not be marked is told to the GM, not reported as a failed start.
                logger?.LogWarning(ex, "Marking prepared encounter {Plan} as run failed", plan.Id);
            }

            if (!marked) logger?.LogWarning("Prepared encounter {Plan} started but could not be marked as run", plan.Id);
            return new EncounterStart(true, null, command.EncounterId, command.Joined.Count + command.JoinedHazards.Count, notJoined, !marked, null, command.JoinedHazards.Count);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- The difficulty readout (ENCOUNTER_BUILDER_DESIGN.md 2.1 and 16) ----

    /// <summary>What a rating reads once, however many plans it rates: the system's rules (their labels in the reader's language), the cast, the Library and the party's characters.</summary>
    private sealed record RateKit(SystemDefinition System, DifficultyRules Rules, IReadOnlyDictionary<string, ActorDocument> Cast, Func<string, string, bool, LibraryItem?> Lookup,
        IReadOnlyList<ActorDocument> Party, string ValueKey, string LevelKey, string ValueLabel);

    /// <summary>Rates a plan (the saved one, or the page's unsaved copy) against the campaign's party, or against <paramref name="party"/> when given. Null when the system declares no
    /// difficulty block, and for anyone but a GM. The rules' labels (bands, unit) are worded in <paramref name="culture"/>.</summary>
    public async Task<EncounterRating?> RateAsync(CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument plan, PartyOverride? party = null, string? culture = null)
    {
        var kit = await KitAsync(campaign, access, culture);
        if (kit == null) return null;
        // The complex hazards that would join are listed apart in the readout ("not counted": no system declares a hazard's value), by name.
        IReadOnlyList<string> hazards = Array.Empty<string>();
        if (!string.IsNullOrEmpty(plan.PlaceId) && HazardTypeOf(kit.System) != null)
        {
            var reads = new Reads((await regions.GetVisibleForCampaignAsync(access)).ToList(), kit.Cast, kit.Lookup);
            hazards = (await JoiningAsync(campaign, plan, reads)).Select(o => o.Definition.Name).ToList();
        }

        return RateWith(kit, plan, party, hazards);
    }

    private async Task<RateKit?> KitAsync(CampaignDocument campaign, CampaignAccess access, string? culture)
    {
        if (!access.IsDm || library.Definition(campaign) is not { Encounters.Difficulty: not null } system) return null;
        var shown = system.Localize(culture);
        var rules = shown.Encounters!.Difficulty!;
        var all = (await actors.GetVisibleForCampaignAsync(access)).GroupBy(a => a.Id).Select(g => g.First()).ToList();
        // A companion or familiar is a player-character actor too, but not one of the party; a retired character is out of play.
        var party = all.Where(a => a.Kind == ActorKind.PlayerCharacter && !a.Retired && Codex.Core.Rules.SheetLayout.IsPartyMember(system, a.Sheet)).ToList();
        var valueKey = (rules.Shape == DifficultyRules.ShapeSum ? rules.CreatureValue : rules.CreatureLevel) ?? string.Empty;
        var label = shown.EntityTypes.Where(t => t.ActorBlueprint).SelectMany(t => t.Fields).FirstOrDefault(f => f.Key == valueKey)?.Label;
        return new RateKit(system, rules, all.ToDictionary(a => a.Id), await library.LookupAsync(campaign, access, culture), party, valueKey,
            (string.IsNullOrWhiteSpace(rules.PartyLevel) ? system.FieldRoles.Level : rules.PartyLevel) ?? string.Empty, string.IsNullOrWhiteSpace(label) ? valueKey : label);
    }

    private EncounterRating RateWith(RateKit kit, PreparedEncounterDocument plan, PartyOverride? party, IReadOnlyList<string>? hazards = null)
    {
        var rules = kit.Rules;
        var (from, to) = LevelRange(kit);
        var byLevel = rules.Shape == DifficultyRules.ShapeByLevel;
        var defaultSize = Math.Clamp(byLevel ? rules.PartySize ?? 4 : 4, 1, MaxParty);
        var unknown = new List<string>();
        List<int> levels;
        PartySource source;
        if (party != null)
        {
            levels = Enumerable.Repeat(party.Level, Math.Clamp(party.Size, 1, MaxParty)).ToList();
            source = PartySource.Override;
        }
        else if (kit.Party.Count == 0)
        {
            levels = Enumerable.Repeat(from, defaultSize).ToList();
            source = PartySource.Default;
        }
        else
        {
            levels = new List<int>();
            foreach (var character in kit.Party)
            {
                if (LevelOf(character, kit.LevelKey) is { } level) levels.Add(level);
                else unknown.Add(character.Name);
            }

            source = PartySource.Campaign;
            if (unknown.Count > 0)
            {
                return new EncounterRating(DifficultyRating.None(new DifficultyIssue("Encounters.Difficulty.LevelUnknown", string.Join(", ", unknown), unknown.Count)), source, unknown, from, to, defaultSize, from);
            }
        }

        var lines = plan.Lines.Select(l => RatedLineOf(kit, l)).Concat((hazards ?? Array.Empty<string>()).Select(h => new RatedLine(h, 1, null, null, Hazard: true))).ToList();
        return new EncounterRating(EncounterDifficulty.Rate(rules, levels, lines), source, unknown, from, to, defaultSize, from);
    }

    /// <summary>The levels the "different party" form offers: a <c>sum</c> block's first and last budget row; for <c>byLevel</c>, which has no rows, the level field's own bounds (1 to 20 when it has none).</summary>
    private static (int From, int To) LevelRange(RateKit kit)
    {
        if (kit.Rules.Shape == DifficultyRules.ShapeSum)
        {
            var levels = kit.Rules.Budget.Select(r => int.TryParse(r.GetValueOrDefault("level"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : (int?)null)
                .OfType<int>().ToList();
            if (levels.Count > 0) return (levels.Min(), levels.Max());
        }

        var field = kit.System.ActorFields.FirstOrDefault(f => f.Key == kit.LevelKey);
        return ((int)(field?.Min ?? 1), (int)(field?.Max ?? 20));
    }

    /// <summary>A character's level from their sheet: a whole number, or null (missing, or not a number).</summary>
    private static int? LevelOf(ActorDocument character, string key) =>
        character.Sheet.TryGetValue(key, out var raw) && YamlValues.ToNumber(raw) is { } number && number == Math.Floor(number) && Math.Abs(number) < int.MaxValue ? (int)number : null;

    private RatedLine RatedLineOf(RateKit kit, PreparedLine line)
    {
        var key = kit.ValueKey;
        var name = string.IsNullOrWhiteSpace(line.Name) ? line.EntryId : line.Name;
        switch (line.Kind)
        {
            case PreparedLineKind.Hazard:
                return new RatedLine(name, 1, null, Hazard: true);
            case PreparedLineKind.CastMember:
            {
                if (string.IsNullOrEmpty(line.ActorId) || !kit.Cast.TryGetValue(line.ActorId, out var actor))
                {
                    return new RatedLine(name, 1, null, new DifficultyIssue("Encounters.Difficulty.NoActor", name));
                }

                // The sheet first (a creature made from a stat block has its properties on it); else the Library entry it was made from, when it was made from a pack's.
                var value = actor.Sheet.TryGetValue(key, out var onSheet) ? YamlValues.ToNumber(onSheet) : null;
                if (value == null && !string.IsNullOrEmpty(actor.BlueprintId) && registry.GetActor(actor.BlueprintId) is { } blueprint)
                {
                    value = blueprint.Properties.TryGetValue(key, out var onBlueprint) ? YamlValues.ToNumber(onBlueprint) : null;
                }

                return new RatedLine(actor.Name, 1, value, value == null ? new DifficultyIssue("Encounters.Difficulty.NoValue", actor.Name, kit.ValueLabel) : null);
            }
            default:
            {
                var item = kit.Lookup(line.EntryType, line.EntryId, line.IsHomebrew);
                if (item == null) return new RatedLine(name, Math.Max(0, line.Count), null, new DifficultyIssue("Encounters.Difficulty.NoEntry", name));
                var value = item.Entry.Properties.TryGetValue(key, out var raw) ? YamlValues.ToNumber(raw) : null;
                return new RatedLine(item.Entry.Name, Math.Max(0, line.Count), value, value == null ? new DifficultyIssue("Encounters.Difficulty.NoValue", item.Entry.Name, kit.ValueLabel) : null);
            }
        }
    }

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
    public static string Summary(PreparedEncounterDocument plan) =>
        string.Join(", ", plan.Lines.Select(l => l.Kind == PreparedLineKind.Creature && l.Count > 1 ? $"{l.Name} ×{l.Count}" : l.Name).Where(n => n.Length > 0));
}
