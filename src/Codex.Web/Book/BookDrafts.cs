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
    BookDraftFollowed? Followed = null, bool Edge = false);

/// <summary>What a draft is printed or previewed from: the page shell, the brew, its chapters (with the version each was read at), and the
/// edition's pictures by slot.</summary>
/// <param name="Player">For one player's own book (docs/BOOK_EXPORT.md 45): their characters' names, which the file is named by.</param>
public sealed record BookDraftPage(string Title, string Shell, string Markdown, IReadOnlyDictionary<string, string> Art, IReadOnlyList<BookDraftPageChapter> Chapters,
    string? Player = null)
{
    public IReadOnlyList<BookLayoutChapter> Layout => [.. Chapters.Select(c => new BookLayoutChapter(c.Section, c.Markdown))];
}

public sealed record BookDraftPageChapter(string Section, string Title, string Markdown, int Version);

/// <summary>What "Bring up to date" reads of a chapter (docs/BOOK_EXPORT.md 41.18): the GM's text, its base, the version, and the chapter as the
/// campaign makes it now (null when the campaign no longer has it). <paramref name="Moved"/>: the campaign changed since the base.</summary>
public sealed record BookDraftMergeInput(CampaignAccess Dm, string SystemId, string Title, string Markdown, string Base, int Version, bool Moved, BookDraftGenerated? Now);

/// <summary>What Fit pages did (docs/BOOK_EXPORT.md 41.15). <paramref name="Result"/> is the first save that was refused, or Saved; chapters
/// before it are saved. <paramref name="Breaks"/> counts the fitted breaks the book has now, <paramref name="Chapters"/> the chapters whose
/// text changed. <paramref name="Refused"/> is why the draft could not be laid out at all (its language, for one).</summary>
public sealed record BookDraftFitReport(BookDraftSave Result, int Breaks, int Chapters, IReadOnlyList<BookOverflow> Overflow, IReadOnlyList<BookOverflow> Unplaced,
    string? Refused = null);

/// <summary>
/// The editable book's service (docs/BOOK_EXPORT.md 41): makes a draft from the edition's masked source, keeps the chapters nobody edited in step
/// with the campaign, says which edited chapters the campaign moved on from, and replaces one on Regenerate. Every call reads the campaign as the
/// asking GM may read it now; a key, a section or a language posted from the page is checked by the store before it reaches an id.
/// </summary>
public sealed class BookDrafts(BookSourceReader reader, IBookDraftRepository store, IBookArtRepository art, BookArtReader artReader, ICampaignAccessResolver resolver,
    Codex.Plugin.Abstractions.ISystemCatalog? systems = null)
{
    public static BookAudience? Audience(string edition) => edition switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };

    private async Task<(CampaignAccess Dm, BookSource Source, IReadOnlyList<BookDraftGenerated> Now)?> GenerateAsync(CampaignAccess asked, string edition, string language)
    {
        if (Audience(edition) is not { } audience || !BookDraftDocument.IsLanguage(language)) return null;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        if (await reader.ReadAsync(dm, audience) is not { } source) return null;
        return (dm, source, BookDraftWriter.Generate(source, language, await art.GetAsync(dm), systems?.GetDefinition(source.Campaign.SystemId)));
    }

    /// <summary>One chapter of the draft as stored, read as the asking GM may read the campaign now, with the campaign's game system. Null when
    /// the draft or the chapter is not there, or not the caller's. Names: the names this edition gives (<see cref="BookCompiler.Names"/>), for the
    /// facts check of an AI edit.</summary>
    public async Task<(CampaignAccess Dm, string SystemId, BookDraftChapterView Chapter, IReadOnlyList<string> Names)?> ChapterAsync(CampaignAccess asked, string edition,
        string language, string section)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return null;
        if (await store.GetAsync(made.Dm, edition, language) is not { } draft) return null;
        return View(draft, made.Now, null).Chapters.FirstOrDefault(c => c.Section == section) is { } chapter
            ? (made.Dm, made.Source.Campaign.SystemId, chapter, BookCompiler.Names(made.Source))
            : null;
    }

    /// <summary>Every chapter of the draft as stored, read as <see cref="ChapterAsync"/> reads one, with the edition's names: for an AI run over the
    /// whole book (docs/BOOK_EXPORT.md 42, step 3), which reads the campaign once and not once per chapter. Null when not the caller's.</summary>
    public async Task<(CampaignAccess Dm, string SystemId, IReadOnlyList<BookDraftChapterView> Chapters, IReadOnlyList<string> Names)?> ChaptersAsync(CampaignAccess asked,
        string edition, string language)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return null;
        if (await store.GetAsync(made.Dm, edition, language) is not { } draft) return null;
        return (made.Dm, made.Source.Campaign.SystemId, View(draft, made.Now, null).Chapters, BookCompiler.Names(made.Source));
    }

    /// <summary>A chapter's three versions for "Bring up to date", read as the asking GM may read the campaign now; null when not the caller's.</summary>
    public async Task<BookDraftMergeInput?> MergeInputAsync(CampaignAccess asked, string edition, string language, string section)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return null;
        if (await store.GetAsync(made.Dm, edition, language) is not { } draft) return null;
        if (draft.Chapters.FirstOrDefault(c => c.Section == section) is not { } chapter) return null;
        var now = made.Now.FirstOrDefault(g => g.Section == section);
        return new BookDraftMergeInput(made.Dm, made.Source.Campaign.SystemId, chapter.Title, chapter.Markdown, chapter.Base, chapter.Version,
            now != null && now.Key != chapter.BaseKey, now);
    }

    /// <summary>Keeps a merge: <paramref name="markdown"/> becomes the chapter's text and the campaign's chapter now its base. The campaign is read
    /// here, never taken from the page; when it has moved on from <paramref name="key"/> (the version the merge was made against) nothing is saved
    /// and the result is null, so the GM merges again rather than marking changes seen that were never shown.</summary>
    public async Task<(BookDraftSave Result, int Version)?> SaveMergeAsync(CampaignAccess asked, string edition, string language, string section, int version,
        string markdown, string key)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return (BookDraftSave.NotAllowed, version);
        if (made.Now.FirstOrDefault(g => g.Section == section) is not { } now) return (BookDraftSave.NotAllowed, version);
        if (now.Key != key) return null;
        return await store.MergeAsync(made.Dm, edition, language, now, markdown, version);
    }

    /// <summary>
    /// The party leak check (docs/BOOK_EXPORT.md 41.5, 41.19): what only the GM edition has, found in the party draft of
    /// <paramref name="language"/> as stored (its chapters and stylesheet). Both editions are read as the asking GM may read the campaign now;
    /// the GM edition's secrets as generated and as the GM's own draft of it has them. Null when not the caller's, or there is no party draft.
    /// </summary>
    public async Task<BookLeakReport?> LeaksAsync(CampaignAccess asked, string language)
    {
        if (await GenerateAsync(asked, "party", language) is not { } party) return null;
        if (await store.GetAsync(party.Dm, "party", language) is not { } draft) return null;
        if (await reader.ReadAsync(party.Dm, BookAudience.Dm) is not { } gm) return null;
        var gmNow = BookDraftWriter.Generate(gm, language, await art.GetAsync(party.Dm), systems?.GetDefinition(gm.Campaign.SystemId));
        var gmDraft = await store.GetAsync(party.Dm, "dm", language);
        var needles = BookLeakCheck.Needles(gm, party.Source,
            gmNow.Select(c => c.Markdown).Concat(gmDraft?.Chapters.Select(c => c.Markdown) ?? []));
        var chapters = draft.Chapters.Select(c => (c.Section, c.Markdown)).Append((BookLeakCheck.Css, draft.Document.Css));
        // A theme the GM brought in prints into the party book too: its stylesheet is looked in as the draft's own is (invariant 12).
        if (draft.Document.Theme.StartsWith(BookCustomTheme.Prefix, StringComparison.Ordinal)
            && await store.ThemeAsync(party.Dm, draft.Document.Theme[BookCustomTheme.Prefix.Length..]) is { } own)
        {
            chapters = chapters.Append((BookLeakCheck.Css, own.Css));
        }

        return BookLeakCheck.Find(needles, party.Now.Select(c => c.Markdown), chapters);
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
        return new BookDraftView(d.Edition, d.Language, d.Theme, d.Homebrewery, d.PageSize, d.Css, d.Version, chapters, followed, d.Edge);
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
        bool homebrewery, int version, bool? edge = null) =>
        store.SaveSettingsAsync(asked, edition, language, css, pageSize, theme, homebrewery, version, edge);

    /// <summary>The draft's copies of the web addresses its stylesheet names (docs/BOOK_EXPORT.md 41.14).</summary>
    public Task<BookCssWeb> WebAsync(CampaignAccess asked, string edition, string language) => store.WebAsync(asked, edition, language);

    public Task<bool> StartOverAsync(CampaignAccess asked, string edition, string language) => store.DeleteAsync(asked, edition, language);

    /// <summary>
    /// What the draft is drawn from: its shell (theme, faces, the GM's CSS, cleaned), its chapters as one brew, and the edition's pictures by slot,
    /// read for this edition only (<see cref="BookSlots.Choose"/>: the party draft gets only the party's). Null when not allowed; a shell of null
    /// with a reason when the theme cannot print the language.
    /// </summary>
    /// <para><paramref name="player"/>: one player's own copy of the party draft (<see cref="PrintedAsync"/>); null when that player has none.</para>
    public async Task<(BookDraftPage? Page, string? Refused)> PageAsync(CampaignAccess asked, string edition, string language, BookArtQuality quality,
        Func<string, byte[]?> font, CancellationToken cancel, bool withArt = true, string? player = null)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return (null, null);
        if (await store.GetAsync(made.Dm, edition, language) is not { } draft) return (null, null);
        if (await PrintedAsync(made.Dm, made.Source, draft, edition, language, player) is not { } printed) return (null, null);
        // A built-in theme or one the GM brought into the campaign (41.22), resolved as the editor's preview resolves it.
        var theme = await new BookThemeCatalog(store).ResolveAsync(made.Dm, draft.Document.Theme, language, font);
        var web = await store.WebAsync(made.Dm, edition, language);
        var shell = BookDraftHtml.Shell(made.Source.Campaign.Name, language, theme.Theme, draft.Document.PageSize, draft.Document.Css,
            theme.Homebrewery ?? draft.Document.Homebrewery, font, web, draft.Document.Edge);
        if (shell.Html == null) return (null, shell.Refused);
        var markdown = string.Join("\n\n\\page\n\n", printed.Chapters.Select(c => c.Markdown.TrimEnd()));
        // The party's pictures, read for the party's source: a player's copy adds text, never a picture.
        var pictures = withArt ? (await ArtAsync(made.Dm, made.Source, quality, cancel)).Art : new Dictionary<string, string>();
        return (new BookDraftPage(made.Source.Campaign.Name, shell.Html, markdown, pictures, printed.Chapters, printed.Player), null);
    }

    /// <summary>
    /// The edition's picture slots, for the editor's suggestions (docs/BOOK_EXPORT.md 47): each slot's key, kind and label as this edition gives
    /// it (the party's labels are the party's: a stranger by their mask). Empty when not allowed.
    /// </summary>
    public async Task<IReadOnlyList<BookSlot>> SlotsAsync(CampaignAccess asked, string edition, string language)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return [];
        var compiled = BookComposer.Compose(made.Source, language, out var entries);
        return BookSlots.For(made.Source, compiled, entries);
    }

    /// <summary>
    /// The players who have a book of their own, for the party draft's "Download PDF for…" (docs/BOOK_EXPORT.md 45). Empty for anyone but the
    /// campaign's GM.
    /// </summary>
    public async Task<IReadOnlyList<BookPlayer>> PlayersAsync(CampaignAccess asked) =>
        await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is { IsDm: true } dm ? await reader.PlayersAsync(dm) : [];

    /// <summary>
    /// The draft's chapters as they print: as stored, or for <paramref name="player"/> their own copy of the party draft, with what only their
    /// characters may read in notes (<see cref="BookPlayerBook"/>), and their characters' names. Null when a player is asked for and has no book
    /// (not the party edition, not a player of the campaign now, no character).
    /// </summary>
    private async Task<(IReadOnlyList<BookDraftPageChapter> Chapters, string? Player)?> PrintedAsync(CampaignAccess dm, BookSource party, BookDraft draft,
        string edition, string language, string? player)
    {
        IReadOnlyList<BookDraftPageChapter> stored = [.. draft.Chapters.Select(c => new BookDraftPageChapter(c.Section, c.Title, c.Markdown, c.Version))];
        if (player == null) return (stored, null);
        if (Audience(edition) != BookAudience.Party || await reader.ReadForPlayerAsync(dm, player) is not { } theirs) return null;
        var names = string.Join(", ", theirs.Actors.Where(a => theirs.OwnedActorIds.Contains(a.Id)).Select(a => a.Name.Trim())
            .Where(n => n.Length > 0).Order(StringComparer.CurrentCulture));
        var heading = string.Format(System.Globalization.CultureInfo.InvariantCulture, Codex.Localization.Localizer.For(language)["Campaign.Book.Draft.OnlyFor"], names);
        return (BookPlayerBook.Overlay(stored, party, theirs, language, heading), names);
    }

    /// <summary>
    /// The draft as one Markdown file (docs/BOOK_EXPORT.md 41.24): its chapters as stored, joined by page breaks, with Fit pages' breaks written
    /// as plain ones (<see cref="BookDraftText.Unfitted"/>: the mark is this app's, and means nothing anywhere else). Pictures stay <c>art:</c> references and the stylesheet is
    /// not in it. Null when not allowed.
    /// </summary>
    /// <para><paramref name="player"/>: one player's own copy of the party draft, as for the PDF; <c>Player</c> is then their characters' names.</para>
    public async Task<(string Title, string Markdown, string? Player)?> MarkdownAsync(CampaignAccess asked, string edition, string language, string? player = null)
    {
        if (await GenerateAsync(asked, edition, language) is not { } made) return null;
        if (await store.GetAsync(made.Dm, edition, language) is not { } draft) return null;
        if (await PrintedAsync(made.Dm, made.Source, draft, edition, language, player) is not { } printed) return null;
        var chapters = printed.Chapters.Select(c => string.Join("\n", c.Markdown.ReplaceLineEndings("\n").Split('\n')
            .Select(BookDraftText.Unfitted)).TrimEnd());
        return (made.Source.Campaign.Name, string.Join("\n\n\\page\n\n", chapters) + "\n", printed.Player);
    }

    /// <summary>
    /// Fit pages (docs/BOOK_EXPORT.md 41.15): the draft laid out by <paramref name="layout"/> (the print browser, as the PDF is), and each
    /// chapter whose text it gave breaks to saved at the version it was read at. A chapter changed meanwhile (another tab) is not overwritten:
    /// the run stops there and says so. Breaks are layout, not edits, so no chapter turns "edited" by them.
    /// </summary>
    public async Task<BookDraftFitReport?> FitAsync(CampaignAccess asked, string edition, string language, Func<string, byte[]?> font,
        Func<BookDraftPage, Task<BookFitted>> layout, CancellationToken cancel)
    {
        var (page, refused) = await PageAsync(asked, edition, language, BookArtQuality.Print, font, cancel);
        if (refused != null) return new BookDraftFitReport(BookDraftSave.NotAllowed, 0, 0, [], [], refused);
        if (page == null) return null;
        var fitted = await layout(page);
        var changed = 0;
        foreach (var chapter in page.Chapters)
        {
            if (fitted.Chapters.FirstOrDefault(c => c.Section == chapter.Section) is not { } after || after.Markdown == chapter.Markdown) continue;
            var (result, _) = await store.SaveChapterAsync(asked, edition, language, chapter.Section, after.Markdown, chapter.Version);
            if (result != BookDraftSave.Saved) return new BookDraftFitReport(result, 0, changed, fitted.Overflow, fitted.Unplaced);
            changed++;
        }

        var breaks = fitted.Chapters.Sum(c => c.Markdown.ReplaceLineEndings("\n").Split('\n').Count(BookDraftText.IsFitted));
        return new BookDraftFitReport(BookDraftSave.Saved, breaks, changed, fitted.Overflow, fitted.Unplaced);
    }

    /// <summary>The edition's pictures by slot key, as data images: the cover, each chapter's header, and each entry's (the GM's book picture for
    /// the slot, or the entry's own picture as this source flags it); and the pictures left out, each by its chapter and the label this edition
    /// gives its entry. The preview and the print leave out the same ones (<see cref="BookArtReader"/>).</summary>
    public async Task<BookDraftArt> ArtAsync(CampaignAccess asked, string edition, BookArtQuality quality, CancellationToken cancel)
    {
        if (Audience(edition) is not { } audience) return BookDraftArt.None;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return BookDraftArt.None;
        if (await reader.ReadAsync(dm, audience) is not { } source) return BookDraftArt.None;
        return await ArtAsync(dm, source, quality, cancel);
    }

    private async Task<BookDraftArt> ArtAsync(CampaignAccess dm, BookSource source, BookArtQuality quality, CancellationToken cancel)
    {
        // The slots' keys do not depend on the language (it only names the chapters).
        var compiled = BookComposer.Compose(source, "en", out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        // Two columns: the scaler sizes each picture for the box it prints in.
        var pictures = await artReader.ReadAsync(source, quality, 2, cancel, BookSlots.Choose(slots, await art.GetAsync(dm)), dm.UserId);
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

        // Named only through this edition's own slots: a picture whose slot the edition does not have is not named at all.
        var bySlot = slots.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var left = pictures.Skipped
            .Select(s => s.Slot != null && bySlot.TryGetValue(s.Slot, out var slot) ? new BookDraftLeftOut(slot.Kind == BookSlots.Cover ? BookDraftWriter.Front : slot.Section, slot.Label, s.Reason) : null)
            .OfType<BookDraftLeftOut>()
            .ToList();
        return new BookDraftArt(map, left);
    }
}

/// <summary>The edition's pictures by slot key, and the ones left out.</summary>
public sealed record BookDraftArt(IReadOnlyDictionary<string, string> Art, IReadOnlyList<BookDraftLeftOut> LeftOut)
{
    public static readonly BookDraftArt None = new(new Dictionary<string, string>(), []);
}

/// <summary>A picture that is not in the book: the chapter it is in, the entry's label as this edition gives it, and why (<c>limit</c> when
/// the book's size cap was reached, else what was wrong with the file).</summary>
public sealed record BookDraftLeftOut(string Section, string Label, string Reason);
