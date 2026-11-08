using System.IO.Compression;
using System.Text.Json;
using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>A vault as a file (<c>vault.json</c> in the personal export): the caller's characters, brought back by
/// <see cref="PersonalExport.ImportVaultAsync"/>.</summary>
public class VaultBundle
{
    public const string CurrentFormat = "codex-vault/1";

    public string Format { get; set; } = CurrentFormat;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public List<ActorDocument> Characters { get; set; } = new();
}

/// <summary>One campaign's journal entries of the caller's, with the campaign's name to tell them apart.</summary>
public sealed record JournalExport(string CampaignId, string CampaignName, List<JournalEntryDocument> Entries);

/// <summary>
/// Exporting your own data (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15), as one zip: the vault (<c>vault.json</c>), the private
/// journals (<c>journals.json</c>: the campaign export leaves them out, being each player's own), and every campaign the caller is a
/// GM of as the campaign export makes it, with its pictures. Nothing of anyone else's that the caller could not already export: the
/// GM role is read from the campaign as stored, and no account data, key or password is in it.
/// </summary>
public sealed class PersonalExport(RavenDbService db, ICampaignRepository campaigns, IVaultRepository vault, CampaignExportService export)
{
    public const string Format = "codex-export/1";

    /// <summary>The most characters one vault file brings in, and the largest file read.</summary>
    public const int MaxImported = 500;
    public const long MaxImportBytes = 20 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task WriteAsync(string userId, string username, Stream output, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var mine = (await campaigns.GetForUserAsync(userId)).ToList();
        var gm = mine.Where(c => CampaignAccessResolver.RoleOf(c, userId) == CampaignRole.DM).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        await JsonAsync(zip, "about.json", new { Format, ExportedAt = DateTime.UtcNow, Username = username, Campaigns = gm.Select(c => c.Name).ToList() }, ct);
        await JsonAsync(zip, "vault.json", new VaultBundle { Characters = (await vault.GetAllAsync(userId)).ToList() }, ct);
        await JsonAsync(zip, "journals.json", await JournalsAsync(userId, mine), ct);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var campaign in gm)
        {
            ct.ThrowIfCancellationRequested();
            if (await export.ExportAsync(new CampaignAccess(campaign.Id, userId, CampaignRole.DM)) is not { } bundle) continue;
            var folder = Folder(campaign, used);
            await TextAsync(zip, $"campaigns/{folder}/campaign.json", CampaignExportService.Serialize(bundle), ct);

            var owners = new[] { campaign.Id }
                .Concat(bundle.Actors.Select(a => a.Id))
                .Concat(bundle.Homebrew.Select(h => h.Id))
                .Concat(bundle.Regions.Select(r => r.Id))
                .Concat(bundle.Wiki.Select(p => p.Id));
            foreach (var owner in owners.Where(id => !string.IsNullOrEmpty(id)))
            {
                await ImagesAsync(zip, owner, $"campaigns/{folder}/images/{Safe(owner)}", ct);
            }
        }
    }

    /// <summary>Reads a vault file, or the personal export holding one, and adds its characters to the caller's vault as new ones;
    /// those of a system this server does not have are left out. Null when the file is not one.</summary>
    public async Task<(int Added, int Skipped)?> ImportVaultAsync(string userId, Stream file, Func<string, bool> knownSystem)
    {
        if (await ReadVaultAsync(file) is not { } bundle) return null;
        int added = 0, skipped = 0;
        foreach (var character in bundle.Characters.Take(MaxImported))
        {
            if (string.IsNullOrEmpty(character.SystemId) || !knownSystem(character.SystemId))
            {
                skipped++;
                continue;
            }

            // Only what a character is: its name, sheet and history. Who owns it, where it lives and its ids are the vault's to set.
            var sheet = CampaignExportService.NormalizeMap(character.Sheet);
            await vault.CreateAsync(userId, character.SystemId, character.Name, a =>
            {
                a.Sheet = sheet;
                a.Biography = character.Biography;
            });
            added++;
        }

        return (added, skipped + Math.Max(0, bundle.Characters.Count - MaxImported));
    }

    private static async Task<VaultBundle?> ReadVaultAsync(Stream file)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        if (buffer.Length > MaxImportBytes) return null;
        buffer.Position = 0;
        try
        {
            // A zip (the personal export): its vault.json. Otherwise the file is the vault itself.
            if (buffer.Length > 4 && buffer.GetBuffer()[0] == 'P' && buffer.GetBuffer()[1] == 'K')
            {
                using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.GetEntry("vault.json") is not { } entry || entry.Length > MaxImportBytes) return null;
                // The size an archive states is its own claim: read at most the bound, whatever it says.
                using var json = new MemoryStream();
                await using (var stream = entry.Open())
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(chunk)) > 0)
                    {
                        if (json.Length + read > MaxImportBytes) return null;
                        json.Write(chunk, 0, read);
                    }
                }

                json.Position = 0;
                return Checked(await JsonSerializer.DeserializeAsync<VaultBundle>(json));
            }

            return Checked(await JsonSerializer.DeserializeAsync<VaultBundle>(buffer));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static VaultBundle? Checked(VaultBundle? bundle) => bundle is { Format: VaultBundle.CurrentFormat } ? bundle : null;

    private async Task<List<JournalExport>> JournalsAsync(string userId, IReadOnlyList<CampaignDocument> mine)
    {
        using var session = db.Store.OpenAsyncSession();
        var entries = await session.Query<JournalEntryDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(j => j.OwnerUserId == userId)
            .Take(int.MaxValue)
            .ToListAsync();
        var names = mine.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        return entries.GroupBy(e => e.CampaignId)
            .Select(g => new JournalExport(g.Key, names.GetValueOrDefault(g.Key, string.Empty), g.OrderBy(e => e.CreatedAt).ToList()))
            .OrderBy(j => j.CampaignName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task ImagesAsync(ZipArchive zip, string documentId, string folder, CancellationToken ct)
    {
        using var session = db.Store.OpenAsyncSession();
        if (await session.LoadAsync<object>(documentId) is not { } document) return;
        foreach (var attachment in session.Advanced.Attachments.GetNames(document))
        {
            using var stored = await session.Advanced.Attachments.GetAsync(documentId, attachment.Name);
            if (stored == null) continue;
            var entry = zip.CreateEntry($"{folder}/{Safe(attachment.Name)}{Extension(attachment.ContentType)}", CompressionLevel.NoCompression);
            await using var to = entry.Open();
            await stored.Stream.CopyToAsync(to, ct);
        }
    }

    private static string Folder(CampaignDocument campaign, HashSet<string> used)
    {
        var name = Safe(campaign.Name).Trim('_');
        if (name.Length == 0) name = "campaign";
        if (name.Length > 60) name = name[..60];
        var folder = name;
        for (var n = 2; !used.Add(folder); n++) folder = $"{name}-{n}";
        return folder;
    }

    private static async Task JsonAsync<T>(ZipArchive zip, string path, T value, CancellationToken ct) =>
        await TextAsync(zip, path, JsonSerializer.Serialize(value, JsonOptions), ct);

    private static async Task TextAsync(ZipArchive zip, string path, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var to = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        await to.WriteAsync(text.AsMemory(), ct);
    }

    private static string Safe(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static string Extension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/avif" => ".avif",
        _ => ".bin"
    };
}
