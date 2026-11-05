using System.Globalization;
using System.Text.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

/// <summary>What a backup wrote.</summary>
public sealed record BackupResult(string Folder, int Campaigns, int Images, int Users);

/// <summary>
/// The backup the app makes itself (D11). RavenDB's own periodic backup needs a licence the free one is not, so every day
/// the server writes each campaign as the same bundle "Export campaign" makes (with its images beside it, and the players'
/// journals, which an export leaves out), and the accounts, into <c>Backups/&lt;time&gt;/</c>:
/// <code>
/// backup.json                              when, and what is in it
/// users.json                               accounts and their AI settings; never an API key
/// campaigns/&lt;id&gt;/campaign.json            restore with Settings → "restore it as a new campaign"
/// campaigns/&lt;id&gt;/journals.json
/// campaigns/&lt;id&gt;/book-drafts.json         the GM's edited book (drafts and chapters), which an export leaves out
/// campaigns/&lt;id&gt;/images/&lt;document&gt;/&lt;name&gt;.&lt;ext&gt;
/// </code>
/// A backup is written under a ".partial" name and renamed when complete, so a folder with the final name is whole.
/// </summary>
public sealed class JsonBackups(RavenDbService db, ICampaignRepository campaigns, CampaignExportService export)
{
    public const string FolderFormat = "yyyy-MM-dd_HHmmss";
    private const string Partial = ".partial";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<BackupResult> WriteAsync(string root, DateTime utcNow, CancellationToken ct = default)
    {
        var name = utcNow.ToString(FolderFormat, CultureInfo.InvariantCulture);
        var final = Path.Combine(root, name);
        var working = final + Partial;
        if (Directory.Exists(working))
        {
            Directory.Delete(working, recursive: true);
        }

        Directory.CreateDirectory(working);
        int campaignCount = 0, imageCount = 0;
        foreach (var campaign in await campaigns.GetAllAsync())
        {
            ct.ThrowIfCancellationRequested();
            var bundle = await export.ExportForBackupAsync(campaign);
            if (bundle == null)
            {
                continue;
            }

            var folder = Path.Combine(working, "campaigns", Safe(campaign.Id));
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "campaign.json"), CampaignExportService.Serialize(bundle), ct);
            await WriteJsonAsync(Path.Combine(folder, "journals.json"), await JournalsAsync(campaign.Id), ct);
            await WriteJsonAsync(Path.Combine(folder, "book-drafts.json"), await BookDraftsAsync(campaign.Id), ct);

            var owners = new[] { campaign.Id }
                .Concat(bundle.Actors.Select(a => a.Id))
                .Concat(bundle.Homebrew.Select(h => h.Id))
                .Concat(bundle.Regions.Select(r => r.Id))
                .Concat(bundle.Wiki.Select(p => p.Id))
                // The GM's book pictures cost money to draw; the export leaves them out, the backup keeps them.
                .Append(BookArtDocument.IdFor(campaign.Id));
            foreach (var owner in owners.Where(id => !string.IsNullOrEmpty(id)))
            {
                imageCount += await WriteImagesAsync(owner, Path.Combine(folder, "images", Safe(owner)), ct);
            }

            campaignCount++;
        }

        var users = await UsersAsync();
        await WriteJsonAsync(Path.Combine(working, "users.json"), users, ct);
        await WriteJsonAsync(Path.Combine(working, "backup.json"), new { WrittenAt = utcNow, Campaigns = campaignCount, Images = imageCount, Users = users.Count }, ct);

        Directory.Move(working, final);
        return new BackupResult(final, campaignCount, imageCount, users.Count);
    }

    /// <summary>When the newest complete backup under <paramref name="root"/> was written; null when there is none.</summary>
    public static DateTime? LastWrittenAt(string root) => Complete(root).Select(b => (DateTime?)b.At).FirstOrDefault();

    /// <summary>Deletes all but the newest <paramref name="keep"/> complete backups, and any left half-written; returns what it deleted.
    /// Only folders this class names are touched.</summary>
    public static IReadOnlyList<string> Prune(string root, int keep)
    {
        var doomed = Complete(root).Skip(Math.Max(1, keep)).Select(b => b.Path).ToList();
        if (Directory.Exists(root))
        {
            doomed.AddRange(Directory.GetDirectories(root, "*" + Partial).Where(p => ParseName(Path.GetFileName(p)[..^Partial.Length]) != null));
        }

        foreach (var path in doomed)
        {
            Directory.Delete(path, recursive: true);
        }

        return doomed;
    }

    private static IEnumerable<(string Path, DateTime At)> Complete(string root) =>
        !Directory.Exists(root)
            ? Enumerable.Empty<(string, DateTime)>()
            : Directory.GetDirectories(root)
                .Select(p => (Path: p, At: ParseName(System.IO.Path.GetFileName(p))))
                .Where(b => b.At != null && File.Exists(System.IO.Path.Combine(b.Path, "backup.json")))
                .Select(b => (b.Path, b.At!.Value))
                .OrderByDescending(b => b.Value);

    private static DateTime? ParseName(string name) =>
        DateTime.TryParseExact(name, FolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private async Task<int> WriteImagesAsync(string documentId, string folder, CancellationToken ct)
    {
        using var session = db.Store.OpenAsyncSession();
        var document = await session.LoadAsync<object>(documentId);
        if (document == null)
        {
            return 0;
        }

        var written = 0;
        foreach (var attachment in session.Advanced.Attachments.GetNames(document))
        {
            using var stored = await session.Advanced.Attachments.GetAsync(documentId, attachment.Name);
            if (stored == null)
            {
                continue;
            }

            Directory.CreateDirectory(folder);
            await using var file = File.Create(Path.Combine(folder, Safe(attachment.Name) + Extension(attachment.ContentType)));
            await stored.Stream.CopyToAsync(file, ct);
            written++;
        }

        return written;
    }

    /// <summary>The GM's book drafts: hand-written work, so the backup keeps it, drafts and their chapters as stored.</summary>
    private async Task<object> BookDraftsAsync(string campaignId)
    {
        using var session = db.Store.OpenAsyncSession();
        var drafts = new List<BookDraftDocument>();
        var chapters = new List<BookDraftChapter>();
        foreach (var edition in new[] { BookDraftDocument.Party, BookDraftDocument.Dm })
        {
            foreach (var language in BookDraftDocument.Languages)
            {
                var id = BookDraftDocument.IdFor(campaignId, edition, language);
                if (await session.LoadAsync<BookDraftDocument>(id) is not { } draft) continue;
                drafts.Add(draft);
                chapters.AddRange(await session.Advanced.LoadStartingWithAsync<BookDraftChapter>(id + "/chapters/", pageSize: 1024));
            }
        }

        return new { Drafts = drafts, Chapters = chapters };
    }

    private async Task<List<JournalEntryDocument>> JournalsAsync(string campaignId)
    {
        using var session = db.Store.OpenAsyncSession();
        return await session.Query<JournalEntryDocument>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(j => j.CampaignId == campaignId)
            .ToListAsync();
    }

    /// <summary>Every account, with its AI settings but never a key: a key is encrypted for this server, and a backup is a file
    /// someone may copy elsewhere. Password hashes stay, or no one could sign in to a restored server.</summary>
    private async Task<List<UserDocument>> UsersAsync()
    {
        using var session = db.Store.OpenAsyncSession();
        var users = await session.Query<UserDocument>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        session.Advanced.Clear();
        foreach (var provider in users.SelectMany(u => u.AiProviders.Concat(u.ImageProviders)))
        {
            provider.ProtectedApiKey = string.Empty;
        }

        return users;
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);

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
