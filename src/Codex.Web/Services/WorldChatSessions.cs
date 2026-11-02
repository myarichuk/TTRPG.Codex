using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>Keeps the DM's world-building conversations open for as long as their connection lasts (a turn keeps running while
/// they look at another chat), loading each from the database the first time it is opened. DM only.</summary>
public sealed class WorldChatSessions(WorldBuilderService builder, AiProviderService providers, IWorldChatRepository repository,
    ICampaignAccessResolver accessResolver) : IDisposable
{
    private readonly Dictionary<string, WorldChat> open = new();
    // Every chat made here, stored or not yet (a new one is stored when its first turn ends), so all of them can be stopped.
    private readonly List<WorldChat> made = new();

    /// <summary>The DM's chats in the campaign, most recent first.</summary>
    public async Task<IReadOnlyList<WorldChatSummary>> ListAsync(CampaignAccess access)
    {
        DmOnly(access);
        return await repository.ListMineAsync(access);
    }

    /// <summary>The chat <paramref name="id"/>, as it was left; null when it is not one of the DM's.</summary>
    public async Task<WorldChat?> OpenAsync(CampaignDocument campaign, CampaignAccess access, string id)
    {
        DmOnly(access);
        if (open.TryGetValue(id, out var chat) && chat.CampaignId == campaign.Id) return chat;
        var document = await repository.GetMineAsync(id, access);
        if (document == null) return null;
        return open[id] = Make(campaign, access, document);
    }

    /// <summary>A new chat, stored with its first message.</summary>
    public WorldChat New(CampaignDocument campaign, CampaignAccess access)
    {
        DmOnly(access);
        return Make(campaign, access, null);
    }

    /// <summary>True while a turn of the chat <paramref name="id"/> is under way, for the chats list.</summary>
    public bool IsBusy(string id) => open.TryGetValue(id, out var chat) && chat.IsBusy;

    public async Task<bool> DeleteAsync(CampaignAccess access, string id)
    {
        DmOnly(access);
        if (open.Remove(id, out var chat)) chat.Stop();
        return await repository.TryDeleteAsync(id, access);
    }

    private WorldChat Make(CampaignDocument campaign, CampaignAccess access, WorldChatDocument? document)
    {
        WorldChat? chat = null;
        chat = new WorldChat(builder, () => providers.ChatClientForAsync(access.UserId), campaign, access, document, store: async doc =>
        {
            switch (await repository.TrySaveAsync(doc, access))
            {
                case ChatSave.Saved:
                    open[doc.Id] = chat!;
                    return;
                case ChatSave.Changed:
                    // This copy is out of date: forget it, so opening the chat again loads what is stored.
                    open.Remove(doc.Id);
                    throw new LocalizedException("WorldChat.Error.ChangedElsewhere");
                case ChatSave.Gone:
                    open.Remove(doc.Id);
                    throw new LocalizedException("WorldChat.Error.Gone");
                default:
                    throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }
        }, stillDm: async () => (await accessResolver.ResolveAsync(campaign.Id, access.UserId))?.IsDm == true);
        made.Add(chat);
        return chat;
    }

    /// <summary>The connection is gone: nobody is left to read what a turn under way would bring, so it stops.</summary>
    public void Dispose()
    {
        foreach (var chat in made) chat.Stop();
    }

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }
}
