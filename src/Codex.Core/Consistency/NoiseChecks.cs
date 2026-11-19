namespace Codex.Core.Consistency;

// Checks that are true but rarely worth a DM's time. They are never part of the default run: they sit in ConsistencyChecker.Noisy and run only
// when a caller asks for them (list_issues scope "noise" or the check's name), so they never appear in the default list or in the issue count.

/// <summary>A page that nothing leads to: no page or secret links it, and no character or place is about it. Often fine (an index, a draft).</summary>
public sealed class OrphanPageCheck : IConsistencyCheck
{
    public string Name => CheckNames.OrphanPage;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var lookup = new PageLookup(s.Pages);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in s.Pages)
        {
            foreach (var title in WikiText.ExtractLinks(p.Body))
            {
                if (lookup.Resolve(WikiText.Slugify(title)) is { } target && target.Id != p.Id) reached.Add(target.Id);
            }
        }

        foreach (var f in s.Facts)
        {
            foreach (var title in WikiText.ExtractLinks(f.Summary + "\n" + f.Details))
            {
                if (lookup.Resolve(WikiText.Slugify(title)) is { } target) reached.Add(target.Id);
            }
        }

        foreach (var a in s.Actors.Where(a => a.WikiPageId != null)) reached.Add(a.WikiPageId!);
        foreach (var p in s.Places.Where(p => p.WikiPageId != null)) reached.Add(p.WikiPageId!);

        foreach (var page in s.Pages.Where(p => !reached.Contains(p.Id) && p.ActorId == null && p.PlaceId == null))
        {
            yield return Make.Issue(Name, IssueSeverity.Info, "page", page.Id, "links", null, "Consistency.OrphanPage",
                new[] { page.Title },
                $"Nothing links to the page \"{Make.Quote(page.Title)}\".",
                "Ask the DM whether it should be linked from somewhere. Do not add links on your own.", FixVia.ReportOnly);
        }
    }
}

/// <summary>A page and a character or place that point at each other on one side only: the page names the character, but the character's own page is another.</summary>
public sealed class BacklinkMismatchCheck : IConsistencyCheck
{
    public string Name => CheckNames.BacklinkMismatch;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var places = s.Places.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var pages = s.Pages.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var p in s.Pages)
        {
            if (p.ActorId != null && actors.TryGetValue(p.ActorId, out var a) && a.WikiPageId != p.Id)
            {
                yield return Make.Issue(Name, IssueSeverity.Info, "page", p.Id, "actorId", p.ActorId, "Consistency.BacklinkMismatch",
                    new[] { p.Title, a.Name }, $"The page \"{Make.Quote(p.Title)}\" says it is about \"{Make.Quote(a.Name)}\", whose own page is {(a.WikiPageId == null ? "none" : "another")}.",
                    "Tell the DM; which page belongs to the character is theirs to say.", FixVia.ReportOnly);
            }

            if (p.PlaceId != null && places.TryGetValue(p.PlaceId, out var place) && place.WikiPageId != p.Id)
            {
                yield return Make.Issue(Name, IssueSeverity.Info, "page", p.Id, "placeId", p.PlaceId, "Consistency.BacklinkMismatch",
                    new[] { p.Title, place.Name }, $"The page \"{Make.Quote(p.Title)}\" says it is about the place \"{Make.Quote(place.Name)}\", whose own page is {(place.WikiPageId == null ? "none" : "another")}.",
                    "Tell the DM; which page belongs to the place is theirs to say.", FixVia.ReportOnly);
            }
        }

        foreach (var a in s.Actors.Where(a => a.WikiPageId != null && pages.TryGetValue(a.WikiPageId, out var page) && page.ActorId != a.Id))
        {
            yield return Make.Issue(Name, IssueSeverity.Info, "actor", a.Id, "wikiPageId", a.WikiPageId, "Consistency.BacklinkMismatch",
                new[] { a.Name, pages[a.WikiPageId!].Title }, $"\"{Make.Quote(a.Name)}\" has the page \"{Make.Quote(pages[a.WikiPageId!].Title)}\", which does not say it is about them.",
                "Tell the DM; the link is theirs to put right.", FixVia.ReportOnly);
        }
    }
}

/// <summary>A page of the kind "person" that no character is attached to. Many people in a world are never on the cast.</summary>
public sealed class PersonPageNoActorCheck : IConsistencyCheck
{
    public string Name => CheckNames.PersonPageNoActor;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var linked = s.Actors.Where(a => a.WikiPageId != null).Select(a => a.WikiPageId!).ToHashSet(StringComparer.Ordinal);
        foreach (var p in s.Pages.Where(p => p.Kind == "person" && p.ActorId == null && !linked.Contains(p.Id)))
        {
            yield return Make.Issue(Name, IssueSeverity.Info, "page", p.Id, "actorId", null, "Consistency.PersonPageNoActor",
                new[] { p.Title }, $"The person page \"{Make.Quote(p.Title)}\" has no character on the cast.",
                "Ask the DM whether they should be on the cast. Do not add cast members on your own.", FixVia.ReportOnly);
        }
    }
}

/// <summary>A text that mentions a character who has been retired (<c>[[actor:id]]</c>): out of play, but still named. Often exactly right (a death).</summary>
public sealed class RetiredMentionCheck : IConsistencyCheck
{
    public string Name => CheckNames.RetiredMention;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var retired = s.Actors.Where(a => a.Retired).ToDictionary(a => a.Id, StringComparer.Ordinal);
        if (retired.Count == 0) yield break;
        foreach (var field in TextFields.Of(s))
        {
            foreach (var (kind, id) in TextFields.Mentions(field.Text).Where(m => m.Kind == "actor" && retired.ContainsKey(m.Id)))
            {
                yield return Make.Issue(Name, IssueSeverity.Info, field.Kind, field.EntityId, field.Field, id, "Consistency.RetiredMention",
                    new[] { retired[id].Name },
                    $"The text mentions \"{Make.Quote(retired[id].Name)}\", who is retired.",
                    "Ask the DM whether the mention should stay; a retired character is still part of the story.", field.Fix);
            }
        }
    }
}
