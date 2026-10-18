using System.Security.Claims;
using Codex.Persistence;

namespace Codex.Web;

/// <summary>
/// Serves campaign art. Every request resolves the caller's <see cref="CampaignAccess"/> first:
/// a cover is visible to members, a portrait to whoever may see the actor (so a hidden NPC's
/// face never leaks through its image URL). URLs carry a version query so browsers may cache.
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
    }

    private static IResult Image(HttpResponse response, MediaFile file)
    {
        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Bytes(file.Bytes, file.ContentType);
    }
}
