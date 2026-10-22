namespace Codex.Persistence;

/// <summary>
/// What an AI summary of a session is allowed to see, as plain lines. Built once per audience:
/// the DM digest carries everything; the player digest drops secret rolls, private and secret notes and anything about actors the players can't see, so
/// a player-facing recap can never be drafted from something the players don't know.
/// </summary>
public sealed class SessionDigest
{
    public string Title { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public bool ForDm { get; init; }
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Rolls { get; init; } = Array.Empty<string>();

    /// <summary>Lines of dialogue from the table, as <c>Speaker: "words"</c>. A speaker the audience can't identify is "Someone".</summary>
    public IReadOnlyList<string> Quotes { get; init; } = Array.Empty<string>();

    /// <summary>The DM's own prior summary, included only in the DM digest as context.</summary>
    public string? DmSummary { get; init; }

    public bool IsEmpty => Events.Count == 0 && Notes.Count == 0 && Rolls.Count == 0 && Quotes.Count == 0;

    /// <param name="actors">The campaign's actors, so the player digest can leave out events about ones the players
    /// don't know of and keep exact numbers out of the rest, and name the speakers of quotes. Without them a player digest
    /// carries no actor events at all and every quote is "Someone"'s.</param>
    public static SessionDigest Build(SessionDocument session, IEnumerable<NoteDocument> notes, bool forDm, Func<string, string>? nameOf = null, IEnumerable<ActorDocument>? actors = null)
    {
        nameOf ??= id => id;
        var sourceEvents = forDm ? session.Events : SessionEventRedactor.ForPlayers(session.Events, actors ?? Array.Empty<ActorDocument>());
        // Notes and quotes are in the digest in full below; their "Note" events would only repeat them.
        var events = sourceEvents.Where(e => e.Type != "Note").OrderBy(e => e.Timestamp)
            .Select(e => $"{e.Timestamp:HH:mm} [{e.Type}] {e.Description}")
            .ToList();

        // Two note sources, one timeline: notes left on the session page, and the quick notes typed at the live table.
        var pageNotes = notes
            .Where(n => forDm || n.Visibility == CommentVisibility.Public)
            .Select(n => (n.CreatedAt, Line: $"{nameOf(n.AuthorId)}{(n.Visibility == CommentVisibility.Private ? " (private)" : "")}: {n.Content}"));
        var visibleTableNotes = session.Notes.Where(n => forDm || !n.IsSecret).ToList();
        var tableNotes = visibleTableNotes
            .Where(n => n.SpeakerActorId == null)
            .Select(n => (CreatedAt: n.Timestamp, Line: $"{nameOf(n.AuthorId)}{(n.IsSecret ? " (secret)" : "")}: {n.Text}"));
        var noteLines = pageNotes.Concat(tableNotes)
            .OrderBy(n => n.CreatedAt)
            .Select(n => n.Line)
            .ToList();

        // Players' recap names only speakers they fully know, the same rule the player event log follows.
        var speakers = (actors ?? Array.Empty<ActorDocument>())
            .Where(a => forDm || a.Visibility == ActorVisibility.Known)
            .ToDictionary(a => a.Id);
        var quotes = visibleTableNotes
            .Where(n => n.SpeakerActorId != null)
            .OrderBy(n => n.Timestamp)
            .Select(n => $"{SessionQuotes.SpeakerName(n.SpeakerActorId!, speakers, forDm)}{(n.IsSecret ? " (secret)" : "")}: \"{n.Text}\"")
            .ToList();

        var rolls = session.RollLog
            .Where(r => forDm || !r.IsSecret)
            .OrderBy(r => r.Timestamp)
            .Select(r => $"{r.Timestamp:HH:mm} {nameOf(r.RollerUserId)} rolled {r.Expression} = {r.Total}{(r.IsSecret ? " (secret)" : "")}")
            .ToList();

        return new SessionDigest
        {
            Title = session.Title,
            Date = session.Date,
            ForDm = forDm,
            Events = events,
            Notes = noteLines,
            Rolls = rolls,
            Quotes = quotes,
            DmSummary = forDm && !string.IsNullOrWhiteSpace(session.DmSummary) ? session.DmSummary : null
        };
    }
}
