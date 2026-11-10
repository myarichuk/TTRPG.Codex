using Codex.Plugin.Abstractions;
using Codex.Localization;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

public class CampaignRepository(
    RavenDbService dbService,
    ISystemCatalog systemCatalog,
    IActorRepository actorRepository,
    ISessionRepository sessionRepository,
    INoteRepository noteRepository,
    IRegionRepository regionRepository,
    IFactRepository factRepository,
    IHomebrewRepository homebrewRepository,
    IEncounterRepository encounterRepository,
    IWikiRepository? wikiRepository = null,
    IJournalRepository? journalRepository = null,
    IWorldChatRepository? worldChatRepository = null,
    CampaignLifecycleHooks? lifecycle = null,
    ITemplateRepository? templateRepository = null,
    InviteAttempts? inviteAttempts = null,
    IBookBuildRepository? bookBuildRepository = null,
    IBookDraftRepository? bookDraftRepository = null,
    IVaultRepository? vaultRepository = null,
    ICampaignObjectsRepository? campaignObjectsRepository = null) : ICampaignRepository
{
    public async Task<IEnumerable<CampaignDocument>> GetAllAsync()
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        return await session.Query<CampaignDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<CampaignDocument>> GetForUserAsync(string userId)
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        // Membership is what the access layer checks; the owner is always a DM member (SaveAsync
        // guarantees it), so filtering on Members covers both. The filter runs in the Raven query so
        // another user's campaigns never leave the database for this session.
        return await session.Query<CampaignDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(c => c.OwnerId == userId || c.Members.Any(m => m.UserId == userId))
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<CampaignDocument?> GetAsync(string campaignId)
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CampaignDocument>(campaignId);
    }

    public async Task SaveAsync(CampaignDocument campaign)
    {
        EnsureKnownSystem(campaign);

        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();

        if (string.IsNullOrEmpty(campaign.Id))
        {
            campaign.CreatedAt = DateTime.UtcNow;
        }

        Normalize(campaign);
        await session.StoreAsync(campaign);
        await session.SaveChangesAsync();
    }

    public Task<CampaignDocument?> UpdateAsync(string campaignId, Action<CampaignDocument> mutate) =>
        RavenUpdate.RunAsync<CampaignDocument>(dbService, campaignId, campaign =>
        {
            mutate(campaign);
            EnsureKnownSystem(campaign);
            Normalize(campaign);
        });

    public Task<CampaignDocument?> UpdateAsDmAsync(CampaignAccess access, Action<CampaignDocument> mutate) =>
        UpdateAsync(access.CampaignId, campaign =>
        {
            if (CampaignAccessResolver.RoleOf(campaign, access.UserId) != CampaignRole.DM)
            {
                throw new LocalizedException("Campaign.AccessChanged");
            }

            // A co-GM may do what the GM does, except change who owns the campaign or who else is a GM in it; stepping down
            // themselves is theirs to do (docs/SERVER_ACCESS_AND_VAULT.md 4). Checked on the result, so no edit path can get round it.
            var owner = campaign.OwnerId;
            var gms = CampaignAccessResolver.GameMasters(campaign);
            mutate(campaign);
            if (string.Equals(owner, access.UserId, StringComparison.Ordinal)) return;
            var after = CampaignAccessResolver.GameMasters(campaign);
            var steppedDown = gms.Where(g => !string.Equals(g, access.UserId, StringComparison.Ordinal));
            if (!string.Equals(owner, campaign.OwnerId, StringComparison.Ordinal) || !(after.SetEquals(gms) || after.SetEquals(steppedDown)))
            {
                throw new LocalizedException("Campaign.OwnerOnly");
            }
        });

    /// <summary>The GM has seen what changed in the campaign's system: the campaign now records the system as it is loaded.</summary>
    public Task<CampaignDocument?> AcceptSystemAsync(CampaignAccess access) =>
        UpdateAsDmAsync(access, campaign =>
        {
            if (systemCatalog.GetFingerprint(campaign.SystemId) is { } fingerprint)
            {
                campaign.SystemStamp = SystemStamp.Of(campaign.SystemId, fingerprint, DateTime.UtcNow);
            }
        });

    /// <summary>1.1: reject a SystemId nothing implements. Skipped until the catalog has actually run
    /// once - refusing every save during the plugin-loading window at startup would be worse
    /// than the gap it closes.</summary>
    private void EnsureKnownSystem(CampaignDocument campaign)
    {
        if (systemCatalog.IsLoaded
            && !string.IsNullOrEmpty(campaign.SystemId)
            && !systemCatalog.LoadedSystemIds.Contains(campaign.SystemId))
        {
            throw new LocalizedException("Campaign.SystemMissing", campaign.SystemId);
        }
    }

    private void Normalize(CampaignDocument campaign)
    {
        campaign.UpdatedAt = DateTime.UtcNow;

        // The system the campaign plays under, recorded when it is first known: at creation, on a switch, and for a campaign
        // stored before stamps existed. Accepting a changed system's changes records it again (AcceptSystem).
        if ((campaign.SystemStamp == null || campaign.SystemStamp.SystemId != campaign.SystemId)
            && systemCatalog.GetFingerprint(campaign.SystemId) is { } fingerprint)
        {
            campaign.SystemStamp = SystemStamp.Of(campaign.SystemId, fingerprint, campaign.UpdatedAt);
        }

        // A campaign always has its owner as at least a DM member - membership, not OwnerId, is
        // what the access layer (1.4) actually checks.
        if (!campaign.Members.Any(m => string.Equals(m.UserId, campaign.OwnerId, StringComparison.Ordinal)))
        {
            campaign.Members.Add(new CampaignMember { UserId = campaign.OwnerId, Role = CampaignRole.DM });
        }

        if (string.IsNullOrEmpty(campaign.InviteCode))
        {
            campaign.InviteCode = InviteCodes.New();
        }
    }

    public async Task<CampaignLeaveResult> LeaveAsync(string campaignId, string userId)
    {
        var result = CampaignLeaveResult.NotMember;
        await UpdateAsync(campaignId, campaign =>
        {
            result = string.Equals(campaign.OwnerId, userId, StringComparison.Ordinal) ? CampaignLeaveResult.Owner
                : campaign.Members.RemoveAll(m => string.Equals(m.UserId, userId, StringComparison.Ordinal)) > 0 ? CampaignLeaveResult.Left
                : CampaignLeaveResult.NotMember;
        });

        // What they asked to bring in goes with them; a GM would otherwise be offered a character for nobody.
        if (result == CampaignLeaveResult.Left && vaultRepository != null)
        {
            await vaultRepository.WithdrawAllAsync(campaignId, userId);
        }

        return result;
    }

    public async Task<CampaignDeleteResult> DeleteAsync(string campaignId, string requestingUserId)
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        var campaign = await session.LoadAsync<CampaignDocument>(campaignId);
        if (campaign == null)
        {
            return CampaignDeleteResult.NotFound;
        }

        if (!string.Equals(campaign.OwnerId, requestingUserId, StringComparison.Ordinal))
        {
            return CampaignDeleteResult.Forbidden;
        }

        // Stop the live table first: otherwise its next command rewrites actors and the session into
        // a campaign that no longer exists, and every open table page keeps driving it.
        if (lifecycle != null)
        {
            await lifecycle.RunDeletingAsync(campaignId);
        }

        // B13: cascade delete everything scoped to this campaign before the campaign itself.
        await actorRepository.DeleteAllForCampaignAsync(campaignId);
        await sessionRepository.DeleteAllForCampaignAsync(campaignId);
        await noteRepository.DeleteAllForCampaignAsync(campaignId);
        await regionRepository.DeleteAllForCampaignAsync(campaignId);
        await factRepository.DeleteAllForCampaignAsync(campaignId);
        await homebrewRepository.DeleteAllForCampaignAsync(campaignId);
        await encounterRepository.DeleteAllForCampaignAsync(campaignId);
        if (wikiRepository != null) await wikiRepository.DeleteAllForCampaignAsync(campaignId);
        if (journalRepository != null) await journalRepository.DeleteAllForCampaignAsync(campaignId);
        if (worldChatRepository != null) await worldChatRepository.DeleteAllForCampaignAsync(campaignId);
        if (bookBuildRepository != null) await bookBuildRepository.DeleteAllForCampaignAsync(campaignId);
        if (bookDraftRepository != null) await bookDraftRepository.DeleteAllForCampaignAsync(campaignId);
        if (vaultRepository != null) await vaultRepository.DeleteAllRequestsForCampaignAsync(campaignId);
        if (templateRepository != null) await templateRepository.DeleteAllForCampaignAsync(campaignId);
        if (campaignObjectsRepository != null) await campaignObjectsRepository.DeleteAllForCampaignAsync(campaignId);

        // Attachments (cover art) are deleted with the document; the GM's book pictures are attachments of the book art document.
        session.Delete(BookArtDocument.IdFor(campaignId));
        session.Delete(campaign);
        await session.SaveChangesAsync();
        return CampaignDeleteResult.Deleted;
    }

    public async Task<bool> InviteCodeExistsAsync(string inviteCode)
    {
        var code = InviteCodes.Clean(inviteCode);
        if (code.Length == 0) return false;
        var campaign = await FindByInviteCodeAsync(code);
        return campaign != null && InviteCodes.Admits(campaign, code, null, Attempts.Now);
    }

    private InviteAttempts Attempts { get; } = inviteAttempts ?? new InviteAttempts();

    private async Task<CampaignDocument?> FindByInviteCodeAsync(string code)
    {
        using var lookup = dbService.Store.OpenAsyncSession();
        return await lookup.Query<CampaignDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .FirstOrDefaultAsync(c => c.InviteCode == code);
    }

    public async Task<(CampaignJoinResult Result, string? CampaignId)> JoinByInviteCodeAsync(string inviteCode, string userId)
    {
        if (!Attempts.Allows(userId))
        {
            return (CampaignJoinResult.TooManyAttempts, null);
        }

        var code = InviteCodes.Clean(inviteCode);
        var campaignId = code.Length == 0 ? null : (await FindByInviteCodeAsync(code))?.Id;
        if (campaignId == null)
        {
            Attempts.Failed(userId);
            return (CampaignJoinResult.InvalidCode, null);
        }

        // Reload-then-mutate: a settings save or the live table writing the same document at the
        // same moment must neither lose this member nor be reverted by the join. The code is checked
        // again on the stored copy, so two players racing for a one-use link cannot both get in.
        var result = CampaignJoinResult.InvalidCode;
        var updated = await RavenUpdate.RunVersionedAsync<CampaignDocument>(dbService, campaignId, (campaign, _) =>
        {
            if (!InviteCodes.Admits(campaign, code, userId, Attempts.Now))
            {
                result = CampaignJoinResult.InvalidCode;
                return false;
            }

            if (campaign.Members.Any(m => string.Equals(m.UserId, userId, StringComparison.Ordinal)))
            {
                result = CampaignJoinResult.AlreadyMember;
                return false;
            }

            campaign.Members.Add(new CampaignMember { UserId = userId, Role = CampaignRole.Player });
            if (campaign.InviteSingleUse) campaign.InviteUsedBy = userId;
            result = CampaignJoinResult.Joined;
            return true;
        });

        if (updated == null || result == CampaignJoinResult.InvalidCode)
        {
            Attempts.Failed(userId);
            return (CampaignJoinResult.InvalidCode, null);
        }

        return (result, campaignId);
    }
}
