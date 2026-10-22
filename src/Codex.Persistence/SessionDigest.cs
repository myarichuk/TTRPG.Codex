namespace Codex.Persistence;

/// <summary>
/// What an AI summary of a session is allowed to see, as plain lines. Built once per audience:
/// the DM digest carries everything; the player digest drops secret rolls, private notes and anything about actors the players can't see, so
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

    /// <summary>The DM's own prior summary, included only in the DM digest as context.</summary>
    public string? DmSummary { get; init; }

    public bool IsEmpty => Events.Count == 0 && Notes.Count == 0 && Rolls.Count == 0;

    /// <param name="actors">The campaign's actors, so the player digest can leave out events about ones the players
    /// don't know of and keep exact numbers out of the rest. Without them a player digest carries no actor events at all.</param>
    public static SessionDigest Build(SessionDocument session, IEnumerable<NoteDocument> notes, bool forDm, Func<string, string>? nameOf = null, IEnumerable<ActorDocument>? actors = null)
    {
        nameOf ??= id => id;
        var sourceEvents = forDm ? session.Events : SessionEventRedactor.ForPlayers(session.Events, actors ?? Array.Empty<ActorDocument>());
        var events = sourceEvents.OrderBy(e => e.Timestamp)
            .Select(e => $"{e.Timestamp:HH:mm} [{e.Type}] {e.Description}")
            .ToList();

        var noteLines = notes
            .Where(n => forDm || n.Visibility == CommentVisibility.Public)
            .OrderBy(n => n.CreatedAt)
            .Select(n => $"{nameOf(n.AuthorId)}{(n.Visibility == CommentVisibility.Private ? " (private)" : "")}: {n.Content}")
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
            DmSummary = forDm && !string.IsNullOrWhiteSpace(session.DmSummary) ? session.DmSummary : null
        };
    }
}
