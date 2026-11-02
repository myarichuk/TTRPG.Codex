using Codex.Core;

namespace Codex.Persistence;

/// <summary>
/// How a table note reads for one viewer: its kind's name, and when in an encounter it was written. Like
/// <see cref="SessionQuotes"/>, every actor a note names goes through the viewer's own list, so a player never
/// learns of someone they can't see from a note's context.
/// </summary>
public static class SessionNotes
{
    public static IReadOnlyList<NoteKind> Kinds { get; } = Enum.GetValues<NoteKind>();

    /// <summary>The kind in English, for the AI's session digest; the pages name it through NoteKindText.</summary>
    public static string KindLabel(NoteKind kind) => kind switch
    {
        NoteKind.Clue => "Clue",
        NoteKind.Loot => "Loot",
        NoteKind.Ruling => "Ruling",
        NoteKind.FollowUp => "Follow-up",
        _ => "Note"
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

    /// <summary>A table note's words as <paramref name="access"/> reads them. Notes are typed in boxes that take <c>:::dm</c>
    /// blocks, so a player reads someone else's note without the blocks they may not read (<paramref name="ownedActorIds"/>:
    /// their characters), as they read a page; their own they wrote, and may correct, so it comes whole (F8).</summary>
    public static string TextFor(SessionNote note, CampaignAccess access, IReadOnlySet<string> ownedActorIds) =>
        access.IsDm || note.AuthorId == access.UserId || !SecretText.HasMarkers(note.Text)
            ? note.Text
            : SecretText.ForPlayers(note.Text, ownedActorIds);
}
