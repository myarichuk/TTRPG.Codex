namespace Codex.Core.Consistency;

/// <summary>
/// A stored mention (<c>[[actor:id]]</c>, <c>[[place:id]]</c>) in any text whose character or place is gone. Unlike a red wiki link, this is
/// almost never meant: a mention is made by picking the thing from a list. A retired character still exists, so it is never reported.
/// </summary>
public sealed class DanglingMentionCheck : IConsistencyCheck
{
    public string Name => CheckNames.DanglingMention;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var places = s.Places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var field in TextFields.Of(s))
        {
            foreach (var (kind, id) in TextFields.Mentions(field.Text))
            {
                if ((kind == "actor" ? actors : places).Contains(id)) continue;
                yield return Make.Issue(Name, IssueSeverity.Error, field.Kind, field.EntityId, field.Field, $"{kind}:{id}", "Consistency.DanglingMention",
                    new[] { kind, id },
                    $"The text mentions a {kind} ({id}) that no longer exists.",
                    $"Take the mention out, or replace it with a {kind} that exists. Do not invent an id.",
                    field.Fix);
            }
        }
    }
}

/// <summary>
/// The pointers between documents that name a document which is gone: a page's character or place, a character's or place's page.
/// These are not text, so no proposal can fix them yet: the DM relinks or clears them.
/// </summary>
public sealed class DanglingReferenceCheck : IConsistencyCheck
{
    public string Name => CheckNames.DanglingReference;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var pages = s.Pages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var places = s.Places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var p in s.Pages)
        {
            if (!string.IsNullOrEmpty(p.ActorId) && !actors.Contains(p.ActorId)) yield return Of("page", p.Id, "actorId", "character", p.ActorId);
            if (!string.IsNullOrEmpty(p.PlaceId) && !places.Contains(p.PlaceId)) yield return Of("page", p.Id, "placeId", "place", p.PlaceId);
        }

        foreach (var a in s.Actors)
        {
            if (!string.IsNullOrEmpty(a.WikiPageId) && !pages.Contains(a.WikiPageId)) yield return Of("actor", a.Id, "wikiPageId", "page", a.WikiPageId);
        }

        foreach (var p in s.Places)
        {
            if (!string.IsNullOrEmpty(p.WikiPageId) && !pages.Contains(p.WikiPageId)) yield return Of("place", p.Id, "wikiPageId", "page", p.WikiPageId);
        }
    }

    private Issue Of(string kind, string entityId, string field, string pointsAt, string target) =>
        Make.Issue(Name, IssueSeverity.Error, kind, entityId, field, null, "Consistency.DanglingReference",
            new[] { field, pointsAt, target },
            $"The {kind}'s {field} points at a {pointsAt} ({target}) that no longer exists.",
            "The link has to be cleared or pointed at something that exists, which a proposal cannot do. Tell the DM.",
            FixVia.ReportOnly);
}
