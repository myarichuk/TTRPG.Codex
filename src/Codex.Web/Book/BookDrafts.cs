using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>A chapter as the editor shows it.</summary>
/// <param name="Edited">The GM's text differs from what it was generated as (page and column breaks aside).</param>
/// <param name="Stale">Edited, and the campaign changed since: the notice, with Regenerate. Not shown again after "keep mine" until the campaign changes again.</param>
/// <param name="Gone">Edited, and the campaign no longer has this chapter at all.</param>
/// <param name="Changed">For a stale chapter: the entries whose generated text changed (their headings as the generator writes them now).</param>
public sealed record BookDraftChapterView(string Section, string Title, string Markdown, int Version, bool Edited, bool Stale, bool Gone, bool CanUndo,
    IReadOnlyList<string> Changed);

public sealed record BookDraftView(string Edition, string Language, string Theme, bool Homebrewery, string PageSize, string Css, int Version, IReadOnlyList<BookDraftChapterView> Chapters,
    BookDraftFollowed? Followed = null);

/// <summary>What a draft is printed or previewed from: the page shell, the brew, and the edition's pictures by slot.</summary>
public sealed record BookDraftPage(string Title, string Shell, string Markdown, IReadOnlyDictionary<string, string> Art);

/// <summary>
/// The editable book's service (docs/BOOK_EXPORT.md 41): makes a draft from the edition's masked source, keeps the chapters nobody edited in step
/// with the campaign, says which edited chapters the campaign moved on from, and replaces one on Regenerate. Every call reads the campaign as the
/// asking GM may read it now; a key, a section or a language posted from the page is checked by the store before it reaches an id.
/// </summary>
public sealed class BookDrafts(BookSourceReader reader, IBookDraftRepository store, IBookArtRepository art, BookArtReader artReader, ICampaignAccessResolver resolver)
{
    public static BookAudience? Audience(string edition) => edition switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };

    private async Task<(CampaignAccess Dm, BookSource Source, IReadOnlyList<BookDraftGenerated> Now)?> GenerateAsync(CampaignAccess asked, string edition, string language)
    {
        if (Audience(edition) is not { } audience || !BookDraftDocument.IsLanguage(language)) return null;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        if (await reader.ReadAsync(dm, audience) is not { } source) return null;
        return (dm, source, BookDraftWriter.Generate(source, language, await art.GetAsync(dm)));
    }

    /// <summary>Whether the GM has a draft of this edition and language (without making one).</summary>
    public async Task<bool> ExistsAsync(CampaignAccess asked, string edition, string language) =>
        Audience(edition) != null && await store.GetAsync(asked, edition, language) != null;

    /// <summary>The draft, made first if there is none, with the chapters nobody edited brought in step with the campaign.</summary>
    public async Task<BookDraftView?> OpenAsync(CampaignAccess asked, string edition, string language, string pageSize = "Letter")
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return null;
        var followed = await store.FollowAsync(made.Dm, edition, language, made.Now);
        var draft = await store.GetAsync(made.Dm, edition, language) ?? await store.CreateAsync(made.Dm, edition, language, made.Now, pageSize);
        return draft == null ? null : View(draft, made.Now, followed);
    }

    /// <summary>The draft as it is stored, compared with the campaign now; null when there is none.</summary>
    public async Task<BookDraftView?> GetAsync(CampaignAccess asked, string edition, string language)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return null;
        return await store.GetAsync(made.Dm, edition, language) is { } draft ? View(draft, made.Now, null) : null;
    }

    private static BookDraftView View(BookDraft draft, IReadOnlyList<BookDraftGenerated> now, BookDraftFollowed? followed)
    {
        var generated = now.ToDictionary(g => g.Section, StringComparer.Ordinal);
        var chapters = draft.Chapters.Select(c =>
        {
            var edited = !RavenBookDraftRepository.Untouched(c);
            generated.TryGetValue(c.Section, out var g);
            var moved = g != null && g.Key != c.BaseKey && c.KeptKey != g.Key;
            return new BookDraftChapterView(c.Section, c.Title, c.Markdown, c.Version, edited, edited && moved, edited && g == null,
                c.PreviousMarkdown != null, edited && moved ? ChangedEntries(c.Base, g!.Markdown) : []);
        }).ToList();
        var d = draft.Document;
        return new BookDraftView(d.Edition, d.Language, d.Theme, d.Homebrewery, d.PageSize, d.Css, d.Version, chapters, followed);
    }

    /// <summary>The entries (by the id the generator gives each) whose generated text differs between the base and now, named by their heading now.</summary>
    public static IReadOnlyList<string> ChangedEntries(string before, string now)
    {
        var was = Entries(before);
        var changed = new List<string>();
        foreach (var (id, (heading, text)) in Entries(now))
        {
            if (!was.TryGetValue(id, out var old) || old.Text != text) changed.Add(heading);
        }

        // Entries gone from the campaign are named by their old heading.
        changed.AddRange(was.Where(kv => !Entries(now).ContainsKey(kv.Key)).Select(kv => kv.Value.Heading));
        return changed.Distinct().Take(20).ToList();
    }

    private static Dictionary<string, (string Heading, string Text)> Entries(string markdown)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        string? id = null;
        var heading = string.Empty;
        var text = new System.Text.StringBuilder();
        void Close()
        {
            if (id != null) result[id] = (heading, text.ToString());
        }

        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("{{entry,#e-", StringComparison.Ordinal) && line.EndsWith("}}", StringComparison.Ordinal))
            {
                Close();
                id = line[11..^2];
                heading = string.Empty;
                text.Clear();
                continue;
            }

            // An entry runs to the next entry or chapter.
            if (line.StartsWith("{{chapter,", StringComparison.Ordinal))
            {
                Close();
                id = null;
            }

            if (id == null) continue;
            if (heading.Length == 0 && line.StartsWith('#')) heading = Unescape(line.TrimStart('#').Trim());
            text.Append(line).Append('\n');
        }

        Close();
        return result;
    }

    /// <summary>A heading the generator escaped, as words (for a notice, not for a page).</summary>
    private static string Unescape(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i++;
            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    public async Task<(BookDraftSave Result, int Version)> SaveChapterAsync(CampaignAccess asked, string edition, string language, string section, string markdown, int version) =>
        await store.SaveChapterAsync(asked, edition, language, section, markdown, version);

    /// <summary>Regenerate: the chapter as the campaign makes it now. A chapter the campaign no longer has is not regenerated (there is nothing to
    /// generate); the GM removes its text or keeps it.</summary>
    public async Task<(BookDraftSave Result, int Version)> RegenerateAsync(CampaignAccess asked, string edition, string language, string section, int version)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return (BookDraftSave.NotAllowed, version);
        var now = made.Now.FirstOrDefault(g => g.Section == section);
        return now == null ? (BookDraftSave.NotAllowed, version) : await store.RegenerateAsync(made.Dm, edition, language, now, version);
    }

    public Task<(BookDraftSave Result, int Version)> UndoAsync(CampaignAccess asked, string edition, string language, string section, int version) =>
        store.UndoAsync(asked, edition, language, section, version);

    /// <summary>"Keep mine": the key is the one the campaign makes now, read here, never taken from the page.</summary>
    public async Task<(BookDraftSave Result, int Version)> KeepAsync(CampaignAccess asked, string edition, string language, string section, int version)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return (BookDraftSave.NotAllowed, version);
        var now = made.Now.FirstOrDefault(g => g.Section == section);
        return now == null ? (BookDraftSave.NotAllowed, version) : await store.KeepAsync(made.Dm, edition, language, section, now.Key, version);
    }

    public Task<(BookDraftSave Result, int Version)> SaveSettingsAsync(CampaignAccess asked, string edition, string language, string css, string pageSize, string theme,
        bool homebrewery, int version) =>
        store.SaveSettingsAsync(asked, edition, language, css, pageSize, theme, homebrewery, version);

    public Task<bool> StartOverAsync(CampaignAccess asked, string edition, string language) => store.DeleteAsync(asked, edition, language);

    /// <summary>
    /// What the draft is drawn from: its shell (theme, faces, the GM's CSS, cleaned), its chapters as one brew, and the edition's pictures by slot,
    /// read for this edition only (<see cref="BookSlots.Choose"/>: the party draft gets only the party's). Null when not allowed; a shell of null
    /// with a reason when the theme cannot print the language.
    /// </summary>
    public async Task<(BookDraftPage? Page, string? Refused)> PageAsync(CampaignAccess asked, string edition, string language, BookArtQuality quality,
        Func<string, byte[]?> font, CancellationToken cancel, bool withArt = true)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return (null, null);
        if (await store.GetAsync(made.Dm, edition, language) is not { } draft) return (null, null);
        var theme = BookDraftThemes.Find(draft.Document.Theme) ?? BookDraftThemes.Plain;
        var shell = BookDraftHtml.Shell(made.Source.Campaign.Name, language, theme, draft.Document.PageSize, draft.Document.Css, draft.Document.Homebrewery, font);
        if (shell.Html == null) return (null, shell.Refused);
        var markdown = string.Join("\n\n\\page\n\n", draft.Chapters.Select(c => c.Markdown.TrimEnd()));
        var pictures = withArt ? await ArtAsync(made.Dm, made.Source, quality, cancel) : new Dictionary<string, string>();
        return (new BookDraftPage(made.Source.Campaign.Name, shell.Html, markdown, pictures), null);
    }

    /// <summary>The edition's pictures by slot key, as data images: the cover, each chapter's header, and each entry's (the GM's book picture for
    /// the slot, or the entry's own picture as this source flags it).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ArtAsync(CampaignAccess asked, string edition, BookArtQuality quality, CancellationToken cancel)
    {
        if (Audience(edition) is not { } audience) return new Dictionary<string, string>();
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return new Dictionary<string, string>();
        if (await reader.ReadAsync(dm, audience) is not { } source) return new Dictionary<string, string>();
        return await ArtAsync(dm, source, quality, cancel);
    }

    private async Task<IReadOnlyDictionary<string, string>> ArtAsync(CampaignAccess dm, BookSource source, BookArtQuality quality, CancellationToken cancel)
    {
        // The slots' keys do not depend on the language (it only names the chapters).
        var compiled = BookComposer.Compose(source, "en", out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        // Two columns: the scaler sizes each picture for the box it prints in.
        var pictures = await artReader.ReadAsync(source, quality, 2, cancel, BookSlots.Choose(slots, await art.GetAsync(dm)));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var anchor in BookCompiler.ArtAnchors(entries, pictures))
        {
            if (pictures.DataUri(anchor.Asset) is { } uri) map[anchor.Entry.SlotKey] = uri;
        }

        foreach (var slot in slots.Where(s => s.Kind is BookSlots.Header or BookSlots.Cover))
        {
            var asset = pictures.Slot(slot.Key, slot.Audience) ?? (slot.Kind == BookSlots.Cover ? pictures.Cover() : null);
            if (asset != null && pictures.DataUri(asset) is { } uri) map[slot.Key] = uri;
        }

        return map;
    }
}
