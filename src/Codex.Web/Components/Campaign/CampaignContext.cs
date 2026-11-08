using Codex.Localization;
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
    /// <summary>The actors in play, for lists and pickers: retired ones (D17) are left out.</summary>
    public required IReadOnlyList<ActorDocument> Actors { get; init; }

    /// <summary>Every actor this caller may see, retired ones too: what names a mention, a reveal or a linked page.</summary>
    public required IReadOnlyList<ActorDocument> AllActors { get; init; }
    public required Func<Task> ReloadAsync { get; init; }
    public required Action<Flash> Notify { get; init; }

    /// <summary>Says the caller's role in the campaign was taken away after the page resolved it, and reloads the page, which
    /// resolves it again (D15).</summary>
    public required Func<Task> AccessChangedAsync { get; init; }

    /// <summary>Runs a change the store checks against the caller's role as the campaign has it now. False, after
    /// <see cref="AccessChangedAsync"/>, when it was refused because that role changed since the page loaded.</summary>
    public async Task<bool> WithCurrentRoleAsync(Func<Task> change)
    {
        try
        {
            await change();
            return true;
        }
        catch (LocalizedException ex) when (ex.Key == "Campaign.AccessChanged")
        {
            await AccessChangedAsync();
            return false;
        }
    }

    public bool IsDm => Access.IsDm;

    /// <summary>The campaign's game system is not installed: read-only (<see cref="CampaignAccess.SystemMissing"/>).</summary>
    public bool SystemMissing => Access.SystemMissing;
    public string BasePath => $"/campaigns/{Campaign.Id}";
    public IReadOnlySet<string> OwnedActorIds => AllActors.Where(a => a.OwnerUserId == UserId).Select(a => a.Id).ToHashSet();
}
