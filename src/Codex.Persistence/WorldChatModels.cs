using Codex.Core.WorldBuilder;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

/// <summary>
/// One world-building conversation (docs/WORLD_BUILDER.md): what the DM and their model said, what the model looked up on the
/// way, and the proposal waiting for the DM. A DM keeps as many as they like, each with a name, and comes back to them later. They
/// are the DM's own working notes, so only their owner reads them, and like the journal they stay out of the campaign export.
/// </summary>
public class WorldChatDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>The name the chats list shows: the start of the first message until the DM renames it.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>True once the DM named the chat themselves, so the first message no longer names it.</summary>
    public bool Named { get; set; }

    /// <summary>The conversation as the model reads it, without the system prompt (made afresh each time from the campaign).</summary>
    public List<WorldChatMessage> History { get; set; } = new();

    /// <summary>The conversation as the page shows it.</summary>
    public List<WorldChatLine> Lines { get; set; } = new();

    /// <summary>The proposal waiting for the DM, with what they have ticked so far; null when there is none.</summary>
    public WorldBundle? Proposal { get; set; }

    /// <summary>The proposal as the model wrote it (JSON), so <c>add_entries</c> can put more beside it.</summary>
    public string? ProposalInput { get; set; }

    public List<string> ProposalWarnings { get; set; } = new();

    /// <summary>Counts the proposals made from scratch; the page keeps the DM's review decisions while it stays the same.</summary>
    public int ProposalEpoch { get; set; }

    /// <summary>The refs of the entries the DM has looked at and decided on, kept with the proposal.</summary>
    public List<string> Decided { get; set; } = new();

    /// <summary>How many entries of the waiting proposal are ticked, for the chats list.</summary>
    public int Waiting { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A message of the conversation as the model reads it: the DM's or the app's words, the model's reply and the tools it
/// called, or what those tools returned. Plain data, so the database stores it without knowing the AI library's types.</summary>
public class WorldChatMessage
{
    /// <summary>"user", "assistant" or "tool".</summary>
    public string Role { get; set; } = string.Empty;

    public string? Text { get; set; }
    public List<WorldChatCall>? Calls { get; set; }
    public List<WorldChatResult>? Results { get; set; }
}

public class WorldChatCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>The arguments as a JSON object.</summary>
    public string Arguments { get; set; } = "{}";
}

public class WorldChatResult
{
    public string CallId { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
}

public enum WorldChatSpeaker
{
    Dm,
    Model,
    Tool
}

/// <summary>A line of the conversation as the page shows it: the DM's message, the model's reply with the steps it took to get
/// there, or a note from the app (a proposal applied or discarded), which may carry links to what was made.</summary>
public class WorldChatLine
{
    public WorldChatLine()
    {
    }

    public WorldChatLine(WorldChatSpeaker speaker, string text, List<WorldChatStep>? steps = null, string? icon = null)
    {
        Speaker = speaker;
        Text = text;
        Steps = steps;
        Icon = icon;
    }

    public WorldChatSpeaker Speaker { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>For a note: the icon that says what kind of note it is.</summary>
    public string? Icon { get; set; }

    /// <summary>For a reply: the tools the model called on the way, in order.</summary>
    public List<WorldChatStep>? Steps { get; set; }

    /// <summary>For a reply: the model's reasoning, for models that share it (the last stretch of it when it ran long).</summary>
    public string? Reasoning { get; set; }

    /// <summary>For a reply: which model answered, what it cost and how long it took.</summary>
    public WorldChatUsage? Usage { get; set; }

    /// <summary>For a note about an applied proposal: what was made or changed.</summary>
    public List<WorldChatLink>? Links { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

public enum WorldChatStepStatus
{
    Running,
    Done,
    Failed
}

/// <summary>One tool call of a turn: what the model asked for, what it got back, and how long it took. The page shows a line for
/// it and, asked for more, the arguments and the result as they went over the wire.</summary>
public class WorldChatStep
{
    /// <summary>The tool's name, as the model calls it ("search_world").</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>The argument that says what the call was about (a query, a page's key, an entry count), for the line the page shows.</summary>
    public string? Subject { get; set; }

    /// <summary>For a tool that takes entries: how many it was given.</summary>
    public int? Count { get; set; }

    /// <summary>The arguments as JSON.</summary>
    public string Arguments { get; set; } = "{}";

    /// <summary>What the tool returned to the model (cut when very long).</summary>
    public string? Result { get; set; }

    /// <summary>The error the tool hit, when it failed; the model reads it too.</summary>
    public string? Error { get; set; }

    /// <summary>The tool worked but refused what it was given (a proposal with errors), so the model has to try again.</summary>
    public bool Rejected { get; set; }

    public WorldChatStepStatus Status { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public long DurationMs { get; set; }
}

public class WorldChatUsage
{
    public string? Model { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }

    /// <summary>How many times the model was called during the turn (once, plus once after each round of tool calls).</summary>
    public int Rounds { get; set; }

    public long DurationMs { get; set; }
}

/// <summary>Something an applied proposal made or changed, for a link in the conversation: <see cref="Kind"/> is "page", "actor",
/// "place", "homebrew" (<see cref="Id"/> is type/entryId), "lore", "bestiary" or "change-page", "change-place", "change-actor",
/// "change-homebrew".</summary>
public class WorldChatLink
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>A chat as the chats list shows it.</summary>
public class WorldChatSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public int Waiting { get; set; }
}

public interface IWorldChatRepository
{
    /// <summary>The caller's own chats in the campaign, most recent first. Nobody else's, ever.</summary>
    Task<IReadOnlyList<WorldChatSummary>> ListMineAsync(CampaignAccess access);

    /// <summary>One of the caller's own chats; null for anyone else's, or one that is gone.</summary>
    Task<WorldChatDocument?> GetMineAsync(string id, CampaignAccess access);

    /// <summary>Creates or replaces a chat the caller owns. A chat loaded with <see cref="GetMineAsync"/> (or saved here before) is
    /// replaced only if nobody saved it since: a second tab on the same chat gets <see cref="ChatSave.Changed"/>, not the last word.</summary>
    Task<ChatSave> TrySaveAsync(WorldChatDocument chat, CampaignAccess access);

    Task<bool> TryDeleteAsync(string id, CampaignAccess access);

    Task DeleteAllForCampaignAsync(string campaignId);
}

/// <summary>How saving a chat went.</summary>
public enum ChatSave
{
    Saved,

    /// <summary>Not the caller's chat. Nothing changed.</summary>
    Refused,

    /// <summary>Someone saved the chat since this copy was loaded (another tab). Nothing changed; load it again.</summary>
    Changed,

    /// <summary>The chat was deleted since this copy was loaded. Nothing changed, and it is not made again.</summary>
    Gone
}

public class WorldChatsByOwnerIndex : AbstractIndexCreationTask<WorldChatDocument>
{
    public WorldChatsByOwnerIndex()
    {
        Map = chats => from chat in chats
                       select new
                       {
                           chat.CampaignId,
                           chat.OwnerUserId,
                           chat.UpdatedAt
                       };
    }
}

public class RavenWorldChatRepository(RavenDbService dbService) : IWorldChatRepository
{
    public async Task<IReadOnlyList<WorldChatSummary>> ListMineAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        // Owner filter and projection run inside the query: another member's chats never leave the database, and the list does
        // not load whole conversations.
        return await session.Query<WorldChatDocument, WorldChatsByOwnerIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(c => c.CampaignId == access.CampaignId && c.OwnerUserId == access.UserId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new WorldChatSummary { Id = c.Id, Title = c.Title, UpdatedAt = c.UpdatedAt, Waiting = c.Waiting })
            .ToListAsync();
    }

    // The version each copy handed out (or last saved) was, so a save can tell whether it is still the latest. Kept beside the copy
    // rather than on it, so it is never stored.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldChatDocument, string> Versions = new();

    public async Task<WorldChatDocument?> GetMineAsync(string id, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var chat = await session.LoadAsync<WorldChatDocument>(id);
        if (chat == null || chat.CampaignId != access.CampaignId || chat.OwnerUserId != access.UserId)
        {
            return null;
        }

        Versions.AddOrUpdate(chat, session.Advanced.GetChangeVectorFor(chat));
        return chat;
    }

    public async Task<ChatSave> TrySaveAsync(WorldChatDocument chat, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var known = Versions.TryGetValue(chat, out var version) ? version : null;
        var existing = string.IsNullOrEmpty(chat.Id) ? null : await session.LoadAsync<WorldChatDocument>(chat.Id);
        if (existing != null && (existing.CampaignId != access.CampaignId || existing.OwnerUserId != access.UserId))
        {
            return ChatSave.Refused;
        }

        if (existing == null && known != null)
        {
            return ChatSave.Gone;
        }

        if (existing != null)
        {
            var current = session.Advanced.GetChangeVectorFor(existing);
            if (known != null && known != current)
            {
                return ChatSave.Changed;
            }

            // The chat is held whole by the page that saves it, so it replaces what is stored, as long as that is still the version
            // it was loaded at: the store below carries it, and a save that got in between fails it.
            session.Advanced.Evict(existing);
            known = current;
        }
        else
        {
            chat.Id = string.IsNullOrEmpty(chat.Id) ? "worldchats/" + Guid.NewGuid() : chat.Id;
            chat.CreatedAt = DateTime.UtcNow;
        }

        chat.CampaignId = access.CampaignId;
        chat.OwnerUserId = access.UserId;
        chat.UpdatedAt = DateTime.UtcNow;
        // A new chat is stored only if the id is still free (an empty change vector); an old one only at the version checked above.
        await session.StoreAsync(chat, known ?? string.Empty, chat.Id);
        try
        {
            await session.SaveChangesAsync();
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return ChatSave.Changed;
        }

        Versions.AddOrUpdate(chat, session.Advanced.GetChangeVectorFor(chat));
        return ChatSave.Saved;
    }

    public async Task<bool> TryDeleteAsync(string id, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var chat = await session.LoadAsync<WorldChatDocument>(id);
        if (chat == null || chat.CampaignId != access.CampaignId || chat.OwnerUserId != access.UserId)
        {
            return false;
        }

        session.Delete(chat);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var ids = await session.Query<WorldChatDocument, WorldChatsByOwnerIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(c => c.CampaignId == campaignId)
            .Select(c => c.Id)
            .ToListAsync();
        foreach (var id in ids)
        {
            session.Delete(id);
        }

        await session.SaveChangesAsync();
    }
}
