using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>Bytes plus content type of a stored image.</summary>
public sealed record MediaFile(byte[] Bytes, string ContentType);

/// <summary>
/// Campaign art (covers, portraits) stored as RavenDB attachments on the owning document, so
/// they live, back up and delete with it. Reading is the caller's job: the endpoints that serve
/// these resolve <see cref="CampaignAccess"/> first and only then ask for the bytes. Writing checks here (D15): the
/// document must be in <see cref="CampaignAccess.CampaignId"/>, and the writer, by their role in the campaign as stored
/// now, its DM (or the owning player, for their character's portrait). Otherwise <c>Campaign.AccessChanged</c>.
/// </summary>
public interface IMediaStore
{
    Task PutAsync(CampaignAccess access, string documentId, string name, Stream content, string contentType);
    Task<MediaFile?> GetAsync(string documentId, string name);
    Task DeleteAsync(CampaignAccess access, string documentId, string name);
}

public class RavenMediaStore(RavenDbService dbService) : IMediaStore
{
    public const string CampaignCover = "cover";
    public const string ActorPortrait = "portrait";
    public const string EntryArt = "art";

    /// <summary>Attachment name for a pack entry's art on the campaign document. Ids are slugs, but
    /// anything else is folded to '-' so a crafted id can't address another attachment.</summary>
    public static string PackArtName(string type, string id) => $"art-{Safe(type)}--{Safe(id)}";

    /// <summary>Attachment name for a location's art on its region document.</summary>
    public static string LocationArtName(string locationId) => $"location-{Safe(locationId)}";

    public static string PackArtKey(string type, string id) => $"{type}/{id}".ToLowerInvariant();

    private static string Safe(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9_-]", "-");

    /// <summary>Image types the uploader accepts. SVG is excluded on purpose: it can carry scripts.</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif"
    };

    public const long MaxBytes = 6 * 1024 * 1024;

    public async Task PutAsync(CampaignAccess access, string documentId, string name, Stream content, string contentType)
    {
        if (!AllowedContentTypes.Contains(contentType))
        {
            throw new InvalidOperationException($"Unsupported image type '{contentType}'.");
        }

        using var session = dbService.Store.OpenAsyncSession();
        await EnsureMayWriteAsync(session, access, documentId);
        if (await session.Advanced.Attachments.ExistsAsync(documentId, name))
        {
            session.Advanced.Attachments.Delete(documentId, name);
            await session.SaveChangesAsync();
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        buffer.Position = 0;
        session.Advanced.Attachments.Store(documentId, name, buffer, contentType);
        await session.SaveChangesAsync();
    }

    public async Task<MediaFile?> GetAsync(string documentId, string name)
    {
        using var session = dbService.Store.OpenAsyncSession();
        using var attachment = await session.Advanced.Attachments.GetAsync(documentId, name);
        if (attachment == null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer);
        return new MediaFile(buffer.ToArray(), attachment.Details.ContentType);
    }

    public async Task DeleteAsync(CampaignAccess access, string documentId, string name)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await EnsureMayWriteAsync(session, access, documentId);
        if (await session.Advanced.Attachments.ExistsAsync(documentId, name))
        {
            session.Advanced.Attachments.Delete(documentId, name);
            await session.SaveChangesAsync();
        }
    }

    private static async Task EnsureMayWriteAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, CampaignAccess access, string documentId)
    {
        var campaign = await session.LoadAsync<CampaignDocument>(access.CampaignId);
        if (campaign != null && access.SystemMissing)
        {
            throw new Codex.Localization.LocalizedException("Campaign.SystemMissing", campaign.SystemId);
        }

        var role = campaign == null ? null : CampaignAccessResolver.RoleOf(campaign, access.UserId);
        var allowed = role != null && (documentId == access.CampaignId
            ? role == CampaignRole.DM
            : await session.LoadAsync<object>(documentId) switch
            {
                ActorDocument actor => actor.CampaignId == access.CampaignId && (role == CampaignRole.DM || actor.OwnerUserId == access.UserId),
                HomebrewEntryDocument brew => brew.CampaignId == access.CampaignId && role == CampaignRole.DM,
                RegionDocument region => region.CampaignId == access.CampaignId && role == CampaignRole.DM,
                WikiPageDocument page => page.CampaignId == access.CampaignId && role == CampaignRole.DM,
                _ => false
            });

        if (!allowed)
        {
            throw new Codex.Localization.LocalizedException("Campaign.AccessChanged");
        }
    }
}
