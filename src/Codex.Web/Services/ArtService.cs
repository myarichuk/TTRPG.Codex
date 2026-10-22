using Codex.Core.AI;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>An image picked or generated for an art slot, held in memory until it is stored.</summary>
public sealed record ArtImage(byte[] Bytes, string ContentType);

/// <summary>
/// The art slot every Library entry and every place has. Homebrew keeps its picture on its own
/// document and a place on its region, so the art lives and deletes with its owner; art for a
/// pack entry belongs to the campaign (the pack itself is read-only). Serving goes through
/// <see cref="MediaEndpoints"/>, which applies the Library's own visibility rules.
/// </summary>
public sealed class ArtService(IMediaStore media, IHomebrewRepository homebrew, ICampaignRepository campaigns, IRegionRepository regions, AiProviderService providers)
{
    public const string Accept = "image/png,image/jpeg,image/webp,image/gif,image/avif";

    /// <summary>Why an upload can't be stored, or null when it can.</summary>
    public static string? Problem(string contentType, long size) =>
        !RavenMediaStore.AllowedContentTypes.Contains(contentType) ? "Use a PNG, JPEG, WebP, GIF or AVIF image."
        : size > RavenMediaStore.MaxBytes ? "That image is over 6 MB. Resize it and try again."
        : null;

    public static string? Url(CampaignDocument campaign, LibraryItem item)
    {
        var path = $"/media/campaigns/{campaign.Id}/library/{Uri.EscapeDataString(item.Entry.Type)}/{Uri.EscapeDataString(item.Entry.Id)}/art";
        if (item.Homebrew is { } brew)
        {
            return brew.HasArt ? $"{path}?v=h{brew.ArtVersion}" : null;
        }

        return campaign.PackArt.TryGetValue(RavenMediaStore.PackArtKey(item.Entry.Type, item.Entry.Id), out var version) ? $"{path}?v=p{version}" : null;
    }

    public static string? Url(RegionDocument region, LocationDocument location) =>
        location.HasArt ? $"/media/regions/{region.Id}/locations/{Uri.EscapeDataString(location.Id)}/art?v={location.ArtVersion}" : null;

    /// <summary>Stores art for a Library entry; false when the caller isn't the DM or the image can't be stored.
    /// For pack art, <paramref name="campaign"/>'s <see cref="CampaignDocument.PackArt"/> is refreshed so the new URL shows at once.</summary>
    public async Task<bool> PutAsync(CampaignDocument campaign, CampaignAccess access, LibraryItem item, ArtImage image)
    {
        if (!access.IsDm || access.CampaignId != campaign.Id || Problem(image.ContentType, image.Bytes.Length) != null)
        {
            return false;
        }

        if (item.Homebrew is { } brew)
        {
            return await PutHomebrewAsync(brew, access, image);
        }

        using (var stream = new MemoryStream(image.Bytes))
        {
            await media.PutAsync(campaign.Id, RavenMediaStore.PackArtName(item.Entry.Type, item.Entry.Id), stream, image.ContentType);
        }

        var key = RavenMediaStore.PackArtKey(item.Entry.Type, item.Entry.Id);
        var updated = await campaigns.UpdateAsync(campaign.Id, c => c.PackArt[key] = c.PackArt.GetValueOrDefault(key) + 1);
        if (updated != null)
        {
            campaign.PackArt = updated.PackArt;
        }

        return true;
    }

    public async Task<bool> PutHomebrewAsync(HomebrewEntryDocument brew, CampaignAccess access, ArtImage image)
    {
        if (!access.IsDm || string.IsNullOrEmpty(brew.Id) || brew.CampaignId != access.CampaignId || Problem(image.ContentType, image.Bytes.Length) != null)
        {
            return false;
        }

        using (var stream = new MemoryStream(image.Bytes))
        {
            await media.PutAsync(brew.Id, RavenMediaStore.EntryArt, stream, image.ContentType);
        }

        if (!await homebrew.SetArtAsync(brew.Id, true, access))
        {
            return false;
        }

        brew.HasArt = true;
        brew.ArtVersion++;
        return true;
    }

    public async Task<bool> RemoveAsync(CampaignDocument campaign, CampaignAccess access, LibraryItem item)
    {
        if (!access.IsDm || access.CampaignId != campaign.Id)
        {
            return false;
        }

        if (item.Homebrew is { } brew)
        {
            await media.DeleteAsync(brew.Id, RavenMediaStore.EntryArt);
            if (!await homebrew.SetArtAsync(brew.Id, false, access)) return false;
            brew.HasArt = false;
            brew.ArtVersion++;
            return true;
        }

        await media.DeleteAsync(campaign.Id, RavenMediaStore.PackArtName(item.Entry.Type, item.Entry.Id));
        var key = RavenMediaStore.PackArtKey(item.Entry.Type, item.Entry.Id);
        var updated = await campaigns.UpdateAsync(campaign.Id, c => c.PackArt.Remove(key));
        if (updated != null)
        {
            campaign.PackArt = updated.PackArt;
        }

        return true;
    }

    /// <summary>Stores or removes (<paramref name="image"/> null) a place's art and records it on the location.</summary>
    public async Task<bool> SetLocationAsync(string regionId, LocationDocument location, CampaignAccess access, ArtImage? image)
    {
        if (!access.IsDm || (image != null && Problem(image.ContentType, image.Bytes.Length) != null))
        {
            return false;
        }

        var region = await regions.GetAsync(regionId, access);
        var stored = region?.Locations.FirstOrDefault(l => l.Id == location.Id);
        if (region == null || stored == null)
        {
            return false;
        }

        if (image == null)
        {
            await media.DeleteAsync(regionId, RavenMediaStore.LocationArtName(location.Id));
        }
        else
        {
            using var stream = new MemoryStream(image.Bytes);
            await media.PutAsync(regionId, RavenMediaStore.LocationArtName(location.Id), stream, image.ContentType);
        }

        stored.HasArt = image != null;
        stored.ArtVersion++;
        if (!await regions.UpsertLocationAsync(regionId, stored, access))
        {
            return false;
        }

        location.HasArt = stored.HasArt;
        location.ArtVersion = stored.ArtVersion;
        return true;
    }

    /// <summary>Draws a picture with the user's image model. <paramref name="visual"/> is the entry's
    /// art prompt when it has one, otherwise a short description.</summary>
    public async Task<ArtImage> GenerateAsync(string userId, string subject, string subjectKind, string? visual, bool landscape)
    {
        var image = await providers.GenerateImageAsync(userId, AiPrompts.Image(subject, subjectKind, visual, landscape), landscape);
        if (!RavenMediaStore.AllowedContentTypes.Contains(image.ContentType))
        {
            throw new InvalidOperationException($"The image model sent a {image.ContentType} file, which can't be stored. Try another model.");
        }

        if (image.Bytes.Length > RavenMediaStore.MaxBytes)
        {
            throw new InvalidOperationException("The generated image is over 6 MB. Pick a smaller size or another model.");
        }

        return new ArtImage(image.Bytes, image.ContentType);
    }
}
