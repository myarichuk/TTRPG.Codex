using Codex.Core;
using Raven.Client.Documents;

namespace Codex.Persistence;

public class FactRepository(RavenDbService dbService) : IFactRepository
{
    public async Task<IEnumerable<FactDocument>> GetVisibleForCampaignAsync(CampaignAccess access, IReadOnlySet<string> ownedActorIds)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var facts = await session.Query<FactDocument, FactsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(f => f.CampaignId == access.CampaignId)
            .ToListAsync();

        if (access.IsDm)
        {
            return facts;
        }

        var visible = facts.Where(f => f.Visibility == FactVisibility.Public ||
            (f.Visibility == FactVisibility.KnowersOnly && f.KnownBy.Any(k => ownedActorIds.Contains(k.EntityId)))).ToList();
        foreach (var fact in visible)
        {
            RedactForPlayer(fact, ownedActorIds);
        }

        return visible;
    }

    // Facts handed to a player with their secret blocks taken out. One of these is never saved, which would write the
    // player's copy over the DM's text; as with wiki pages, only the DM saves, so this only guards the future.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FactDocument, object> Redacted = new();

    /// <summary>The fact as a player owning <paramref name="ownedActorIds"/> reads it: Details without the blocks they may not
    /// read, and links only to what is left, so neither "Lore about" nor search finds a fact by a name inside a DM block.</summary>
    private static void RedactForPlayer(FactDocument fact, IReadOnlySet<string> ownedActorIds)
    {
        if (SecretText.HasMarkers(fact.Details))
        {
            var details = SecretText.ForPlayersMarked(fact.Details, ownedActorIds);
            fact.Details = string.IsNullOrWhiteSpace(details) ? null : details;
        }

        fact.RelatedEntityIds = FactLinks.Of(fact.Summary, fact.Details).ToList();
        Redacted.AddOrUpdate(fact, fact);
    }

    public async Task<bool> SaveAsync(FactDocument fact, CampaignAccess access)
    {
        if (!access.IsDm || Redacted.TryGetValue(fact, out _))
        {
            return false;
        }

        fact.Summary = (fact.Summary ?? string.Empty).Trim();
        fact.Details = string.IsNullOrWhiteSpace(fact.Details) ? null : fact.Details.Trim();
        fact.RelatedEntityIds = FactLinks.Of(fact.Summary, fact.Details).ToList();
        if (!string.IsNullOrEmpty(fact.Id))
        {
            // An edit changes the text only, on the fact as stored: who knows it and who may read it change through
            // AddKnowerAsync, RemoveKnowerAsync and SetVisibilityAsync, so an edit open while a knower was added keeps the knower.
            var wrongCampaign = false;
            var updated = await RavenUpdate.RunAsync<FactDocument>(dbService, fact.Id, existing =>
            {
                wrongCampaign = existing.CampaignId != access.CampaignId;
                if (wrongCampaign)
                {
                    return;
                }

                existing.Summary = fact.Summary;
                existing.Details = fact.Details;
                existing.RelatedEntityIds = fact.RelatedEntityIds;
                existing.Metadata = fact.Metadata;
            });
            if (updated != null)
            {
                return !wrongCampaign;
            }
        }

        fact.CampaignId = access.CampaignId;
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(fact, string.IsNullOrEmpty(fact.Id) ? null : fact.Id);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task RestoreAsync(FactDocument fact)
    {
        fact.RelatedEntityIds = FactLinks.Of(fact.Summary, fact.Details).ToList();
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(fact, string.IsNullOrEmpty(fact.Id) ? null : fact.Id);
        await session.SaveChangesAsync();
    }

    public async Task<bool> DeleteAsync(string factId, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var fact = await session.LoadAsync<FactDocument>(factId);
        if (fact == null || fact.CampaignId != access.CampaignId)
        {
            return false;
        }

        session.Delete(fact);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveKnowerAsync(string factId, string entityId, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var fact = await session.LoadAsync<FactDocument>(factId);
        if (fact == null || fact.CampaignId != access.CampaignId || fact.KnownBy.RemoveAll(k => k.EntityId == entityId) == 0)
        {
            return false;
        }

        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetVisibilityAsync(string factId, FactVisibility visibility, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var fact = await session.LoadAsync<FactDocument>(factId);
        if (fact == null || fact.CampaignId != access.CampaignId)
        {
            return false;
        }

        fact.Visibility = visibility;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> AddKnowerAsync(string factId, KnowerEntry knower, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var fact = await session.LoadAsync<FactDocument>(factId);
        if (fact == null || fact.CampaignId != access.CampaignId)
        {
            return false;
        }

        fact.KnownBy.RemoveAll(k => k.EntityId == knower.EntityId);
        fact.KnownBy.Add(knower);
        if (fact.Visibility == FactVisibility.DmOnly)
        {
            fact.Visibility = FactVisibility.KnowersOnly;
        }

        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<FactDocument, FactsByCampaignIndex>()
            .Where(f => f.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
