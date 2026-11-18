using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>A campaign's pack choice, read through <see cref="PackSelection"/>: the single place a <see cref="CampaignDocument"/> is turned into the rule's input.</summary>
public static class CampaignPacks
{
    public static PackChoice Choice(this CampaignDocument campaign) =>
        new(campaign.ActivePacks, campaign.PacksOn, campaign.PacksOff, campaign.AllPacks);

    /// <summary>The pack ids the campaign's pickers and lists are narrowed to; null when nothing is narrowed.</summary>
    public static IReadOnlySet<string>? Active(this IContentRegistry registry, CampaignDocument campaign) =>
        PackSelection.Resolve(campaign.Choice(), registry.GetPacks(campaign.SystemId));

    /// <summary>Whether an entry may be offered or proposed as a new pick: homebrew, an entry carried by the character, or a pack entry whose
    /// pack is on. What a campaign already holds resolves without asking this.</summary>
    public static bool IsOffered(this IReadOnlySet<string>? active, ContentEntry entry) =>
        active == null || entry.PackId.Length == 0 || entry.PackId == CarriedEntry.PackId || active.Contains(entry.PackId);
}
