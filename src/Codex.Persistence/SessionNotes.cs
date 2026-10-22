namespace Codex.Persistence;

/// <summary>
/// How a table note reads for one viewer: its kind's name, and when in an encounter it was written. Like
/// <see cref="SessionQuotes"/>, every actor a note names goes through the viewer's own list, so a player never
/// learns of someone they can't see from a note's context.
/// </summary>
public static class SessionNotes
{
    public static IReadOnlyList<NoteKind> Kinds { get; } = Enum.GetValues<NoteKind>();

    /// <summary>The short name on chips and pills.</summary>
    public static string KindLabel(NoteKind kind) => kind switch
    {
        NoteKind.Clue => "Clue",
        NoteKind.Loot => "Loot",
        NoteKind.Ruling => "Ruling",
        NoteKind.FollowUp => "Follow-up",
        _ => "Note"
    };

    /// <summary>The kind as a noun in a sentence ("a clue was added").</summary>
    public static string KindNoun(NoteKind kind) => kind switch
    {
        NoteKind.Clue => "clue",
        NoteKind.Loot => "loot entry",
        NoteKind.Ruling => "ruling",
        NoteKind.FollowUp => "follow-up",
        _ => "note"
    };

    /// <summary>"Round 3 · Grik's turn", "Round 3" when the viewer can't see who was acting, or null outside an encounter.</summary>
    /// <param name="visibleActors">The actors this viewer may see (a player's list already leaves Hidden ones out).</param>
    public static string? Context(SessionNote note, IReadOnlyDictionary<string, ActorDocument> visibleActors, bool isDm)
    {
        if (note.Round is not { } round) return null;
        return TurnName(note, visibleActors, isDm) is { } turn ? $"Round {round} · {turn}'s turn" : $"Round {round}";
    }

    /// <summary>Who was acting when the note was written, by the name this viewer knows them by; null when nobody was or the viewer can't see them.</summary>
    public static string? TurnName(SessionNote note, IReadOnlyDictionary<string, ActorDocument> visibleActors, bool isDm) =>
        note.TurnActorId != null && visibleActors.TryGetValue(note.TurnActorId, out var actor) && (isDm || actor.Visibility != ActorVisibility.Hidden)
            ? actor.NameFor(isDm)
            : null;

    /// <summary>The others in a line's conversation that this viewer can see, by the names they know them by.</summary>
    public static IReadOnlyList<string> WithNames(SessionNote note, IReadOnlyDictionary<string, ActorDocument> visibleActors, bool isDm) =>
        note.WithActorIds
            .Select(id => visibleActors.GetValueOrDefault(id))
            .Where(a => a != null && (isDm || a.Visibility != ActorVisibility.Hidden))
            .Select(a => a!.NameFor(isDm))
            .ToList();

    /// <summary>Whether <paramref name="userId"/> may correct or remove <paramref name="note"/>: its author or the DM,
    /// and only notes that have an id.</summary>
    public static bool CanEdit(SessionNote note, string userId, bool isDm) =>
        !string.IsNullOrEmpty(note.Id) && (isDm || note.AuthorId == userId);
}
