using Codex.Core;
using Codex.Localization;
using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenVaultRepository(RavenDbService dbService) : IVaultRepository
{
    public const int MaxName = 120;

    public async Task<IReadOnlyList<ActorDocument>> GetAllAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return Array.Empty<ActorDocument>();
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(a => a.CampaignId == string.Empty && a.OwnerUserId == userId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync();
    }

    public async Task<ActorDocument?> GetAsync(string actorId, string userId)
    {
        if (string.IsNullOrEmpty(actorId) || string.IsNullOrEmpty(userId)) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var actor = await session.LoadAsync<ActorDocument>(actorId);
        return actor != null && IsOwnVault(actor, userId) ? DocumentVersions.Track(session, actor) : null;
    }

    private static bool IsOwnVault(ActorDocument actor, string userId) =>
        actor.IsVault && !string.IsNullOrEmpty(actor.SystemId) && string.Equals(actor.OwnerUserId, userId, StringComparison.Ordinal);

    private static string CleanName(string? name, string fallback)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length > MaxName ? clean[..MaxName] : clean;
    }

    public async Task<ActorDocument> CreateAsync(string userId, string systemId, string name, Action<ActorDocument>? fill = null)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(systemId)) throw new ArgumentException("A vault character needs an owner and a system.");
        var actor = new ActorDocument { Name = name };
        fill?.Invoke(actor);
        actor.Id = Guid.NewGuid().ToString();
        actor.CampaignId = string.Empty;
        actor.SystemId = systemId;
        actor.OwnerUserId = userId;
        actor.Kind = ActorKind.PlayerCharacter;
        actor.VaultSourceId = null;
        actor.Name = CleanName(actor.Name, "New character");
        actor.CreatedAt = DateTime.UtcNow;
        actor.UpdatedAt = DateTime.UtcNow;
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(actor);
        await session.SaveChangesAsync();
        return DocumentVersions.Track(session, actor);
    }

    public async Task<bool> SaveAsync(ActorDocument actor, string userId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var stored = string.IsNullOrEmpty(actor.Id) ? null : await session.LoadAsync<ActorDocument>(actor.Id);
        if (stored == null || !IsOwnVault(stored, userId)) return false;

        stored.Name = CleanName(actor.Name, stored.Name);
        stored.Sheet = new Dictionary<string, object>(actor.Sheet);
        stored.Biography = string.IsNullOrWhiteSpace(actor.Biography) ? null : actor.Biography;
        stored.UpdatedAt = DateTime.UtcNow;
        if (await DocumentVersions.SaveIfUnchangedAsync(session, stored, actor) == SaveOutcome.Changed)
        {
            throw new LocalizedException("Actor.Sheet.ChangedMeanwhile");
        }

        return true;
    }

    public async Task<bool> DeleteAsync(string actorId, string userId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var stored = string.IsNullOrEmpty(actorId) ? null : await session.LoadAsync<ActorDocument>(actorId);
        if (stored == null || !IsOwnVault(stored, userId)) return false;

        // Waiting requests for it go too: there is nothing left to bring in.
        var requests = await session.Query<CharacterRequestDocument, CharacterRequestsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(r => r.VaultActorId == actorId)
            .ToListAsync();
        foreach (var request in requests) session.Delete(request);
        session.Delete(stored);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<CharacterRequestDocument?> AskAsync(CampaignAccess access, CharacterRequestDocument request)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var campaign = await session.LoadAsync<CampaignDocument>(current.CampaignId);
        var vault = await session.LoadAsync<ActorDocument>(request.VaultActorId);
        if (campaign == null || vault == null || !IsOwnVault(vault, current.UserId)
            || !string.Equals(vault.SystemId, campaign.SystemId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (request.ReplacesActorId != null)
        {
            var copy = await session.LoadAsync<ActorDocument>(request.ReplacesActorId);
            if (copy == null || copy.CampaignId != current.CampaignId || copy.Retired
                || !string.Equals(copy.OwnerUserId, current.UserId, StringComparison.Ordinal) || copy.VaultSourceId != vault.Id)
            {
                return null;
            }
        }

        var earlier = await session.Query<CharacterRequestDocument, CharacterRequestsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(r => r.CampaignId == current.CampaignId && r.VaultActorId == vault.Id)
            .ToListAsync();
        foreach (var old in earlier) session.Delete(old);

        var stored = new CharacterRequestDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = current.CampaignId,
            UserId = current.UserId,
            VaultActorId = vault.Id,
            ReplacesActorId = request.ReplacesActorId,
            Name = CleanName(request.Name, vault.Name),
            Sheet = new Dictionary<string, object>(request.Sheet),
            // A player cannot write secret blocks; any typed in the vault come in as the text a player reads.
            Biography = string.IsNullOrWhiteSpace(request.Biography) ? null : SecretText.ForPlayers(request.Biography),
            LeftOut = request.LeftOut.Distinct().ToList(),
            AskedFor = request.AskedFor.Distinct().ToList(),
            CreatedAt = DateTime.UtcNow
        };
        await session.StoreAsync(stored);
        await session.SaveChangesAsync();
        return stored;
    }

    public async Task<IReadOnlyList<CharacterRequestDocument>> GetRequestsAsync(CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return Array.Empty<CharacterRequestDocument>();
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<CharacterRequestDocument, CharacterRequestsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(r => r.CampaignId == current.CampaignId);
        if (!current.IsDm) query = query.Where(r => r.UserId == current.UserId);
        return await query.OrderBy(r => r.CreatedAt).ToListAsync();
    }

    public async Task<bool> WithdrawAsync(string requestId, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        using var session = dbService.Store.OpenAsyncSession();
        var request = await session.LoadAsync<CharacterRequestDocument>(requestId);
        if (request == null || request.CampaignId != current.CampaignId
            || !(current.IsDm || string.Equals(request.UserId, current.UserId, StringComparison.Ordinal)))
        {
            return false;
        }

        session.Delete(request);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<(CharacterRequestDocument Request, ActorDocument? Added)?> AcceptAsync(string requestId, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { IsDm: true } current) return null;
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var request = await session.LoadAsync<CharacterRequestDocument>(requestId);
        var campaign = request == null ? null : await session.LoadAsync<CampaignDocument>(request.CampaignId);
        if (request == null || campaign == null || request.CampaignId != current.CampaignId) return null;

        session.Delete(request);
        if (CampaignAccessResolver.RoleOf(campaign, request.UserId) == null)
        {
            // The player left while it waited: nothing to bring in for them.
            await session.SaveChangesAsync();
            return null;
        }

        ActorDocument? added = null;
        if (request.ReplacesActorId == null)
        {
            added = new ActorDocument
            {
                Id = Guid.NewGuid().ToString(),
                CampaignId = campaign.Id,
                Kind = ActorKind.PlayerCharacter,
                OwnerUserId = request.UserId,
                VaultSourceId = request.VaultActorId,
                Name = request.Name,
                Sheet = new Dictionary<string, object>(request.Sheet),
                Biography = request.Biography,
                Visibility = ActorVisibility.Known,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await session.StoreAsync(added);
        }

        try
        {
            await session.SaveChangesAsync();
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // Accepted twice at once (two GMs, or a double click): the first one brought it in.
            return null;
        }

        return (request, added);
    }

    public async Task<bool> UpdateFromCopyAsync(string vaultActorId, string userId, string name, Dictionary<string, object> sheet, string? biography)
    {
        var allowed = false;
        await RavenUpdate.RunAsync<ActorDocument>(dbService, vaultActorId, stored =>
        {
            allowed = IsOwnVault(stored, userId);
            if (!allowed) return;
            stored.Name = CleanName(name, stored.Name);
            stored.Sheet = new Dictionary<string, object>(sheet);
            stored.Biography = string.IsNullOrWhiteSpace(biography) ? null : SecretText.ForPlayers(biography);
            stored.UpdatedAt = DateTime.UtcNow;
        });
        return allowed;
    }

    public Task DeleteAllRequestsForCampaignAsync(string campaignId) => DeleteRequestsAsync(campaignId, null);

    public Task WithdrawAllAsync(string campaignId, string userId) =>
        string.IsNullOrEmpty(userId) ? Task.CompletedTask : DeleteRequestsAsync(campaignId, userId);

    private async Task DeleteRequestsAsync(string campaignId, string? userId)
    {
        if (string.IsNullOrEmpty(campaignId)) return;
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<CharacterRequestDocument, CharacterRequestsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(r => r.CampaignId == campaignId);
        if (userId != null) query = query.Where(r => r.UserId == userId);
        var requests = await query.ToListAsync();
        foreach (var request in requests) session.Delete(request);
        await session.SaveChangesAsync();
    }
}
