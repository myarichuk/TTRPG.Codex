using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>A table note's kind as the reader reads it ("Clue", "Follow-up"): the session page, the notes drawer and a
/// character's notes all name it here.</summary>
public static class NoteKindText
{
    public static string Label(Localizer l, NoteKind kind) => l[$"Combat.Notes.Kind.{kind}"];
}
