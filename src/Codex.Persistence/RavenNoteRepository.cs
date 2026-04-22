using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    public async Task<IEnumerable<NoteDocument>> GetNotesForTargetAsync(string campaignId, string targetId, string currentUserId, bool isDm)
    {
        using var session = dbService.Store.OpenAsyncSession();

        var query = session.Query<NoteDocument>()
            .Where(x => x.CampaignId == campaignId && x.TargetId == targetId);

        if (!isDm)
        {
            // Players see public notes OR their own private notes
            query = query.Where(x => x.Visibility == CommentVisibility.Public || x.AuthorId == currentUserId);
        }

        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync();
    }

    public async Task<IEnumerable<NoteDocument>> GetNotesByAuthorAsync(string campaignId, string authorId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<NoteDocument>()
            .Where(x => x.CampaignId == campaignId && x.AuthorId == authorId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }
}