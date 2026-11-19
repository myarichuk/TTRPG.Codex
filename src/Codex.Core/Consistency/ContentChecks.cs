using System.Text.RegularExpressions;

namespace Codex.Core.Consistency;

/// <summary>
/// A character of the table (not a bestiary creature, not a retired one) whose history is empty. Info, and never to be filled in by
/// a model on its own: a history is what the DM or the player wants it to be.
/// </summary>
public sealed class EmptyBioCheck : IConsistencyCheck
{
    public string Name => CheckNames.EmptyBio;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var a in s.Actors.Where(a => a.Role != ActorRole.Monster && !a.Retired && string.IsNullOrWhiteSpace(SecretText.WithoutMarkers(a.Biography))))
        {
            yield return Make.Issue(Name, IssueSeverity.Info, "actor", a.Id, "biography", null, "Consistency.EmptyBio",
                new[] { a.Name },
                $"\"{Make.Quote(a.Name)}\" has no history written.",
                "Ask the DM what the history is, or propose text only from what the campaign already says about this character. Never invent a past for them.",
                FixVia.ActorBiography);
        }
    }
}

/// <summary>A page with nothing written under its title: empty, or only headings.</summary>
public sealed class EmptyBodyCheck : IConsistencyCheck
{
    public string Name => CheckNames.EmptyBody;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var p in s.Pages.Where(p => IsEmpty(p.Body)))
        {
            yield return Make.Issue(Name, IssueSeverity.Info, "page", p.Id, "body", null, "Consistency.EmptyBody",
                new[] { p.Title },
                $"The page \"{Make.Quote(p.Title)}\" has no text.",
                "Ask the DM what the page should say. Never fill a page with invented lore.",
                FixVia.Page);
        }
    }

    private static bool IsEmpty(string? body) =>
        string.IsNullOrWhiteSpace(body) || body.Replace("\r", "").Split('\n').All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith('#'));
}

/// <summary>Placeholder words left in a text: TODO, TBD, FIXME, lorem ipsum.</summary>
public sealed class TodoMarkerCheck : IConsistencyCheck
{
    public string Name => CheckNames.TodoMarker;

    private static readonly Regex Marker = new(@"\b(TODO|TBD|FIXME)\b|(?i:lorem ipsum)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var field in TextFields.Of(s))
        {
            // DM notes are the DM's own scratch space.
            if (field.Field == "dmNotes" || Marker.Match(field.Text) is not { Success: true } m) continue;
            yield return Make.Issue(Name, IssueSeverity.Info, field.Kind, field.EntityId, field.Field, null, "Consistency.TodoMarker",
                new[] { m.Value },
                $"The text still holds the placeholder \"{m.Value}\".",
                "Ask the DM what should replace it. Do not invent content to fill the gap.",
                field.Fix);
        }
    }
}
