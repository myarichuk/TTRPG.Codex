using System.Security.Claims;
using Codex.Core;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web;

/// <summary>
/// The small card shown on hovering a mention or a <c>[[link]]</c> (app-startup.js): a name, what it is, the first lines and
/// the picture. It reads only through the same visible-to-the-caller calls the pages themselves use, so it can never say more
/// than following the link would: a hidden character, an unrevealed place or a DM-only page is "not found", a stranger is
/// their mask, and a player's text comes without the blocks they may not read.
/// </summary>
public static class PreviewEndpoints
{
    /// <summary>What the card shows; <see cref="Image"/> is a media URL or null.</summary>
    public sealed record Preview(string Title, string Sub, string Text, string? Image);

    private const int TextLength = 260;

    public static void MapPreviews(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId}/preview/{kind}/{**id}", async (string campaignId, string kind, string id, ClaimsPrincipal user, HttpResponse response,
            ICampaignAccessResolver resolver, ICampaignRepository campaigns, IActorRepository actors, IWikiRepository wiki, IRegionRepository regions,
            MentionSources mentionSources) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var access = userId == null ? null : await resolver.ResolveAsync(campaignId, userId);
            var campaign = access == null ? null : await campaigns.GetAsync(campaignId);
            if (access == null || campaign == null)
            {
                return Results.NotFound();
            }

            // A reveal can change the answer at any moment: never from a cache.
            response.Headers.CacheControl = "no-store";
            var preview = kind switch
            {
                "actor" => await ActorAsync(id, access, campaign, actors, mentionSources),
                "page" => await PageAsync(id, access, campaign, actors, wiki, mentionSources),
                "place" => await PlaceAsync(id, access, campaign, actors, regions, mentionSources),
                _ => null
            };
            return preview == null ? Results.NotFound() : Results.Json(preview);
        });
    }

    private static async Task<Preview?> ActorAsync(string id, CampaignAccess access, CampaignDocument campaign, IActorRepository actors, MentionSources mentionSources)
    {
        var actor = await actors.GetVisibleAsync(id, access);
        if (actor == null)
        {
            return null;
        }

        var L = new Localizer();
        // A stranger's mask carries only the public name and look; the DM, who sees the stranger whole, is told what players see.
        var stranger = actor.Visibility == ActorVisibility.Unknown;
        var kind = actor.Kind switch
        {
            ActorKind.PlayerCharacter => L["Actor.Sheet.KindPc"],
            ActorKind.Monster => L["Actor.Sheet.KindMonster"],
            _ => L["Actor.Sheet.KindNpc"]
        };
        var sub = stranger ? $"{kind} · {L["Actor.Stranger.NotIdentified"]}" : kind;
        var text = stranger && !access.IsDm ? actor.PublicDescription : actor.Biography;
        var mentions = await MentionsAsync(text, campaign, access, actors, mentionSources);
        return new Preview(actor.Name, sub, WikiRender.Summary(text, mentions, TextLength, access.IsDm),
            actor.HasPortrait && !(stranger && !access.IsDm) ? MediaEndpoints.PortraitUrl(actor) : null);
    }

    private static async Task<Preview?> PageAsync(string slug, CampaignAccess access, CampaignDocument campaign, IActorRepository actors, IWikiRepository wiki, MentionSources mentionSources)
    {
        var page = await wiki.GetVisibleBySlugAsync(slug, access);
        if (page == null)
        {
            return null;
        }

        var L = new Localizer();
        var mentions = await MentionsAsync(page.Body, campaign, access, actors, mentionSources);
        return new Preview(page.Title, L[$"Campaign.Wiki.Kind.{page.Kind}"], WikiRender.Summary(page.Body, mentions, TextLength, access.IsDm), ArtService.Url(page));
    }

    private static async Task<Preview?> PlaceAsync(string id, CampaignAccess access, CampaignDocument campaign, IActorRepository actors, IRegionRepository regions, MentionSources mentionSources)
    {
        foreach (var region in await regions.GetVisibleForCampaignAsync(access))
        {
            if (region.Locations.FirstOrDefault(l => l.Id == id) is not { } place)
            {
                continue;
            }

            var L = new Localizer();
            var mentions = await MentionsAsync(place.Description, campaign, access, actors, mentionSources);
            return new Preview(place.Name, $"{L[$"Campaign.Places.Kind.{place.Kind}"]} · {region.Name}",
                WikiRender.Summary(place.Description, mentions, TextLength, access.IsDm), ArtService.Url(region, place));
        }

        return null;
    }

    // The names of what the text mentions, as the caller may know them; only loaded when there is something to name.
    private static async Task<MentionDirectory> MentionsAsync(string? text, CampaignDocument campaign, CampaignAccess access, IActorRepository actors, MentionSources mentionSources) =>
        WikiText.HasMentions(text)
            ? await mentionSources.DirectoryAsync(campaign, access, await actors.GetVisibleForCampaignAsync(access))
            : MentionDirectory.Empty;
}
