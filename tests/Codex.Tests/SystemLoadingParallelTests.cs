using System.Collections.Concurrent;
using Codex.Core.Systems;

namespace Codex.Tests;

/// <summary>Systems load on many threads at once (every test class, and an import beside a reload): the empty-section filler's nullability
/// reader keeps plain dictionaries, so it must never be shared between loads.</summary>
public class SystemLoadingParallelTests
{
    [Fact]
    public void LoadingASystemFromManyThreadsAtOnce_NeverFails()
    {
        var path = Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "system.yaml");
        var errors = new ConcurrentBag<string>();

        for (var round = 0; round < 10; round++)
        {
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
            {
                try
                {
                    Assert.Equal("four-degrees", SystemDefinitionLoader.Load(path).Id);
                }
                catch (Exception ex)
                {
                    errors.Add(ex.GetType().Name + ": " + ex.Message);
                }
            });
        }

        Assert.Empty(errors);
    }
}
