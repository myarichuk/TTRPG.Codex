using System.Security.Claims;
using Codex.Core;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web;

/// <summary>
/// The card shown on hovering (or, on a phone, tapping) a mention, a <c>[[link]]</c> or a name on the table (app-startup.js): a
/// name, what it is, the first lines, the picture, where it opens and what it mentions, so a card can open the next one. It reads
/// only through the same visible-to-the-caller calls the pages themselves use, so it can never say more than following the link
/// would: a hidden character, an unrevealed place or a DM-only page is "not found", a stranger is their mask, and a player's text
/// comes without the blocks they may not read.
/// </summary>
public static class PreviewEndpoints
{
    /// <summary>What the card shows; <see cref="Image"/> is a media URL or null. <see cref="Links"/> are what the text names that
    /// the caller may open: its mentions, then its page links.</summary>
    public sealed record Preview(string Title, string Sub, string Text, string? Image, string Href, IReadOnlyList<PreviewLink> Links);

    public sealed record PreviewLink(string Title, string Href);

    private const int TextLength = 260;

    // A library entry's card is its rules (a condition, a spell), which are read rather than glanced at.
    private const int EntryTextLength = 600;
    private const int MaxLinks = 8;

    public static void MapPreviews(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId}/preview/{kind}/{**id}", async (string campaignId, string kind, string id, string? @as,
            [Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Time-Zone")] string? timeZone, ClaimsPrincipal user, HttpResponse response,
            ICampaignAccessResolver resolver, ICampaignRepository campaigns, IActorRepository actors, IWikiRepository wiki, IRegionRepository regions,
            ISessionRepository sessions, CampaignLibrary library, MentionSources mentionSources) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var access = userId == null ? null : await resolver.ResolveAsync(campaignId, userId);
            var campaign = access == null ? null : await campaigns.GetAsync(campaignId);
            if (access == null || campaign == null)
            {
                return Results.NotFound();
            }

            // The DM's "view as players" lens asks as a player with no characters, as the lens itself reads (S6). Anyone else
            // already is what they are, so the parameter changes nothing for them.
            if (@as == "players" && access.PartyPreview() is { } party)
            {
                access = party;
            }

            // A reveal can change the answer at any moment: never from a cache.
            response.Headers.CacheControl = "no-store";
            var reader = new Reader(campaign, access, actors, wiki, mentionSources);
            var preview = kind switch
            {
                "actor" => await ActorAsync(id, reader),
                "page" => await PageAsync(id, reader),
                "place" => await PlaceAsync(id, reader, regions),
                "entry" => await EntryAsync(id, reader, library),
                "session" => await SessionAsync(id, reader, sessions, ViewerClock.ForZone(timeZone)),
                _ => null
            };
            return preview == null ? Results.NotFound() : Results.Json(preview);
        });
    }

    private static async Task<Preview?> ActorAsync(string id, Reader reader)
    {
        var actor = await reader.Actors.GetVisibleAsync(id, reader.Access);
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
        var text = stranger && !reader.Access.IsDm ? actor.PublicDescription : actor.Biography;
        return await reader.CardAsync(actor.Name, sub, text, actor.HasPortrait && !(stranger && !reader.Access.IsDm) ? MediaEndpoints.PortraitUrl(actor) : null,
            MentionLinks.Href(reader.Campaign.Id, MentionKind.Actor, actor.Id));
    }

    private static async Task<Preview?> PageAsync(string slug, Reader reader)
    {
        var page = await reader.Wiki.GetVisibleBySlugAsync(slug, reader.Access);
        if (page == null)
        {
            return null;
        }

        var L = new Localizer();
        return await reader.CardAsync(page.Title, L[$"Campaign.Wiki.Kind.{page.Kind}"], page.Body, ArtService.Url(page),
            $"/campaigns/{reader.Campaign.Id}/wiki/{Uri.EscapeDataString(page.Slug)}");
    }

    private static async Task<Preview?> PlaceAsync(string id, Reader reader, IRegionRepository regions)
    {
        foreach (var region in await regions.GetVisibleForCampaignAsync(reader.Access))
        {
            if (region.Locations.FirstOrDefault(l => l.Id == id) is not { } place)
            {
                continue;
            }

            var L = new Localizer();
            return await reader.CardAsync(place.Name, $"{L[$"Campaign.Places.Kind.{place.Kind}"]} · {region.Name}", place.Description, ArtService.Url(region, place),
                MentionLinks.Href(reader.Campaign.Id, MentionKind.Place, place.Id));
        }

        return null;
    }

    // A library entry ("type/id", as a $ mention and the library route name it): a condition's rules, a spell, an item.
    private static async Task<Preview?> EntryAsync(string key, Reader reader, CampaignLibrary library)
    {
        var parts = key.Split('/', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var item = await library.GetAsync(reader.Campaign, reader.Access, parts[0], parts[1], System.Globalization.CultureInfo.CurrentUICulture.Name);
        if (item == null)
        {
            return null;
        }

        var type = (await library.TypesAsync(reader.Campaign, reader.Access))
            .FirstOrDefault(t => string.Equals(t.Type, item.Entry.Type, StringComparison.OrdinalIgnoreCase))?.Label ?? CampaignLibrary.Humanize(item.Entry.Type);
        return await reader.CardAsync(item.Entry.Name, $"{type} · {item.SourceName}", item.Entry.Description, ArtService.Url(reader.Campaign, item),
            MentionLinks.Href(reader.Campaign.Id, MentionKind.Item, MentionSources.ItemKey(item.Entry.Type, item.Entry.Id)), EntryTextLength);
    }

    // A session: its recap, which the repository has already cut to what this viewer may read. Its date is the day in the
    // viewer's zone, as the session list shows it.
    private static async Task<Preview?> SessionAsync(string id, Reader reader, ISessionRepository sessions, ViewerClock clock)
    {
        var session = await sessions.GetVisibleAsync(id, reader.Access);
        if (session == null)
        {
            return null;
        }

        var L = new Localizer();
        var date = clock.ToViewer(session.Date).ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentUICulture);
        return await reader.CardAsync(string.IsNullOrWhiteSpace(session.Title) ? date : session.Title, $"{L[$"Session.Status.{session.Status}"]} · {date}", session.Recap, null,
            $"/campaigns/{reader.Campaign.Id}/sessions/{Uri.EscapeDataString(session.Id)}");
    }

    /// <summary>What one caller may read, and the card built from a text as they read it.</summary>
    private sealed class Reader(CampaignDocument campaign, CampaignAccess access, IActorRepository actors, IWikiRepository wiki, MentionSources mentionSources)
    {
        public CampaignDocument Campaign => campaign;
        public CampaignAccess Access => access;
        public IActorRepository Actors => actors;
        public IWikiRepository Wiki => wiki;

        public async Task<Preview> CardAsync(string title, string sub, string? text, string? image, string href, int length = TextLength)
        {
            // The DM reads all of the text; anyone else, the text without the blocks they may not read. The links come from the
            // same text, so a card never links what its text leaves out.
            var visible = string.IsNullOrEmpty(text) ? string.Empty : access.IsDm ? SecretText.WithoutMarkers(text) : SecretText.ForPlayers(text);
            var mentions = WikiText.HasMentions(visible)
                ? await mentionSources.DirectoryAsync(campaign, access, await actors.GetVisibleForCampaignAsync(access))
                : MentionDirectory.Empty;
            return new Preview(title, sub, WikiRender.Summary(text, mentions, length, access.IsDm), image, href, await LinksAsync(visible, mentions, href));
        }

        // Each thing the text names that the caller may open, once: mentions as this viewer knows them, and [[links]] to pages
        // they may read. A link back to the card's own target is left out.
        private async Task<IReadOnlyList<PreviewLink>> LinksAsync(string text, MentionDirectory mentions, string self)
        {
            if (!text.Contains("[[", StringComparison.Ordinal))
            {
                return Array.Empty<PreviewLink>();
            }

            var links = new List<PreviewLink>();
            foreach (var segment in mentions.Read(text))
            {
                if (segment is { Kind: { } kind, Id: { } id, Known: true })
                {
                    links.Add(new PreviewLink(segment.Text, MentionLinks.Href(campaign.Id, kind, id)));
                }
            }

            var titles = WikiText.ExtractLinks(text);
            if (titles.Count > 0)
            {
                var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
                foreach (var title in titles)
                {
                    if (WikiLinks.TargetOfTitle(title, pages) is { } page)
                    {
                        links.Add(new PreviewLink(page.Title, $"/campaigns/{campaign.Id}/wiki/{Uri.EscapeDataString(page.Slug)}"));
                    }
                }
            }

            return links.Where(l => l.Href != self).DistinctBy(l => l.Href).Take(MaxLinks).ToList();
        }
    }
}
