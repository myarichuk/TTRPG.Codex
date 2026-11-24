using Codex.Core.AI;
using Codex.Core.WorldBuilder;

namespace Codex.Tests;

/// <summary>The world builder's <c>encounter</c> entry and the edit and delete of a prepared encounter (ENCOUNTER_BUILDER_DESIGN.md 6 and 17), read by the pure parser.</summary>
public class EncounterProposalParserTests
{
    private const string PlanId = "6f1c7a5e-2b0d-4f43-9d0e-5b1f4a7c9d10";
    private const string OtherId = "0b6f5d3c-8a41-4c2e-b7a9-3e2d1c0f9a88";

    private static ProposalRules Rules() => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["monster"] = "monster", ["hazard"] = "hazard" },
        new Dictionary<string, string>(),
        new[] { "cellar", "hall" },
        _ => Array.Empty<FieldSpec>(),
        ExistingActors: new[] { "npc-1", "rat-1" },
        ExistingHomebrew: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["monster/vault-rat"] = "Vault Rat" },
        HazardType: "hazard",
        ExistingEncounters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PlanId] = "Ambush", [OtherId] = "Wolves" },
        CreatureTypes: new[] { "monster" });

    private static ProposalCheck Parse(string entries) => WorldBundleParser.ParseProposal($$"""{"entries": [{{entries}}]}""", Rules());

    private const string NewThings = """
        {"ref": "c", "kind": "place", "name": "Wine cellar", "text": "Casks."},
        {"ref": "h", "kind": "homebrew", "type": "monster", "name": "Cask mimic", "text": "It waits."},
        {"ref": "f", "kind": "encounter", "name": "Ambush in the cellar", "place": "c", "text": "They want the key.", "tags": ["grey-coast"], "status": "ready",
         "creatures": [{"entry": "monster/bandit", "count": 3, "note": "behind the casks"}, {"entry": "h"}, {"entry": "existing:homebrew:monster/vault-rat", "count": 2},
                       {"entry": "existing:actor:npc-1", "hidden": true}]}
        """;

    [Fact]
    public void AnEncounter_WithANewPlace_ANewHomebrewCreature_AndLibraryCreatures_Parses()
    {
        var check = Parse(NewThings);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var plan = check.Bundle.Find("f")!;
        Assert.Equal((DraftKinds.Encounter, "Ambush in the cellar", "c", "ready"), (plan.Kind, plan.Name, plan.Place, plan.Status));
        Assert.Equal("They want the key.", plan.Body);
        Assert.Equal(new[] { "grey-coast" }, plan.Tags);
        Assert.Equal(4, plan.Lineup.Count);
        Assert.Equal(("monster", "bandit", false, 3, "behind the casks"), (plan.Lineup[0].Type, plan.Lineup[0].Id, plan.Lineup[0].Homebrew, plan.Lineup[0].Count, plan.Lineup[0].Note));
        // A creature this proposal makes is named by its ref and typed from its own entry.
        Assert.Equal(("h", "monster", true, "Cask mimic"), (plan.Lineup[1].Ref, plan.Lineup[1].Type, plan.Lineup[1].Homebrew, plan.Lineup[1].Name));
        Assert.Equal(("monster", "vault-rat", true, 2), (plan.Lineup[2].Type, plan.Lineup[2].Id, plan.Lineup[2].Homebrew, plan.Lineup[2].Count));
        Assert.Equal(("npc-1", 1, true), (plan.Lineup[3].ActorId, plan.Lineup[3].Count, plan.Lineup[3].Hidden));
    }

    [Fact]
    public void ANewEncounter_IsADraft_UnlessItSaysReady_AndNeverRan()
    {
        var plain = Parse("""{"ref": "f", "kind": "encounter", "name": "Wolves", "creatures": [{"entry": "monster/wolf"}]}""");
        var ran = Parse("""{"ref": "f", "kind": "encounter", "name": "Wolves", "status": "ran", "creatures": [{"entry": "monster/wolf"}]}""");

        Assert.Equal("draft", plain.Bundle.Find("f")!.Status);
        Assert.False(ran.Ok);
        Assert.Contains("status 'ran' must be draft or ready", Assert.Single(ran.Errors));
    }

    [Fact]
    public void AHazardInTheCreatures_IsRefused_WithAPointerToThePlacesFeature()
    {
        var library = Parse("""{"ref": "f", "kind": "encounter", "name": "Pit", "creatures": [{"entry": "hazard/spiked-pit"}]}""");
        var made = Parse("""{"ref": "t", "kind": "homebrew", "type": "hazard", "name": "Rolling stone", "text": "Rolls."}, {"ref": "f", "kind": "encounter", "name": "Pit", "creatures": [{"entry": "t"}]}""");

        Assert.Contains("place it on the place's feature (feature.hazard)", Assert.Single(library.Errors));
        Assert.Contains("feature.hazard", Assert.Single(made.Errors));
    }

    [Fact]
    public void ACreatureEntryTaggedAsAHazard_IsRefusedByRef_WhateverItsTypeIsCalled()
    {
        var check = Parse("""{"ref": "t", "kind": "homebrew", "type": "monster", "name": "Rolling stone", "text": "Rolls.", "tags": ["Hazard"]}, {"ref": "f", "kind": "encounter", "name": "Pit", "creatures": [{"entry": "t"}]}""");

        Assert.Contains("is tagged as a hazard", Assert.Single(check.Errors));
        Assert.Contains("feature.hazard", check.Errors[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public void ACountOutsideOneToTwenty_IsRefused(int count)
    {
        var check = Parse("""{"ref": "f", "kind": "encounter", "name": "Wolves", "creatures": [{"entry": "monster/wolf", "count": """ + count + "}]}");

        Assert.Contains($"count {count} is outside 1 to 20", Assert.Single(check.Errors));
    }

    [Fact]
    public void APlayerCharacter_AsACastMember_IsRefused_AndACastMemberCountsOnce()
    {
        // pc-1 is not among the cast members a proposal may name (the player characters are left out of ExistingActors).
        var player = Parse("""{"ref": "f", "kind": "encounter", "name": "Duel", "creatures": [{"entry": "existing:actor:pc-1"}]}""");
        var many = Parse("""{"ref": "f", "kind": "encounter", "name": "Duel", "creatures": [{"entry": "existing:actor:npc-1", "count": 2}]}""");

        Assert.Contains("a player character cannot", Assert.Single(player.Errors));
        Assert.Contains("a cast member joins once", Assert.Single(many.Errors));
    }

    [Fact]
    public void ALineThatIsNotACreature_OrNotThere_IsRefusedByName()
    {
        var wrongType = Parse("""{"ref": "f", "kind": "encounter", "name": "Duel", "creatures": [{"entry": "spell/fireball"}]}""");
        var noHomebrew = Parse("""{"ref": "f", "kind": "encounter", "name": "Duel", "creatures": [{"entry": "existing:homebrew:monster/nothing"}]}""");
        var noRef = Parse("""{"ref": "f", "kind": "encounter", "name": "Duel", "creatures": [{"entry": "ghost"}]}""");
        var noPlace = Parse("""{"ref": "f", "kind": "encounter", "name": "Duel", "place": "existing:place:nowhere", "creatures": [{"entry": "monster/wolf"}]}""");

        Assert.Contains("'spell' is not a creature type", Assert.Single(wrongType.Errors));
        Assert.Contains("is not a homebrew entry the campaign has", Assert.Single(noHomebrew.Errors));
        Assert.Contains("'ghost' is not a homebrew creature entry of this proposal", Assert.Single(noRef.Errors));
        Assert.Contains("is not a place the campaign has", Assert.Single(noPlace.Errors));
    }

    [Fact]
    public void AnEdit_ReplacesWhatItNames_Field_ByField()
    {
        var check = Parse($$"""
            {"ref": "g", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "rename": "Ambush at dusk", "place": "existing:place:hall",
             "creatures": [{"entry": "monster/wolf", "count": 4}], "text": "New notes.", "tags": ["a", "b"], "status": "ready"}
            """);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var edit = check.Bundle.Find("g")!;
        Assert.Equal($"existing:encounter:{PlanId}", edit.Target);
        Assert.Equal(("Ambush at dusk", "existing:place:hall", true, true, "ready", true), (edit.Rename, edit.Place, edit.PlaceGiven, edit.LineupGiven, edit.Status, edit.Retag));
        Assert.Equal("New notes.", edit.Body);
        Assert.Equal(new[] { "a", "b" }, edit.Tags);
        Assert.Equal("Ambush", edit.Name); // named after the plan the campaign has
        Assert.True(edit.Selected);
    }

    [Fact]
    public void AnEdit_OfOneField_LeavesTheOthersAlone_AndAnEmptyPlaceTakesThePlaceAway()
    {
        var rename = Parse($$"""{"ref": "g", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "rename": "Dusk"}""").Bundle.Find("g")!;
        var clear = Parse($$"""{"ref": "g", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "place": "none"}""").Bundle.Find("g")!;

        Assert.False(rename.PlaceGiven);
        Assert.False(rename.LineupGiven);
        Assert.False(rename.Retag);
        Assert.Null(rename.Status);
        Assert.True(clear.PlaceGiven);
        Assert.Null(clear.Place);
    }

    [Fact]
    public void AnEdit_ThatChangesNothing_IsRefused()
    {
        // An empty list of creatures is no lineup at all, so a model that fills every optional field cannot empty a plan by it.
        var check = Parse($$"""{"ref": "g", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "creatures": [], "status": "", "text": "", "place": ""}""");

        Assert.Contains("the edit changes nothing", Assert.Single(check.Errors));
    }

    [Fact]
    public void ADelete_StandsAlone_AndArrivesUnticked()
    {
        var check = Parse($$"""{"ref": "d", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "delete": true}""");

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var delete = check.Bundle.Find("d")!;
        Assert.True(delete.Remove);
        Assert.False(delete.Selected);
        Assert.Equal("Ambush", delete.Name);
        Assert.Equal($"existing:encounter:{PlanId}", delete.Target);
    }

    [Fact]
    public void ADelete_ThatCarriesAnEmptyTagsListAndAnEmptyPlace_IsStillADelete()
    {
        var check = Parse($$"""{"ref": "d", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "delete": true, "tags": [], "place": "", "creatures": [], "status": ""}""");

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.True(check.Bundle.Find("d")!.Remove);
        Assert.False(check.Bundle.Find("d")!.Selected);
    }

    [Fact]
    public void ACreatureOrCastMemberTheSameProposalDeletes_IsNotLeftInAnEncounter()
    {
        var homebrew = Parse("""
            {"ref": "x", "kind": "edit", "target": "existing:homebrew:monster/vault-rat", "delete": true},
            {"ref": "f", "kind": "encounter", "name": "Rats", "creatures": [{"entry": "existing:homebrew:monster/vault-rat"}]}
            """);
        var actor = Parse($$"""
            {"ref": "x", "kind": "edit", "target": "existing:actor:npc-1", "delete": true},
            {"ref": "e", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "creatures": [{"entry": "existing:actor:npc-1"}]}
            """);

        Assert.Contains("its creature 'existing:homebrew:monster/vault-rat' is deleted by x", Assert.Single(homebrew.Errors));
        Assert.Contains("its creature 'existing:actor:npc-1' is deleted or retired by x", Assert.Single(actor.Errors));
    }

    [Fact]
    public void HiddenOnACastMember_IsAWarning_AndAFactDoesNotLinkToAnEncounter()
    {
        var check = Parse("""
            {"ref": "f", "kind": "encounter", "name": "Duel", "creatures": [{"entry": "existing:actor:npc-1", "hidden": true}]},
            {"ref": "s", "kind": "fact", "name": "A secret", "links": ["f"]}
            """);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains("\"hidden\" does nothing for a cast member", Assert.Single(check.Warnings, w => w.Contains("hidden")));
        Assert.Empty(check.Bundle.Find("s")!.Links);
    }

    [Theory]
    [InlineData("\"rename\": \"Other\"")]
    [InlineData("\"place\": \"none\"")]
    [InlineData("\"status\": \"ready\"")]
    [InlineData("\"tags\": [\"a\"]")]
    [InlineData("\"text\": \"Notes\"")]
    [InlineData("\"creatures\": [{\"entry\": \"monster/wolf\"}]")]
    public void ADelete_WithAnyOtherChange_IsRefused(string other)
    {
        var check = Parse($$"""{"ref": "d", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "delete": true, """ + other + "}");

        Assert.Contains("a \"delete\" stands alone", Assert.Single(check.Errors));
    }

    [Fact]
    public void DeletingAPlanAndEditingIt_OrDeletingItTwice_IsAnError()
    {
        var both = Parse($$"""{"ref": "d", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "delete": true}, {"ref": "g", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "rename": "x"}""");
        var twice = Parse($$"""{"ref": "d", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "delete": true}, {"ref": "e", "kind": "edit", "target": "existing:encounter:{{PlanId}}", "delete": true}""");

        Assert.Contains("is already changed by another entry", Assert.Single(both.Errors));
        Assert.Contains("is already changed by another entry", Assert.Single(twice.Errors));
    }

    [Theory]
    [InlineData("existing:encounter:not-a-guid")]
    [InlineData("existing:encounter:preparedencounters/6f1c7a5e-2b0d-4f43-9d0e-5b1f4a7c9d10")]
    [InlineData("existing:encounter:")]
    [InlineData("existing:encounter:6f1c7a5e-2b0d-4f43-9d0e-5b1f4a7c9d11")]
    public void ATargetThatIsNotAGuidTheCampaignHas_IsRefused(string target)
    {
        var check = Parse($$"""{"ref": "d", "kind": "edit", "target": "{{target}}", "delete": true}""");

        Assert.False(check.Ok);
        Assert.Single(check.Errors);
    }

    [Fact]
    public void LeavingOutAPlace_LeavesOutTheEncounterSetInIt_AndTheHomebrewCreatureItUses()
    {
        var bundle = Parse(NewThings).Bundle;
        var plan = bundle.Find("f")!;

        var withPlace = bundle.Skip(bundle.Find("c")!);

        Assert.False(plan.Selected);
        Assert.Contains(plan, withPlace);

        bundle.Select(plan);

        Assert.True(plan.Selected);
        Assert.True(bundle.Find("c")!.Selected);
        Assert.True(bundle.Find("h")!.Selected);

        var withCreature = bundle.Skip(bundle.Find("h")!);

        Assert.False(plan.Selected);
        Assert.Contains(plan, withCreature);
        // Both ways, what is left out is told before it is done.
        bundle.Select(plan);
        Assert.Contains(plan, bundle.WouldSkip(bundle.Find("c")!));
    }

    [Fact]
    public void AnEncounter_InAPlaceThatTheSameProposalDeletes_IsAnError()
    {
        var check = Parse("""
            {"ref": "x", "kind": "edit", "target": "existing:place:hall", "delete": true},
            {"ref": "f", "kind": "encounter", "name": "Duel", "place": "existing:place:hall", "creatures": [{"entry": "monster/wolf"}]}
            """);

        Assert.Contains("its place 'existing:place:hall' is deleted by x", Assert.Single(check.Errors));
    }

    [Fact]
    public void ReadLines_IsTheOneReaderOfALineup_ForTheRateTool()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""[{"entry": "monster/wolf", "count": 2}, "monster/bandit"]""");
        var errors = new List<string>();

        var lines = WorldBundleParser.ReadLines(json.RootElement, "creatures", Rules(), errors);

        Assert.Empty(errors);
        Assert.Equal(new[] { ("wolf", 2), ("bandit", 1) }, lines.Select(l => (l.Id!, l.Count)));
    }
}
