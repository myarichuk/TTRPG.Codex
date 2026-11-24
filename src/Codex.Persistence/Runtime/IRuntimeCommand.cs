namespace Codex.Persistence.Runtime;

/// <summary>
/// One mutation applied to a <see cref="CampaignRuntime"/>'s ECS world. Every command runs on the
/// runtime's single-writer loop (3.1) - DefaultEcs's <c>World</c> is not thread-safe, and a single
/// consumer draining a channel means no locks and no races, unlike the old global <c>CodexWorld</c>
/// singleton that every Blazor circuit mutated concurrently (B8).
/// </summary>
public interface IRuntimeCommand
{
    /// <summary>Actor ids this command touched, so the runtime knows which <see cref="ActorDocument"/>s
    /// need re-persisting after it runs.</summary>
    IReadOnlyCollection<string> AffectedActorIds { get; }

    void Apply(CampaignRuntime runtime);

    /// <summary>
    /// Turns this command into the <see cref="SessionEvent"/> appended to the live session's log
    /// (3.2). Called after <see cref="Apply"/> succeeds, against the same runtime, so it can resolve
    /// actor ids to names for a readable audit trail - the raw material a recap is generated from.
    /// </summary>
    SessionEvent Describe(CampaignRuntime runtime);
}

/// <summary>A command whose work is several of the table's usual log lines: its own event, then these, in this order and in the same session write. A prepared
/// encounter's start writes each creature's initiative line as a roll at the console would, with that creature's own actor ids, so redaction is per creature.
/// Called after <see cref="IRuntimeCommand.Describe"/>, against the same runtime.</summary>
internal interface IFollowedByEvents
{
    IReadOnlyList<SessionEvent> EventsAfter(CampaignRuntime runtime);
}

/// <summary>A change to one creature's pools or conditions (damage, healing, a condition, a death save) that the DM can take
/// back with <see cref="UndoLastChangeCommand"/> while nothing else has changed the creature since (R5).</summary>
public interface IUndoableCommand : IRuntimeCommand
{
    string ActorId { get; }

    /// <summary>The event type the change is logged as ("Damage"), which names it on the undo button.</summary>
    string UndoKind { get; }
}
