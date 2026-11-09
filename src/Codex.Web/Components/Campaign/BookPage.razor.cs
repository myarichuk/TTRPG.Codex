using System.Globalization;
using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Book;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Codex.Web.Components.Campaign;

/// <summary>
/// The Book page (docs/BOOK_EXPORT.md 49): the GM's editable book of one edition, on a page of its own under the campaign's header. This part
/// holds its state and what it does; the toolbar, outline, source, preview and dock (<see cref="BookPart"/>) draw it.
/// </summary>
public partial class BookPage : IAsyncDisposable
{
    [CascadingParameter] public CampaignContext Ctx { get; set; } = null!;

    /// <summary>The route's edition: "gm" or "party" (anything else, or none, goes to "gm").</summary>
    [Parameter] public string? EditionRoute { get; set; }

    /// <summary>The route's chapter: a section id, or "style" for the stylesheet.</summary>
    [Parameter] public string? ChapterRoute { get; set; }

    internal CampaignAccess Access => Ctx.Access;
    internal string BasePath => Ctx.BasePath;

    internal DotNetObjectReference<BookPage>? self;
    internal IJSObjectReference? module;
    internal ElementReference host;
    internal ElementReference frame;
    internal string? editorId;
    internal int openings;
    internal BookDraftView? view;
    internal string edition = "party";
    internal string language = "en";
    internal string pageSize = "A4";
    internal string? current = BookDraftWriter.Front;
    internal bool openRequested;
    internal bool printing;
    internal bool working;
    internal bool fitting;
    internal BookDraftFitReport? fitReport;
    internal string? printReport;
    internal BookLeakReport? leakReport;
    internal IReadOnlyList<(string Section, string Characters, int More)> letters = [];
    internal IReadOnlyList<BookDraftLeftOut> leftOut = [];
    internal IReadOnlyList<BookThemeDocument> customThemes = [];
    internal bool wizardOpen;

    /// <summary>The wizard closed: the campaign's themes read again, and the theme it saved, if any, chosen for this draft.</summary>
    internal async Task WizardClosedAsync((string Id, bool Homebrewery)? chosen)
    {
        wizardOpen = false;
        customThemes = (await ThemeStore.ThemesAsync(Access)).Where(t => t.Ready).ToList();
        if (chosen is { } c && view != null) await SettingsAsync(c.Id, c.Homebrewery);
    }

    // Each bundled face's character map, read once per process (BookFontCoverage).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlySet<int>?> FontMaps = new(StringComparer.Ordinal);

    /// <summary>The characters of the draft no face of its theme draws, by chapter (the Problems tab).</summary>
    internal async Task CheckLettersAsync()
    {
        if (view == null)
        {
            letters = [];
            return;
        }

        var resolved = await Catalog.ResolveAsync(Access, view.Theme, language, Font);
        var chapters = view.Chapters.Select(c => (c.Section, c.Markdown));
        if (resolved.Faces is { } measured)
        {
            // A theme the GM brought in: its own faces, the layer's when it sits on it, and the stand-ins for this language.
            letters = BookFontCoverage.Uncovered(chapters, measured);
            return;
        }

        var theme = resolved.Theme;
        // A theme with no faces of its own (Homebrewery layout) draws in the layer's; checking against nothing would list every letter.
        var faces = theme.Fonts.Count > 0
            ? theme.FontsFor(language)
            : BookDraftThemes.LayerFaces.Select(f => new BookFontFace(f.Family, 400, f.Style, f.File, "U+0-10FFFF")).ToList();
        letters = BookFontCoverage.Uncovered(chapters, faces,
            file => FontMaps.GetOrAdd(file, f => Font(f) is { } bytes ? BookFontCoverage.Read(bytes) : null));
    }
    internal bool confirmStartOver;
    internal bool confirmMarkdown;
    // Which download the party leak check stopped: Download anyway makes that one.
    internal bool leaksForMarkdown;
    internal string? leaksForPlayer;

    // The players who have a book of their own, read when the menu opens (a player may have joined since the editor did).
    internal IReadOnlyList<BookPlayer> players = [];

    internal async Task ToggleMoreAsync()
    {
        moreOpen = !moreOpen;
        if (moreOpen && edition == "party") players = await Drafts.PlayersAsync(Access);
    }

    /// <summary>One player's own book as a PDF (docs/BOOK_EXPORT.md 45). The party draft is leak-checked first, as for the party's own PDF: their copy
    /// is that draft with their notes added.</summary>
    internal async Task PlayerDownloadAsync(BookPlayer player)
    {
        moreOpen = false;
        await DownloadAsync(false, player: player.UserId);
    }
    internal string? message;
    internal string? messageDetail;
    internal SaveState state = SaveState.Saved;
    internal readonly SemaphoreSlim saving = new(1, 1);
    internal readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    internal bool cssDirty;

    // The draft's copies of the web addresses its stylesheet names (docs/BOOK_EXPORT.md 41.14), what the last fetch did, and the stylesheet as
    // last saved (a fetch that ends restyles the preview with it, not with the text it started from).
    internal BookCssWeb web = new();
    internal BookWebReport? webReport;
    internal int fetching;
    internal string savedCss = string.Empty;
    internal readonly CancellationTokenSource closing = new();

    internal string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 1048576 ? "0.00" : "0.0", System.Globalization.CultureInfo.CurrentCulture);

    internal string Problem(BookWebFailure failure) => failure.Problem switch
    {
        BookWebProblem.NotPublic => L["Campaign.Book.Draft.Web.NotPublic"],
        BookWebProblem.TimedOut => L["Campaign.Book.Draft.Web.TimedOut"],
        BookWebProblem.Refused => L["Campaign.Book.Draft.Web.Refused", failure.Status],
        BookWebProblem.TooLarge => L["Campaign.Book.Draft.Web.TooLarge"],
        BookWebProblem.NotWhatWasNamed => L["Campaign.Book.Draft.Web.NotWhatWasNamed"],
        BookWebProblem.TooManyRedirects => L["Campaign.Book.Draft.Web.TooManyRedirects"],
        BookWebProblem.Full => L["Campaign.Book.Draft.Web.Full"],
        BookWebProblem.NotListed => L["Campaign.Book.Draft.Web.NotListed", new Uri(failure.Url).Host],
        _ => L["Campaign.Book.Draft.Web.Unreachable"]
    };

    internal enum SaveState { Saved, Saving, Conflict, Failed }

    internal BookDraftChapterView? Current => current == null ? null : view?.Chapters.FirstOrDefault(c => c.Section == current);

    internal string EditionName => edition == "dm" ? L["Campaign.Book.Edition.Dm"] : L["Campaign.Book.Edition.Party"];

    internal string SaveText => state switch
    {
        SaveState.Saving => L["Campaign.Book.Draft.Saving"],
        SaveState.Conflict => L["Campaign.Book.Draft.Conflict"],
        SaveState.Failed => L["Campaign.Book.Draft.SaveFailed"],
        _ => dirty.Count > 0 || cssDirty ? L["Campaign.Book.Draft.Unsaved"] : L["Campaign.Book.Draft.Saved"]
    };

    internal string Title(BookDraftChapterView chapter) => chapter.Section == BookDraftWriter.Front ? L["Campaign.Book.Draft.Front"] : chapter.Title;

    internal string TitleOf(string? section) =>
        view?.Chapters.FirstOrDefault(c => c.Section == section) is { } chapter ? Title(chapter) : section ?? string.Empty;

    // A line of the Fit pages report: the page (when there is one), its chapter, and the first words of what it is about.
    internal string Item(BookOverflow o) => o.Page > 0
        ? L["Campaign.Book.Draft.Fit.Item", o.Page, TitleOf(o.Section), o.Words ?? string.Empty]
        : L["Campaign.Book.Draft.Fit.ItemNoPage", TitleOf(o.Section), o.Words ?? string.Empty];

    /// <summary>Opens the editor on the GM's draft of an edition in a language, making the draft first if there is none.</summary>
    public async Task OpenAsync(string edition, string language, string pageSize)
    {
        this.edition = edition;
        this.language = language;
        this.pageSize = pageSize;
        message = null;
        confirmStartOver = false;
        leaveAsked = null;
        view = await Drafts.OpenAsync(Access, edition, language, pageSize);
        customThemes = (await ThemeStore.ThemesAsync(Access)).Where(t => t.Ready).ToList();
        await CheckLettersAsync();
        if (view == null)
        {
            message = L["Campaign.Book.Draft.NotAllowed"];
            return;
        }

        // The chapter the address names (a reload or a link restores the place); else the first that moved on, else the first.
        current = ChapterRoute == StyleRoute ? null
            : view.Chapters.FirstOrDefault(c => c.Section == ChapterRoute)?.Section
              ?? view.Chapters.FirstOrDefault(c => c.Stale)?.Section ?? view.Chapters.FirstOrDefault()?.Section;
        web = await Drafts.WebAsync(Access, edition, language);
        await ReadSpendAsync();
        webReport = null;
        leakReport = null;
        savedCss = view.Css;
        state = SaveState.Saved;
        dirty.Clear();
        cssDirty = false;
        openRequested = true;
        StateHasChanged();
    }

    protected override void OnInitialized()
    {
        // The book is in the reader's language when a theme can print it, else in English.
        var preferred = SupportedCultures.Normalize(CultureInfo.CurrentUICulture.Name);
        language = BookDraftThemes.All.Any(t => t.Supports(preferred)) ? preferred : "en";
    }

    protected override void OnParametersSet()
    {
        // A missing or unknown edition is the GM's own; the address says so.
        if (EditionRoute is not ("gm" or "party")) Nav.NavigateTo($"{BasePath}/book/gm", replace: true);
        var wanted = EditionRoute == "party" ? "party" : "dm";
        if (shownEdition != wanted)
        {
            // Another edition (or the first): this one's draft is opened once the page is interactive.
            shownEdition = wanted;
            edition = wanted;
            started = false;
            view = null;
        }
    }

    private string? shownEdition;

    internal const string StyleRoute = "style";
    private bool started;
    private bool starting;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Only the interactive circuit gets here (prerendering has no after-render), so nothing is made twice.
        if (!started && !starting && EditionRoute is "gm" or "party")
        {
            starting = true;
            try
            {
                // The book's language is the one the GM last chose in this browser, else the reader's own.
                if (!layoutRead)
                {
                    await ReadLayoutAsync();
                    if (savedLanguage != null && BookDraftThemes.All.Any(t => t.Supports(savedLanguage))) language = savedLanguage;
                }

                await OpenAsync(edition, language, pageSize);
            }
            finally
            {
                starting = false;
                // The address moved on to another edition while this one was opening: that one is opened next.
                started = shownEdition == edition;
                if (!started) StateHasChanged();
            }
        }

        if (!openRequested || view == null) return;
        openRequested = false;
        if (!layoutRead)
        {
            await ReadLayoutAsync();
            StateHasChanged();
        }

        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", "/" + Assets["lib/book/book-editor.js"]);
            self ??= DotNetObjectReference.Create(this);
            if (editorId != null) await module.InvokeVoidAsync("close", editorId);
            // Opening waits for the preview to draw; another opening may start meanwhile (a theme picked twice quickly). Only the last one's
            // editor is kept: the script closes an earlier one when the next starts, and an earlier one that ends last is not taken.
            var opening = ++openings;
            var asked = current;
            var shell = await ShellAsync();
            if (shell.Html == null)
            {
                // The only refusal is the language gate (a theme stored before the draft's language could use it, or one with Latin faces only).
                message = L["Campaign.Book.Failed.Language"];
                StateHasChanged();
                return;
            }

            var opened = await module.InvokeAsync<string>("open", host, frame, new
            {
                chapters = view.Chapters.Select(c => new { section = c.Section, markdown = c.Markdown }),
                css = view.Css,
                shell = shell.Html,
                artUrl = $"{BasePath}/book-file/{edition}/draft/{language}/art",
                current = asked,
                rtl = BookThemes.IsRtl(language),
                assist = await AssistAsync()
            }, self, new
            {
                overflow = L["Campaign.Book.Draft.Overflow"].ToString(),
                runOn = L["Campaign.Book.Draft.RunOn"].ToString(),
                titles = view.Chapters.ToDictionary(c => c.Section, Title)
            });
            if (opening != openings) return;
            editorId = opened;
            if (current == null || current != asked) await module.InvokeVoidAsync("show", editorId, current);
            await SyncUrlAsync();
            // An address the last fetch could not reach is tried again.
            _ = FetchWebAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "The book editor could not start");
            message = L["Campaign.Book.Draft.EditorFailed"];
            StateHasChanged();
        }
    }

    internal byte[]? Font(string file)
    {
        var fonts = Path.GetFullPath(Path.Combine(Env.WebRootPath, "fonts"));
        var path = Path.GetFullPath(Path.Combine(fonts, file));
        return path.StartsWith(fonts, StringComparison.Ordinal) && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    internal async Task<BookHtmlResult> ShellAsync()
    {
        // A built-in theme or one of the campaign's own (41.22), resolved as the PDF resolves it.
        var theme = await Catalog.ResolveAsync(Access, view!.Theme, language, Font);
        return BookDraftHtml.Shell(string.Empty, language, theme.Theme, view.PageSize, view.Css, theme.Homebrewery ?? view.Homebrewery, Font, web, view.Edge);
    }

    internal async Task ShowAsync(string? section)
    {
        // Clicked before the editor has started: remembered, and shown once it has (a click was lost on a slow machine).
        if (module == null || editorId == null)
        {
            current = section;
            await SyncUrlAsync();
            return;
        }

        await FlushAsync();
        await module.InvokeVoidAsync("show", editorId, section);
        current = section;
        await SyncUrlAsync();
    }

    /// <summary>The editor says a chapter changed (typing stopped): its text is read as a stream and saved.</summary>
    [JSInvokable]
    public async Task Changed(string section)
    {
        dirty.Add(section);
        await SaveAsync(section);
    }

    /// <summary>The pictures the preview's art left out, as the server sent them with it.</summary>
    [JSInvokable]
    public Task ArtLeftOut(List<BookDraftLeftOut>? pictures)
    {
        leftOut = pictures ?? [];
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task CssChanged()
    {
        cssDirty = true;
        await SaveCssAsync();
    }

    internal async Task<string?> ReadAsync(string? section)
    {
        if (module == null || editorId == null) return null;
        if (await module.InvokeAsync<int>("length", editorId, section) == 0) return string.Empty;
        var stream = await module.InvokeAsync<IJSStreamReference>("read", editorId, section);
        await using var read = await stream.OpenReadStreamAsync(BookDraftDocument.MaxChapterChars * 4L + 16);
        using var reader = new StreamReader(read, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    internal async Task SaveAsync(string section)
    {
        if (view == null || state == SaveState.Conflict) return;
        await saving.WaitAsync();
        try
        {
            if (!dirty.Contains(section) || view.Chapters.FirstOrDefault(c => c.Section == section) is not { } chapter) return;
            state = SaveState.Saving;
            await InvokeAsync(StateHasChanged);
            var text = await ReadAsync(section);
            if (text == null) return;
            var (result, version) = await Drafts.SaveChapterAsync(Access, edition, language, section, text, chapter.Version);
            switch (result)
            {
                case BookDraftSave.Saved:
                    dirty.Remove(section);
                    state = SaveState.Saved;
                    await RefreshAsync(push: false);
                    break;
                case BookDraftSave.Conflict:
                    // Another tab saved this chapter: nothing is overwritten, and nothing more is saved from this copy.
                    state = SaveState.Conflict;
                    break;
                case BookDraftSave.TooLarge:
                    state = SaveState.Failed;
                    message = L["Campaign.Book.Draft.TooLarge"];
                    break;
                default:
                    state = SaveState.Failed;
                    break;
            }
        }
        catch (Exception ex) when (ex is JSException or IOException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Saving a book chapter failed");
            state = SaveState.Failed;
        }
        finally
        {
            saving.Release();
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task SaveCssAsync()
    {
        if (view == null || state == SaveState.Conflict) return;
        var saved = false;
        await saving.WaitAsync();
        try
        {
            if (!cssDirty) return;
            var css = await ReadAsync(null);
            if (css == null) return;
            var (result, _) = await Drafts.SaveSettingsAsync(Access, edition, language, css, view.PageSize, view.Theme, view.Homebrewery, view.Version);
            if (result == BookDraftSave.Saved)
            {
                cssDirty = false;
                state = SaveState.Saved;
                savedCss = css;
                saved = true;
                await RefreshAsync(push: false);
                if (module != null && editorId != null) await module.InvokeVoidAsync("style", editorId, BookCss.Clean(css, out _, web));
            }
            else
            {
                state = result == BookDraftSave.Conflict ? SaveState.Conflict : SaveState.Failed;
                if (result == BookDraftSave.TooLarge) message = L["Campaign.Book.Draft.TooLarge"];
            }
        }
        catch (Exception ex) when (ex is JSException or IOException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Saving the book's stylesheet failed");
            state = SaveState.Failed;
        }
        finally
        {
            saving.Release();
            await InvokeAsync(StateHasChanged);
        }

        // Outside the save: a fetch can take seconds, and chapters keep saving meanwhile.
        if (saved) _ = FetchWebAsync();
    }

    /// <summary>Fetches the web addresses the saved stylesheet names and has no copy of, lets go of copies it no longer names, and restyles the
    /// preview with the copies. A newer save's fetch takes over from this one.</summary>
    internal async Task FetchWebAsync()
    {
        if (view == null) return;
        var css = savedCss;
        if (!BookDraftWebs.NamesTheWeb(css, new BookCssWeb(web.Files)))
        {
            webReport = null;
            return;
        }

        fetching++;
        await InvokeAsync(StateHasChanged);
        try
        {
            var report = await Webs.RefreshAsync(Access, edition, language, css, closing.Token);
            if (report == null || view == null) return;
            webReport = report;
            if (report.Failed.Count > 0) Reveal("problems");
            // Nothing fetched or let go: the preview already has these copies (the save restyled it with them).
            if (!report.Changed) return;
            web = await Drafts.WebAsync(Access, edition, language);
            if (module != null && editorId != null) await module.InvokeVoidAsync("style", editorId, BookCss.Clean(savedCss, out _, web));
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or IOException)
        {
            Logger.LogWarning(ex, "Fetching the book stylesheet's web addresses failed");
        }
        finally
        {
            fetching--;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>The draft read again (versions, notices). With <paramref name="push"/>, chapters whose text the server changed go to the editor.</summary>
    internal async Task RefreshAsync(bool push, string? section = null)
    {
        var fresh = await Drafts.GetAsync(Access, edition, language);
        if (fresh == null) return;
        var before = view;
        view = fresh;
        await CheckLettersAsync();
        if (!push || module == null || editorId == null) return;
        if (before == null || before.Chapters.Select(c => c.Section).SequenceEqual(fresh.Chapters.Select(c => c.Section)) == false)
        {
            await module.InvokeVoidAsync("chapters", editorId, fresh.Chapters.Select(c => new { section = c.Section, markdown = c.Markdown }));
        }

        foreach (var chapter in fresh.Chapters.Where(c => section == null || c.Section == section))
        {
            await module.InvokeVoidAsync("replace", editorId, chapter.Section, chapter.Markdown);
        }
    }

    internal sealed record Pending(string[] Sections, bool Css);

    /// <summary>Saves what was typed and not yet saved, and returns when it is saved (or refused).</summary>
    internal async Task FlushAsync()
    {
        if (module == null || editorId == null) return;
        Pending? pending;
        try
        {
            pending = await module.InvokeAsync<Pending>("flush", editorId);
        }
        catch (JSDisconnectedException)
        {
            return;
        }

        foreach (var section in pending?.Sections ?? [])
        {
            dirty.Add(section);
            await SaveAsync(section);
        }

        if (pending?.Css == true)
        {
            cssDirty = true;
            await SaveCssAsync();
        }

        // A save the editor had already sent may still be running.
        await saving.WaitAsync();
        saving.Release();
    }

    internal async Task Act(string section, Func<int, Task<(BookDraftSave Result, int Version)>> action)
    {
        if (working) return;
        working = true;
        message = null;
        try
        {
            await FlushAsync();
            if (view?.Chapters.FirstOrDefault(c => c.Section == section) is not { } chapter) return;
            var (result, _) = await action(chapter.Version);
            if (result == BookDraftSave.Conflict) state = SaveState.Conflict;
            else if (result != BookDraftSave.Saved) message = L["Campaign.Book.Draft.ActionFailed"];
            await RefreshAsync(push: result == BookDraftSave.Saved, section);
        }
        finally
        {
            working = false;
        }
    }

    /// <summary>Another theme: what was typed is saved first, the theme is stored (the chapters are not touched), and the preview is drawn again
    /// in the new theme's page.</summary>
    internal Task ThemeAsync(ChangeEventArgs e) =>
        view == null || e.Value is not string chosen || chosen == view.Theme ? Task.CompletedTask
        // A campaign theme brings its own choice of the layer; a built-in one keeps the draft's.
        : SettingsAsync(chosen, customThemes.FirstOrDefault(t => BookCustomTheme.Prefix + t.Key == chosen)?.Homebrewery ?? view.Homebrewery);

    /// <summary>The Homebrewery layout under the theme, on or off: saved as a theme is, and the preview drawn again.</summary>
    internal Task HomebreweryAsync(ChangeEventArgs e) =>
        view == null || e.Value is not bool on || on == view.Homebrewery ? Task.CompletedTask : SettingsAsync(view.Theme, on);

    /// <summary>Torn page edges (docs/BOOK_EXPORT.md 46), on or off: saved with the settings, and the preview drawn again.</summary>
    internal async Task EdgeAsync()
    {
        moreOpen = false;
        if (view != null) await SettingsAsync(view.Theme, view.Homebrewery, !view.Edge);
        await MoreFocusAsync();
    }

    /// <summary>The paper the draft is laid out for ("A4" or "Letter"): saved with the settings, and the preview drawn again.</summary>
    internal async Task PaperAsync(string size)
    {
        moreOpen = false;
        if (view != null && view.PageSize != size) await SettingsAsync(view.Theme, view.Homebrewery, null, size);
        await MoreFocusAsync();
    }

    /// <summary>A picture was put in or taken out (the dock's Pictures tab): the preview takes the edition's pictures again.</summary>
    internal async Task ArtChangedAsync()
    {
        if (module == null || editorId == null) return;
        try
        {
            await module.InvokeVoidAsync("refreshArt", editorId, $"{BasePath}/book-file/{edition}/draft/{language}/art");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
        }
    }

    // A menu item that closes the menu would leave focus nowhere: it goes back to the button that opened it.
    internal async Task MoreFocusAsync()
    {
        StateHasChanged();
        try
        {
            await JS.InvokeVoidAsync("codex.focus", "book-draft-more");
        }
        catch (JSException)
        {
        }
    }

    internal async Task SettingsAsync(string chosen, bool homebrewery, bool? edge = null, string? size = null)
    {
        if (working || view == null) return;
        working = true;
        message = null;
        try
        {
            await FlushAsync();
            if (view == null || state == SaveState.Conflict) return;
            var (result, _) = await Drafts.SaveSettingsAsync(Access, edition, language, view.Css, size ?? view.PageSize, chosen, homebrewery, view.Version, edge);
            if (result == BookDraftSave.Conflict) state = SaveState.Conflict;
            else if (result != BookDraftSave.Saved) message = L["Campaign.Book.Draft.ActionFailed"];
            await RefreshAsync(push: false);
            if (result == BookDraftSave.Saved) openRequested = true;
        }
        finally
        {
            working = false;
        }
    }

    internal Task RegenerateAsync(string section) => Act(section, version => Drafts.RegenerateAsync(Access, edition, language, section, version));

    internal Task UndoAsync(string section) => Act(section, version => Drafts.UndoAsync(Access, edition, language, section, version));

    internal Task KeepAsync(string section) => Act(section, version => Drafts.KeepAsync(Access, edition, language, section, version));

    internal async Task StartOverAsync()
    {
        moreOpen = false;
        await FlushAsync();
        confirmStartOver = true;
    }

    internal async Task StartOverConfirmedAsync()
    {
        confirmStartOver = false;
        await Drafts.StartOverAsync(Access, edition, language);
        await OpenAsync(edition, language, pageSize);
    }

    /// <summary>
    /// Fit pages (docs/BOOK_EXPORT.md 41.15): what was typed is saved first; the draft is laid out in the print browser, as the PDF is (the GM's
    /// browser may break lines differently), a break is written wherever a block would run onto a page of its own, and the chapters it changed
    /// are saved and put back in the editor. The report says what it wrote, what still prints cut off, and what it could not place.
    /// </summary>
    internal async Task FitAsync()
    {
        if (working || view == null) return;
        working = true;
        fitting = true;
        message = null;
        fitReport = null;
        printReport = null;
        try
        {
            await FlushAsync();
            if (view == null || state == SaveState.Conflict) return;
            using var slot = Pdf.TryReserve(Access.UserId);
            if (slot == null)
            {
                message = L["Campaign.Book.Failed.Busy"];
                return;
            }

            var script = BookEndpoints.RenderScript(Env);
            var report = await Drafts.FitAsync(Access, edition, language, Font,
                page => Pdf.FitAsync(slot, page.Shell, new BookPrintLayout(script, page.Markdown, page.Art) { Chapters = page.Layout }, closing.Token), closing.Token);
            if (report == null)
            {
                message = L["Campaign.Book.Draft.NotAllowed"];
                return;
            }

            if (report.Refused != null)
            {
                message = L["Campaign.Book.Failed.Language"];
                return;
            }

            if (report.Result == BookDraftSave.Conflict) state = SaveState.Conflict;
            else if (report.Result == BookDraftSave.TooLarge) message = L["Campaign.Book.Draft.TooLarge"];
            else if (report.Result != BookDraftSave.Saved) message = L["Campaign.Book.Draft.ActionFailed"];
            fitReport = report;
            Reveal("problems");
            if (report.Chapters > 0) await RefreshAsync(push: true);
        }
        catch (BookPdfTimeoutException)
        {
            message = L["Campaign.Book.Failed.Timeout"];
        }
        catch (BookPdfUnavailableException)
        {
            message = L["Campaign.Book.Failed.NoBrowser"];
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException)
        {
            Logger.LogWarning(ex, "Fit pages failed");
            message = L["Campaign.Book.Draft.Fit.Failed"];
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            fitting = false;
            working = false;
        }
    }

    /// <summary>The draft as a Markdown file (docs/BOOK_EXPORT.md 41.24): the GM edition asks first, the party edition is leak-checked as the PDF is.</summary>
    internal async Task MarkdownAsync()
    {
        moreOpen = false;
        if (edition == "dm")
        {
            confirmMarkdown = true;
            return;
        }

        await DownloadAsync(false, markdown: true);
    }

    /// <summary>Prints the draft, or with <paramref name="markdown"/> gives its Markdown. The party edition is first checked for what only the GM
    /// edition has (docs/BOOK_EXPORT.md 41.19): with a hit, nothing is made and the Problems tab lists each; <paramref name="anyway"/> (the GM
    /// read them) makes it all the same.</summary>
    internal async Task DownloadAsync(bool anyway, bool markdown = false, string? player = null)
    {
        printing = true;
        message = null;
        printReport = null;
        confirmMarkdown = false;
        try
        {
            await FlushAsync();
            if (edition == "party" && !anyway)
            {
                leakReport = await Drafts.LeaksAsync(Access, language);
                leaksForMarkdown = markdown;
                leaksForPlayer = player;
                if (leakReport is { Total: > 0 })
                {
                    Reveal("problems");
                    return;
                }
            }

            var url = Nav.ToAbsoluteUri($"{BasePath}/book-file/{edition}/draft/{language}/{(markdown ? "md" : "pdf")}"
                + (player != null ? "?player=" + Uri.EscapeDataString(player) : string.Empty)).ToString();
            var result = await JS.InvokeAsync<BookDownload>("codex.downloadFrom", url);
            if (result.Status != 200)
            {
                message = (result.Status, result.Code) switch
                {
                    (422, _) => L["Campaign.Book.Failed.Language"],
                    (429, _) => L["Campaign.Book.Failed.Busy"],
                    (504, _) => L["Campaign.Book.Failed.Timeout"],
                    (503, "browser-too-old") => L["Campaign.Book.Failed.TooOld"],
                    (503, "launch-failed") => L["Campaign.Book.Failed.Launch"],
                    (503, _) => L["Campaign.Book.Failed.NoBrowser"],
                    _ => L["Campaign.Book.Failed.Other"]
                };
                // The setup sentence names an environment variable, which the person who runs the app needs; it is in English and
                // is shown as its own run so a right-to-left sentence around it does not reorder it.
                messageDetail = result is { Status: 503, Code: "no-browser" } ? result.Detail : null;
            }
            else if (BookEndpoints.ReadOverflowHeader(result.Overflow) is { Count: > 0 } cut)
            {
                // As the print browser laid it out, which the preview may not match line for line.
                Reveal("problems");
                printReport = L["Campaign.Book.Draft.PrintedOverflow",
                    string.Join(", ", cut.Select(o => o.Section is { } section ? $"{o.Page} ({TitleOf(section)})" : o.Page.ToString(System.Globalization.CultureInfo.CurrentCulture)))];
            }
        }
        finally
        {
            printing = false;
        }
    }

    // The layout (docs/BOOK_EXPORT.md 41.17): which panes are open and the panel's tab, remembered in this browser.
    internal enum Pane { Chapters, Preview }

    internal const string LayoutKey = "codex.bookEditor.layout";
    internal const string AssistKey = "codex.bookEditor.assist";

    // Suggestions and hover help in the editor (docs/BOOK_EXPORT.md 47): on unless the GM turned them off in this browser.
    internal bool assistOn = true;

    internal async Task AssistToggleAsync()
    {
        moreOpen = false;
        assistOn = !assistOn;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", AssistKey, assistOn ? "1" : "0");
        }
        catch (JSException)
        {
        }

        if (module != null && editorId != null) await module.InvokeVoidAsync("setAssist", editorId, assistOn);
        await MoreFocusAsync();
    }

    /// <summary>What the editor's suggestions need: this edition's picture slots by their labels, and the help in the GM's language.</summary>
    internal async Task<object> AssistAsync()
    {
        string T(string key) => L[key];
        var slots = await Drafts.SlotsAsync(Access, edition, language);
        return new
        {
            on = assistOn,
            dm = edition == "dm",
            slots = slots.Select(s => new { key = s.Key, kind = s.Kind, label = s.Label }),
            help = new
            {
                blocks = new[] { "note", "descriptive", "secret", "monster", "portrait", "place", "spot", "wide", "quote" }.ToDictionary(b => b, b => T("Campaign.Book.Draft.Help.Block." + b)),
                frames = new[] { "torn", "oval", "round", "frame" }.ToDictionary(f => f, f => T("Campaign.Book.Draft.Help.Frame." + f)),
                breaks = new[] { "page", "column" }.ToDictionary(b => b, b => T("Campaign.Book.Draft.Help.Break." + b)),
                slot = T("Campaign.Book.Draft.Help.Slot"),
                entry = T("Campaign.Book.Draft.Help.Entry"),
                spot = T("Campaign.Book.Art.Spot"),
                gmOnly = T("Campaign.Book.Draft.Help.GmOnly")
            }
        };
    }
    internal bool chaptersOpen = true;
    internal bool previewOpen = true;
    internal bool panelOpen = true;
    internal string panelTab = "ai";
    internal bool moreOpen;
    internal bool layoutRead;

    // Under 900 px wide one pane shows at a time: the Source or the Preview.
    internal string mobilePane = "source";

    internal void ShowMobilePane(string pane)
    {
        mobilePane = pane;
        // A folded preview has nothing to show on a phone.
        if (pane == "preview") previewOpen = true;
    }

    // The splitter between the Source and the Preview (docs/BOOK_EXPORT.md 49): the Source's width in pixels, 0 until the GM drags it; kept in this browser.
    internal int splitWidth;
    private string? savedLanguage;
    internal const string LanguageKey = "codex.bookEditor.language";

    /// <summary>Another language for the book: each edition has a draft per language, so the other one is opened (what was typed is saved first).</summary>
    internal async Task LanguageAsync(string tag)
    {
        moreOpen = false;
        if (tag == language || working || AiBusy) return;
        await FlushAsync();
        if (Unsaved)
        {
            message = L["Campaign.Book.Draft.CloseUnsaved"];
            return;
        }

        language = tag;
        savedLanguage = tag;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", LanguageKey, tag);
        }
        catch (JSException)
        {
        }

        view = null;
        started = false;
    }
    internal const string SplitKey = "codex.bookEditor.split";
    internal const int SplitMin = 360;
    internal const int SplitStep = 24;

    /// <summary>A drag on the splitter: the script moves it as the pointer does and answers with the width where it was let go.</summary>
    internal async Task SplitDragAsync(MouseEventArgs e)
    {
        if (e.Button != 0) return;
        try
        {
            splitWidth = await JS.InvokeAsync<int>("codex.dragSplitter", "book-body", "book-pane-source", SplitMin, BookThemes.IsRtl(language));
            await SaveSplitAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
        }
    }

    /// <summary>Left and Right move the splitter by 24 px (the other way round on a right-to-left page); Home or Escape-less double click resets.</summary>
    internal async Task SplitKeyAsync(KeyboardEventArgs e)
    {
        var rtl = BookThemes.IsRtl(language);
        var grow = e.Key switch { "ArrowRight" => !rtl, "ArrowLeft" => rtl, _ => (bool?)null };
        if (grow == null) return;
        try
        {
            var width = splitWidth > 0 ? splitWidth : await JS.InvokeAsync<int>("codex.elementWidth", "book-pane-source");
            splitWidth = Math.Max(SplitMin, width + (grow.Value ? SplitStep : -SplitStep));
            await SaveSplitAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
        }
    }

    internal Task SplitResetAsync()
    {
        splitWidth = 0;
        return SaveSplitAsync();
    }

    private async Task SaveSplitAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", SplitKey, splitWidth.ToString(CultureInfo.InvariantCulture));
        }
        catch (JSException)
        {
        }
    }

    internal (string Tab, string Icon, string Label)[] PanelTabs =>
    [
        ("ai", "sparkles", L["Campaign.Book.Draft.Ai.Title"]),
        ("art", "image", L["Campaign.Book.Tab.Art"]),
        ("problems", "alert-triangle", L["Campaign.Book.Draft.Problems"])
    ];

    /// <summary>What the Problems tab lists: blocks too tall and breaks not placed (Fit pages), pages printed cut off, web addresses not fetched, and
    /// what only the GM edition should have, found in the party draft before it was printed.</summary>
    internal int ProblemCount =>
        (fitReport?.Overflow.Count ?? 0) + (fitReport?.Unplaced.Count ?? 0) + (printReport != null ? 1 : 0) + (webReport?.Failed.Count ?? 0) + (leakReport?.Total ?? 0) + letters.Count + leftOut.Count
        + KeptCount;

    internal int KeptCount => aiKept.Sum(k => k.Parts.Count);

    /// <summary>The entries the last entry-by-entry AI edit left as they were, with their chapter.</summary>
    internal sealed record AiKeptParts(string Section, IReadOnlyList<BookEditKept> Parts);

    internal readonly List<AiKeptParts> aiKept = [];

    internal string KeptReason(string reason) => reason switch
    {
        "checks" => L["Campaign.Book.Draft.Ai.KeptReason.checks"],
        "cutoff" => L["Campaign.Book.Draft.Ai.KeptReason.cutoff"],
        _ => L["Campaign.Book.Draft.Ai.KeptReason.other"]
    };

    internal string LeakLabel(BookLeak hit) => hit.Kind switch
    {
        BookLeakKind.Secret when hit.From is { Length: > 0 } from => L["Campaign.Book.Draft.Leak.SecretIn", from],
        BookLeakKind.Secret => L["Campaign.Book.Draft.Leak.Secret"],
        BookLeakKind.HiddenName => L["Campaign.Book.Draft.Leak.HiddenName"],
        BookLeakKind.RealName => L["Campaign.Book.Draft.Leak.RealName", hit.From ?? string.Empty],
        _ => L["Campaign.Book.Draft.Leak.GmOnly"]
    };

    // A secret can run for paragraphs: its first words, on one line.
    internal static string Snippet(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= 140 ? line : line[..140].TrimEnd() + "…";
    }

    internal async Task ReadLayoutAsync()
    {
        if (layoutRead) return;
        layoutRead = true;
        try
        {
            assistOn = await JS.InvokeAsync<string?>("localStorage.getItem", AssistKey) != "0";
            savedLanguage = await JS.InvokeAsync<string?>("localStorage.getItem", LanguageKey);
            if (int.TryParse(await JS.InvokeAsync<string?>("localStorage.getItem", SplitKey), NumberStyles.None, CultureInfo.InvariantCulture, out var split) && split >= SplitMin)
            {
                splitWidth = split;
            }

            var saved = await JS.InvokeAsync<string?>("localStorage.getItem", LayoutKey);
            if (saved?.Split('|') is [var chapters, var preview, var panel, var tab])
            {
                chaptersOpen = chapters != "0";
                previewOpen = preview != "0";
                panelOpen = panel != "0";
                if (tab is "ai" or "art" or "problems") panelTab = tab;
            }
        }
        catch (JSException)
        {
            // Storage refused (a private window): the default layout.
        }
    }

    internal async Task SaveLayoutAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", LayoutKey, $"{(chaptersOpen ? 1 : 0)}|{(previewOpen ? 1 : 0)}|{(panelOpen ? 1 : 0)}|{panelTab}");
        }
        catch (JSException)
        {
        }
    }

    internal Task TogglePaneAsync(Pane pane)
    {
        if (pane == Pane.Chapters) chaptersOpen = !chaptersOpen;
        else previewOpen = !previewOpen;
        return SaveLayoutAsync();
    }

    internal Task TogglePanelAsync()
    {
        panelOpen = !panelOpen;
        return SaveLayoutAsync();
    }

    internal Task ShowTabAsync(string tab)
    {
        // A second click on the open tab folds the panel, as an IDE's does.
        if (panelOpen && panelTab == tab) panelOpen = false;
        else (panelOpen, panelTab) = (true, tab);
        return SaveLayoutAsync();
    }

    /// <summary>Opens the panel on a tab because something there needs the GM (a report, the review); not remembered as their choice.</summary>
    internal void Reveal(string tab) => (panelOpen, panelTab) = (true, tab);

    // Left and right move between the panel's tabs, Home and End to the first and last.
    internal async Task PanelKeyAsync(KeyboardEventArgs e)
    {
        var tabs = PanelTabs.Select(t => t.Tab).ToList();
        var at = tabs.IndexOf(panelTab);
        var next = e.Key switch
        {
            "ArrowRight" => (at + 1) % tabs.Count,
            "ArrowLeft" => (at + tabs.Count - 1) % tabs.Count,
            "Home" => 0,
            "End" => tabs.Count - 1,
            _ => -1
        };
        if (next < 0) return;
        (panelOpen, panelTab) = (true, tabs[next]);
        await SaveLayoutAsync();
        await JS.InvokeVoidAsync("codex.focusById", "book-draft-tab-" + panelTab);
    }

    // AI edits: what is about to be sent (after the pre-flight), the call under way, the review open, and what the last one said.
    internal sealed record EditorSelection(string Section, int From, int To, string Text);

    internal sealed record AiPending(EditorSelection Selection, int Version, string Ask)
    {
        public bool Whole => Selection.From == Selection.To;
    }

    internal string aiText = string.Empty;
    internal AiPending? aiPending;
    internal BookEditPreview? aiPreview;
    internal bool aiAsking;
    internal bool reviewing;
    internal CancellationTokenSource? aiStop;
    internal string? aiNote;
    internal bool aiNoteError;

    internal bool AiBusy => aiAsking || reviewing || aiPending != null || mergePreview != null || mergeSection != null || shapePreview != null || shapeRun != null;

    /// <summary>What this edition's AI edits and merges have cost over all their runs (docs/BOOK_EXPORT.md 42, step 2), for the Ask AI tab.</summary>
    internal BookBuildTotals? spendTotals;

    private async Task ReadSpendAsync() => spendTotals = await BookSpend.ReadAsync(Builds, Access, edition);

    internal string? SpendLine => spendTotals is not { } t ? null
        : L["Campaign.Book.Ai.Totals", t.Calls, Number(t.InputTokens), Number(t.OutputTokens)]
          + (t.Estimated ? " " + L["Campaign.Book.Ai.Estimated"] : string.Empty)
          + (t.Cost <= 0 ? string.Empty
              : " " + L[t.UnpricedRuns == 0 ? "Campaign.Book.Ai.TotalsCost" : "Campaign.Book.Ai.TotalsCostPartial", Codex.Core.AI.AiPrice.Format(t.Cost, t.PriceLabel, System.Globalization.CultureInfo.CurrentCulture)]);

    internal static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    internal static string Money(decimal amount, Codex.Core.AI.AiPriceQuote? price) =>
        Codex.Core.AI.AiPrice.Format(amount, price?.Label ?? string.Empty, System.Globalization.CultureInfo.CurrentCulture);

    internal void Note(string text, bool error = true)
    {
        aiNote = text;
        aiNoteError = error;
    }

    internal string Outcome(BookEditOutcome outcome) => outcome switch
    {
        BookEditOutcome.Stale => L["Campaign.Book.Draft.Ai.Stale"],
        BookEditOutcome.Empty => L["Campaign.Book.Draft.Ai.Empty"],
        BookEditOutcome.TooLong => L["Campaign.Book.Draft.Ai.TooLong"],
        BookEditOutcome.Busy => L["Campaign.Book.Draft.Ai.Busy"],
        BookEditOutcome.NoModel => L["Campaign.Book.Draft.Ai.NoModel"],
        _ => L["Campaign.Book.Draft.NotAllowed"]
    };

    internal async Task AiLockAsync(bool on)
    {
        if (module != null && editorId != null) await module.InvokeVoidAsync("lock", editorId, on);
    }

    internal Task AiKeyAsync(KeyboardEventArgs e) => e.Key == "Enter" ? AiPrepareAsync(null) : Task.CompletedTask;

    /// <summary>The pre-flight: what was typed is saved, the selection read, and the server says what it would send (the selection widened to whole
    /// pieces) and cost. Nothing is sent; the editor is locked so the text stays what the server checked.</summary>
    internal async Task AiPrepareAsync(string? preset)
    {
        if (working || AiBusy || view == null || module == null || editorId == null || current == null) return;
        aiNote = null;
        message = null;
        var ask = preset ?? aiText.Trim();
        if (ask.Length == 0)
        {
            Note(L["Campaign.Book.Draft.Ai.NoAsk"]);
            return;
        }

        working = true;
        try
        {
            await FlushAsync();
            if (state == SaveState.Conflict) return;
            var selection = await module.InvokeAsync<EditorSelection?>("selection", editorId);
            if (selection == null || view?.Chapters.FirstOrDefault(c => c.Section == selection.Section) is not { } chapter) return;
            var preview = await Ai.PreviewAsync(Access, edition, language, selection.Section, chapter.Version, selection.From, selection.To, selection.Text, ask);
            if (preview.Outcome != BookEditOutcome.Ready)
            {
                Note(Outcome(preview.Outcome));
                return;
            }

            await AiLockAsync(true);
            Reveal("ai");
            aiPending = new AiPending(selection, chapter.Version, ask);
            aiPreview = preview;
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Reading the editor's selection failed");
            Note(L["Campaign.Book.Draft.ActionFailed"]);
        }
        finally
        {
            working = false;
        }
    }

    internal async Task AiCancelAsync()
    {
        aiPending = null;
        aiPreview = null;
        await AiLockAsync(false);
    }

    /// <summary>Sends the request and waits for the reply; a reply that passed the checks opens the review. The editor stays locked until the review ends.</summary>
    internal async Task<BookEditProposal?> AiSendAsync()
    {
        if (aiPending is not { } p || aiAsking || module == null || editorId == null) return null;
        BookEditProposal? sent = null;
        aiPreview = null;
        aiAsking = true;
        working = true;
        aiNote = null;
        aiStop = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        try
        {
            var proposal = await Ai.AskAsync(Access, edition, language, p.Selection.Section, p.Version, p.Selection.From, p.Selection.To, p.Selection.Text, p.Ask, aiStop.Token);
            sent = proposal;
            if (proposal.Tokens > 0) await ReadSpendAsync();
            // A whole-book run gathers every chapter's; an edit of its own starts the list again.
            if (shapeRun == null) aiKept.Clear();
            aiKept.RemoveAll(k => k.Section == p.Selection.Section);
            if (proposal.Kept is { Count: > 0 } kept) aiKept.Add(new AiKeptParts(p.Selection.Section, kept));
            var spent = proposal.Tokens == 0 ? null
                : proposal.Cost is { } cost ? L["Campaign.Book.Draft.Ai.SpentCost", Number(proposal.Tokens), Money(cost, null)].ToString()
                : L["Campaign.Book.Draft.Ai.Spent", Number(proposal.Tokens)].ToString();
            if (proposal.Outcome == BookEditOutcome.Failed)
            {
                Note(L["Campaign.Book.Draft.Ai.Failed." + (proposal.Reason is "checks" or "cutoff" or "budget" or "auth" or "ratelimit" or "allowance" or "cancelled" ? proposal.Reason : "error")]
                     + (spent == null ? string.Empty : " " + spent));
            }
            else if (proposal.Outcome != BookEditOutcome.Ready || proposal.Replacement == null)
            {
                Note(Outcome(proposal.Outcome));
            }
            else if (proposal.Replacement == proposal.Original)
            {
                Note(L["Campaign.Book.Draft.Ai.Same"] + (spent == null ? string.Empty : " " + spent), error: false);
            }
            else if (await module.InvokeAsync<bool>("review", editorId,
                         new { section = p.Selection.Section, from = proposal.From, to = proposal.To, original = proposal.Original, replacement = proposal.Replacement },
                         new
                         {
                             accept = L["Campaign.Book.Draft.Ai.Accept"].ToString(), reject = L["Campaign.Book.Draft.Ai.Reject"].ToString(),
                             left = L["Campaign.Book.Draft.Ai.Left"].ToString(), label = L["Campaign.Book.Draft.Ai.ReviewLabel"].ToString()
                         }))
            {
                reviewing = true;
                var keptNote = proposal.Kept is not { Count: > 0 } ? null : L["Campaign.Book.Draft.Ai.KeptPartsNote", proposal.Kept.Count].ToString();
                if (spent != null || keptNote != null) Note(string.Join(" ", new[] { keptNote, spent }.OfType<string>()), error: keptNote != null);
            }
            else
            {
                Note(L["Campaign.Book.Draft.Ai.Stale"]);
            }
        }
        catch (LocalizedException ex)
        {
            // Refused before anything was sent: the server's allowance, or no model.
            Note(L.Describe(ex));
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Showing the AI edit failed");
            Note(L["Campaign.Book.Draft.ActionFailed"]);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            aiAsking = false;
            aiPending = null;
            aiStop?.Dispose();
            aiStop = null;
            // A review open counts as work under way: the theme, Fit pages and Regenerate would redraw the chapter under it.
            working = reviewing;
            if (!reviewing) await AiLockAsync(false);
        }

        return sent;
    }

    /// <summary>Ends the review: what the GM kept goes into the chapter as one change, and is saved like typing.</summary>
    internal async Task AiFinishAsync(string how)
    {
        if (!reviewing || module == null || editorId == null) return;
        if (mergeKey != null)
        {
            await MergeFinishAsync(how);
            return;
        }

        reviewing = false;
        working = false;
        try
        {
            var changed = await module.InvokeAsync<bool>("finish", editorId, how);
            if (changed) await FlushAsync();
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Ending the AI review failed");
            Note(L["Campaign.Book.Draft.ActionFailed"]);
            shapeRun = null;
        }

        if (shapeRun != null) await ShapeBookNextAsync();
    }

    // "As the book" over the whole book (docs/BOOK_EXPORT.md 42, step 3): the cost of all of it shown first; then one chapter at a time, entry
    // by entry, each reviewed before the next is sent, so a closed page loses one chapter's reply at most and nothing is saved unreviewed.
    internal sealed class ShapeRun(string ask, IReadOnlyList<string> sections)
    {
        public string Ask { get; } = ask;
        public IReadOnlyList<string> Sections { get; } = sections;
        public int Next { get; set; }
    }

    internal const string ShapeAsk = "shape";
    internal BookShapePreview? shapePreview;
    internal ShapeRun? shapeRun;

    internal async Task ShapeBookPrepareAsync()
    {
        moreOpen = false;
        if (working || AiBusy || view == null) return;
        aiNote = null;
        message = null;
        working = true;
        try
        {
            await FlushAsync();
            if (state == SaveState.Conflict) return;
            var preview = await Ai.BookPreviewAsync(Access, edition, language, ShapeAsk);
            Reveal("ai");
            if (preview.Outcome != BookEditOutcome.Ready) Note(Outcome(preview.Outcome));
            else shapePreview = preview;
        }
        finally
        {
            working = false;
        }
    }

    internal void ShapeBookCancel() => shapePreview = null;

    /// <summary>Stops the run after the chapter under review: what is in the review stays the GM's to keep or discard.</summary>
    internal void ShapeBookStop()
    {
        if (shapeRun == null) return;
        shapeRun = null;
        Note(L["Campaign.Book.Draft.ShapeBook.Stopped"], error: false);
    }

    internal async Task ShapeBookStartAsync()
    {
        if (shapePreview is not { } book) return;
        shapePreview = null;
        aiKept.Clear();
        shapeRun = new ShapeRun(ShapeAsk, book.Sections);
        await ShapeBookNextAsync();
    }

    internal async Task ShapeBookNextAsync()
    {
        while (shapeRun is { } run && run.Next < run.Sections.Count)
        {
            var section = run.Sections[run.Next++];
            if (view?.Chapters.FirstOrDefault(c => c.Section == section) is not { } chapter) continue;
            await ShowAsync(section);
            if (state == SaveState.Conflict) break;
            await AiLockAsync(true);
            aiPending = new AiPending(new EditorSelection(section, 0, 0, string.Empty), chapter.Version, run.Ask);
            StateHasChanged();
            var proposal = await AiSendAsync();
            if (reviewing) return;
            // What would fail the same way for the next chapter ends the run: the key refused, the money or allowance gone, a stop, no model.
            if (proposal == null || proposal.Outcome is BookEditOutcome.NoModel or BookEditOutcome.Busy or BookEditOutcome.NotAllowed
                || proposal.Reason is "auth" or "ratelimit" or "allowance" or "budget" or "cancelled")
            {
                shapeRun = null;
                return;
            }
        }

        if (shapeRun != null)
        {
            shapeRun = null;
            Note(L["Campaign.Book.Draft.ShapeBook.Done"] + (KeptCount > 0 ? " " + L["Campaign.Book.Draft.Ai.KeptPartsNote", KeptCount] : string.Empty), error: KeptCount > 0);
        }
    }

    // "Bring up to date" (docs/BOOK_EXPORT.md 41.18): the pre-flight, the run, and the review, which is kept through the server, not typed in.
    internal BookMergePreview? mergePreview;
    internal string? mergeSection;
    internal int mergeVersion;
    internal string? mergeKey;
    internal bool mergeHadBreaks;

    internal string MergeOutcome(BookMergeOutcome outcome) => outcome switch
    {
        BookMergeOutcome.UpToDate => L["Campaign.Book.Draft.Merge.UpToDate"],
        BookMergeOutcome.Duplicate => L["Campaign.Book.Draft.Merge.Duplicate"],
        BookMergeOutcome.Stale => L["Campaign.Book.Draft.Ai.Stale"],
        BookMergeOutcome.Busy => L["Campaign.Book.Draft.Ai.Busy"],
        _ => L["Campaign.Book.Draft.NotAllowed"]
    };

    /// <summary>What was typed is saved, and the server says what the merge would do. With no conflicts nothing could be sent, so the review
    /// opens at once; otherwise the pre-flight waits on the Markdown with the cost, and the chapter is locked so it stays what was merged.</summary>
    internal async Task MergePrepareAsync(string section)
    {
        if (working || AiBusy || module == null || editorId == null) return;
        aiNote = null;
        message = null;
        working = true;
        var direct = false;
        try
        {
            await FlushAsync();
            if (state == SaveState.Conflict) return;
            if (view?.Chapters.FirstOrDefault(c => c.Section == section) is not { } chapter) return;
            var preview = await Merges.PreviewAsync(Access, edition, language, section, chapter.Version);
            if (preview.Outcome != BookMergeOutcome.Ready)
            {
                Reveal("ai");
                Note(MergeOutcome(preview.Outcome));
                return;
            }

            if (current != section) await ShowAsync(section);
            mergeSection = section;
            mergeVersion = chapter.Version;
            await AiLockAsync(true);
            if (preview.Conflicts == 0) direct = true;
            else mergePreview = preview;
        }
        finally
        {
            working = false;
        }

        if (direct) await MergeRunAsync(false);
    }

    internal async Task MergeCancelAsync()
    {
        mergePreview = null;
        mergeSection = null;
        await AiLockAsync(false);
    }

    /// <summary>Makes the merge (the conflicts sent to the model only with <paramref name="useAi"/>) and opens it for review against the chapter.</summary>
    internal async Task MergeRunAsync(bool useAi)
    {
        if (mergeSection is not { } section || module == null || editorId == null || aiAsking) return;
        mergePreview = null;
        aiAsking = useAi;
        working = true;
        aiNote = null;
        aiStop = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        try
        {
            var proposal = await Merges.MergeAsync(Access, edition, language, section, mergeVersion, useAi, aiStop.Token);
            if (proposal.Tokens > 0) await ReadSpendAsync();
            var spent = proposal.Tokens == 0 ? null
                : proposal.Cost is { } cost ? L["Campaign.Book.Draft.Ai.SpentCost", Number(proposal.Tokens), Money(cost, null)].ToString()
                : L["Campaign.Book.Draft.Ai.Spent", Number(proposal.Tokens)].ToString();
            var said = proposal.Conflicts == 0 ? null : L["Campaign.Book.Draft.Merge.Ready", proposal.Merged, proposal.Fallback].ToString();
            if (proposal.Fallback > 0 && proposal.Reason is "auth" or "ratelimit" or "allowance" or "budget" or "cancelled") said += " " + L["Campaign.Book.Draft.Ai.Failed." + proposal.Reason];
            var words = string.Join(" ", new[] { said, spent }.Where(w => !string.IsNullOrEmpty(w)));
            if (proposal.Outcome != BookMergeOutcome.Ready || proposal.Replacement == null)
            {
                Note(MergeOutcome(proposal.Outcome));
            }
            else if (proposal.Replacement == proposal.Original)
            {
                // Nothing in the GM's text changes: the chapter's base moves on, and the notice goes.
                var (result, _) = await Merges.SaveAsync(Access, edition, language, section, mergeVersion, proposal.Original, proposal.Key);
                MergeSaved(result, L["Campaign.Book.Draft.Merge.Same"]);
                await RefreshAsync(push: true, section);
            }
            else if (await module.InvokeAsync<bool>("review", editorId,
                         new { section, from = 0, to = proposal.Original.Length, original = proposal.Original, replacement = proposal.Replacement },
                         new
                         {
                             accept = L["Campaign.Book.Draft.Ai.Accept"].ToString(), reject = L["Campaign.Book.Draft.Ai.Reject"].ToString(),
                             left = L["Campaign.Book.Draft.Ai.Left"].ToString(), label = L["Campaign.Book.Draft.Merge.Review"].ToString()
                         }))
            {
                reviewing = true;
                mergeKey = proposal.Key;
                mergeHadBreaks = proposal.Original.ReplaceLineEndings("\n").Split('\n').Any(BookDraftText.IsFitted);
                if (words.Length > 0) Note(words, error: false);
            }
            else
            {
                Note(L["Campaign.Book.Draft.Ai.Stale"]);
            }
        }
        catch (LocalizedException ex)
        {
            Note(L.Describe(ex));
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Showing the merge failed");
            Note(L["Campaign.Book.Draft.ActionFailed"]);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            aiAsking = false;
            aiStop?.Dispose();
            aiStop = null;
            working = reviewing;
            if (!reviewing)
            {
                mergeSection = null;
                await AiLockAsync(false);
            }
        }
    }

    internal void MergeSaved(BookMergeSave result, string done)
    {
        Reveal("ai");
        switch (result)
        {
            case BookMergeSave.Saved:
                Note(done, error: false);
                break;
            case BookMergeSave.Moved:
                Note(L["Campaign.Book.Draft.Merge.Moved"]);
                break;
            case BookMergeSave.Conflict:
                state = SaveState.Conflict;
                break;
            default:
                Note(L["Campaign.Book.Draft.ActionFailed"]);
                break;
        }
    }

    /// <summary>Ends a merge's review: what the GM kept is saved by the server together with the chapter's new base (Undo takes both back), and
    /// then put in the editor. Discard saves nothing, and the notice stays.</summary>
    internal async Task MergeFinishAsync(string how)
    {
        reviewing = false;
        var key = mergeKey!;
        var section = mergeSection!;
        mergeKey = null;
        try
        {
            var text = await module!.InvokeAsync<string?>("take", editorId, how);
            if (text != null)
            {
                var (result, _) = await Merges.SaveAsync(Access, edition, language, section, mergeVersion, text, key);
                MergeSaved(result, L["Campaign.Book.Draft.Merge.Saved"] + (mergeHadBreaks ? " " + L["Campaign.Book.Draft.Merge.FitAgain"] : string.Empty));
                await RefreshAsync(push: true, section);
            }
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Ending the merge's review failed");
            Note(L["Campaign.Book.Draft.ActionFailed"]);
        }
        finally
        {
            mergeSection = null;
            working = false;
            await AiLockAsync(false);
        }
    }

    // Leaving the page (a link, Back, the other edition): what was typed is saved first. Text that could not be saved (the draft changed in
    // another tab) is not thrown away without a word: the GM is asked, and stays or leaves.
    internal string? leaveAsked;
    private bool leaveConfirmed;

    /// <summary>Text the server has not got: typed and not yet saved, or refused.</summary>
    internal bool Unsaved => view != null && (dirty.Count > 0 || cssDirty || state is SaveState.Conflict or SaveState.Failed);

    internal async Task LeavingAsync(LocationChangingContext context)
    {
        if (leaveConfirmed) return;
        await SettleAsync();
        if (Unsaved)
        {
            context.PreventNavigation();
            leaveAsked = context.TargetLocation;
        }
    }

    /// <summary>An open review or pre-flight is let go of (nothing of it was saved), and what was typed is saved.</summary>
    private async Task SettleAsync()
    {
        aiStop?.Cancel();
        if (reviewing) await AiFinishAsync("none");
        if (aiPending != null) await AiCancelAsync();
        if (mergePreview != null) await MergeCancelAsync();
        await FlushAsync();
    }

    internal void LeaveStay() => leaveAsked = null;

    internal void LeaveAnyway()
    {
        var to = leaveAsked;
        leaveAsked = null;
        if (to == null) return;
        leaveConfirmed = true;
        Nav.NavigateTo(to);
    }

    /// <summary>The other edition: the address changes, and the draft of that edition opens.</summary>
    internal async Task SwitchEditionAsync(string to)
    {
        if (to == edition || working || AiBusy) return;
        await FlushAsync();
        var target = $"{BasePath}/book/{(to == "dm" ? "gm" : "party")}";
        if (Unsaved) leaveAsked = target;
        else Nav.NavigateTo(target);
    }

    /// <summary>The address names the chapter shown, so a reload or a link comes back to it (the page itself is not left).</summary>
    private async Task SyncUrlAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("codex.replaceUrl", $"{BasePath}/book/{(edition == "dm" ? "gm" : "party")}/{Uri.EscapeDataString(current ?? StyleRoute)}");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
        }
    }

    internal void Refresh() => _ = InvokeAsync(StateHasChanged);

    internal int renders;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module != null)
            {
                if (editorId != null) await module.InvokeVoidAsync("close", editorId);
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }

        self?.Dispose();
        closing.Cancel();
        closing.Dispose();
    }
}
