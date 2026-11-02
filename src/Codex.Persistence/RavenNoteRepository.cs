using Codex.Core;
using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenNoteRepository(RavenDbService dbService) : INoteRepository
{
    public async Task CreateNoteAsync(NoteDocument note)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(note);
        await session.SaveChangesAsync();
    }

    public async Task<bool> DeleteNoteAsync(string id, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

        using var session = dbService.Store.OpenAsyncSession();
        var note = await session.LoadAsync<NoteDocument>(id);
        if (note == null || note.CampaignId != access.CampaignId)
        {
            return false;
        }

        var campaign = await session.LoadAsync<CampaignDocument>(access.CampaignId);
        var role = campaign == null ? null : CampaignAccessResolver.RoleOf(campaign, access.UserId);
        if (role != CampaignRole.DM && (role == null || note.AuthorId != access.UserId))
        {
            return false;
        }

        session.Delete(note);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<NoteDocument>> GetNotesForTargetAsync(string targetId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();

        var query = session.Query<NoteDocument, NotesByTargetIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == access.CampaignId && x.TargetId == targetId);

        if (!access.IsDm)
        {
            // Players see public notes OR their own private notes - filtered here, not after
            // loading, so another player's private note never reaches this session (1.4).
            query = query.Where(x => x.Visibility == CommentVisibility.Public || x.AuthorId == access.UserId);
        }

        return await ForReaderAsync(session, await query.OrderByDescending(x => x.CreatedAt).ToListAsync(), access);
    }

    public async Task<IEnumerable<NoteDocument>> GetVisibleForCampaignAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();

        var query = session.Query<NoteDocument, NotesByTargetIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == access.CampaignId);

        if (!access.IsDm)
        {
            query = query.Where(x => x.Visibility == CommentVisibility.Public || x.AuthorId == access.UserId);
        }

        return await ForReaderAsync(session, await query.ToListAsync(), access);
    }

    /// <summary>Comments are written in the same boxes as pages, and a DM who pastes a paragraph with a <c>:::dm</c> block
    /// into one ships the block with it (S5). A player reads other people's comments as they read a page; their own come
    /// whole, since they wrote them (and may edit them).</summary>
    private static async Task<List<NoteDocument>> ForReaderAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, List<NoteDocument> notes, CampaignAccess access)
    {
        if (access.IsDm || !notes.Any(n => n.AuthorId != access.UserId && SecretText.HasMarkers(n.Content)))
        {
            return notes;
        }

        var owned = await OwnedActors.OfAsync(session, access);
        foreach (var note in notes.Where(n => n.AuthorId != access.UserId && SecretText.HasMarkers(n.Content)))
        {
            note.Content = SecretText.ForPlayers(note.Content, owned);
        }

        return notes;
    }

    public async Task<IEnumerable<NoteDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<NoteDocument, NotesByTargetIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();
    }

    public async Task<IEnumerable<NoteDocument>> GetNotesByAuthorAsync(string campaignId, string authorId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<NoteDocument, NotesByTargetIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId && x.AuthorId == authorId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<NoteDocument, NotesByTargetIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
