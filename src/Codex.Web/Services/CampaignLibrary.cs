using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>One row of the Library: a pack entry or a homebrew entry, plus where it came from.</summary>
public sealed record LibraryItem(ContentEntry Entry, PackManifest? Pack, HomebrewEntryDocument? Homebrew)
{
    public bool IsHomebrew => Homebrew != null;
    public string SourceName => Homebrew != null ? "Homebrew" : Pack?.Name ?? Entry.PackId;
    public LicenseInfo? License => Pack?.License;
}

/// <summary>
/// The campaign's view of reference content: the registry's entries for the campaign's system,
/// narrowed to the packs the campaign has switched on, merged with the campaign's own homebrew.
/// Visibility is already decided by the repositories (a player only receives published homebrew);
/// this class only merges and ranks.
/// </summary>
public sealed class CampaignLibrary(IContentRegistry registry, IHomebrewRepository homebrew, ISystemCatalog catalog)
{
    public SystemDefinition? Definition(CampaignDocument campaign) => catalog.GetDefinition(campaign.SystemId);

    /// <summary>Content types available for this campaign: the system's declared types first (in
    /// declaration order), then any extra types packs or homebrew introduced.</summary>
    public async Task<IReadOnlyList<ContentTypeDefinition>> TypesAsync(CampaignDocument campaign, CampaignAccess access)
    {
        var definition = Definition(campaign);
        var declared = definition?.EntityTypes.ToList() ?? new List<ContentTypeDefinition>();
        var known = declared.Select(t => t.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extra = registry.GetEntryTypes(campaign.SystemId)
            .Concat((await homebrew.GetVisibleForCampaignAsync(access)).Select(h => h.Type))
            .Where(t => !known.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new ContentTypeDefinition { Type = t, Label = Humanize(t), PluralLabel = Humanize(t) });

        return declared.Concat(extra).ToList();
    }

    /// <summary>A homebrew entry as the Library shows it: whatever lore a flavor pack registered for its type and
    /// id fills in what the entry does not say itself.</summary>
    private ContentEntry ToEntry(HomebrewEntryDocument brew)
    {
        var entry = brew.ToEntry();
        entry.AddFlavor(registry.GetFlavor(entry.SystemId, entry.Type, entry.Id), overwrite: false);
        return entry;
    }

    public IReadOnlySet<string>? ActivePacks(CampaignDocument campaign) =>
        campaign.ActivePacks.Count == 0 ? null : campaign.ActivePacks.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<LibraryItem>> SearchAsync(CampaignDocument campaign, CampaignAccess access, string query, string? type, int limit = 200)
    {
        var packItems = registry.Search(campaign.SystemId, query, type, ActivePacks(campaign), limit)
            .Select(e => new LibraryItem(e, registry.GetPack(e.PackId), null));

        var brews = (await homebrew.GetVisibleForCampaignAsync(access))
            .Where(h => type == null || string.Equals(h.Type, type, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var brewEntries = ContentRegistry.Rank(brews.Select(ToEntry), query).ToList();
        var brewItems = brewEntries.Select(e => new LibraryItem(e, null, brews.First(b => b.EntryId == e.Id && b.Type == e.Type)));

        // Homebrew first when the query is empty (it's the table's own material), otherwise by rank
        // which Search/Rank already applied within each source; interleave by name match quality.
        var merged = ContentRegistry.Rank(brewItems.Concat(packItems).Select(i => i.Entry), query).ToList();
        var byKey = brewItems.Concat(packItems).ToDictionary(i => (i.Entry.Type, i.Entry.Id, i.IsHomebrew));
        var result = new List<LibraryItem>();
        var seen = new HashSet<(string, string, bool)>();
        foreach (var entry in merged)
        {
            foreach (var isHomebrew in new[] { true, false })
            {
                var key = (entry.Type, entry.Id, isHomebrew);
                if (byKey.TryGetValue(key, out var item) && seen.Add(key))
                {
                    result.Add(item);
                }
            }
        }

        return result.Take(limit).ToList();
    }

    /// <summary>
    /// Players may only open types the system marks <c>playerFacing</c>; the monster manual and
    /// its kin stay DM-only. The browser already hides those types from its chips and results, so
    /// the detail path (a typed-in or shared URL) has to apply the same rule.
    /// </summary>
    public bool CanRead(CampaignDocument campaign, CampaignAccess access, string type) =>
        access.IsDm
        || Definition(campaign)?.EntityTypes.FirstOrDefault(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase))?.PlayerFacing != false;

    public async Task<LibraryItem?> GetAsync(CampaignDocument campaign, CampaignAccess access, string type, string id)
    {
        if (!CanRead(campaign, access, type))
        {
            return null;
        }

        var entry = registry.GetEntry(campaign.SystemId, type, id);
        if (entry != null && (ActivePacks(campaign) is not { } packs || packs.Contains(entry.PackId)))
        {
            return new LibraryItem(entry, registry.GetPack(entry.PackId), null);
        }

        var brew = (await homebrew.GetVisibleForCampaignAsync(access))
            .FirstOrDefault(h => string.Equals(h.Type, type, StringComparison.OrdinalIgnoreCase) && h.EntryId == id);
        return brew == null ? null : new LibraryItem(ToEntry(brew), null, brew);
    }

    /// <summary>Options for an EntityReference picker: every entry of the target type, homebrew included.</summary>
    public async Task<IReadOnlyList<LibraryItem>> OptionsAsync(CampaignDocument campaign, CampaignAccess access, string type) =>
        await SearchAsync(campaign, access, string.Empty, type, limit: 2000);

    public static string Humanize(string type) =>
        string.Join(' ', type.Split('-', '_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}
