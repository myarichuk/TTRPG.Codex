using Codex.Core.WorldBuilder;
using Codex.Persistence;

namespace Codex.Tests;

public class PlaceFeatureTests
{
    [Theory]
    [InlineData(RegionKind.General, PlaceKind.Area)]
    [InlineData(RegionKind.City, PlaceKind.District)]
    [InlineData(RegionKind.Wilderness, PlaceKind.Site)]
    [InlineData(RegionKind.Dungeon, PlaceKind.Section)]
    [InlineData(RegionKind.Vessel, PlaceKind.Section)]
    public void ChildKind_AtTheTop_FollowsWhatTheRegionIs(RegionKind region, PlaceKind expected) =>
        Assert.Equal(expected, PlaceTree.ChildKind(null, region));

    [Theory]
    [InlineData(PlaceKind.District, PlaceKind.Site)]
    [InlineData(PlaceKind.Site, PlaceKind.Room)]
    [InlineData(PlaceKind.Section, PlaceKind.Room)]
    [InlineData(PlaceKind.Room, PlaceKind.Room)]
    [InlineData(PlaceKind.Feature, PlaceKind.Feature)]
    public void ChildKind_InsideAPlace_IsOneStepSmaller(PlaceKind parent, PlaceKind expected) =>
        Assert.Equal(expected, PlaceTree.ChildKind(new LocationDocument { Kind = parent }));

    [Fact]
    public void Tidy_DropsAFeatureWithNothingInIt_AndTrimsTheRest()
    {
        Assert.Null(PlaceFeature.Tidy(new PlaceFeature { Lock = "  ", Contents = "" }));

        var tidy = PlaceFeature.Tidy(new PlaceFeature { Lock = " DC 15 ", Contents = new string('x', PlaceFeature.MaxContents + 10) })!;
        Assert.Equal("DC 15", tidy.Lock);
        Assert.Equal(PlaceFeature.MaxContents, tidy.Contents!.Length);
        Assert.Null(tidy.Trap);
    }

    [Fact]
    public void ParseProposal_ReadsRoomsFeaturesAndTheRegionKind()
    {
        const string json = """
            {"region": "The Hollow Wreck", "regionKind": "vessel", "entries": [
              {"ref": "d", "kind": "place", "name": "Lower Deck", "placeKind": "section", "text": "Dark."},
              {"ref": "g", "kind": "place", "name": "Galley", "placeKind": "room", "parent": "d", "text": "Pots."},
              {"ref": "c", "kind": "place", "name": "Iron-bound Chest", "placeKind": "feature", "parent": "g", "text": "A chest.",
               "feature": {"container": true, "lock": "DC 15", "creature": "a mimic", "contents": "40 gp"}},
              {"ref": "s", "kind": "place", "name": "Stove", "placeKind": "feature", "parent": "g", "text": "Cold.", "feature": {"lock": "  "}}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, WorldBuilderTests.Rules());

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal("vessel", check.Bundle.RegionKind);
        Assert.Equal("section", check.Bundle.Find("d")!.PlaceKind);
        Assert.Equal("room", check.Bundle.Find("g")!.PlaceKind);
        var chest = check.Bundle.Find("c")!;
        Assert.Equal("feature", chest.PlaceKind);
        Assert.True(chest.Feature!.Container);
        Assert.Equal("DC 15", chest.Feature.Lock);
        Assert.Equal("a mimic", chest.Feature.Creature);
        Assert.Equal("40 gp", chest.Feature.Contents);
        Assert.Null(check.Bundle.Find("s")!.Feature);
    }

    [Fact]
    public void ParseProposal_WarnsAboutAnUnknownRegionKind()
    {
        var check = WorldBundleParser.ParseProposal("""{"regionKind": "spaceship", "entries": [{"kind": "place", "name": "Bridge", "text": "x"}]}""", WorldBuilderTests.Rules());

        Assert.Null(check.Bundle.RegionKind);
        Assert.Contains(check.Warnings, w => w.Contains("spaceship"));
    }
}
