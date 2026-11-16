using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;

namespace Codex.Web.Components.Shared;

/// <summary>A way to find or disarm a hazard the panel offers: the route's key and what its option says (name, check and DC, or what it needs).</summary>
public sealed record HazardWay(string Key, string Label, bool SetsOff = false);

/// <summary>A hazard on a place, as the Hazards panel shows it. <see cref="Site"/> is null when the entry is gone from the Library: the row says so and offers nothing.</summary>
public sealed record HazardView(string Id, HazardSite? Site, string Name, string PlaceName, HazardState State, int Successes, IReadOnlyList<string> NoticedBy,
    IReadOnlyList<HazardWay> Detect, IReadOnlyList<HazardWay> Disarm, IReadOnlyList<string> Others, int Waiting, bool CanUndo = false,
    HazardTurn? Turn = null);

/// <summary>A complex hazard that is an actor at the table (HAZARDS_DESIGN.md 3.3): its routine, whether it is in the initiative order now and whether it is its turn, and whether
/// a routine step can be set off on creatures (the hazard has an effect).</summary>
public sealed record HazardTurn(string ActorId, IReadOnlyList<HazardStep> Routine, bool InOrder, bool ItsTurn, bool HasEffect);

/// <summary>A complex hazard the quick-add panel offers (HAZARDS_DESIGN.md 3.3): the actor it would become, the link that ties it to its placement (or to the Library alone), its
/// initiative bonus and, when it stands on a place, that place's name.</summary>
public sealed record HazardOffer(Codex.Plugin.Abstractions.IActorDefinition Definition, HazardActorLink Link, int Initiative, string? PlaceName = null);

/// <summary>A character or creature the game master can name in the panel.</summary>
public sealed record HazardWho(string Id, string Name);

/// <summary>One number the panel asks for: <see cref="Key"/> names the question (<see cref="HazardAsk.Key"/>), and the label says whose and which.</summary>
public sealed record HazardNeed(string Key, string ActorId, string ActorName, HazardAskKind Kind, string Label);

/// <summary>A place whose hazards the panel can be narrowed to.</summary>
public sealed record HazardPlace(string Id, string Label);

/// <summary>What the panel asks for: the console sends it as a <see cref="HazardCommand"/>.</summary>
public sealed record HazardRequest(HazardView Hazard, HazardAction Action, IReadOnlyList<string> ActorIds, string? Route = null,
    IReadOnlyList<string>? Routes = null, HazardState? State = null, IReadOnlyDictionary<string, int>? Bonuses = null, IReadOnlyList<string>? Reach = null);
