using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

/// <summary>The characters a player plays: who a <c>:::for</c> block (see <see cref="Codex.Core.SecretText"/>) is revealed to
/// is decided by these.</summary>
internal static class OwnedActors
{
    /// <summary>The ids of the campaign's characters <paramref name="access"/>'s user owns. Empty for the DM's "view as players",
    /// whose user is no one.</summary>
    public static async Task<IReadOnlySet<string>> OfAsync(IAsyncDocumentSession session, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(access.UserId))
        {
            return new HashSet<string>();
        }

        var ids = await session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(a => a.CampaignId == access.CampaignId && a.OwnerUserId == access.UserId)
            .Select(a => a.Id)
            .ToListAsync();
        return ids.ToHashSet();
    }
}
