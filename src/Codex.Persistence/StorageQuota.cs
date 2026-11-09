using Codex.Localization;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

/// <summary>
/// The bytes of every picture and file kept on a campaign's documents, per campaign: the campaign's own (its cover and Library art),
/// and those of every document that names the campaign (portraits, homebrew and location art, wiki pictures, book pictures, the book's
/// fetched stylesheet files). Read from the attachments' sizes, never their bytes, and over every collection, so a document kind added
/// later is counted without a change here.
/// </summary>
public class StorageByCampaignIndex : AbstractJavaScriptIndexCreationTask
{
    public sealed class Result
    {
        public string CampaignId { get; set; } = string.Empty;
        public long Bytes { get; set; }
    }

    public StorageByCampaignIndex()
    {
        Maps = new HashSet<string>
        {
            """
            map('@all_docs', function (doc) {
                var files = attachmentsFor(doc);
                if (!files || files.length === 0) return null;
                var campaign = doc.CampaignId || (getMetadata(doc)['@collection'] === 'CampaignDocuments' ? id(doc) : null);
                if (!campaign) return null;
                var bytes = 0;
                for (var i = 0; i < files.length; i++) bytes += files[i].Size;
                return { CampaignId: campaign, Bytes: bytes };
            })
            """
        };
        Reduce = """
            groupBy(x => x.CampaignId).aggregate(g => ({ CampaignId: g.key, Bytes: g.values.reduce((sum, v) => sum + v.Bytes, 0) }))
            """;
    }
}

/// <summary>
/// The storage limit per account (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15 and 16). Attachments do not record who uploaded them,
/// so a campaign's pictures and files count against its owner, whoever added them: a player's portrait in someone's campaign counts
/// against that campaign's owner, who can take it away. The limit is the owner's own (<see cref="UserDocument.StorageLimitMb"/>) when
/// the admin set one, otherwise the server's (<see cref="ServerSettingsDocument.StorageLimitMb"/>); with neither, nothing is refused.
/// </summary>
public sealed class StorageQuota(RavenDbService db, IServerSettingsStore settings)
{
    private static readonly TimeSpan IndexWait = TimeSpan.FromSeconds(15);

    /// <summary>The limit an account has, in bytes; null for none. Its own when set (0 being none), otherwise the server's.</summary>
    public static long? LimitBytesFor(int? accountMb, int? serverMb) => (accountMb ?? serverMb) is { } mb and > 0 ? mb * 1024L * 1024L : null;

    /// <summary>The limit <paramref name="account"/> has now, in bytes; null for none.</summary>
    public long? LimitFor(UserDocument? account) => LimitBytesFor(account?.StorageLimitMb, settings.Get().StorageLimitMb);

    /// <summary>What each account keeps: the bytes of the campaigns it owns, by owner id. Accounts that keep nothing are left out.
    /// Null while the count is still being made (an index catching up, as on the first start after it was added).</summary>
    public async Task<IReadOnlyDictionary<string, long>?> ByOwnerAsync()
    {
        using var session = db.Store.OpenAsyncSession();
        Dictionary<string, long> byCampaign;
        try
        {
            byCampaign = await AllAsync(session);
        }
        catch (TimeoutException)
        {
            return null;
        }

        var owners = new Dictionary<string, long>(StringComparer.Ordinal);
        if (byCampaign.Count == 0) return owners;
        var campaigns = await session.LoadAsync<CampaignDocument>(byCampaign.Keys);
        foreach (var (id, bytes) in byCampaign)
        {
            if (campaigns.GetValueOrDefault(id) is not { } campaign) continue;
            owners[campaign.OwnerId] = owners.GetValueOrDefault(campaign.OwnerId) + bytes;
        }

        return owners;
    }

    /// <summary>The bytes the campaigns <paramref name="userId"/> owns keep.</summary>
    public async Task<long> UsedByAsync(string userId)
    {
        using var session = db.Store.OpenAsyncSession();
        return await UsedByAsync(session, userId, null);
    }

    /// <summary>Whether the owner of <paramref name="campaignId"/> has room left. (A picture larger than the room left is still drawn,
    /// then refused.)</summary>
    public async Task<bool> HasRoomAsync(string campaignId)
    {
        using var session = db.Store.OpenAsyncSession();
        if (await OwnerAsync(session, campaignId) is not ({ } owner, { } limit)) return true;
        return await UsedByAsync(session, owner, campaignId) < limit;
    }

    /// <summary>Refuses with <c>Service.Storage.FullBeforeDrawing</c> when the owner of <paramref name="campaignId"/> has no room left:
    /// checked before a picture is drawn for money, so a limit reached refuses before the model is paid, not after.</summary>
    public async Task EnsureRoomBeforeDrawingAsync(string campaignId)
    {
        using var session = db.Store.OpenAsyncSession();
        if (await OwnerAsync(session, campaignId) is not ({ } owner, { } limit)) return;
        if (await UsedByAsync(session, owner, campaignId) >= limit) throw new LocalizedException("Service.Storage.FullBeforeDrawing", Megabytes(limit));
    }

    /// <summary>Refuses with <c>Service.Storage.Full</c> when keeping <paramref name="adding"/> more bytes in <paramref name="campaignId"/>,
    /// in place of <paramref name="replacing"/> bytes it already keeps, would take its owner over the limit.</summary>
    public async Task EnsureRoomAsync(string campaignId, long adding, long replacing = 0)
    {
        if (adding <= replacing) return;
        using var session = db.Store.OpenAsyncSession();
        if (await OwnerAsync(session, campaignId) is not ({ } owner, { } limit)) return;
        var used = await UsedByAsync(session, owner, campaignId);
        if (used - replacing + adding > limit) throw new LocalizedException("Service.Storage.Full", Megabytes(used), Megabytes(limit));
    }

    // The campaign's owner and the limit they have; no owner when the campaign is gone.
    private async Task<(string? Owner, long? Limit)> OwnerAsync(IAsyncDocumentSession session, string campaignId)
    {
        if ((await session.LoadAsync<CampaignDocument>(campaignId))?.OwnerId is not { } owner) return (null, null);
        return (owner, LimitFor(await session.LoadAsync<UserDocument>(owner)));
    }

    /// <summary>The size of the attachment <paramref name="name"/> on <paramref name="entity"/>, loaded in <paramref name="session"/>;
    /// 0 when it has none by that name.</summary>
    public static long SizeOf(IAsyncDocumentSession session, object? entity, string name)
    {
        if (entity == null) return 0;
        var metadata = session.Advanced.GetMetadataFor(entity);
        if (!metadata.ContainsKey("@attachments")) return 0;
        return metadata.GetObjects("@attachments")
            .Where(a => string.Equals(a.GetString("Name"), name, StringComparison.Ordinal))
            .Select(a => a.GetLong("Size"))
            .FirstOrDefault();
    }

    /// <summary>Megabytes for a person to read, one decimal.</summary>
    public static string Megabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    // The campaign being written to is counted whatever the campaign list says: one made moments ago may not be in it yet. Both
    // queries wait for their index, and when it is still catching up (a large server's first start with the index) they take what it
    // has counted so far rather than fail the upload.
    private static async Task<long> UsedByAsync(IAsyncDocumentSession session, string userId, string? campaignId)
    {
        var owned = await WaitedOrStaleAsync(() => session.Query<CampaignDocument>().Where(c => c.OwnerId == userId).Select(c => c.Id));
        if (campaignId != null && !owned.Contains(campaignId)) owned.Add(campaignId);
        if (owned.Count == 0) return 0;
        var results = await WaitedOrStaleAsync(() => session.Query<StorageByCampaignIndex.Result, StorageByCampaignIndex>().Where(r => r.CampaignId.In(owned)));
        return results.Sum(r => r.Bytes);
    }

    // A query is made twice rather than reused: customizing a Raven query changes it in place.
    private static async Task<List<T>> WaitedOrStaleAsync<T>(Func<IRavenQueryable<T>> query)
    {
        try
        {
            return await query().Customize(x => x.WaitForNonStaleResults(IndexWait)).ToListAsync();
        }
        catch (TimeoutException)
        {
            return await query().ToListAsync();
        }
    }

    private static async Task<Dictionary<string, long>> AllAsync(IAsyncDocumentSession session)
    {
        // Paged rather than streamed: a stream does not wait for the index, and a page just changed should show at once.
        var all = new Dictionary<string, long>(StringComparer.Ordinal);
        const int Page = 1024;
        for (var skip = 0; ; skip += Page)
        {
            var page = await session.Query<StorageByCampaignIndex.Result, StorageByCampaignIndex>()
                .Customize(x => x.WaitForNonStaleResults(IndexWait))
                .OrderBy(r => r.CampaignId).Skip(skip).Take(Page).ToListAsync();
            foreach (var r in page) all[r.CampaignId] = r.Bytes;
            if (page.Count < Page) return all;
        }
    }
}
