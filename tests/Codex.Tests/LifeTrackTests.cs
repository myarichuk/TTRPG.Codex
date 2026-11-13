using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The table shows hit points and a "Down" state only for a system that has a life track: Blades in the Dark has
/// none, so its cards never read "Down 0/0 HP". Decided from the system's data, never from its id.</summary>
public class LifeTrackTests
{
    private static SystemDefinition Load(string id) => SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", id, "system.yaml"));

    [Fact]
    public void Srd5e_HasAHitPointTrack() => Assert.True(SystemDefinition.HasLifeTrack(Load("srd5e")));

    [Fact]
    public void FitdSrd_HasNoHitPointTrack() => Assert.False(SystemDefinition.HasLifeTrack(Load("fitd-srd")));

    [Fact]
    public void NoSystem_OrASystemWithNothingDeclared_KeepsTheDefaultTrack()
    {
        Assert.True(SystemDefinition.HasLifeTrack(null));
        Assert.True(SystemDefinition.HasLifeTrack(new SystemDefinition()));
    }
}
