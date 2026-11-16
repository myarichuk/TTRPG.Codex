using Codex.Persistence;
using Codex.Persistence.Runtime;

namespace Codex.Web.Components.Shared;

/// <summary>A way to find or disarm a hazard the panel offers: the route's key and what its option says (name, check and DC, or what it needs).</summary>
public sealed record HazardWay(string Key, string Label);

/// <summary>A hazard on a place, as the Hazards panel shows it. <see cref="Site"/> is null when the entry is gone from the Library: the row says so and offers nothing.</summary>
public sealed record HazardView(string Id, HazardSite? Site, string Name, string PlaceName, HazardState State, int Successes, IReadOnlyList<string> NoticedBy,
    IReadOnlyList<HazardWay> Detect, IReadOnlyList<HazardWay> Disarm, IReadOnlyList<string> Others, int Waiting, bool CanUndo = false);

/// <summary>A character or creature the game master can name in the panel.</summary>
public sealed record HazardWho(string Id, string Name);

/// <summary>One number the panel asks for: <see cref="Key"/> names the question (<see cref="HazardAsk.Key"/>), and the label says whose and which.</summary>
public sealed record HazardNeed(string Key, string ActorId, string ActorName, HazardAskKind Kind, string Label);

/// <summary>A place whose hazards the panel can be narrowed to.</summary>
public sealed record HazardPlace(string Id, string Label);

/// <summary>What the panel asks for: the console sends it as a <see cref="HazardCommand"/>.</summary>
public sealed record HazardRequest(HazardView Hazard, HazardAction Action, IReadOnlyList<string> ActorIds, string? Route = null,
    IReadOnlyList<string>? Routes = null, HazardState? State = null, IReadOnlyDictionary<string, int>? Bonuses = null);
