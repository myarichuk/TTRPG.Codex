using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Newtonsoft.Json.Linq;

namespace Codex.Persistence.Runtime;

/// <summary>A hazard placed on a place, as the table works on it: the Library entry resolved, and the placement as it was stored when the table read it
/// (HAZARDS_DESIGN.md 4.1, 11). The command takes it whole, because a command runs on the loop and the loop reads no repository.</summary>
public sealed record HazardSite(string RegionId, string LocationId, string Name, PlacedHazard Stored, ContentEntry Entry, ContentTypeDefinition Type);

/// <summary>What the running encounter holds of one hazard: the placement as the encounter took it (<see cref="Taken"/>) and as the table has it now
/// (<see cref="Current"/>). Kept in <c>EncounterDocument.ActiveEffects</c>, so a table that restarts finds it again.</summary>
public sealed class LiveHazard
{
    public string RegionId { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public PlacedHazard Taken { get; set; } = new();
    public PlacedHazard Current { get; set; } = new();
}

/// <summary>
/// The live state of the hazards the table has worked on, owned by the runtime's loop. While an encounter runs, a hazard's state lives in the encounter's
/// <c>ActiveEffects</c> (key <see cref="KeyOf"/>) and is written to its place when the encounter ends, unless the place's hazard was changed meanwhile or
/// nothing changed. Outside an encounter a change is written to the place at once. Both writes are patches of <c>PlaceFeature.Hazard</c> alone
/// (<see cref="IRegionRepository.PatchPlacedHazardAsync"/>), never the whole place.
/// </summary>
public sealed class HazardLedger(CampaignRuntime runtime)
{
    public const string KeyPrefix = "hazard:";

    private readonly List<(string RegionId, string LocationId, Func<PlacedHazard, PlacedHazard?> Change)> _pending = new();

    /// <summary>The key of a place's hazard in <c>EncounterDocument.ActiveEffects</c> and in the snapshot.</summary>
    public static string KeyOf(string regionId, string locationId) => $"{KeyPrefix}{regionId}/{locationId}";

    /// <summary>A copy of the placement as the table has it now: the encounter's live state when it holds one, else the placement as stored.</summary>
    public PlacedHazard Begin(HazardSite site)
    {
        if (runtime.ActiveEncounter is { } encounter
            && encounter.ActiveEffects.TryGetValue(KeyOf(site.RegionId, site.LocationId), out var raw) && Read(raw) is { } live)
        {
            return Copy(live.Current);
        }

        return Copy(site.Stored);
    }

    // The last change to each hazard, as the state just before and just after it: what the hazard's undo puts back while nothing has changed it since.
    private readonly Dictionary<string, (PlacedHazard Before, PlacedHazard After)> _undo = new(StringComparer.Ordinal);

    /// <summary>The keys of the hazards whose last change can still be offered to undo (the panel's button; <see cref="Undo"/> says whether it still can).</summary>
    public IReadOnlyCollection<string> Undoable() => _undo.Keys.ToList();

    /// <summary>Keeps <paramref name="changed"/> as the hazard's state: in the running encounter, or written to the place after the command. <paramref name="before"/> is the
    /// state the command began from, kept with it as the hazard's last change.</summary>
    public void Commit(HazardSite site, PlacedHazard before, PlacedHazard changed)
    {
        var key = KeyOf(site.RegionId, site.LocationId);
        if (!SameState(before, changed)) _undo[key] = (Copy(before), Copy(changed));
        Store(site, changed);
    }

    private void Store(HazardSite site, PlacedHazard changed)
    {
        if (runtime.ActiveEncounter is { } encounter)
        {
            var key = KeyOf(site.RegionId, site.LocationId);
            var taken = encounter.ActiveEffects.TryGetValue(key, out var raw) && Read(raw) is { } live ? live.Taken : Copy(site.Stored);
            encounter.ActiveEffects[key] = new LiveHazard { RegionId = site.RegionId, LocationId = site.LocationId, Taken = taken, Current = Copy(changed) };
            runtime.MarkEncounterDirty();
            return;
        }

        var now = Copy(changed);
        _pending.Add((site.RegionId, site.LocationId, stored => SameEntry(stored, now) ? Merge(stored, now) : null));
    }

    /// <summary>Takes back the hazard's last change, only while the hazard still stands as that change left it (the encounter's live copy, else <paramref name="site"/>'s
    /// stored placement): a hazard changed since, by the table or on the place's page, is not put back over. False when there is nothing to take back or it changed.</summary>
    public bool Undo(HazardSite site)
    {
        var key = KeyOf(site.RegionId, site.LocationId);
        if (!_undo.TryGetValue(key, out var point)) return false;
        var now = Begin(site);
        if (!SameState(now, point.After)) return false;
        _undo.Remove(key);
        Store(site, point.Before);
        return true;
    }

    /// <summary>The live copies of the running encounter's hazards, by key, for the snapshot.</summary>
    public IReadOnlyDictionary<string, PlacedHazard> Live()
    {
        var live = new Dictionary<string, PlacedHazard>(StringComparer.Ordinal);
        if (runtime.ActiveEncounter is not { } encounter) return live;
        foreach (var (key, raw) in encounter.ActiveEffects)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal) && Read(raw) is { } hazard) live[key] = Copy(hazard.Current);
        }

        return live;
    }

    /// <summary>The encounter ended: each hazard it holds that changed is queued to be written to its place, unless the place's hazard is no longer what the
    /// encounter took (the game master changed it on the place's page meanwhile: the table does not write over that). The encounter forgets them.</summary>
    public void WriteBack(EncounterDocument ended)
    {
        foreach (var key in ended.ActiveEffects.Keys.Where(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal)).ToList())
        {
            if (Read(ended.ActiveEffects[key]) is { } live && !SameState(live.Current, live.Taken))
            {
                var taken = live.Taken;
                var current = live.Current;
                _pending.Add((live.RegionId, live.LocationId, stored => SameState(stored, taken) ? Merge(stored, current) : null));
            }

            ended.ActiveEffects.Remove(key);
        }
    }

    /// <summary>Writes what the loop queued, once per command: a patch of each placement, nothing else.</summary>
    public async Task FlushAsync()
    {
        if (_pending.Count == 0) return;
        var writes = _pending.ToList();
        _pending.Clear();
        if (runtime.Places is not { } places) return;
        foreach (var (regionId, locationId, change) in writes)
        {
            await places.PatchPlacedHazardAsync(runtime.CampaignId, regionId, locationId, change);
        }
    }

    /// <summary>A live hazard as an encounter holds it: the object itself in memory, or what the database returns for it after the encounter is restored.</summary>
    internal static LiveHazard? Read(object? raw) => raw switch
    {
        LiveHazard live => live,
        JToken token => token.ToObject<LiveHazard>(),
        System.Collections.IDictionary map => JObject.FromObject(map).ToObject<LiveHazard>(),
        _ => null
    };

    public static PlacedHazard Copy(PlacedHazard hazard) => new()
    {
        Entry = hazard.Entry,
        IsHomebrew = hazard.IsHomebrew,
        Levels = hazard.Levels,
        State = hazard.State,
        Successes = hazard.Successes,
        NoticedBy = hazard.NoticedBy.ToList(),
        Found = hazard.Found.ToList()
    };

    private static bool SameEntry(PlacedHazard a, PlacedHazard b) => a.Entry == b.Entry && a.IsHomebrew == b.IsHomebrew;

    /// <summary>The same entry, version, state, count and lists (in any order).</summary>
    public static bool SameState(PlacedHazard a, PlacedHazard b) =>
        SameEntry(a, b) && a.Levels == b.Levels && a.State == b.State && a.Successes == b.Successes
        && a.NoticedBy.ToHashSet(StringComparer.Ordinal).SetEquals(b.NoticedBy) && a.Found.ToHashSet(StringComparer.Ordinal).SetEquals(b.Found);

    /// <summary>The stored placement with the table's state laid over it.</summary>
    private static PlacedHazard Merge(PlacedHazard stored, PlacedHazard now)
    {
        var merged = Copy(stored);
        merged.State = now.State;
        merged.Successes = now.Successes;
        merged.NoticedBy = now.NoticedBy.ToList();
        merged.Found = now.Found.ToList();
        return merged;
    }
}
