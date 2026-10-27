using System.IO.Compression;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly string[] NonEntryFolders = { "abilities", "actors", "locations", "flavor" };
    private const int MaxZipEntries = 5000;
    private const long MaxYamlBytes = 1024 * 1024;

    /// <summary>
    /// Imports a content-pack .zip (as written by "Export pack") into the target campaign's homebrew.
    /// The pack must be for the campaign's system; entries whose type and id already exist are skipped.
    /// Entries land as drafts so the DM reviews them before players see anything.
    /// </summary>
    public async Task<PackImportResult> ImportPackAsync(Stream zip, CampaignDocument target, CampaignAccess access)
    {
        if (!access.IsDm || access.CampaignId != target.Id)
        {
            return PackImportResult.Fail(PackImportError.NotAllowed);
        }

        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxZipEntries)
            {
                return PackImportResult.Fail(PackImportError.NotAPack);
            }

            var manifestEntry = archive.GetEntry("manifest.yaml");
            if (manifestEntry == null)
            {
                return PackImportResult.Fail(PackImportError.NotAPack);
            }

            var manifest = Yaml.Deserialize<PackManifest>(await ReadTextAsync(manifestEntry));
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                return PackImportResult.Fail(PackImportError.NotAPack);
            }

            if (!string.Equals(manifest.SystemId, target.SystemId, StringComparison.OrdinalIgnoreCase))
            {
                return PackImportResult.Fail(PackImportError.WrongSystem, manifest.SystemId);
            }

            var existing = (await homebrew.GetAllForCampaignAsync(target.Id)).Select(h => (h.Type, h.EntryId)).ToHashSet();
            int imported = 0, skipped = 0;
            foreach (var file in archive.Entries)
            {
                var segments = file.FullName.Replace('\\', '/').TrimStart('/').Split('/');
                if (segments.Length < 2 || !file.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    || NonEntryFolders.Contains(segments[0], StringComparer.OrdinalIgnoreCase) || segments[0].StartsWith('.'))
                {
                    continue;
                }

                foreach (var entry in ParseEntries(await ReadTextAsync(file)))
                {
                    var type = string.IsNullOrWhiteSpace(entry.Type) ? segments[0].ToLowerInvariant() : entry.Type;
                    if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || existing.Contains((type, entry.Id)))
                    {
                        skipped++;
                        continue;
                    }

                    var doc = new HomebrewEntryDocument
                    {
                        CampaignId = target.Id,
                        SystemId = target.SystemId,
                        Type = type,
                        EntryId = entry.Id,
                        Name = entry.Name,
                        Description = entry.Description,
                        Tags = new List<string>(entry.Tags),
                        Properties = entry.Properties.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
                        IsPublished = false,
                        AuthorUserId = access.UserId,
                        ImportedFrom = $"pack “{manifest.Name}”"
                    };
                    if (await homebrew.TrySaveAsync(doc, access))
                    {
                        imported++;
                        existing.Add((type, entry.Id));
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            return new PackImportResult(imported, skipped, null, manifest.Name);
        }
        catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return PackImportResult.Fail(PackImportError.NotAPack);
        }
    }

    private static IEnumerable<ContentEntry> ParseEntries(string yaml)
    {
        try
        {
            var list = Yaml.Deserialize<List<ContentEntry>>(yaml);
            if (list != null)
            {
                return list;
            }
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Not a list - the single-entry shape below.
        }

        try
        {
            var one = Yaml.Deserialize<ContentEntry>(yaml);
            return one == null ? Array.Empty<ContentEntry>() : new[] { one };
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return Array.Empty<ContentEntry>();
        }
    }

    /// <summary>Reads a bounded amount, so a hostile archive cannot inflate into memory.</summary>
    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxYamlBytes)
        {
            throw new InvalidDataException("Entry too large.");
        }

        using var reader = new StreamReader(entry.Open());
        var buffer = new char[MaxYamlBytes + 1];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        return read > MaxYamlBytes ? throw new InvalidDataException("Entry too large.") : new string(buffer, 0, read);
    }

    /// <summary>YamlDotNet nests untyped maps as Dictionary&lt;object, object&gt;; the database wants string keys.</summary>
    private static object Plain(object value) => value switch
    {
        System.Collections.IDictionary map => map.Keys.Cast<object>().ToDictionary(k => k.ToString() ?? string.Empty, k => Plain(map[k]!)),
        string s => s,
        System.Collections.IEnumerable list => list.Cast<object>().Select(Plain).ToList(),
        _ => value
    };

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

public enum PackImportError { NotAllowed, NotAPack, WrongSystem }

public sealed record PackImportResult(int Imported, int Skipped, PackImportError? Error, string? Detail)
{
    public static PackImportResult Fail(PackImportError error, string? detail = null) => new(0, 0, error, detail);
}
