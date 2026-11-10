using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>
/// A theme the GM brought into a campaign with the theme wizard (docs/BOOK_EXPORT.md 41.22): a Homebrewery theme's stylesheet as pasted, whether
/// it sits on Homebrewery's 5e layer, the languages it prints (measured from its faces when it was saved, with the stand-ins the GM chose for the
/// scripts its faces lack), and the copies of the web addresses it names, as attachments on its own web document. It lives under the campaign's
/// book prefix, so it goes with the campaign; only the campaign's GM reads or writes it.
/// </summary>
public class BookThemeDocument
{
    public const int MaxPerCampaign = 20;
    public const int MaxNameChars = 60;

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Css { get; set; } = string.Empty;
    public bool Homebrewery { get; set; } = true;

    /// <summary>The languages the wizard found it prints, with the stand-ins chosen; English when its text faces have the Latin letters.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>By language, the stand-in (<c>BookStandIns</c> id) chosen for each family whose faces lack that language's letters.</summary>
    public Dictionary<string, Dictionary<string, string>> StandIns { get; set; } = new();

    /// <summary>Saved once the GM finished the wizard; until then it is not offered in the theme picker.</summary>
    public bool Ready { get; set; }

    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string PrefixFor(string campaignId) => BookDraftDocument.PrefixFor(campaignId) + "themes/";

    /// <summary>The short key a draft names the theme by after <c>custom:</c>: hex, so it is never a path.</summary>
    public string Key => Id[(Id.LastIndexOf('/') + 1)..];

    public static string IdFor(string campaignId, string key) => PrefixFor(campaignId) + key;

    public static bool IsKey(string? key) => key is { Length: 32 } && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string WebIdFor(string id) => id + "/web";
}

public partial interface IBookDraftRepository
{
    /// <summary>The campaign's themes, ready or not; none for anyone who is not its GM now.</summary>
    Task<IReadOnlyList<BookThemeDocument>> ThemesAsync(CampaignAccess dm);

    /// <summary>One of the campaign's themes by its key; null when there is none or for anyone who is not its GM now.</summary>
    Task<BookThemeDocument?> ThemeAsync(CampaignAccess dm, string key);

    /// <summary>A theme made (no key) or saved at <paramref name="version"/>. Too long a stylesheet or name, or too many themes, is refused.</summary>
    Task<(BookDraftSave Result, BookThemeDocument? Theme)> SaveThemeAsync(CampaignAccess dm, string? key, string name, string css, bool homebrewery,
        IReadOnlyList<string> languages, IReadOnlyDictionary<string, Dictionary<string, string>> standIns, bool ready, int version);

    /// <summary>The theme and its copies, gone. A draft that names it prints in Plain.</summary>
    Task<bool> DeleteThemeAsync(CampaignAccess dm, string key);

    /// <summary>The copies of the web addresses the theme's stylesheet names.</summary>
    Task<Codex.Core.Book.BookCssWeb> ThemeWebAsync(CampaignAccess dm, string key);

    /// <summary>As <see cref="SaveWebAsync"/>, for a theme's copies.</summary>
    Task<BookDraftSave> SaveThemeWebAsync(CampaignAccess dm, string key, IReadOnlyList<Codex.Core.Book.BookCssWebFile> added, IReadOnlySet<string> keep);
}

public partial class RavenBookDraftRepository
{
    private async Task<CampaignAccess?> GmNowAsync(CampaignAccess dm) => await CampaignAccessResolver.NowAsync(dbService.Store, dm) is { IsDm: true } now ? now : null;

    public async Task<IReadOnlyList<BookThemeDocument>> ThemesAsync(CampaignAccess dm)
    {
        if (await GmNowAsync(dm) is not { } now) return [];
        using var session = dbService.Store.OpenAsyncSession();
        var all = await session.Advanced.LoadStartingWithAsync<BookThemeDocument>(BookThemeDocument.PrefixFor(now.CampaignId), pageSize: 256);
        // The web documents share the prefix; a theme's id ends in its key.
        return all.Where(t => t.CampaignId == now.CampaignId && BookThemeDocument.IsKey(t.Key) && t.Id == BookThemeDocument.IdFor(now.CampaignId, t.Key))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<BookThemeDocument?> ThemeAsync(CampaignAccess dm, string key)
    {
        if (!BookThemeDocument.IsKey(key) || await GmNowAsync(dm) is not { } now) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var theme = await session.LoadAsync<BookThemeDocument>(BookThemeDocument.IdFor(now.CampaignId, key));
        return theme?.CampaignId == now.CampaignId ? theme : null;
    }

    public async Task<(BookDraftSave Result, BookThemeDocument? Theme)> SaveThemeAsync(CampaignAccess dm, string? key, string name, string css, bool homebrewery,
        IReadOnlyList<string> languages, IReadOnlyDictionary<string, Dictionary<string, string>> standIns, bool ready, int version)
    {
        name = Clip(name, BookThemeDocument.MaxNameChars);
        if (css.Length > BookDraftDocument.MaxCssChars) return (BookDraftSave.TooLarge, null);
        if (name.Length == 0 || (key != null && !BookThemeDocument.IsKey(key))) return (BookDraftSave.NotAllowed, null);
        if (await GmNowAsync(dm) is not { } now) return (BookDraftSave.NotAllowed, null);
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        BookThemeDocument? theme;
        if (key == null)
        {
            var count = (await session.Advanced.LoadStartingWithAsync<BookThemeDocument>(BookThemeDocument.PrefixFor(now.CampaignId), pageSize: 256))
                .Count(t => BookThemeDocument.IsKey(t.Key));
            if (count >= BookThemeDocument.MaxPerCampaign) return (BookDraftSave.TooLarge, null);
            var id = BookThemeDocument.IdFor(now.CampaignId, Guid.NewGuid().ToString("N"));
            theme = new BookThemeDocument { Id = id, CampaignId = now.CampaignId };
            await session.StoreAsync(theme, id);
        }
        else
        {
            theme = await session.LoadAsync<BookThemeDocument>(BookThemeDocument.IdFor(now.CampaignId, key));
            if (theme == null || theme.CampaignId != now.CampaignId) return (BookDraftSave.NotAllowed, null);
            if (theme.Version != version) return (BookDraftSave.Conflict, theme);
        }

        theme.Name = name;
        theme.Css = css;
        theme.Homebrewery = homebrewery;
        // Only languages a book has, and stand-ins only for those, by the ids the catalogue has: what is stored is what the wizard can offer.
        theme.Languages = languages.Where(BookDraftDocument.IsLanguage).Distinct().ToList();
        theme.StandIns = standIns.Where(l => BookDraftDocument.IsLanguage(l.Key)).ToDictionary(l => l.Key,
            l => l.Value.Where(f => f.Key.Length is > 0 and <= 120 && Codex.Core.Book.BookStandIns.Find(f.Value)?.Language == l.Key)
                .Take(64).ToDictionary(f => f.Key, f => f.Value));
        theme.Ready = ready;
        theme.Version++;
        theme.UpdatedAt = DateTime.UtcNow;
        try
        {
            await session.SaveChangesAsync();
            return (BookDraftSave.Saved, theme);
        }
        catch (ConcurrencyException)
        {
            return (BookDraftSave.Conflict, null);
        }
    }

    public async Task<bool> DeleteThemeAsync(CampaignAccess dm, string key)
    {
        if (!BookThemeDocument.IsKey(key) || await GmNowAsync(dm) is not { } now) return false;
        using var session = dbService.Store.OpenAsyncSession();
        var id = BookThemeDocument.IdFor(now.CampaignId, key);
        var theme = await session.LoadAsync<BookThemeDocument>(id);
        if (theme == null || theme.CampaignId != now.CampaignId) return false;
        session.Delete(theme);
        // Its web document (with the copies as attachments) goes with it.
        session.Delete(BookThemeDocument.WebIdFor(id));
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<Codex.Core.Book.BookCssWeb> ThemeWebAsync(CampaignAccess dm, string key)
    {
        if (!BookThemeDocument.IsKey(key) || await GmNowAsync(dm) is not { } now) return new Codex.Core.Book.BookCssWeb();
        using var session = dbService.Store.OpenAsyncSession();
        var id = BookThemeDocument.WebIdFor(BookThemeDocument.IdFor(now.CampaignId, key));
        var web = await session.LoadAsync<BookDraftWeb>(id);
        if (web == null || web.CampaignId != now.CampaignId) return new Codex.Core.Book.BookCssWeb();
        var files = new List<Codex.Core.Book.BookCssWebFile>();
        foreach (var entry in web.Files)
        {
            using var attachment = await session.Advanced.Attachments.GetAsync(id, entry.Name);
            if (attachment == null) continue;
            using var buffer = new MemoryStream();
            await attachment.Stream.CopyToAsync(buffer);
            files.Add(new Codex.Core.Book.BookCssWebFile(entry.Url, entry.Kind, entry.ContentType, buffer.ToArray()));
        }

        return new Codex.Core.Book.BookCssWeb(files);
    }

    public async Task<BookDraftSave> SaveThemeWebAsync(CampaignAccess dm, string key, IReadOnlyList<Codex.Core.Book.BookCssWebFile> added, IReadOnlySet<string> keep)
    {
        if (!BookThemeDocument.IsKey(key) || !Copyable(added) || await GmNowAsync(dm) is not { } now) return BookDraftSave.NotAllowed;
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var id = BookThemeDocument.IdFor(now.CampaignId, key);
        if (await session.LoadAsync<BookThemeDocument>(id) is not { } theme || theme.CampaignId != now.CampaignId) return BookDraftSave.NotAllowed;
        return await StoreWebAsync(session, now.CampaignId, BookThemeDocument.WebIdFor(id), added, keep);
    }
}
