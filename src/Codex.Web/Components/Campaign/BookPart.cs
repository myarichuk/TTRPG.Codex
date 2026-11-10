using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Codex.Web.Components.Campaign;

/// <summary>
/// One part of the Book page (toolbar, outline, source, preview, dock). The state and what the page does live in <see cref="BookPage"/>, which
/// every part reads and calls through <see cref="P"/>; the members below only name what the parts' markup uses. A part has no state of its own:
/// an event in one re-draws the whole page (the parts are drawn together), as the single editor did before it was split.
/// </summary>
public abstract class BookPart : ComponentBase, IHandleEvent
{
    [CascadingParameter] public BookPage P { get; set; } = null!;

    /// <summary>Changes on every draw of the page: a part with no parameters of its own would otherwise never be drawn again.</summary>
    [Parameter] public int Stamp { get; set; }

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        var task = callback.InvokeAsync(arg);
        P.Refresh();
        return task.Status is TaskStatus.RanToCompletion or TaskStatus.Canceled ? Task.CompletedTask : Settled(task);
    }

    private async Task Settled(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
        }

        P.Refresh();
    }

    // ---- State ----
    internal CampaignAccess Access => P.Access;
    internal BookDraftView? view => P.view;
    internal string edition => P.edition;
    internal string language => P.language;
    internal string? current => P.current;
    internal BookDraftChapterView? Current => P.Current;
    internal bool working => P.working;
    internal bool printing => P.printing;
    internal bool fitting => P.fitting;
    internal BookPage.SaveState state => P.state;
    internal string? message => P.message;
    internal string SaveText => P.SaveText;
    internal string EditionName => P.EditionName;
    internal bool AiBusy => P.AiBusy;
    internal bool EditorUp => P.EditorUp;
    internal int ProblemCount => P.ProblemCount;
    internal int KeptCount => P.KeptCount;
    internal IReadOnlyList<BookThemeDocument> customThemes => P.customThemes;
    internal IReadOnlyList<BookPlayer> players => P.players;
    internal bool moreOpen { get => P.moreOpen; set => P.moreOpen = value; }
    internal bool wizardOpen { get => P.wizardOpen; set => P.wizardOpen = value; }
    internal bool assistOn => P.assistOn;
    internal bool chaptersOpen => P.chaptersOpen;
    internal bool previewOpen => P.previewOpen;
    internal bool panelOpen => P.panelOpen;
    internal string panelTab => P.panelTab;
    internal (string Tab, string Icon, string Label)[] PanelTabs => P.PanelTabs;
    internal string mobilePane => P.mobilePane;

    // The source pane's own elements, which the page's script (the editor and its preview) is opened on.
    internal ElementReference host { get => P.host; set => P.host = value; }
    internal ElementReference frame { get => P.frame; set => P.frame = value; }

    // The web addresses of the stylesheet.
    internal int fetching => P.fetching;
    internal BookWebReport? webReport => P.webReport;

    // The AI request, its review, the whole-book run and the merge.
    internal string aiText { get => P.aiText; set => P.aiText = value; }
    internal BookPage.AiPending? aiPending => P.aiPending;
    internal BookEditPreview? aiPreview => P.aiPreview;
    internal bool aiAsking => P.aiAsking;
    internal bool reviewing => P.reviewing;
    internal CancellationTokenSource? aiStop => P.aiStop;
    internal string? aiNote => P.aiNote;
    internal bool aiNoteError => P.aiNoteError;
    internal BookShapePreview? shapePreview => P.shapePreview;
    internal BookPage.ShapeRun? shapeRun => P.shapeRun;
    internal BookMergePreview? mergePreview => P.mergePreview;
    internal string? mergeSection => P.mergeSection;
    internal string? mergeKey => P.mergeKey;

    // The reports.
    internal BookDraftFitReport? fitReport => P.fitReport;
    internal string? printReport => P.printReport;
    internal BookLeakReport? leakReport => P.leakReport;
    internal IReadOnlyList<(string Section, string Characters, int More)> letters => P.letters;
    internal IReadOnlyList<BookDraftLeftOut> leftOut => P.leftOut;
    internal IReadOnlyList<BookPage.AiKeptParts> aiKept => P.aiKept;
    internal bool leaksForMarkdown => P.leaksForMarkdown;
    internal string? leaksForPlayer => P.leaksForPlayer;

    // ---- What the page does ----
    internal string Title(BookDraftChapterView chapter) => P.Title(chapter);
    internal string TitleOf(string? section) => P.TitleOf(section);
    internal string Item(BookOverflow o) => P.Item(o);
    internal string Megabytes(long bytes) => P.Megabytes(bytes);
    internal string Problem(BookWebFailure failure) => P.Problem(failure);
    internal string KeptReason(string reason) => P.KeptReason(reason);
    internal string LeakLabel(BookLeak hit) => P.LeakLabel(hit);
    internal static string Snippet(string text) => BookPage.Snippet(text);
    internal static string Number(long value) => BookPage.Number(value);
    internal static string Money(decimal amount, Codex.Core.AI.AiPriceQuote? price) => BookPage.Money(amount, price);

    internal Task ShowAsync(string? section) => P.ShowAsync(section);
    internal Task ThemeAsync(ChangeEventArgs e) => P.ThemeAsync(e);
    internal Task HomebreweryAsync(ChangeEventArgs e) => P.HomebreweryAsync(e);
    internal Task FitAsync() => P.FitAsync();
    internal Task DownloadAsync(bool anyway, bool markdown = false, string? player = null) => P.DownloadAsync(anyway, markdown, player);
    internal Task ToggleMoreAsync() => P.ToggleMoreAsync();
    internal Task EdgeAsync() => P.EdgeAsync();
    internal Task PaperAsync(string size) => P.PaperAsync(size);
    internal Task LanguageAsync(string tag) => P.LanguageAsync(tag);
    internal Task AssistToggleAsync() => P.AssistToggleAsync();
    internal Task MarkdownAsync() => P.MarkdownAsync();
    internal Task PlayerDownloadAsync(BookPlayer player) => P.PlayerDownloadAsync(player);
    internal Task ShapeBookPrepareAsync() => P.ShapeBookPrepareAsync();
    internal Task StartOverAsync() => P.StartOverAsync();
    internal Task SwitchEditionAsync(string to) => P.SwitchEditionAsync(to);
    internal Task TogglePaneAsync(BookPage.Pane pane) => P.TogglePaneAsync(pane);
    internal Task TogglePanelAsync() => P.TogglePanelAsync();
    internal Task ShowTabAsync(string tab) => P.ShowTabAsync(tab);
    internal Task PanelKeyAsync(KeyboardEventArgs e) => P.PanelKeyAsync(e);
    internal Task UndoAsync(string section) => P.UndoAsync(section);
    internal Task RegenerateAsync(string section) => P.RegenerateAsync(section);
    internal Task KeepAsync(string section) => P.KeepAsync(section);
    internal Task MergePrepareAsync(string section) => P.MergePrepareAsync(section);
    internal Task MergeRunAsync(bool useAi) => P.MergeRunAsync(useAi);
    internal Task MergeCancelAsync() => P.MergeCancelAsync();
    internal Task AiPrepareAsync(string? preset) => P.AiPrepareAsync(preset);
    internal Task AiKeyAsync(KeyboardEventArgs e) => P.AiKeyAsync(e);
    internal Task<BookEditProposal?> AiSendAsync() => P.AiSendAsync();
    internal Task AiCancelAsync() => P.AiCancelAsync();
    internal Task AiFinishAsync(string how) => P.AiFinishAsync(how);
    internal Task ShapeBookStartAsync() => P.ShapeBookStartAsync();
    internal void ShapeBookCancel() => P.ShapeBookCancel();
    internal void ShapeBookStop() => P.ShapeBookStop();
    internal Task ArtChangedAsync() => P.ArtChangedAsync();
}
