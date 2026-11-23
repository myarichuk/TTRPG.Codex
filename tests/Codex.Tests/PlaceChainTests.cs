using Codex.Persistence;

namespace Codex.Tests;

/// <summary>The one walk from a place up through the places it is inside, shared by the Hazards panel, the hazard offers and the encounter builder.</summary>
public class PlaceChainTests
{
    private static RegionDocument Region() => new()
    {
        Id = "r",
        Locations =
        {
            new LocationDocument { Id = "keep", Name = "Keep" },
            new LocationDocument { Id = "cellar", Name = "Cellar", ParentId = "keep" },
            new LocationDocument { Id = "vault", Name = "Vault", ParentId = "cellar" },
            new LocationDocument { Id = "lost", Name = "Lost", ParentId = "gone" },
            new LocationDocument { Id = "a", Name = "A", ParentId = "b" },
            new LocationDocument { Id = "b", Name = "B", ParentId = "a" }
        }
    };

    private static string[] Ids(string place) => PlaceTree.Chain(Region(), Region().Locations.First(l => l.Id == place)).Select(l => l.Id).ToArray();

    [Fact]
    public void TheChain_IsThePlaceThenEachPlaceAroundItUpToTheTop()
    {
        Assert.Equal(new[] { "vault", "cellar", "keep" }, Ids("vault"));
        Assert.Equal(new[] { "keep" }, Ids("keep"));
    }

    [Fact]
    public void AParentThatIsGoneEndsTheWalk_AndALoopOfParentsIsWalkedOnce()
    {
        Assert.Equal(new[] { "lost" }, Ids("lost"));
        Assert.Equal(new[] { "a", "b" }, Ids("a"));
    }
}
