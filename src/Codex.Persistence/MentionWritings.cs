namespace Codex.Persistence;

/// <summary>Which box a mention was written in, besides a table note.</summary>
public enum WrittenIn { Recap, DmSummary, Comment, Journal, Biography, DmNotes, WikiBody }

/// <summary>The page that holds the writing: a session, a character sheet, a wiki page or a journal entry.</summary>
public enum MentionHome { Session, Character, WikiPage, Journal }

/// <summary>One piece of writing that mentions something, cut to the line around the mention.</summary>
/// <param name="HomeTitle">The holding page's title; empty for an untitled session, which pages name by <paramref name="HomeDate"/>.</param>
/// <param name="Private">Not everyone in the campaign can read it: the DM's own writing, a private comment or a journal.</param>
public sealed record WrittenMention(WrittenIn Source, MentionHome Home, string HomeId, string HomeTitle, DateTime HomeDate, DateTime When,
    string Excerpt, bool Private, string? AuthorId = null);

/// <summary>
/// Everything one viewer has written or may read outside the table notes: session recaps and DM summaries, comments
/// on sessions and wiki pages, their own journal, and character biographies and DM notes. The repositories already
/// scope what they load to the viewer; the rules are applied here again so a list built from it never shows someone
/// writing they couldn't open.
/// </summary>
public sealed class MentionWritings
{
    private readonly IReadOnlyList<SessionDocument> _sessions;
    private readonly IReadOnlyList<NoteDocument> _comments;
    private readonly IReadOnlyList<JournalEntryDocument> _journals;
    private readonly IReadOnlyList<ActorDocument> _actors;
    private readonly IReadOnlyList<WikiPageDocument> _wikiPages;
    private readonly bool _isDm;
    private readonly string _userId;

    public static MentionWritings Empty { get; } = new(Array.Empty<SessionDocument>(), Array.Empty<NoteDocument>(), Array.Empty<JournalEntryDocument>(),
        Array.Empty<ActorDocument>(), Array.Empty<WikiPageDocument>(), isDm: false, userId: string.Empty);

    public MentionWritings(IEnumerable<SessionDocument> sessions, IEnumerable<NoteDocument> comments, IEnumerable<JournalEntryDocument> journals,
        IEnumerable<ActorDocument> actors, IEnumerable<WikiPageDocument> wikiPages, bool isDm, string userId)
    {
        _isDm = isDm;
        _userId = userId;
        _sessions = sessions.ToList();
        _comments = comments.Where(c => isDm || c.Visibility == CommentVisibility.Public || c.AuthorId == userId).ToList();
        _journals = journals.Where(j => j.OwnerUserId == userId).ToList();
        _actors = actors.ToList();
        _wikiPages = wikiPages.Where(p => isDm || p.Visibility == WikiVisibility.Party).ToList();
    }

    /// <summary>Whether anything here mentions anything, so a page can skip building a directory when nothing does.</summary>
    public bool HasMentions =>
        _sessions.Any(s => Has(s.Recap) || (_isDm && Has(s.DmSummary)))
        || _comments.Any(c => Has(c.Content))
        || _journals.Any(j => Has(j.Body))
        || _actors.Any(a => Has(a.Biography) || (_isDm && Has(a.DmNotes)))
        || _wikiPages.Any(p => Has(p.Body));

    /// <summary>Every piece of writing that mentions <paramref name="id"/>, newest first.</summary>
    public IReadOnlyList<WrittenMention> Mentioning(MentionKind kind, string id)
    {
        var token = SessionMentions.Token(kind, id);
        bool Mentions(string? text) => text != null && text.Contains(token, StringComparison.Ordinal);
        string Cut(string text) => SessionMentions.Excerpt(text, token);
        var found = new List<WrittenMention>();
        var sessionsById = _sessions.ToDictionary(s => s.Id);

        foreach (var s in _sessions)
        {
            if (Mentions(s.Recap)) found.Add(new(WrittenIn.Recap, MentionHome.Session, s.Id, s.Title, s.Date, s.Date, Cut(s.Recap!), Private: false));
            if (_isDm && Mentions(s.DmSummary)) found.Add(new(WrittenIn.DmSummary, MentionHome.Session, s.Id, s.Title, s.Date, s.Date, Cut(s.DmSummary!), Private: true));
        }

        foreach (var c in _comments.Where(c => Mentions(c.Content)))
        {
            var isPrivate = c.Visibility == CommentVisibility.Private;
            if (c.TargetId.StartsWith("session:", StringComparison.Ordinal) && sessionsById.TryGetValue(c.TargetId["session:".Length..], out var session))
            {
                found.Add(new(WrittenIn.Comment, MentionHome.Session, session.Id, session.Title, session.Date, c.CreatedAt, Cut(c.Content), isPrivate, c.AuthorId));
            }
            else if (c.TargetId.StartsWith("wiki:", StringComparison.Ordinal) && _wikiPages.FirstOrDefault(p => p.Id == c.TargetId["wiki:".Length..]) is { } page)
            {
                found.Add(new(WrittenIn.Comment, MentionHome.WikiPage, page.Slug, page.Title, c.CreatedAt, c.CreatedAt, Cut(c.Content), isPrivate, c.AuthorId));
            }
        }

        foreach (var j in _journals.Where(j => Mentions(j.Body)))
        {
            var title = !string.IsNullOrWhiteSpace(j.Title) ? j.Title
                : j.SessionId != null && sessionsById.TryGetValue(j.SessionId, out var session) ? session.Title : string.Empty;
            found.Add(new(WrittenIn.Journal, MentionHome.Journal, j.Id, title, j.CreatedAt, j.UpdatedAt, Cut(j.Body), Private: true));
        }

        // A character's own sheet doesn't list itself.
        foreach (var a in _actors.Where(a => !(kind == MentionKind.Actor && a.Id == id)))
        {
            if (Mentions(a.Biography)) found.Add(new(WrittenIn.Biography, MentionHome.Character, a.Id, a.NameFor(_isDm), a.UpdatedAt, a.UpdatedAt, Cut(a.Biography!), Private: false));
            if (_isDm && Mentions(a.DmNotes)) found.Add(new(WrittenIn.DmNotes, MentionHome.Character, a.Id, a.NameFor(_isDm), a.UpdatedAt, a.UpdatedAt, Cut(a.DmNotes!), Private: true));
        }

        foreach (var p in _wikiPages.Where(p => Mentions(p.Body)))
        {
            found.Add(new(WrittenIn.WikiBody, MentionHome.WikiPage, p.Slug, p.Title, p.UpdatedAt, p.UpdatedAt, Cut(p.Body), Private: p.Visibility == WikiVisibility.DmOnly));
        }

        return found.OrderByDescending(m => m.When).ToList();
    }

    public static string SourceLabel(WrittenIn source) => source switch
    {
        WrittenIn.Recap => "Recap",
        WrittenIn.DmSummary => "DM summary",
        WrittenIn.Comment => "Comment",
        WrittenIn.Journal => "Journal",
        WrittenIn.Biography => "Biography",
        WrittenIn.WikiBody => "Wiki page",
        _ => "DM notes"
    };

    private static bool Has(string? text) => text != null && SessionMentions.HasMentions(text);
}
