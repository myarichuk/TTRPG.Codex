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

    public async Task DeleteNoteAsync(string id)
    {
        using var session = dbService.Store.OpenAsyncSession();
        session.Delete(id);
        await session.SaveChangesAsync();
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

        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync();
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
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
