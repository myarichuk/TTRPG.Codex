using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>
/// What an AI polish of a campaign book has produced so far, for one DM and one edition (docs/BOOK_EXPORT.md, step 5). The DM's own
/// working copy, like a world chat: only its owner reads it, <b>it is not part of the campaign export or the backups</b> (it is the DM's
/// working copy), and it is deleted with the campaign. The model's prompts are
/// never stored here, only its replies. The DM's secret words are in neither: they were never sent to the model, and the boxes that print them
/// are rendered from the campaign at print time.
/// </summary>
public class BookBuildDocument
{
    /// <summary>"bookbuilds/{campaign id}/{user id}/{edition}": one build per DM per edition, found by its name and never by a query, so
    /// nobody's build can be reached with another's id.</summary>
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>"dm" or "party".</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The book language and the editor's style sheet the chapters were built with. A chapter built with others is stale.</summary>
    public string Language { get; set; } = string.Empty;
    public string Style { get; set; } = string.Empty;

    public List<BookBuildSection> Sections { get; set; } = new();

    /// <summary>What every run of this build has used, added up. A provider that reports no usage is counted at our estimate and
    /// <see cref="Estimated"/> says so.</summary>
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }
    public int Runs { get; set; }
    public bool Estimated { get; set; }

    /// <summary>The money of the runs whose prices were known (the prices of the day, as the user had set them), and how many runs had none.
    /// With <see cref="UnpricedRuns"/> above zero this is a lower bound, and the page says so.</summary>
    public decimal Cost { get; set; }
    public int UnpricedRuns { get; set; }
    public string PriceLabel { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string IdFor(string campaignId, string userId, string audience) => $"bookbuilds/{campaignId}/{userId}/{audience}";

    public static string PrefixFor(string campaignId) => $"bookbuilds/{campaignId}/";
}

/// <summary>One chapter of a stored build.</summary>
public class BookBuildSection
{
    public string Id { get; set; } = string.Empty;

    /// <summary>What the chapter was made from (<c>BookBuildKey</c>). Used only while it equals the key of the campaign as read now.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>True when the model's rewrite passed the checks. Otherwise the chapter prints from the deterministic text, and
    /// <see cref="Reason"/> says why (budget, checks, error, auth, ratelimit, allowance, cancelled, skipped).</summary>
    public bool Refined { get; set; }
    public string? Reason { get; set; }
    public int Attempts { get; set; }

    /// <summary>The chapter with its blocks as <c>{{block:N}}</c> markers; empty unless <see cref="Refined"/>.</summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>The model that wrote it, shown beside the chapter; it is not part of the key.</summary>
    public string Model { get; set; } = string.Empty;

    public DateTime BuiltAt { get; set; } = DateTime.UtcNow;
}

public interface IBookBuildRepository
{
    /// <summary>The DM's own build of one edition; null when there is none, and for anyone who is not the campaign's DM now.</summary>
    Task<BookBuildDocument?> GetAsync(CampaignAccess dm, string audience);

    /// <summary>Stores the DM's build, replacing what was there. False when the caller is not the campaign's DM now.</summary>
    Task<bool> SaveAsync(CampaignAccess dm, BookBuildDocument build);

    Task<bool> DeleteAsync(CampaignAccess dm, string audience);

    Task DeleteAllForCampaignAsync(string campaignId);
}

public class RavenBookBuildRepository(RavenDbService dbService) : IBookBuildRepository
{
    private static bool IsEdition(string audience) => audience is "dm" or "party";

    public async Task<BookBuildDocument?> GetAsync(CampaignAccess dm, string audience)
    {
        if (!IsEdition(audience) || await CampaignAccessResolver.NowAsync(dbService.Store, dm, write: false) is not { IsDm: true } now) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var build = await session.LoadAsync<BookBuildDocument>(BookBuildDocument.IdFor(now.CampaignId, now.UserId, audience));
        // The id already names the owner; the fields are checked too, so a document written by hand under another's name is not served.
        return build != null && build.CampaignId == now.CampaignId && build.OwnerUserId == now.UserId && build.Audience == audience ? build : null;
    }

    public async Task<bool> SaveAsync(CampaignAccess dm, BookBuildDocument build)
    {
        if (!IsEdition(build.Audience) || await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        build.CampaignId = now.CampaignId;
        build.OwnerUserId = now.UserId;
        build.Id = BookBuildDocument.IdFor(now.CampaignId, now.UserId, build.Audience);
        build.UpdatedAt = DateTime.UtcNow;
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(build, build.Id);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(CampaignAccess dm, string audience)
    {
        if (!IsEdition(audience) || await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        using var session = dbService.Store.OpenAsyncSession();
        session.Delete(BookBuildDocument.IdFor(now.CampaignId, now.UserId, audience));
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        // By the name's prefix (it ends in a slash, so "campaigns/1" never takes "campaigns/10"): no index to wait for.
        var all = await session.Advanced.LoadStartingWithAsync<BookBuildDocument>(BookBuildDocument.PrefixFor(campaignId), pageSize: 1024);
        foreach (var build in all)
        {
            session.Delete(build);
        }

        await session.SaveChangesAsync();
    }
}
