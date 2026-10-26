using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>
/// Keeps a place on the world map and its wiki page together: <see cref="LocationDocument.WikiPageId"/> and
/// <see cref="WikiPageDocument.PlaceId"/> point at each other. They share a name and the place's picture, and sharing the
/// page reveals the place. Their texts do not follow each other: a place's description is what players read once it is
/// revealed, and its page may hold what the place hides. Every write goes through the repositories' own DM checks.
/// </summary>
public sealed class PlaceWikiSync(IRegionRepository regions, IWikiRepository wiki, ArtService art)
{
    /// <summary>Writes the wiki page for <paramref name="location"/> (new, or the one it already names) and links both ways.
    /// The page starts with the place's description, visible to the party only when the place is. Null when the caller is not the DM.</summary>
    public async Task<WikiPageDocument?> CreatePageAsync(string regionId, LocationDocument location, string tag, CampaignAccess access)
    {
        var known = location.Visibility == ActorVisibility.Known;
        var page = new WikiPageDocument
        {
            Id = string.IsNullOrEmpty(location.WikiPageId) ? Guid.NewGuid().ToString() : location.WikiPageId,
            Title = location.Name,
            Body = location.Description ?? string.Empty,
            Visibility = known ? WikiVisibility.Party : WikiVisibility.DmOnly,
            Tags = string.IsNullOrWhiteSpace(tag) ? new List<string>() : new List<string> { tag },
            PlaceId = location.Id,
            AuthorUserId = access.UserId
        };
        if (!await wiki.TrySaveAsync(page, access))
        {
            return null;
        }

        if (location.WikiPageId != page.Id)
        {
            await LinkPlaceAsync(regionId, location.Id, page.Id, access);
            location.WikiPageId = page.Id;
        }

        return page;
    }

    /// <summary>Puts <paramref name="page"/> on the map: a new place named after it in <paramref name="regionId"/> (a region named
    /// <paramref name="newRegionName"/> is made when there is none), with the page's text when the party can read it anyway, and
    /// <paramref name="image"/> as its picture. Returns the region and place, or null when the caller is not the DM.</summary>
    public async Task<(RegionDocument Region, LocationDocument Place)?> CreatePlaceAsync(WikiPageDocument page, string? regionId, string newRegionName, ArtImage? image, CampaignAccess access)
    {
        if (!access.IsDm) return null;

        var all = (await regions.GetVisibleForCampaignAsync(access)).ToList();
        var region = all.FirstOrDefault(r => r.Id == regionId) ?? all.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var known = page.Visibility == WikiVisibility.Party;
        var place = new LocationDocument
        {
            Id = Guid.NewGuid().ToString(),
            Name = page.Title.Trim(),
            Description = known && !string.IsNullOrWhiteSpace(page.Body) ? page.Body : null,
            Visibility = known ? ActorVisibility.Known : ActorVisibility.Hidden,
            WikiPageId = page.Id
        };

        if (region == null)
        {
            region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = access.CampaignId, Name = newRegionName, Locations = new List<LocationDocument> { place } };
            if (!await regions.SaveAsync(region, access)) return null;
        }
        else if (!await regions.UpsertLocationAsync(region.Id, place, access))
        {
            return null;
        }

        page.PlaceId = place.Id;
        if (!await wiki.TrySaveAsync(page, access)) return null;

        if (image != null)
        {
            await art.SetLocationAsync(region.Id, place, access, image);
        }

        return (region, place);
    }

    /// <summary>The place a page is about, with its region, or null when the page has none or the viewer can't see it.</summary>
    public async Task<(RegionDocument Region, LocationDocument Place)?> FindPlaceAsync(string? placeId, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(placeId)) return null;
        foreach (var region in await regions.GetVisibleForCampaignAsync(access))
        {
            if (region.Locations.FirstOrDefault(l => l.Id == placeId) is { } place)
            {
                return (region, place);
            }
        }

        return null;
    }

    /// <summary>The place was renamed: the page about it takes the new name.</summary>
    public async Task RenamePageAsync(LocationDocument location, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(location.WikiPageId)) return;
        var page = (await wiki.GetVisibleForCampaignAsync(access)).FirstOrDefault(p => p.Id == location.WikiPageId);
        if (page == null || page.Title == location.Name) return;
        page.Title = location.Name;
        await wiki.TrySaveAsync(page, access);
    }

    /// <summary>The page was renamed or shared: its place takes the name, and a shared page's place is revealed so the party
    /// can open what the page shows.</summary>
    public async Task UpdatePlaceAsync(WikiPageDocument page, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(page.PlaceId)) return;
        if (await FindPlaceAsync(page.PlaceId, access) is not var (region, place)) return;

        if (place.Name != page.Title)
        {
            place.Name = page.Title;
            await regions.UpsertLocationAsync(region.Id, place, access);
        }

        if (page.Visibility == WikiVisibility.Party && place.Visibility != ActorVisibility.Known)
        {
            await regions.SetLocationVisibilityAsync(region.Id, place.Id, ActorVisibility.Known, access);
        }
    }

    private async Task LinkPlaceAsync(string regionId, string locationId, string pageId, CampaignAccess access)
    {
        // Patch the stored place, not the caller's copy, which may be older than the last edit.
        var region = await regions.GetAsync(regionId, access);
        if (region?.Locations.FirstOrDefault(l => l.Id == locationId) is { } stored)
        {
            stored.WikiPageId = pageId;
            await regions.UpsertLocationAsync(regionId, stored, access);
        }
    }
}
