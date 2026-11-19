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
public sealed record PageView(string Id, string Slug, string Title, string Body, bool PartyVisible, string? ActorId = null, string? PlaceId = null);

/// <summary>A cast member as the checks read it.</summary>
public sealed record ActorView(string Id, string Name, ActorRole Role, bool Retired, string? Biography, string? DmNotes, string? WikiPageId,
    IReadOnlyDictionary<string, object> Sheet);

/// <summary>A place on a region's map as the checks read it.</summary>
public sealed record PlaceView(string Id, string RegionId, string Name, string? Description, string? WikiPageId);

/// <summary>A piece of lore as the checks read it.</summary>
public sealed record FactView(string Id, string Summary, string? Details);

/// <summary>
/// Everything the checks read, taken once, as plain values. A check is a pure function of this: no database, no clock, no random, and
/// no model. The lists are sorted by id when the snapshot is made (<see cref="Create"/>), so a check that walks one sees the same order
/// whatever order the database returned its documents in; a check must still sort anything it builds itself.
/// </summary>
public sealed class WorldSnapshot
{
    private WorldSnapshot() { }

    public IReadOnlyList<PageView> Pages { get; private init; } = Array.Empty<PageView>();
    public IReadOnlyList<ActorView> Actors { get; private init; } = Array.Empty<ActorView>();
    public IReadOnlyList<PlaceView> Places { get; private init; } = Array.Empty<PlaceView>();
    public IReadOnlyList<FactView> Facts { get; private init; } = Array.Empty<FactView>();

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
        string enforcement = "off") => new()
    {
        Pages = (pages ?? Array.Empty<PageView>()).OrderBy(p => p.Id, StringComparer.Ordinal).ToList(),
        Actors = (actors ?? Array.Empty<ActorView>()).OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
        Places = (places ?? Array.Empty<PlaceView>()).OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.RegionId, StringComparer.Ordinal).ToList(),
        Facts = (facts ?? Array.Empty<FactView>()).OrderBy(f => f.Id, StringComparer.Ordinal).ToList(),
        System = system,
        Library = library ?? NullEntryResolver.Instance,
        Enforcement = enforcement
    };
}

/// <summary>One check: a pure function from a snapshot to issues. No model, no clock, no random, no I/O.</summary>
public interface IConsistencyCheck
{
    /// <summary>The check's name, as <see cref="CheckNames"/> lists it. A check that finds several kinds of issue names the family.</summary>
    string Name { get; }

    IEnumerable<Issue> Run(WorldSnapshot snapshot);
}
