namespace Codex.Core.Consistency;

/// <summary>
/// Every <see cref="Issue.Code"/> the app's own checks make, so the app can show each issue's sentence in the reader's language
/// (<c>Consistency.*</c> keys in the localisation files) and a test can see that none is missing. Not here: the keys of the sheet rules
/// (<c>Actor.Rule.*</c>, already translated with the sheet), and the codes a game system names for its own checks (<c>issueChecks</c>), which fall
/// back to the English message where the app has no text for them.
/// </summary>
public static class ConsistencyCodes
{
    public static readonly IReadOnlyList<string> Known = new[]
    {
        "Consistency.BrokenLink", "Consistency.UnlinkedMention", "Consistency.VisibilityLeak", "Consistency.DanglingMention", "Consistency.DanglingReference",
        "Consistency.EmptyBio", "Consistency.EmptyBody", "Consistency.TodoMarker", "Consistency.EmptyInventory",
        "Consistency.SecretUnclosed", "Consistency.SecretUnknownAudience", "Consistency.SecretForNobody", "Consistency.SecretForMissing",
        "Consistency.SheetRule", "Consistency.SheetEvaluationFailed",
        "Consistency.PlaceTreeSelf", "Consistency.PlaceTreeCrossRegion", "Consistency.PlaceTreeMissing", "Consistency.PlaceTreeCycle",
        "Consistency.DuplicateSlug", "Consistency.NearDuplicateTitle", "Consistency.FactRelatedGone", "Consistency.FactKnowerGone",
        "Consistency.PlacedCreatureGone", "Consistency.PlacedHazardGone", "Consistency.HazardPlaceGone", "Consistency.OwnerNotMember",
        "Consistency.HomebrewDuplicate", "Consistency.HomebrewReference", "Consistency.HazardShape", "Consistency.TagCase",
        "Consistency.SystemUnknown", "Consistency.SystemDrift", "Consistency.PackDrift", "Consistency.PackProblem",
        "Consistency.BlueprintGone", "Consistency.InventoryItemGone", "Consistency.BestiaryGone", "Consistency.CheckFailed",
        "Consistency.OrphanPage", "Consistency.BacklinkMismatch", "Consistency.PersonPageNoActor", "Consistency.RetiredMention"
    };
}
