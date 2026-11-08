using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>How an AI edit request went, before or after the model.</summary>
public enum BookEditOutcome
{
    /// <summary>The pre-flight: ready to send. After sending: a replacement to review.</summary>
    Ready,
    /// <summary>Not the caller's draft, or no such chapter.</summary>
    NotAllowed,
    /// <summary>The chapter changed since the editor read it (another tab, a save not yet made): nothing was sent.</summary>
    Stale,
    /// <summary>No request, or a passage with nothing the model may change.</summary>
    Empty,
    /// <summary>The passage or the request is longer than one edit takes.</summary>
    TooLong,
    /// <summary>Another paid book run of this GM's is under way.</summary>
    Busy,
    /// <summary>The GM has no model set up.</summary>
    NoModel,
    /// <summary>Sent, and no reply passed the checks, or the call failed: see the reason. Nothing changes.</summary>
    Failed
}

/// <summary>What an edit would send and cost. <paramref name="From"/> and <paramref name="To"/> are the selection widened to whole pieces.
/// <paramref name="Calls"/>: how many requests it is (a whole chapter asked with a preset goes entry by entry).</summary>
public sealed record BookEditPreview(BookEditOutcome Outcome, int From = 0, int To = 0, int Pieces = 0, BookPreflight? Preflight = null, BookModelInfo? Model = null,
    int Calls = 1);

/// <summary>A preset over the whole book, before anything is sent: the chapters it would go through, in order, and what they would send and cost
/// together.
/// <paramref name="TooLong"/>: chapters with an entry too long for one request, left out of the run (the GM can still select parts of them).</summary>
public sealed record BookShapePreview(BookEditOutcome Outcome, IReadOnlyList<string> Sections, int Calls = 0, BookPreflight? Preflight = null, BookModelInfo? Model = null)
{
    public IReadOnlyList<string> TooLong { get; init; } = [];
}

/// <summary>A part of a chapter an edit left as it was: its first heading (or the chapter's title, for the head) and why.</summary>
public sealed record BookEditKept(string Label, string Reason);

/// <summary>A replacement for the chapter's text between <paramref name="From"/> and <paramref name="To"/>, which was <paramref name="Original"/>
/// when the model was asked. Nothing is saved: the GM reviews it in the editor. <paramref name="Kept"/>: the entries of an entry-by-entry
/// edit whose reply failed, which keep their text.</summary>
public sealed record BookEditProposal(BookEditOutcome Outcome, int From = 0, int To = 0, string Original = "", string? Replacement = null, string? Reason = null,
    long Tokens = 0, decimal? Cost = null, IReadOnlyList<BookEditKept>? Kept = null);

/// <summary>
/// AI edits on a passage of a draft (docs/BOOK_EXPORT.md 41.4, phase 4). The passage is read from the draft as stored, at the version the editor
/// had, and must be the text the editor shows there; it is cut on the server (<see cref="BookDraftEdits.Cut"/>), so no secret, picture, marker
/// or break reaches the model whatever the page sends. The prompt takes the book's shape from the campaign's game system
/// (<see cref="AiVocabulary.BookShape"/>, or <see cref="AiVocabulary.PlayerBookShape"/> for the party edition). A request takes the per-GM
/// <see cref="BookGate"/> before anything is awaited, sets aside the server's requests up front, and is held to a cap worked out before it is sent.
/// <para>
/// A preset keeps the passage's facts, so its reply passes <see cref="BookFacts.Check"/> against this edition's names too. A preset on the whole
/// chapter goes entry by entry (docs/BOOK_EXPORT.md 42, step 2): the chapter's head and each entry (the merge's units) is one request, so a
/// book-length rewrite is never one reply that might be cut off, and a reply that fails costs its entry only, which keeps its text.
/// </para>
/// </summary>
public sealed class BookDraftAi(BookDrafts drafts, IBookModels models, BookGate gate, ISystemCatalog catalog, IBookBuildRepository? builds = null,
    ILogger<BookDraftAi>? log = null)
{
    /// <summary>One part of what is sent: where it is in the chapter, what to call it, and its request (null: nothing in it the model may change).</summary>
    private sealed record Unit(int From, int To, string Label, BookEditRequest? Request);

    private sealed record Prepared(CampaignAccess Dm, string Original, int From, int To, IReadOnlyList<Unit> Units)
    {
        public IReadOnlyList<BookEditRequest> Requests => [.. Units.Select(u => u.Request).OfType<BookEditRequest>()];
        public int Pieces => Requests.Sum(r => r.Passage.Pieces.Count);
    }

    /// <summary>The words a request is sent as: a preset's, or the GM's own.</summary>
    public static string? AskText(string? ask) =>
        ask == null ? null : BookEditPrompts.Presets.TryGetValue(ask, out var preset) ? preset : ask.Trim();

    /// <summary>The game's own words a book capitalises without their being names: its abilities, resources, conditions and sheet fields, as the
    /// language shows them and as the system wrote them.</summary>
    public static IReadOnlyList<string> Vocabulary(SystemDefinition? system) => system == null ? [] :
        [.. system.Attributes.SelectMany(a => new[] { a.Label, a.Abbreviation, a.CanonicalLabel, a.CanonicalAbbreviation }),
            .. system.Resources.Select(r => r.Label), .. system.Conditions, .. system.ActorFields.Select(f => f.Label)];

    private async Task<(BookEditOutcome Outcome, Prepared? Prepared)> PrepareAsync(CampaignAccess asked, string edition, string language, string section, int version,
        int from, int to, string selected, string? ask)
    {
        var words = AskText(ask);
        if (string.IsNullOrWhiteSpace(words)) return (BookEditOutcome.Empty, null);
        if (words.Length > BookDraftEdits.MaxAskChars) return (BookEditOutcome.TooLong, null);
        if (await drafts.ChapterAsync(asked, edition, language, section) is not { } found) return (BookEditOutcome.NotAllowed, null);
        return Prepare(found.Dm, found.SystemId, found.Chapter, found.Names, edition, language, version, from, to, selected, ask, words);
    }

    private (BookEditOutcome Outcome, Prepared? Prepared) Prepare(CampaignAccess dm, string systemId, BookDraftChapterView chapter, IReadOnlyList<string> names,
        string edition, string language, int version, int from, int to, string selected, string? ask, string words)
    {
        var found = (Dm: dm, SystemId: systemId, Chapter: chapter, Names: names);
        var text = found.Chapter.Markdown;
        // What the editor showed must be what is stored: the page's offsets are trusted only as far as the text they point at matches.
        if (found.Chapter.Version != version || from < 0 || to > text.Length || from > to || selected.Length != to - from
            || string.CompareOrdinal(text, from, selected, 0, to - from) != 0)
        {
            return (BookEditOutcome.Stale, null);
        }

        var preset = ask != null && BookEditPrompts.Presets.ContainsKey(ask);
        var party = edition == "party";
        var localized = catalog.GetLocalizedDefinition(found.SystemId, language);
        var system = localized?.Canonical;
        var name = system == null ? null : catalog.GetDisplayName(found.SystemId);
        var shape = party ? system?.Ai.PlayerBookShape : system?.Ai.BookShape;
        // Only a preset keeps the facts: the GM's own words may ask to change one ("make Vera a dwarf").
        var facts = preset ? new BookFactNames(found.Names, [.. Vocabulary(localized), .. Vocabulary(system)]) : null;
        BookEditRequest Request(BookEditPassage passage) =>
            new(found.Chapter.Title, language, name, shape, words, passage) { Party = party, Facts = facts };

        // Nothing selected: the whole chapter; with a preset, entry by entry.
        var whole = from == to;
        if (whole) (from, to) = (0, text.Length);
        var parts = whole && preset ? Parts(text) : null;
        if (parts == null)
        {
            if (BookDraftEdits.Cut(text, from, to) is not { } passage) return (BookEditOutcome.Stale, null);
            if (Blank(passage)) return (BookEditOutcome.Empty, null);
            if (BookTokens.Estimate(passage.Text) > BookDraftEdits.MaxPassageTokens) return (BookEditOutcome.TooLong, null);
            return (BookEditOutcome.Ready, new Prepared(found.Dm, passage.Original, passage.From, passage.To,
                [new Unit(passage.From, passage.To, found.Chapter.Title, Request(passage))]));
        }

        var units = new List<Unit>();
        foreach (var (start, end, label) in parts)
        {
            // A part's text is its own: the pieces were found in the whole chapter, and Parts already joined any a piece crossed.
            var passage = BookDraftEdits.Cut(text, start, end)!;
            if (BookTokens.Estimate(passage.Text) > BookDraftEdits.MaxPassageTokens) return (BookEditOutcome.TooLong, null);
            units.Add(new Unit(passage.From, passage.To, label.Length > 0 ? label : found.Chapter.Title, Blank(passage) ? null : Request(passage)));
        }

        if (units.All(u => u.Request == null)) return (BookEditOutcome.Empty, null);
        return (BookEditOutcome.Ready, new Prepared(found.Dm, text, 0, text.Length, units));
    }

    private static bool Blank(BookEditPassage passage) =>
        System.Text.RegularExpressions.Regex.Replace(passage.Text, @"\{\{block:\d+\}\}", string.Empty).Trim().Length == 0;

    /// <summary>
    /// The chapter in the parts an entry-by-entry edit sends: its head and each entry (<see cref="BookDraftMerge.Split"/>), each with its first
    /// heading. Parts a piece crosses (a secret never closed runs on into the next entry) are one part, so no two replies ever overlap. Null when an
    /// entry is in the chapter twice: the chapter is then one request, as a selection is.
    /// </summary>
    public static List<(int From, int To, string Label)>? Parts(string chapter)
    {
        if (BookDraftMerge.Split(chapter) is not { } split) return null;
        var parts = new List<(int From, int To, string Label)>();
        var at = 0;
        foreach (var unit in split)
        {
            var end = at + unit.Text.Length;
            string label = BookChecks.Headings(BookDraftEdits.Bare(unit.Text)).FirstOrDefault() ?? string.Empty;
            if (parts.Count > 0 && BookDraftEdits.Cut(chapter, parts[^1].From, parts[^1].To) is { } last && last.To > parts[^1].To)
            {
                // The part before took some of this one with it: one part.
                parts[^1] = (parts[^1].From, end, parts[^1].Label.Length > 0 ? parts[^1].Label : label);
            }
            else
            {
                parts.Add((at, end, label));
            }

            at = end;
        }

        // The last part may still reach past where it was thought to end: it is the chapter's end, so it cannot.
        return parts;
    }

    /// <summary>
    /// What a preset over the whole book would send and cost (docs/BOOK_EXPORT.md 42, step 3): every chapter of the draft that has something the
    /// model may change, entry by entry, as one edit of each would. Sends nothing. The chapters are then asked one at a time, each reviewed before
    /// the next is sent (<see cref="AskAsync"/>), so a closed page loses one chapter's reply at most.
    /// </summary>
    public async Task<BookShapePreview> BookPreviewAsync(CampaignAccess asked, string edition, string language, string ask)
    {
        if (!BookEditPrompts.Presets.ContainsKey(ask) || AskText(ask) is not { } words) return new BookShapePreview(BookEditOutcome.Empty, []);
        if (await drafts.ChaptersAsync(asked, edition, language) is not { } found) return new BookShapePreview(BookEditOutcome.NotAllowed, []);
        var sections = new List<string>();
        var requests = new List<BookEditRequest>();
        var tooLong = new List<string>();
        foreach (var chapter in found.Chapters.Where(c => !c.Gone))
        {
            var (outcome, p) = Prepare(found.Dm, found.SystemId, chapter, found.Names, edition, language, chapter.Version, 0, 0, string.Empty, ask, words);
            // A chapter with an entry too long for one request is left out of the run, and said so.
            if (outcome == BookEditOutcome.TooLong) tooLong.Add(chapter.Section);
            if (p == null) continue;
            sections.Add(chapter.Section);
            requests.AddRange(p.Requests);
        }

        if (sections.Count == 0) return new BookShapePreview(BookEditOutcome.Empty, []) { TooLong = tooLong };
        var model = await models.InfoAsync(found.Dm.UserId);
        if (model == null) return new BookShapePreview(BookEditOutcome.NoModel, sections, requests.Count) { TooLong = tooLong };
        return new BookShapePreview(BookEditOutcome.Ready, sections, requests.Count, BookPreflight.Estimate([.. requests.Select(BookEditWorker.LoadFor)], 0, model.Price), model)
        {
            TooLong = tooLong
        };
    }

    /// <summary>The most one request may spend: the first attempt and a retry that carries the refused reply back, each at its worst case.</summary>
    public static long CapFor(BookEditRequest request)
    {
        var load = BookEditWorker.LoadFor(request);
        var retry = load.InputTokens + BookTokens.Estimate(new string('x', 4000)) + 400;
        return BookBudget.WorstCase(load.InputTokens, load.MaxOutputTokens) + BookBudget.WorstCase(retry, load.MaxOutputTokens);
    }

    /// <summary>What the request would send and cost now; sends nothing.</summary>
    public async Task<BookEditPreview> PreviewAsync(CampaignAccess asked, string edition, string language, string section, int version, int from, int to, string selected, string? ask)
    {
        var (outcome, p) = await PrepareAsync(asked, edition, language, section, version, from, to, selected, ask);
        if (p == null) return new BookEditPreview(outcome);
        var requests = p.Requests;
        var model = await models.InfoAsync(p.Dm.UserId);
        if (model == null) return new BookEditPreview(BookEditOutcome.NoModel, p.From, p.To, p.Pieces, Calls: requests.Count);
        var preflight = BookPreflight.Estimate([.. requests.Select(BookEditWorker.LoadFor)], 0, model.Price);
        return new BookEditPreview(BookEditOutcome.Ready, p.From, p.To, p.Pieces, preflight, model, requests.Count);
    }

    /// <summary>Asks the model for the edit and waits for it. Refused before anything is sent: another paid run of this GM's, no model, the
    /// server's allowance (its own message, as a <see cref="LocalizedException"/>). Entry by entry, the requests go in turn under one budget capped
    /// at all their worst cases; an entry whose reply fails keeps its text and is named in <see cref="BookEditProposal.Kept"/>, and a failure the
    /// rest would meet the same way (the key refused, the money or the allowance gone, a stop) ends the run there, the entries not yet asked kept
    /// as they were.</summary>
    public async Task<BookEditProposal> AskAsync(CampaignAccess asked, string edition, string language, string section, int version, int from, int to, string selected, string? ask,
        CancellationToken cancel = default)
    {
        if (!gate.TryEnter(asked.UserId)) return new BookEditProposal(BookEditOutcome.Busy);
        try
        {
            var (outcome, p) = await PrepareAsync(asked, edition, language, section, version, from, to, selected, ask);
            if (p == null) return new BookEditProposal(outcome);
            var requests = p.Requests;
            BookModelHandle model;
            try
            {
                model = await models.OpenAsync(p.Dm.UserId, BookEditWorker.MaxAttempts * requests.Count);
            }
            catch (AiNotConfiguredException)
            {
                return new BookEditProposal(BookEditOutcome.NoModel);
            }

            using (model)
            {
                var budget = new BookBudget(requests.Sum(CapFor));
                var worker = new BookEditWorker(model.Chat, budget, refusedBeforeSending: model.RefusedBeforeSending);
                var replaced = new Dictionary<Unit, string>();
                var kept = new List<BookEditKept>();
                string? reason = null;
                var stopped = false;
                foreach (var unit in p.Units)
                {
                    if (unit.Request is not { } request) continue;
                    if (stopped)
                    {
                        kept.Add(new BookEditKept(unit.Label, reason ?? "cancelled"));
                        continue;
                    }

                    var result = await worker.RunAsync(request, cancel);
                    if (result.Replacement != null)
                    {
                        replaced[unit] = result.Replacement;
                        continue;
                    }

                    reason = result.Reason;
                    kept.Add(new BookEditKept(unit.Label, result.Reason ?? "error"));
                    // The checks' words, not the reply: they may quote a marker number or a name the passage gave, never text the model was not sent.
                    log?.LogInformation("AI book edit: a part kept its text after {Attempts} attempt(s), reason {Reason}; problems: {Problems}",
                        result.Attempts, result.Reason, string.Join(" | ", result.Problems));
                    if (result.Reason is "auth" or "ratelimit" or "allowance" or "budget" or "cancelled") stopped = true;
                }

                var spent = budget.Snapshot();
                await BookSpend.AddAsync(builds, p.Dm, edition, spent, model.Price);
                var tokens = spent.InputTokens + spent.OutputTokens;
                var cost = spent.Cost(model.Price);
                if (replaced.Count == 0) return new BookEditProposal(BookEditOutcome.Failed, p.From, p.To, p.Original, null, reason, tokens, cost, kept);
                var text = new System.Text.StringBuilder();
                var at = p.From;
                foreach (var unit in p.Units)
                {
                    text.Append(p.Original, at - p.From, unit.From - at);
                    text.Append(replaced.TryGetValue(unit, out var r) ? r : p.Original.Substring(unit.From - p.From, unit.To - unit.From));
                    at = unit.To;
                }

                text.Append(p.Original, at - p.From, p.To - at);
                return new BookEditProposal(BookEditOutcome.Ready, p.From, p.To, p.Original, text.ToString(), kept.Count > 0 ? reason : null, tokens, cost, kept);
            }
        }
        finally
        {
            gate.Exit(asked.UserId);
        }
    }
}
