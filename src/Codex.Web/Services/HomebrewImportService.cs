using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>
/// Moves homebrew between campaigns and clones pack entries into homebrew. Importing from
/// another campaign requires the caller to be a DM there too (resolved, never assumed), so a
/// DM can carry their own material between tables but not lift someone else's.
/// </summary>
public sealed class HomebrewImportService(IHomebrewRepository homebrew, ICampaignRepository campaigns, ICampaignAccessResolver resolver)
{
    /// <summary>Campaigns on the same system where the caller is a DM, other than the current one.</summary>
    public async Task<IReadOnlyList<CampaignDocument>> SourceCampaignsAsync(CampaignDocument current, CampaignAccess access)
    {
        var result = new List<CampaignDocument>();
        foreach (var campaign in await campaigns.GetForUserAsync(access.UserId))
        {
            if (campaign.Id == current.Id || !string.Equals(campaign.SystemId, current.SystemId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceAccess = await resolver.ResolveAsync(campaign.Id, access.UserId);
            if (sourceAccess is { IsDm: true })
            {
                result.Add(campaign);
            }
        }

        return result.OrderBy(c => c.Name).ToList();
    }

    /// <summary>Entries of a source campaign the caller may import (they must be its DM).</summary>
    public async Task<IReadOnlyList<HomebrewEntryDocument>> EntriesOfAsync(string sourceCampaignId, CampaignAccess access)
    {
        var sourceAccess = await resolver.ResolveAsync(sourceCampaignId, access.UserId);
        if (sourceAccess is not { IsDm: true })
        {
            return Array.Empty<HomebrewEntryDocument>();
        }

        return (await homebrew.GetAllForCampaignAsync(sourceCampaignId)).OrderBy(h => h.Type).ThenBy(h => h.Name).ToList();
    }

    /// <summary>Copies the chosen entries into the target campaign as new documents. Returns how many were copied; an entry whose EntryId already exists in the target is skipped.</summary>
    public async Task<int> ImportAsync(string sourceCampaignId, IEnumerable<string> entryIds, CampaignDocument target, CampaignAccess access)
    {
        if (!access.IsDm || access.CampaignId != target.Id)
        {
            return 0;
        }

        var source = await campaigns.GetAsync(sourceCampaignId);
        var candidates = (await EntriesOfAsync(sourceCampaignId, access)).ToDictionary(h => h.Id);
        var existing = (await homebrew.GetAllForCampaignAsync(target.Id)).Select(h => (h.Type, h.EntryId)).ToHashSet();
        var copied = 0;
        foreach (var id in entryIds)
        {
            if (!candidates.TryGetValue(id, out var entry) || existing.Contains((entry.Type, entry.EntryId)))
            {
                continue;
            }

            var clone = Clone(entry, target, access);
            clone.ImportedFrom = $"campaign “{source?.Name ?? sourceCampaignId}”";
            if (await homebrew.TrySaveAsync(clone, access))
            {
                copied++;
                existing.Add((clone.Type, clone.EntryId));
            }
        }

        return copied;
    }

    /// <summary>A homebrew copy of a pack entry, ready for the editor: same fields, new id, marked as a draft until the DM saves it.</summary>
    public static HomebrewEntryDocument CloneFromPack(ContentEntry entry, PackManifest? pack, CampaignDocument target, CampaignAccess access) => new()
    {
        CampaignId = target.Id,
        SystemId = target.SystemId,
        Type = entry.Type,
        EntryId = entry.Id + "-variant",
        Name = entry.Name + " (variant)",
        Description = entry.Description,
        Tags = new List<string>(entry.Tags),
        Properties = DeepCopy(entry.Properties),
        IsPublished = false,
        AuthorUserId = access.UserId,
        ImportedFrom = pack?.Name ?? entry.PackId
    };

    private static HomebrewEntryDocument Clone(HomebrewEntryDocument entry, CampaignDocument target, CampaignAccess access) => new()
    {
        CampaignId = target.Id,
        SystemId = target.SystemId,
        Type = entry.Type,
        EntryId = entry.EntryId,
        Name = entry.Name,
        Description = entry.Description,
        Tags = new List<string>(entry.Tags),
        Properties = DeepCopy(entry.Properties),
        IsPublished = entry.IsPublished,
        AuthorUserId = access.UserId
    };

    private static Dictionary<string, object> DeepCopy(Dictionary<string, object> map) =>
        map.ToDictionary(kv => kv.Key, kv => CopyValue(kv.Value));

    private static object CopyValue(object value) => value switch
    {
        IDictionary<string, object> dict => dict.ToDictionary(kv => kv.Key, kv => CopyValue(kv.Value)),
        string s => s,
        IEnumerable<object> list => list.Select(CopyValue).ToList(),
        _ => value
    };
}
