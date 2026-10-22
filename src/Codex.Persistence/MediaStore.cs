using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>Bytes plus content type of a stored image.</summary>
public sealed record MediaFile(byte[] Bytes, string ContentType);

/// <summary>
/// Campaign art (covers, portraits) stored as RavenDB attachments on the owning document, so
/// they live, back up and delete with it. Authorisation is the caller's job: the endpoints that
/// serve these resolve <see cref="CampaignAccess"/> first and only then ask for the bytes.
/// </summary>
public interface IMediaStore
{
    Task PutAsync(string documentId, string name, Stream content, string contentType);
    Task<MediaFile?> GetAsync(string documentId, string name);
    Task DeleteAsync(string documentId, string name);
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

    public async Task PutAsync(string documentId, string name, Stream content, string contentType)
    {
        if (!AllowedContentTypes.Contains(contentType))
        {
            throw new InvalidOperationException($"Unsupported image type '{contentType}'.");
        }

        using var session = dbService.Store.OpenAsyncSession();
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

    public async Task DeleteAsync(string documentId, string name)
    {
        using var session = dbService.Store.OpenAsyncSession();
        if (await session.Advanced.Attachments.ExistsAsync(documentId, name))
        {
            session.Advanced.Attachments.Delete(documentId, name);
            await session.SaveChangesAsync();
        }
    }
}
