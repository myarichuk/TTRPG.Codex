using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

/// <summary>A player asking to bring a character from their vault into a campaign, or to bring a newer vault version over the
/// campaign's copy (docs/SERVER_ACCESS_AND_VAULT.md 7). No actor exists in the campaign until a GM accepts, so a character
/// waiting here is in no list, table or book of the campaign.</summary>
public class CharacterRequestDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;

    /// <summary>The player asking, who will play the character.</summary>
    public string UserId { get; set; } = string.Empty;

    public string VaultActorId { get; set; } = string.Empty;

    /// <summary>When set, the campaign's copy this request would update instead of adding a new character.</summary>
    public string? ReplacesActorId { get; set; }

    /// <summary>The character as it would come in: copied from the vault, less what the player chose to leave out.</summary>
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object> Sheet { get; set; } = new();
    public string? Biography { get; set; }

    /// <summary>Names of what the campaign does not have and the player left out of the copy.</summary>
    public List<string> LeftOut { get; set; } = new();

    /// <summary>Names of what the campaign does not have and the player asks the GM to add. Kept in the copy, so the sheet
    /// shows it as missing until the GM adds it.</summary>
    public List<string> AskedFor { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CharacterRequestsByCampaignIndex : AbstractIndexCreationTask<CharacterRequestDocument>
{
    public CharacterRequestsByCampaignIndex()
    {
        Map = requests => from request in requests
                          select new
                          {
                              request.CampaignId,
                              request.UserId,
                              request.VaultActorId,
                              request.CreatedAt
                          };
    }
}
