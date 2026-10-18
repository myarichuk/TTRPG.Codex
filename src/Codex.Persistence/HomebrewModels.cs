using Codex.Plugin.Abstractions;

namespace Codex.Persistence;

/// <summary>
/// A piece of table-made content (a homebrew spell, a custom monster, a house rule entry) that
/// lives with a campaign rather than in a pack on disk. Same shape as <see cref="ContentEntry"/>
/// so the Library can list, search and pick it alongside SRD material; the DM can later export a
/// campaign's homebrew as a real pack.
/// </summary>
public class HomebrewEntryDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;

    /// <summary>A content type of the campaign's system ("spell", "monster"...), or a free type for systems without schemas.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Stable id inside the campaign, slug-like; becomes the entry id when exported.</summary>
    public string EntryId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>True once the DM wants players to see it in their Library; false while drafting.</summary>
    public bool IsPublished { get; set; } = true;

    public string AuthorUserId { get; set; } = string.Empty;

    /// <summary>Set when the entry was copied in from another campaign or cloned from a pack entry: where it came from, for the attribution line.</summary>
    public string? ImportedFrom { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public const string PackIdPrefix = "homebrew:";

    public ContentEntry ToEntry() => new()
    {
        Id = EntryId,
        Type = Type,
        SystemId = SystemId,
        PackId = PackIdPrefix + CampaignId,
        Name = Name,
        Description = Description,
        Tags = new List<string>(Tags),
        Properties = new Dictionary<string, object>(Properties),
        Source = "Homebrew"
    };
}

public interface IHomebrewRepository
{
    /// <summary>Entries of a campaign the caller may see: everything for the DM, published only for players.</summary>
    Task<IEnumerable<HomebrewEntryDocument>> GetVisibleForCampaignAsync(CampaignAccess access);

    Task<HomebrewEntryDocument?> GetVisibleAsync(string id, CampaignAccess access);

    /// <summary>Creates or updates an entry. DM only. Returns false (and saves nothing) otherwise.</summary>
    Task<bool> TrySaveAsync(HomebrewEntryDocument entry, CampaignAccess access);

    /// <summary>Deletes one entry. DM only.</summary>
    Task<bool> TryDeleteAsync(string id, CampaignAccess access);

    /// <summary>Every entry in a campaign regardless of publication - export/backup only.</summary>
    Task<IEnumerable<HomebrewEntryDocument>> GetAllForCampaignAsync(string campaignId);

    Task SaveAsync(HomebrewEntryDocument entry);

    Task DeleteAllForCampaignAsync(string campaignId);
}
