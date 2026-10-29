using Codex.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

public class ActorRepository(RavenDbService dbService) : IActorRepository
{
    public async Task<ActorDocument?> GetVisibleAsync(string actorId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var actor = await session.LoadAsync<ActorDocument>(actorId);
        if (actor == null || actor.CampaignId != access.CampaignId)
        {
            return null;
        }

        return CanSee(actor, access) ? Redact(actor, access, access.IsDm ? null : await OwnedActors.OfAsync(session, access)) : null;
    }

    // Copies handed to a player that are not theirs to save: a stranger's mask, and another's character read with the blocks
    // revealed to the reader's characters. Saving one would write a player's view over the DM's text; TrySaveAsync refuses.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ActorDocument, object> ReadOnly = new();

    /// <summary>
    /// The actor as <paramref name="access"/> may have it. The DM gets it whole. A player never gets the DM's private notes or
    /// the DM blocks of a history (see <see cref="SecretText"/>), and:
    /// <list type="bullet">
    /// <item>their own character's history comes without the blocks revealed to single characters, since they edit that text
    /// and saving would make such a block everyone's; the ones revealed to their characters come apart, in
    /// <see cref="ActorDocument.RevealedToReader"/>;</item>
    /// <item>someone else's history comes with the blocks revealed to the reader's characters in it (it is read, never saved);</item>
    /// <item>an <see cref="ActorVisibility.Unknown"/> actor comes as a stranger: <see cref="Mask"/>.</item>
    /// </list>
    /// </summary>
    private static ActorDocument Redact(ActorDocument actor, CampaignAccess access, IReadOnlySet<string>? owned)
    {
        if (access.IsDm)
        {
            return actor;
        }

        owned ??= new HashSet<string>();
        if (actor.OwnerUserId == access.UserId)
        {
            actor.DmNotes = null;
            actor.RevealedToReader = SecretText.RevealedOnlyTo(actor.Biography, owned) is { Length: > 0 } mine ? mine : null;
            actor.Biography = actor.Biography == null ? null : SecretText.ForPlayers(actor.Biography);
            return actor;
        }

        if (actor.Visibility == ActorVisibility.Unknown)
        {
            var mask = Mask(actor);
            ReadOnly.AddOrUpdate(mask, mask);
            return mask;
        }

        actor.DmNotes = null;
        actor.Biography = actor.Biography == null ? null : SecretText.ForPlayers(actor.Biography, owned);
        ReadOnly.AddOrUpdate(actor, actor);
        return actor;
    }

    /// <summary>
    /// What players are given of an actor the DM keeps <see cref="ActorVisibility.Unknown"/>: someone the party has met but
    /// not identified. Only the id (so mentions, quotes and the table can point at them), the kind, the name they go by
    /// (<see cref="ActorDocument.PublicName"/>, or a plain "stranger") and what the party can see of them
    /// (<see cref="ActorDocument.PublicDescription"/>, its own DM blocks taken out). Built new rather than cleared field by
    /// field, so a field added to the document later stays out of it until someone decides it belongs.
    /// </summary>
    internal static ActorDocument Mask(ActorDocument actor) => new()
    {
        Id = actor.Id,
        CampaignId = actor.CampaignId,
        Kind = actor.Kind,
        Visibility = ActorVisibility.Unknown,
        Name = actor.NameFor(isDm: false),
        PublicName = actor.NameFor(isDm: false),
        PublicDescription = string.IsNullOrWhiteSpace(actor.PublicDescription) ? null : SecretText.ForPlayers(actor.PublicDescription),
        CreatedAt = actor.CreatedAt,
        UpdatedAt = actor.UpdatedAt
    };

    public async Task<IEnumerable<ActorDocument>> GetVisibleForCampaignAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(a => a.CampaignId == access.CampaignId);

        if (!access.IsDm)
        {
            // Filters in the Raven query itself (1.4): a Hidden actor never leaves the database for a player's session,
            // rather than being loaded and filtered out in Razor after the fact. An Unknown one is masked by Redact.
            query = query.Where(a => a.Visibility != ActorVisibility.Hidden || a.OwnerUserId == access.UserId);
        }

        var actors = await query.ToListAsync();
        var owned = access.IsDm ? null : actors.Where(a => a.OwnerUserId == access.UserId).Select(a => a.Id).ToHashSet();
        return actors.Select(a => Redact(a, access, owned)).ToList();
    }

    public async Task SaveAsync(ActorDocument actor)
    {
        using var session = dbService.Store.OpenAsyncSession();

        actor.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(actor.Id))
        {
            actor.CreatedAt = DateTime.UtcNow;
        }

        await session.StoreAsync(actor);
        await session.SaveChangesAsync();
    }

    public Task<ActorDocument?> UpdateAsync(string actorId, Action<ActorDocument> mutate) =>
        RavenUpdate.RunAsync(dbService, actorId, mutate);

    public async Task<bool> TrySaveAsync(ActorDocument actor, CampaignAccess access)
    {
        if (ReadOnly.TryGetValue(actor, out _))
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var existing = string.IsNullOrEmpty(actor.Id) ? null : await session.LoadAsync<ActorDocument>(actor.Id);

        if (existing == null)
        {
            if (access.IsDm)
            {
                actor.CampaignId = access.CampaignId;
            }
            else if (actor.OwnerUserId != access.UserId || actor.CampaignId != access.CampaignId)
            {
                return false;
            }

            actor.CreatedAt = DateTime.UtcNow;
            actor.UpdatedAt = DateTime.UtcNow;
            await session.StoreAsync(actor);
            await session.SaveChangesAsync();
            return true;
        }

        if (existing.CampaignId != access.CampaignId)
        {
            return false;
        }

        if (!access.IsDm && existing.OwnerUserId != access.UserId)
        {
            return false;
        }

        // Copy onto the tracked instance (the passed object is detached): CampaignId never
        // moves, and only the DM can change ownership - a player's OwnerUserId is ignored.
        CopyEditableFields(existing, actor, access.IsDm);
        await session.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// The fields a sheet save may change, copied from <paramref name="source"/> onto
    /// <paramref name="target"/>. CampaignId never moves; only the DM can change ownership and DM
    /// notes (a player's save arrives with the notes redacted to null and leaves them alone).
    /// </summary>
    internal static void CopyEditableFields(ActorDocument target, ActorDocument source, bool isDm)
    {
        target.Name = source.Name;
        target.Kind = source.Kind;
        target.Visibility = source.Visibility;
        target.PublicName = source.PublicName;
        target.PublicDescription = source.PublicDescription;
        target.BlueprintId = source.BlueprintId;
        target.Sheet = source.Sheet;
        target.State = source.State;
        // A player edits the history they were given without its DM blocks; the blocks stay, at its end.
        target.Biography = isDm ? source.Biography : SecretText.KeepSecrets(target.Biography, source.Biography);
        target.HasPortrait = source.HasPortrait;
        target.PortraitVersion = source.PortraitVersion;
        if (isDm)
        {
            target.OwnerUserId = source.OwnerUserId;
            target.DmNotes = source.DmNotes;
            target.WikiPageId = source.WikiPageId;
        }

        target.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<bool> TryDeleteAsync(string actorId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var actor = await session.LoadAsync<ActorDocument>(actorId);
        if (actor == null || actor.CampaignId != access.CampaignId)
        {
            return false;
        }

        // The DM deletes anything; a player may retire their own player character and nothing else.
        var ownsPlayerCharacter = actor.Kind == ActorKind.PlayerCharacter && actor.OwnerUserId == access.UserId;
        if (!access.IsDm && !ownsPlayerCharacter)
        {
            return false;
        }

        session.Delete(actorId);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Where(a => a.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }

    public async Task<IEnumerable<ActorDocument>> GetRecentForOwnerAsync(string ownerUserId, int limit, bool sortByCreated)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Where(a => a.OwnerUserId == ownerUserId);

        query = sortByCreated
            ? query.OrderByDescending(a => a.CreatedAt)
            : query.OrderByDescending(a => a.UpdatedAt);

        return await query.Take(limit).ToListAsync();
    }

    private static bool CanSee(ActorDocument actor, CampaignAccess access) =>
        access.IsDm || actor.Visibility != ActorVisibility.Hidden || actor.OwnerUserId == access.UserId;
}
