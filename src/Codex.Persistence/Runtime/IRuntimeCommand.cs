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
}
