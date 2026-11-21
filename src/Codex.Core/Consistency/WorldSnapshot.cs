using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Consistency;

/// <summary>Who an actor is to the table, for the checks that apply to some and not to others.</summary>
public enum ActorRole
{
    PlayerCharacter,
    NonPlayerCharacter,
    /// <summary>A creature from the bestiary: no history to write, no gear to pick; no check about those applies.</summary>
    Monster
}

/// <summary>A wiki page as the checks read it. Not the stored document: Core knows nothing of the database.</summary>
/// <param name="Tags">The page's tags as stored.</param>
/// <param name="Kind">The page's kind in lower case: <c>general</c>, <c>person</c>, <c>place</c>, <c>faction</c> or <c>quest</c>.</param>
public sealed record PageView(string Id, string Slug, string Title, string Body, bool PartyVisible, string? ActorId = null, string? PlaceId = null,
    IReadOnlyList<string>? Tags = null, string Kind = "general");

/// <summary>The hazard a complex hazard's actor stands for: where it stands (empty for one taken straight from the Library) and which Library entry.</summary>
public sealed record HazardLinkView(string RegionId, string LocationId, string Entry, bool IsHomebrew);

/// <summary>A cast member as the checks read it.</summary>
/// <param name="OwnerUserId">The player who owns the character; null for the DM's own.</param>
/// <param name="BlueprintId">The pack creature the actor was made from (<c>system:id</c>); null for none.</param>
/// <param name="Hazard">Set on a hazard that took a turn in initiative.</param>
public sealed record ActorView(string Id, string Name, ActorRole Role, bool Retired, string? Biography, string? DmNotes, string? WikiPageId,
    IReadOnlyDictionary<string, object> Sheet, string? OwnerUserId = null, string? BlueprintId = null, HazardLinkView? Hazard = null);

/// <summary>A place on a region's map as the checks read it.</summary>
/// <param name="ParentId">The place in the same region this one is inside; null for one straight in the region.</param>
/// <param name="HazardEntry">The Library hazard placed here as <c>type/id</c>, if any; <paramref name="HazardIsHomebrew"/> says whose.</param>
/// <param name="CreatureActorId">The cast member the place's feature names as what lives in it.</param>
public sealed record PlaceView(string Id, string RegionId, string Name, string? Description, string? WikiPageId, string? ParentId = null,
    string? HazardEntry = null, bool HazardIsHomebrew = false, string? CreatureActorId = null);

/// <summary>A piece of lore as the checks read it.</summary>
/// <param name="RelatedEntityIds">What the fact says it is about, as stored (<c>actor:id</c>, <c>place:id</c>, <c>wiki:slug</c>, <c>item:type/id</c>).</param>
/// <param name="KnownBy">The ids of the characters that know it.</param>
public sealed record FactView(string Id, string Summary, string? Details, IReadOnlyList<string>? RelatedEntityIds = null, IReadOnlyList<string>? KnownBy = null);

/// <summary>A homebrew entry as the checks read it. <paramref name="Id"/> is the document id.</summary>
public sealed record HomebrewView(string Id, string Type, string EntryId, string Name, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, object> Properties)
{
    /// <summary>The key an issue names it by: <c>type/entryId</c>.</summary>
    public string Key => $"{Type}/{EntryId}";
}

/// <summary>A creature on the campaign's Bestiary shortlist.</summary>
public sealed record BestiaryView(string EntryType, string EntryId, bool IsHomebrew, string Name);

/// <summary>
/// The campaign itself as the checks read it. <paramref name="Changes"/> are the differences, one line each, between the system as the campaign last
/// played under it and the one loaded now; <paramref name="LoadProblems"/> are what loading the system and its packs reported; both are English
/// text made by code (a file name, a version).
/// </summary>
public sealed record CampaignView(string Id, string OwnerId, string SystemId, bool SystemInstalled, IReadOnlyList<string> MemberIds,
    IReadOnlyList<string> NamedPacks, IReadOnlyList<string> KnownPacks, IReadOnlyList<string> Changes, IReadOnlyList<string> LoadProblems,
    IReadOnlyList<BestiaryView> Bestiary)
{
    public static CampaignView Empty { get; } = new("", "", "", true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<BestiaryView>());
}

/// <summary>
/// Everything the checks read, taken once, as plain values. A check is a pure function of this: no database, no clock, no random, and
/// no model. The lists are sorted by id when the snapshot is made (<see cref="Create"/>), so a check that walks one sees the same order
/// whatever order the database returned its documents in; a check must still sort anything it builds itself.
/// </summary>
public sealed class WorldSnapshot
{
    private WorldSnapshot() { }

    private SnapshotDerived? derived;

    /// <summary>What several checks derive from the same texts (the secret blocks), worked out once for this snapshot on first use and then only read.
    /// A snapshot is immutable, so the derived values are the values; they live as long as the snapshot (one run in the app), never in anything static.</summary>
    internal SnapshotDerived Derived => LazyInitializer.EnsureInitialized(ref derived, () => new SnapshotDerived(this));

    public IReadOnlyList<PageView> Pages { get; private init; } = Array.Empty<PageView>();
    public IReadOnlyList<ActorView> Actors { get; private init; } = Array.Empty<ActorView>();
    public IReadOnlyList<PlaceView> Places { get; private init; } = Array.Empty<PlaceView>();
    public IReadOnlyList<FactView> Facts { get; private init; } = Array.Empty<FactView>();

    /// <summary>The campaign's homebrew entries, sorted by document id. Empty when none were loaded (see <see cref="HomebrewLoaded"/>).</summary>
    public IReadOnlyList<HomebrewView> Homebrew { get; private init; } = Array.Empty<HomebrewView>();

    /// <summary>True when the homebrew was read. A snapshot taken for a scope that needs none leaves it out, and the checks that read it have nothing to find.</summary>
    public bool HomebrewLoaded { get; private init; }

    /// <summary>The campaign: its owner and members, system, packs and Bestiary.</summary>
    public CampaignView Campaign { get; private init; } = CampaignView.Empty;

    /// <summary>The campaign's game system, or null when it is not installed (the sheet checks then have nothing to read).</summary>
    public SystemDefinition? System { get; private init; }

    /// <summary>The Library the campaign reads entries from (packs and homebrew).</summary>
    public IEntryResolver Library { get; private init; } = NullEntryResolver.Instance;

    /// <summary>How strictly the sheet rules apply here, as <see cref="SheetRuleEngine.EffectiveEnforcement"/> says: <c>strict</c>,
    /// <c>warn</c> or <c>off</c>. <c>off</c>: the sheet rules are not reported at all.</summary>
    public string Enforcement { get; private init; } = "off";

    public static WorldSnapshot Create(
        IEnumerable<PageView>? pages = null,
        IEnumerable<ActorView>? actors = null,
        IEnumerable<PlaceView>? places = null,
        IEnumerable<FactView>? facts = null,
        SystemDefinition? system = null,
        IEntryResolver? library = null,
        string enforcement = "off",
        CampaignView? campaign = null,
        IEnumerable<HomebrewView>? homebrew = null,
        bool homebrewLoaded = false) => new()
    {
        Pages = (pages ?? Array.Empty<PageView>()).OrderBy(p => p.Id, StringComparer.Ordinal).ToList(),
        Actors = (actors ?? Array.Empty<ActorView>()).OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
        Places = (places ?? Array.Empty<PlaceView>()).OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.RegionId, StringComparer.Ordinal).ToList(),
        Facts = (facts ?? Array.Empty<FactView>()).OrderBy(f => f.Id, StringComparer.Ordinal).ToList(),
        System = system,
        Library = library ?? NullEntryResolver.Instance,
        Enforcement = enforcement,
        Campaign = campaign ?? CampaignView.Empty,
        Homebrew = (homebrew ?? Array.Empty<HomebrewView>()).OrderBy(h => h.Id, StringComparer.Ordinal).ToList(),
        HomebrewLoaded = homebrewLoaded || homebrew != null
    };
}

/// <summary>One check: a pure function from a snapshot to issues. No model, no clock, no random, no I/O.</summary>
public interface IConsistencyCheck
{
    /// <summary>The check's name, as <see cref="CheckNames"/> lists it. A check that finds several kinds of issue names the family.</summary>
    string Name { get; }

    IEnumerable<Issue> Run(WorldSnapshot snapshot);

    /// <summary>
    /// <see cref="Run(WorldSnapshot)"/> for a caller with a time budget. A check that loops over large inputs (the plain-text mention scan, the sheet
    /// rules) looks at <paramref name="cancel"/> inside its loop and throws <see cref="OperationCanceledException"/>; the token is the only thing it
    /// reads besides the snapshot, so the issues it returns are the same whenever it is not cancelled. The default runs the check as it is (the checker
    /// still looks at the token between the issues it yields). Not part of the systems' plugin surface: <c>ICodexIssueCheck</c> has a token overload of its own,
    /// which <see cref="SystemRuleCheck"/> calls with a time limit.
    /// </summary>
    IEnumerable<Issue> Run(WorldSnapshot snapshot, CancellationToken cancel) => Run(snapshot);
}
