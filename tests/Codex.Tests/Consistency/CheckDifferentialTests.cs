using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>
/// The performance pass changed how some checks do their work, never what they find: each optimised check is run beside its version from before
/// (CheckOracles.cs) on seeded generated worlds and adversarial ones, and must yield the same issues, byte for byte, in the same order.
/// </summary>
public class CheckDifferentialTests
{
    private static void Same(IConsistencyCheck oracle, IConsistencyCheck current, WorldSnapshot snapshot, string label)
    {
        var expected = oracle.Run(snapshot).Select(Fx.Flat).ToList();
        var actual = current.Run(snapshot).Select(Fx.Flat).ToList();
        Assert.True(expected.SequenceEqual(actual), $"{label}: expected\n{string.Join("\n", expected)}\nactual\n{string.Join("\n", actual)}");
    }

    // ---- the place tree ----

    private static List<PlaceView> RandomPlaces(Random r, int count, int regions)
    {
        var places = new List<PlaceView>();
        for (var i = 0; i < count; i++)
        {
            var region = "regions/" + r.Next(regions);
            var parent = r.Next(10) switch
            {
                0 => null,
                1 => "gone" + r.Next(3),
                2 => "p" + i, // itself
                _ => "p" + r.Next(count)
            };
            places.Add(new PlaceView("p" + i, region, "Place " + r.Next(count), null, null, parent));
        }

        return places;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ThePlaceTree_FindsWhatItFoundBefore_OnGeneratedWorlds(int seed)
    {
        var r = new Random(seed);
        for (var world = 0; world < 200; world++)
        {
            var places = RandomPlaces(r, r.Next(1, 40), r.Next(1, 4));
            Same(new OraclePlaceTreeCheck(), new PlaceTreeCheck(), Fx.Snap(places: places), $"seed {seed} world {world}");
        }
    }

    [Fact]
    public void ThePlaceTree_FindsWhatItFoundBefore_OnDeepChainsAndManyLoops()
    {
        var places = new List<PlaceView>();
        // A chain of 600 ending in a loop of 50, in an order where ids do not follow the chain; chains that run into it; loops of one length each;
        // a chain into a missing parent; a chain into another region.
        for (var i = 0; i < 600; i++) places.Add(new PlaceView($"c{(i * 7919) % 600:D4}", "regions/a", "C" + i, null, null, i == 599 ? $"c{(550 * 7919) % 600:D4}" : $"c{((i + 1) * 7919) % 600:D4}"));
        for (var i = 0; i < 100; i++) places.Add(new PlaceView($"t{i:D3}", "regions/a", "T" + i, null, null, i % 10 == 0 ? $"c{(i * 3) % 600:D4}" : $"t{i - 1:D3}"));
        for (var n = 2; n < 30; n++)
            for (var k = 0; k < n; k++) places.Add(new PlaceView($"l{n:D2}-{k:D2}", "regions/b", "L" + n + "." + k, null, null, $"l{n:D2}-{(k + 1) % n:D2}"));
        for (var i = 0; i < 300; i++) places.Add(new PlaceView($"m{i:D3}", "regions/b", "M" + i, null, null, i == 0 ? "nowhere" : $"m{i - 1:D3}"));
        for (var i = 0; i < 50; i++) places.Add(new PlaceView($"x{i:D3}", "regions/c", "X" + i, null, null, i == 0 ? "c0000" : $"x{i - 1:D3}"));
        Same(new OraclePlaceTreeCheck(), new PlaceTreeCheck(), Fx.Snap(places: places), "adversarial");
    }
}
