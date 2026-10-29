namespace Codex.Persistence;

/// <summary>
/// Lines of dialogue are table notes with a speaker (<see cref="SessionNote.SpeakerActorId"/>). This is where
/// a viewer's name for that speaker is decided, and where an actor's lines are gathered across sessions.
/// </summary>
public static class SessionQuotes
{
    /// <summary>What a speaker the viewer can't see is called. The words were said aloud; who said them stays hidden.</summary>
    public const string UnknownSpeaker = "Someone";

    /// <summary>The name <paramref name="actor"/> goes by for this viewer: while the DM keeps them unidentified, the public
    /// name (or "a stranger", in the viewer's language, when the DM gave none: never the real one); the real name otherwise.</summary>
    public static string NameFor(this ActorDocument actor, bool isDm) =>
        isDm || actor.Visibility != ActorVisibility.Unknown ? actor.Name
        : string.IsNullOrWhiteSpace(actor.PublicName) ? StrangerName
        : actor.PublicName.Trim();

    /// <summary>What players call someone the DM keeps unidentified and gave no public name.</summary>
    public static string StrangerName => new Codex.Localization.Localizer()["Actor.Stranger"];

    /// <param name="visibleActors">The actors this viewer may see (a player's list already leaves Hidden ones out).</param>
    /// <param name="unknownName">What to call a speaker the viewer can't see, in the viewer's language.</param>
    public static string SpeakerName(string speakerActorId, IReadOnlyDictionary<string, ActorDocument> visibleActors, bool isDm, string unknownName = UnknownSpeaker) =>
        visibleActors.TryGetValue(speakerActorId, out var actor) && (isDm || actor.Visibility != ActorVisibility.Hidden)
            ? actor.NameFor(isDm)
            : unknownName;

    /// <summary>Everything <paramref name="actorId"/> said in these sessions that the viewer may read, oldest first.</summary>
    public static IReadOnlyList<(SessionDocument Session, SessionNote Line)> SaidBy(IEnumerable<SessionDocument> sessions, string actorId, bool isDm) =>
        sessions
            .SelectMany(s => s.Notes
                .Where(n => n.SpeakerActorId == actorId && (isDm || !n.IsSecret))
                .Select(n => (Session: s, Line: n)))
            .OrderBy(x => x.Line.Timestamp)
            .ToList();
}
