using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>
/// What of a campaign's characters is their player's (docs/SERVER_ACCESS_AND_VAULT.md 7 and 16): the sheet and history as a player
/// reads them (no GM notes, no secret or revealed blocks), mentions of the campaign's things turned into plain names. Taken to the
/// vault from a campaign's copy, and put in the player's personal export for the campaigns they play in.
/// </summary>
public sealed class PlayedCharacters(IActorRepository actors, MentionSources mentionSources) : IPlayedCharacters
{
    /// <summary>The characters the player plays in <paramref name="campaign"/>, as theirs (<see cref="AsTheirsAsync"/>), with the
    /// campaign's system.</summary>
    public async Task<IReadOnlyList<ActorDocument>> ForExportAsync(CampaignDocument campaign, CampaignAccess player)
    {
        var asPlayer = player.AsPlayer();
        var visible = (await actors.GetVisibleForCampaignAsync(asPlayer)).ToList();
        var mine = visible.Where(a => a.OwnerUserId == asPlayer.UserId && a.Kind == ActorKind.PlayerCharacter).ToList();
        return mine.Count == 0 ? Array.Empty<ActorDocument>() : await AsTheirsAsync(campaign, asPlayer, visible, mine);
    }

    /// <summary>New documents holding what of <paramref name="characters"/> is theirs, read through <paramref name="asPlayer"/> (a
    /// player's access) along with the rest of the <paramref name="cast"/> they see, which names the mentions. Nothing else the campaign
    /// keeps about a character comes along.</summary>
    public async Task<IReadOnlyList<ActorDocument>> AsTheirsAsync(CampaignDocument campaign, CampaignAccess asPlayer, IEnumerable<ActorDocument> cast,
        IReadOnlyList<ActorDocument> characters)
    {
        var mentions = await mentionSources.DirectoryAsync(campaign, asPlayer, cast);
        return characters.Select(c => new ActorDocument
        {
            Id = c.Id,
            CampaignId = c.CampaignId,
            OwnerUserId = c.OwnerUserId,
            Kind = c.Kind,
            SystemId = campaign.SystemId,
            Name = c.Name,
            Sheet = c.Sheet.ToDictionary(kv => kv.Key, kv => kv.Value is string text ? mentions.Plain(text) : kv.Value),
            Biography = c.Biography == null ? null : mentions.Plain(c.Biography)
        }).ToList();
    }
}
