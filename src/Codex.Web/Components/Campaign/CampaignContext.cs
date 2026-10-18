using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Web.Components.Campaign;

/// <summary>
/// Everything a campaign section needs from the workspace shell: the campaign, the caller's
/// resolved access, the system definition, the actors this caller may see, and callbacks to reload
/// or show a status message. Cascaded by <c>CampaignPage</c>.
/// </summary>
public sealed class CampaignContext
{
    public required CampaignDocument Campaign { get; init; }
    public required CampaignAccess Access { get; init; }
    public required string UserId { get; init; }
    public SystemDefinition? System { get; init; }
    public required IReadOnlyList<ActorDocument> Actors { get; init; }
    public required Func<Task> ReloadAsync { get; init; }
    public required Action<Flash> Notify { get; init; }

    public bool IsDm => Access.IsDm;
    public string BasePath => $"/campaigns/{Campaign.Id}";
    public IReadOnlySet<string> OwnedActorIds => Actors.Where(a => a.OwnerUserId == UserId).Select(a => a.Id).ToHashSet();
}
