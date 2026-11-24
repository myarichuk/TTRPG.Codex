using System.Globalization;
using System.Text;
using System.Text.Json;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

// The world builder and prepared encounters (ENCOUNTER_BUILDER_DESIGN.md 6 and 17): what the chat reads about them, the check of an encounter entry, and its save.
public sealed partial class WorldBuilderService
{
    private const string EncounterStep = "encounter";

    /// <summary>What an edit of a plan compares at apply time: the plan's last save. Starting the plan at the table also moves it (it is marked as run), so a plan started after the review is refused as changed.</summary>
    internal static string EncounterStamp(PreparedEncounterDocument plan) => Stamp(plan.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture));

    private static bool IsEncounterDraft(WorldDraft d) =>
        d.Kind == DraftKinds.Encounter || d is { Kind: DraftKinds.Edit, Target: { } t } && WorldBundle.ParseTarget(t, out _) == EditTarget.Encounter;

    /// <summary>The plans of the campaign by the GUID their key carries, with their names: what an edit or delete may name.</summary>
    internal async Task<IReadOnlyDictionary<string, string>> ExistingEncountersAsync(CampaignAccess access)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (encounters == null) return found;
        foreach (var plan in await encounters.PlansAsync(access))
        {
            var route = PreparedEncounterService.RouteOf(plan.Id);
            if (Guid.TryParse(route, out _)) found.TryAdd(route, plan.Name);
        }

        return found;
    }

    /// <summary>The GUID a key names (<c>existing:encounter:&lt;id&gt;</c>, <c>encounter:&lt;id&gt;</c> or the bare id), or null when it is not one.</summary>
    private static string? EncounterRoute(string? key)
    {
        var rest = key?.Trim() ?? string.Empty;
        if (rest.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase)) rest = rest[WorldBundle.ExistingPrefix.Length..];
        if (rest.StartsWith(WorldBundle.EncounterPrefix, StringComparison.OrdinalIgnoreCase)) rest = rest[WorldBundle.EncounterPrefix.Length..];
        return Guid.TryParse(rest, out _) ? rest : null;
    }

    private static string PlaceIdOf(string? key)
    {
        var rest = key?.Trim() ?? string.Empty;
        if (rest.StartsWith(WorldBundle.ExistingPrefix, StringComparison.OrdinalIgnoreCase)) rest = rest[WorldBundle.ExistingPrefix.Length..];
        if (rest.StartsWith(WorldBundle.PlacePrefix, StringComparison.OrdinalIgnoreCase)) rest = rest[WorldBundle.PlacePrefix.Length..];
        return rest;
    }

    /// <summary>The words of a readout in one line, or null when the system declares none: the band and the total, who it is for, and how many lines could not be counted
    /// (<paramref name="unrated"/> counts lines the rating never saw, such as a creature this proposal makes).</summary>
    internal static string? ReadoutText(EncounterRating? rating, Localizer l, int unrated = 0)
    {
        if (rating == null) return null;
        var text = DifficultyText.Summary(rating.Rating, l);
        if (rating.Source == PartySource.Default && rating.Rating.Available) text += " · " + l["Encounters.Difficulty.Source.Default", DifficultyText.Party(rating.Rating, l)];
        var notCounted = rating.Rating.NotCounted.Count + unrated;
        if (notCounted > 0) text += " · " + l["Encounters.Difficulty.NotCounted", notCounted];
        return text;
    }

    /// <summary>The name of a place by its id (or key), for a starter that names it; null when there is none.</summary>
    public async Task<string?> PlaceNameAsync(CampaignAccess access, string place)
    {
        DmOnly(access);
        var id = PlaceIdOf(place);
        return (await regions.GetVisibleForCampaignAsync(access)).SelectMany(r => r.Locations).FirstOrDefault(l => l.Id == id)?.Name;
    }

    // ---- What the chat reads ----

    /// <summary>The prepared encounters, one line each, at a place and under it when one is given (<c>existing:place:&lt;id&gt;</c>).</summary>
    public async Task<string> ListEncountersAsync(CampaignDocument campaign, CampaignAccess access, string? place)
    {
        DmOnly(access);
        if (encounters == null) return "Prepared encounters are not available.";
        var english = Localizer.For("en");
        var rows = (await encounters.ListAsync(campaign, access, "en")).ToList();
        var placeId = string.IsNullOrWhiteSpace(place) ? null : PlaceIdOf(place);
        if (placeId != null)
        {
            var inside = new HashSet<string> { placeId };
            foreach (var region in (await regions.GetVisibleForCampaignAsync(access)).Where(r => r.Locations.Any(l => l.Id == placeId)))
            {
                foreach (var under in PlaceTree.Subtree(region.Locations, placeId)) inside.Add(under.Id);
            }

            rows = rows.Where(r => r.Plan.PlaceId != null && inside.Contains(r.Plan.PlaceId)).ToList();
        }

        if (rows.Count == 0) return placeId == null ? "The campaign has no prepared encounters yet." : "No prepared encounter is set at that place or under it.";
        var lines = rows.Take(ContextCap).Select(r =>
        {
            var plan = r.Plan;
            var where = string.IsNullOrEmpty(plan.PlaceId) ? "no place" : r.PlaceGone ? "its place was deleted" : $"place existing:{WorldBundle.PlacePrefix}{plan.PlaceId} \"{r.PlaceName}\"";
            var band = DifficultyText.Word(r.Rating, english);
            return $"existing:{WorldBundle.EncounterPrefix}{PreparedEncounterService.RouteOf(plan.Id)} - {plan.Name}; {where}; lineup: {(r.Summary.Length == 0 ? "empty" : r.Summary)}; status: {StatusWord(plan)}" +
                   (band == null ? string.Empty : $"; difficulty: {band}");
        }).ToList();
        if (rows.Count > ContextCap) lines.Add($"({rows.Count - ContextCap} more; give a place to narrow the list)");
        return string.Join('\n', lines);
    }

    private static string StatusWord(PreparedEncounterDocument plan) =>
        plan.Status == PreparedStatus.Ran ? $"ran ({plan.RunEncounterIds.Count}×)" : plan.Status.ToString().ToLowerInvariant();

    private static string LineKey(PreparedLine line) =>
        line.Kind == PreparedLineKind.CastMember ? $"existing:{WorldBundle.ActorPrefix}{line.ActorId}"
        : line.IsHomebrew ? $"existing:{WorldBundle.HomebrewPrefix}{line.EntryType}/{line.EntryId}"
        : $"{line.EntryType}/{line.EntryId}";

    /// <summary>One prepared encounter in full: its lines with their keys, its notes, the place's hazards and the readout. Null when there is no such plan.</summary>
    public async Task<string?> EncounterAsync(CampaignDocument campaign, CampaignAccess access, string encounter)
    {
        DmOnly(access);
        if (encounters == null || EncounterRoute(encounter) is not { } route) return null;
        var english = Localizer.For("en");
        if (await encounters.ViewAsync(campaign, access, route, "en") is not { } view) return null;
        var plan = view.Plan;
        var sb = new StringBuilder($"# {plan.Name}\nkey: existing:{WorldBundle.EncounterPrefix}{route}; status: {StatusWord(plan)}; GM only\n");
        sb.AppendLine(string.IsNullOrEmpty(plan.PlaceId)
            ? "place: none"
            : view.PlaceGone ? "place: it was deleted" : $"place: existing:{WorldBundle.PlacePrefix}{plan.PlaceId} \"{view.PlacePath}\"");
        sb.AppendLine("lineup:");
        if (view.Lines.Count == 0) sb.AppendLine("- (empty)");
        foreach (var line in view.Lines.Take(ContextCap))
        {
            var l = line.Line;
            sb.AppendLine($"- {LineKey(l)} \"{line.Name}\" ×{l.Count}" + (string.IsNullOrWhiteSpace(l.Note) ? string.Empty : $"; note: {l.Note}") + (l.StartsHidden ? "; starts hidden" : string.Empty) + (line.Found ? string.Empty : "; no longer in the campaign"));
        }

        if (view.Lines.Count > ContextCap) sb.AppendLine($"({view.Lines.Count - ContextCap} more lines not shown)");
        if (plan.Tags.Count > 0) sb.AppendLine("tags: " + string.Join(", ", plan.Tags));
        if (view.Hazards.Count > 0)
        {
            sb.AppendLine("hazards at the place and under it:");
            foreach (var h in view.Hazards.Take(ContextCap)) sb.AppendLine("- " + HazardLine(h));
            if (view.Hazards.Count > ContextCap) sb.AppendLine($"({view.Hazards.Count - ContextCap} more hazards not shown; use place_hazards on a place inside it)");
        }

        var readout = ReadoutText(await encounters.RateAsync(campaign, access, plan, null, "en"), english);
        sb.AppendLine(readout == null ? "difficulty: this system declares no difficulty readout" : "difficulty: " + readout);
        foreach (var issue in view.Check.Errors.Concat(view.Check.Warnings).Take(ContextCap)) sb.AppendLine("problem: " + english.Get(issue.Key, issue.Args));
        sb.AppendLine().AppendLine("notes:");
        sb.Append(string.IsNullOrWhiteSpace(plan.Notes) ? "(none)" : Cap(plan.Notes));
        return sb.ToString().TrimEnd();
    }

    private static string StateWords(HazardState state) => state switch
    {
        Codex.Persistence.HazardState.Hidden => "hidden (nobody has found it)",
        Codex.Persistence.HazardState.Noticed => "noticed",
        Codex.Persistence.HazardState.Disabled => "made safe",
        _ => "went off"
    };

    private static string HazardLine(EncounterHazard h) =>
        $"{(h.IsHomebrew ? WorldBundle.ExistingPrefix + WorldBundle.HomebrewPrefix : string.Empty)}{h.Entry} \"{h.Name}\": {StateWords(h.State)}; on \"{h.PlaceName}\" existing:{WorldBundle.PlacePrefix}{h.PlaceId}; {(h.Complex ? "complex (also takes a turn in the order)" : "simple")}";

    /// <summary>The hazards at a place and under it, and the creatures its features say live there ("a mimic").</summary>
    public async Task<string> PlaceHazardsAsync(CampaignDocument campaign, CampaignAccess access, string place)
    {
        DmOnly(access);
        if (encounters == null) return "Prepared encounters are not available.";
        var id = PlaceIdOf(place);
        var region = (await regions.GetVisibleForCampaignAsync(access)).FirstOrDefault(r => r.Locations.Any(l => l.Id == id));
        if (region == null) return $"There is no place \"{place}\". Use list_places to find one.";
        var at = region.Locations.First(l => l.Id == id);
        var lines = (await encounters.HazardsAtAsync(campaign, access, region.Id, id, "en")).Select(h => "hazard " + HazardLine(h)).ToList();
        foreach (var feature in region.Locations.Where(l => l.Feature?.Creature is { Length: > 0 } && PlaceTree.Chain(region, l).Any(c => c.Id == id)))
        {
            lines.Add($"creature noted on \"{feature.Name}\" existing:{WorldBundle.PlacePrefix}{feature.Id}: {feature.Feature!.Creature}");
        }

        if (lines.Count == 0) return $"\"{at.Name}\" and the places under it have no hazards placed and no feature noting a creature.";
        var shown = lines.Take(ContextCap).ToList();
        if (lines.Count > ContextCap) shown.Add($"({lines.Count - ContextCap} more not shown; ask about a place inside it)");
        return string.Join('\n', shown);
    }

    /// <summary>The readout for a lineup that has not been proposed yet, or why it cannot be rated.</summary>
    public async Task<string> RateEncounterAsync(CampaignDocument campaign, CampaignAccess access, IReadOnlyList<ProposalCreature>? creatures)
    {
        DmOnly(access);
        var system = catalog.GetDefinition(campaign.SystemId);
        if (encounters == null || system == null) return "Prepared encounters are not available.";
        if (system.Encounters?.Difficulty == null) return "This system declares no difficulty readout, so a lineup is not rated.";
        if (creatures is not { Count: > 0 }) return "Nothing was rated: give \"creatures\", the lines of the lineup.";
        var english = Localizer.For("en");
        var everyone = (await actorRepository.GetVisibleForCampaignAsync(access)).ToList();
        var actors = everyone.Where(a => a.Kind != ActorKind.PlayerCharacter).Select(a => a.Id).ToList();
        var brewKeys = (await homebrew.GetVisibleForCampaignAsync(access)).GroupBy(h => $"{h.Type}/{h.EntryId}", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var rules = new ProposalRules(new Dictionary<string, string>(), new Dictionary<string, string>(), Array.Empty<string>(), _ => Array.Empty<FieldSpec>(),
            ExistingActors: actors, ExistingHomebrew: brewKeys, HazardType: HazardTypeOf(system), CreatureTypes: CreatureTypes(system));
        var errors = new List<string>();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(creatures, RatingJson));
        var lines = WorldBundleParser.ReadLines(json.RootElement, "creatures", rules, errors);
        errors.AddRange(lines.Where(l => l.Ref != null).Select(l => $"creatures: '{l.Entry}' names an entry of a proposal; rate a lineup of Library, homebrew and cast entries that exist already."));
        var lookup = await library.LookupAsync(campaign, access, "en");
        var plan = new PreparedEncounterDocument { Name = "Draft lineup", CampaignId = campaign.Id };
        const string pointer = "a hazard is not a creature line; place it on the place's feature (feature.hazard)";
        foreach (var line in lines.Where(l => l.Ref == null))
        {
            if (line.ActorId != null)
            {
                var actor = everyone.FirstOrDefault(a => a.Id == line.ActorId);
                if (actor == null || actor.Retired) errors.Add($"creatures: the cast member '{line.Entry}' is gone or retired.");
                else if (actor.Hazard != null) errors.Add($"creatures: '{line.Entry}' is a hazard that takes a turn; {pointer}.");
                else plan.Lines.Add(LineOf(line, lookup));
                continue;
            }

            var item = lookup(line.Type!, line.Id!, line.Homebrew);
            if (item == null)
            {
                errors.Add($"creatures: '{line.Entry}' is not in the Library; use type/id as search_library lists it.");
                continue;
            }

            if (item.Entry.Tags.Contains("hazard", StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"creatures: '{line.Entry}' is tagged as a hazard; {pointer}.");
                continue;
            }

            plan.Lines.Add(LineOf(line, lookup));
        }

        if (errors.Count > 0) return "Nothing was rated. Fix these and call rate_encounter again:\n- " + string.Join("\n- ", errors);
        var rating = await encounters.RateAsync(campaign, access, plan, null, "en");
        if (rating == null) return "This system declares no difficulty readout, so a lineup is not rated.";
        var sb = new StringBuilder(ReadoutText(rating, english));
        if (rating.Rating.Available && rating.Rating.Budgets.Count > 0) sb.Append("\nBands for this party: " + DifficultyText.Budgets(rating.Rating, english));
        foreach (var skipped in rating.Rating.NotCounted) sb.Append($"\nNot counted: {skipped.Name}: {DifficultyText.Reason(skipped.Reason, english)}");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions RatingJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // A resolved line as the plan stores it: the Library entry as it is named in the reader's language when the entry is found, else as the model wrote it.
    private static PreparedLine LineOf(DraftLine line, Func<string, string, bool, LibraryItem?>? lookup = null)
    {
        if (line.ActorId != null)
        {
            return new PreparedLine { Kind = PreparedLineKind.CastMember, ActorId = line.ActorId, Name = line.Name, Count = 1, Note = line.Note, StartsHidden = line.Hidden };
        }

        var name = line.Name.Length > 0 ? line.Name : lookup?.Invoke(line.Type!, line.Id!, line.Homebrew)?.Entry.Name ?? line.Id!;
        return new PreparedLine
        {
            Kind = PreparedLineKind.Creature, EntryType = line.Type!, EntryId = line.Id!, IsHomebrew = line.Homebrew, Name = name, Count = line.Count, Note = line.Note, StartsHidden = line.Hidden
        };
    }

    // ---- The check ----

    /// <summary>
    /// Settles what each encounter entry points at (its Library lines by name, its place, a cast member who is gone), runs the plan's own check on the plan as it would be saved
    /// (<see cref="PreparedEncounterService.CheckAsync"/>: counts, hazards, player characters; the rules are not repeated here), keeps for an edit the plan as it is now (its stamp,
    /// its place, its lineup, its notes) for the review card and for apply, and rates the lineup when the system declares a readout. A plan that is gone is an error. The readout is
    /// information, never an error.
    /// </summary>
    private async Task<(List<string> Errors, List<string> Warnings)> EncountersAsync(CampaignDocument campaign, CampaignAccess access, SystemDefinition? system, WorldBundle bundle)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var drafts = bundle.Drafts.Where(IsEncounterDraft).ToList();
        if (drafts.Count == 0) return (errors, warnings);
        if (encounters == null || system == null)
        {
            errors.AddRange(drafts.Select(d => $"{d.Ref} ({d.Kind} '{d.Name}'): prepared encounters are not available for this campaign."));
            return (errors, warnings);
        }

        var english = Localizer.For("en");
        var mine = Localizer.For(Lang);
        var allRegions = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var placeNames = allRegions.SelectMany(r => r.Locations).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var actors = (await actorRepository.GetVisibleForCampaignAsync(access)).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var stored = (await encounters.PlansAsync(access)).ToList();
        var lookup = await library.LookupAsync(campaign, access);
        var shown = await library.LookupAsync(campaign, access, Lang);
        var existingPlace = WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix;

        string? PlaceNameOf(string? place) => place == null ? null
            : place.StartsWith(existingPlace, StringComparison.OrdinalIgnoreCase) ? placeNames.GetValueOrDefault(place[existingPlace.Length..])
            : bundle.Find(place)?.Name;

        foreach (var draft in drafts)
        {
            var label = $"{draft.Ref} ({draft.Kind} '{draft.Name}')";
            PreparedEncounterDocument? plan = null;
            if (draft.Kind == DraftKinds.Edit)
            {
                WorldBundle.ParseTarget(draft.Target!, out var id);
                plan = stored.FirstOrDefault(p => string.Equals(PreparedEncounterService.RouteOf(p.Id), id, StringComparison.OrdinalIgnoreCase));
                if (plan == null)
                {
                    errors.Add($"{label}: that encounter is no longer in the campaign; use list_encounters.");
                    continue;
                }

                draft.Name = plan.Name;
                draft.Before = plan.Notes;
                draft.BeforeStamp = EncounterStamp(plan);
                draft.SharedWithPlayers = false;
                draft.BeforePlace = string.IsNullOrEmpty(plan.PlaceId) ? null : placeNames.GetValueOrDefault(plan.PlaceId);
                draft.BeforeLineup = PreparedEncounterService.Summary(plan);
                draft.BeforeStatus = plan.Status.ToString().ToLowerInvariant();
                draft.BeforeTags = new List<string>(plan.Tags);
                if (draft.Remove) continue;
                if (draft.Status != null && plan.Status == PreparedStatus.Ran) warnings.Add($"{label}: this encounter has been run, so it stays marked as run whatever status is given.");
            }

            // What each line is, found in the Library or the cast; the name the game master reads.
            foreach (var line in draft.Lineup)
            {
                if (line.ActorId != null)
                {
                    // A hazard that takes a turn is refused by the plan's own check below, with the pointer to the place's feature.
                    if (!actors.TryGetValue(line.ActorId, out var actor) || actor.Retired) errors.Add($"{label}: the cast member '{line.Entry}' is gone or retired.");
                    else line.Name = actor.Name;
                }
                else if (line.Ref == null)
                {
                    var item = lookup(line.Type!, line.Id!, line.Homebrew);
                    if (item == null) errors.Add($"{label}: '{line.Entry}' is not in the Library; use type/id as search_library lists it.");
                    else line.Name = shown(line.Type!, line.Id!, line.Homebrew)?.Entry.Name ?? item.Entry.Name;
                }
            }

            draft.PlaceName = draft.Kind == DraftKinds.Encounter || draft.PlaceGiven ? PlaceNameOf(draft.Place) : null;

            // The plan as it would be saved, as far as it is known now: what the plan's own check and the readout look at. A creature this proposal makes is not in the Library yet.
            var lines = draft.Kind == DraftKinds.Encounter || draft.LineupGiven ? draft.Lineup.Where(l => l.Ref == null).Select(l => LineOf(l, lookup)).ToList() : plan!.Lines.Select(Copy).ToList();
            var unrated = draft.Kind == DraftKinds.Encounter || draft.LineupGiven ? draft.Lineup.Count(l => l.Ref != null) : 0;
            var placeId = draft.Kind == DraftKinds.Encounter || draft.PlaceGiven
                ? draft.Place != null && draft.Place.StartsWith(existingPlace, StringComparison.OrdinalIgnoreCase) ? draft.Place[existingPlace.Length..] : null
                : plan!.PlaceId;
            var candidate = new PreparedEncounterDocument
            {
                CampaignId = campaign.Id, Name = draft.Rename ?? plan?.Name ?? draft.Name, PlaceId = placeId, Lines = lines, Status = plan?.Status ?? PreparedStatus.Draft
            };

            // Every encounter entry that is not a delete: saving runs this same check on the plan, so an edit of a plan whose stored lines no longer pass (a creature
            // gone from the Library) is refused here, not half way through an apply.
            var check = await encounters.CheckAsync(campaign, access, candidate);
            foreach (var issue in check.Errors)
            {
                var text = english.Get(issue.Key, issue.Args);
                errors.Add($"{label}: {text}" + (issue.Key == "Encounters.Check.HazardLine" ? " (A hazard goes on the place's feature, feature.hazard.)" : string.Empty));
            }

            foreach (var issue in check.Warnings) warnings.Add($"{label}: {english.Get(issue.Key, issue.Args)}");

            // The hazards at the place, for the review card: a plan set in a room with a trap should be read with it.
            draft.PlaceHazards = new List<string>();
            if (placeId != null && allRegions.FirstOrDefault(r => r.Locations.Any(l => l.Id == placeId)) is { } region)
            {
                draft.PlaceHazards = (await encounters.HazardsAtAsync(campaign, access, region.Id, placeId, Lang)).Select(h => $"{h.Name}, {h.PlaceName}").ToList();
            }

            draft.Readout = ReadoutText(await encounters.RateAsync(campaign, access, candidate, null, Lang), mine, unrated);
        }

        return (errors, warnings);

        static PreparedLine Copy(PreparedLine line) => new()
        {
            Kind = line.Kind, EntryType = line.EntryType, EntryId = line.EntryId, IsHomebrew = line.IsHomebrew, ActorId = line.ActorId, Name = line.Name, Count = line.Count, Note = line.Note, StartsHidden = line.StartsHidden
        };
    }

    // ---- The save ----

    /// <summary>Saves the new encounters, after every place, homebrew entry, Bestiary pick and cast member of the proposal is stored, so each ref is settled. A plan is a Draft unless the
    /// entry said ready, created by the game master and marked with the chat that proposed it. Nothing here starts an encounter.</summary>
    private async Task<List<PreparedEncounterDocument>> SaveEncountersAsync(CampaignDocument campaign, CampaignAccess access, WorldBundle bundle, List<WorldDraft> pending, string? chatId, Func<WorldDraft, string, Task> stored)
    {
        var saved = new List<PreparedEncounterDocument>();
        if (pending.Count == 0) return saved;
        if (encounters == null) throw new LocalizedException("WorldBuilder.Error.EncountersUnavailable");
        foreach (var draft in pending)
        {
            var plan = await PlanFromAsync(draft, bundle, campaign, access, null);
            plan.FromChatId = chatId;
            await WriteAsync(campaign, access, draft, plan);
            saved.Add(plan);
            await stored(draft, plan.Id);
        }

        return saved;
    }

    private async Task WriteAsync(CampaignDocument campaign, CampaignAccess access, WorldDraft draft, PreparedEncounterDocument plan)
    {
        var result = await encounters!.SaveAsync(campaign, access, plan);
        if (result.Saved) return;
        var said = Localizer.For(Lang);
        throw new LocalizedException("WorldBuilder.Error.EncounterRefused", draft.Name, string.Join(" ", result.Check.Errors.Select(e => said.Get(e.Key, e.Args))));
    }

    /// <summary>The plan an entry makes or changes: what the entry names replaces what the plan had (a new plan has nothing), and every ref is followed to what saving made of it.
    /// A ref that was not saved (its entry unticked, or left out of the apply) is an error that names the encounter, never a line or a place left dangling.</summary>
    private async Task<PreparedEncounterDocument> PlanFromAsync(WorldDraft draft, WorldBundle bundle, CampaignDocument campaign, CampaignAccess access, PreparedEncounterDocument? existing)
    {
        var plan = existing ?? new PreparedEncounterDocument { CampaignId = campaign.Id };
        var isNew = existing == null;
        var missing = new LocalizedException("WorldBuilder.Error.EncounterRefMissing", draft.Name);
        if (isNew || draft.Rename != null) plan.Name = (isNew ? draft.Name : draft.Rename!).Trim();
        if (isNew || draft.PlaceGiven)
        {
            plan.PlaceId = plan.RegionId = null;
            if (draft.Place is { } place)
            {
                var id = place.StartsWith(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix, StringComparison.OrdinalIgnoreCase)
                    ? place[(WorldBundle.ExistingPrefix + WorldBundle.PlacePrefix).Length..]
                    : bundle.Find(place)?.SavedAs.GetValueOrDefault("place") ?? throw missing;
                var region = (await regions.GetVisibleForCampaignAsync(access)).FirstOrDefault(r => r.Locations.Any(l => l.Id == id)) ?? throw missing;
                plan.PlaceId = id;
                plan.RegionId = region.Id;
            }
        }

        if (isNew || draft.LineupGiven)
        {
            var made = draft.Lineup.Any(l => l.Ref != null) ? (await homebrew.GetVisibleForCampaignAsync(access)).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First()) : new Dictionary<string, HomebrewEntryDocument>();
            plan.Lines = new List<PreparedLine>();
            foreach (var line in draft.Lineup)
            {
                if (line.Ref == null)
                {
                    plan.Lines.Add(LineOf(line));
                    continue;
                }

                if (bundle.Find(line.Ref)?.SavedAs.GetValueOrDefault("homebrew") is not { } entryDoc || !made.TryGetValue(entryDoc, out var entry)) throw missing;
                plan.Lines.Add(LineOf(new DraftLine { Type = entry.Type, Id = entry.EntryId, Homebrew = true, Name = entry.Name, Count = line.Count, Note = line.Note, Hidden = line.Hidden }));
            }
        }

        if (isNew || draft.Body != null) plan.Notes = draft.Body == null ? null : WorldBundleParser.ResolveLinks(draft.Body, bundle, keepLinks: false).Trim();
        if (isNew || draft.Retag) plan.Tags = new List<string>(draft.Tags);
        if (draft.Status != null) plan.Status = draft.Status == "ready" ? PreparedStatus.Ready : PreparedStatus.Draft;
        else if (isNew) plan.Status = PreparedStatus.Draft;
        return plan;
    }

    /// <summary>Changes or deletes the plan an edit names. Both are refused when the plan was saved after the review (the stamp is the plan's last save), and a plan that is gone is an error.</summary>
    private async Task<WorldEdit> ApplyEncounterEditAsync(WorldDraft draft, WorldBundle bundle, CampaignDocument campaign, CampaignAccess access, string id)
    {
        if (encounters == null) throw new LocalizedException("WorldBuilder.Error.EncountersUnavailable");
        var plan = await encounters.GetAsync(access, id) ?? throw new LocalizedException("WorldBuilder.Error.EditTargetGone", draft.Name);
        if (draft.BeforeStamp != null && draft.BeforeStamp != EncounterStamp(plan)) throw new LocalizedException("WorldBuilder.Error.EditChanged", draft.Name);
        if (draft.Remove)
        {
            // The creatures, the place and any fight already started from it are not touched.
            if (!await encounters.DeleteAsync(campaign, access, id)) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            return new WorldEdit(EditTarget.Encounter, id, plan.Name, false, Removed: true, Text: false);
        }

        await PlanFromAsync(draft, bundle, campaign, access, plan);
        await WriteAsync(campaign, access, draft, plan);
        return new WorldEdit(EditTarget.Encounter, id, plan.Name, false, Text: draft.Body != null);
    }
}
