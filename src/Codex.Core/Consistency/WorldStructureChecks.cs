using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Consistency;

// The structural checks of v2: the shape of the place tree, duplicated names, the lists the lore keeps, placements, owners, homebrew and the
// campaign itself. Every one is a pure function of the snapshot, reads ids rather than titles for the ids of its issues, and sorts anything it
// builds. Most of what they find is the DM's to fix by hand, so most are report-only; the ones an existing edit can fix say so.

/// <summary>
/// The place tree of a region: a place inside a place that is not there, inside itself, inside another region, or in a loop. Each is an error:
/// the Places list cannot draw such a place where it belongs. Moving the place (an edit with a <c>parent</c>) fixes it, so the issue is about the place.
/// A loop is reported once, on the member with the lowest id.
/// </summary>
public sealed class PlaceTreeCheck : IConsistencyCheck
{
    public string Name => CheckNames.PlaceTree;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var inRegion = s.Places.GroupBy(p => p.RegionId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        var everywhere = s.Places.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var region in inRegion.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var tidy = new HashSet<string>(StringComparer.Ordinal); // places whose parent exists in their own region
            foreach (var place in region.Value.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(place.ParentId)) continue;
                if (place.ParentId == place.Id)
                {
                    yield return Issue(place, "self", "Self", new[] { place.Name }, $"The place \"{Make.Quote(place.Name)}\" is inside itself.");
                }
                else if (region.Value.ContainsKey(place.ParentId))
                {
                    tidy.Add(place.Id);
                }
                else if (everywhere.TryGetValue(place.ParentId, out var other))
                {
                    yield return Issue(place, "cross-region:" + place.ParentId, "CrossRegion", new[] { place.Name, other.Name },
                        $"The place \"{Make.Quote(place.Name)}\" is inside \"{Make.Quote(other.Name)}\", which is in another region; a place can only be inside one of its own region.");
                }
                else
                {
                    yield return Issue(place, "missing:" + place.ParentId, "Missing", new[] { place.Name, place.ParentId },
                        $"The place \"{Make.Quote(place.Name)}\" is inside a place ({place.ParentId}) that no longer exists.");
                }
            }

            // Loops: each place has one parent, so the loops are separate; walk from every place and report a loop at its lowest id.
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in region.Value.Values.Where(p => tidy.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var path = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var at = start;
                while (at != null && tidy.Contains(at.Id) && seen.Add(at.Id))
                {
                    path.Add(at.Id);
                    at = region.Value.GetValueOrDefault(at.ParentId!);
                }

                if (at == null || !seen.Contains(at.Id) || !tidy.Contains(at.Id)) continue;
                var loop = path.Skip(path.IndexOf(at.Id)).ToList();
                var lowest = loop.Min(StringComparer.Ordinal)!;
                if (!reported.Add(lowest)) continue;
                var names = loop.OrderBy(id => id, StringComparer.Ordinal).Select(id => Make.Quote(region.Value[id].Name)).ToList();
                yield return Issue(region.Value[lowest], "cycle", "Cycle", new[] { string.Join(", ", names.Select(n => "\"" + n + "\"")) },
                    $"These places are inside one another in a loop, with no top: {string.Join(", ", names.Select(n => "\"" + n + "\""))}.");
            }
        }
    }

    private Issue Issue(PlaceView place, string detail, string code, IReadOnlyList<string> args, string message) =>
        Make.Issue(Name, IssueSeverity.Error, "place", place.Id, "parentId", detail, "Consistency.PlaceTree" + code, args, message,
            "Move the place to where it belongs with an edit of the place and its \"parent\" (a place of the same region, or \"none\" for the top of the region). Ask the DM where it goes; do not guess.",
            FixVia.Place);
}

/// <summary>
/// Two pages that answer to the same name. Pages with the same slug: a link by slug or title reaches the first and never the other, so one of them
/// is unreachable by link (warning). Pages whose titles differ only by an article or a doubled word (<see cref="TextNorm.Canonical"/>): they may be the
/// same page twice, which the DM decides (info). Neither can be fixed by a proposal: renaming or merging pages is the DM's call.
/// </summary>
public sealed class DuplicateTitleCheck : IConsistencyCheck
{
    public string Name => CheckNames.DuplicateSlug;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var sameSlug = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in s.Pages.Where(p => p.Slug.Length > 0).GroupBy(p => p.Slug, StringComparer.Ordinal).Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var page in group)
            {
                sameSlug.Add(page.Id);
                yield return Make.Issue(CheckNames.DuplicateSlug, IssueSeverity.Warning, "page", page.Id, "slug", group.Key, "Consistency.DuplicateSlug",
                    new[] { page.Title, group.Key, (group.Count() - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The page \"{Make.Quote(page.Title)}\" shares the address \"{group.Key}\" with {group.Count() - 1} other page(s); a link reaches only one of them.",
                    "Ask the DM which page is meant, and whether they should be merged or one renamed. Do not merge or delete pages on your own.",
                    FixVia.ReportOnly);
            }
        }

        foreach (var group in s.Pages.Select(p => (Page: p, Canonical: TextNorm.Canonical(p.Title))).Where(x => x.Canonical.Length > 0)
                     .GroupBy(x => x.Canonical, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var pages = group.Select(x => x.Page).ToList();
            if (pages.Count < 2 || pages.Select(p => p.Slug).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            var key = WikiText.Slugify(group.Key);
            foreach (var page in pages.Where(p => !sameSlug.Contains(p.Id)))
            {
                var others = pages.Where(p => p.Id != page.Id).Select(p => "\"" + Make.Quote(p.Title) + "\"").ToList();
                yield return Make.Issue(CheckNames.NearDuplicateTitle, IssueSeverity.Info, "page", page.Id, "title", key, "Consistency.NearDuplicateTitle",
                    new[] { page.Title, string.Join(", ", others) },
                    $"The page \"{Make.Quote(page.Title)}\" has nearly the same title as {string.Join(", ", others)} (they differ only by an article or a repeated word).",
                    "Ask the DM whether these are the same page twice. Do not merge them on your own.",
                    FixVia.ReportOnly);
            }
        }
    }
}

/// <summary>
/// What a secret in Lore says it is about or who knows it, where the thing is gone: a stored related id (<c>actor:</c>, <c>place:</c>, <c>item:</c>) the
/// text no longer mentions, and a knower that is not on the cast. A related id the text still mentions is the dangling-mention check's, not reported twice.
/// The lists are rebuilt when the secret is saved, but a proposal cannot touch them: the DM opens and saves the secret.
/// </summary>
public sealed class FactReferenceCheck : IConsistencyCheck
{
    public string Name => CheckNames.FactReference;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var places = s.Places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in s.Facts)
        {
            var mentioned = TextFields.Mentions(fact.Summary + "\n" + fact.Details).Select(m => $"{m.Kind}:{m.Id}").ToHashSet(StringComparer.Ordinal);
            foreach (var key in (fact.RelatedEntityIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                var colon = key.IndexOf(':');
                if (colon <= 0 || mentioned.Contains(key)) continue;
                var (kind, id) = (key[..colon], key[(colon + 1)..]);
                var gone = kind switch
                {
                    "actor" => !actors.Contains(id),
                    "place" => !places.Contains(id),
                    "item" => id.IndexOf('/') is var slash && slash > 0 && s.System != null && s.Library.Resolve(id[..slash], id[(slash + 1)..]) == null,
                    _ => false
                };
                if (!gone) continue;
                yield return Make.Issue(Name, IssueSeverity.Warning, "fact", fact.Id, "relatedEntityIds", key, "Consistency.FactRelatedGone",
                    new[] { fact.Summary, kind, id },
                    $"The secret \"{Make.Quote(fact.Summary)}\" is filed under a {kind} ({id}) that no longer exists, though its text does not mention it.",
                    "The list is rebuilt from the text when the secret is saved. Tell the DM to open and save the secret, or to check that nothing was meant to stay.",
                    FixVia.ReportOnly);
            }

            foreach (var id in (fact.KnownBy ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Where(id => !actors.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, "fact", fact.Id, "knownBy", id, "Consistency.FactKnowerGone",
                    new[] { fact.Summary, id },
                    $"The secret \"{Make.Quote(fact.Summary)}\" is known by a character ({id}) who is not on the cast.",
                    "Tell the DM; who knows a secret is theirs to change. Do not decide who should know it.",
                    FixVia.ReportOnly);
            }
        }
    }
}

/// <summary>
/// Things placed on the map or on the table whose other end is gone: a feature's creature that is not on the cast, a hazard's actor standing on a place
/// that no longer exists, a Library hazard placed on a place that the Library no longer has.
/// </summary>
public sealed class PlacementCheck : IConsistencyCheck
{
    public string Name => CheckNames.DanglingPlacement;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var places = s.Places.Select(p => (p.RegionId, p.Id)).ToHashSet();
        var homebrew = s.Homebrew.Select(h => h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var place in s.Places)
        {
            if (!string.IsNullOrEmpty(place.CreatureActorId) && !actors.Contains(place.CreatureActorId))
            {
                yield return Make.Issue(Name, IssueSeverity.Error, "place", place.Id, "creatureActorId", place.CreatureActorId, "Consistency.PlacedCreatureGone",
                    new[] { place.Name, place.CreatureActorId },
                    $"The place \"{Make.Quote(place.Name)}\" says its creature is a cast member ({place.CreatureActorId}) who is not on the cast.",
                    "Tell the DM; the link to the cast member has to be cleared or put back by hand.", FixVia.ReportOnly);
            }

            if (string.IsNullOrEmpty(place.HazardEntry) || s.System == null) continue;
            var slash = place.HazardEntry.IndexOf('/');
            if (slash <= 0) continue;
            var (type, id) = (place.HazardEntry[..slash], place.HazardEntry[(slash + 1)..]);
            // A homebrew hazard can only be judged against the homebrew list; with none read, nothing is said about it.
            if (place.HazardIsHomebrew && !s.HomebrewLoaded) continue;
            var known = place.HazardIsHomebrew ? homebrew.Contains(place.HazardEntry) : s.Library.Resolve(type, id) != null;
            if (known) continue;
            yield return Make.Issue(Name, IssueSeverity.Warning, "place", place.Id, "hazard", place.HazardEntry, "Consistency.PlacedHazardGone",
                new[] { place.Name, place.HazardEntry },
                $"The hazard {place.HazardEntry} placed on \"{Make.Quote(place.Name)}\" is no longer in the Library.",
                "Tell the DM; the placement has to be cleared or the entry put back. Do not invent a replacement hazard.", FixVia.ReportOnly);
        }

        foreach (var actor in s.Actors.Where(a => a.Hazard is { } link && link.RegionId.Length > 0 && link.LocationId.Length > 0 && !places.Contains((link.RegionId, link.LocationId))))
        {
            yield return Make.Issue(Name, IssueSeverity.Error, "actor", actor.Id, "hazard", actor.Hazard!.LocationId, "Consistency.HazardPlaceGone",
                new[] { actor.Name, actor.Hazard.LocationId },
                $"The hazard \"{Make.Quote(actor.Name)}\" on the table stands on a place ({actor.Hazard.LocationId}) that no longer exists.",
                "Tell the DM; the hazard has to be taken off the table or its place put back.", FixVia.ReportOnly);
        }
    }
}

/// <summary>A character whose player is not a member of the campaign (they left, or were removed). The DM decides what happens to the character.</summary>
public sealed class OwnerMembershipCheck : IConsistencyCheck
{
    public string Name => CheckNames.OwnerNotMember;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (s.Campaign.Id.Length == 0) yield break;
        var members = s.Campaign.MemberIds.Append(s.Campaign.OwnerId).ToHashSet(StringComparer.Ordinal);
        foreach (var a in s.Actors.Where(a => !string.IsNullOrEmpty(a.OwnerUserId) && !a.Retired && !members.Contains(a.OwnerUserId!)))
        {
            yield return Make.Issue(Name, IssueSeverity.Warning, "actor", a.Id, "ownerUserId", a.OwnerUserId, "Consistency.OwnerNotMember",
                new[] { a.Name, a.OwnerUserId! },
                $"The character \"{Make.Quote(a.Name)}\" belongs to a player ({a.OwnerUserId}) who is not a member of the campaign.",
                "Tell the DM; whether the character is retired or given to someone else is theirs to decide.", FixVia.ReportOnly);
        }
    }
}

/// <summary>
/// Homebrew entries that point at Library entries that are not there (a field that names a class, a spell or an item), and entries that share one id
/// (the second hides behind the first). The first is fixable with an edit of the entry's properties; the second is the DM's.
/// </summary>
public sealed class HomebrewChecks : IConsistencyCheck
{
    public string Name => CheckNames.HomebrewReference;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (!s.HomebrewLoaded) yield break;

        foreach (var group in s.Homebrew.GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in group.OrderBy(h => h.Id, StringComparer.Ordinal))
            {
                yield return Make.Issue(CheckNames.HomebrewDuplicate, IssueSeverity.Error, "homebrew", entry.Key, "entryId", WikiText.Slugify(entry.Id), "Consistency.HomebrewDuplicate",
                    new[] { entry.Name, entry.Key, (group.Count() - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The homebrew entry \"{Make.Quote(entry.Name)}\" shares its id {entry.Key} with {group.Count() - 1} other {(group.Count() == 2 ? "entry" : "entries")}; only one can be reached.",
                    "Tell the DM; which entry stays, and whether one is renamed, is theirs to decide.", FixVia.ReportOnly);
            }
        }

        if (s.System == null) yield break;
        foreach (var entry in s.Homebrew)
        {
            if (s.System.FindType(entry.Type) is not { } type) continue;
            foreach (var field in type.Fields.Where(f => f.Type == FieldType.EntityReference && !string.IsNullOrEmpty(f.TargetEntityType)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                if (!entry.Properties.TryGetValue(field.Key, out var raw) || Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)?.Trim() is not { Length: > 0 } id) continue;
                if (s.Library.Resolve(field.TargetEntityType!, id) != null) continue;
                yield return Make.Issue(CheckNames.HomebrewReference, IssueSeverity.Warning, "homebrew", entry.Key, field.Key, id, "Consistency.HomebrewReference",
                    new[] { entry.Name, field.Label, id, field.TargetEntityType! },
                    $"The homebrew entry \"{Make.Quote(entry.Name)}\" names \"{Make.Quote(id)}\" as its {field.Label}, which is not a {field.TargetEntityType} in the Library.",
                    "Ask the DM which entry is meant, or clear the field with an edit of the entry's properties. Do not invent an id.", FixVia.Homebrew);
            }
        }
    }
}

/// <summary>A homebrew hazard whose numbers do not hold together: a scaled row naming a field the type does not have, a route with a DC and no check,
/// damage that is not dice, and the like (<see cref="HazardNumbers.Problems"/>, the same messages the editor shows). Fixed by an edit of the entry's fields.</summary>
public sealed class HazardShapeCheck : IConsistencyCheck
{
    public string Name => CheckNames.HazardShape;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (s.System == null) yield break;
        foreach (var h in s.Homebrew)
        {
            if (s.System.FindType(h.Type) is not { Hazard: not null } type) continue;
            var entry = new ContentEntry { Id = h.EntryId, Type = h.Type, SystemId = s.System.Id, Name = h.Name, Properties = new Dictionary<string, object>(h.Properties) };
            foreach (var problem in HazardNumbers.Problems(entry, type, s.System).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            {
                var slug = WikiText.Slugify(problem);
                yield return Make.Issue(Name, IssueSeverity.Warning, "homebrew", h.Key, "properties", slug.Length > 60 ? slug[..60].TrimEnd('-') : slug, "Consistency.HazardShape",
                    new[] { h.Name, problem },
                    $"The hazard \"{Make.Quote(h.Name)}\": {problem}",
                    "Fix the numbers with an edit of the entry's properties, only as the game master asks; do not invent values.", FixVia.Homebrew);
            }
        }
    }
}

/// <summary>
/// The same tag written in more than one case ("Harbor", "harbor") across pages and homebrew entries: tags are found by exact spelling, so the two do not
/// find each other. The spelling most used is the one the rest should take (ties go to the first in ordinal order). A homebrew entry's tags can be set
/// by an edit of the entry; a page's cannot be claimed by a proposal, so those are the DM's.
/// </summary>
public sealed class TagCaseCheck : IConsistencyCheck
{
    public string Name => CheckNames.TagCase;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var holders = new List<(string Kind, string Id, string Key, string Tag)>();
        foreach (var p in s.Pages) holders.AddRange((p.Tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => ("page", p.Id, p.Id, t.Trim())));
        foreach (var h in s.Homebrew) holders.AddRange(h.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => ("homebrew", h.Id, h.Key, t.Trim())));

        foreach (var group in holders.GroupBy(h => h.Tag.ToLowerInvariant(), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var spellings = group.GroupBy(h => h.Tag, StringComparer.Ordinal).Select(g => (Spelling: g.Key, Count: g.Count())).ToList();
            if (spellings.Count < 2) continue;
            var canonical = spellings.OrderByDescending(x => x.Count).ThenBy(x => x.Spelling, StringComparer.Ordinal).First().Spelling;
            foreach (var holder in group.Where(h => h.Tag != canonical).DistinctBy(h => (h.Kind, h.Id, h.Tag)).OrderBy(h => h.Kind, StringComparer.Ordinal).ThenBy(h => h.Id, StringComparer.Ordinal))
            {
                var entityId = holder.Kind == "page" ? holder.Id : holder.Key;
                yield return Make.Issue(Name, IssueSeverity.Info, holder.Kind, entityId, "tags", WikiText.Slugify(group.Key) is { Length: > 0 } slug ? slug : "tag", "Consistency.TagCase",
                    new[] { holder.Tag, canonical },
                    $"The tag \"{Make.Quote(holder.Tag)}\" is also written \"{Make.Quote(canonical)}\" elsewhere, so the two are not found together.",
                    $"Offer the DM to write it \"{Make.Quote(canonical)}\" everywhere; do not rename tags without their say.",
                    holder.Kind == "homebrew" ? FixVia.Homebrew : FixVia.ReportOnly);
            }
        }
    }
}

/// <summary>
/// The campaign itself: a game system that is not installed (error: nothing about the sheets can be read), a system that changed since the campaign
/// last played under it (the DM accepts the changes in Settings), packs the campaign names that the system does not have, and what loading the system
/// and its packs reported. All the DM's: no proposal can change them.
/// </summary>
public sealed class CampaignHealthCheck : IConsistencyCheck
{
    public string Name => CheckNames.CampaignSystem;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var c = s.Campaign;
        if (c.Id.Length == 0) yield break;
        if (!c.SystemInstalled)
        {
            yield return Make.Issue(CheckNames.CampaignSystem, IssueSeverity.Error, "campaign", c.Id, "systemId", WikiText.Slugify(c.SystemId), "Consistency.SystemUnknown",
                new[] { c.SystemId },
                $"The campaign's game system \"{Make.Quote(c.SystemId)}\" is not installed, so no character sheet or Library entry can be read.",
                "Tell the DM to install the system or pick another in Settings. Nothing can be proposed about it.", FixVia.ReportOnly);
            yield break;
        }

        if (c.Changes.Count > 0)
        {
            yield return Make.Issue(CheckNames.SystemDrift, IssueSeverity.Warning, "campaign", c.Id, "systemStamp", null, "Consistency.SystemDrift",
                new[] { c.SystemId, c.Changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Join("; ", c.Changes.Take(5)) },
                $"The system \"{Make.Quote(c.SystemId)}\" changed since this campaign last played under it ({c.Changes.Count} change(s): {string.Join("; ", c.Changes.Take(5))}).",
                "Tell the DM; they review the changes in Settings and accept them. Sheets may read differently until then.", FixVia.ReportOnly);
        }

        var known = c.KnownPacks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (known.Count > 0)
        {
            foreach (var pack in c.NamedPacks.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !known.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
            {
                yield return Make.Issue(CheckNames.PackDrift, IssueSeverity.Info, "campaign", c.Id, "packs", WikiText.Slugify(pack), "Consistency.PackDrift",
                    new[] { pack },
                    $"The campaign's pack choice names a pack \"{Make.Quote(pack)}\" that the system no longer has.",
                    "Tell the DM; the choice is cleaned up by saving the pack settings.", FixVia.ReportOnly);
            }
        }

        foreach (var problem in c.LoadProblems.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var slug = WikiText.Slugify(problem);
            yield return Make.Issue(CheckNames.PackProblem, IssueSeverity.Warning, "campaign", c.Id, "load", slug.Length > 60 ? slug[..60].TrimEnd('-') : slug, "Consistency.PackProblem",
                new[] { problem },
                $"Loading the system or its packs reported: {Make.Quote(problem)}",
                "Tell the DM; this is about the installed files, not the campaign's text. Nothing can be proposed about it.", FixVia.ReportOnly);
        }
    }
}

/// <summary>
/// Library entries the campaign names that are not there: a hazard's or creature's pack entry that is gone (<c>blueprintId</c>), gear on a sheet that the
/// Library does not have (only where the sheet rules do not already say so), and Bestiary entries. Info or warning: the app shows "Missing content" for
/// these and carries on, but the table cannot use them. The DM's to fix; a model never invents a replacement.
/// </summary>
public sealed class LibraryReferenceCheck : IConsistencyCheck
{
    public string Name => CheckNames.LibraryReference;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        if (s.System == null) yield break;
        var blueprintTypes = s.System.EntityTypes.Where(t => t.ActorBlueprint).Select(t => t.Type).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var inventory = s.System.SheetRules?.Inventory;
        // The sheet rules report a missing inventory item as an error where they run; this check covers the places they do not.
        var rulesRun = s.System.SheetRules != null && s.Enforcement != "off";

        foreach (var a in s.Actors.Where(a => !a.Retired))
        {
            if (!string.IsNullOrEmpty(a.BlueprintId) && blueprintTypes.Count > 0)
            {
                var colon = a.BlueprintId.LastIndexOf(':');
                var id = colon >= 0 ? a.BlueprintId[(colon + 1)..] : a.BlueprintId;
                if (id.Length > 0 && !blueprintTypes.Any(t => s.Library.Resolve(t, id) != null))
                {
                    yield return Make.Issue(Name, IssueSeverity.Info, "actor", a.Id, "blueprintId", WikiText.Slugify(a.BlueprintId), "Consistency.BlueprintGone",
                        new[] { a.Name, a.BlueprintId },
                        $"\"{Make.Quote(a.Name)}\" was made from the creature {a.BlueprintId}, which the Library no longer has (its pack is off or gone).",
                        "Tell the DM; the sheet still works, but its source is missing. Do not pick another creature for them.", FixVia.ReportOnly);
                }
            }

            if (inventory == null || rulesRun && a.Role != ActorRole.Monster) continue;
            foreach (var id in SheetRuleEngine.RawList(a.Sheet, inventory.Key).Select(raw => SheetRuleEngine.ParsePick(raw).Id).Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal))
            {
                if (inventory.Types.Any(t => s.Library.Resolve(t, id) != null)) continue;
                yield return Make.Issue(Name, IssueSeverity.Warning, "actor", a.Id, inventory.Key, id, "Consistency.InventoryItemGone",
                    new[] { a.Name, id },
                    $"\"{Make.Quote(a.Name)}\" carries \"{Make.Quote(id)}\", which is not in the Library.",
                    "Tell the DM; do not replace the item with another.", FixVia.ReportOnly);
            }
        }

        if (s.Campaign.Id.Length == 0) yield break;
        var homebrew = s.Homebrew.Select(h => h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in s.Campaign.Bestiary.OrderBy(b => b.EntryType, StringComparer.Ordinal).ThenBy(b => b.EntryId, StringComparer.Ordinal))
        {
            var key = $"{b.EntryType}/{b.EntryId}";
            if (b.IsHomebrew ? !s.HomebrewLoaded || homebrew.Contains(key) : s.Library.Resolve(b.EntryType, b.EntryId) != null) continue;
            yield return Make.Issue(Name, IssueSeverity.Warning, "campaign", s.Campaign.Id, "bestiary", WikiText.Slugify(key), "Consistency.BestiaryGone",
                new[] { b.Name, key },
                $"The Bestiary lists \"{Make.Quote(b.Name)}\" ({key}), which is not in the Library.",
                "Tell the DM; taking it off the shortlist is theirs to do.", FixVia.ReportOnly);
        }
    }
}
