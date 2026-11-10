using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>
/// The editable book (docs/BOOK_EXPORT.md 41): Markdown generated from the campaign, which the GM then edits freely. One draft per campaign,
/// edition and language (<c>bookdrafts/{campaign}/{edition}/{language}</c>); chapter titles are translated, so another language is another draft.
/// The draft holds the settings and the order of its chapters; each chapter is its own document, edited one at a time, so two tabs editing two
/// chapters never collide, and two tabs editing one chapter are told so instead of one silently overwriting the other.
/// <para>It is the GM's: only the campaign's GM, as the campaign stores it now, reads or writes it. The party draft is generated from the party
/// source only, but what the GM types into it is the GM's own words; see <see cref="BookDraftChapter"/>.</para>
/// </summary>
public class BookDraftDocument
{
    public const string Party = "party";
    public const string Dm = "dm";

    /// <summary>Bounds that hold whatever is posted: a chapter's text, the GM's stylesheet, the number of chapters.</summary>
    public const int MaxChapterChars = 400_000;
    public const int MaxCssChars = Codex.Core.Book.BookCss.MaxLength;
    public const int MaxChapters = 40;

    /// <summary>Languages a draft can be made in: the app's own. Anything else posted as a language is refused before it reaches an id.</summary>
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(["en", "ru", "he"], StringComparer.Ordinal);

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Edition { get; set; } = Party;
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "plain";

    /// <summary>Homebrewery's 5e layout under the theme (docs/BOOK_EXPORT.md 41.12), so CSS written for a Homebrewery brew lands where it does
    /// there. On unless the GM turns it off; a draft saved before the setting existed reads as on.</summary>
    public bool Homebrewery { get; set; } = true;

    /// <summary>Torn page edges with paper fibre (docs/BOOK_EXPORT.md 46), over whatever the theme draws. Off unless the GM turns it on.</summary>
    public bool Edge { get; set; }
    public string PageSize { get; set; } = "Letter";

    /// <summary>The GM's own stylesheet, applied after the theme (cleaned on the way out, as the book's custom CSS always is).</summary>
    public string Css { get; set; } = string.Empty;

    /// <summary>The chapters' section ids, in book order.</summary>
    public List<string> Chapters { get; set; } = new();

    /// <summary>Counts every save of the settings, so a save made from an older copy is refused instead of overwriting a newer one.</summary>
    public int Version { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static bool IsEdition(string? edition) => edition is Party or Dm;

    public static bool IsLanguage(string? language) => language != null && Languages.Contains(language);

    /// <summary>A section id as the generator makes them: lowercase letters and digits, a dash at most between them. Anything else is refused.</summary>
    public static bool IsSection(string? section) =>
        section is { Length: > 0 and <= 40 } && section.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && section[0] != '-' && section[^1] != '-';

    /// <summary>A chapter of the GM's own (docs/BOOK_EXPORT.md 51): one the campaign does not make. Its id says so, <c>own-</c> and a GUID's 32
    /// digits, a form no generated chapter has; it has no generated text, so it never follows the campaign and is never stale.</summary>
    public const string OwnPrefix = "own-";

    public static bool IsOwn(string? section) =>
        section is { Length: 36 } && section.StartsWith(OwnPrefix, StringComparison.Ordinal) && section.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static string NewOwn() => OwnPrefix + Guid.NewGuid().ToString("N");

    /// <summary>The longest title a chapter of the GM's own may have.</summary>
    public const int MaxTitleChars = 200;

    public static string IdFor(string campaignId, string edition, string language) => $"bookdrafts/{campaignId}/{edition}/{language}";

    public static string ChapterIdFor(string campaignId, string edition, string language, string section) => IdFor(campaignId, edition, language) + "/chapters/" + section;

    /// <summary>Every draft and chapter of a campaign; ends in a slash, so "campaigns/1" never takes "campaigns/10".</summary>
    public static string PrefixFor(string campaignId) => $"bookdrafts/{campaignId}/";
}

/// <summary>
/// One chapter of a draft. <see cref="Markdown"/> is what the GM has now; <see cref="Base"/> is the generated text the GM's edits started from
/// (the text itself, not only its hash: a three-way merge needs it), and <see cref="BaseKey"/> says what it was generated from, so a chapter can
/// tell that the campaign changed since. A chapter whose text is still its base was never edited and simply follows the campaign.
/// </summary>
public class BookDraftChapter
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;

    /// <summary>The chapter's title as last generated, for the list of chapters (the GM's text may have renamed it).</summary>
    public string Title { get; set; } = string.Empty;

    public string Markdown { get; set; } = string.Empty;
    public string Base { get; set; } = string.Empty;
    public string BaseKey { get; set; } = string.Empty;

    /// <summary>The key the GM said "keep mine" to: the notice stays away until the campaign changes again (the key moves on).</summary>
    public string? KeptKey { get; set; }

    /// <summary>What the chapter was before the last Regenerate (or undo), all three parts, so undoing it brings back the notice too.</summary>
    public string? PreviousMarkdown { get; set; }
    public string? PreviousBase { get; set; }
    public string? PreviousBaseKey { get; set; }
    public DateTime? PreviousAt { get; set; }

    /// <summary>Counts every save, so a save made from an older copy (another tab) is refused instead of overwriting the newer text.</summary>
    public int Version { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The copies of the web addresses a draft's stylesheet names (docs/BOOK_EXPORT.md 41.14): one attachment each on this document, which lives
/// under the draft's id, so it goes with "start over" and with the campaign. What the attachments are is listed here, so reading them needs no
/// guess at a name.
/// </summary>
public class BookDraftWeb
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public List<BookDraftWebEntry> Files { get; set; } = new();

    public static string IdFor(string campaignId, string edition, string language) => BookDraftDocument.IdFor(campaignId, edition, language) + "/web";

    /// <summary>An attachment's name from its address: a hash, so an address never becomes a name.</summary>
    public static string NameFor(string url) =>
        "web-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant();
}

public sealed class BookDraftWebEntry
{
    public string Url { get; set; } = string.Empty;
    public Codex.Core.Book.BookCssWebKind Kind { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>A chapter as the generator made it now, for a new draft or to compare against a stored one.</summary>
public sealed record BookDraftGenerated(string Section, string Title, string Markdown, string Key);

/// <summary>A chapter of the GM's own (docs/BOOK_EXPORT.md 51) as the contents need it: its id, its title, and the chapter it follows in the
/// draft (empty: it is the first after the front pages).</summary>
public sealed record BookDraftOwn(string Section, string Title, string After);

/// <summary>A draft and its chapters, in book order.</summary>
public sealed record BookDraft(BookDraftDocument Document, IReadOnlyList<BookDraftChapter> Chapters);

public enum BookDraftSave
{
    Saved,

    /// <summary>The caller is not the campaign's GM now, the draft does not exist, or what was asked is malformed.</summary>
    NotAllowed,

    /// <summary>The chapter (or the settings) changed since the caller read it: another tab, or Regenerate.</summary>
    Conflict,

    TooLarge
}

/// <summary>What the follow pass did: chapters made to follow the campaign without asking (they were never edited), added and removed.</summary>
public sealed record BookDraftFollowed(int Updated, int Added, int Removed);

public partial interface IBookDraftRepository
{
    /// <summary>The draft; null when there is none, and for anyone who is not the campaign's GM now.</summary>
    Task<BookDraft?> GetAsync(CampaignAccess dm, string edition, string language);

    /// <summary>Makes the draft from <paramref name="generated"/> if there is none; returns the draft either way (null when not allowed).</summary>
    Task<BookDraft?> CreateAsync(CampaignAccess dm, string edition, string language, IReadOnlyList<BookDraftGenerated> generated, string pageSize = "Letter");

    /// <summary>The GM's own text for a chapter, saved only while the chapter is still at <paramref name="version"/>.</summary>
    Task<(BookDraftSave Result, int Version)> SaveChapterAsync(CampaignAccess dm, string edition, string language, string section, string markdown, int version);

    /// <summary>The chapter replaced by what the generator makes now; what it was is kept as the previous version.</summary>
    Task<(BookDraftSave Result, int Version)> RegenerateAsync(CampaignAccess dm, string edition, string language, BookDraftGenerated now, int version);

    /// <summary>Back to what the chapter was before the last Regenerate; what it is now becomes the previous version.</summary>
    Task<(BookDraftSave Result, int Version)> UndoAsync(CampaignAccess dm, string edition, string language, string section, int version);

    /// <summary>A merge the GM kept ("Bring up to date", docs/BOOK_EXPORT.md 41.18): <paramref name="markdown"/> is the chapter's text, and
    /// <paramref name="now"/> becomes its base, so the notice goes and the next merge starts from here. The chapter as it was becomes the
    /// previous version, all three parts, so Undo brings the notice back.</summary>
    Task<(BookDraftSave Result, int Version)> MergeAsync(CampaignAccess dm, string edition, string language, BookDraftGenerated now, string markdown, int version);

    /// <summary>"Keep mine": the notice stays away until the campaign changes from <paramref name="key"/>.</summary>
    Task<(BookDraftSave Result, int Version)> KeepAsync(CampaignAccess dm, string edition, string language, string section, string key, int version);

    /// <summary>
    /// Makes the draft follow the campaign where nobody edited it: a chapter whose text is still its base takes the generated text, a chapter
    /// the campaign has newly is added in its place, and a chapter the campaign no longer has is removed if it was never edited. An edited
    /// chapter is never touched here; it gets the notice instead.
    /// </summary>
    Task<BookDraftFollowed?> FollowAsync(CampaignAccess dm, string edition, string language, IReadOnlyList<BookDraftGenerated> generated);

    /// <summary>The GM's own chapters, in the draft's order, each with the chapter it follows: what the contents list of them. Empty when there
    /// are none, there is no draft, or the caller is not the campaign's GM now.</summary>
    Task<IReadOnlyList<BookDraftOwn>> OwnAsync(CampaignAccess dm, string edition, string language);

    /// <summary>A new chapter of the GM's own (docs/BOOK_EXPORT.md 51), holding its title and nothing else, placed after
    /// <paramref name="after"/> (a chapter of the draft; null or one it does not have: at the end). Refused without a title, and as too large
    /// when the draft has all the chapters it may hold. Returns the new chapter's id.</summary>
    Task<(BookDraftSave Result, string? Section)> AddChapterAsync(CampaignAccess dm, string edition, string language, string title, string? after);

    /// <summary>A new title for a chapter of the GM's own, as the list of chapters and the contents show it. The chapter's text is the GM's and
    /// is not touched: its heading is renamed by whoever edits the text.</summary>
    Task<(BookDraftSave Result, int Version)> RenameChapterAsync(CampaignAccess dm, string edition, string language, string section, string title, int version);

    /// <summary>Moves a chapter of the GM's own one place up (<paramref name="by"/> below zero) or down among the draft's chapters, never before
    /// the front pages. At the end already: saved, and nothing changes.</summary>
    Task<BookDraftSave> MoveChapterAsync(CampaignAccess dm, string edition, string language, string section, int by);

    /// <summary>A chapter of the GM's own, gone with its text. A chapter the campaign makes is never deleted this way.</summary>
    Task<BookDraftSave> DeleteChapterAsync(CampaignAccess dm, string edition, string language, string section);

    /// <summary>The draft's theme, paper and GM stylesheet. A theme the book does not have is refused; the chapters are never touched.</summary>
    /// <para><paramref name="edge"/>: torn page edges on or off; null leaves them as they are.</para>
    Task<(BookDraftSave Result, int Version)> SaveSettingsAsync(CampaignAccess dm, string edition, string language, string css, string pageSize, string theme, bool homebrewery,
        int version, bool? edge = null);

    /// <summary>The copies of the web addresses the draft's stylesheet names; none when there are none, or for anyone not the campaign's GM now.</summary>
    Task<Codex.Core.Book.BookCssWeb> WebAsync(CampaignAccess dm, string edition, string language);

    /// <summary>Stores <paramref name="added"/> and removes every copy whose address is not in <paramref name="keep"/> (or added). A copy that is not
    /// a web address, not a type a copy is stored as, or too large, and a set that would grow past the draft's bounds, are refused whole.</summary>
    Task<BookDraftSave> SaveWebAsync(CampaignAccess dm, string edition, string language, IReadOnlyList<Codex.Core.Book.BookCssWebFile> added, IReadOnlySet<string> keep);

    /// <summary>The draft and all its chapters, gone: "start over".</summary>
    Task<bool> DeleteAsync(CampaignAccess dm, string edition, string language);

    Task DeleteAllForCampaignAsync(string campaignId);
}

public partial class RavenBookDraftRepository(RavenDbService dbService, StorageQuota? quota = null) : IBookDraftRepository
{
    private const int MaxAttempts = 8;

    private static bool Valid(string edition, string language) => BookDraftDocument.IsEdition(edition) && BookDraftDocument.IsLanguage(language);

    private async Task<CampaignAccess?> GmAsync(CampaignAccess dm, string edition, string language) =>
        Valid(edition, language) && await CampaignAccessResolver.NowAsync(dbService.Store, dm) is { IsDm: true } now ? now : null;

    public async Task<BookDraft?> GetAsync(CampaignAccess dm, string edition, string language)
    {
        if (await GmAsync(dm, edition, language) is not { } now) return null;
        using var session = dbService.Store.OpenAsyncSession();
        return await LoadAsync(session, now.CampaignId, edition, language);
    }

    private static async Task<BookDraft?> LoadAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string campaignId, string edition, string language)
    {
        var draft = await session.LoadAsync<BookDraftDocument>(BookDraftDocument.IdFor(campaignId, edition, language));
        // The id already names the campaign; the fields are checked too, so a document written by hand under another's name is not served.
        if (draft == null || draft.CampaignId != campaignId || draft.Edition != edition || draft.Language != language) return null;
        var ids = draft.Chapters.Select(s => BookDraftDocument.ChapterIdFor(campaignId, edition, language, s)).ToList();
        var loaded = await session.LoadAsync<BookDraftChapter>(ids);
        var chapters = ids.Select(id => loaded.GetValueOrDefault(id)).Where(c => c != null && c.CampaignId == campaignId).Select(c => c!).ToList();
        return new BookDraft(draft, chapters);
    }

    public async Task<BookDraft?> CreateAsync(CampaignAccess dm, string edition, string language, IReadOnlyList<BookDraftGenerated> generated, string pageSize = "Letter")
    {
        if (await GmAsync(dm, edition, language) is not { } now) return null;
        if (generated.Count > BookDraftDocument.MaxChapters || generated.Any(g => !BookDraftDocument.IsSection(g.Section) || BookDraftDocument.IsOwn(g.Section)) || generated.DistinctBy(g => g.Section).Count() != generated.Count)
        {
            return null;
        }

        using (var read = dbService.Store.OpenAsyncSession())
        {
            if (await LoadAsync(read, now.CampaignId, edition, language) is { } existing) return existing;
        }

        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var id = BookDraftDocument.IdFor(now.CampaignId, edition, language);
        var draft = new BookDraftDocument
        {
            Id = id, CampaignId = now.CampaignId, Edition = edition, Language = language, PageSize = Paper(pageSize), CreatedBy = now.UserId,
            Chapters = generated.Select(g => g.Section).ToList()
        };
        // A changeVector of "" means "this id must not exist yet": two tabs making the same draft at once make it once.
        await session.StoreAsync(draft, string.Empty, id);
        foreach (var g in generated)
        {
            var chapter = NewChapter(now, edition, language, g);
            await session.StoreAsync(chapter, string.Empty, chapter.Id);
        }

        try
        {
            await session.SaveChangesAsync();
        }
        catch (ConcurrencyException)
        {
            // Made by the other tab in the meantime: that one is the draft.
        }

        using var again = dbService.Store.OpenAsyncSession();
        return await LoadAsync(again, now.CampaignId, edition, language);
    }

    private static BookDraftChapter NewChapter(CampaignAccess now, string edition, string language, BookDraftGenerated g) => new()
    {
        Id = BookDraftDocument.ChapterIdFor(now.CampaignId, edition, language, g.Section), CampaignId = now.CampaignId, Section = g.Section,
        Title = Clip(g.Title, 200), Markdown = Fit(g.Markdown), Base = Fit(g.Markdown), BaseKey = g.Key, UpdatedBy = now.UserId
    };

    // Generated text longer than a chapter may hold is cut, never refused: the draft must be makeable from any campaign.
    private static string Fit(string text) => text.Length <= BookDraftDocument.MaxChapterChars ? text : text[..BookDraftDocument.MaxChapterChars];

    public Task<(BookDraftSave Result, int Version)> SaveChapterAsync(CampaignAccess dm, string edition, string language, string section, string markdown, int version) =>
        markdown.Length > BookDraftDocument.MaxChapterChars
            ? Task.FromResult((BookDraftSave.TooLarge, version))
            : ChangeChapterAsync(dm, edition, language, section, version, (chapter, _) =>
            {
                chapter.Markdown = markdown;
                return true;
            });

    public Task<(BookDraftSave Result, int Version)> RegenerateAsync(CampaignAccess dm, string edition, string language, BookDraftGenerated now, int version) =>
        ChangeChapterAsync(dm, edition, language, now.Section, version, (chapter, at) =>
        {
            Remember(chapter, at);
            chapter.Markdown = Fit(now.Markdown);
            chapter.Base = Fit(now.Markdown);
            chapter.BaseKey = now.Key;
            chapter.Title = Clip(now.Title, 200);
            chapter.KeptKey = null;
            return true;
        });

    public Task<(BookDraftSave Result, int Version)> MergeAsync(CampaignAccess dm, string edition, string language, BookDraftGenerated now, string markdown, int version) =>
        markdown.Length > BookDraftDocument.MaxChapterChars
            ? Task.FromResult((BookDraftSave.TooLarge, version))
            : ChangeChapterAsync(dm, edition, language, now.Section, version, (chapter, at) =>
            {
                Remember(chapter, at);
                chapter.Markdown = markdown;
                chapter.Base = Fit(now.Markdown);
                chapter.BaseKey = now.Key;
                chapter.Title = Clip(now.Title, 200);
                chapter.KeptKey = null;
                return true;
            });

    public Task<(BookDraftSave Result, int Version)> UndoAsync(CampaignAccess dm, string edition, string language, string section, int version) =>
        ChangeChapterAsync(dm, edition, language, section, version, (chapter, at) =>
        {
            if (chapter.PreviousMarkdown == null || chapter.PreviousBase == null || chapter.PreviousBaseKey == null) return false;
            var (markdown, @base, key) = (chapter.PreviousMarkdown, chapter.PreviousBase, chapter.PreviousBaseKey);
            Remember(chapter, at);
            chapter.Markdown = markdown;
            chapter.Base = @base;
            chapter.BaseKey = key;
            chapter.KeptKey = null;
            return true;
        });

    public Task<(BookDraftSave Result, int Version)> KeepAsync(CampaignAccess dm, string edition, string language, string section, string key, int version) =>
        ChangeChapterAsync(dm, edition, language, section, version, (chapter, _) =>
        {
            if (key.Length is 0 or > 100) return false;
            chapter.KeptKey = key;
            return true;
        });

    private static void Remember(BookDraftChapter chapter, DateTime at)
    {
        chapter.PreviousMarkdown = chapter.Markdown;
        chapter.PreviousBase = chapter.Base;
        chapter.PreviousBaseKey = chapter.BaseKey;
        chapter.PreviousAt = at;
    }

    private async Task<(BookDraftSave Result, int Version)> ChangeChapterAsync(CampaignAccess dm, string edition, string language, string section, int version,
        Func<BookDraftChapter, DateTime, bool> change)
    {
        if (!BookDraftDocument.IsSection(section) || await GmAsync(dm, edition, language) is not { } now) return (BookDraftSave.NotAllowed, version);
        var id = BookDraftDocument.ChapterIdFor(now.CampaignId, edition, language, section);
        using var session = dbService.Store.OpenAsyncSession();
        // The version is compared here, and optimistic concurrency covers the moment between this load and the save.
        session.Advanced.UseOptimisticConcurrency = true;
        var chapter = await session.LoadAsync<BookDraftChapter>(id);
        if (chapter == null || chapter.CampaignId != now.CampaignId) return (BookDraftSave.NotAllowed, version);
        if (chapter.Version != version) return (BookDraftSave.Conflict, chapter.Version);
        var at = DateTime.UtcNow;
        if (!change(chapter, at)) return (BookDraftSave.NotAllowed, version);
        chapter.Version++;
        chapter.UpdatedAt = at;
        chapter.UpdatedBy = now.UserId;
        try
        {
            await session.SaveChangesAsync();
            return (BookDraftSave.Saved, chapter.Version);
        }
        catch (ConcurrencyException)
        {
            return (BookDraftSave.Conflict, version);
        }
    }

    public async Task<BookDraftFollowed?> FollowAsync(CampaignAccess dm, string edition, string language, IReadOnlyList<BookDraftGenerated> generated)
    {
        if (await GmAsync(dm, edition, language) is not { } now) return null;
        if (generated.Any(g => !BookDraftDocument.IsSection(g.Section) || BookDraftDocument.IsOwn(g.Section)) || generated.DistinctBy(g => g.Section).Count() != generated.Count) return null;
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            if (await LoadAsync(session, now.CampaignId, edition, language) is not { } draft) return null;
            var stored = draft.Chapters.ToDictionary(c => c.Section, StringComparer.Ordinal);
            var wanted = generated.ToDictionary(g => g.Section, StringComparer.Ordinal);
            int updated = 0, added = 0, removed = 0;
            var at = DateTime.UtcNow;

            foreach (var chapter in draft.Chapters)
            {
                if (wanted.TryGetValue(chapter.Section, out var g))
                {
                    // Never edited, and the campaign moved on: it follows, with no notice and nothing to undo.
                    if (chapter.BaseKey != g.Key && Untouched(chapter))
                    {
                        chapter.Markdown = Fit(g.Markdown);
                        chapter.Base = Fit(g.Markdown);
                        chapter.BaseKey = g.Key;
                        chapter.Title = Clip(g.Title, 200);
                        chapter.Version++;
                        chapter.UpdatedAt = at;
                        chapter.UpdatedBy = now.UserId;
                        updated++;
                    }
                }
                else if (!BookDraftDocument.IsOwn(chapter.Section) && Untouched(chapter))
                {
                    // The campaign no longer has this chapter (every entry hidden or deleted) and the GM never wrote in it.
                    session.Delete(chapter);
                    draft.Document.Chapters.Remove(chapter.Section);
                    removed++;
                }
            }

            // A chapter the campaign has newly goes where the generator puts it: after the chapter that comes before it there.
            for (var i = 0; i < generated.Count; i++)
            {
                var g = generated[i];
                if (stored.ContainsKey(g.Section) || draft.Document.Chapters.Count >= BookDraftDocument.MaxChapters) continue;
                var chapter = NewChapter(now, edition, language, g);
                await session.StoreAsync(chapter, string.Empty, chapter.Id);
                var before = generated.Take(i).Select(p => draft.Document.Chapters.IndexOf(p.Section)).Where(x => x >= 0).DefaultIfEmpty(-1).Max();
                draft.Document.Chapters.Insert(before + 1, g.Section);
                added++;
            }

            // The campaign's chapters stand in the order the generator gives them now (a newer generator may order them differently). A chapter
            // it does not make (the GM's own, or an edited one the campaign no longer has) stays behind the chapter it stood behind.
            var ordered = Ordered(draft.Document.Chapters, generated);
            var moved = !ordered.SequenceEqual(draft.Document.Chapters, StringComparer.Ordinal);
            if (moved) draft.Document.Chapters = ordered;
            if (updated + added + removed == 0 && !moved) return new BookDraftFollowed(0, 0, 0);
            if (added + removed > 0 || moved)
            {
                draft.Document.Version++;
                draft.Document.UpdatedAt = at;
            }

            try
            {
                await session.SaveChangesAsync();
                return new BookDraftFollowed(updated, added, removed);
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // A tab saved a chapter in between: read again, and that chapter may now count as edited.
            }
        }
    }

    /// <summary>The draft's chapters with the generated ones in the generator's order; each of the others follows the generated chapter it
    /// followed before (or stays in front of them all). Public for its tests.</summary>
    public static List<string> Ordered(IReadOnlyList<string> chapters, IReadOnlyList<BookDraftGenerated> generated)
    {
        var made = generated.Select(g => g.Section).Where(chapters.Contains).ToList();
        var fixedSet = made.ToHashSet(StringComparer.Ordinal);
        var behind = new Dictionary<string, List<string>>(StringComparer.Ordinal) { [string.Empty] = [] };
        var last = string.Empty;
        foreach (var section in chapters)
        {
            if (fixedSet.Contains(section)) last = section;
            else (behind.TryGetValue(last, out var list) ? list : behind[last] = []).Add(section);
        }

        var result = new List<string>(chapters.Count);
        result.AddRange(behind[string.Empty]);
        foreach (var section in made)
        {
            result.Add(section);
            if (behind.TryGetValue(section, out var list)) result.AddRange(list);
        }

        return result;
    }

    public async Task<IReadOnlyList<BookDraftOwn>> OwnAsync(CampaignAccess dm, string edition, string language)
    {
        if (await GmAsync(dm, edition, language) is not { } now) return [];
        using var session = dbService.Store.OpenAsyncSession();
        var draft = await session.LoadAsync<BookDraftDocument>(BookDraftDocument.IdFor(now.CampaignId, edition, language));
        if (draft == null || draft.CampaignId != now.CampaignId || !draft.Chapters.Any(BookDraftDocument.IsOwn)) return [];
        var ids = draft.Chapters.Where(BookDraftDocument.IsOwn).Select(s => BookDraftDocument.ChapterIdFor(now.CampaignId, edition, language, s)).ToList();
        var loaded = await session.LoadAsync<BookDraftChapter>(ids);
        var own = new List<BookDraftOwn>();
        for (var i = 0; i < draft.Chapters.Count; i++)
        {
            var section = draft.Chapters[i];
            if (!BookDraftDocument.IsOwn(section)) continue;
            if (loaded.GetValueOrDefault(BookDraftDocument.ChapterIdFor(now.CampaignId, edition, language, section)) is not { } chapter || chapter.CampaignId != now.CampaignId) continue;
            own.Add(new BookDraftOwn(section, chapter.Title, i == 0 ? string.Empty : draft.Chapters[i - 1]));
        }

        return own;
    }

    /// <summary>What a new chapter of the GM's own holds: the chapter block every chapter opens with, around its title.</summary>
    public static string OwnText(string section, string title) => "{{chapter,#ch-" + section + "\n# " + BookCompiler.Escape(title) + "\n}}\n";

    public async Task<(BookDraftSave Result, string? Section)> AddChapterAsync(CampaignAccess dm, string edition, string language, string title, string? after)
    {
        title = Clip(title, BookDraftDocument.MaxTitleChars);
        if (title.Length == 0 || await GmAsync(dm, edition, language) is not { } now) return (BookDraftSave.NotAllowed, null);
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var draft = await session.LoadAsync<BookDraftDocument>(BookDraftDocument.IdFor(now.CampaignId, edition, language));
        if (draft == null || draft.CampaignId != now.CampaignId) return (BookDraftSave.NotAllowed, null);
        if (draft.Chapters.Count >= BookDraftDocument.MaxChapters) return (BookDraftSave.TooLarge, null);
        var section = BookDraftDocument.NewOwn();
        var chapter = new BookDraftChapter
        {
            Id = BookDraftDocument.ChapterIdFor(now.CampaignId, edition, language, section), CampaignId = now.CampaignId, Section = section,
            Title = title, Markdown = OwnText(section, title), UpdatedBy = now.UserId
        };
        await session.StoreAsync(chapter, string.Empty, chapter.Id);
        var at = after == null ? -1 : draft.Chapters.IndexOf(after);
        draft.Chapters.Insert(at < 0 ? draft.Chapters.Count : at + 1, section);
        draft.Version++;
        draft.UpdatedAt = DateTime.UtcNow;
        try
        {
            await session.SaveChangesAsync();
            return (BookDraftSave.Saved, section);
        }
        catch (ConcurrencyException)
        {
            return (BookDraftSave.Conflict, null);
        }
    }

    public Task<(BookDraftSave Result, int Version)> RenameChapterAsync(CampaignAccess dm, string edition, string language, string section, string title, int version) =>
        !BookDraftDocument.IsOwn(section) || Clip(title, BookDraftDocument.MaxTitleChars).Length == 0
            ? Task.FromResult((BookDraftSave.NotAllowed, version))
            : ChangeChapterAsync(dm, edition, language, section, version, (chapter, _) =>
            {
                chapter.Title = Clip(title, BookDraftDocument.MaxTitleChars);
                return true;
            });

    public Task<BookDraftSave> MoveChapterAsync(CampaignAccess dm, string edition, string language, string section, int by) =>
        ChangeOwnAsync(dm, edition, language, section, (draft, _) =>
        {
            var at = draft.Chapters.IndexOf(section);
            var to = at + Math.Sign(by);
            // The front pages (the cover and the contents) are the book's first chapter whatever else moves.
            if (to < 0 || to >= draft.Chapters.Count || draft.Chapters[to] == "front") return false;
            (draft.Chapters[at], draft.Chapters[to]) = (draft.Chapters[to], draft.Chapters[at]);
            return true;
        });

    public Task<BookDraftSave> DeleteChapterAsync(CampaignAccess dm, string edition, string language, string section) =>
        ChangeOwnAsync(dm, edition, language, section, (draft, session) =>
        {
            session.Delete(BookDraftDocument.ChapterIdFor(draft.CampaignId, edition, language, section));
            draft.Chapters.Remove(section);
            return true;
        });

    // A change to where a chapter of the GM's own stands, or whether it is there: the draft's list of chapters, under the draft's own version.
    private async Task<BookDraftSave> ChangeOwnAsync(CampaignAccess dm, string edition, string language, string section,
        Func<BookDraftDocument, Raven.Client.Documents.Session.IAsyncDocumentSession, bool> change)
    {
        if (!BookDraftDocument.IsOwn(section) || await GmAsync(dm, edition, language) is not { } now) return BookDraftSave.NotAllowed;
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var draft = await session.LoadAsync<BookDraftDocument>(BookDraftDocument.IdFor(now.CampaignId, edition, language));
        if (draft == null || draft.CampaignId != now.CampaignId || !draft.Chapters.Contains(section)) return BookDraftSave.NotAllowed;
        if (!change(draft, session)) return BookDraftSave.Saved;
        draft.Version++;
        draft.UpdatedAt = DateTime.UtcNow;
        try
        {
            await session.SaveChangesAsync();
            return BookDraftSave.Saved;
        }
        catch (ConcurrencyException)
        {
            return BookDraftSave.Conflict;
        }
    }

    /// <summary>The GM never changed the text from what was generated. Page and column breaks are layout, not edits (docs 41.3): a fitted chapter is
    /// still untouched.</summary>
    public static bool Untouched(BookDraftChapter chapter) => BookDraftText.Content(chapter.Markdown) == BookDraftText.Content(chapter.Base);

    public async Task<(BookDraftSave Result, int Version)> SaveSettingsAsync(CampaignAccess dm, string edition, string language, string css, string pageSize, string theme,
        bool homebrewery, int version, bool? edge = null)
    {
        if (css.Length > BookDraftDocument.MaxCssChars) return (BookDraftSave.TooLarge, version);
        if (await GmAsync(dm, edition, language) is not { } now) return (BookDraftSave.NotAllowed, version);
        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        // A built-in theme, or one of this campaign's own that the wizard finished (docs/BOOK_EXPORT.md 41.22).
        string chosen;
        if (Codex.Core.Book.BookDraftThemes.Find(theme) is { } builtIn)
        {
            chosen = builtIn.Id;
        }
        else if (theme.StartsWith(Codex.Core.Book.BookCustomTheme.Prefix, StringComparison.Ordinal) && theme[Codex.Core.Book.BookCustomTheme.Prefix.Length..] is var key
                 && BookThemeDocument.IsKey(key) && await session.LoadAsync<BookThemeDocument>(BookThemeDocument.IdFor(now.CampaignId, key)) is { Ready: true } own
                 && own.CampaignId == now.CampaignId)
        {
            chosen = theme;
        }
        else
        {
            return (BookDraftSave.NotAllowed, version);
        }

        var draft = await session.LoadAsync<BookDraftDocument>(BookDraftDocument.IdFor(now.CampaignId, edition, language));
        if (draft == null || draft.CampaignId != now.CampaignId) return (BookDraftSave.NotAllowed, version);
        if (draft.Version != version) return (BookDraftSave.Conflict, draft.Version);
        draft.Css = css;
        draft.PageSize = Paper(pageSize);
        draft.Theme = chosen;
        draft.Homebrewery = homebrewery;
        if (edge is { } torn) draft.Edge = torn;
        draft.Version++;
        draft.UpdatedAt = DateTime.UtcNow;
        try
        {
            await session.SaveChangesAsync();
            return (BookDraftSave.Saved, draft.Version);
        }
        catch (ConcurrencyException)
        {
            return (BookDraftSave.Conflict, version);
        }
    }

    public async Task<bool> DeleteAsync(CampaignAccess dm, string edition, string language)
    {
        if (await GmAsync(dm, edition, language) is not { } now) return false;
        using var session = dbService.Store.OpenAsyncSession();
        var prefix = BookDraftDocument.IdFor(now.CampaignId, edition, language);
        session.Delete(prefix);
        // Its copies of web addresses (their attachments go with the document).
        session.Delete(prefix + "/web");
        // Every chapter that ever belonged to it, listed or not.
        foreach (var chapter in await session.Advanced.LoadStartingWithAsync<BookDraftChapter>(prefix + "/chapters/", pageSize: 1024))
        {
            session.Delete(chapter);
        }

        await session.SaveChangesAsync();
        return true;
    }

    public async Task<Codex.Core.Book.BookCssWeb> WebAsync(CampaignAccess dm, string edition, string language)
    {
        if (await GmAsync(dm, edition, language) is not { } now) return new Codex.Core.Book.BookCssWeb();
        using var session = dbService.Store.OpenAsyncSession();
        var id = BookDraftWeb.IdFor(now.CampaignId, edition, language);
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

    public async Task<BookDraftSave> SaveWebAsync(CampaignAccess dm, string edition, string language, IReadOnlyList<Codex.Core.Book.BookCssWebFile> added,
        IReadOnlySet<string> keep)
    {
        if (await GmAsync(dm, edition, language) is not { } now) return BookDraftSave.NotAllowed;
        foreach (var f in added)
        {
            if (Codex.Core.Book.BookCssWeb.Address(f.Url, null) != f.Url || !Codex.Core.Book.BookCssWeb.ContentTypes.Contains(f.ContentType)
                || f.Bytes.Length == 0 || f.Bytes.Length > Codex.Core.Book.BookCssWeb.MaxBytes(f.Kind))
            {
                return BookDraftSave.NotAllowed;
            }
        }

        using var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        if (await session.LoadAsync<BookDraftDocument>(BookDraftDocument.IdFor(now.CampaignId, edition, language)) is not { } draft || draft.CampaignId != now.CampaignId)
        {
            return BookDraftSave.NotAllowed;
        }

        return await StoreWebAsync(session, now.CampaignId, BookDraftWeb.IdFor(now.CampaignId, edition, language), added, keep);
    }

    private static bool Copyable(IReadOnlyList<Codex.Core.Book.BookCssWebFile> added) => added.All(f =>
        Codex.Core.Book.BookCssWeb.Address(f.Url, null) == f.Url && Codex.Core.Book.BookCssWeb.ContentTypes.Contains(f.ContentType)
        && f.Bytes.Length > 0 && f.Bytes.Length <= Codex.Core.Book.BookCssWeb.MaxBytes(f.Kind));

    /// <summary>The copies kept on the web document <paramref name="id"/> (a draft's or a campaign theme's), in the open session, which also
    /// holds what they belong to under optimistic concurrency.</summary>
    private async Task<BookDraftSave> StoreWebAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string campaignId, string id,
        IReadOnlyList<Codex.Core.Book.BookCssWebFile> added, IReadOnlySet<string> keep)
    {
        var now = new { CampaignId = campaignId };
        var web = await session.LoadAsync<BookDraftWeb>(id);
        if (web != null && web.CampaignId != now.CampaignId) return BookDraftSave.NotAllowed;
        if (web == null)
        {
            web = new BookDraftWeb { Id = id, CampaignId = now.CampaignId };
            await session.StoreAsync(web, id);
        }

        // A copy already kept stays as it is (an attachment is not deleted and stored again in one session).
        var adding = added.Select(f => f.Url).ToHashSet(StringComparer.Ordinal);
        long freed = 0;
        foreach (var gone in web.Files.Where(e => !keep.Contains(e.Url) && !adding.Contains(e.Url)).ToList())
        {
            freed += gone.Length;
            session.Advanced.Attachments.Delete(id, gone.Name);
            web.Files.Remove(gone);
        }

        var have = web.Files.Select(e => e.Url).ToHashSet(StringComparer.Ordinal);
        var at = DateTime.UtcNow;
        foreach (var f in added.DistinctBy(f => f.Url).Where(f => !have.Contains(f.Url)))
        {
            var entry = new BookDraftWebEntry { Url = f.Url, Kind = f.Kind, ContentType = f.ContentType, Name = BookDraftWeb.NameFor(f.Url), Length = f.Bytes.Length, FetchedAt = at };
            web.Files.Add(entry);
            session.Advanced.Attachments.Store(id, entry.Name, new MemoryStream(f.Bytes), f.ContentType);
        }

        if (web.Files.Count > Codex.Core.Book.BookCssWeb.MaxFiles || web.Files.Sum(e => e.Length) > Codex.Core.Book.BookCssWeb.MaxTotalBytes) return BookDraftSave.TooLarge;
        if (quota != null)
        {
            // The account's storage limit (docs/SERVER_ACCESS_AND_VAULT.md 15) refuses these copies as too large for what is left.
            try
            {
                await quota.EnsureRoomAsync(now.CampaignId, added.Where(f => !have.Contains(f.Url)).DistinctBy(f => f.Url).Sum(f => (long)f.Bytes.Length), freed);
            }
            catch (Codex.Localization.LocalizedException ex) when (ex.Key == "Service.Storage.Full")
            {
                return BookDraftSave.TooLarge;
            }
        }

        try
        {
            await session.SaveChangesAsync();
            return BookDraftSave.Saved;
        }
        catch (ConcurrencyException)
        {
            return BookDraftSave.Conflict;
        }
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        // By the name's prefix: no index to wait for. Drafts and chapters are both under it; loaded as one type only to be deleted by their ids.
        foreach (var doc in await session.Advanced.LoadStartingWithAsync<BookDraftChapter>(BookDraftDocument.PrefixFor(campaignId), pageSize: 1024))
        {
            session.Delete(doc);
        }

        await session.SaveChangesAsync();
    }

    private static string Paper(string? size) => string.Equals(size, "A4", StringComparison.OrdinalIgnoreCase) ? "A4" : "Letter";

    private static string Clip(string? text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..max];
    }
}

/// <summary>Reading a draft's Markdown for what it says, apart from how it is laid out.</summary>
public static class BookDraftText
{
    /// <summary>The text without its plain page and column breaks (<c>\page</c>, <c>\column</c>, alone on a line), and without trailing spaces on
    /// blank lines: the part an edit is an edit of. A break with Homebrewery's <c>{…}</c> options (<c>\page {reliquary}</c>) is kept: it styles the
    /// page, so it is the GM's edit, and a chapter that has one must not be replaced silently when the campaign changes.</summary>
    public static string Content(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (IsBreak(t)) continue;
            kept.Add(t.Length == 0 ? string.Empty : line.TrimEnd('\r'));
        }

        // Breaks usually stand between blank lines; dropping one leaves two blank lines where there was one.
        var collapsed = new List<string>(kept.Count);
        foreach (var line in kept)
        {
            if (line.Length == 0 && collapsed.Count > 0 && collapsed[^1].Length == 0) continue;
            collapsed.Add(line);
        }

        return string.Join('\n', collapsed).Trim('\n');
    }

    /// <summary>A plain break: <c>\page</c> or <c>\column</c>, or the <c>\page {fitted}</c> that Fit pages writes (docs/BOOK_EXPORT.md 41.15).
    /// One with other options (<c>\page {reliquary,fitted}</c>, written after a styled page) styles its page, so it is not.</summary>
    public static bool IsBreak(string trimmed) =>
        trimmed is @"\page" or @"\pagebreak" or @"\column" or @"\columnbreak" || PlainFitted.IsMatch(trimmed);

    /// <summary>A break Fit pages wrote, with or without the classes of the page it continues (the renderer's <c>isFitted</c>).</summary>
    public static bool IsFitted(string line) =>
        Fitted.Match(line.Trim()) is { Success: true } m && m.Groups[1].Value.Split(',').Any(t => t.Trim() == FitClass);

    public const string FitClass = "fitted";

    /// <summary>A break Fit pages wrote, as a plain Homebrewery break: <c>\page {fitted}</c> is <c>\page</c>, and <c>\page {reliquary,fitted}</c>
    /// keeps the class it carried (<c>\page {reliquary}</c>). Any other line is as it was.</summary>
    public static string Unfitted(string line)
    {
        if (!IsFitted(line)) return line;
        var classes = Fitted.Match(line.Trim()).Groups[1].Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0 && t != FitClass).ToList();
        return classes.Count == 0 ? @"\page" : @"\page {" + string.Join(",", classes) + "}";
    }

    private static readonly System.Text.RegularExpressions.Regex PlainFitted = new(@"^\\page(?:break)? *\{ *fitted *\}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Fitted = new(@"^\\page(?:break)? *\{([^\n{}]*)\}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
