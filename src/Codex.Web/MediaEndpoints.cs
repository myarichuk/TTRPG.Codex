using System.Security.Claims;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web;

/// <summary>
/// Serves campaign art. Every request resolves the caller's <see cref="CampaignAccess"/> first:
/// a cover is visible to members, a portrait to whoever may see the actor (so a hidden NPC's
/// face never leaks through its image URL), Library art to whoever the Library would show the entry
/// (the same <see cref="CampaignLibrary.GetAsync"/> call), and a place's art once the place is
/// revealed. URLs carry a version query so browsers may cache.
/// </summary>
public static class MediaEndpoints
{
    public static string CoverUrl(CampaignDocument campaign) => $"/media/campaigns/{campaign.Id}/cover?v={campaign.CoverVersion}";
    public static string PortraitUrl(ActorDocument actor) => $"/media/actors/{actor.Id}/portrait?v={actor.PortraitVersion}";

    public static void MapMedia(this WebApplication app)
    {
        app.MapGet("/media/campaigns/{campaignId}/cover", async (string campaignId, ClaimsPrincipal user, ICampaignAccessResolver resolver, IMediaStore media, HttpResponse response) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null || await resolver.ResolveAsync(campaignId, userId) == null)
            {
                return Results.NotFound();
            }

            var file = await media.GetAsync(campaignId, RavenMediaStore.CampaignCover);
            return file == null ? Results.NotFound() : Image(response, file);
        });

        app.MapGet("/media/actors/{actorId}/portrait", async (string actorId, ClaimsPrincipal user, ICampaignAccessResolver resolver, IActorRepository actors, IMediaStore media, RavenDbService db, HttpResponse response) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Results.NotFound();
            }

            // The actor's campaign is on the document; load it unguarded only to learn which campaign to resolve access for.
            string? campaignId;
            using (var session = db.Store.OpenAsyncSession())
            {
                campaignId = (await session.LoadAsync<ActorDocument>(actorId))?.CampaignId;
            }

            if (campaignId == null)
            {
                return Results.NotFound();
            }

            var access = await resolver.ResolveAsync(campaignId, userId);
            if (access == null || await actors.GetVisibleAsync(actorId, access) == null)
            {
                return Results.NotFound();
            }

            var file = await media.GetAsync(actorId, RavenMediaStore.ActorPortrait);
            return file == null ? Results.NotFound() : Image(response, file);
        });

        MapArt(app);
    }

    private static void MapArt(WebApplication app)
    {
        app.MapGet("/media/campaigns/{campaignId}/library/{type}/{id}/art", async (string campaignId, string type, string id, ClaimsPrincipal user, ICampaignAccessResolver resolver, ICampaignRepository campaigns, CampaignLibrary library, IMediaStore media, HttpResponse response) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var access = userId == null ? null : await resolver.ResolveAsync(campaignId, userId);
            var campaign = access == null ? null : await campaigns.GetAsync(campaignId);
            var item = campaign == null ? null : await library.GetAsync(campaign, access!, type, id);
            if (item == null)
            {
                return Results.NotFound();
            }

            var file = item.Homebrew is { } brew
                ? (brew.HasArt ? await media.GetAsync(brew.Id, RavenMediaStore.EntryArt) : null)
                : await media.GetAsync(campaignId, RavenMediaStore.PackArtName(item.Entry.Type, item.Entry.Id));
            return file == null ? Results.NotFound() : Image(response, file);
        });

        app.MapGet("/media/regions/{regionId}/locations/{locationId}/art", async (string regionId, string locationId, ClaimsPrincipal user, ICampaignAccessResolver resolver, IRegionRepository regions, IMediaStore media, RavenDbService db, HttpResponse response) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
            {
                return Results.NotFound();
            }

            // As with portraits: load unguarded only to learn the campaign, then go through the guarded read.
            string? campaignId;
            using (var session = db.Store.OpenAsyncSession())
            {
                campaignId = (await session.LoadAsync<RegionDocument>(regionId))?.CampaignId;
            }

            var access = campaignId == null ? null : await resolver.ResolveAsync(campaignId, userId);
            var region = access == null ? null : await regions.GetAsync(regionId, access);
            if (region?.Locations.Any(l => l.Id == locationId && l.HasArt) != true)
            {
                return Results.NotFound();
            }

            var file = await media.GetAsync(regionId, RavenMediaStore.LocationArtName(locationId));
            return file == null ? Results.NotFound() : Image(response, file);
        });
    }

    private static IResult Image(HttpResponse response, MediaFile file)
    {
        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Bytes(file.Bytes, file.ContentType);
    }
}
