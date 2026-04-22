using System.Collections.Generic;
using System.Threading.Tasks;

namespace Codex.Persistence;

public interface INoteRepository
{
    Task CreateNoteAsync(NoteDocument note);
    Task DeleteNoteAsync(string id);
    Task<IEnumerable<NoteDocument>> GetNotesForTargetAsync(string campaignId, string targetId, string currentUserId, bool isDm);
    Task<IEnumerable<NoteDocument>> GetNotesByAuthorAsync(string campaignId, string authorId);
}