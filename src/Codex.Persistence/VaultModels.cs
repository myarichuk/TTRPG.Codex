using Codex.Core;
using Codex.Plugin.Abstractions;
using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

/// <summary>
/// A Library entry a vault character brings along because no pack has it: a subclass the AI wrote for the concept, say. It lives
/// on the character (<see cref="ActorDocument.Carried"/>), so the sheet works in the vault; when the character asks to join a
/// campaign the GM reviews each one and adds it to the campaign's Library, swaps it for one the campaign has, or leaves it out.
/// </summary>
/// <summary>What the GM did to a character when they accepted it into a campaign, for its player to read.</summary>
/// <param name="Kind">One of <see cref="JoinNote.LeftOut"/>, <see cref="JoinNote.Swapped"/> or <see cref="JoinNote.Edited"/>.</param>
/// <param name="Name">The entry, or the choice or list, it is about.</param>
/// <param name="To">What it became: the campaign's entry a swap used, or the picks an edit left. Empty for a left-out entry.</param>
/// <param name="From">What an edit replaced: the picks as the player had them. Empty for a swap or a left-out entry.</param>
/// <param name="Type">The content type of a swapped or left-out entry, so its name can be read in the viewer's language.</param>
/// <param name="Id">The id of a swapped or left-out entry.</param>
/// <param name="ToId">The id the swap used instead.</param>
public sealed record JoinNote(string Kind, string Name, string To = "", string From = "", string Type = "", string Id = "", string ToId = "")
{
    public const string LeftOut = "left-out";
    public const string Swapped = "swapped";
    public const string Edited = "edited";
}

public class CarriedEntry
{
    public string Type { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();

    public const string PackId = "carried";

    public ContentEntry ToEntry(string systemId) => new()
    {
        Id = EntryId,
        Type = Type,
        SystemId = systemId,
        PackId = PackId,
        Name = Name,
        Description = Description,
        Tags = new List<string>(Tags),
        Properties = YamlValues.NormalizeMap(Properties),
        Source = "Homebrew"
    };

    public static CarriedEntry From(ContentEntry entry) => new()
    {
        Type = entry.Type,
        EntryId = entry.Id,
        Name = entry.Name,
        Description = entry.Description,
        Tags = new List<string>(entry.Tags),
        Properties = new Dictionary<string, object>(entry.Properties)
    };

    public bool Is(string type, string id) => string.Equals(Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(EntryId, id, StringComparison.OrdinalIgnoreCase);
}

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

    /// <summary>The entries the character carries and still uses (<see cref="CarriedEntry"/>), for the GM to review: each is
    /// added to the campaign's Library, swapped for one it has, or left out when the request is accepted.</summary>
    public List<CarriedEntry> Brings { get; set; } = new();

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
