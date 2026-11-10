using System.Globalization;
using Codex.Core.Book;
using Codex.Persistence;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Codex.Web.Components.Campaign;

// The Source pane's tabs, the snippet bar, the outline tree and the GM's own chapters (docs/BOOK_EXPORT.md 52; step 4 of the source-book editor).
public partial class BookPage
{
    /// <summary>Which tab the Source pane shows: "text" (the chapter), "style" (the GM's CSS) or "theme" (the theme's sheet, read-only). The chapter
    /// shown (<see cref="current"/>) is kept when the tab changes.</summary>
    internal string sourceTab = "text";

    internal bool followPreview = true;
    internal string zoom = "fit";
    internal const string FollowKey = "codex.bookEditor.follow";
    internal const string ZoomKey = "codex.bookEditor.zoom";

    internal static readonly string[] SourceTabs = ["text", "style", "theme"];

    /// <summary>A heading of a chapter as the editor script read it: its level (2 or 3), its words (plain, cut short) and its place in the chapter's
    /// list of headings. The words are text a player may have written: they are only ever drawn as text.</summary>
    public sealed record OutlineItem(int Level, string Text, int Index);

    internal readonly Dictionary<string, IReadOnlyList<OutlineItem>> outlines = [];
    internal readonly HashSet<string> opened = [];

    private const int MaxOutline = 150;
    private const int MaxOutlineText = 120;

    /// <summary>The editor script tells the server a chapter's headings when they changed (never on every key).</summary>
    [JSInvokable]
    public Task Outline(string section, List<OutlineItem>? items)
    {
        if (view?.Chapters.All(c => c.Section != section) != false) return Task.CompletedTask;
        outlines[section] = (items ?? [])
            .Where(i => i.Level is 2 or 3)
            .Take(MaxOutline)
            .Select(i => new OutlineItem(i.Level, (i.Text ?? string.Empty).Length > MaxOutlineText ? i.Text![..MaxOutlineText] : i.Text ?? string.Empty, Math.Max(0, i.Index)))
            .ToList();
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>A click in the preview named a chapter other than the one shown: it is opened.</summary>
    [JSInvokable]
    public async Task PreviewPicked(string section)
    {
        if (view?.Chapters.Any(c => c.Section == section) != true || AiBusy) return;
        if (current != section || sourceTab != "text") await ShowAsync(section);
    }

    internal async Task GoToHeadingAsync(string section, int index)
    {
        if (module == null || editorId == null || AiBusy) return;
        if (current != section || sourceTab != "text") await ShowAsync(section);
        await module.InvokeVoidAsync("goTo", editorId, section, index);
    }

    internal bool IsOpen(string section) => opened.Contains(section);

    internal void ToggleOpen(string section)
    {
        if (!opened.Add(section)) opened.Remove(section);
    }

    internal IReadOnlyList<OutlineItem> OutlineOf(string section) => outlines.GetValueOrDefault(section) ?? [];

    // ---- Tabs ----

    internal string SourceTabName(string tab) => tab switch
    {
        "style" => L["Campaign.Book.Source.Style"],
        "theme" => L["Campaign.Book.Source.Theme"],
        _ => L["Campaign.Book.Source.Text"]
    };

    // Left and right move between the tabs the way they are drawn (on a right-to-left page the next tab is to the left).
    internal async Task SourceKeyAsync(KeyboardEventArgs e)
    {
        var at = Array.IndexOf(SourceTabs, sourceTab);
        var forward = PageRtl ? "ArrowLeft" : "ArrowRight";
        var next = e.Key switch
        {
            "ArrowRight" or "ArrowLeft" when e.Key == forward => (at + 1) % SourceTabs.Length,
            "ArrowRight" or "ArrowLeft" => (at + SourceTabs.Length - 1) % SourceTabs.Length,
            "Home" => 0,
            "End" => SourceTabs.Length - 1,
            _ => -1
        };
        if (next < 0) return;
        await SourceTabAsync(SourceTabs[next]);
        await JS.InvokeVoidAsync("codex.focusById", "book-source-tab-" + sourceTab);
    }

    /// <summary>The theme's rules added at the end of the GM's own stylesheet; the Style tab is then shown with them.</summary>
    internal async Task CopyThemeAsync()
    {
        if (module == null || editorId == null || AiBusy || working) return;
        if (!await module.InvokeAsync<bool>("copyTheme", editorId)) return;
        await SourceTabAsync("style");
    }

    // ---- Snippets ----

    /// <summary>The snippets the bar offers, in order: the kind the script takes, an icon, and the words on the button. Secret is the GM edition's only.</summary>
    internal (string Kind, string Icon, string Label)[] Snippets =>
    [
        ("note", "sticky-note", L["Campaign.Book.Snippet.Note"]),
        ("aloud", "message-square-quote", L["Campaign.Book.Snippet.Aloud"]),
        .. edition == "dm" ? new[] { ("secret", "eye-off", L["Campaign.Book.Snippet.Secret"].ToString()) } : [],
        ("stat", "swords", L["Campaign.Book.Snippet.Stat"]),
        ("table", "table", L["Campaign.Book.Snippet.Table"]),
        ("picture", "image", L["Campaign.Book.Snippet.Picture"]),
        ("wide", "move-horizontal", L["Campaign.Book.Snippet.Wide"]),
        ("column", "columns-2", L["Campaign.Book.Snippet.Column"]),
        ("page", "file-plus", L["Campaign.Book.Snippet.Page"])
    ];

    internal async Task SnippetAsync(string kind)
    {
        if (module == null || editorId == null || AiBusy || working || sourceTab != "text" || state == SaveState.Conflict) return;
        await module.InvokeVoidAsync("snippet", editorId, kind);
    }

    /// <summary>The words a snippet puts in as placeholders, in the GM's language.</summary>
    internal Dictionary<string, string> SnippetWords() => new()
    {
        ["title"] = L["Campaign.Book.Snippet.W.Title"],
        ["text"] = L["Campaign.Book.Snippet.W.Text"],
        ["name"] = L["Campaign.Book.Snippet.W.Name"],
        ["type"] = L["Campaign.Book.Snippet.W.Type"],
        ["label"] = L["Campaign.Book.Snippet.W.Label"],
        ["value"] = L["Campaign.Book.Snippet.W.Value"],
        ["caption"] = L["Campaign.Book.Snippet.W.Caption"],
        ["heading"] = L["Campaign.Book.Snippet.W.Heading"],
        ["cell"] = L["Campaign.Book.Snippet.W.Cell"]
    };

    // ---- The preview's header: following the cursor and the zoom ----

    internal async Task FollowAsync()
    {
        followPreview = !followPreview;
        if (module != null && editorId != null) await module.InvokeVoidAsync("setFollow", editorId, followPreview);
        await RememberAsync(FollowKey, followPreview ? "1" : "0");
    }

    internal async Task ZoomAsync(string to)
    {
        zoom = to == "100" ? "100" : "fit";
        if (module != null && editorId != null) await module.InvokeVoidAsync("setZoom", editorId, zoom);
        await RememberAsync(ZoomKey, zoom);
    }

    private async Task RememberAsync(string key, string value)
    {
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", key, value);
        }
        catch (JSException)
        {
        }
    }

    private async Task ReadPreviewSettingsAsync()
    {
        try
        {
            followPreview = await JS.InvokeAsync<string?>("localStorage.getItem", FollowKey) != "0";
            zoom = await JS.InvokeAsync<string?>("localStorage.getItem", ZoomKey) == "100" ? "100" : "fit";
        }
        catch (JSException)
        {
        }
    }

    // ---- The GM's own chapters ----

    internal bool adding;
    internal string newTitle = string.Empty;
    internal string? renaming;
    internal string renameTitle = string.Empty;
    internal string? menuFor;
    internal string? deleteAsked;

    internal void StartAdding()
    {
        adding = true;
        newTitle = string.Empty;
        menuFor = null;
    }

    internal async Task AddKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await AddOwnChapterAsync();
        else if (e.Key == "Escape") adding = false;
    }

    /// <summary>A chapter of the GM's own, after the one shown. What was typed is saved first; the contents take the new chapter in.</summary>
    internal async Task AddOwnChapterAsync()
    {
        var title = newTitle.Trim();
        if (title.Length == 0 || working || AiBusy) return;
        working = true;
        message = null;
        try
        {
            await FlushAsync();
            var (result, section) = await Drafts.AddChapterAsync(Access, edition, language, title, current);
            if (result == BookDraftSave.Saved && section != null)
            {
                adding = false;
                newTitle = string.Empty;
                await ReloadViewAsync();
                opened.Add(section);
                await ShowSourceAsync(section, "text");
            }
            else
            {
                message = result == BookDraftSave.TooLarge ? L["Campaign.Book.Own.Full"] : L["Campaign.Book.Draft.ActionFailed"];
            }
        }
        finally
        {
            working = false;
        }
    }

    internal void StartRenaming(string section)
    {
        menuFor = null;
        renaming = section;
        renameTitle = view?.Chapters.FirstOrDefault(c => c.Section == section)?.Title ?? string.Empty;
    }

    internal async Task RenameKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await RenameAsync();
        else if (e.Key == "Escape") renaming = null;
    }

    internal async Task RenameAsync()
    {
        var section = renaming;
        var title = renameTitle.Trim();
        if (section == null || working || AiBusy) return;
        if (title.Length == 0)
        {
            renaming = null;
            return;
        }

        working = true;
        message = null;
        try
        {
            await FlushAsync();
            if (view?.Chapters.FirstOrDefault(c => c.Section == section) is not { } chapter) return;
            var (result, _) = await Drafts.RenameChapterAsync(Access, edition, language, section, title, chapter.Version);
            if (result == BookDraftSave.Conflict) state = SaveState.Conflict;
            else if (result != BookDraftSave.Saved) message = L["Campaign.Book.Draft.ActionFailed"];
            else await ReloadViewAsync();
            renaming = null;
        }
        finally
        {
            working = false;
        }
    }

    internal async Task MoveOwnAsync(string section, int by)
    {
        menuFor = null;
        if (working || AiBusy) return;
        working = true;
        message = null;
        try
        {
            await FlushAsync();
            var result = await Drafts.MoveChapterAsync(Access, edition, language, section, by);
            if (result != BookDraftSave.Saved) message = L["Campaign.Book.Draft.ActionFailed"];
            await ReloadViewAsync();
        }
        finally
        {
            working = false;
        }
    }

    internal void AskDelete(string section)
    {
        menuFor = null;
        deleteAsked = section;
    }

    internal async Task DeleteOwnAsync()
    {
        var section = deleteAsked;
        deleteAsked = null;
        if (section == null || working || AiBusy) return;
        working = true;
        message = null;
        try
        {
            await FlushAsync();
            var all = view?.Chapters.Select(c => c.Section).ToList() ?? [];
            var at = all.IndexOf(section);
            var result = await Drafts.DeleteChapterAsync(Access, edition, language, section);
            if (result != BookDraftSave.Saved)
            {
                message = L["Campaign.Book.Draft.ActionFailed"];
                return;
            }

            outlines.Remove(section);
            opened.Remove(section);
            await ReloadViewAsync();
            if (current == section || view?.Chapters.All(c => c.Section != current) == true)
            {
                // The chapter shown is gone: its neighbour is shown, else the first.
                var next = view!.Chapters.Select(c => c.Section).ElementAtOrDefault(Math.Max(0, at - 1)) ?? view.Chapters.FirstOrDefault()?.Section;
                await ShowSourceAsync(next, "text");
            }
        }
        finally
        {
            working = false;
        }
    }

    /// <summary>The draft read again after the chapters changed as a set: the editor takes the new list (and the text of the shown chapter, which the
    /// contents may have changed), and the titles the overflow line uses.</summary>
    private async Task ReloadViewAsync()
    {
        var fresh = await Drafts.OpenAsync(Access, edition, language, pageSize);
        if (fresh == null) return;
        view = fresh;
        if (module == null || editorId == null) return;
        await module.InvokeVoidAsync("chapters", editorId, fresh.Chapters.Select(c => new { section = c.Section, markdown = c.Markdown }));
        await module.InvokeVoidAsync("titles", editorId, fresh.Chapters.ToDictionary(c => c.Section, Title));
        if (current != null && fresh.Chapters.FirstOrDefault(c => c.Section == current) is { } shown)
        {
            await module.InvokeVoidAsync("replace", editorId, shown.Section, shown.Markdown);
        }
    }

    internal bool FirstOrLast(string section, int by)
    {
        var list = view?.Chapters.Select(c => c.Section).ToList() ?? [];
        var at = list.IndexOf(section);
        return by < 0 ? at <= 0 : at < 0 || at >= list.Count - 1;
    }
}
